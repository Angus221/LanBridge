using System.IO;
using System.Net;
using System.Windows.Threading;
using LanBridge.Core;
using LanBridge.Server;
using LanBridge.Storage;

namespace LanBridge.App;

public sealed class AppHost : IAsyncDisposable
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly LanBridgeDatabase _database;
    private readonly ClipboardGuard _guard = new();
    private readonly Queue<string> _pendingFiles = new();
    private readonly object _fileGate = new();
    private MdnsAdvertiser? _mdns;
    private ClipboardWatcher? _watcher;
    private DispatcherTimer? _clipboardDebounce;
    private bool _exit;

    public AppHost()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _settingsStore = new JsonSettingsStore(AppPaths.SettingsPath);
        Settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(Settings.DeviceId)) Settings.DeviceId = Guid.NewGuid().ToString("D");
        if (string.IsNullOrWhiteSpace(Settings.DeviceName)) Settings.DeviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(Settings.IncomingDirectory)) Settings.IncomingDirectory = AppPaths.DefaultIncoming();
        if (Settings.Port is < 1 or > 65535) Settings.Port = ProtocolConstants.DefaultPort;
        _settingsStore.Save(Settings);
        _database = new LanBridgeDatabase(AppPaths.DatabasePath);
        Directory.CreateDirectory(Settings.IncomingDirectory);
    }

    public AppSettings Settings { get; }
    public LanBridgeDatabase Database => _database;
    public BridgeServer? Server { get; private set; }
    public ClipboardGuard Guard => _guard;
    public string? StartError { get; private set; }
    public string FirewallNote { get; private set; } = "";
    public bool StartupFinished { get; private set; }
    public bool ExitRequested => _exit;
    public event Action<IReadOnlyList<string>>? FilesReady;
    public event Action<string, string>? Notification;

    public void Log(string message)
    {
        try
        {
            File.AppendAllText(AppPaths.LogPath, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
        }
        catch (Exception) { }
    }

    public void SaveSettings() => _settingsStore.Save(Settings);

    public void Start()
    {
        _ = Task.Run(StartAsync);
    }

    public void EnqueueFiles(IReadOnlyList<string> paths)
    {
        HoldFiles(paths);
        FilesReady?.Invoke(paths);
    }

    public void HoldFiles(IReadOnlyList<string> paths)
    {
        lock (_fileGate)
        {
            foreach (var path in paths) _pendingFiles.Enqueue(path);
        }
    }

    public void Notify(string title, string body) => Notification?.Invoke(title, body);

    public IReadOnlyList<string> TakeFiles()
    {
        lock (_fileGate)
        {
            var list = _pendingFiles.ToList();
            _pendingFiles.Clear();
            return list;
        }
    }

    public void RequestExit() => _exit = true;

    public async ValueTask DisposeAsync()
    {
        try { _clipboardDebounce?.Stop(); } catch (Exception) { }
        try { _watcher?.Dispose(); } catch (Exception) { }
        _mdns?.Dispose();
        if (Server != null) await Server.DisposeAsync();
        _database.Dispose();
    }

    private async Task StartAsync()
    {
        try
        {
            var certificate = CertStore.LoadOrCreate(AppPaths.CertificatePath, Settings.DeviceId);
            var server = new BridgeServer(new BridgeServerOptions
            {
                Certificate = certificate,
                DeviceId = Settings.DeviceId,
                Settings = Settings,
                Devices = _database,
                BindAddress = IPAddress.Any,
                Port = Settings.Port,
                Log = Log
            }, new UiConsent());
            Server = server;
            await server.StartAsync().ConfigureAwait(false);
            Log("listening " + server.BoundPort + " fp " + server.Fingerprint);
            try { FirewallNote = FirewallSetup.Ensure(server.BoundPort, Environment.ProcessPath, Log); }
            catch (Exception ex) { Log("firewall: " + ex.Message); }
            _mdns = new MdnsAdvertiser(
                () => (Settings.DeviceId, Settings.DeviceName, server.BoundPort, server.Fingerprint),
                Log);
            _mdns.Start();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    _watcher = new ClipboardWatcher(ScheduleClipboard);
                    _watcher.Start();
                    _clipboardDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    _clipboardDebounce.Tick += (_, _) =>
                    {
                        _clipboardDebounce.Stop();
                        _ = PushClipboardFromWatcherAsync();
                    };
                });
            }
            try { SendToRegistration.Install(); }
            catch (Exception ex) { Log("sendto: " + ex.Message); }
            try { StartupRegistration.Apply(Settings.RunAtStartup); }
            catch (Exception ex) { Log("startup: " + ex.Message); }
            StartupFinished = true;
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
            StartupFinished = true;
            Log("start failed: " + ex);
        }
    }

    private void ScheduleClipboard()
    {
        if (_clipboardDebounce == null) return;
        _clipboardDebounce.Stop();
        _clipboardDebounce.Start();
    }

    private async Task PushClipboardFromWatcherAsync()
    {
        var server = Server;
        if (server == null) return;
        if (!Settings.ClipboardSyncEnabled || Settings.ClipboardPaused) return;
        string text;
        try { text = System.Windows.Clipboard.GetText() ?? ""; }
        catch (Exception) { return; }
        text = TextNormalizer.Normalize(text);
        if (text.Length == 0 || TextNormalizer.ExceedsLimit(text)) return;
        var hash = Hashing.Sha256Hex(text);
        if (!_guard.ShouldAutoSend(hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
        if (Settings.FilterSensitive && SensitiveTextFilter.IsSensitive(text))
        {
            Log("auto clipboard skipped: sensitive");
            Notify("没有自动发送", "剪贴板里像是密码或密钥。需要的话，打开兰桥后手动发送。");
            return;
        }
        var result = await server.PushClipboardAsync(text, manual: false).ConfigureAwait(false);
        if (result.Ok) _guard.MarkSent(hash);
        Log("auto clipboard " + result.Status);
    }
}

public sealed class UiConsent : IUserConsent
{
    public Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken cancellationToken)
        => Ask(
            "允许这台手机配对？",
            prompt.DeviceName + "（" + (string.IsNullOrWhiteSpace(prompt.Platform) ? "手机" : prompt.Platform) + "）请求连接这台电脑。\n\n密钥指纹：\n"
            + prompt.KeyFingerprint + "\n\n只有你本人正在手机上操作时才要点“是”。",
            cancellationToken);

    public Task<bool> ConfirmReceiveAsync(FilePrompt prompt, CancellationToken cancellationToken)
        => Ask(
            "接收这个文件？",
            prompt.PeerName + " 想发来「" + prompt.Name + "」（" + ByteSize.Format(prompt.Size) + "）。\n\n文件会保存到下载目录，不会被打开或运行。",
            cancellationToken);

    private static Task<bool> Ask(string title, string body, CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.FromResult(false);
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(false);
        return dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            var answer = System.Windows.MessageBox.Show(
                body,
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            return answer == System.Windows.MessageBoxResult.Yes;
        }).Task;
    }
}
