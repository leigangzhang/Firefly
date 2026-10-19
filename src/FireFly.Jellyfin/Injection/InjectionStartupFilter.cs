using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace FireFly.Jellyfin.Injection;

/// <summary>把注入中间件放到管道最前面。</summary>
public sealed class InjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            next(app);
        };
}
