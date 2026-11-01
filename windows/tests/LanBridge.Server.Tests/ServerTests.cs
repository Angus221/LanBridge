using System.Globalization;
using System.Net;
using Xunit;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LanBridge.Core;
using LanBridge.Server;

namespace LanBridge.Server.Tests;

public class ServerTests
{
    [Fact]
    public async Task Info_is_public_and_unpaired_file_put_is_rejected()
    {
        var (server, dir, _) = await StartAsync();
        await using var _ = server;
        using var http = CreateClient(server.Fingerprint);
        var info = await http.GetStringAsync($"https://127.0.0.1:{server.BoundPort}/v1/info");
        Assert.Contains(server.Fingerprint, info, StringComparison.Ordinal);
        var put = new HttpRequestMessage(HttpMethod.Put, $"https://127.0.0.1:{server.BoundPort}/v1/files/{Guid.NewGuid():D}")
        {
            Content = new ByteArrayContent("secret"u8.ToArray())
        };
        var response = await http.SendAsync(put);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Directory.GetFiles(dir));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(server.Fingerprint, server.BoundPort));
    }

    [Fact]
    public async Task Clipboard_targets_the_requested_phone()
    {
        var (server, _, _) = await StartAsync();
        await using var _ = server;
        var (key1, id1, ws1) = await PairAsync(server, server.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        using var _key1 = key1;
        var (key2, id2, ws2) = await PairAsync(server, server.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        using var _key2 = key2;
        Assert.NotNull(ws1);
        Assert.NotNull(ws2);
        Assert.Equal(2, server.ConnectedPeers().Count);
        Assert.Equal(id2, server.CurrentPeer?.DeviceId);

        var pending = server.PushClipboardToAsync(id1, "only-first", true);
        var message = await ReadAsync(ws1!);
        Assert.Equal("clipboard.push", message.Type);
        Assert.Equal("only-first", message.ReadPayload<ClipboardPushPayload>()!.Text);
        await SendAsync(ws1!, Envelope.Create("clipboard.ack", new ClipboardAckPayload { RefId = message.MsgId, Ok = true }));
        var result = await pending;
        Assert.True(result.Ok);
        Assert.Equal("sent", result.Status);

        var missed = await server.PushClipboardToAsync("not-a-phone", "nope", true);
        Assert.False(missed.Ok);
        Assert.Equal("not_connected", missed.Status);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Wrong_pin_is_rejected_and_right_pin_requires_consent()
    {
        var consent = new ScriptedConsent { AllowPair = false };
        var (server, _, _) = await StartAsync(consent: consent);
        await using var _ = server;
        var pin = server.OpenPairing(TimeSpan.FromMinutes(2));
        var ws = await ConnectAsync(server.Fingerprint, server.BoundPort);
        await SendAsync(ws, Envelope.Create("pair.request", new PairRequestPayload
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            DeviceName = "Mate 60",
            Platform = "android",
            PublicKeySpki = Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo()),
            Pin = "000000",
            Protocol = 1
        }));
        var rejected = await ReadAsync(ws);
        Assert.Equal("pair.reject", rejected.Type);
        Assert.Equal("mismatch", rejected.ReadPayload<PairRejectPayload>()!.Reason);

        var (key, id, paired) = await PairAsync(server, pin, allow: false);
        key.Dispose();
        Assert.Null(paired);
        Assert.Equal(0, id.Length == 0 ? 0 : 1);
    }

    [Fact]
    public async Task Paired_upload_verifies_hash_and_blocks_replay_and_traversal()
    {
        var (server, dir, store) = await StartAsync();
        await using var _ = server;
        var (key, id, ws) = await PairAsync(server, server.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        using var _key = key;
        Assert.NotNull(ws);
        var body = "hello lanbridge"u8.ToArray();
        var hash = Hashing.Sha256Hex(body);
        var transferId = Guid.NewGuid().ToString("D");
        await SendAsync(ws!, Envelope.Create("file.offer", new FileOfferPayload
        {
            TransferId = transferId,
            Name = "..\\..\\evil.exe",
            Size = body.Length,
            Mime = "application/octet-stream",
            Sha256 = hash,
            Direction = "to_pc"
        }));
        var accept = await ReadAsync(ws!);
        Assert.Equal("file.accept", accept.Type);
        using var http = CreateClient(server.Fingerprint);
        var response = await PutAsync(http, server.BoundPort, key, id, transferId, body, hash);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("file.complete", (await ReadAsync(ws!)).Type);
        var saved = Path.Combine(dir, "evil.exe");
        Assert.True(File.Exists(saved));
        Assert.Equal("hello lanbridge", File.ReadAllText(saved));
        Assert.True(PathSafety.IsUnderDirectory(dir, saved));
        Assert.DoesNotContain(Directory.GetFiles(dir), path => path.EndsWith(".part", StringComparison.Ordinal));

        var replay = await PutAsync(http, server.BoundPort, key, id, transferId, body, hash, reuseNonce: response.Nonce, reuseTimestamp: response.Timestamp);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);

        var bad = Guid.NewGuid().ToString("D");
        await SendAsync(ws!, Envelope.Create("file.offer", new FileOfferPayload
        {
            TransferId = bad,
            Name = "note.txt",
            Size = 4,
            Sha256 = Hashing.Sha256Hex("nope"),
            Direction = "to_pc"
        }));
        Assert.Equal("file.accept", (await ReadAsync(ws!)).Type);
        var mismatch = await PutAsync(http, server.BoundPort, key, id, bad, "yes!"u8.ToArray(), Hashing.Sha256Hex("nope"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.False(File.Exists(Path.Combine(dir, "note.txt")));

        server.Revoke(id);
        Assert.Null(store.FindUsable(id));
        var after = await PutAsync(http, server.BoundPort, key, id, transferId, body, hash);
        Assert.Equal(HttpStatusCode.Unauthorized, after.Status);
    }

    [Fact]
    public async Task Clipboard_sensitive_text_is_not_applied()
    {
        var (server, _, _) = await StartAsync();
        await using var _ = server;
        var (key, _, ws) = await PairAsync(server, server.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        key.Dispose();
        ClipboardIncoming? incoming = null;
        server.ClipboardReceived += (_, args) => incoming = args;
        await SendAsync(ws!, Envelope.Create("clipboard.push", new ClipboardPushPayload
        {
            Text = "password=hunter2",
            Sha256 = Hashing.Sha256Hex("password=hunter2"),
            Manual = false
        }));
        var blocked = await ReadAsync(ws!);
        Assert.Equal("clipboard.ack", blocked.Type);
        Assert.False(blocked.ReadPayload<ClipboardAckPayload>()!.Ok);
        Assert.Null(incoming);

        var text = "明天开会\n地点：3楼";
        await SendAsync(ws!, Envelope.Create("clipboard.push", new ClipboardPushPayload
        {
            Text = text,
            Sha256 = Hashing.Sha256Hex(text),
            Manual = false
        }));
        var ok = await ReadAsync(ws!);
        Assert.True(ok.ReadPayload<ClipboardAckPayload>()!.Ok);
        Assert.Equal(text, incoming!.Text);
    }

    [Fact]
    public async Task Chunked_upload_assembles_and_no_space_is_rejected()
    {
        var (server, dir, _) = await StartAsync();
        await using var _ = server;
        var (key, id, ws) = await PairAsync(server, server.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        using var _key = key;
        var chunk0 = RandomNumberGenerator.GetBytes(ProtocolConstants.ChunkSize);
        var chunk1 = RandomNumberGenerator.GetBytes(32);
        var all = chunk0.Concat(chunk1).ToArray();
        var hash = Hashing.Sha256Hex(all);
        var transferId = Guid.NewGuid().ToString("D");
        await SendAsync(ws!, Envelope.Create("file.offer", new FileOfferPayload
        {
            TransferId = transferId,
            Name = "blob.bin",
            Size = all.Length,
            Sha256 = hash,
            Direction = "to_pc"
        }));
        Assert.Equal("file.accept", (await ReadAsync(ws!)).Type);
        using var http = CreateClient(server.Fingerprint);
        var first = await PutChunkAsync(http, server.BoundPort, key, id, transferId, 0, chunk0);
        Assert.Equal(HttpStatusCode.OK, first);
        var again = await PutChunkAsync(http, server.BoundPort, key, id, transferId, 0, RandomNumberGenerator.GetBytes(ProtocolConstants.ChunkSize));
        Assert.Equal(HttpStatusCode.Conflict, again);
        var second = await PutChunkAsync(http, server.BoundPort, key, id, transferId, 1, chunk1);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Equal(all, await File.ReadAllBytesAsync(Path.Combine(dir, "blob.bin")));

        var tight = await StartAsync(free: 1000);
        await using var tightServer = tight.Server;
        var paired = await PairAsync(tightServer, tightServer.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        using var tightKey = paired.Key;
        await SendAsync(paired.Socket!, Envelope.Create("file.offer", new FileOfferPayload
        {
            TransferId = Guid.NewGuid().ToString("D"),
            Name = "big.bin",
            Size = 1024,
            Sha256 = new string('a', 64),
            Direction = "to_pc"
        }));
        var reject = await ReadAsync(paired.Socket!);
        Assert.Equal("file.reject", reject.Type);
        Assert.Equal("no_space", reject.ReadPayload<FileRejectPayload>()!.Reason);
    }

    [Fact]
    public async Task Manual_receive_can_decline()
    {
        var consent = new ScriptedConsent { AllowPair = true, AllowReceive = false };
        var (server, _, _) = await StartAsync(autoReceive: false, consent: consent);
        await using var _ = server;
        var (key, _, ws) = await PairAsync(server, server.OpenPairing(TimeSpan.FromMinutes(2)), allow: true);
        key.Dispose();
        await SendAsync(ws!, Envelope.Create("file.offer", new FileOfferPayload
        {
            TransferId = Guid.NewGuid().ToString("D"),
            Name = "a.txt",
            Size = 1,
            Sha256 = Hashing.Sha256Hex("a"),
            Direction = "to_pc"
        }));
        Assert.Equal("file.pending", (await ReadAsync(ws!)).Type);
        var rejected = await ReadAsync(ws!);
        Assert.Equal("file.reject", rejected.Type);
        Assert.Equal("user_declined", rejected.ReadPayload<FileRejectPayload>()!.Reason);
    }

    private static async Task<(BridgeServer Server, string Dir, MemoryDeviceStore Store)> StartAsync(
        bool autoReceive = true,
        long? free = null,
        IUserConsent? consent = null)
    {
        var dir = Directory.CreateTempSubdirectory("lanbridge-server").FullName;
        var settings = new AppSettings
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            DeviceName = "TestPC",
            IncomingDirectory = dir,
            AutoReceive = autoReceive,
            FilterSensitive = true,
            MaxFileBytes = ProtocolConstants.DefaultMaxFileBytes
        };
        var store = new MemoryDeviceStore();
        var server = new BridgeServer(new BridgeServerOptions
        {
            Certificate = CertificateFactory.Create(settings.DeviceId),
            DeviceId = settings.DeviceId,
            Settings = settings,
            Devices = store,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            FreeBytesOverride = free == null ? null : () => free.Value
        }, consent ?? new AllowAllConsent());
        await server.StartAsync();
        return (server, dir, store);
    }

    private static async Task<(ECDsa Key, string Id, ClientWebSocket? Socket)> PairAsync(BridgeServer server, string pin, bool allow)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Guid.NewGuid().ToString("D");
        var ws = await ConnectAsync(server.Fingerprint, server.BoundPort);
        await SendAsync(ws, Envelope.Create("pair.request", new PairRequestPayload
        {
            DeviceId = id,
            DeviceName = "Mate 60",
            Platform = "android",
            PublicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Pin = pin,
            Protocol = 1
        }));
        var first = await ReadAsync(ws);
        if (!allow)
        {
            if (first.Type == "pair.pending")
            {
                var second = await ReadAsync(ws);
                Assert.Equal("pair.reject", second.Type);
            }
            else Assert.Equal("pair.reject", first.Type);
            return (key, "", null);
        }
        Assert.Equal("pair.pending", first.Type);
        Assert.Equal("pair.accept", (await ReadAsync(ws)).Type);
        Assert.Equal("hello", (await ReadAsync(ws)).Type);
        return (key, id, ws);
    }

    private static HttpClient CreateClient(string fingerprint)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && FingerprintOf(cert) == fingerprint
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static async Task<ClientWebSocket> ConnectAsync(string fingerprint, int port, Action<ClientWebSocket>? configure = null)
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && FingerprintOf(cert) == fingerprint;
        configure?.Invoke(ws);
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        return ws;
    }

    private static string FingerprintOf(X509Certificate cert)
    {
        var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert.Export(X509ContentType.Cert));
        return CertFingerprints.Sha256(cert2);
    }

    private static async Task SendAsync(ClientWebSocket ws, Envelope envelope)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJson());
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<Envelope> ReadAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            // ClientWebSocket.ReceiveAsync ignores a cancelled token and, on .NET 8, a
            // token that is disposed after a successful read can stall the next read.
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("socket closed");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Envelope.Parse(Encoding.UTF8.GetString(ms.ToArray())) ?? throw new InvalidDataException("bad envelope");
    }

    private static async Task<SignedResponse> PutAsync(
        HttpClient http, int port, ECDsa key, string deviceId, string transferId, byte[] body, string hash,
        string? reuseNonce = null, long? reuseTimestamp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"https://127.0.0.1:{port}/v1/files/{transferId}")
        {
            Content = new ByteArrayContent(body)
        };
        var signed = Sign(request, key, deviceId, hash, reuseNonce, reuseTimestamp);
        var response = await http.SendAsync(request);
        return new SignedResponse(response.StatusCode, signed.Nonce, signed.Timestamp);
    }

    private static async Task<HttpStatusCode> PutChunkAsync(HttpClient http, int port, ECDsa key, string deviceId, string transferId, int index, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"https://127.0.0.1:{port}/v1/files/{transferId}/chunks/{index}")
        {
            Content = new ByteArrayContent(body)
        };
        Sign(request, key, deviceId, Hashing.Sha256Hex(body));
        var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private static (string Nonce, long Timestamp) Sign(HttpRequestMessage request, ECDsa key, string deviceId, string contentSha, string? nonce = null, long? timestamp = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var path = request.RequestUri!.AbsolutePath;
        var canonical = RequestAuth.Canonical(ts, nonce, request.Method.Method, path, contentSha);
        var signature = Convert.ToBase64String(RequestAuth.Sign(key, canonical));
        request.Headers.TryAddWithoutValidation(ProtocolConstants.HeaderDeviceId, deviceId);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.HeaderTimestamp, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(ProtocolConstants.HeaderNonce, nonce);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.HeaderSignature, signature);
        if (contentSha.Length > 0)
            request.Headers.TryAddWithoutValidation(ProtocolConstants.HeaderContentSha256, contentSha);
        return (nonce, ts);
    }

    private sealed record SignedResponse(HttpStatusCode Status, string Nonce, long Timestamp);

    private sealed class MemoryDeviceStore : ITrustedDeviceStore
    {
        private readonly Dictionary<string, TrustedDevice> _items = new();
        public TrustedDevice? FindUsable(string deviceId) => _items.TryGetValue(deviceId, out var device) && !device.Revoked ? device : null;
        public IReadOnlyList<TrustedDevice> ListActive() => _items.Values.Where(device => !device.Revoked).ToList();
        public void Save(TrustedDevice device) => _items[device.DeviceId] = device;
        public void Revoke(string deviceId) { if (_items.TryGetValue(deviceId, out var device)) device.Revoked = true; }
        public void Touch(string deviceId, long unixMs) { if (_items.TryGetValue(deviceId, out var device)) device.LastSeenUnixMs = unixMs; }
        public void Rename(string deviceId, string name) { if (_items.TryGetValue(deviceId, out var device)) device.Name = name; }
    }

    private sealed class ScriptedConsent : IUserConsent
    {
        public bool AllowPair { get; set; } = true;
        public bool AllowReceive { get; set; } = true;
        public Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken cancellationToken) => Task.FromResult(AllowPair);
        public Task<bool> ConfirmReceiveAsync(FilePrompt prompt, CancellationToken cancellationToken) => Task.FromResult(AllowReceive);
    }
}
