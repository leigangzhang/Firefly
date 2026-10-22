namespace FireFly.Core.Sources;

/// <summary>info-hash 的统一处理：从磁力链接里取出、统一成 40 位大写十六进制。</summary>
internal static class InfoHash
{
    /// <summary>从磁力链接里取 btih（40 位十六进制，或 32 位 Base32）。</summary>
    public static string? FromMagnet(string? magnet)
    {
        if (string.IsNullOrEmpty(magnet))
        {
            return null;
        }

        var i = magnet.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        var start = i + "urn:btih:".Length;
        var end = magnet.IndexOf('&', start);
        return end < 0 ? magnet[start..] : magnet[start..end];
    }

    /// <summary>统一成 40 位大写十六进制；Base32 的转换过来；格式不对返回 null。</summary>
    public static string? Normalize(string? hash)
    {
        hash = hash?.Trim();
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
        {
            return hash.ToUpperInvariant();
        }

        if (hash.Length == 32)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new byte[20];
            int buffer = 0, bits = 0, index = 0;
            foreach (var c in hash.ToUpperInvariant())
            {
                var v = alphabet.IndexOf(c);
                if (v < 0)
                {
                    return null;
                }

                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes[index++] = (byte)((buffer >> bits) & 0xFF);
                }
            }

            return Convert.ToHexString(bytes);
        }

        return null;
    }
}
