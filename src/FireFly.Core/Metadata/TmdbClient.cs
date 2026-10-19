using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FireFly.Core.Models;

namespace FireFly.Core.Metadata;

public sealed record TmdbItem(
    string TmdbId,
    MediaKind Kind,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterUrl,
    double? Rating,
    int? SeasonCount);

/// <summary>TMDb 只用两个能力：本周热门、单条详情。API Key 由用户自己填，只存在本机配置里。</summary>
public sealed class TmdbClient
{
    private const string Base = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w342";
    private readonly HttpClient _http;
    private readonly Func<(string ApiKey, string Language)> _settings;

    public TmdbClient(HttpClient http, Func<(string ApiKey, string Language)> settings)
    {
        _http = http;
        _settings = settings;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_settings().ApiKey);

    public async Task<IReadOnlyList<TmdbItem>> TrendingAsync(MediaKind kind, CancellationToken ct)
    {
        var page = await GetAsync<TmdbPage>($"/trending/{Segment(kind)}/week", ct).ConfigureAwait(false);
        return (page?.Results ?? new()).Select(r => r.ToItem(kind)).ToList();
    }

    public async Task<TmdbItem?> DetailAsync(MediaKind kind, string tmdbId, CancellationToken ct)
    {
        var r = await GetAsync<TmdbResult>($"/{Segment(kind)}/{Uri.EscapeDataString(tmdbId)}", ct).ConfigureAwait(false);
        return r?.ToItem(kind);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        var (key, language) = _settings();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("未配置 TMDb API Key");
        }

        var url = $"{Base}{path}?api_key={Uri.EscapeDataString(key)}&language={Uri.EscapeDataString(language)}";
        return await _http.GetFromJsonAsync<T>(url, ct).ConfigureAwait(false);
    }

    private static string Segment(MediaKind kind) => kind == MediaKind.Movie ? "movie" : "tv";

    internal sealed class TmdbPage
    {
        public List<TmdbResult>? Results { get; set; }
    }

    internal sealed class TmdbResult
    {
        public long Id { get; set; }

        public string? Title { get; set; }

        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("number_of_seasons")]
        public int? NumberOfSeasons { get; set; }

        public TmdbItem ToItem(MediaKind kind)
        {
            var title = (kind == MediaKind.Movie ? Title : Name) ?? Title ?? Name ?? string.Empty;
            var original = kind == MediaKind.Movie ? OriginalTitle : OriginalName;
            var date = kind == MediaKind.Movie ? ReleaseDate : FirstAirDate;
            int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
            return new TmdbItem(
                Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kind,
                title,
                string.Equals(original, title, StringComparison.Ordinal) ? null : original,
                year,
                Overview,
                string.IsNullOrEmpty(PosterPath) ? null : ImageBase + PosterPath,
                VoteAverage,
                NumberOfSeasons);
        }
    }
}
