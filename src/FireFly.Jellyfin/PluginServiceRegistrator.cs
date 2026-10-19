using FireFly.Jellyfin.Injection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FireFly.Jellyfin;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("FireFly 插件尚未加载");
            return new FireFlyRuntime(new JellyfinMediaHost(sp.GetRequiredService<ILibraryManager>()), plugin.DataFolderPath);
        });
        serviceCollection.AddTransient<IStartupFilter, InjectionStartupFilter>();
        serviceCollection.AddHostedService<TaskMonitorService>();
    }
}
