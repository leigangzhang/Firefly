using FireFly.Core.Models;

namespace FireFly.Core.Storage;

public sealed class FavoriteItem
{
    public MediaKind Kind { get; set; }

    public string TmdbId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? OriginalTitle { get; set; }

    public int? Year { get; set; }

    public string? PosterUrl { get; set; }

    public double? Rating { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

public sealed class FavoriteList
{
    public List<FavoriteItem> Items { get; set; } = new();
}

/// <summary>收藏夹：按 TMDb 类型 + ID 去重，最新收藏排最前。</summary>
public sealed class Favorites
{
    private readonly JsonStore<FavoriteList> _store;
    private readonly Func<DateTimeOffset> _now;

    public Favorites(JsonStore<FavoriteList> store, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<FavoriteItem> List() => _store.Read(l => l.Items.ToList());

    public bool Contains(MediaKind kind, string tmdbId) =>
        _store.Read(l => l.Items.Any(i => Same(i, kind, tmdbId)));

    public void Add(FavoriteItem item)
    {
        if (string.IsNullOrWhiteSpace(item.TmdbId) || string.IsNullOrWhiteSpace(item.Title))
        {
            throw new InvalidOperationException("收藏缺少 TMDb ID 或片名");
        }

        _store.Update(l =>
        {
            l.Items.RemoveAll(i => Same(i, item.Kind, item.TmdbId));
            item.AddedAt = _now();
            l.Items.Insert(0, item);
            return 0;
        });
    }

    public bool Remove(MediaKind kind, string tmdbId) =>
        _store.Update(l => l.Items.RemoveAll(i => Same(i, kind, tmdbId)) > 0);

    private static bool Same(FavoriteItem i, MediaKind kind, string tmdbId) =>
        i.Kind == kind && string.Equals(i.TmdbId, tmdbId, StringComparison.Ordinal);
}
