using System.Text.RegularExpressions;
using FireFly.Core.Models;

namespace FireFly.Core.Parsing;

/// <summary>从种子标题里识别展示字段。规则宁缺毋滥：认不出就留空，不猜。</summary>
public static partial class ReleaseParser
{
    public static ReleaseTraits Parse(string name)
    {
        var text = name ?? string.Empty;
        return new ReleaseTraits
        {
            Resolution = ParseResolution(text),
            Codec = Match(text, CodecRegex(), m => m.Groups["h265"].Success ? "H.265" : m.Groups["h264"].Success ? "H.264" : "AV1"),
            Hdr = ParseHdr(text),
            MediaSource = CamRegex().IsMatch(text) ? CamSource : Match(text, SourceRegex(), m => m.Groups["remux"].Success ? "REMUX" : m.Groups["bluray"].Success ? "BluRay" : m.Groups["web"].Success ? "WEB-DL" : "HDTV"),
            Audio = ParseAudio(text),
            Subtitles = ParseSubtitles(text),
            Year = ParseYear(text),
            Season = ParseSeason(text),
            Publisher = ParsePublisher(text),
            ForeignOnly = ForeignRegex().IsMatch(text) && !ChineseOrEnglishRegex().IsMatch(text),
        };
    }

    /// <summary>影院偷录的枪版（CAM / TS / TC），画质差，排序时放到最后。</summary>
    public const string CamSource = "枪版";

    /// <summary>成人内容：同名恶搞片常混进搜索结果，直接过滤。</summary>
    public static bool IsAdult(string text) => AdultRegex().IsMatch(text);

