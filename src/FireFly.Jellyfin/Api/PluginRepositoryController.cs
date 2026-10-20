using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FireFly.Jellyfin.Api;

/// <summary>
/// 让 Jellyfin 自托管插件仓库清单与安装包。
/// 手工复制安装时 FireFly 不在任何仓库清单里，Jellyfin 插件详情页会请求
/// /Packages/FireFly 并得到 404，前端随后提示“从存储库获取插件详情时发生错误”。
/// 把本控制器的 manifest.json 注册为本地仓库即可让详情请求命中。
/// </summary>
[ApiController]
[Route("FireFly")]
public sealed class PluginRepositoryController : ControllerBase
{
    private const string PackageFileName = "FireFly.zip";
    private static readonly DateTimeOffset ZipEntryTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Lazy<PluginPackage> Package = new(BuildPackage, LazyThreadSafetyMode.ExecutionAndPublication);

    [HttpGet("manifest.json")]
    [AllowAnonymous]
    public IActionResult Manifest()
    {
        var package = Package.Value;
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var version = new
        {
            version = package.Version,
            changelog = string.Empty,
            targetAbi = package.TargetAbi,
            sourceUrl = $"{baseUrl}/FireFly/package.zip",
            checksum = package.Checksum,
            timestamp = package.Timestamp,
        };

        return Ok(new[]
        {
            new
            {
                guid = package.Guid,
                name = package.Name,
                description = package.Description,
                overview = package.Overview,
                owner = package.Owner,
                category = package.Category,
                versions = new[] { version },
            },
        });
    }

    [HttpGet("package.zip")]
    [AllowAnonymous]
    public IActionResult Download()
    {
        var package = Package.Value;
        return File(package.Bytes, "application/zip", PackageFileName);
    }

    private static PluginPackage BuildPackage()
    {
        var pluginDir = Path.GetDirectoryName(typeof(PluginRepositoryController).Assembly.Location)
            ?? throw new InvalidOperationException("无法定位 FireFly 插件目录");
        var metaPath = Path.Combine(pluginDir, "meta.json");
        using var meta = JsonDocument.Parse(System.IO.File.ReadAllBytes(metaPath));
        var root = meta.RootElement;

        var files = Directory.EnumerateFiles(pluginDir)
            .Where(path => Path.GetExtension(path) is ".dll" or ".json" or ".png")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in files)
            {
                var entry = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                entry.LastWriteTime = ZipEntryTimestamp;
                using var target = entry.Open();
                using var source = System.IO.File.OpenRead(path);
                source.CopyTo(target);
            }
        }

        var bytes = output.ToArray();
        return new PluginPackage(
            bytes,
            Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
            ReadString(root, "guid"),
            ReadString(root, "name"),
            ReadString(root, "description"),
            ReadString(root, "overview"),
            ReadString(root, "owner"),
            ReadString(root, "category"),
            ReadString(root, "version"),
            ReadString(root, "targetAbi"),
            ReadString(root, "timestamp"));
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private sealed record PluginPackage(
        byte[] Bytes,
        string Checksum,
        string Guid,
        string Name,
        string Description,
        string Overview,
        string Owner,
        string Category,
        string Version,
        string TargetAbi,
        string Timestamp);
}
