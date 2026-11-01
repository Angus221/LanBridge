namespace LanBridge.Server;

public sealed class BridgeServerOptions
{
    public required System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; init; }
    public required string DeviceId { get; init; }
    public required LanBridge.Core.IRuntimeSettings Settings { get; init; }
    public required LanBridge.Core.ITrustedDeviceStore Devices { get; init; }
    public System.Net.IPAddress BindAddress { get; init; } = System.Net.IPAddress.Any;
    public int Port { get; init; } = LanBridge.Core.ProtocolConstants.DefaultPort;
    public Func<long>? FreeBytesOverride { get; init; }
    public Action<string>? Log { get; init; }
}

public interface IUserConsent
{
    Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken cancellationToken);
    Task<bool> ConfirmReceiveAsync(FilePrompt prompt, CancellationToken cancellationToken);
}

public sealed record PairingPrompt(string DeviceId, string DeviceName, string Platform, string KeyFingerprint);
public sealed record FilePrompt(string TransferId, string Name, long Size, string PeerName);
public sealed record SendResult(bool Ok, string Status, string? Detail);

public sealed record ClipboardIncoming(
    string PeerId,
    string PeerName,
    string Text,
    string Sha256,
    bool Manual,
    bool Sensitive);

public sealed record TransferNotice(
    string Id,
    string Direction,
    string Kind,
    string? PeerId,
    string? PeerName,
    string Name,
    long Size,
    long BytesDone,
    string Status,
    string? Error,
    string? LocalPath,
    string? Sha256,
    string? TextPreview,
    bool Sensitive);

public sealed record PeerPresence(string DeviceId, string Name, bool Online);

public sealed class AllowAllConsent : IUserConsent
{
    public Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> ConfirmReceiveAsync(FilePrompt prompt, CancellationToken cancellationToken) => Task.FromResult(true);
}