    public static int ParseResolution(string text)
    {
        if (Res2160Regex().IsMatch(text))
        {
            return 2160;
        }

        var m = ResRegex().Match(text);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    public static int? ParseYear(string text)
    {
        foreach (Match m in YearRegex().Matches(text))
        {
            return int.Parse(m.Value);
        }

        return null;
    }

    /// <summary>认 S01 / S01E02 / Season 1 / 第1季 / 第一季；没标季号返回 null。</summary>
    public static int? ParseSeason(string text)
    {
        var m = SeasonSRegex().Match(text);
        if (m.Success)
        {
            return int.Parse(m.Groups[1].Value);
        }

        m = SeasonWordRegex().Match(text);
        if (m.Success)
        {
            return int.Parse(m.Groups[1].Value);
        }

        m = SeasonChineseRegex().Match(text);
        if (m.Success)
        {
            var raw = m.Groups[1].Value;
            return int.TryParse(raw, out var n) ? n : ChineseNumber(raw);
        }

        return null;
    }

    /// <summary>从单集文件名里认集号：S01E05 / E05 / 第5集 / EP05。</summary>
    public static int? ParseEpisode(string fileName)
    {
        var m = EpisodeRegex().Match(fileName);
        if (m.Success)
        {
            return int.Parse(m.Groups["n"].Value);
        }

        m = EpisodeChineseRegex().Match(fileName);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>【高清剧集网发布 www.DDHDTV.com】→ DDHDTV；没有网址时取"发布"前的名字。</summary>
    public static string? ParsePublisher(string text)
    {
        var m = PublisherRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }

        var site = PublisherSiteRegex().Match(m.Value);
        return site.Success ? site.Groups[1].Value.ToUpperInvariant() : m.Groups["name"].Value.Trim();
    }

    private static string? ParseHdr(string text)
    {
        if (DoviRegex().IsMatch(text))
        {
            return HdrRegex().IsMatch(text) ? "杜比视界+HDR" : "杜比视界";
        }

        if (Hdr10PlusRegex().IsMatch(text))
        {
            return "HDR10+";
        }

        return HdrRegex().IsMatch(text) ? "HDR" : null;
    }

    private static string? ParseAudio(string text)
    {
        var parts = new List<string>();
        if (AtmosRegex().IsMatch(text))
        {
            parts.Add("Atmos");
        }

        var m = AudioRegex().Match(text);
        if (m.Success)
        {
            parts.Add(m.Groups["truehd"].Success ? "TrueHD" : m.Groups["dtsx"].Success ? "DTS:X" : m.Groups["dtshd"].Success ? "DTS-HD MA" : m.Groups["dts"].Success ? "DTS" : m.Groups["ddp"].Success ? "DDP" : "AAC");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string? ParseSubtitles(string text)
    {
        foreach (Match m in BracketRegex().Matches(text))
        {
            var inner = m.Groups[1].Value;
            if (inner.Contains("字幕") || inner.Contains("中字"))
            {
                return inner.Trim();
            }
        }

        return SubtitleWordRegex().Match(text) is { Success: true } w ? w.Value : null;
    }

    private static string? Match(string text, Regex regex, Func<Match, string> map)
    {
        var m = regex.Match(text);
        return m.Success ? map(m) : null;
    }

    internal static int? ChineseNumber(string raw)
    {
        const string digits = "零一二三四五六七八九";
        if (raw.Length == 0)
        {
            return null;
        }

        var tenIndex = raw.IndexOf('十');
        if (tenIndex < 0)
        {
            var d = digits.IndexOf(raw[0]);
            return raw.Length == 1 && d > 0 ? d : null;
        }

        var tens = tenIndex == 0 ? 1 : digits.IndexOf(raw[0]);
        var ones = tenIndex == raw.Length - 1 ? 0 : digits.IndexOf(raw[^1]);
        return tens < 0 || ones < 0 ? null : tens * 10 + ones;
    }

    [GeneratedRegex(@"HDCAM|CAMRip|HDTS|TELESYNC|TELECINE|HDTC|枪版|(?<![A-Za-z0-9])(?:CAM|TS|TC)(?=[-. _\]\)][^.]*\S)", RegexOptions.None)]
    private static partial Regex CamRegex();

    [GeneratedRegex(@"(?<![A-Za-z])(?:XXX|Porn|OnlyFans|Brazzers)(?![A-Za-z])|成人(?:短剧|影片|电影|视频)|色情", RegexOptions.IgnoreCase)]
    private static partial Regex AdultRegex();

    [GeneratedRegex(@"(?<![A-Za-z])(?:rus|LostFilm|Ukr|Hurtom|Subtitulado|Latino|Castellano|VOSTFR|TRUEFRENCH|FRENCH|German|iTALiAN|ITA|Esp|Dublado|Legendado|Hindi|Tamil|Telugu)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignRegex();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}|(?<![A-Za-z])(?:ENG|English|CHS|CHT|CHI|Chinese|DUAL)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseOrEnglishRegex();

    [GeneratedRegex(@"(?<![0-9])(2160[pP]|4[kK]|UHD)(?![0-9a-zA-Z])")]
    private static partial Regex Res2160Regex();

    [GeneratedRegex(@"(?<![0-9])(1080|720|480)[pPiI]")]
    private static partial Regex ResRegex();

    [GeneratedRegex(@"(?<![0-9])(19[3-9][0-9]|20[0-4][0-9])(?![0-9pPiI])")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[Ss](\d{1,2})(?=[Ee]\d|[^A-Za-z0-9]|$)")]
    private static partial Regex SeasonSRegex();

    [GeneratedRegex(@"Season[\s._]*(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(@"第\s*([0-9]{1,2}|[一二三四五六七八九十]{1,3})\s*季")]
    private static partial Regex SeasonChineseRegex();

    [GeneratedRegex(@"(?:(?<![A-Za-z0-9])[Ss]\d{1,2}[\s._-]?[Ee][Pp]?|(?<![A-Za-z0-9])[Ee][Pp]?)(?<n>\d{1,3})(?![0-9])")]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"第\s*(\d{1,3})\s*[集话話]")]
    private static partial Regex EpisodeChineseRegex();

    [GeneratedRegex(@"【(?<name>[^】]*?)发布[^】]*】")]
    private static partial Regex PublisherRegex();

    [GeneratedRegex(@"www\.([A-Za-z0-9-]+)\.(?:com|net|org|cc|tv|me)", RegexOptions.IgnoreCase)]
    private static partial Regex PublisherSiteRegex();

    [GeneratedRegex(@"(?<h265>x265|[Hh]\.?265|HEVC)|(?<h264>x264|[Hh]\.?264|AVC)|(?<av1>(?<![A-Za-z])AV1(?![0-9]))", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"(?<remux>REMUX)|(?<bluray>Blu-?ray|BDRip|BluRay)|(?<web>WEB-?DL|WEBRip|(?<![A-Za-z])WEB(?![A-Za-z]))|(?<hdtv>(?<![A-Za-z])HDTV)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"DoVi|Dolby[\s.]?Vision|杜比视界|(?<![A-Za-z])DV(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DoviRegex();

    [GeneratedRegex(@"HDR10\+|HDR10Plus", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10PlusRegex();

    [GeneratedRegex(@"HDR", RegexOptions.IgnoreCase)]
    private static partial Regex HdrRegex();

    [GeneratedRegex(@"Atmos|全景声", RegexOptions.IgnoreCase)]
    private static partial Regex AtmosRegex();

    [GeneratedRegex(@"(?<truehd>TrueHD)|(?<dtsx>DTS[:\-\s]?X(?![A-Za-z]))|(?<dtshd>DTS-?HD[\s.]?MA)|(?<dts>DTS)|(?<ddp>DDP|DD\+|E-?AC-?3)|(?<aac>AAC)", RegexOptions.IgnoreCase)]
    private static partial Regex AudioRegex();

    [GeneratedRegex(@"[\[【]([^\]】]+)[\]】]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"中英双字|中英字幕|简繁英?字幕|中文字幕|中字")]
    private static partial Regex SubtitleWordRegex();
}
