using FireFly.Core.Library;
using FireFly.Core.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace FireFly.Jellyfin;

/// <summary>IMediaHost 的 Jellyfin 实现：媒体库目录、刷新、"已在库"判断。</summary>
public sealed class JellyfinMediaHost : IMediaHost
{
    private readonly ILibraryManager _library;

    public JellyfinMediaHost(ILibraryManager library)
    {
        _library = library;
    }

    public LibraryPaths GetLibraryPaths()
    {
        var config = Plugin.Instance?.Configuration;
        var movie = string.IsNullOrWhiteSpace(config?.MovieLibraryPath) ? FirstLocation(CollectionTypeOptions.movies) : FireFlyRuntime.ExpandHome(config!.MovieLibraryPath.Trim());
        var series = string.IsNullOrWhiteSpace(config?.SeriesLibraryPath) ? FirstLocation(CollectionTypeOptions.tvshows) : FireFlyRuntime.ExpandHome(config!.SeriesLibraryPath.Trim());
        return new LibraryPaths(movie, series);
    }

    /// <summary>所有媒体库目录，配置页下拉用。</summary>
    public IReadOnlyList<(string Library, string? Type, string Path)> AllLocations() =>
        _library.GetVirtualFolders()
            .SelectMany(f => (f.Locations ?? Array.Empty<string>()).Select(p => (f.Name, f.CollectionType?.ToString(), p)))
            .ToList();

    public void RequestRefresh() => _library.QueueLibraryScan();

    public string? FindItemId(MediaQuery query)
    {
        var kind = query.Kind == MediaKind.Movie ? BaseItemKind.Movie : BaseItemKind.Series;
        if (!string.IsNullOrEmpty(query.TmdbId))
        {
            var byId = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = query.TmdbId },
                Recursive = true,
                Limit = 1,
            });
            if (byId.Count > 0)
            {
                return byId[0].Id.ToString("N");
            }
        }

        return _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Name = query.Title,
            Years = query.Year is int y ? new[] { y } : Array.Empty<int>(),
            Recursive = true,
            Limit = 1,
        }).FirstOrDefault()?.Id.ToString("N");
    }

    private string? FirstLocation(CollectionTypeOptions type) =>
        _library.GetVirtualFolders()
            .Where(f => f.CollectionType == type)
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .FirstOrDefault();
}
