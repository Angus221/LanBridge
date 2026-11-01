using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanBridge.Core;

public static class CertFingerprints
{
    public static string Sha256(X509Certificate2 cert) => Hashing.ToHex(SHA256.HashData(cert.RawData));

    public static string Group(string hex)
    {
        var h = hex.Trim().ToLowerInvariant();
        var parts = new List<string>();
        for (var i = 0; i < h.Length; i += 4)
            parts.Add(h.Substring(i, Math.Min(4, h.Length - i)));
        return string.Join(' ', parts);
    }
}

public static class CertificateFactory
{
    public static X509Certificate2 Create(string deviceId)
    {
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN=LanBridge-{deviceId}");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var oids = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("lanbridge.local");
        san.AddDnsName("id-" + deviceId);
        request.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var issued = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        var pfx = issued.Export(X509ContentType.Pfx, "lanbridge");
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;
        return new X509Certificate2(pfx, "lanbridge", flags);
    }
}

public static class RequestAuth
{
    public const long WindowMs = 300_000;

    public static string Canonical(long timestampMs, string nonce, string method, string path, string? contentSha256)
        => string.Join('\n',
            timestampMs.ToString(CultureInfo.InvariantCulture),
            nonce,
            method.ToUpperInvariant(),
            path,
            contentSha256 ?? "");

    public static byte[] Sign(ECDsa key, string canonical)
        => key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool Verify(ECDsa key, string canonical, byte[] signature)
    {
        try
        {
            return key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool TimestampFresh(long timestampMs, long nowMs, long windowMs = WindowMs)
        => timestampMs >= nowMs - windowMs && timestampMs <= nowMs + windowMs;

    public static bool NonceFormatOk(string? nonce)
    {
        if (nonce is null || nonce.Length is < 16 or > 64) return false;
        foreach (var c in nonce)
            if (c is < '0' or > '9' and < 'a' or > 'f') return false;
        return true;
    }

    public static bool TryImportP256Public(byte[] spki, out ECDsa? key)
    {
        key = null;
        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            var parameters = ecdsa.ExportParameters(false);
            if (parameters.Q.X is not { Length: 32 } || parameters.Q.Y is not { Length: 32 })
            {
                ecdsa.Dispose();
                return false;
            }
            key = ecdsa;
            return true;
        }
        catch (CryptographicException)
        {
            key?.Dispose();
            key = null;
            return false;
        }
    }
}

public enum PinCheckResult { Match, Mismatch, Locked, Closed }

public sealed class PairingWindow
{
    public const int PinLength = 6;
    public const int MaxFailures = 5;

    public string Pin { get; }
    public DateTimeOffset ExpiresAt { get; }
    public int Failures { get; private set; }
    public bool Closed { get; private set; }

    public PairingWindow(string pin, DateTimeOffset expiresAt)
    {
        if (pin.Length != PinLength || !pin.All(char.IsDigit))
            throw new ArgumentException("PIN 必须是 6 位数字。", nameof(pin));
        Pin = pin;
        ExpiresAt = expiresAt;
    }

    public static PairingWindow Open(DateTimeOffset now, TimeSpan ttl)
    {
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return new PairingWindow(value.ToString("D6", CultureInfo.InvariantCulture), now.Add(ttl));
    }

    public bool IsActive(DateTimeOffset now) => !Closed && Failures < MaxFailures && now < ExpiresAt;

    public PinCheckResult Check(string? pin, DateTimeOffset now)
    {
        if (Closed || now >= ExpiresAt) return PinCheckResult.Closed;
        if (Failures >= MaxFailures) return PinCheckResult.Locked;
        var digits = pin is { Length: PinLength } && pin.All(char.IsDigit);
        var ok = digits && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Pin),
            Encoding.ASCII.GetBytes(pin!));
        if (!ok)
        {
            Failures++;
            return Failures >= MaxFailures ? PinCheckResult.Locked : PinCheckResult.Mismatch;
        }
        return PinCheckResult.Match;
    }

    public void Close() => Closed = true;
}

public static class SensitiveTextFilter
{
    private static readonly System.Text.RegularExpressions.Regex[] Patterns =
    [
        new(@"-----BEGIN (?:RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"\bAKIA[0-9A-Z]{16}\b", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9]{16,}", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"\bghp_[A-Za-z0-9]{20,}", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"(?i)(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token|密码|口令)\s*[:=：]\s*\S{4,}", System.Text.RegularExpressions.RegexOptions.Compiled)
    ];

    public static bool IsSensitive(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var pattern in Patterns)
            if (pattern.IsMatch(text)) return true;
        return false;
    }
}

public static class LanAddress
{
    public static bool IsPrivateLan(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            if ((b[0] & 0xFE) == 0xFC) return true;
            return false;
        }
        return false;
    }
}

public sealed class NonceCache
{
    private readonly Dictionary<string, long> _seen = new();
    private readonly object _gate = new();

    public bool TryUse(string deviceId, string nonce, long nowMs, long ttlMs = 600_000)
    {
        lock (_gate)
        {
            if (_seen.Count > 8000)
            {
                var stale = _seen.Where(kv => nowMs - kv.Value > ttlMs).Select(kv => kv.Key).ToList();
                foreach (var key in stale) _seen.Remove(key);
            }
            var id = deviceId + "\n" + nonce;
            if (_seen.TryGetValue(id, out var at) && nowMs - at <= ttlMs) return false;
            _seen[id] = nowMs;
            return true;
        }
    }
}
