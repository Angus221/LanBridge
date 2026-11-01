using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanBridge.Core;

namespace LanBridge.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<PhoneCardModel> Phones { get; } = new();

    [ObservableProperty] private string _selectedPhoneId = "";
    [ObservableProperty] private bool _settingsOpen;
    [ObservableProperty] private bool _sidebarPinned;
    [ObservableProperty] private string _phoneSettingsId = "";
    [ObservableProperty] private string _hotkeySend = HotkeyParser.DefaultSend;
    [ObservableProperty] private string _hotkeySidebar = HotkeyParser.DefaultSidebar;
    [ObservableProperty] private string _hotkeyStatus = "";

    public event Action? HotkeysChanged;

    public string PinLabel => SidebarPinned ? "已固定" : "固定";
    public string PhoneSettingsName => Phones.FirstOrDefault(phone => phone.DeviceId == PhoneSettingsId)?.Name ?? "";
    public bool PhoneSettingsOpen => !string.IsNullOrEmpty(PhoneSettingsId);

    partial void OnSidebarPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(PinLabel));
        if (!_ready) return;
        _host.Settings.SidebarPinned = value;
        try { _host.SaveSettings(); }
        catch (Exception ex) { DetailText = "设置没有保存：" + ex.Message; }
    }

    partial void OnPhoneSettingsIdChanged(string value)
    {
        OnPropertyChanged(nameof(PhoneSettingsName));
        OnPropertyChanged(nameof(PhoneSettingsOpen));
    }

    partial void OnHotkeySendChanged(string value) => CommitHotkeys();
    partial void OnHotkeySidebarChanged(string value) => CommitHotkeys();

    public void SetHotkeyStatus(string text) => HotkeyStatus = text;

    [RelayCommand]
    private void ToggleSettings() => SettingsOpen = !SettingsOpen;

    [RelayCommand]
    private void TogglePin() => SidebarPinned = !SidebarPinned;

    [RelayCommand]
    private void SelectPhone(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        SelectedPhoneId = deviceId;
        _host.Settings.SelectedPhoneId = deviceId;
        foreach (var phone in Phones) phone.Selected = phone.DeviceId == deviceId;
        if (_ready)
        {
            try { _host.SaveSettings(); }
            catch (Exception ex) { DetailText = "设置没有保存：" + ex.Message; }
        }
        UpdateConnectionText();
        ReloadHistory();
    }

    [RelayCommand]
    private void OpenPhoneSettings(string? deviceId)
    {
        PhoneSettingsId = PhoneSettingsId == deviceId ? "" : deviceId ?? "";
    }

    [RelayCommand]
    private void CopyHistory(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            DetailText = "已复制到剪贴板。";
        }
        catch (Exception ex) { DetailText = "无法复制：" + ex.Message; }
    }

    [RelayCommand]
    private void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { DetailText = "无法打开文件：" + ex.Message; }
    }

    private void RefreshPhones()
    {
        var online = new HashSet<string>(StringComparer.Ordinal);
        foreach (var peer in _host.Server?.ConnectedPeers() ?? Array.Empty<LanBridge.Server.PeerPresence>())
            online.Add(peer.DeviceId);
        var previous = SelectedPhoneId;
        Phones.Clear();
        foreach (var device in _host.Database.ListActive())
        {
            var card = new PhoneCardModel
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Online = online.Contains(device.DeviceId),
                Selected = device.DeviceId == previous
            };
            card.ClipboardSync = _host.Settings.ClipboardEnabledFor(device.DeviceId);
            card.SyncChanged = OnPhoneSyncChanged;
            Phones.Add(card);
        }
        if (Phones.Count > 0 && Phones.All(phone => !phone.Selected))
        {
            var fallback = Phones.FirstOrDefault(phone => phone.DeviceId == previous) ?? Phones[0];
            fallback.Selected = true;
            SelectedPhoneId = fallback.DeviceId;
        }
        if (Phones.Count == 0) SelectedPhoneId = "";
        OnPropertyChanged(nameof(PhoneSettingsName));
        UpdateConnectionText();
    }

    private void OnPhoneSyncChanged(PhoneCardModel card)
    {
        _host.Settings.SetClipboardEnabled(card.DeviceId, card.ClipboardSync);
        if (card.ClipboardSync) _host.Settings.ClipboardSyncEnabled = true;
        try { _host.SaveSettings(); }
        catch (Exception ex) { DetailText = "设置没有保存：" + ex.Message; }
    }

    private void UpdateConnectionText()
    {
        var id = TargetPhoneId();
        var card = Phones.FirstOrDefault(phone => phone.DeviceId == id);
        IsOnline = card?.Online == true;
        if (card == null)
        {
            StatusText = Phones.Count == 0 ? "未配对" : "未连接";
            return;
        }
        StatusText = card.Online ? "已连接 · " + card.Name : "未连接 · " + card.Name;
    }

    private string? TargetPhoneId()
    {
        if (!string.IsNullOrEmpty(SelectedPhoneId) && Phones.Any(phone => phone.DeviceId == SelectedPhoneId))
            return SelectedPhoneId;
        var online = Phones.Where(phone => phone.Online).ToList();
        if (online.Count == 1) return online[0].DeviceId;
        if (Phones.Count == 1) return Phones[0].DeviceId;
        return _host.Server?.CurrentPeer?.DeviceId;
    }

    private string? SelectedPeerOrNull()
    {
        var id = TargetPhoneId();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private int PeerCount() => Math.Max(Phones.Count, _host.Database.ListActive().Count);

    private bool _hotkeyMute;

    private void CommitHotkeys()
    {
        if (!_ready || _hotkeyMute) return;
        var conflict = HotkeyParser.Conflict(HotkeySend, HotkeySidebar);
        if (conflict != null)
        {
            HotkeyStatus = conflict;
            return;
        }
        if (!HotkeyParser.TryParse(HotkeySend, out var send, out var error) || !HotkeyParser.TryParse(HotkeySidebar, out var sidebar, out error))
        {
            HotkeyStatus = error ?? "快捷键无效。";
            return;
        }
        _hotkeyMute = true;
        HotkeySend = send.Canonical;
        HotkeySidebar = sidebar.Canonical;
        _hotkeyMute = false;
        _host.Settings.HotkeySend = send.Canonical;
        _host.Settings.HotkeySidebar = sidebar.Canonical;
        try { _host.SaveSettings(); }
        catch (Exception ex)
        {
            HotkeyStatus = "设置没有保存：" + ex.Message;
            return;
        }
        HotkeysChanged?.Invoke();
    }
}

public partial class PhoneCardModel : ObservableObject
{
    public string DeviceId { get; set; } = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _clipboardSync;
    [ObservableProperty] private bool _selected;
    public Action<PhoneCardModel>? SyncChanged { get; set; }
    public string StatusText => Online ? "已连接" : "未连接";

    partial void OnOnlineChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnClipboardSyncChanged(bool value)
    {
        if (SyncChanged == null) return;
        SyncChanged(this);
    }
}
