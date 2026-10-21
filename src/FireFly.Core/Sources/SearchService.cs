using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

/// <summary>NeedsFallback：只做了优选阶段、还应再做降级搜索（前端据此异步追加）。</summary>
public sealed record SearchResult(MediaQuery Query, IReadOnlyList<Release> Releases, IReadOnlyList<string> Queries, IReadOnlyList<string> Errors, bool FromCache, bool NeedsFallback = false)
{
    /// <summary>这次按什么类型、用了几个站点和关键词搜的（获取资源面板顶部显示）。</summary>
    public SearchPlanSummary? Plan { get; init; }
}

/// <summary>NoSiteForCategory：没有站点负责这个类型，改用了全部站点。</summary>
public sealed record SearchPlanSummary(string Category, int Sources, int Keywords, bool NoSiteForCategory);

/// <summary>一个站点在这次搜索里用哪些关键词；Rank 是站点的优先级（0 最优先）。</summary>
public sealed record SourcePlan(IResourceSource Source, IReadOnlyList<string> Keywords, int Rank);

/// <summary>按类型选出的站点和各站点生效的关键词；KeywordOrder 是全部关键词的优先级顺序。</summary>
public sealed record SearchPlan(string Category, IReadOnlyList<SourcePlan> Sources, IReadOnlyList<string> KeywordOrder, bool NoSiteForCategory)
{
    public SearchPlanSummary Summary() =>
        new(Category, Sources.Count, Sources.SelectMany(s => s.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).Count(), NoSiteForCategory);
}

/// <summary>一次请求的结果，用来更新配置页里站点的状态灯。</summary>
public sealed record SourceReport(string Source, bool Ok, TimeSpan Elapsed, int Count, string? Error)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public static class SearchPlanner
{
    /// <summary>
    /// 只请求负责这个类型的站点（站点的 types 为空表示全部类型）；没有站点负责时用全部站点。
    /// 每个站点只用对它、对这个类型生效的关键词。站点和关键词都按配置里的顺序排优先级。
    /// </summary>
    public static SearchPlan Plan(RulesDocument rules, IReadOnlyList<IResourceSource> sources, string category)
    {
        var types = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules.Sources)
        {
            types.TryAdd(rule.Name, rule.Types);
        }

        bool Serves(IResourceSource s) =>
            !types.TryGetValue(s.Name, out var t) || t.Count == 0 || t.Contains(category, StringComparer.OrdinalIgnoreCase);

        var chosen = sources.Where(Serves).ToList();
        var none = chosen.Count == 0 && sources.Count > 0;
        if (none)
        {
            chosen = sources.ToList();
        }

        var keywords = rules.EffectiveKeywords();
        var plans = chosen
            .Select((s, i) => new SourcePlan(s, keywords.Where(k => k.AppliesTo(s.Name, category)).Select(k => k.Word).ToList(), i))
            .ToList();
        return new SearchPlan(category, plans, keywords.Select(k => k.Word).ToList(), none);
    }
}

/// <summary>
/// Preferred：只搜带发布者关键词的优选资源，先返回给用户；
/// Fallback：在优选结果上追加不带关键词的降级结果；Full：两步一起做完再返回。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchStage
{
    Full,
    Preferred,
    Fallback,
}

