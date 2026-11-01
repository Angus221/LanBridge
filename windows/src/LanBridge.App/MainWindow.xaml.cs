using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LanBridge.App.ViewModels;
using LanBridge.Core;

namespace LanBridge.App;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeySend = 1;
    private const int HotkeySidebar = 2;

    private readonly AppHost _host;
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _edgeTimer;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private System.Windows.Forms.Screen? _docked;
    private bool _shown;
    private bool _pointerInside;
    private bool _hotkeyHold;
    private bool _hotkeysRegistered;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _model = new MainViewModel(host);
        DataContext = _model;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_pointerInside) Collapse();
        };
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _edgeTimer.Tick += (_, _) => TrackEdge();
        Loaded += async (_, _) => await _model.ActivateAsync();
        SourceInitialized += (_, _) => OnSource();
        MouseEnter += (_, _) =>
        {
            _pointerInside = true;
            _hotkeyHold = false;
            Reveal(false);
        };
        MouseLeave += (_, _) =>
        {
            _pointerInside = false;
            ScheduleHide();
        };
        _model.HotkeysChanged += () => Dispatcher.Invoke(RegisterHotkeys);
        _model.PropertyChanged += OnModelChanged;
    }

    public void RevealFromOutside() => Reveal(true);

    public void SendClipboardFromOutside() => _model.SendClipboardCommand.Execute(null);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_host.ExitRequested)
        {
            e.Cancel = true;
            Collapse();
            return;
        }
        UnregisterHotkeys();
        _edgeTimer.Stop();
        base.OnClosing(e);
    }

    private void OnSource()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        Dock(false);
        RegisterHotkeys();
        _edgeTimer.Start();
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(() => Dock(_shown)); }
        catch (Exception) { }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SidebarPinned) && _model.SidebarPinned)
            Reveal(false);
    }

    private void Reveal(bool hold)
    {
        if (hold) _hotkeyHold = true;
        _shown = true;
        _hideTimer.Stop();
        Dock(true);
        if (!_model.SidebarPinned && !_pointerInside && !_hotkeyHold)
            ScheduleHide();
    }

    private void ScheduleHide()
    {
        if (_model.SidebarPinned || _model.SettingsOpen || _hotkeyHold || !_shown) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Collapse()
    {
        if (_model.SidebarPinned || _hotkeyHold) return;
        _shown = false;
        _hideTimer.Stop();
        Dock(true);
    }

    private void TrackEdge()
    {
        if (_shown) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        var near = SidebarLayout.NearRightEdge(cursor.X, area.Left, area.Width, 36);
        if (_docked == null || (near && !ReferenceEquals(screen, _docked)))
        {
            _docked = screen;
            Dock(false);
        }
    }

    private void Dock(bool animate)
    {
        if (_hwnd == IntPtr.Zero) return;
        var screen = _shown
            ? _docked ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position)
            : _docked ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        _docked = screen;
        var area = screen.WorkingArea;
        var dips = SidebarLayout.ToDips(area.X, area.Y, area.Width, area.Height, DpiScale());
        var place = SidebarLayout.Place(dips.X, dips.Y, dips.Width, dips.Height, _shown);
        Top = place.Top;
        Height = Math.Max(MinHeight, place.Height);
        Move(place.Left, place.Width, animate);
    }

    private void Move(double left, double width, bool animate)
    {
        var duration = SidebarLayout.Duration(!SystemParameters.ClientAreaAnimation);
        if (!animate || duration == TimeSpan.Zero)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(WidthProperty, null);
            Left = left;
            Width = width;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(LeftProperty, new DoubleAnimation(left, duration) { EasingFunction = ease });
        BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = ease });
    }

    private double DpiScale()
    {
        if (_hwnd == IntPtr.Zero) return 1;
        var dpi = GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == HotkeySend)
            {
                _model.SendClipboardCommand.Execute(null);
                handled = true;
            }
            else if (id == HotkeySidebar)
            {
                if (_shown && !_model.SidebarPinned) CollapseFromHotkey();
                else Reveal(true);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void CollapseFromHotkey()
    {
        _hotkeyHold = false;
        _pointerInside = false;
        _shown = false;
        Dock(true);
    }

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotkeys();
        var conflict = HotkeyParser.Conflict(_model.HotkeySend, _model.HotkeySidebar);
        if (conflict != null)
        {
            _model.SetHotkeyStatus(conflict);
            return;
        }
        if (!HotkeyParser.TryParse(_model.HotkeySend, out var send, out var error)
            || !HotkeyParser.TryParse(_model.HotkeySidebar, out var sidebar, out error))
        {
            _model.SetHotkeyStatus(error ?? "快捷键无效。");
            return;
        }
        var sendOk = RegisterHotKey(_hwnd, HotkeySend, (uint)send.Modifiers, (uint)send.VirtualKey);
        var sideOk = RegisterHotKey(_hwnd, HotkeySidebar, (uint)sidebar.Modifiers, (uint)sidebar.VirtualKey);
        _hotkeysRegistered = sendOk || sideOk;
        if (!sendOk || !sideOk)
        {
            if (!sendOk) UnregisterHotKey(_hwnd, HotkeySend);
            if (!sideOk) UnregisterHotKey(_hwnd, HotkeySidebar);
            _hotkeysRegistered = false;
            var message = (!sendOk ? "发送快捷键已被其他程序占用。" : "") + (!sideOk ? "侧栏快捷键已被其他程序占用。" : "");
            _model.SetHotkeyStatus(message);
            return;
        }
        _model.SetHotkeyStatus(send.Canonical + " 发送剪贴板 · " + sidebar.Canonical + " 打开侧栏");
    }

    private void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero || !_hotkeysRegistered) return;
        UnregisterHotKey(_hwnd, HotkeySend);
        UnregisterHotKey(_hwnd, HotkeySidebar);
        _hotkeysRegistered = false;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
