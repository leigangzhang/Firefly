using System.Collections.Concurrent;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

public sealed record SearchResult(MediaQuery Query, IReadOnlyList<Release> Releases, IReadOnlyList<string> Queries, IReadOnlyList<string> Errors, bool FromCache);

/// <summary>
/// 一次搜索，两级查询：先"片名 + 发布者关键词"，没结果再"只用片名"。
/// 不打分：过滤掉明显不是这部片的，剩下的按 发布者命中 → 分辨率 → 发布时间 排序，交给用户选。
/// </summary>
public sealed class SearchService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly Func<IReadOnlyList<IResourceSource>> _sources;
    private readonly Func<RulesDocument> _rules;
    private readonly Func<IReadOnlyList<string>> _keywords;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, SearchResult Result)> _cache = new();

    public SearchService(Func<IReadOnlyList<IResourceSource>> sources, Func<RulesDocument> rules, Func<IReadOnlyList<string>> keywords)
    {
        _sources = sources;
        _rules = rules;
        _keywords = keywords;
    }

    public async Task<SearchResult> SearchAsync(MediaQuery query, bool refresh, CancellationToken ct)
    {
        var cacheKey = query.QuotaKey;
        if (!refresh && _cache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl)
        {
            return hit.Result with { FromCache = true };
        }

        var keywords = _keywords();
        var titles = new[] { query.Title, query.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var queries = new List<string>();
        var errors = new List<string>();

        // 第一级：主标题 + 每个发布者关键词（一行一个词，多词会变成"全部命中"）
        var tier1 = keywords.Select(k => $"{query.Title} {k}").ToList();
        var found = await RunAsync(tier1, queries, errors, ct).ConfigureAwait(false);
        var filtered = Filter(found, query);

        // 第二级：只用片名（中文名、原名）
        if (filtered.Count == 0)
        {
            found = await RunAsync(titles, queries, errors, ct).ConfigureAwait(false);
            filtered = Filter(found, query);
        }

        var ordered = Order(filtered, keywords);
        var result = new SearchResult(query, ordered, queries, errors, false);
        if (errors.Count == 0 || ordered.Count > 0)
        {
            _cache[cacheKey] = (DateTimeOffset.UtcNow, result);
        }

        return result;
    }

    private async Task<List<Release>> RunAsync(IEnumerable<string> keywords, List<string> queries, List<string> errors, CancellationToken ct)
    {
        var all = new List<Release>();
        foreach (var keyword in keywords)
        {
            queries.Add(keyword);
            foreach (var source in _sources())
            {
                try
                {
                    all.AddRange(await source.SearchAsync(keyword, ct).ConfigureAwait(false));
                }
                catch (SourceException ex)
                {
                    errors.Add(ex.Message);
                }
            }
        }

        return all;
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

                // MVP 只做整季：单集种子（只有一个视频文件）不列
                if (r.Files.Count > 0 && r.Files.Count(f => rules.IsVideo(f.Path) && f.Size >= rules.MinVideoBytes) < 2)
                {
                    continue;
                }
            }

            if (!NameMentions(r.Name, query))
            {
                continue;
            }

            list.Add(r);
        }

        return list;
    }

    /// <summary>标题里至少出现中文名或原名之一（去掉空白和标点后比较），挡掉搜索站的模糊匹配噪音。</summary>
    internal static bool NameMentions(string name, MediaQuery query)
    {
        var haystack = Normalize(name);
        return new[] { query.Title, query.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Any(t => haystack.Contains(Normalize(t!), StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string s) =>
        new(s.Where(c => char.IsLetterOrDigit(c)).ToArray());

    internal static List<Release> Order(IEnumerable<Release> releases, IReadOnlyList<string> keywords) =>
        releases
            .OrderByDescending(r => keywords.Any(k => r.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(r => r.Traits.Resolution)
            .ThenByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
}
