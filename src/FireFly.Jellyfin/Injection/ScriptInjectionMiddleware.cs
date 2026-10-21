using System.Text;
using Microsoft.AspNetCore.Http;

namespace FireFly.Jellyfin.Injection;

/// <summary>
/// 在 Jellyfin 网页入口 index.html 的 &lt;/body&gt; 前追加 FireFly 的脚本和样式。
/// 写法参考老项目 jellyfin-plugin-downloader（不依赖 File Transformation 等其他插件）。
/// </summary>
public sealed class ScriptInjectionMiddleware
{
    private const string Marker = "FireFly/web/firefly.js";

    // defer 脚本按出现顺序执行：词典、公共工具（firefly.js）、抽屉、榜单、详情页（最后一个负责启动）
    private static readonly string[] Scripts = ["firefly-i18n.js", "firefly.js", "firefly-drawer.js", "firefly-hot.js", "firefly-detail.js"];

    private static readonly string Payload =
        $"<link rel=\"stylesheet\" href=\"/FireFly/web/firefly.css?v={Version}\">" +
        string.Concat(Scripts.Select(s => $"<script defer src=\"/FireFly/web/{s}?v={Version}\"></script>"));

    private readonly RequestDelegate _next;

    public ScriptInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    // 带上程序集的构建 ID（每次编译都会变）：版本号不变时重装插件，浏览器也会取新脚本而不是用缓存的旧版
    private static string Version =>
        (typeof(ScriptInjectionMiddleware).Assembly.GetName().Version?.ToString() ?? "0") + "-" +
        typeof(ScriptInjectionMiddleware).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldInspect(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 去掉压缩协商，才能直接改写正文
        context.Request.Headers.Remove("Accept-Encoding");
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var contentType = context.Response.ContentType ?? string.Empty;
        if (buffer.Length == 0 || !contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (!html.Contains(Marker, StringComparison.Ordinal))
        {
            var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = index >= 0 ? html.Insert(index, Payload) : html + Payload;
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.Headers.Remove("Content-Encoding");
        context.Response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool ShouldInspect(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value ?? "/";
        return path is "/" or "/web" or "/web/" || path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
