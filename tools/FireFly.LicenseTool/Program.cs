using System.Security.Cryptography;
using FireFly.Core.Licensing;

// FireFly 授权码工具：只在作者自己的电脑上运行，私钥永远不要放进仓库或插件。
//   keygen                      生成密钥对 → keys/private.key（保密）+ 打印公钥（填进 FireFlyRuntime.LicensePublicKey）
//   issue <买家标识> <monthly|yearly|lifetime> [--key keys/private.key]   签发授权码
//   verify <授权码> <公钥base64>  验证
var cmd = args.FirstOrDefault();
switch (cmd)
{
    case "keygen":
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory("keys");
        var path = Path.Combine("keys", "private.key");
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} 已存在，拒绝覆盖（覆盖会让已售出的授权码全部失效）");
            return 1;
        }

        File.WriteAllText(path, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        Console.WriteLine("私钥已写入 " + path + "（请备份并妥善保管）");
        Console.WriteLine("公钥（填进 src/FireFly.Jellyfin/FireFlyRuntime.cs 的 LicensePublicKey）：");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    case "issue" when args.Length >= 3:
    {
        var keyPath = OptionValue("--key") ?? Path.Combine("keys", "private.key");
        var now = DateTimeOffset.UtcNow;
        var expires = args[2] switch
        {
            "monthly" => now.AddMonths(1).ToUnixTimeSeconds(),
            "yearly" => now.AddYears(1).ToUnixTimeSeconds(),
            "lifetime" => 0,
            _ => throw new ArgumentException("plan 只能是 monthly / yearly / lifetime"),
        };
        var payload = new LicensePayload { Subject = args[1], Plan = args[2], Expires = expires, IssuedAt = now.ToUnixTimeSeconds() };
        Console.WriteLine(LicenseVerifier.Issue(payload, File.ReadAllText(keyPath).Trim()));
        return 0;
    }

    case "verify" when args.Length >= 3:
    {
        var info = new LicenseVerifier(args[2]).Verify(args[1]);
        Console.WriteLine($"{(info.IsVip ? "有效" : "无效")}：{info.Status}  {info.Payload}");
        return info.IsVip ? 0 : 2;
    }

    default:
        Console.WriteLine("用法：keygen | issue <买家标识> <monthly|yearly|lifetime> [--key 路径] | verify <授权码> <公钥>");
        return 1;
}

string? OptionValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
