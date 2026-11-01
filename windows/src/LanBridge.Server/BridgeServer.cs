using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using LanBridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LanBridge.Server;

public sealed class BridgeServer : IAsyncDisposable
{
    private readonly BridgeServerOptions _options;
    private readonly IRuntimeSettings _settings;
    private readonly ITrustedDeviceStore _devices;
    private readonly IUserConsent _consent;
    private readonly NonceCache _nonces = new();
    private readonly ConcurrentDictionary<string, PeerSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, TransferSession> _transfers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClipboardAckPayload>> _acks = new();
    private readonly object _pairGate = new();
    private PairingWindow? _pairing;
    private WebApplication? _app;
    private CancellationTokenSource? _run;
    private PeerSocket? _current;

    public BridgeServer(BridgeServerOptions options, IUserConsent consent)
    {
        _options = options;
        _settings = options.Settings;
        _devices = options.Devices;
        _consent = consent;
    }

    public int BoundPort { get; private set; }
    public string Fingerprint => CertFingerprints.Sha256(_options.Certificate);
    public bool PairingOpen { get { lock (_pairGate) return _pairing?.IsActive(DateTimeOffset.UtcNow) == true; } }
    public string? ActivePin { get { lock (_pairGate) return _pairing?.IsActive(DateTimeOffset.UtcNow) == true ? _pairing.Pin : null; } }
    public DateTimeOffset? PairingExpiresAt { get { lock (_pairGate) return _pairing?.IsActive(DateTimeOffset.UtcNow) == true ? _pairing.ExpiresAt : null; } }
    public PeerPresence? CurrentPeer => _current is { Authenticated: true, DeviceId: { } id }
        ? new PeerPresence(id, _current.Name, true) : null;

    public event EventHandler<ClipboardIncoming>? ClipboardReceived;
    public event EventHandler<TransferNotice>? TransferChanged;
    public event EventHandler<PeerPresence>? PeerPresenceChanged;

    public string OpenPairing(TimeSpan? ttl = null)
    {
        var window = PairingWindow.Open(DateTimeOffset.UtcNow, ttl ?? TimeSpan.FromSeconds(ProtocolConstants.PairingTtlSeconds));
        lock (_pairGate) _pairing = window;
        Log("pairing window opened");
        return window.Pin;
    }

