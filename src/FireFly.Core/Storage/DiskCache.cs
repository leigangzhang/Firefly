using System.Security.Cryptography;
using System.Text;

namespace FireFly.Core.Storage;

/// <summary>
/// 落盘缓存：一个键一个文件，文件名是键的 SHA-256。文件修改时间 = 缓存时间，访问时间 = 最近使用时间
/// （读写时显式设置，不依赖文件系统是否记录访问时间）。超过容量上限时先删最久没用的。
/// 多个请求同时写同一个键时后写的覆盖先写的，内容相同，无所谓。
/// </summary>
public sealed class DiskCache
{
    private readonly string _dir;
    private readonly string _extension;
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _now;
    private int _writesSincePrune;

    public DiskCache(string dir, string extension, long maxBytes, Func<DateTimeOffset>? now = null)
    {
        _dir = dir;
        _extension = extension;
        _maxBytes = maxBytes;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(dir);
    }

    /// <summary>每写这么多次检查一次容量。</summary>
    public const int PruneEvery = 50;

    /// <summary>取缓存；maxAge 为空表示不过期。返回内容和缓存了多久。</summary>
    public (byte[] Data, TimeSpan Age)? Get(string key, TimeSpan? maxAge = null)
    {
        var path = PathFor(key);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var age = _now() - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (maxAge is { } max && age > max)
            {
                return null;
            }

            var data = File.ReadAllBytes(path);
            File.SetLastAccessTimeUtc(path, _now().UtcDateTime);
            return (data, age);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Set(string key, byte[] data)
    {
        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            var now = _now().UtcDateTime;
            File.SetLastWriteTimeUtc(temp, now);
            File.SetLastAccessTimeUtc(temp, now);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temp);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            return;
        }

        if (Interlocked.Increment(ref _writesSincePrune) >= PruneEvery)
        {
            Interlocked.Exchange(ref _writesSincePrune, 0);
            Prune();
        }
    }

    /// <summary>当前占用的字节数。</summary>
    public long SizeBytes() => Files().Sum(f => f.Length);

    /// <summary>超过容量上限时，从最久没用的文件开始删，删到上限的 80%。</summary>
    public void Prune()
    {
        var files = Files().ToList();
        var total = files.Sum(f => f.Length);
        if (total <= _maxBytes)
        {
            return;
        }

        var target = _maxBytes * 8 / 10;
        foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
        {
            if (total <= target)
            {
                break;
            }

            total -= file.Length;
            TryDelete(file.FullName);
        }
    }

    public void Clear()
    {
        foreach (var file in Files())
        {
            TryDelete(file.FullName);
        }
    }

    private IEnumerable<FileInfo> Files()
    {
        try
        {
            return new DirectoryInfo(_dir).EnumerateFiles("*" + _extension).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<FileInfo>();
        }
    }

    private string PathFor(string key) =>
        Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + _extension);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
