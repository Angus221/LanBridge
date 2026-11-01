namespace LanBridge.Core;

public static class HistoryLabels
{
    public static string OriginOnPc(string? direction) => direction is "in" or "from_phone" ? "来自手机" : "来自电脑";

    public static string When(long unixMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return local.ToString("M月d日 HH:mm");
    }

    public static bool VisibleForPeer(string? recordPeerId, string? selectedPeerId, int peerCount)
    {
        if (string.IsNullOrEmpty(selectedPeerId)) return peerCount == 0 && string.IsNullOrEmpty(recordPeerId);
        if (string.Equals(recordPeerId, selectedPeerId, StringComparison.Ordinal)) return true;
        return string.IsNullOrEmpty(recordPeerId) && peerCount <= 1;
    }

    public static bool CanCopy(string? kind) => kind is "clipboard" or "text";

    public static bool CanOpenFile(string? kind, string? path, bool exists)
        => kind == "file" && !string.IsNullOrWhiteSpace(path) && exists;
}
