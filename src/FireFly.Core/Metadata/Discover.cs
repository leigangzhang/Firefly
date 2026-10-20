using System.Globalization;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Metadata;

/// <summary>榜单页的一次筛选。空字符串表示"全部"。</summary>
public sealed record DiscoverSelection(
    string? Category = null,
    string? Genre = null,
    string? Region = null,
    string? Time = null,
    string? Company = null,
    string? Sort = null,
    string? Rating = null,
    int Page = 1);

public sealed record FilterRow(string Key, string Label, IReadOnlyList<FilterOption> Options);

public sealed record DiscoverPlan(DiscoverCategory Category, string Path, IReadOnlyDictionary<string, string> Parameters);

/// <summary>把榜单页的筛选项翻译成 TMDb /discover 参数。纯函数，便于测试。</summary>
public static class DiscoverPlanner
{
    public static readonly IReadOnlyList<FilterOption> Sorts = new[]
    {
        new FilterOption("hot", "最热"),
        new FilterOption("new", "最新"),
        new FilterOption("top", "高分"),
        new FilterOption("votes", "最多评价"),
    };

    public static DiscoverCategory Category(DiscoverRule rule, string? key) =>
        rule.Categories.FirstOrDefault(c => c.Key == key) ?? rule.Categories.FirstOrDefault()
        ?? new DiscoverCategory { Key = "movie", Label = "电影", Segment = "movie" };

    /// <summary>"时间"选项：最近 7 年逐年，之后按十年，最后"更早"。</summary>
    public static IReadOnlyList<FilterOption> TimeOptions(int currentYear)
    {
        var list = new List<FilterOption>();
        for (var y = currentYear; y > currentYear - 7; y--)
        {
            list.Add(new FilterOption(y.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)));
        }

        var end = currentYear - 7;
        for (var i = 0; i < 3; i++)
        {
            var start = end - 9;
            list.Add(new FilterOption($"{start}-{end}", $"{start}-{end}"));
            end = start - 1;
        }

        list.Add(new FilterOption($"-{end}", "更早"));
        return list;
    }

    /// <summary>筛选行：分类、类型、地区、时间、出品、推荐、分级（剧集没有分级，TMDb 不支持按分级筛剧集）。</summary>
    public static IReadOnlyList<FilterRow> Rows(DiscoverRule rule, DiscoverCategory category, IReadOnlyList<FilterOption> genres, int currentYear)
    {
        var fixedGenres = Split(category.WithGenres).Concat(Split(category.WithoutGenres)).ToHashSet();
        var rows = new List<FilterRow>
        {
            new("category", "分类", rule.Categories.Select(c => new FilterOption(c.Key, c.Label)).ToList()),
            new("genre", "类型", genres.Where(g => !fixedGenres.Contains(g.Value)).ToList()),
            new("region", "地区", rule.Regions),
            new("time", "时间", TimeOptions(currentYear)),
            new("company", "出品", category.Kind == MediaKind.Movie ? rule.Companies : rule.Networks),
            new("sort", "推荐", Sorts),
        };
        if (category.Kind == MediaKind.Movie && rule.Certifications.Count > 0)
        {
            rows.Add(new FilterRow("rating", "分级", rule.Certifications));
        }

        return rows;
    }

    public static DiscoverPlan Plan(DiscoverRule rule, DiscoverSelection s, DateOnly today)
    {
        var category = Category(rule, s.Category);
        var movie = category.Kind == MediaKind.Movie;
        var dateField = movie ? "primary_release_date" : "first_air_date";
        var p = new Dictionary<string, string>
        {
            ["include_adult"] = "false",
            ["page"] = Math.Clamp(s.Page, 1, 500).ToString(CultureInfo.InvariantCulture),
        };

        var genres = new[] { category.WithGenres, Blank(s.Genre) }.Where(g => !string.IsNullOrEmpty(g)).ToList();
        if (genres.Count > 0)
        {
            p["with_genres"] = string.Join(',', genres);
        }

        if (!string.IsNullOrEmpty(category.WithoutGenres))
        {
            p["without_genres"] = category.WithoutGenres;
        }

        if (Blank(s.Region) is { } region)
        {
            p["with_origin_country"] = region;
        }

        if (Blank(s.Company) is { } company)
        {
            p[movie ? "with_companies" : "with_networks"] = company;
        }

        if (movie && Blank(s.Rating) is { } rating)
        {
            p["certification_country"] = rule.CertificationCountry;
            p["certification"] = rating;
        }

        var time = ParseTime(s.Time);
        if (time.From is int from)
        {
            p[dateField + ".gte"] = $"{from:0000}-01-01";
        }

        if (time.To is int to)
        {
            p[dateField + ".lte"] = $"{to:0000}-12-31";
        }

        var sort = Blank(s.Sort);
        // TMDb 的 TV popularity 长期被长寿剧占据；剧集类“最热”只看最近两年，
        // 避免默认榜单变成十几年前开播的老剧。用户显式选了时间范围时不覆盖。
        if (!movie && (sort is null or "hot") && time.From is null && time.To is null)
        {
            p["first_air_date.gte"] = $"{today.Year - 2:0000}-01-01";
            p["first_air_date.lte"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        switch (sort)
        {
            case "new":
                p["sort_by"] = dateField + ".desc";
                // 未上映的条目没有评分和资源，最新只看已上映
                var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!p.TryGetValue(dateField + ".lte", out var lte) || string.CompareOrdinal(lte, todayText) > 0)
                {
                    p[dateField + ".lte"] = todayText;
                }

                p["vote_count.gte"] = "5";
                break;
            case "top":
                p["sort_by"] = "vote_average.desc";
                p["vote_count.gte"] = movie ? "300" : "150";
                break;
            case "votes":
                p["sort_by"] = "vote_count.desc";
                break;
            default:
                p["sort_by"] = "popularity.desc";
                break;
        }

        return new DiscoverPlan(category, "/discover/" + category.Segment, p);
    }

    /// <summary>"2025" → (2025, 2025)；"2010-2019" → (2010, 2019)；"-1989" → (null, 1989)。</summary>
    internal static (int? From, int? To) ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value.Split('-');
        int? Year(string t) => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1800 and < 3000 ? y : null;
        return parts.Length switch
        {
            1 => (Year(parts[0]), Year(parts[0])),
            2 => (Year(parts[0]), Year(parts[1])),
            _ => (null, null),
        };
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static IEnumerable<string> Split(string? genres) =>
        (genres ?? string.Empty).Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