/// <summary>
/// 一次搜索分两个阶段：先"片名 + 发布者关键词"（主标题、原名、别名依次尝试；剧集再带季号），
/// 某类结果不足 5 条时再降级为只用片名 / 片名 + 季号，结果追加进来。
/// 不打分：过滤掉明显不是这部片的，剩下的按 发布者命中 → 画质降级 → 分辨率 → 发布时间 排序，交给用户选。
/// </summary>
public sealed class SearchService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly Func<RulesDocument> _rules;
    private readonly Func<string, SearchPlan> _plan;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, SearchResult Result)> _cache = new();

    /// <summary>所有站点用同一组关键词（不区分类型）。</summary>
    public SearchService(Func<IReadOnlyList<IResourceSource>> sources, Func<RulesDocument> rules, Func<IReadOnlyList<string>> keywords)
        : this(rules, category =>
        {
            var words = keywords();
            return new SearchPlan(category, sources().Select((s, i) => new SourcePlan(s, words, i)).ToList(), words, false);
        })
    {
    }

    /// <summary>plan：按资源类型给出要请求的站点和各站点的关键词，一般是 SearchPlanner.Plan。</summary>
    public SearchService(Func<RulesDocument> rules, Func<string, SearchPlan> plan)
    {
        _rules = rules;
        _plan = plan;
    }

    /// <summary>每次请求完一个站点时调用，用来记录站点状态。</summary>
    public Action<SourceReport>? Report { get; set; }

    /// <summary>规则或配置变了：清掉搜索缓存，下次重新搜。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// 按 InfoHash 找回最近搜索结果里的版本。前端只拿到版本 ID，提交下载时由这里换回完整版本（含磁力），
    /// 这样网页和接口里都看不到原始发布名和磁力链接。
    /// </summary>
    public Release? FindRelease(string infoHash) =>
        _cache.Values
            .Where(c => DateTimeOffset.UtcNow - c.At < CacheTtl)
            .SelectMany(c => c.Result.Releases)
            .FirstOrDefault(r => string.Equals(r.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));

    public Task<SearchResult> SearchAsync(MediaQuery query, bool refresh, CancellationToken ct) =>
        SearchAsync(query, refresh, SearchStage.Full, ct);

    public async Task<SearchResult> SearchAsync(MediaQuery query, bool refresh, SearchStage stage, CancellationToken ct)
    {
        var fullKey = query.TitleKey + "|full";
        var preferredKey = query.TitleKey + "|preferred";
        if (!refresh && Cached(fullKey) is { } full)
        {
            return full with { FromCache = true, NeedsFallback = false };
        }

        if (stage == SearchStage.Preferred && !refresh && Cached(preferredKey) is { } preferredHit)
        {
            return preferredHit with { FromCache = true };
        }

        // 降级阶段紧跟在优选阶段之后请求：直接接着刚才的优选结果做，不重搜（即使是"刷新"）
        var preferred = stage == SearchStage.Fallback && Cached(preferredKey) is { } p
            ? p
            : await PreferredAsync(query, ct).ConfigureAwait(false);
        if (stage == SearchStage.Preferred)
        {
            Store(preferredKey, preferred);
            return preferred;
        }

        var result = preferred.NeedsFallback ? await FallbackAsync(query, preferred, ct).ConfigureAwait(false) : preferred;
        result = result with { NeedsFallback = false, FromCache = false };
        Store(fullKey, result);
        return result;
    }

    private SearchResult? Cached(string key) =>
        _cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl ? hit.Result : null;

    private void Store(string key, SearchResult result)
    {
        if (result.Errors.Count == 0 || result.Releases.Count > 0)
        {
            _cache[key] = (DateTimeOffset.UtcNow, result);
        }
    }

    /// <summary>
    /// 优选阶段：片名 + 发布者关键词。资源站的译名常和 TMDb 主标题不同，主标题搜不到时依次换原名、别名，命中一个就停。
    /// 剧集同时带季号搜（"第一季"和"S01"两种写法）：只用片名搜时上游按时间倒序，新一季的单集会把这一季挤出结果页。
    /// </summary>
    private async Task<SearchResult> PreferredAsync(MediaQuery query, CancellationToken ct)
    {
        var plan = _plan(query.EffectiveCategory);
        var queries = new List<string>();
        var errors = new List<string>();
        var filtered = new List<Release>();
        foreach (var title in SearchTitles(query))
        {
            // 站点在前、关键词在后：同一个版本出现在多个站点时，保留排在前面的站点的那条
            var batch = plan.Sources.SelectMany(sp => query.Kind == MediaKind.Series
                ? sp.Keywords.SelectMany(k => SeasonForms(query).Select(f => (sp.Source, $"{title}{f} {k}")))
                : sp.Keywords.Select(k => (sp.Source, $"{title} {k}")));
            filtered = Merge(filtered, Filter(await RunAsync(batch, queries, errors, ct).ConfigureAwait(false), query));
            if (filtered.Count > 0)
            {
                break;
            }
        }

        var needsFallback = query.Kind == MediaKind.Series
            ? NeedsSeriesFallback(filtered, _rules(), query.SeasonEpisodes)
            : filtered.Count < MinResultsPerPackType;
        return new SearchResult(query, Order(filtered, plan), queries, errors, false, needsFallback) { Plan = plan.Summary() };
    }

    /// <summary>
    /// 降级阶段：不带发布者关键词，剧集按季号搜（补出单集 / 分集包），电影只用片名；结果追加到优选结果后面。
    /// 排序时优选资源仍在前面，所以降级只会增加可选项，不会把优选挤下去。
    /// </summary>
    private async Task<SearchResult> FallbackAsync(MediaQuery query, SearchResult preferred, CancellationToken ct)
    {
        var plan = _plan(query.EffectiveCategory);
        var titles = SearchTitles(query);
        var queries = preferred.Queries.ToList();
        var errors = preferred.Errors.ToList();
        var words = query.Kind == MediaKind.Series
            ? titles.SelectMany(t => SeasonForms(query).Where(f => f.Length > 0).Select(f => t + f)).ToList()
            : titles;
        var filtered = Merge(preferred.Releases, Filter(await RunAsync(Each(plan, words), queries, errors, ct, FallbackPages).ConfigureAwait(false), query));

        // 剧集带季号也搜不到时，最后只用片名搜一次
        if (filtered.Count == 0 && query.Kind == MediaKind.Series)
        {
            filtered = Filter(await RunAsync(Each(plan, titles), queries, errors, ct, FallbackPages).ConfigureAwait(false), query);
        }

        return new SearchResult(query, Order(filtered, plan), queries, errors, false) { Plan = plan.Summary() };
    }

    /// <summary>剧集搜索词里的季号写法：""（不带季号）、" 第一季"、" S01"。资源站认"第一季"，不认"第1季"。</summary>
    private static IEnumerable<string> SeasonForms(MediaQuery query)
    {
        var season = query.Season ?? 1;
        return new[] { string.Empty, $" 第{ChineseNumeral(season)}季", $" S{season:00}" };
    }

    /// <summary>1 → 一，10 → 十，12 → 十二，20 → 二十。</summary>
    internal static string ChineseNumeral(int n)
    {
        const string digits = "零一二三四五六七八九";
        if (n is < 1 or > 99)
        {
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (n < 10)
        {
            return digits[n].ToString();
        }

        var tens = n / 10 == 1 ? string.Empty : digits[n / 10].ToString();
        return tens + "十" + (n % 10 == 0 ? string.Empty : digits[n % 10].ToString());
    }

    /// <summary>
    /// 搜索用的片名：主标题、原名、别名，冒号换成空格后去重，最多 4 个。
    /// 资源站按标点分词：带冒号（"Avatar: Seven Havens"）或直接删掉冒号（"降世神通七净土"）都常搜出 0 条，换成空格最稳。
    /// </summary>
    internal static List<string> SearchTitles(MediaQuery query) =>
        new[] { query.Title, query.OriginalTitle }
            .Concat(query.AlternateTitles)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ColonToSpace(t!))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

    private static string ColonToSpace(string title) =>
        string.Join(' ', title.Replace('：', ' ').Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>排序降级：0 正常；1 只有外语音轨 / 字幕；2 枪版。</summary>
    public static int Demotion(Release r) =>
        r.Traits.MediaSource == ReleaseParser.CamSource ? 2 : r.Traits.ForeignOnly ? 1 : 0;

    private const int MinResultsPerPackType = 5;

    private static bool NeedsSeriesFallback(IReadOnlyCollection<Release> releases, RulesDocument rules, int? seasonEpisodes)
    {
        var counts = releases
            .GroupBy(r => PackType(r, rules, seasonEpisodes))
            .ToDictionary(g => g.Key, g => g.Count());
        return counts.GetValueOrDefault("full") < MinResultsPerPackType
            || counts.GetValueOrDefault("multi") < MinResultsPerPackType
            || counts.GetValueOrDefault("single") < MinResultsPerPackType;
    }

    private static string PackType(Release release, RulesDocument rules, int? seasonEpisodes)
    {
        var coverage = ReleaseDisplay.Coverage(release, rules, seasonEpisodes);
        return coverage.FullSeason ? "full" : coverage.Episodes.Count > 1 ? "multi" : "single";
    }

    private static List<Release> Merge(IEnumerable<Release> current, IEnumerable<Release> more) =>
        current.Concat(more)
            .GroupBy(r => r.InfoHash, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    /// <summary>每个站点都用这些搜索词（降级阶段不带关键词）。</summary>
    private static IEnumerable<(IResourceSource Source, string Keyword)> Each(SearchPlan plan, IReadOnlyList<string> words) =>
        plan.Sources.SelectMany(sp => words.Select(w => (sp.Source, w)));

    /// <summary>同一级的请求并发（最多 MaxParallelRequests 个），结果按请求顺序拼接，保证排序稳定。</summary>
    private async Task<List<Release>> RunAsync(IEnumerable<(IResourceSource Source, string Keyword)> requests, List<string> queries, List<string> errors, CancellationToken ct, int? maxPages = null)
    {
        var list = requests.ToList();
        queries.AddRange(list.Select(r => r.Keyword).Distinct().Where(k => !queries.Contains(k)).ToList());
        using var gate = new SemaphoreSlim(MaxParallelRequests);
        var tasks = list
            .Select(r => SearchOneAsync(r.Source, r.Keyword, maxPages, gate, ct))
            .ToList();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        var all = new List<Release>();
        foreach (var (releases, error) in outcomes)
        {
            all.AddRange(releases);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        return all;
    }

    private const int MaxParallelRequests = 6;

    /// <summary>降级搜索不带发布者关键词，结果多且杂，只取前 3 页（30 条）。</summary>
    private const int FallbackPages = 3;

    private async Task<(IReadOnlyList<Release> Releases, string? Error)> SearchOneAsync(IResourceSource source, string keyword, int? maxPages, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var releases = await source.SearchAsync(keyword, ct, maxPages).ConfigureAwait(false);
            Report?.Invoke(new SourceReport(source.Name, true, watch.Elapsed, releases.Count, null));
            return (releases, null);
        }
        catch (SourceException ex)
        {
            Report?.Invoke(new SourceReport(source.Name, false, watch.Elapsed, 0, ex.Message));
            return (Array.Empty<Release>(), ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    internal List<Release> Filter(IEnumerable<Release> releases, MediaQuery query)
    {
        var rules = _rules();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Release>();
        foreach (var r in releases)
        {
            if (!seen.Add(r.InfoHash))
            {
                continue;
            }

            // 必须含有视频文件（有文件清单时）
            if (r.Files.Count > 0 && !r.Files.Any(f => rules.IsVideo(f.Path) && f.Size >= rules.MinVideoBytes))
            {
                continue;
            }

            // 标题里的年份与查询年份差 1 年以上：多半是同名的另一部
            if (query.Year is int year && r.Traits.Year is int ry && Math.Abs(ry - year) > 1 && query.Kind == MediaKind.Movie)
            {
                continue;
            }

            if (query.Kind == MediaKind.Series)
            {
                var wanted = query.Season ?? 1;
                if (r.Traits.Season is int s ? s != wanted : wanted != 1)
                {
                    continue;
                }

                // 单集、分集包也保留，由前端按"整季 / 单集"分开展示
            }

            if (ReleaseParser.IsAdult(r.Name))
            {
                continue;
            }

            if (!NameMentions(r.Name, query))
            {
                continue;
            }

            list.Add(r);
        }

        return list;
    }

    /// <summary>
    /// 标题里至少出现主标题、原名或别名之一，挡掉搜索站的模糊匹配噪音。
    /// 比较前把标点换成空格；片名前紧挨着同类文字（"海洋奥德赛"、"TheOdyssey"）、
    /// 英文片名后紧挨着字母（"Belowground"）都不算命中，避免短片名误配别的片。
    /// </summary>
    internal static bool NameMentions(string name, MediaQuery query)
    {
        var haystack = Normalize(name);
        return SearchTitles(query)
            .Select(Normalize)
            .Where(t => t.Length > 0)
            .Any(t => ContainsWord(haystack, t));
    }

    private static bool ContainsWord(string haystack, string title)
    {
        for (var i = haystack.IndexOf(title, StringComparison.OrdinalIgnoreCase); i >= 0; i = haystack.IndexOf(title, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + title.Length;
            var before = i > 0 && SameClass(haystack[i - 1], title[0]);
            var after = end < haystack.Length && !IsCjk(title[^1]) && SameClass(haystack[end], title[^1]);
            if (!before && !after)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameClass(char a, char b) =>
        a != ' ' && b != ' ' && (char.IsDigit(a) ? char.IsDigit(b) : !char.IsDigit(b) && IsCjk(a) == IsCjk(b));

    private static bool IsCjk(char c) => char.IsLetter(c) && c > '\u024F';

    /// <summary>标点、空白统一成单个空格；中日韩文字之间的空格去掉（"降世神通.七净土" 与 "降世神通：七净土" 相同）。</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var bracket = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                // 括号是硬边界："[某字幕组] 某动画" 里的"组"和"某"不能连起来
                if (!bracket && sb.Length >= 2 && sb[^1] == ' ' && IsCjk(c) && IsCjk(sb[^2]))
                {
                    sb.Length--;
                }

                sb.Append(c);
                bracket = false;
            }
            else
            {
                bracket |= Brackets.Contains(c);
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
            }
        }

        return sb.ToString().Trim();
    }

    private const string Brackets = "[]【】()（）「」『』〔〕<>《》";

    /// <summary>所有站点用同一组关键词时的排序。</summary>
    internal static List<Release> Order(IEnumerable<Release> releases, IReadOnlyList<string> keywords) =>
        Order(releases, r => FirstHit(r.Name, keywords, keywords), _ => 0);

    /// <summary>每条资源只看它所在站点、这个类型生效的关键词；站点优先级按配置顺序。</summary>
    internal static List<Release> Order(IEnumerable<Release> releases, SearchPlan plan)
    {
        var bySource = new Dictionary<string, SourcePlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var sp in plan.Sources)
        {
            bySource.TryAdd(sp.Source.Name, sp);
        }

        return Order(
            releases,
            r => bySource.TryGetValue(r.Source, out var sp) ? FirstHit(r.Name, sp.Keywords, plan.KeywordOrder) : null,
            r => bySource.TryGetValue(r.Source, out var sp) ? sp.Rank : int.MaxValue);
    }

    /// <summary>
    /// 排序：命中优先级高的关键词 → 画质降级（枪版、纯外语靠后）→ 站点顺序 → 分辨率 → 发布时间。
    /// 不打分，只决定展示顺序；KeywordRank 写进每条资源，前端据此标"优选"。
    /// </summary>
    private static List<Release> Order(IEnumerable<Release> releases, Func<Release, int?> keywordRank, Func<Release, int> sourceRank) =>
        releases
            .Select(r => r with { KeywordRank = keywordRank(r) })
            .OrderBy(r => r.KeywordRank ?? int.MaxValue)
            .ThenBy(Demotion)
            .ThenBy(sourceRank)
            .ThenByDescending(r => r.Traits.Resolution)
            .ThenByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>标题里命中的关键词中，优先级最高的那个在 order 里的位置。</summary>
    private static int? FirstHit(string name, IReadOnlyList<string> keywords, IReadOnlyList<string> order)
    {
        int? best = null;
        foreach (var k in keywords)
        {
            if (k.Length == 0 || !name.Contains(k, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = IndexOf(order, k);
            var rank = index < 0 ? order.Count : index;
            best = best is null ? rank : Math.Min(best.Value, rank);
        }

        return best;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
