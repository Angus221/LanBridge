using System.ComponentModel;
using System.Windows;
using LanBridge.App.ViewModels;

namespace LanBridge.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        var model = new MainViewModel(host);
        DataContext = model;
        Loaded += async (_, _) => await model.ActivateAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_host.ExitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
