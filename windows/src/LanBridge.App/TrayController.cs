using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace LanBridge.App;

public sealed class TrayController : IDisposable
{
    private readonly Window _window;
    private readonly AppHost _host;
    private readonly Action _show;
    private readonly Action _send;
    private readonly NotifyIcon _icon;

    public TrayController(Window window, AppHost host, Action show, Action send)
    {
        _window = window;
        _host = host;
        _show = show;
        _send = send;
        _icon = new NotifyIcon
        {
            Text = "兰桥",
            Icon = LoadIcon(),
            Visible = true
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开兰桥", null, (_, _) => _show());
        menu.Items.Add("发送剪贴板", null, (_, _) => _send());
        menu.Items.Add("退出", null, (_, _) => Exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _show();
    }

    public void Balloon(string title, string body)
    {
        try { _icon.ShowBalloonTip(4000, title, body, ToolTipIcon.Info); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void Exit()
    {
        _host.RequestExit();
        _window.Close();
        Application.Current.Shutdown();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lanbridge.ico"))?.Stream;
            if (stream != null) return new Icon(stream);
        }
        catch (Exception) { }
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "lanbridge.ico");
        if (File.Exists(path)) return new Icon(path);
        return SystemIcons.Application;
    }
}
