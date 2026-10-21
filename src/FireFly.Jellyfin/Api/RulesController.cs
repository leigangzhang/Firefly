using System.Text.Json;
using System.Text.Json.Nodes;
using FireFly.Core.Models;
using FireFly.Core.Rules;
using FireFly.Core.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FireFly.Jellyfin.Api;

/// <summary>Rule：直接给一份站点规则（添加站点时）；Name：测试已有站点。Overlay：配置页还没保存的设置。</summary>
public sealed record SourceTestRequest(JsonElement? Rule, string? Name, string? Keyword, string? Overlay);

public sealed record DetectRequest(string? Url, string? Sample);

public sealed record ImportRequest(string? Url);

public sealed record PreviewRequest(string? Overlay);

public sealed record KeywordTryRequest(string? Title, string? Category, string? Overlay);

/// <summary>
/// 配置页"资源站点""优选关键词"用的接口。配置页的改动保存在插件配置的 RulesOverlay 里（第三层），
/// 这里负责给出内置 + rules.json 的底层规则、预览叠加结果、测试站点、从样例地址识别站点、导入规则片段。
/// </summary>
[ApiController]
[Route("FireFly")]
[Authorize(Policy = "RequiresElevation")]
public sealed class RulesController : ControllerBase
{
    private readonly FireFlyRuntime _runtime;

    public RulesController(FireFlyRuntime runtime) => _runtime = runtime;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static ContentResult ApiJson(object? value, int statusCode = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(value, Json),
        ContentType = "application/json; charset=utf-8",
        StatusCode = statusCode,
    };

    /// <summary>配置页打开时取一次：底层规则（判断哪些是内置的）、当前生效的规则、站点状态、可选的资源类型。</summary>
    [HttpGet("rules/editor")]
    public IActionResult Editor()
    {
        var lower = RulesLoader.Parse(_runtime.LowerRulesJson(out var lowerWarnings));
        var rules = _runtime.Rules;
        var config = Plugin.Instance?.Configuration;
        return ApiJson(new
        {
            lower = new
            {
                sources = lower.Sources.Select(SourceView),
                keywords = lower.EffectiveKeywords(),
                legacyKeywords = lower.PublisherKeywords.Count > 0,
            },
            overlay = ParseOverlay(config?.RulesOverlay),
            categories = rules.Discover.Categories.Select(c => new { c.Key, c.Label }),
            health = _runtime.Health.Values.ToDictionary(h => h.Source, h => new
            {
                h.Ok,
                h.At,
                ms = (long)h.Elapsed.TotalMilliseconds,
                h.Count,
                h.Error,
            }),
            effective = Effective(rules),
            warnings = rules.Warnings.Concat(lowerWarnings).Distinct(),
            rulesVersion = rules.Version,
            rulesFile = _runtime.RulesFilePath,
            rulesFileExists = System.IO.File.Exists(_runtime.RulesFilePath),
            sample = SourceDetector.DefaultSample,
        });
    }

    /// <summary>按还没保存的设置算一遍规则，返回叠加结果和问题提示。</summary>
    [HttpPost("rules/preview")]
    public IActionResult Preview([FromBody] PreviewRequest request)
    {
        var rules = _runtime.PreviewRules(request.Overlay);
        return ApiJson(new { effective = Effective(rules), warnings = rules.Warnings });
    }

    /// <summary>测试一个站点：用样例片名搜一页，返回耗时、条数和前几条标题。</summary>
    [HttpPost("sources/test")]
    public async Task<IActionResult> Test([FromBody] SourceTestRequest request, CancellationToken ct)
    {
        SourceRule? rule;
        try
        {
            rule = request.Rule is { ValueKind: JsonValueKind.Object } r
                ? r.Deserialize<SourceRule>(Json)
                : _runtime.PreviewRules(request.Overlay ?? Plugin.Instance?.Configuration.RulesOverlay).Sources
                    .FirstOrDefault(s => string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException ex)
        {
            return ApiJson(new { ok = false, error = "规则格式不对：" + ex.Message });
        }

        if (rule is null)
        {
            return ApiJson(new { ok = false, error = "没有找到这个站点" });
        }

        if (!SourceFactory.TryCreate(rule, _runtime.Http, out var source, out var problem))
        {
            return ApiJson(new { ok = false, error = problem });
        }

        var keyword = string.IsNullOrWhiteSpace(request.Keyword) ? SourceDetector.DefaultSample : request.Keyword.Trim();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var releases = await source.SearchAsync(keyword, cts.Token, 1).ConfigureAwait(false);
            var report = new SourceReport(rule.Name, true, watch.Elapsed, releases.Count, null);
            Remember(report);
            return ApiJson(new
            {
                ok = true,
                ms = (long)watch.Elapsed.TotalMilliseconds,
                count = releases.Count,
                rows = Rows(releases),
            });
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var error = ex is TaskCanceledException ? "连接超时" : ex.InnerException is { } inner && ex is SourceException ? $"{ex.Message}（{inner.Message}）" : ex.Message;
            Remember(new SourceReport(rule.Name, false, watch.Elapsed, 0, error));
            return ApiJson(new { ok = false, ms = (long)watch.Elapsed.TotalMilliseconds, error });
        }
    }

    /// <summary>"粘贴一次搜索的地址"：从样例地址识别出站点规则。</summary>
    [HttpPost("sources/detect")]
    public async Task<IActionResult> Detect([FromBody] DetectRequest request, CancellationToken ct)
    {
        var result = await new SourceDetector(_runtime.Http).DetectAsync(request.Url ?? string.Empty, request.Sample, _runtime.Rules, ct).ConfigureAwait(false);
        return ApiJson(new
        {
            steps = result.Steps,
            rule = result.Rule,
            known = result.Known,
            preview = Rows(result.Preview),
            previewCount = result.Preview.Count,
            result.ApiUrl,
            result.KeywordParam,
            arrays = result.Arrays,
            fields = result.Fields,
            result.SampleJson,
        });
    }

