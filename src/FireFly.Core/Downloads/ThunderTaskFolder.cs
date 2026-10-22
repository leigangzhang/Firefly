using System.Text;
using FireFly.Core.Models;

namespace FireFly.Core.Downloads;

/// <summary>
/// 迅雷（macOS）给每个 BT 任务建的文件夹里有两个隐藏文件：".{种子哈希}.js" 和 ".magent_*.torrent"。
/// 用前者认出任务文件夹，从后者读出完整文件清单，不依赖资源站抓到的名字和大小（网页站点经常抓不准）。
/// </summary>
internal static class ThunderTaskFolder
{
    private const int MaxTorrentBytes = 16 << 20;

    /// <summary>下载目录下第一层里带 ".{哈希}.js" 标记的文件夹；找不到返回 null。</summary>
    public static string? Find(string downloadDir, string infoHash)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
        {
            return null;
        }

        var marker = "." + infoHash.Trim() + ".js";
        try
        {
            return Directory.EnumerateDirectories(downloadDir)
                .FirstOrDefault(d => Directory.EnumerateFiles(d, ".*.js")
                    .Any(f => string.Equals(Path.GetFileName(f), marker, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读任务文件夹里迅雷保存的种子，返回文件清单；没有种子或解析失败返回空。</summary>
    public static IReadOnlyList<ReleaseFile> ReadTorrentFiles(string folder)
    {
        try
        {
            var torrent = Directory.EnumerateFiles(folder, "*.torrent")
                .Select(p => new FileInfo(p))
                .Where(f => f.Length <= MaxTorrentBytes)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return torrent is null ? Array.Empty<ReleaseFile>() : ParseFiles(File.ReadAllBytes(torrent.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return Array.Empty<ReleaseFile>();
        }
    }

    /// <summary>从种子的 info 字典里取文件清单：多文件种子读 files，单文件种子读 name + length。</summary>
    internal static IReadOnlyList<ReleaseFile> ParseFiles(byte[] data)
    {
        var pos = 0;
        if (Bencode.Read(data, ref pos) is not Dictionary<string, object> root
            || !root.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
        {
            throw new FormatException("种子里没有 info");
        }

        var name = Text(info, "name.utf-8") ?? Text(info, "name") ?? string.Empty;
        if (info.TryGetValue("files", out var filesObj) && filesObj is List<object> files)
        {
            return files.OfType<Dictionary<string, object>>()
                .Select(f => new ReleaseFile(
                    string.Join('/', new[] { name }.Concat(Segments(f, "path.utf-8") ?? Segments(f, "path") ?? new List<string>())),
                    f.TryGetValue("length", out var l) && l is long len ? len : 0))
                .ToList();
        }

        return info.TryGetValue("length", out var single) && single is long size
            ? new[] { new ReleaseFile(name, size) }
            : Array.Empty<ReleaseFile>();
    }

    private static string? Text(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is byte[] b ? Encoding.UTF8.GetString(b) : null;

    private static List<string>? Segments(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object> list
            ? list.OfType<byte[]>().Select(b => Encoding.UTF8.GetString(b)).ToList()
            : null;

    /// <summary>最小的 bencode 读取：整数 → long，字符串 → byte[]，列表 → List，字典 → Dictionary（键按 UTF-8 解码）。</summary>
    private static class Bencode
    {
        private const int MaxDepth = 32;

        public static object Read(byte[] s, ref int i, int depth = 0)
        {
            if (depth > MaxDepth || i >= s.Length)
            {
                throw new FormatException("种子格式不对");
            }

            switch ((char)s[i])
            {
                case 'i':
                {
                    var end = IndexOf(s, (byte)'e', i);
                    var value = long.Parse(Encoding.ASCII.GetString(s, i + 1, end - i - 1), System.Globalization.CultureInfo.InvariantCulture);
                    i = end + 1;
                    return value;
                }

                case 'l':
                {
                    i++;
                    var list = new List<object>();
                    while (i < s.Length && s[i] != 'e')
                    {
                        list.Add(Read(s, ref i, depth + 1));
                    }

                    i++;
                    return list;
                }

                case 'd':
                {
                    i++;
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    while (i < s.Length && s[i] != 'e')
                    {
                        var key = Encoding.UTF8.GetString((byte[])ReadBytes(s, ref i));
                        dict[key] = Read(s, ref i, depth + 1);
                    }

                    i++;
                    return dict;
                }

                default:
                    return ReadBytes(s, ref i);
            }
        }

        private static object ReadBytes(byte[] s, ref int i)
        {
            var colon = IndexOf(s, (byte)':', i);
            var length = int.Parse(Encoding.ASCII.GetString(s, i, colon - i), System.Globalization.CultureInfo.InvariantCulture);
            if (length < 0 || colon + 1 + length > s.Length)
            {
                throw new FormatException("种子格式不对");
            }

            var bytes = s.AsSpan(colon + 1, length).ToArray();
            i = colon + 1 + length;
            return bytes;
        }

        private static int IndexOf(byte[] s, byte b, int from)
        {
            var at = Array.IndexOf(s, b, from);
            return at < 0 ? throw new FormatException("种子格式不对") : at;
        }
    }
}
