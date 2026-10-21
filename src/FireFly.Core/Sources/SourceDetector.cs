using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

/// <summary>识别过程中的一步。State：ok 成功、fail 失败、skip 没用到、info 提示。</summary>
public sealed record DetectStep(string Label, string State, string Detail);

/// <summary>接口返回里的一个字段：路径、示例值、类型（string / number / bool / array / object）。</summary>
public sealed record SamplePath(string Path, string Sample, string Type);

/// <summary>
/// 识别结果。Rule 不为空表示识别成功（Known = 是已有的站点）；
/// 找到了 JSON 接口但没认出字段时 Rule 为空，ApiUrl / Arrays / Fields 给手动映射用。
/// </summary>
public sealed record DetectResult(
    IReadOnlyList<DetectStep> Steps,
    SourceRule? Rule,
    bool Known,
    IReadOnlyList<Release> Preview,
    string? ApiUrl,
    string? KeywordParam,
    IReadOnlyList<SamplePath> Arrays,
    IReadOnlyList<SamplePath> Fields,
    string? SampleJson);

/// <summary>
/// "粘贴一次搜索的地址"：用户在资源站搜一次，把浏览器地址栏的地址贴过来，插件从这个样例学会这个站怎么搜。
/// 网页地址和接口地址往往不同，按四步依次尝试，前一步成功就停：
/// 1. 认识的站（规则里已有这个域名）直接用现成规则；
/// 2. 地址本身就是 JSON 接口，或同域名下常见的接口路径；
/// 3. 从网页和它引用的脚本里找 /api/... 路径；
/// 4. 都不行就请用户在开发者工具里复制接口地址（前端提示）。
/// 找到 JSON 后自动认出列表、hash、标题、体积、时间等字段（AutoMap）。
/// </summary>
public sealed partial class SourceDetector
{
    public const string DefaultSample = "流浪地球";

    /// <summary>一次识别最多发多少个请求，避免对站点扫描太多。</summary>
    private const int MaxRequests = 24;

    private static readonly string[] KeywordParams = { "keyword", "keywords", "q", "wd", "kw", "search", "query", "s", "k", "key", "word", "title", "name" };
    private static readonly string[] LimitParams = { "limit", "size", "pagesize", "page_size", "per_page", "perpage", "num", "count", "rows" };
    private static readonly string[] OffsetParams = { "offset", "start", "skip", "from" };

    private readonly HttpClient _http;
    private int _requests;
    private string? _signedApi;

    public SourceDetector(HttpClient http) => _http = http;

