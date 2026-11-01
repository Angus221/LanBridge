namespace LanBridge.Core;

public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (s.Contains('\0')) s = s.Replace("\0", "", StringComparison.Ordinal);
        return s;
    }

    public static bool ExceedsLimit(string text)
        => System.Text.Encoding.UTF8.GetByteCount(text) > ProtocolConstants.MaxTextBytes;
}

public sealed class ClipboardGuard
{
    private readonly object _gate = new();
    private string? _suppressHash;
    private long _suppressUntilMs;
    private string? _lastOutbound;

    public void MarkApplied(string hash, long nowMs, int windowMs = 5000)
    {
        lock (_gate)
        {
            _suppressHash = hash;
            _suppressUntilMs = nowMs + windowMs;
        }
    }

    public void MarkSent(string hash)
    {
        lock (_gate) _lastOutbound = hash;
    }

    public bool ShouldAutoSend(string hash, long nowMs)
    {
        lock (_gate)
        {
            if (hash == _lastOutbound) return false;
            if (hash == _suppressHash && nowMs <= _suppressUntilMs) return false;
            return true;
        }
    }
}
