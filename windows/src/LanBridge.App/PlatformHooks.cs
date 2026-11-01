using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanBridge.Core;
using Microsoft.Win32;

namespace LanBridge.App;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string DatabasePath => Path.Combine(DataDirectory, "lanbridge.db");
    public static string CertificatePath => Path.Combine(DataDirectory, "identity.pfx.bin");
    public static string LogPath => Path.Combine(DataDirectory, "lanbridge.log");

    public static string DefaultIncoming()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "LanBridge");
    }
}

public static class CertStore
{
    public static X509Certificate2 LoadOrCreate(string path, string deviceId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var blob = File.ReadAllBytes(path);
            var pfx = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            return new X509Certificate2(pfx, "lanbridge", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        var created = CertificateFactory.Create(deviceId);
        var exported = created.Export(X509ContentType.Pfx, "lanbridge");
        File.WriteAllBytes(path, ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));
        return created;
    }
}

public static class StartupRegistration
{
    private const string ValueName = "LanBridge";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key == null) return;
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        key.SetValue(ValueName, "\"" + exe + "\"");
    }
}

public static class SendToRegistration
{
    public static void Install()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(sendTo) || string.IsNullOrEmpty(exe)) return;
        var link = Path.Combine(sendTo, "发送到手机.lnk");
        var dir = Path.GetDirectoryName(exe) ?? "";
        var script = "$w = New-Object -ComObject WScript.Shell\n"
            + "$s = $w.CreateShortcut('" + link.Replace("'", "''") + "')\n"
            + "$s.TargetPath = '" + exe.Replace("'", "''") + "'\n"
            + "$s.Arguments = '--send'\n"
            + "$s.WorkingDirectory = '" + dir.Replace("'", "''") + "'\n"
            + "$s.WindowStyle = 7\n"
            + "$s.Description = '通过兰桥发送到已配对的手机'\n"
            + "$s.Save()\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            process?.WaitForExit(8000);
        }
        catch (Exception) { }
    }
}

public sealed record IpcMessage(string Action, string[] Paths);

public static class SingleInstance
{
    private static Mutex? _mutex;
    private static bool _owner;
    private static CancellationTokenSource? _cts;

    public static event Action? ShowRequested;
    public static event Action<IReadOnlyList<string>>? FilesRequested;

    public static bool TryBecomePrimary(string[] args)
    {
        _mutex = new Mutex(true, @"Local\LanBridge.V1", out var created);
        if (!created)
        {
            TryForward(args);
            return false;
        }
        _owner = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Listen(_cts.Token));
        return true;
    }

    public static void Release()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        if (_owner)
        {
            try { _mutex?.ReleaseMutex(); } catch (Exception) { }
        }
        _mutex?.Dispose();
    }

    private static void TryForward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", "LanBridge.IPC.V1", PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            var action = args.Any(arg => arg == "--send") ? "send" : "show";
            var paths = args.Where(arg => arg != "--send" && !arg.StartsWith('-')).ToArray();
            writer.WriteLine(JsonSerializer.Serialize(new IpcMessage(action, paths)));
        }
        catch (Exception) { }
    }

    private static async Task Listen(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream("LanBridge.IPC.V1", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var message = JsonSerializer.Deserialize<IpcMessage>(line);
                if (message == null) continue;
                if (message.Action == "send" && message.Paths.Length > 0)
                    FilesRequested?.Invoke(message.Paths);
                else
                    ShowRequested?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { await Task.Delay(200, cancellationToken); }
        }
    }
}