    public async Task<DetectResult> DetectAsync(string pasted, string? sample, RulesDocument rules, CancellationToken ct)
    {
        var steps = new List<DetectStep>();
        if (!Uri.TryCreate(pasted?.Trim(), UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
        {
            steps.Add(new DetectStep("读取地址", "fail", "这不是一个 http / https 地址"));
            return Fail(steps);
        }

        // 1. 认识的站
        var known = rules.Sources.FirstOrDefault(s => s.Endpoints.Any(e => SameSite(e, page)));
        if (known is not null)
        {
            steps.Add(new DetectStep("1. 认识这个站吗", "ok", $"已有站点 {known.Name}，直接使用现成规则"));
            return new DetectResult(steps, known, true, Array.Empty<Release>(), known.Endpoints.FirstOrDefault(), known.Request.KeywordParam, Array.Empty<SamplePath>(), Array.Empty<SamplePath>(), null);
        }

        steps.Add(new DetectStep("1. 认识这个站吗", "skip", $"规则里没有 {page.Host}"));

        var query = ParseQuery(page.Query);
        var keywordParam = FindKeywordParam(query, sample);
        if (keywordParam is null)
        {
            // 搜索词在路径里（/search/流浪地球/1）：没有查询参数可换，只能按网页站点识别
            if (PathKeyword(page, sample) is not { } inPath)
            {
                steps.Add(new DetectStep("找搜索词", "fail", "地址里没找到搜索词：请在站内搜索一次，复制搜索结果页的地址"));
                return Fail(steps);
            }

            var pathAttempt = await TryAsync(page, inPath, ct).ConfigureAwait(false);
            if (pathAttempt.Blocked)
            {
                steps.Add(new DetectStep("2. 打开搜索页", "fail", $"{page.Host} 开启了 Cloudflare 人机验证，插件在服务器上打不开它的搜索页（浏览器里能打开是因为你过了验证）"));
                steps.Add(new DetectStep("怎么办", "info", "可以" + ViaJackett));
                return Fail(steps);
            }

            if (pathAttempt.Html is null)
            {
                steps.Add(new DetectStep("2. 打开搜索页", "fail", pathAttempt.Error ?? "搜索词在路径里的地址只支持网页站点"));
                return Fail(steps);
            }

            steps.Add(new DetectStep("2. 找搜索接口", "skip", "搜索词在路径里，按网页站点识别"));
            return await DetectHtmlAsync(steps, MiniHtml.Parse(pathAttempt.Html), page, null, inPath, inPath, ct).ConfigureAwait(false) ?? Fail(steps);
        }

        var keyword = query.First(p => p.Key == keywordParam).Value;

        // 2. 地址本身，或同域名下常见的接口路径
        JsonCandidate? best = null;
        string? html = null;
        var first = await TryAsync(page, keyword, ct).ConfigureAwait(false);
        if (first.Blocked)
        {
            steps.Add(new DetectStep("2. 打开搜索页", "fail", $"{page.Host} 开启了 Cloudflare 人机验证，插件在服务器上打不开它的搜索页（浏览器里能打开是因为你过了验证）"));
            steps.Add(new DetectStep("怎么办", "info", "可以" + ViaJackett));
            return Fail(steps);
        }

        if (first.Json is { } direct)
        {
            best = direct;
            if (direct.Mapped is not null)
            {
                steps.Add(new DetectStep("2. 找搜索接口", "ok", $"粘贴的地址就是接口，返回 JSON"));
                return Success(steps, direct, page, keywordParam, query);
            }
        }
        else
        {
            html = first.Html;

            // 搜索页里直接列着带磁力的结果：先按网页站点识别，不再去猜接口路径（少发请求，免得被站点限流）
            if (html is not null && GuessHtml(MiniHtml.Parse(html), keyword, page) is { MagnetInList: true })
            {
                var listed = new List<DetectStep>(steps) { new("2. 找搜索接口", "skip", "搜索页里直接列着带磁力的结果，按网页读取") };
                if (await DetectHtmlAsync(listed, MiniHtml.Parse(html), page, keywordParam, keyword, keyword, ct).ConfigureAwait(false) is { } fromList)
                {
                    return fromList;
                }
            }
        }

        foreach (var url in GuessUrls(page, keywordParam, keyword))
        {
            var attempt = await TryAsync(url, keyword, ct).ConfigureAwait(false);
            if (attempt.Json is { } found)
            {
                best ??= found;
                if (found.Mapped is not null)
                {
                    steps.Add(new DetectStep("2. 同域名找接口", "ok", $"{page.AbsolutePath} 是网页；{url.AbsolutePath} 返回 JSON"));
                    return Success(steps, found, url, keywordParam, ParseQuery(url.Query));
                }
            }
        }

        steps.Add(new DetectStep("2. 同域名找接口", "fail", "常见的接口路径都没有返回资源列表"));

        // 3. 从网页和脚本里找 /api/...
        if (html is not null)
        {
            var paths = new List<string>(ExtractApiPaths(html));
            foreach (var script in ScriptUrls(html, page).Take(6))
            {
                if (_requests >= MaxRequests)
                {
                    break;
                }

                _requests++;
                try
                {
                    var js = await _http.GetStringAsync(script, ct).ConfigureAwait(false);
                    paths.AddRange(ExtractApiPaths(js));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                }
            }

            foreach (var url in ApiCandidates(paths, page, keywordParam, keyword))
            {
                if (_requests >= MaxRequests)
                {
                    break;
                }

                var attempt = await TryAsync(url, keyword, ct).ConfigureAwait(false);
                if (attempt.Json is { } found)
                {
                    best ??= found;
                    if (found.Mapped is not null)
                    {
                        steps.Add(new DetectStep("3. 从网页里找线索", "ok", $"网页脚本里找到 {url.AbsolutePath}，返回 JSON"));
                        return Success(steps, found, url, keywordParam, ParseQuery(url.Query));
                    }
                }
            }

            steps.Add(new DetectStep("3. 从网页里找线索", "fail", paths.Count == 0 ? "网页和脚本里没有找到接口路径" : $"试了 {paths.Distinct().Count()} 个接口路径，都没有返回资源列表"));
        }
        else
        {
            steps.Add(new DetectStep("3. 从网页里找线索", "fail", first.Error ?? "没能打开这个网页"));
        }

        if (html is not null && await DetectHtmlAsync(steps, MiniHtml.Parse(html), page, keywordParam, keyword, keyword, ct).ConfigureAwait(false) is { } fromHtml)
        {
            return fromHtml;
        }

        if (best is not null)
        {
            steps.Add(new DetectStep("读取结果", "fail", "找到了 JSON 接口，但没认出资源列表或 hash 字段，可以手动调整"));
            return new DetectResult(steps, null, false, Array.Empty<Release>(), best.Url.ToString(), keywordParam, best.Arrays, best.Fields, best.SampleJson);
        }

        if (html is not null && LooksLikeResultPage(html))
        {
            steps.Add(new DetectStep("怎么办", "info", "网页里有磁力，但没认出结果列表的结构；可以在下面手动填选择器，或者" + ViaJackett));
            return Fail(steps);
        }

        if (_signedApi is not null)
        {
            steps.Add(new DetectStep("怎么办", "info", $"这个站的搜索接口（{_signedApi}）要求每次请求带上网页脚本算出的签名，这是站点用来挡第三方程序的，插件不模仿。可以" + ViaJackett));
            return Fail(steps);
        }

        steps.Add(new DetectStep("请你帮忙", "info", "按 F12 打开开发者工具 → 网络（Network）→ 再搜一次 → 复制名字里带 search 的那条请求地址，粘贴到这里。如果那条地址就是你粘贴的网页地址，说明这个站没有数据接口。也可以" + ViaJackett));
        return Fail(steps);
    }

    private const string ViaJackett = "在 Jackett / Prowlarr 里添加这个站（开了人机验证的站需要再配一个 FlareSolverr），然后用\"Torznab\"方式接入。";

    /// <summary>网页里直接有磁力链接或一串 info-hash：结果是服务端渲染的。</summary>
    internal static bool LooksLikeResultPage(string html) =>
        html.Contains("magnet:?", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.Matches(html, @"(?<![0-9a-fA-F])[0-9a-fA-F]{40}(?![0-9a-fA-F])").Count >= 3;

    /// <summary>接口因为缺少签名拒绝请求：401/403/400，返回里提到 sign / signature。</summary>
    internal static bool NeedsSignature(int status, string body) =>
        status is 400 or 401 or 403 && body.Length < 2000
        && System.Text.RegularExpressions.Regex.IsMatch(body, @"\b(missing|invalid|wrong|bad|no)[ _-]?sign(ature)?\b|\bsign(ature)?[ _-]?(missing|invalid|error|required|expired)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Cloudflare 等人机验证页：403/503，带 cf-mitigated 头或"Just a moment"挑战页。</summary>
    internal static bool IsChallenge(int status, string? mitigated, string? server, string body) =>
        status is 403 or 503 or 429
        && ((mitigated?.Contains("challenge", StringComparison.OrdinalIgnoreCase) ?? false)
            || ((server?.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false)
                && (body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))));

    private static DetectResult Fail(List<DetectStep> steps) =>
        new(steps, null, false, Array.Empty<Release>(), null, null, Array.Empty<SamplePath>(), Array.Empty<SamplePath>(), null);

    private static DetectResult Success(List<DetectStep> steps, JsonCandidate found, Uri api, string keywordParam, List<KeyValuePair<string, string>> query)
    {
        var rule = BuildRule(api, keywordParam, query, found.Mapped!);
        steps.Add(new DetectStep("读取结果", "ok", $"找到 {found.Preview.Count} 条资源"));
        return new DetectResult(steps, rule, false, found.Preview, api.ToString(), keywordParam, found.Arrays, found.Fields, found.SampleJson);
    }

    private sealed record JsonCandidate(Uri Url, JsonApiResponse? Mapped, IReadOnlyList<Release> Preview, IReadOnlyList<SamplePath> Arrays, IReadOnlyList<SamplePath> Fields, string SampleJson);

    private sealed record Attempt(JsonCandidate? Json, string? Html, string? Error, bool Blocked = false);

    private async Task<Attempt> TryAsync(Uri url, string keyword, CancellationToken ct)
    {
        if (_requests >= MaxRequests)
        {
            return new Attempt(null, null, "请求次数已达上限");
        }

        _requests++;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json, text/html;q=0.9, */*;q=0.8");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var mitigated = response.Headers.TryGetValues("cf-mitigated", out var m) ? string.Join(",", m) : null;
                var server = response.Headers.Server.ToString();
                var text = status is 400 or 401 or 403 or 503 or 429 ? await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false) : string.Empty;
                var blocked = IsChallenge(status, mitigated, server, text);
                if (!blocked && NeedsSignature(status, text))
                {
                    _signedApi ??= url.AbsolutePath;
                }
                return new Attempt(null, null, blocked ? $"HTTP {status}（人机验证）" : $"HTTP {status}", blocked);
            }

            body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new Attempt(null, null, ex is TaskCanceledException ? "连接超时" : ex.Message);
        }

        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return new Attempt(null, body, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var mapped = AutoMap(root, keyword);
            var preview = Array.Empty<Release>() as IReadOnlyList<Release>;
            if (mapped is not null)
            {
                var rule = new SourceRule { Name = url.Host, Kind = SourceFactory.JsonApi, Response = mapped };
                preview = JsonApiSource.Find(root, mapped.ItemsPath) is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray().Take(20).Select(e => JsonApiSource.ToRelease(rule, e)).Where(r => r.InfoHash.Length > 0).ToList()
                    : Array.Empty<Release>();
                if (preview.Count == 0)
                {
                    mapped = null;
                }
            }

            return new Attempt(new JsonCandidate(url, mapped, preview, Arrays(root), ItemFields(root, mapped?.ItemsPath), Sample(root)), null, null);
        }
        catch (JsonException)
        {
            return new Attempt(null, body, null);
        }
    }

    /// <summary>同一个站：主机名相同，或去掉 www. / m. 之后相同。</summary>
    internal static bool SameSite(string endpoint, Uri page) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var e) && string.Equals(BareHost(e.Host), BareHost(page.Host), StringComparison.OrdinalIgnoreCase);

    private static string BareHost(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host.StartsWith("m.", StringComparison.OrdinalIgnoreCase) ? host[2..] : host;

    internal static List<KeyValuePair<string, string>> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var i = p.IndexOf('=');
                var key = Uri.UnescapeDataString((i < 0 ? p : p[..i]).Replace('+', ' '));
                var value = i < 0 ? string.Empty : Uri.UnescapeDataString(p[(i + 1)..].Replace('+', ' '));
                return new KeyValuePair<string, string>(key, value);
            })
            .ToList();

    /// <summary>搜索词所在的参数：值等于样例片名的；没有时取含中文的、或常见参数名里有值的。</summary>
    internal static string? FindKeywordParam(List<KeyValuePair<string, string>> query, string? sample)
    {
        sample = string.IsNullOrWhiteSpace(sample) ? DefaultSample : sample.Trim();
        var exact = query.FirstOrDefault(p => string.Equals(p.Value.Trim(), sample, StringComparison.OrdinalIgnoreCase));
        if (exact.Key is not null)
        {
            return exact.Key;
        }

        var contains = query.FirstOrDefault(p => p.Value.Contains(sample, StringComparison.OrdinalIgnoreCase));
        if (contains.Key is not null)
        {
            return contains.Key;
        }

        var cjk = query.FirstOrDefault(p => p.Value.Any(c => c > '⹿'));
        if (cjk.Key is not null)
        {
            return cjk.Key;
        }

        return query.FirstOrDefault(p => p.Value.Length > 0 && !p.Value.All(char.IsDigit) && KeywordParams.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).Key;
    }

    /// <summary>同域名下常见的接口路径，带上原来的查询参数。</summary>
    internal static IEnumerable<Uri> GuessUrls(Uri page, string keywordParam, string keyword)
    {
        var origin = page.GetLeftPart(UriPartial.Authority);
        var path = page.AbsolutePath.TrimEnd('/');
        var paths = new List<string>();
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) && path.Length > 0)
        {
            paths.Add("/api" + path);
        }

        paths.AddRange(new[] { "/api/search", "/api/v1/search", "/api/torrents/search", "/api/torrent/search", "/api/search/torrents" });
        if (path.Length > 0)
        {
            paths.Add(path + ".json");
        }

        return paths
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new Uri(origin + p + page.Query));
    }

    /// <summary>网页里引用的同域名脚本。</summary>
    internal static IEnumerable<Uri> ScriptUrls(string html, Uri page) =>
        ScriptRegex().Matches(html)
            .Select(m => Uri.TryCreate(page, m.Groups["src"].Value, out var u) ? u : null)
            .OfType<Uri>()
            .Where(u => string.Equals(BareHost(u.Host), BareHost(page.Host), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(u => u.ToString());

    /// <summary>文本里出现的接口路径（/api/... 或完整地址），带 search 的排在前面。</summary>
    internal static IEnumerable<string> ExtractApiPaths(string text) =>
        ApiPathRegex().Matches(text)
            .Select(m => m.Groups["path"].Value.TrimEnd('/', '.'))
            .Where(p => p.Length > 4 && !p.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Contains("search", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    private static IEnumerable<Uri> ApiCandidates(IEnumerable<string> paths, Uri page, string keywordParam, string keyword)
    {
        var origin = page.GetLeftPart(UriPartial.Authority);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var baseUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : origin + path;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u))
            {
                continue;
            }

            foreach (var param in new[] { keywordParam, "keyword", "q" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var url = $"{u.GetLeftPart(UriPartial.Path)}?{Uri.EscapeDataString(param)}={Uri.EscapeDataString(keyword)}";
                if (tried.Add(url))
                {
                    yield return new Uri(url);
                }
            }
        }
    }

    private static SourceRule BuildRule(Uri api, string keywordParam, List<KeyValuePair<string, string>> query, JsonApiResponse response)
    {
        var limit = query.FirstOrDefault(p => LimitParams.Contains(p.Key, StringComparer.OrdinalIgnoreCase));
        var offset = query.FirstOrDefault(p => OffsetParams.Contains(p.Key, StringComparer.OrdinalIgnoreCase));
        var used = new HashSet<string>(new[] { keywordParam, limit.Key, offset.Key }.OfType<string>(), StringComparer.OrdinalIgnoreCase);

        // 其余查询参数（如 type=all）原样留在地址里
        var rest = query.Where(p => !used.Contains(p.Key)).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}").ToList();
        var endpoint = api.GetLeftPart(UriPartial.Path) + (rest.Count > 0 ? "?" + string.Join('&', rest) : string.Empty);
        var pageSize = int.TryParse(limit.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 10;
        return new SourceRule
        {
            Name = api.Host,
            Kind = SourceFactory.JsonApi,
            Endpoints = new List<string> { endpoint },
            PageSize = pageSize,

            // 不知道翻页参数时只取一页，免得每页都是同样的结果
            MaxPages = offset.Key is null ? 1 : 3,
            Request = new JsonApiRequest
            {
                KeywordParam = keywordParam,
                LimitParam = limit.Key ?? "limit",
                OffsetParam = offset.Key ?? "offset",
                MaxLimit = pageSize,
            },
            Response = response,
        };
    }

    /// <summary>
    /// 从一份搜索结果 JSON 里自动认出字段：资源列表、hash（或磁力）、标题、体积、时间、文件列表、状态码、下一页标志。
    /// 认不出列表或 hash 时返回 null。
    /// </summary>
    public static JsonApiResponse? AutoMap(JsonElement root, string keyword)
    {
        var best = ArraysOf(root, string.Empty, 0)
            .Select(a => (a.Path, a.Items, Hash: HashField(a.Items)))
            .Where(a => a.Hash is not null)
            .OrderByDescending(a => a.Items.Count)
            .FirstOrDefault();
        if (best.Hash is null)
        {
            return null;
        }

        var items = best.Items;
        var fields = items.SelectMany(i => Flatten(i, string.Empty, 0)).GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.Select(x => x.Value).ToList());
        var magnet = fields.FirstOrDefault(f => f.Value.Any(v => v.ValueKind == JsonValueKind.String && v.GetString()!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))).Key;
        var name = NameField(fields, keyword, best.Hash, magnet);
        var size = NumberField(fields, v => v > 1024 * 1024, "size", "length", "bytes", "filesize");
        var (created, format) = DateField(fields);
        var files = FilesField(items);
        var response = new JsonApiResponse
        {
            ItemsPath = best.Path,
            Fields = new JsonApiFields
            {
                InfoHash = best.Hash,
                Name = name ?? best.Hash,
                Magnet = magnet,
                Size = size,
                CreatedAt = created,
                CreatedAtFormat = format,
                Files = files?.Path,
                FilePath = files?.FilePath ?? "path",
                FileSize = files?.FileSize ?? "size",
            },
        };

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
            {
                var key = p.Name.ToLowerInvariant();
                if (response.StatusPath is null && key is "status" or "code" or "errcode" or "ret" or "err_code" && p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True)
                {
                    response.StatusPath = p.Name;
                    response.StatusOk = p.Value.ValueKind == JsonValueKind.True ? "true" : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                }
                else if (response.MessagePath is null && key is "message" or "msg" or "error" or "errmsg" && p.Value.ValueKind == JsonValueKind.String)
                {
                    response.MessagePath = p.Name;
                }
            }
        }

        response.HasMorePath = HasMoreField(root, best.Path);
        return response;
    }

    private static IEnumerable<(string Path, List<JsonElement> Items)> ArraysOf(JsonElement e, string path, int depth)
    {
        if (depth > 6)
        {
            yield break;
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            var objects = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(30).ToList();
            if (objects.Count > 0 && path.Length > 0)
            {
                yield return (path, objects);
            }

            yield break;
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var p in e.EnumerateObject())
        {
            foreach (var a in ArraysOf(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, depth + 1))
            {
                yield return a;
            }
        }
    }

    /// <summary>对象里的标量字段（含嵌套对象，最多两层），路径相对于对象。</summary>
    private static IEnumerable<(string Path, JsonElement Value)> Flatten(JsonElement e, string path, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object || depth > 2)
        {
            yield break;
        }

        foreach (var p in e.EnumerateObject())
        {
            var child = path.Length == 0 ? p.Name : path + "." + p.Name;
            if (p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in Flatten(p.Value, child, depth + 1))
                {
                    yield return f;
                }
            }
            else
            {
                yield return (child, p.Value);
            }
        }
    }

    /// <summary>多数条目里都是 40 位十六进制 / 32 位 Base32 / 磁力链接的字段；优先纯 hash。</summary>
    private static string? HashField(List<JsonElement> items)
    {
        var fields = items.SelectMany(i => Flatten(i, string.Empty, 0)).GroupBy(f => f.Path);
        string? magnet = null;
        foreach (var g in fields)
        {
            var values = g.Where(v => v.Value.ValueKind == JsonValueKind.String).Select(v => v.Value.GetString()!).ToList();
            if (values.Count * 2 < items.Count)
            {
                continue;
            }

            if (values.Count(v => TorznabSource.Normalize(v) is not null) * 2 >= items.Count)
            {
                return g.Key;
            }

            if (magnet is null && values.Count(v => TorznabSource.Normalize(TorznabSource.HashFromMagnet(v)) is not null) * 2 >= items.Count)
            {
                magnet = g.Key;
            }
        }

        return magnet;
    }

    private static string? NameField(Dictionary<string, List<JsonElement>> fields, string keyword, string hash, string? magnet)
    {
        var strings = fields
            .Where(f => f.Key != hash && f.Key != magnet)
            .Select(f => (f.Key, Values: f.Value.Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()))
            .Where(f => f.Values.Count > 0 && !f.Values.Any(v => v.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var bare = keyword.Replace(" ", string.Empty);
        var withKeyword = strings
            .Select(f => (f.Key, Hits: f.Values.Count(v => v.Replace(" ", string.Empty).Contains(bare, StringComparison.OrdinalIgnoreCase) || v.Replace(".", string.Empty).Contains(bare, StringComparison.OrdinalIgnoreCase))))
            .Where(f => f.Hits > 0)
            .OrderByDescending(f => f.Hits)
            .ThenBy(f => NamePreference(f.Key))
            .FirstOrDefault();
        if (withKeyword.Key is not null)
        {
            return withKeyword.Key;
        }

        return strings.OrderBy(f => NamePreference(f.Key)).ThenByDescending(f => f.Values.Average(v => v.Length)).Select(f => f.Key).FirstOrDefault();
    }

    private static int NamePreference(string path)
    {
        var key = path.Split('.')[^1].ToLowerInvariant();
        return key is "name" or "title" or "filename" or "torrent_name" ? 0 : 1;
    }

    private static string? NumberField(Dictionary<string, List<JsonElement>> fields, Func<long, bool> accept, params string[] preferred) =>
        fields
            .Select(f => (f.Key, Values: f.Value.Select(AsLong).OfType<long>().Where(accept).ToList()))
            .Where(f => f.Values.Count * 2 >= Math.Max(1, fields.Values.Max(v => v.Count)))
            .OrderBy(f => preferred.Any(p => f.Key.Split('.')[^1].Contains(p, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenByDescending(f => f.Values.Count)
            .Select(f => f.Key)
            .FirstOrDefault(k => !LooksLikeTime(k));

    private static bool LooksLikeTime(string path)
    {
        var key = path.Split('.')[^1].ToLowerInvariant();
        return key.Contains("time") || key.Contains("date") || key.Contains("created") || key.Contains("updated") || key.EndsWith("_at") || key.EndsWith("at") && key.Length <= 9 || key == "ts";
    }

    private static (string? Path, string Format) DateField(Dictionary<string, List<JsonElement>> fields)
    {
        const long minSeconds = 946684800;   // 2000-01-01
        const long maxSeconds = 4102444800;  // 2100-01-01
        var candidates = new List<(string Path, string Format, bool Named)>();
        foreach (var (path, values) in fields)
        {
            var numbers = values.Select(AsLong).OfType<long>().ToList();
            var named = LooksLikeTime(path);
            if (numbers.Count > 0 && numbers.All(n => n is >= minSeconds and <= maxSeconds))
            {
                candidates.Add((path, "unix", named));
            }
            else if (numbers.Count > 0 && numbers.All(n => n is >= minSeconds * 1000 and <= maxSeconds * 1000))
            {
                candidates.Add((path, "unixms", named));
            }
            else if (values.All(v => v.ValueKind == JsonValueKind.String) && values.Count > 0
                && values.All(v => v.GetString()!.Length >= 10 && char.IsDigit(v.GetString()![0]) && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)))
            {
                candidates.Add((path, "iso", named));
            }
        }

        var pick = candidates.OrderByDescending(c => c.Named).FirstOrDefault();
        return (pick.Path, pick.Path is null ? "unix" : pick.Format);
    }

    private sealed record FilesMapping(string Path, string FilePath, string FileSize);

    private static FilesMapping? FilesField(List<JsonElement> items)
    {
        foreach (var item in items.Take(5))
        {
            foreach (var p in item.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var first = p.Value.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
                if (first.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var props = first.EnumerateObject().ToList();
                var path = props.FirstOrDefault(x => x.Value.ValueKind == JsonValueKind.String && (x.Value.GetString()!.Contains('.') || x.Value.GetString()!.Contains('/')));
                var size = FileSizeField(p.Value, props);
                if (path.Value.ValueKind == JsonValueKind.String && size is not null)
                {
                    return new FilesMapping(p.Name, path.Name, size);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 文件大小字段：先按名字找（size / length / bytes），再找数值像字节数的字段。
    /// 不能取第一个数字字段：有的站点文件对象是 {"index":0,"path":..,"size":..}，序号会被当成大小，所有文件都太小被过滤掉。
    /// </summary>
    private static string? FileSizeField(JsonElement files, List<JsonProperty> props)
    {
        var numeric = props.Where(x => AsLong(x.Value) is not null).Select(x => x.Name).ToList();
        string Key(string name) => name.ToLowerInvariant().Replace("_", string.Empty);
        var named = numeric.FirstOrDefault(n => Key(n) is "size" or "length" or "bytes" or "filesize" or "filelength" or "len")
            ?? numeric.FirstOrDefault(n => Key(n).Contains("size") || Key(n).Contains("length") || Key(n).Contains("bytes"));
        if (named is not null)
        {
            return named;
        }

        var all = files.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(50).ToList();
        return numeric
            .Select(n => (Name: n, Max: all.Select(f => f.TryGetProperty(n, out var v) ? AsLong(v) ?? 0 : 0).DefaultIfEmpty(0).Max()))
            .Where(x => x.Max > 1024 * 1024)
            .OrderByDescending(x => x.Max)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    /// <summary>列表所在对象或根对象里像"还有下一页"的布尔字段。</summary>
    private static string? HasMoreField(JsonElement root, string itemsPath)
    {
        var parentPath = itemsPath.Contains('.') ? itemsPath[..itemsPath.LastIndexOf('.')] : string.Empty;
        foreach (var path in new[] { parentPath, string.Empty }.Distinct())
        {
            var parent = path.Length == 0 ? root : JsonApiSource.Find(root, path);
            if (parent is not { ValueKind: JsonValueKind.Object } obj)
            {
                continue;
            }

            foreach (var p in obj.EnumerateObject())
            {
                var key = p.Name.ToLowerInvariant().Replace("_", string.Empty);
                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False && key is "hasmore" or "more" or "hasnext" or "hasnextpage")
                {
                    return path.Length == 0 ? p.Name : path + "." + p.Name;
                }
            }
        }

        return null;
    }

    private static long? AsLong(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    /// <summary>JSON 里所有对象数组的路径（手动映射时选"资源列表"用）。</summary>
    private static IReadOnlyList<SamplePath> Arrays(JsonElement root) =>
        ArraysOf(root, string.Empty, 0).Select(a => new SamplePath(a.Path, $"{a.Items.Count} 项", "array")).ToList();

    /// <summary>列表第一项的字段（手动映射时的下拉选项）；没认出列表时取最大的那个数组。</summary>
    private static IReadOnlyList<SamplePath> ItemFields(JsonElement root, string? itemsPath)
    {
        var items = itemsPath is null
            ? ArraysOf(root, string.Empty, 0).OrderByDescending(a => a.Items.Count).Select(a => a.Items).FirstOrDefault()
            : JsonApiSource.Find(root, itemsPath) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : null;
        var first = items?.FirstOrDefault();
        if (first is not { ValueKind: JsonValueKind.Object } item)
        {
            return Array.Empty<SamplePath>();
        }

        var list = Flatten(item, string.Empty, 0)
            .Select(f => new SamplePath(f.Path, Short(f.Value), f.Value.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "bool",
                JsonValueKind.Array => "array",
                _ => "other",
            }))
            .ToList();
        return list;
    }

    private static string Short(JsonElement e)
    {
        var text = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
        return text.Length > 80 ? text[..77] + "…" : text;
    }

    /// <summary>接口返回的示例：数组只留第一项，最多 4 KB，给手动映射对照看。</summary>
    private static string Sample(JsonElement root)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(root.GetRawText());
        Trim(node, 0);
        var text = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? string.Empty;
        return text.Length > 4096 ? text[..4096] + "\n…" : text;
    }

    private static void Trim(System.Text.Json.Nodes.JsonNode? node, int depth)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonArray array:
                while (array.Count > 1)
                {
                    array.RemoveAt(array.Count - 1);
                }

                if (array.Count == 1)
                {
                    Trim(array[0], depth + 1);
                }

                break;
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var (_, value) in obj.ToList())
                {
                    Trim(value, depth + 1);
                }

                break;
        }
    }

    [GeneratedRegex(@"<script[^>]+src\s*=\s*[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"[""'`](?<path>(?:https?://[A-Za-z0-9.\-:]+)?/api/[A-Za-z0-9_\-/.]{2,80})")]
    private static partial Regex ApiPathRegex();
}
