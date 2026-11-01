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

        _host = new AppHost();
        SingleInstance.ShowRequested += () => Dispatcher.Invoke(ShowMain);
        SingleInstance.FilesRequested += paths => _host.EnqueueFiles(paths);
        var window = new MainWindow(_host);
        MainWindow = window;
        _tray = new TrayController(window, _host, ShowMain);
        _host.Notification += (title, body) => Dispatcher.Invoke(() => _tray?.Balloon(title, body));
        _host.Start();
        window.Show();
    }

    private void ShowMain()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
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
