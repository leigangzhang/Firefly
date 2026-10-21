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

    /// <summary>TMDb 接口地址，配置页从规则里的候选地址中选，也可以自定义；空 = 第一个候选（api.tmdb.org）。</summary>
    public string TmdbApiBase { get; set; } = string.Empty;

    /// <summary>TMDb 图片地址，同上；空 = 第一个候选（image.tmdb.org）。图片由插件取回并缓存。</summary>
    public string TmdbImageBase { get; set; } = string.Empty;

    /// <summary>详情页是否显示 YouTube 预告片。大陆网络不翻墙时 YouTube 打不开，默认关闭。</summary>
    public bool ShowTrailers { get; set; }

    /// <summary>发布者关键词，一行一个；空 = 用规则文件默认值。</summary>
    public string PublisherKeywords { get; set; } = string.Empty;
}