    public void ClosePairing()
    {
        lock (_pairGate) _pairing?.Close();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_settings.IncomingDirectory);
        var address = _options.BindAddress;
        var first = _options.Port <= 0 ? 0 : _options.Port;
        var attempts = first == 0 ? new[] { 0 } : Enumerable.Range(first, 10).ToArray();
        Exception? last = null;
        foreach (var port in attempts)
        {
            try
            {
                await StartOnAsync(address, port, cancellationToken);
                _run = new CancellationTokenSource();
                _ = Task.Run(() => PingLoop(_run.Token));
                Log($"listening on {BoundPort}");
                return;
            }
            catch (Exception ex) when (port != 0)
            {
                last = ex;
                Log($"bind {port} failed: {ex.Message}");
                if (_app != null)
                {
                    await _app.DisposeAsync();
                    _app = null;
                }
            }
        }
        throw last ?? new IOException("无法绑定端口。");
    }

    public async Task StopAsync()
    {
        try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var socket in _sockets.Values)
            await socket.CloseAsync();
        _sockets.Clear();
        _current = null;
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    public void Revoke(string deviceId)
    {
        _devices.Revoke(deviceId);
        if (_sockets.TryRemove(deviceId, out var socket))
        {
            if (ReferenceEquals(_current, socket)) _current = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    await socket.SendAsync(Envelope.Create("device.revoked", new DeviceRevokedPayload { DeviceId = deviceId }), CancellationToken.None);
                    await socket.CloseAsync();
                }
                catch (Exception ex) { Log("revoke close: " + ex.Message); }
            });
        }
        RaisePeer(new PeerPresence(deviceId, "", false));
        Log("revoked " + deviceId);
    }

    public async Task<SendResult> PushClipboardAsync(string text, bool manual, CancellationToken cancellationToken = default)
    {
        var peer = _current;
        if (peer is not { Authenticated: true }) return new SendResult(false, "not_connected", null);
        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0) return new SendResult(false, "empty", null);
        if (TextNormalizer.ExceedsLimit(normalized)) return new SendResult(false, "too_large", null);
        var hash = Hashing.Sha256Hex(normalized);
        var envelope = Envelope.Create("clipboard.push", new ClipboardPushPayload
        {
            Text = normalized,
            Sha256 = hash,
            Manual = manual
        });
        var wait = new TaskCompletionSource<ClipboardAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acks[envelope.MsgId] = wait;
        try
        {
            await peer.SendAsync(envelope, cancellationToken);
            var ack = await wait.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return ack.Ok
                ? new SendResult(true, "sent", null)
                : new SendResult(false, ack.Reason ?? "dropped", null);
        }
        catch (TimeoutException)
        {
            return new SendResult(true, "sent_unconfirmed", null);
        }
        finally
        {
            _acks.TryRemove(envelope.MsgId, out _);
        }
    }

    public async Task<SendResult> OfferFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var peer = _current;
        if (peer is not { Authenticated: true, DeviceId: { } peerId }) return new SendResult(false, "not_connected", null);
        if (!File.Exists(path)) return new SendResult(false, "missing", null);
        var size = new FileInfo(path).Length;
        var max = Math.Min(_settings.MaxFileBytes, ProtocolConstants.AbsoluteMaxFileBytes);
        if (size > max) return new SendResult(false, "too_large", null);
        var hash = await Task.Run(() => Hashing.Sha256File(path), cancellationToken);
        var id = Guid.NewGuid().ToString("D");
        var name = Path.GetFileName(path);
        var session = new TransferSession
        {
            Id = id,
            PeerId = peerId,
            Direction = "to_phone",
            OriginalName = name,
            SafeName = name,
            SourcePath = path,
            Size = size,
            Sha256 = hash,
            Mime = MimeGuess.FromName(name),
            State = "offered",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        _transfers[id] = session;
        RaiseTransfer(Notice(session, peer.Name));
        await peer.SendAsync(Envelope.Create("file.offer", new FileOfferPayload
        {
            TransferId = id,
            Name = name,
            Size = size,
            Mime = session.Mime,
            Sha256 = hash,
            Direction = "to_phone"
        }), cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, session.Cancel.Token);
        try
        {
            return await session.Done.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            session.State = "cancelled";
            RaiseTransfer(Notice(session, peer.Name));
            return new SendResult(false, "cancelled", null);
        }
    }

    public void CancelTransfer(string transferId)
    {
        if (!_transfers.TryGetValue(transferId, out var session)) return;
        session.State = "cancelled";
        session.Error = "cancelled";
        session.Cancel.Cancel();
        if (session.PartPath != null) AtomicFile.TryDelete(session.PartPath);
        session.Done.TrySetResult(new SendResult(false, "cancelled", null));
        var peer = FindSocket(session.PeerId);
        if (peer != null)
        {
            _ = peer.SendAsync(Envelope.Create("file.cancel", new FileCancelPayload
            {
                TransferId = transferId,
                Reason = "user"
            }), CancellationToken.None);
        }
        RaiseTransfer(Notice(session, peer?.Name));
    }

    private async Task StartOnAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = "LanBridge",
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = ProtocolConstants.AbsoluteMaxFileBytes;
            options.Listen(address, port, listen =>
            {
                listen.UseHttps(_options.Certificate);
                listen.Protocols = HttpProtocols.Http1;
            });
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(async (context, next) =>
        {
            if (!LanAddress.IsPrivateLan(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = "not_lan" }, ProtocolJson.Options);
                return;
            }
            await next();
        });
        app.MapGet("/v1/info", (HttpContext context) => Results.Json(new
        {
            v = ProtocolConstants.Version,
            protocol = ProtocolConstants.Version,
            role = "pc",
            deviceId = _options.DeviceId,
            name = _settings.DeviceName,
            fingerprint = Fingerprint,
            pairing = PairingOpen,
            port = BoundPort == 0 ? _options.Port : BoundPort
        }, ProtocolJson.Options));
        app.Map("/v1/ws", HandleSocket);
        app.MapPut("/v1/files/{id}", HandlePut);
        app.MapPut("/v1/files/{id}/chunks/{index:int}", HandleChunk);
        app.MapGet("/v1/files/{id}", HandleGet);
        app.MapGet("/v1/files/{id}/status", HandleStatus);
        await app.StartAsync(cancellationToken);
        _app = app;
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? app.Urls;
        BoundPort = new Uri(addresses.First()).Port;
    }

    private async Task HandleSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        TrustedDevice? authed = null;
        var hasIdentity = context.Request.Headers.ContainsKey(ProtocolConstants.HeaderDeviceId);
        if (hasIdentity)
        {
            if (!Authorize(context, out var device))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            authed = device;
        }
        else if (!PairingOpen)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new PeerSocket(socket);
        if (authed != null)
        {
            peer.Authenticated = true;
            peer.DeviceId = authed.DeviceId;
            peer.Name = authed.Name;
            Register(peer);
            await peer.SendAsync(Hello(), context.RequestAborted);
        }
        await ReceiveLoop(peer, CancellationToken.None);
    }

    private async Task ReceiveLoop(PeerSocket peer, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (peer.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await peer.Socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    if (message.Length + result.Count > ProtocolConstants.MaxWsMessageBytes)
                    {
                        await peer.CloseAsync();
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                var envelope = Envelope.Parse(json);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.Type))
                {
                    await SendError(peer, "bad_message", null);
                    continue;
                }
                if (envelope.V != ProtocolConstants.Version)
                {
                    await SendError(peer, "bad_version", null);
                    continue;
                }
                if (!peer.Authenticated)
                {
                    if (envelope.Type != "pair.request")
                    {
                        await SendError(peer, "unauthorized", null);
                        await peer.CloseAsync();
                        return;
                    }
                    var outcome = await HandlePairAsync(peer, envelope, cancellationToken);
                    if (outcome == PairOutcome.Failed) return;
                    continue;
                }
                peer.LastSeenMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await DispatchAsync(peer, envelope, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log("socket: " + ex.Message); }
        finally
        {
            if (peer.DeviceId != null)
            {
                _sockets.TryRemove(new KeyValuePair<string, PeerSocket>(peer.DeviceId, peer));
                if (ReferenceEquals(_current, peer)) _current = _sockets.Values.LastOrDefault();
                RaisePeer(new PeerPresence(peer.DeviceId, peer.Name, false));
            }
            await peer.CloseAsync();
        }
    }

    private async Task<PairOutcome> HandlePairAsync(PeerSocket peer, Envelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.ReadPayload<PairRequestPayload>();
        if (request == null || !Guid.TryParse(request.DeviceId, out var guid))
        {
            await SendReject(peer, "bad_request");
            await peer.CloseAsync();
            return PairOutcome.Failed;
        }
        var deviceId = guid.ToString("D");
        byte[] spki;
        try { spki = Convert.FromBase64String(request.PublicKeySpki ?? ""); }
        catch (FormatException)
        {
            await SendReject(peer, "bad_key");
            await peer.CloseAsync();
            return PairOutcome.Failed;
        }
        ECDsa? imported = null;
        var keyOk = spki.Length is >= 64 and <= 512 && RequestAuth.TryImportP256Public(spki, out imported);
        imported?.Dispose();
        if (!keyOk)
        {
            await SendReject(peer, "bad_key");
            await peer.CloseAsync();
            return PairOutcome.Failed;
        }
        if (request.Protocol != ProtocolConstants.Version)
        {
            await SendReject(peer, "bad_version");
            await peer.CloseAsync();
            return PairOutcome.Failed;
        }

        PinCheckResult pinResult;
        lock (_pairGate)
        {
            pinResult = _pairing?.Check(request.Pin, DateTimeOffset.UtcNow) ?? PinCheckResult.Closed;
        }
        if (pinResult != PinCheckResult.Match)
        {
            var reason = pinResult switch
            {
                PinCheckResult.Locked => "locked",
                PinCheckResult.Closed => "closed",
                _ => "mismatch"
            };
            await SendReject(peer, reason);
            if (pinResult is PinCheckResult.Locked or PinCheckResult.Closed)
            {
                await peer.CloseAsync();
                return PairOutcome.Failed;
            }
            return PairOutcome.Retry;
        }
        lock (_pairGate) _pairing?.Close();

        var name = CleanName(request.DeviceName);
        if (name.Length == 0) name = "Android";
        await peer.SendAsync(Envelope.Create("pair.pending", new PairPendingPayload { DeviceName = name }), CancellationToken.None);
        var prompt = new PairingPrompt(deviceId, name, CleanName(request.Platform), CertFingerprints.Group(Hashing.Sha256Hex(spki)));
        bool allowed;
        var timedOut = false;
        using (var consentTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProtocolConstants.ConsentTimeoutSeconds)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consentTimeout.Token))
        {
            try { allowed = await _consent.ConfirmPairingAsync(prompt, linked.Token); }
            catch (OperationCanceledException) { allowed = false; timedOut = consentTimeout.IsCancellationRequested; }
        }
        if (!allowed)
        {
            await SendReject(peer, timedOut ? "timeout" : "denied");
            await peer.CloseAsync();
            return PairOutcome.Failed;
        }

        _devices.Save(new TrustedDevice
        {
            DeviceId = deviceId,
            Name = name,
            Platform = string.IsNullOrWhiteSpace(request.Platform) ? "android" : CleanName(request.Platform),
            PublicKeySpki = spki,
            PairedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Revoked = false
        });
        peer.Authenticated = true;
        peer.DeviceId = deviceId;
        peer.Name = name;
        Register(peer);
        lock (_pairGate) _pairing?.Close();
        await peer.SendAsync(Envelope.Create("pair.accept", new PairAcceptPayload
        {
            DeviceId = _options.DeviceId,
            DeviceName = _settings.DeviceName,
            Fingerprint = Fingerprint,
            Protocol = ProtocolConstants.Version
        }), cancellationToken);
        await peer.SendAsync(Hello(), cancellationToken);
        Log("paired " + deviceId);
        return PairOutcome.Paired;
    }

    private async Task DispatchAsync(PeerSocket peer, Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case "ping":
                await peer.SendAsync(Envelope.Create("pong", new { }), cancellationToken);
                Touch(peer);
                break;
            case "pong":
                Touch(peer);
                break;
            case "hello":
                var hello = envelope.ReadPayload<HelloPayload>();
                if (hello != null && !string.IsNullOrWhiteSpace(hello.DeviceName) && peer.DeviceId != null)
                {
                    peer.Name = CleanName(hello.DeviceName);
                    _devices.Rename(peer.DeviceId, peer.Name);
                    RaisePeer(new PeerPresence(peer.DeviceId, peer.Name, true));
                }
                break;
            case "clipboard.push":
                await HandleClipboardAsync(peer, envelope);
                break;
            case "clipboard.ack":
                var ack = envelope.ReadPayload<ClipboardAckPayload>();
                if (ack != null && _acks.TryGetValue(ack.RefId, out var waiter)) waiter.TrySetResult(ack);
                break;
            case "file.offer":
                await HandleOfferAsync(peer, envelope, cancellationToken);
                break;
            case "file.accept":
                MarkAccepted(envelope.ReadPayload<FileAcceptPayload>()?.TransferId);
                break;
            case "file.reject":
                var reject = envelope.ReadPayload<FileRejectPayload>();
                FailSession(reject?.TransferId, "rejected", reject?.Reason ?? "rejected");
                break;
            case "file.progress":
                var progress = envelope.ReadPayload<FileProgressPayload>();
                if (progress != null && _transfers.TryGetValue(progress.TransferId, out var progressing))
                {
                    progressing.BytesObserved = progress.BytesTransferred;
                    RaiseTransfer(Notice(progressing, peer.Name));
                }
                break;
            case "file.complete":
                var complete = envelope.ReadPayload<FileCompletePayload>();
                CompleteSession(complete?.TransferId, complete?.Sha256, complete?.Name);
                break;
            case "file.error":
                var error = envelope.ReadPayload<FileErrorPayload>();
                FailSession(error?.TransferId, "failed", error?.Reason ?? "failed");
                break;
            case "file.cancel":
                var cancel = envelope.ReadPayload<FileCancelPayload>();
                if (cancel != null) CancelFromPeer(cancel.TransferId);
                break;
            case "device.unlink":
                if (peer.DeviceId != null) Revoke(peer.DeviceId);
                break;
            default:
                break;
        }
    }

    private async Task HandleClipboardAsync(PeerSocket peer, Envelope envelope)
    {
        var payload = envelope.ReadPayload<ClipboardPushPayload>();
        if (payload == null)
        {
            await Ack(peer, envelope.MsgId, false, "bad_request");
            return;
        }
        var text = TextNormalizer.Normalize(payload.Text);
        if (TextNormalizer.ExceedsLimit(text))
        {
            await Ack(peer, envelope.MsgId, false, "too_large");
            return;
        }
        var hash = Hashing.Sha256Hex(text);
        if (!hash.Equals(payload.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
        {
            await Ack(peer, envelope.MsgId, false, "hash_mismatch");
            return;
        }
        var sensitive = SensitiveTextFilter.IsSensitive(text);
        if (sensitive && _settings.FilterSensitive && !payload.Manual)
        {
            await Ack(peer, envelope.MsgId, false, "sensitive");
            Log("blocked sensitive clipboard");
            return;
        }
        try
        {
            ClipboardReceived?.Invoke(this, new ClipboardIncoming(
                peer.DeviceId ?? "",
                peer.Name,
                text,
                hash,
                payload.Manual,
                sensitive));
        }
        catch (Exception ex) { Log("clipboard listener: " + ex.Message); }
        await Ack(peer, envelope.MsgId, true, null);
    }

    private async Task HandleOfferAsync(PeerSocket peer, Envelope envelope, CancellationToken cancellationToken)
    {
        var offer = envelope.ReadPayload<FileOfferPayload>();
        if (offer == null || !Guid.TryParse(offer.TransferId, out _))
        {
            await SendError(peer, "bad_request", null);
            return;
        }
        if (offer.Direction != "to_pc")
        {
            await peer.SendAsync(Envelope.Create("file.reject", new FileRejectPayload
            {
                TransferId = offer.TransferId,
                Reason = "bad_direction"
            }), cancellationToken);
            return;
        }
        if (!Hashing.IsSha256Hex(offer.Sha256))
        {
            await peer.SendAsync(Envelope.Create("file.reject", new FileRejectPayload
            {
                TransferId = offer.TransferId,
                Reason = "bad_hash"
            }), cancellationToken);
            return;
        }
        var max = Math.Min(_settings.MaxFileBytes, ProtocolConstants.AbsoluteMaxFileBytes);
        var decision = ReceivePolicy.Evaluate(offer.Name, offer.Size, FreeBytes(), max, _settings.AutoReceive, ActiveCount());
        if (decision.Action == ReceiveAction.Reject)
        {
            await peer.SendAsync(Envelope.Create("file.reject", new FileRejectPayload
            {
                TransferId = offer.TransferId,
                Reason = decision.Reason ?? "rejected"
            }), cancellationToken);
            return;
        }
        if (decision.Action == ReceiveAction.Confirm)
        {
            await peer.SendAsync(Envelope.Create("file.pending", new FilePendingPayload { TransferId = offer.TransferId }), cancellationToken);
            bool allowed;
            using var consentTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProtocolConstants.ConsentTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consentTimeout.Token);
            try
            {
                allowed = await _consent.ConfirmReceiveAsync(new FilePrompt(offer.TransferId, decision.SafeName, offer.Size, peer.Name), linked.Token);
            }
            catch (OperationCanceledException) { allowed = false; }
            if (!allowed)
            {
                await peer.SendAsync(Envelope.Create("file.reject", new FileRejectPayload
                {
                    TransferId = offer.TransferId,
                    Reason = "user_declined"
                }), cancellationToken);
                return;
            }
        }
        var directory = _settings.IncomingDirectory;
        Directory.CreateDirectory(directory);
        var unique = FileNameSanitizer.Unique(decision.SafeName, candidate => File.Exists(Path.Combine(directory, candidate)));
        var finalPath = Path.Combine(directory, unique);
        var partPath = Path.Combine(directory, "." + offer.TransferId + ".part");
        if (!PathSafety.IsUnderDirectory(directory, finalPath) || !PathSafety.IsUnderDirectory(directory, partPath))
        {
            await peer.SendAsync(Envelope.Create("file.reject", new FileRejectPayload
            {
                TransferId = offer.TransferId,
                Reason = "bad_name"
            }), cancellationToken);
            return;
        }
        var session = new TransferSession
        {
            Id = offer.TransferId,
            PeerId = peer.DeviceId ?? "",
            Direction = "to_pc",
            OriginalName = offer.Name,
            SafeName = unique,
            FinalPath = finalPath,
            PartPath = partPath,
            Size = offer.Size,
            Sha256 = offer.Sha256.ToLowerInvariant(),
            Mime = string.IsNullOrWhiteSpace(offer.Mime) ? "application/octet-stream" : offer.Mime,
            State = "accepted",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        _transfers[offer.TransferId] = session;
        RaiseTransfer(Notice(session, peer.Name));
        await peer.SendAsync(Envelope.Create("file.accept", new FileAcceptPayload
        {
            TransferId = offer.TransferId,
            Mode = "put",
            RelativePath = "/v1/files/" + offer.TransferId,
            ChunkPathTemplate = "/v1/files/" + offer.TransferId + "/chunks/{index}",
            ChunkSize = ProtocolConstants.ChunkSize,
            ResumeOffset = 0
        }), cancellationToken);
    }

    private async Task HandlePut(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (!Authorize(context, out var device))
        {
            await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }
        if (!_transfers.TryGetValue(id, out var session) || session.PeerId != device.DeviceId)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "unknown_transfer");
            return;
        }
        if (session.Direction != "to_pc")
        {
            await WriteError(context, StatusCodes.Status409Conflict, "bad_direction");
            return;
        }
        var headerSha = HeaderSha(context);
        if (!headerSha.Equals(session.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "bad_hash");
            return;
        }
        if (session.State == "completed" && session.FinalPath != null && File.Exists(session.FinalPath))
        {
            await WriteOk(context, session);
            return;
        }
        if (session.State != "accepted")
        {
            await WriteError(context, StatusCodes.Status409Conflict, "bad_state");
            return;
        }
        if (context.Request.ContentLength is long length && length != session.Size)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "bad_size");
            return;
        }
        session.State = "transferring";
        var peer = FindSocket(session.PeerId);
        RaiseTransfer(Notice(session, peer?.Name));
        Directory.CreateDirectory(_settings.IncomingDirectory);
        try
        {
            string hash;
            await using (var part = new FileStream(session.PartPath!, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var lastReport = 0L;
                hash = await Hashing.CopyAndHashAsync(context.Request.Body, part, session.Size, context.RequestAborted, new Progress<long>(sent =>
                {
                    session.ReceivedBytes = sent;
                    if (sent - lastReport >= 256 * 1024 || sent == session.Size)
                    {
                        lastReport = sent;
                        RaiseTransfer(Notice(session, peer?.Name));
                    }
                }));
                await part.FlushAsync(context.RequestAborted);
            }
            session.ReceivedBytes = new FileInfo(session.PartPath!).Length;
            if (session.ReceivedBytes != session.Size || !hash.Equals(session.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AtomicFile.TryDelete(session.PartPath!);
                session.State = "failed";
                session.Error = "hash_mismatch";
                session.Done.TrySetResult(new SendResult(false, "hash_mismatch", null));
                if (peer != null)
                    await peer.SendAsync(Envelope.Create("file.error", new FileErrorPayload { TransferId = id, Reason = "hash_mismatch" }), context.RequestAborted);
                RaiseTransfer(Notice(session, peer?.Name));
                await WriteError(context, StatusCodes.Status400BadRequest, "hash_mismatch");
                return;
            }
            AtomicFile.CommitVerified(session.PartPath!, session.FinalPath!, session.Sha256);
            session.State = "completed";
            session.ReceivedBytes = session.Size;
            var finished = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            session.FinishedAt = finished;
            session.Done.TrySetResult(new SendResult(true, "completed", session.FinalPath));
            if (peer != null)
                await peer.SendAsync(Envelope.Create("file.complete", new FileCompletePayload
                {
                    TransferId = id,
                    Sha256 = session.Sha256,
                    Name = session.SafeName
                }), context.RequestAborted);
            RaiseTransfer(Notice(session, peer?.Name));
            await WriteOk(context, session);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IntegrityException)
        {
            AtomicFile.TryDelete(session.PartPath!);
            session.State = "failed";
            session.Error = "io_error";
            session.Done.TrySetResult(new SendResult(false, "io_error", null));
            Log("put failed: " + ex.Message);
            await WriteError(context, StatusCodes.Status500InternalServerError, "io_error");
        }
    }

    private async Task HandleChunk(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        var index = int.Parse(context.Request.RouteValues["index"]?.ToString() ?? "-1", System.Globalization.CultureInfo.InvariantCulture);
        if (!Authorize(context, out var device))
        {
            await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }
        if (!_transfers.TryGetValue(id, out var session) || session.PeerId != device.DeviceId || session.Direction != "to_pc")
        {
            await WriteError(context, StatusCodes.Status404NotFound, "unknown_transfer");
            return;
        }
        if (index < 0)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "bad_chunk");
            return;
        }
        var chunkSha = HeaderSha(context);
        if (!Hashing.IsSha256Hex(chunkSha))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "bad_hash");
            return;
        }
        await using var buffer = new MemoryStream();
        await Hashing.CopyAndHashAsync(context.Request.Body, buffer, ProtocolConstants.ChunkSize, context.RequestAborted);
        var data = buffer.ToArray();
        var actual = Hashing.Sha256Hex(data);
        if (!actual.Equals(chunkSha, StringComparison.OrdinalIgnoreCase))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "hash_mismatch");
            return;
        }
        var offset = (long)index * ProtocolConstants.ChunkSize;
        if (offset > session.Size || offset + data.Length > session.Size)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "bad_chunk");
            return;
        }
        var last = offset + data.Length == session.Size;
        if (!last && data.Length != ProtocolConstants.ChunkSize)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "bad_chunk");
            return;
        }
        lock (session.Gate)
        {
            if (offset == session.ReceivedBytes)
            {
                Directory.CreateDirectory(_settings.IncomingDirectory);
                session.PartPath ??= Path.Combine(_settings.IncomingDirectory, "." + id + ".part");
                var mode = session.ReceivedBytes == 0 ? FileMode.Create : FileMode.Append;
                using var part = new FileStream(session.PartPath, mode, FileAccess.Write, FileShare.None);
                part.Write(data);
                session.ReceivedBytes += data.Length;
                session.LastChunkHash = actual;
                session.LastChunkLength = data.Length;
                session.State = "transferring";
            }
            else if (offset + data.Length == session.ReceivedBytes && session.LastChunkHash == actual)
            {
                // idempotent retry of the last chunk
            }
            else
            {
                context.Items["conflict"] = session.ReceivedBytes;
            }
        }
        if (context.Items.ContainsKey("conflict"))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "offset_conflict", receivedBytes = session.ReceivedBytes }, ProtocolJson.Options);
            return;
        }
        if (session.ReceivedBytes == session.Size && session.State != "completed")
        {
            try
            {
                var assembled = Hashing.Sha256File(session.PartPath!);
                if (!assembled.Equals(session.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    AtomicFile.TryDelete(session.PartPath!);
                    session.State = "failed";
                    session.Error = "hash_mismatch";
                    await WriteError(context, StatusCodes.Status400BadRequest, "hash_mismatch");
                    return;
                }
                AtomicFile.CommitVerified(session.PartPath!, session.FinalPath!, session.Sha256);
                session.State = "completed";
                session.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var peer = FindSocket(session.PeerId);
                if (peer != null)
                    await peer.SendAsync(Envelope.Create("file.complete", new FileCompletePayload
                    {
                        TransferId = id,
                        Sha256 = session.Sha256,
                        Name = session.SafeName
                    }), context.RequestAborted);
                RaiseTransfer(Notice(session, peer?.Name));
            }
            catch (Exception ex) when (ex is IOException or IntegrityException)
            {
                session.State = "failed";
                await WriteError(context, StatusCodes.Status500InternalServerError, "io_error");
                Log("chunk commit: " + ex.Message);
                return;
            }
        }
        await WriteOk(context, session);
    }

    private async Task HandleGet(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (!Authorize(context, out var device))
        {
            await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }
        if (!_transfers.TryGetValue(id, out var session) || session.PeerId != device.DeviceId || session.Direction != "to_phone")
        {
            await WriteError(context, StatusCodes.Status404NotFound, "unknown_transfer");
            return;
        }
        if (session.SourcePath == null || !File.Exists(session.SourcePath))
        {
            await WriteError(context, StatusCodes.Status404NotFound, "missing");
            return;
        }
        if (session.State is not ("offered" or "accepted" or "transferring" or "awaiting_confirm"))
        {
            await WriteError(context, StatusCodes.Status409Conflict, "bad_state");
            return;
        }
        session.State = "transferring";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = session.Size;
        context.Response.Headers[ProtocolConstants.HeaderContentSha256] = session.Sha256;
        await using var source = File.OpenRead(session.SourcePath);
        var buffer = new byte[81920];
        long sent = 0;
        var last = 0L;
        var peer = FindSocket(session.PeerId);
        while (true)
        {
            var read = await source.ReadAsync(buffer, context.RequestAborted);
            if (read == 0) break;
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            sent += read;
            session.BytesObserved = sent;
            if (sent - last >= 256 * 1024 || sent == session.Size)
            {
                last = sent;
                RaiseTransfer(Notice(session, peer?.Name));
                if (peer != null)
                {
                    try
                    {
                        await peer.SendAsync(Envelope.Create("file.progress", new FileProgressPayload
                        {
                            TransferId = id,
                            BytesTransferred = sent,
                            Total = session.Size
                        }), context.RequestAborted);
                    }
                    catch (Exception) { }
                }
            }
        }
        session.State = "awaiting_confirm";
        RaiseTransfer(Notice(session, peer?.Name));
    }

    private async Task HandleStatus(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (!Authorize(context, out var device))
        {
            await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }
        if (!_transfers.TryGetValue(id, out var session) || session.PeerId != device.DeviceId)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "unknown_transfer");
            return;
        }
        await context.Response.WriteAsJsonAsync(new
        {
            ok = true,
            transferId = session.Id,
            state = session.State,
            receivedBytes = session.Direction == "to_pc" ? session.ReceivedBytes : session.BytesObserved,
            totalBytes = session.Size,
            sha256 = session.Sha256
        }, ProtocolJson.Options);
    }

    private bool Authorize(HttpContext context, out TrustedDevice device)
    {
        device = null!;
        var deviceIdRaw = context.Request.Headers[ProtocolConstants.HeaderDeviceId].ToString();
        var timestampRaw = context.Request.Headers[ProtocolConstants.HeaderTimestamp].ToString();
        var nonce = context.Request.Headers[ProtocolConstants.HeaderNonce].ToString();
        var signatureRaw = context.Request.Headers[ProtocolConstants.HeaderSignature].ToString();
        if (!Guid.TryParse(deviceIdRaw, out var guid)) return false;
        var deviceId = guid.ToString("D");
        if (!long.TryParse(timestampRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timestamp)) return false;
        if (!RequestAuth.NonceFormatOk(nonce)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!RequestAuth.TimestampFresh(timestamp, now)) return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureRaw); }
        catch (FormatException) { return false; }
        if (signature.Length is < 64 or > 120) return false;
        var stored = _devices.FindUsable(deviceId);
        if (stored == null) return false;
        if (!RequestAuth.TryImportP256Public(stored.PublicKeySpki, out var key) || key == null) return false;
        using (key)
        {
            var contentSha = HeaderSha(context);
            var canonical = RequestAuth.Canonical(timestamp, nonce, context.Request.Method, context.Request.Path.Value ?? "/", contentSha);
            if (!RequestAuth.Verify(key, canonical, signature)) return false;
        }
        if (!_nonces.TryUse(deviceId, nonce, now)) return false;
        device = stored;
        return true;
    }

    private static string HeaderSha(HttpContext context)
        => context.Request.Headers[ProtocolConstants.HeaderContentSha256].ToString().Trim().ToLowerInvariant();

    private void Register(PeerSocket peer)
    {
        if (peer.DeviceId == null) return;
        if (_sockets.TryGetValue(peer.DeviceId, out var previous) && !ReferenceEquals(previous, peer))
        {
            _ = previous.CloseAsync();
        }
        _sockets[peer.DeviceId] = peer;
        _current = peer;
        Touch(peer);
        RaisePeer(new PeerPresence(peer.DeviceId, peer.Name, true));
    }

    private void Touch(PeerSocket peer)
    {
        if (peer.DeviceId == null) return;
        try { _devices.Touch(peer.DeviceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
        catch (Exception ex) { Log("touch: " + ex.Message); }
    }

    private PeerSocket? FindSocket(string? deviceId)
        => deviceId != null && _sockets.TryGetValue(deviceId, out var socket) ? socket : null;

    private Envelope Hello() => Envelope.Create("hello", new HelloPayload
    {
        DeviceId = _options.DeviceId,
        DeviceName = _settings.DeviceName,
        Protocol = ProtocolConstants.Version,
        Role = "pc"
    });

    private async Task SendReject(PeerSocket peer, string reason)
        => await peer.SendAsync(Envelope.Create("pair.reject", new PairRejectPayload { Reason = reason }), CancellationToken.None);

    private async Task SendError(PeerSocket peer, string reason, string? detail)
        => await peer.SendAsync(Envelope.Create("error", new ProtocolErrorPayload { Reason = reason, Detail = detail }), CancellationToken.None);

    private async Task Ack(PeerSocket peer, string refId, bool ok, string? reason)
        => await peer.SendAsync(Envelope.Create("clipboard.ack", new ClipboardAckPayload { RefId = refId, Ok = ok, Reason = reason }), CancellationToken.None);

    private void MarkAccepted(string? transferId)
    {
        if (transferId == null || !_transfers.TryGetValue(transferId, out var session)) return;
        if (session.State == "offered") session.State = "accepted";
        RaiseTransfer(Notice(session, FindSocket(session.PeerId)?.Name));
    }

    private void CompleteSession(string? transferId, string? sha, string? name)
    {
        if (transferId == null || !_transfers.TryGetValue(transferId, out var session)) return;
        if (!string.IsNullOrWhiteSpace(sha) && !sha.Equals(session.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            FailSession(transferId, "failed", "hash_mismatch");
            return;
        }
        session.State = "completed";
        session.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        session.ReceivedBytes = session.Size;
        session.Done.TrySetResult(new SendResult(true, "completed", name));
        RaiseTransfer(Notice(session, FindSocket(session.PeerId)?.Name));
    }

    private void FailSession(string? transferId, string state, string reason)
    {
        if (transferId == null || !_transfers.TryGetValue(transferId, out var session)) return;
        session.State = state;
        session.Error = reason;
        session.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        session.Done.TrySetResult(new SendResult(false, reason, null));
        RaiseTransfer(Notice(session, FindSocket(session.PeerId)?.Name));
    }

    private void CancelFromPeer(string transferId)
    {
        if (!_transfers.TryGetValue(transferId, out var session)) return;
        session.State = "cancelled";
        session.Error = "cancelled";
        session.Cancel.Cancel();
        if (session.PartPath != null) AtomicFile.TryDelete(session.PartPath);
        session.Done.TrySetResult(new SendResult(false, "cancelled", null));
        RaiseTransfer(Notice(session, FindSocket(session.PeerId)?.Name));
    }

    private int ActiveCount()
    {
        var count = 0;
        foreach (var session in _transfers.Values)
            if (session.State is "offered" or "accepted" or "transferring" or "awaiting_confirm") count++;
        return count;
    }

    private long FreeBytes()
    {
        if (_options.FreeBytesOverride != null) return _options.FreeBytesOverride();
        try
        {
            var directory = _settings.IncomingDirectory;
            Directory.CreateDirectory(directory);
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return 0;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            Log("free space: " + ex.Message);
            return 0;
        }
    }

    private TransferNotice Notice(TransferSession session, string? peerName)
        => new(
            session.Id,
            session.Direction == "to_phone" ? "out" : "in",
            "file",
            session.PeerId,
            peerName,
            session.SafeName.Length > 0 ? session.SafeName : session.OriginalName,
            session.Size,
            session.Direction == "to_pc" ? session.ReceivedBytes : session.BytesObserved,
            session.State,
            session.Error,
            session.FinalPath ?? session.SourcePath,
            session.Sha256,
            null,
            false);

    private static async Task WriteError(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { ok = false, error }, ProtocolJson.Options);
    }

    private static async Task WriteOk(HttpContext context, TransferSession session)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new
        {
            ok = true,
            transferId = session.Id,
            sha256 = session.Sha256,
            name = session.SafeName,
            bytes = session.ReceivedBytes,
            state = session.State
        }, ProtocolJson.Options);
    }

    private async Task PingLoop(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var socket in _sockets.Values.ToArray())
                {
                    try { await socket.SendAsync(Envelope.Create("ping", new { }), cancellationToken); }
                    catch (Exception) { await socket.CloseAsync(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private void RaiseTransfer(TransferNotice notice)
    {
        try { TransferChanged?.Invoke(this, notice); }
        catch (Exception ex) { Log("transfer listener: " + ex.Message); }
    }

    private void RaisePeer(PeerPresence presence)
    {
        try { PeerPresenceChanged?.Invoke(this, presence); }
        catch (Exception ex) { Log("peer listener: " + ex.Message); }
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private enum PairOutcome { Failed, Retry, Paired }

    private sealed class PeerSocket
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        public PeerSocket(WebSocket socket) { Socket = socket; }
        public WebSocket Socket { get; }
        public string? DeviceId { get; set; }
        public string Name { get; set; } = "";
        public bool Authenticated { get; set; }
        public long LastSeenMs { get; set; }

        public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(envelope.ToJson());
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Socket.State != WebSocketState.Open) return;
                // A completed send resumes inline, still inside Kestrel's transport write.
                // The next SendAsync then waits on that same lock and never returns.
                // CancellationToken.None: aborting a send with the request token deadlocks
                // Kestrel, because abort waits for the send that the token just cancelled.
                await Task.Yield();
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _send.Release(); }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // CloseAsync waits for the peer's handshake. A phone that already
                    // went away, or a client that only reads data frames, would block
                    // this for the 30s default. Close the output, then abort.
                    var close = Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    await Task.WhenAny(close, Task.Delay(500)).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
            try { Socket.Abort(); } catch (Exception) { }
            try { Socket.Dispose(); } catch (Exception) { }
        }
    }

    private sealed class TransferSession
    {
        public required string Id { get; init; }
        public required string PeerId { get; init; }
        public required string Direction { get; init; }
        public string OriginalName { get; set; } = "";
        public string SafeName { get; set; } = "";
        public string? SourcePath { get; set; }
        public string? FinalPath { get; set; }
        public string? PartPath { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public string Mime { get; set; } = "application/octet-stream";
        public string State { get; set; } = "offered";
        public long ReceivedBytes { get; set; }
        public long BytesObserved { get; set; }
        public string? LastChunkHash { get; set; }
        public int LastChunkLength { get; set; }
        public string? Error { get; set; }
        public long CreatedAt { get; set; }
        public long? FinishedAt { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource<SendResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public object Gate { get; } = new();
    }
}
