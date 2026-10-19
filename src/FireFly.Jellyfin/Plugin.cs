using FireFly.Jellyfin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace FireFly.Jellyfin;

/// <summary>萤火虫 FireFly：在 Jellyfin 里发现资源、交给迅雷下载、自动入库。</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "FireFly";

    public override string Description => "萤火虫：发现资源，一键交给迅雷下载，下载完成自动入库。";

    public override Guid Id => Guid.Parse("4f1d2c8e-9b7a-4e35-8c61-f1ae0f17f1f1");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = "FireFly.Jellyfin.Configuration.configPage.html",
        },
    };
}
