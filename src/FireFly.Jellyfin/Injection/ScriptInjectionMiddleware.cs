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
    private static readonly string Payload =
        $"<link rel=\"stylesheet\" href=\"/FireFly/web/firefly.css?v={Version}\"><script defer src=\"/{Marker}?v={Version}\"></script>";

    private readonly RequestDelegate _next;

    public ScriptInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private static string Version => typeof(ScriptInjectionMiddleware).Assembly.GetName().Version?.ToString() ?? "0";

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
