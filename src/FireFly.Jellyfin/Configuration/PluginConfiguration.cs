using MediaBrowser.Model.Plugins;

namespace FireFly.Jellyfin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>迅雷下载目录：需要与迅雷设置里的默认下载目录一致（M1）。空 = ~/Downloads/FireFly。</summary>
    public string ThunderDownloadDir { get; set; } = string.Empty;

    /// <summary>空 = 自动取 Jellyfin 第一个"电影"类型媒体库的第一个目录。</summary>
    public string MovieLibraryPath { get; set; } = string.Empty;

    /// <summary>空 = 自动取 Jellyfin 第一个"节目"类型媒体库的第一个目录。</summary>
    public string SeriesLibraryPath { get; set; } = string.Empty;

    public string TmdbApiKey { get; set; } = string.Empty;

    public string TmdbLanguage { get; set; } = "zh-CN";

    /// <summary>发布者关键词，一行一个；空 = 用规则文件默认值。</summary>
    public string PublisherKeywords { get; set; } = string.Empty;
}
