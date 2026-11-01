using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanBridge.Core;
using LanBridge.Server;
using Microsoft.Win32;
using QRCoder;

namespace LanBridge.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _clock;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _ready;

    public MainViewModel(AppHost host)
    {
        _host = host;
        IncomingDirectory = host.Settings.IncomingDirectory;
        DeviceName = host.Settings.DeviceName;
        AutoReceive = host.Settings.AutoReceive;
        ClipboardSync = host.Settings.ClipboardSyncEnabled;
        ClipboardPaused = host.Settings.ClipboardPaused;
        SaveTextHistory = host.Settings.SaveTextHistory;
        FilterSensitive = host.Settings.FilterSensitive;
        RunAtStartup = host.Settings.RunAtStartup;
        SelectedPhoneId = host.Settings.SelectedPhoneId;
        HotkeySend = string.IsNullOrWhiteSpace(host.Settings.HotkeySend) ? HotkeyParser.DefaultSend : host.Settings.HotkeySend;
        HotkeySidebar = string.IsNullOrWhiteSpace(host.Settings.HotkeySidebar) ? HotkeyParser.DefaultSidebar : host.Settings.HotkeySidebar;
        SidebarPinned = host.Settings.SidebarPinned;
        HotkeyStatus = HotkeySend + " 发送剪贴板 · " + HotkeySidebar + " 打开侧栏";
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => TickPairing();
        host.FilesReady += _ => Application.Current.Dispatcher.InvokeAsync(SendQueuedAsync);
    }

    public ObservableCollection<TransferRow> Transfers { get; } = new();
    public ObservableCollection<DeviceRow> Devices { get; } = new();

    [ObservableProperty] private string _statusText = "正在启动…";
    [ObservableProperty] private string _detailText = "兰桥只在当前局域网里工作，不经过云端。";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _incomingDirectory = "";
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private bool _autoReceive;
    [ObservableProperty] private bool _clipboardSync;
    [ObservableProperty] private bool _clipboardPaused;
    [ObservableProperty] private bool _saveTextHistory;
    [ObservableProperty] private bool _filterSensitive;
    [ObservableProperty] private bool _runAtStartup;
    [ObservableProperty] private bool _pairingOpen;
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] private string _pairingHint = "";
    [ObservableProperty] private int _pairingSeconds;
    [ObservableProperty] private ImageSource? _qrImage;
    [ObservableProperty] private string _fingerprintText = "";

    public string EmptyHint => Transfers.Count == 0 ? "还没有传输记录。配对手机后，可以从这里发文件或一段文字。" : "";

    public async Task ActivateAsync()
    {
        for (var i = 0; i < 80 && _host.Server == null && _host.StartError == null; i++)
            await Task.Delay(50);
        for (var i = 0; i < 40 && !_host.StartupFinished && _host.StartError == null; i++)
            await Task.Delay(50);
        var server = _host.Server;
        if (server == null)
        {
            StatusText = "没有启动成功";
            DetailText = string.IsNullOrWhiteSpace(_host.StartError)
                ? "请查看 %LOCALAPPDATA%\\LanBridge\\lanbridge.log。"
                : _host.StartError;
            return;
        }
        server.PeerPresenceChanged += (_, presence) => OnUi(() => ApplyPeer(presence));
        server.TransferChanged += (_, notice) => OnUi(() => ApplyTransfer(notice));
        server.ClipboardReceived += (_, incoming) => OnUi(() => ApplyClipboard(incoming));
        FingerprintText = CertFingerprints.Group(server.Fingerprint);
        ReloadHistory();
        ReloadDevices();
        var current = server.CurrentPeer;
        if (current != null) ApplyPeer(current);
        else
        {
            StatusText = "未连接";
            DetailText = IdleDetail(server.BoundPort);
        }
        MaybeExplainFirewall();
        _ready = true;
        if (!string.IsNullOrEmpty(SelectedPhoneId) && _host.Settings.SelectedPhoneId != SelectedPhoneId)
        {
            _host.Settings.SelectedPhoneId = SelectedPhoneId;
            try { _host.SaveSettings(); } catch (Exception) { }
        }
        SendFileCommand.NotifyCanExecuteChanged();
        SendClipboardCommand.NotifyCanExecuteChanged();
        HotkeysChanged?.Invoke();
        await SendQueuedAsync();
    }

    [RelayCommand]
    private void StartPairing()
    {
        var server = _host.Server;
        if (server == null) return;
        var pin = server.OpenPairing();
        Pin = pin;
        PairingOpen = true;
        var host = MdnsAdvertiser.Preferred(MdnsAdvertiser.LocalIpv4());
        if (host == null)
        {
            PairingHint = "没有找到可用的局域网网卡（已跳过回环和虚拟网卡）。请确认电脑连的是 Wi-Fi 或网线，而不是只开着虚拟交换机。";
            QrImage = null;
        }
        else
        {
            var exp = server.PairingExpiresAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.AddSeconds(ProtocolConstants.PairingTtlSeconds).ToUnixTimeSeconds();
            var link = PairingLink.Build(host.ToString(), server.BoundPort, _host.Settings.DeviceId, server.Fingerprint, pin, exp);
            PairingHint = "手机搜索同一 Wi-Fi，或手动输入 " + host + ":" + server.BoundPort
                + "。服务类型是 _lanbridge._tcp。若搜不到，请允许防火墙放行 TCP " + server.BoundPort + " 和 UDP 5353。电脑随后还会再问一次是否允许。";
            QrImage = RenderQr(link);
        }
        TickPairing();
        _clock.Start();
        _host.Log("pairing shown");
    }

    [RelayCommand]
    private void CancelPairing()
    {
        _host.Server?.ClosePairing();
        PairingOpen = false;
        _clock.Stop();
        QrImage = null;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择要发到手机的文件", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        await SendPathsAsync(dialog.FileNames);
    }

    [RelayCommand]
    private async Task SendClipboardAsync()
    {
        var server = _host.Server;
        if (server == null) return;
        string text;
        try { text = System.Windows.Clipboard.GetText() ?? ""; }
        catch (Exception)
        {
            DetailText = "现在读不到剪贴板，请稍后再试。";
            return;
        }
        text = TextNormalizer.Normalize(text);
        if (text.Length == 0)
        {
            DetailText = "剪贴板里没有文字。图片暂不支持。";
            return;
        }
        if (TextNormalizer.ExceedsLimit(text))
        {
            DetailText = "文字超过 256 KB，没有发送。";
            return;
        }
        if (_host.Settings.FilterSensitive && SensitiveTextFilter.IsSensitive(text))
        {
            var allow = MessageBox.Show(
                "这段内容看起来像密码或密钥。确定要发到手机吗？",
                "敏感内容",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (allow != MessageBoxResult.Yes) return;
        }
        var target = TargetPhoneId();
        if (target == null)
        {
            DetailText = "请先选择一台手机。";
            _host.Notify("没有发送", "请先选择一台手机。");
            return;
        }
        var result = await server.PushClipboardToAsync(target, text, manual: true);
        _host.Guard.MarkSent(Hashing.Sha256Hex(text));
        var phoneName = Phones.FirstOrDefault(phone => phone.DeviceId == target)?.Name ?? "手机";
        DetailText = result.Ok ? "文字已发给「" + phoneName + "」。" : Describe(result.Status);
        if (!result.Ok) _host.Notify("没有发送", DetailText);
        if (result.Ok && _host.Settings.SaveTextHistory && !SensitiveTextFilter.IsSensitive(text))
            RememberClipboard("out", text, new PeerPresence(target, phoneName, true));
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择接收文件的文件夹", InitialDirectory = IncomingDirectory };
        if (dialog.ShowDialog() != true) return;
        IncomingDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { DetailText = "无法打开文件夹：" + ex.Message; }
    }

    [RelayCommand]
    private void Revoke(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        var answer = MessageBox.Show("解除后，这台手机必须重新配对才能再传文件。", "解除信任", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        _host.Server?.Revoke(deviceId);
        if (PhoneSettingsId == deviceId) PhoneSettingsId = "";
        if (SelectedPhoneId == deviceId) SelectedPhoneId = "";
        ReloadDevices();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _host.Database.Clear();
        Transfers.Clear();
        OnPropertyChanged(nameof(EmptyHint));
    }

    private bool CanSend() => _ready && IsOnline;

    partial void OnIsOnlineChanged(bool value)
    {
        SendFileCommand.NotifyCanExecuteChanged();
        SendClipboardCommand.NotifyCanExecuteChanged();
    }

    partial void OnIncomingDirectoryChanged(string value) => Persist(settings => settings.IncomingDirectory = value);
    partial void OnDeviceNameChanged(string value) => Persist(settings => settings.DeviceName = value);
    partial void OnAutoReceiveChanged(bool value) => Persist(settings => settings.AutoReceive = value);
    partial void OnClipboardSyncChanged(bool value) => Persist(settings => settings.ClipboardSyncEnabled = value);
    partial void OnClipboardPausedChanged(bool value) => Persist(settings => settings.ClipboardPaused = value);
    partial void OnSaveTextHistoryChanged(bool value) => Persist(settings => settings.SaveTextHistory = value);
    partial void OnFilterSensitiveChanged(bool value) => Persist(settings => settings.FilterSensitive = value);

    partial void OnRunAtStartupChanged(bool value)
    {
        if (!_ready) return;
        _host.Settings.RunAtStartup = value;
        try
        {
            _host.SaveSettings();
            StartupRegistration.Apply(value);
        }
        catch (Exception ex)
        {
            _host.Log("startup toggle: " + ex.Message);
            DetailText = "开机启动没有改成功：" + ex.Message;
        }
    }

    private void Persist(Action<AppSettings> change)
    {
        if (!_ready) return;
        change(_host.Settings);
        try { Directory.CreateDirectory(_host.Settings.IncomingDirectory); }
        catch (Exception) { }
        try { _host.SaveSettings(); }
        catch (Exception ex) { DetailText = "设置没有保存：" + ex.Message; }
    }

    private async Task SendQueuedAsync()
    {
        await _sendGate.WaitAsync();
        try
        {
            await SendQueuedCoreAsync();
        }
        finally { _sendGate.Release(); }
    }

    private async Task SendQueuedCoreAsync()
    {
        var paths = _host.TakeFiles();
        if (paths.Count == 0) return;
        if (!IsOnline)
        {
            DetailText = "手机还没连上，这 " + paths.Count + " 个文件会在连上后发送。";
            _host.HoldFiles(paths);
            return;
        }
        await SendPathsAsync(paths);
    }

    private async Task SendPathsAsync(IReadOnlyList<string> paths)
    {
        var server = _host.Server;
        if (server == null) return;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                DetailText = "找不到文件：" + path;
                continue;
            }
            var target = TargetPhoneId();
            if (target == null)
            {
                DetailText = "请先选择一台手机。";
                _host.HoldFiles(paths.Skip(paths.ToList().IndexOf(path)).ToList());
                return;
            }
            DetailText = "正在发送 " + Path.GetFileName(path) + " …";
            var result = await server.OfferFileToAsync(target, path);
            DetailText = result.Ok
                ? "「" + Path.GetFileName(path) + "」已发到手机。"
                : "「" + Path.GetFileName(path) + "」没有发出：" + Describe(result.Status);
        }
    }

    private void ApplyPeer(PeerPresence presence)
    {
        ReloadDevices();
        if (presence.Online) _ = SendQueuedAsync();
        OnPropertyChanged(nameof(EmptyHint));
    }

    private void ApplyTransfer(TransferNotice notice)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var visible = HistoryLabels.VisibleForPeer(notice.PeerId, SelectedPeerOrNull(), PeerCount());
        var row = Transfers.FirstOrDefault(item => item.Id == notice.Id);
        if (visible)
        {
            if (row == null)
            {
                row = new TransferRow { Id = notice.Id };
                Transfers.Insert(0, row);
            }
            FillRow(row, notice, created);
            while (Transfers.Count > 50) Transfers.RemoveAt(Transfers.Count - 1);
        }
        else if (row != null)
        {
            Transfers.Remove(row);
        }
        OnPropertyChanged(nameof(EmptyHint));
        var title = string.IsNullOrWhiteSpace(notice.Name) ? "未命名" : notice.Name;
        _host.Database.Upsert(new TransferRecord
        {
            Id = notice.Id,
            Direction = notice.Direction,
            Kind = notice.Kind,
            PeerId = notice.PeerId,
            PeerName = notice.PeerName,
            Name = title,
            Size = notice.Size,
            BytesDone = notice.BytesDone,
            Sha256 = notice.Sha256,
            LocalPath = notice.LocalPath,
            Status = notice.Status,
            Error = notice.Error,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FinishedAt = notice.Status is "completed" or "failed" or "rejected" or "cancelled"
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null
        });
        if (notice.Status == "completed" && notice.Direction == "in")
        {
            DetailText = "已收到「" + title + "」，保存在下载文件夹，没有打开它。";
            _host.Notify("文件已收到", title + " 已保存，没有打开。");
        }
    }

    private void ApplyClipboard(ClipboardIncoming incoming)
    {
        _host.Guard.MarkApplied(incoming.Sha256, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try { System.Windows.Clipboard.SetText(incoming.Text); }
        catch (Exception ex) { _host.Log("clipboard set: " + ex.Message); }
        DetailText = incoming.Sensitive ? "收到一段敏感文字，已放入剪贴板，但不会写入历史。" : "收到一段文字，已放入剪贴板。";
        _host.Notify("剪贴板", incoming.Sensitive ? "收到一段敏感文字，已放入剪贴板。" : "收到一段文字，已放入剪贴板。");
        if (_host.Settings.SaveTextHistory && !incoming.Sensitive)
            RememberClipboard("in", incoming.Text, new PeerPresence(incoming.PeerId, incoming.PeerName, true));
    }

    private void RememberClipboard(string direction, string text, PeerPresence? peer)
    {
        var preview = text.Replace('\n', ' ').Replace('\r', ' ');
        if (preview.Length > 80) preview = preview[..80] + "…";
        var record = new TransferRecord
        {
            Id = Guid.NewGuid().ToString("D"),
            Direction = direction,
            Kind = "clipboard",
            PeerId = peer?.DeviceId,
            PeerName = peer?.Name,
            Name = preview,
            Size = System.Text.Encoding.UTF8.GetByteCount(text),
            BytesDone = System.Text.Encoding.UTF8.GetByteCount(text),
            Status = "completed",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TextPreview = text
        };
        _host.Database.Upsert(record);
        if (HistoryLabels.VisibleForPeer(record.PeerId, SelectedPeerOrNull(), PeerCount()))
        {
            Transfers.Insert(0, RowFrom(record));
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    private void ReloadHistory()
    {
        Transfers.Clear();
        var selected = SelectedPeerOrNull();
        var count = PeerCount();
        foreach (var record in _host.Database.Recent(80))
        {
            if (!HistoryLabels.VisibleForPeer(record.PeerId, selected, count)) continue;
            Transfers.Add(RowFrom(record));
        }
        OnPropertyChanged(nameof(EmptyHint));
    }

    private void ReloadDevices()
    {
        Devices.Clear();
        var online = _host.Server?.CurrentPeer?.DeviceId;
        foreach (var device in _host.Database.ListActive())
        {
            Devices.Add(new DeviceRow
            {
                DeviceId = device.DeviceId,
                Title = device.Name + (device.DeviceId == online ? " · 在线" : " · 离线")
            });
        }
        RefreshPhones();
    }

    private string IdleDetail(int port)
    {
        var host = MdnsAdvertiser.Preferred(MdnsAdvertiser.LocalIpv4());
        if (host == null)
            return "没有找到局域网地址。虚拟网卡和回环不会用来广播。请把电脑连上 Wi-Fi 后再配对。";
        return "这台电脑的地址是 " + host + ":" + port + "。手机可以搜索，也可以手动输入这个地址。防火墙需要放行 TCP " + port + " 和 UDP 5353。";
    }

    private void MaybeExplainFirewall()
    {
        var note = _host.FirewallNote;
        if (string.IsNullOrWhiteSpace(note) || FirewallSetup.AlreadyNoted()) return;
        FirewallSetup.MarkShown();
        MessageBox.Show(
            note,
            "需要放行防火墙",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void TickPairing()
    {
        var expires = _host.Server?.PairingExpiresAt;
        if (expires == null)
        {
            PairingOpen = false;
            _clock.Stop();
            return;
        }
        PairingSeconds = Math.Max(0, (int)(expires.Value - DateTimeOffset.Now).TotalSeconds);
        if (PairingSeconds == 0) CancelPairing();
    }

    private static ImageSource RenderQr(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private void FillRow(TransferRow row, TransferNotice notice, long created)
    {
        var exists = !string.IsNullOrWhiteSpace(notice.LocalPath) && File.Exists(notice.LocalPath);
        row.PeerId = notice.PeerId;
        row.Kind = notice.Kind;
        row.Title = string.IsNullOrWhiteSpace(notice.Name) ? "未命名" : notice.Name;
        row.Origin = HistoryLabels.OriginOnPc(notice.Direction);
        row.When = HistoryLabels.When(created);
        row.Meta = (notice.Kind == "clipboard" ? "文字" : "文件") + " · " + StatusLabel(notice.Status) + " · " + ByteSize.Format(notice.Size);
        row.LocalPath = notice.LocalPath;
        row.CopyText = notice.Kind == "clipboard" ? notice.TextPreview ?? notice.Name : "";
        row.CanCopy = HistoryLabels.CanCopy(notice.Kind) && !string.IsNullOrWhiteSpace(row.CopyText);
        row.CanOpen = HistoryLabels.CanOpenFile(notice.Kind, notice.LocalPath, exists);
    }

    private TransferRow RowFrom(TransferRecord record)
    {
        var title = record.Kind == "clipboard"
            ? Preview(record.TextPreview ?? record.Name)
            : (string.IsNullOrWhiteSpace(record.Name) ? "未命名" : record.Name);
        var copy = record.Kind == "clipboard" ? (record.TextPreview ?? record.Name) : "";
        var exists = !string.IsNullOrWhiteSpace(record.LocalPath) && File.Exists(record.LocalPath);
        return new TransferRow
        {
            Id = record.Id,
            PeerId = record.PeerId,
            Kind = record.Kind,
            Title = title,
            Origin = HistoryLabels.OriginOnPc(record.Direction),
            When = HistoryLabels.When(record.CreatedAt),
            Meta = (record.Kind == "clipboard" ? "文字" : "文件") + " · " + StatusLabel(record.Status),
            LocalPath = record.LocalPath,
            CopyText = copy,
            CanCopy = HistoryLabels.CanCopy(record.Kind) && !string.IsNullOrWhiteSpace(copy),
            CanOpen = HistoryLabels.CanOpenFile(record.Kind, record.LocalPath, exists)
        };
    }

    private static string Preview(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length > 80 ? flat[..80] + "…" : flat;
    }

    private static string StatusLabel(string status) => status switch
    {
        "offered" => "等待对方",
        "accepted" or "transferring" => "传输中",
        "awaiting_confirm" => "等待确认",
        "completed" or "sent" => "完成",
        "rejected" => "已拒绝",
        "failed" => "失败",
        "cancelled" => "已取消",
        _ => status
    };

    private static string Describe(string status) => status switch
    {
        "not_connected" => "手机还没连上。",
        "empty" => "没有可发送的内容。",
        "too_large" => "超过大小限制。",
        "missing" => "文件不存在。",
        "cancelled" => "已取消。",
        "sensitive" => "对方拦截了这段敏感内容。",
        "hash_mismatch" => "校验不一致，已丢弃。",
        "sent_unconfirmed" => "已发出，手机还没有确认。",
        _ => status
    };
}

public partial class TransferRow : ObservableObject
{
    public string Id { get; set; } = "";
    public string? PeerId { get; set; }
    public string Kind { get; set; } = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _origin = "";
    [ObservableProperty] private string _when = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _copyText = "";
    [ObservableProperty] private string? _localPath;
    [ObservableProperty] private bool _canCopy;
    [ObservableProperty] private bool _canOpen;
}

public sealed class DeviceRow
{
    public string DeviceId { get; set; } = "";
    public string Title { get; set; } = "";
}
