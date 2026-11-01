using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge.Core;

public static class Hashing
{
    public static string Sha256Hex(byte[] data) => ToHex(SHA256.HashData(data));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256Hex(stream);
    }

    public static string Sha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    public static async Task<string> CopyAndHashAsync(
        Stream input,
        Stream output,
        long maxBytes,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("too_large");
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(total);
        }
        return ToHex(sha.GetHashAndReset());
    }

    public static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    public static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (var c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }
}

public static class FileNameSanitizer
{
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return ProtocolConstants.ReceivedFallbackName;
        var source = input.Replace('\\', '/');
        var parts = source.Split('/');
        var name = "";
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].Length > 0)
            {
                name = parts[i];
                break;
            }
        }
        if (name.Length == 0) return ProtocolConstants.ReceivedFallbackName;

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32) continue;
            if ("<>:\"/\\|?*".Contains(c)) continue;
            if (c is >= '\u202A' and <= '\u202E') continue;
            if (c is >= '\u2066' and <= '\u2069') continue;
            sb.Append(c);
        }
        name = sb.ToString().Trim(' ', '.');
        if (name.Length == 0) return ProtocolConstants.ReceivedFallbackName;
        if (IsReserved(name)) name = "_" + name;
        if (name.Length > 120) name = Truncate(name, 120);
        name = name.Trim(' ', '.');
        if (name.Length == 0) return ProtocolConstants.ReceivedFallbackName;
        return name;
    }

    public static string Unique(string safeName, Func<string, bool> exists)
    {
        if (!exists(safeName)) return safeName;
        var dot = safeName.LastIndexOf('.');
        var stem = dot > 0 ? safeName[..dot] : safeName;
        var ext = dot > 0 ? safeName[dot..] : "";
        for (var i = 1; i <= 9999; i++)
        {
            var suffix = " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
            var keep = stem;
            var candidate = keep + suffix + ext;
            if (candidate.Length > 120)
            {
                var stemKeep = 120 - suffix.Length - ext.Length;
                if (stemKeep < 1) stemKeep = 1;
                if (keep.Length > stemKeep) keep = keep[..stemKeep];
                candidate = keep + suffix + ext;
                if (candidate.Length > 120) candidate = candidate[..120];
            }
            if (!exists(candidate)) return candidate;
        }
        return Guid.NewGuid().ToString("N") + ext;
    }

    private static bool IsReserved(string name)
    {
        var dot = name.LastIndexOf('.');
        var baseName = dot > 0 ? name[..dot] : name;
        baseName = baseName.ToUpperInvariant();
        if (baseName is "CON" or "PRN" or "AUX" or "NUL") return true;
        if (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.Ordinal) || baseName.StartsWith("LPT", StringComparison.Ordinal)))
        {
            var digit = baseName[3];
            if (digit is >= '1' and <= '9') return true;
        }
        return false;
    }

    private static string Truncate(string name, int max)
    {
        var dot = name.LastIndexOf('.');
        if (dot > 0 && name.Length - dot <= 16)
        {
            var ext = name[dot..];
            var keep = max - ext.Length;
            if (keep < 1) return name[..max];
            return name[..keep] + ext;
        }
        return name[..max];
    }
}

public enum ReceiveAction { Accept, Confirm, Reject }

public readonly record struct ReceiveDecision(ReceiveAction Action, string? Reason, string SafeName);

public static class ReceivePolicy
{
    public static ReceiveDecision Evaluate(
        string? originalName,
        long size,
        long freeBytes,
        long maxBytes,
        bool autoReceive,
        int activeTransfers,
        int maxConcurrent = ProtocolConstants.MaxConcurrentTransfers)
    {
        var safe = FileNameSanitizer.Sanitize(originalName);
        if (size < 0) return new ReceiveDecision(ReceiveAction.Reject, "invalid_size", safe);
        if (maxBytes > 0 && size > maxBytes) return new ReceiveDecision(ReceiveAction.Reject, "too_large", safe);
        if (activeTransfers >= maxConcurrent) return new ReceiveDecision(ReceiveAction.Reject, "busy", safe);
        if (freeBytes < size + ProtocolConstants.DiskMarginBytes) return new ReceiveDecision(ReceiveAction.Reject, "no_space", safe);
        if (!autoReceive) return new ReceiveDecision(ReceiveAction.Confirm, "needs_confirm", safe);
        return new ReceiveDecision(ReceiveAction.Accept, null, safe);
    }
}

public static class PathSafety
{
    public static bool IsUnderDirectory(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var sep = Path.DirectorySeparatorChar;
        if (!fullRoot.EndsWith(sep)) fullRoot += sep;
        var full = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(fullRoot, comparison);
    }
}

public sealed class IntegrityException : Exception
{
    public string Expected { get; }
    public string Actual { get; }

    public IntegrityException(string expected, string actual) : base("hash_mismatch")
    {
        Expected = expected;
        Actual = actual;
    }
}

public static class AtomicFile
{
    public static void CommitVerified(string partPath, string finalPath, string expectedSha256)
    {
        var actual = Hashing.Sha256File(partPath);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partPath);
            throw new IntegrityException(expectedSha256, actual);
        }
        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.Move(partPath, finalPath, overwrite: true);
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var index = -1;
        do
        {
            value /= 1024;
            index++;
        } while (value >= 1024 && index < units.Length - 1);
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[index];
    }
}

public static class MimeGuess
{
    public static string FromName(string? name)
    {
        var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" or ".md" => "text/plain",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".zip" => "application/zip",
            ".apk" => "application/vnd.android.package-archive",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }
}
