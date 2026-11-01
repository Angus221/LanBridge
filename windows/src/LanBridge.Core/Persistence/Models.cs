namespace LanBridge.Core;

public interface IRuntimeSettings
{
    bool AutoReceive { get; }
    bool ClipboardSyncEnabled { get; }
    bool ClipboardPaused { get; }
    bool SaveTextHistory { get; }
    bool FilterSensitive { get; }
    long MaxFileBytes { get; }
    string IncomingDirectory { get; }
    string DeviceName { get; }
}

public sealed class AppSettings : IRuntimeSettings
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "我的电脑";
    public string IncomingDirectory { get; set; } = "";
    public int Port { get; set; } = ProtocolConstants.DefaultPort;
    public bool AutoReceive { get; set; } = true;
    public bool ClipboardSyncEnabled { get; set; }
    public bool ClipboardPaused { get; set; }
    public bool SaveTextHistory { get; set; } = true;
    public bool FilterSensitive { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public long MaxFileBytes { get; set; } = ProtocolConstants.DefaultMaxFileBytes;
    public string SelectedPhoneId { get; set; } = "";
    public string HotkeySend { get; set; } = "Ctrl+Alt+V";
    public string HotkeySidebar { get; set; } = "Ctrl+Alt+L";
    public bool SidebarPinned { get; set; }
    public Dictionary<string, bool> PhoneClipboardSync { get; set; } = new(StringComparer.Ordinal);

    public bool ClipboardEnabledFor(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return ClipboardSyncEnabled;
        if (PhoneClipboardSync != null && PhoneClipboardSync.TryGetValue(deviceId, out var value)) return value;
        return ClipboardSyncEnabled;
    }

    public void SetClipboardEnabled(string deviceId, bool enabled)
    {
        PhoneClipboardSync ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        PhoneClipboardSync[deviceId] = enabled;
    }

    public AppSettings Clone() => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        IncomingDirectory = IncomingDirectory,
        Port = Port,
        AutoReceive = AutoReceive,
        ClipboardSyncEnabled = ClipboardSyncEnabled,
        ClipboardPaused = ClipboardPaused,
        SaveTextHistory = SaveTextHistory,
        FilterSensitive = FilterSensitive,
        RunAtStartup = RunAtStartup,
        MaxFileBytes = MaxFileBytes,
        SelectedPhoneId = SelectedPhoneId,
        HotkeySend = string.IsNullOrWhiteSpace(HotkeySend) ? "Ctrl+Alt+V" : HotkeySend,
        HotkeySidebar = string.IsNullOrWhiteSpace(HotkeySidebar) ? "Ctrl+Alt+L" : HotkeySidebar,
        SidebarPinned = SidebarPinned,
        PhoneClipboardSync = PhoneClipboardSync == null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(PhoneClipboardSync, StringComparer.Ordinal)
    };
}

public sealed class TrustedDevice
{
    public required string DeviceId { get; init; }
    public required string Name { get; set; }
    public required string Platform { get; init; }
    public required byte[] PublicKeySpki { get; set; }
    public long PairedAtUnixMs { get; init; }
    public long? LastSeenUnixMs { get; set; }
    public bool Revoked { get; set; }
}

public interface ITrustedDeviceStore
{
    TrustedDevice? FindUsable(string deviceId);
    IReadOnlyList<TrustedDevice> ListActive();
    void Save(TrustedDevice device);
    void Revoke(string deviceId);
    void Touch(string deviceId, long unixMs);
    void Rename(string deviceId, string name);
}

public sealed class TransferRecord
{
    public string Id { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? PeerId { get; set; }
    public string? PeerName { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long BytesDone { get; set; }
    public string? Sha256 { get; set; }
    public string? LocalPath { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public long CreatedAt { get; set; }
    public long? FinishedAt { get; set; }
    public string? TextPreview { get; set; }
}

public interface ITransferHistory
{
    void Upsert(TransferRecord record);
    IReadOnlyList<TransferRecord> Recent(int limit);
    void Clear();
    TransferRecord? Find(string id);
}
