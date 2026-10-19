using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FireFly.Core.Storage;

namespace FireFly.Core.Licensing;

public sealed record LicensePayload
{
    [JsonPropertyName("sub")]
    public string Subject { get; init; } = string.Empty;

    /// <summary>monthly / yearly / lifetime</summary>
    [JsonPropertyName("plan")]
    public string Plan { get; init; } = string.Empty;

    /// <summary>到期时间（Unix 秒）；lifetime 为 0。</summary>
    [JsonPropertyName("exp")]
    public long Expires { get; init; }

    [JsonPropertyName("iat")]
    public long IssuedAt { get; init; }
}

public sealed record LicenseInfo(bool IsVip, string Status, LicensePayload? Payload);

/// <summary>
/// 离线授权码：FF1.&lt;载荷 base64url&gt;.&lt;签名 base64url&gt;，ECDSA P-256 + SHA-256。
/// 插件内置公钥验签，不联网；私钥只在作者电脑上，用 tools/FireFly.LicenseTool 签发。
/// </summary>
public sealed class LicenseVerifier
{
    public const string Prefix = "FF1.";
    private readonly byte[] _publicKey;
    private readonly Func<DateTimeOffset> _now;

    public LicenseVerifier(string publicKeyBase64, Func<DateTimeOffset>? now = null)
    {
        _publicKey = string.IsNullOrWhiteSpace(publicKeyBase64) ? Array.Empty<byte>() : Convert.FromBase64String(publicKeyBase64);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public LicenseInfo Verify(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new LicenseInfo(false, "未填写授权码", null);
        }

        if (_publicKey.Length == 0)
        {
            return new LicenseInfo(false, "此版本未内置授权公钥", null);
        }

        var parts = code.Trim().Split('.');
        if (parts.Length != 3 || parts[0] + "." != Prefix)
        {
            return new LicenseInfo(false, "授权码格式不正确", null);
        }

        byte[] body, signature;
        try
        {
            body = Base64Url.Decode(parts[1]);
            signature = Base64Url.Decode(parts[2]);
        }
        catch (FormatException)
        {
            return new LicenseInfo(false, "授权码格式不正确", null);
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(_publicKey, out _);
        if (!ecdsa.VerifyData(body, signature, HashAlgorithmName.SHA256))
        {
            return new LicenseInfo(false, "授权码无效", null);
        }

        var payload = JsonSerializer.Deserialize<LicensePayload>(body);
        if (payload is null)
        {
            return new LicenseInfo(false, "授权码无效", null);
        }

        if (payload.Expires > 0 && DateTimeOffset.FromUnixTimeSeconds(payload.Expires) < _now())
        {
            return new LicenseInfo(false, "授权已到期", payload);
        }

        var until = payload.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(payload.Expires).ToLocalTime().ToString("yyyy-MM-dd") : "永久";
        return new LicenseInfo(true, $"VIP（{until}）", payload);
    }

    /// <summary>签发授权码（只在 LicenseTool 里用私钥调用）。</summary>
    public static string Issue(LicensePayload payload, string privateKeyPkcs8Base64)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        var signature = ecdsa.SignData(body, HashAlgorithmName.SHA256);
        return Prefix + Base64Url.Encode(body) + "." + Base64Url.Encode(signature);
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException() };
        return Convert.FromBase64String(s);
    }
}

public sealed class QuotaState
{
    /// <summary>月份（yyyy-MM）→ 本月已经计过数的片子（MediaQuery.QuotaKey）。</summary>
    public Dictionary<string, List<string>> Months { get; set; } = new();
}

public sealed record QuotaStatus(bool IsVip, string LicenseStatus, int Used, int FreeLimit)
{
    public int Remaining => IsVip ? int.MaxValue : Math.Max(0, FreeLimit - Used);
}

/// <summary>
/// 本地月度免费额度：一部电影或一整季剧集算 1 部，同一部本月重复下载不重复计数，每月 1 日重置。
/// 初期接受本地计数可能被绕过的风险（见方案 v3 第三节）。
/// </summary>
public sealed class QuotaLedger
{
    private readonly JsonStore<QuotaState> _store;
    private readonly LicenseVerifier _verifier;
    private readonly Func<string?> _licenseCode;
    private readonly int _freeLimit;
    private readonly Func<DateTimeOffset> _now;

    public QuotaLedger(JsonStore<QuotaState> store, LicenseVerifier verifier, Func<string?> licenseCode, int freeLimit, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _verifier = verifier;
        _licenseCode = licenseCode;
        _freeLimit = freeLimit;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    private string Month => _now().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public QuotaStatus Status()
    {
        var license = _verifier.Verify(_licenseCode());
        var used = _store.Read(s => s.Months.TryGetValue(Month, out var list) ? list.Count : 0);
        return new QuotaStatus(license.IsVip, license.Status, used, _freeLimit);
    }

    /// <summary>这部片现在能不能提交（已经计过数的总是可以）。</summary>
    public bool CanSubmit(string quotaKey)
    {
        var status = Status();
        return status.IsVip
            || _store.Read(s => s.Months.TryGetValue(Month, out var list) && list.Contains(quotaKey))
            || status.Used < _freeLimit;
    }

    /// <summary>提交成功后记一笔。</summary>
    public void Record(string quotaKey) =>
        _store.Update(s =>
        {
            if (!s.Months.TryGetValue(Month, out var list))
            {
                s.Months[Month] = list = new List<string>();
            }

            if (!list.Contains(quotaKey))
            {
                list.Add(quotaKey);
            }

            return 0;
        });
}
