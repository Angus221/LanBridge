using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanBridge.Core;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string ServiceType = "_lanbridge._tcp";
    public const int DefaultPort = 47821;
    public const int ChunkSize = 1_048_576;
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const long AbsoluteMaxFileBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxTextBytes = 256 * 1024;
    public const int MaxWsMessageBytes = 512 * 1024;
    public const int PairingTtlSeconds = 120;
    public const int ConsentTimeoutSeconds = 60;
    public const int MaxConcurrentTransfers = 2;
    public const long DiskMarginBytes = 64L * 1024 * 1024;
    public const string ReceivedFallbackName = "received.bin";

    public const string HeaderDeviceId = "X-LanBridge-Device-Id";
    public const string HeaderTimestamp = "X-LanBridge-Timestamp";
    public const string HeaderNonce = "X-LanBridge-Nonce";
    public const string HeaderSignature = "X-LanBridge-Signature";
    public const string HeaderContentSha256 = "X-LanBridge-Content-Sha256";
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class Envelope
{
    [JsonPropertyName("v")] public int V { get; set; } = ProtocolConstants.Version;
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("msgId")] public string MsgId { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; set; }

    public static Envelope Create<T>(string type, T payload)
    {
        return new Envelope
        {
            V = ProtocolConstants.Version,
            Type = type,
            MsgId = Guid.NewGuid().ToString("D"),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options)
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, ProtocolJson.Options);

    public static Envelope? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Envelope>(json, ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    public T? ReadPayload<T>()
    {
        if (Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        return Payload.Deserialize<T>(ProtocolJson.Options);
    }
}

public sealed class FileOfferPayload
{
    public string TransferId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Mime { get; set; } = "application/octet-stream";
    public string Sha256 { get; set; } = "";
    public string Direction { get; set; } = "";
}

public sealed class FileAcceptPayload
{
    public string TransferId { get; set; } = "";
    public string Mode { get; set; } = "";
    [JsonPropertyName("path")] public string RelativePath { get; set; } = "";
    public string ChunkPathTemplate { get; set; } = "";
    public int ChunkSize { get; set; } = ProtocolConstants.ChunkSize;
    public long ResumeOffset { get; set; }
}

public sealed class FileRejectPayload
{
    public string TransferId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class FilePendingPayload
{
    public string TransferId { get; set; } = "";
}

public sealed class FileProgressPayload
{
    public string TransferId { get; set; } = "";
    public long BytesTransferred { get; set; }
    public long Total { get; set; }
}

public sealed class FileCompletePayload
{
    public string TransferId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class FileErrorPayload
{
    public string TransferId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class FileCancelPayload
{
    public string TransferId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ClipboardPushPayload
{
    public string Text { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Manual { get; set; }
}

public sealed class ClipboardAckPayload
{
    public string RefId { get; set; } = "";
    public bool Ok { get; set; }
    public string? Reason { get; set; }
}

public sealed class PairRequestPayload
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string PublicKeySpki { get; set; } = "";
    public string Pin { get; set; } = "";
    public int Protocol { get; set; } = ProtocolConstants.Version;
}

public sealed class PairPendingPayload
{
    public string DeviceName { get; set; } = "";
}

public sealed class PairAcceptPayload
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Protocol { get; set; } = ProtocolConstants.Version;
}

public sealed class PairRejectPayload
{
    public string Reason { get; set; } = "";
}

public sealed class HelloPayload
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int Protocol { get; set; } = ProtocolConstants.Version;
    public string Role { get; set; } = "";
}

public sealed class DeviceRevokedPayload
{
    public string DeviceId { get; set; } = "";
}

public sealed class ProtocolErrorPayload
{
    public string Reason { get; set; } = "";
    public string? Detail { get; set; }
}

public static class PairingLink
{
    public static string Build(string host, int port, string deviceId, string fingerprint, string pin, long expUnixSeconds)
    {
        return "lanbridge://pair?v=1"
            + "&host=" + Uri.EscapeDataString(host)
            + "&port=" + port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&id=" + Uri.EscapeDataString(deviceId)
            + "&fp=" + Uri.EscapeDataString(fingerprint)
            + "&pin=" + Uri.EscapeDataString(pin)
            + "&exp=" + expUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
