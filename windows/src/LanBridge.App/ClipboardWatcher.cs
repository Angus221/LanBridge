using System.Runtime.InteropServices;
using System.Windows.Forms;
using LanBridge.Core;

namespace LanBridge.App;

public sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Action _changed;
    private bool _listening;

    public ClipboardWatcher(Action changed) => _changed = changed;

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _listening) return;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        _listening = AddClipboardFormatListener(Handle);
    }

    public void Dispose()
    {
        if (_listening)
        {
            RemoveClipboardFormatListener(Handle);
            _listening = false;
        }
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate) _changed();
        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