    /// <summary>导入规则订阅地址：由插件取回 JSON 文本（浏览器直接取会被跨域挡住），配置页再解析和预览。</summary>
    [HttpPost("sources/import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return ApiJson(new { error = "请填写 http / https 开头的地址" }, StatusCodes.Status400BadRequest);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _runtime.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImportBytes)
            {
                return ApiJson(new { error = "文件太大，规则片段一般只有几 KB" }, StatusCodes.Status400BadRequest);
            }

            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return text.Length > MaxImportBytes
                ? ApiJson(new { error = "文件太大，规则片段一般只有几 KB" }, StatusCodes.Status400BadRequest)
                : ApiJson(new { text });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ApiJson(new { error = "取不到这个地址：" + (ex is TaskCanceledException ? "连接超时" : ex.Message) }, StatusCodes.Status502BadGateway);
        }
    }

    private const int MaxImportBytes = 512 * 1024;

    /// <summary>
    /// "连接 Jackett / Prowlarr"：看本机常用端口上有没有在跑，有就给出可以直接填的 Torznab 地址。
    /// 只探测本机（插件和 Jackett 常装在同一台机器 / 同一个 Docker 网络里），不扫描局域网。
    /// </summary>
    [HttpGet("sources/indexers")]
    public async Task<IActionResult> ProbeIndexers(CancellationToken ct)
    {
        var probes = new[]
        {
            (Name: "Jackett", Base: "http://127.0.0.1:9117", Path: "/UI/Login", Torznab: "http://127.0.0.1:9117/api/v2.0/indexers/all/results/torznab"),
            (Name: "Jackett", Base: "http://jackett:9117", Path: "/UI/Login", Torznab: "http://jackett:9117/api/v2.0/indexers/all/results/torznab"),
            (Name: "Prowlarr", Base: "http://127.0.0.1:9696", Path: "/ping", Torznab: "http://127.0.0.1:9696/{indexerId}/api"),
            (Name: "Prowlarr", Base: "http://prowlarr:9696", Path: "/ping", Torznab: "http://prowlarr:9696/{indexerId}/api"),
        };
        var found = await Task.WhenAll(probes.Select(async p =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await _runtime.Http.GetAsync(p.Base + p.Path, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                return (int)response.StatusCode < 500 ? new { name = p.Name, url = p.Base, torznab = p.Torznab } : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        })).ConfigureAwait(false);
        return ApiJson(new { found = found.Where(f => f is not null).GroupBy(f => f!.name).Select(g => g.First()) });
    }

    /// <summary>"试一试"：按（还没保存的）设置，只做优选阶段，看每个关键词能搜到多少。</summary>
    [HttpPost("keywords/try")]
    public async Task<IActionResult> TryKeywords([FromBody] KeywordTryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ApiJson(new { error = "请输入片名" }, StatusCodes.Status400BadRequest);
        }

        var category = MediaCategories.IsKnown(request.Category) ? request.Category! : MediaCategories.Movie;
        var rules = _runtime.PreviewRules(request.Overlay);
        var sources = SourceFactory.Create(rules, _runtime.Http);
        var plan = SearchPlanner.Plan(rules, sources, category);
        var service = new SearchService(() => rules, c => SearchPlanner.Plan(rules, sources, c)) { Report = Remember };
        var query = new MediaQuery
        {
            Kind = category == MediaCategories.Movie ? MediaKind.Movie : MediaKind.Series,
            Title = request.Title.Trim(),
            Season = category == MediaCategories.Movie ? null : 1,
            Category = category,
        };
        var result = await service.SearchAsync(query, true, SearchStage.Preferred, ct).ConfigureAwait(false);
        var preferred = result.Releases.Where(r => r.KeywordRank is not null).ToList();
        return ApiJson(new
        {
            count = preferred.Count,
            perKeyword = plan.KeywordOrder
                .Where(k => plan.Sources.Any(s => s.Keywords.Contains(k)))
                .Select(k => new { word = k, count = preferred.Count(r => r.Name.Contains(k, StringComparison.OrdinalIgnoreCase)) }),
            sources = plan.Sources.Count,
            errors = result.Errors.Count,
        });
    }

    private void Remember(SourceReport report)
    {
        if (_runtime.Rules.Sources.Any(s => string.Equals(s.Name, report.Source, StringComparison.OrdinalIgnoreCase)))
        {
            _runtime.RecordHealth(report);
        }
    }

    private static object SourceView(SourceRule s) => new
    {
        s.Name,
        s.Kind,
        s.Endpoints,
        s.Enabled,
        s.Types,
    };

    private static IEnumerable<object> Rows(IEnumerable<Release> releases) =>
        releases.Take(5).Select(r => new { r.Name, size = r.SizeBytes, date = r.CreatedAt });

    private static JsonNode? ParseOverlay(string? overlay)
    {
        if (string.IsNullOrWhiteSpace(overlay))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(overlay) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>叠加后的规则，"高级"里只读展示；API Key 打码。</summary>
    private static string Effective(RulesDocument rules)
    {
        var node = JsonSerializer.SerializeToNode(rules, Json) as JsonObject ?? new JsonObject();
        if (node["sources"] is JsonArray sources)
        {
            foreach (var s in sources.OfType<JsonObject>())
            {
                if (s["apiKey"] is JsonValue v && v.TryGetValue<string>(out var key) && key.Length > 0)
                {
                    s["apiKey"] = "******";
                }
            }
        }

        return node.ToJsonString(new JsonSerializerOptions(Json) { WriteIndented = true });
    }
}
