using System.Windows;

namespace LanBridge.App;

public partial class App : Application
{
    private AppHost? _host;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            _host?.Log("ui: " + args.Exception);
            args.Handled = true;
        };

        if (!SingleInstance.TryBecomePrimary(e.Args))
        {
            Shutdown();
            return;
        }

        ApplyTheme();
        _host = new AppHost();
        SingleInstance.ShowRequested += () => Dispatcher.Invoke(ShowMain);
        SingleInstance.FilesRequested += paths => _host.EnqueueFiles(paths);
        var window = new MainWindow(_host);
        MainWindow = window;
        _tray = new TrayController(window, _host, () => window.RevealFromOutside(), () => window.SendClipboardFromOutside());
        _host.Notification += (title, body) => Dispatcher.Invoke(() => _tray?.Balloon(title, body));
        _host.Start();
        window.Show();
    }

    private void ShowMain()
    {
        if (MainWindow is not MainWindow window) return;
        window.Show();
        window.RevealFromOutside();
    }

    private void ApplyTheme()
    {
        var dark = false;
        try
        {
            dark = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1) is int light && light == 0;
        }
        catch (Exception) { }
        SetBrush("BgBrush", dark ? "#000000" : "#F2F2F7");
        SetBrush("CardBrush", dark ? "#1C1C1E" : "#FFFFFF");
        SetBrush("InkBrush", dark ? "#FFFFFF" : "#1C1C1E");
        SetBrush("MutedBrush", dark ? "#AEAEB2" : "#636366");
        SetBrush("AccentBrush", dark ? "#409CFF" : "#0058C6");
        SetBrush("PhoneBrush", dark ? "#30D158" : "#1F7A34");
        SetBrush("LineBrush", dark ? "#38383A" : "#E5E5EA");
        SetBrush("HandleBrush", dark ? "#3A3A3C" : "#FFFFFF");
    }

    private void SetBrush(string key, string hex)
    {
        Resources[key] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_host != null)
        {
            try { _host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception) { }
        }
        SingleInstance.Release();
        base.OnExit(e);
    }
}
