using System.Diagnostics;
using System.IO;
using LanBridge.Core;

namespace LanBridge.App;

public static class FirewallSetup
{
    public static string Ensure(int port, string? programPath, Action<string> log)
    {
        var explain = FirewallRules.Explain(port, programPath);
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.FirewallNotePath, explain + Environment.NewLine);
        }
        catch (Exception ex) { log("firewall note: " + ex.Message); }

        var installed = true;
        foreach (var argument in FirewallRules.DeleteArguments(port, !string.IsNullOrWhiteSpace(programPath)))
            Run(argument, log, required: false);
        foreach (var argument in FirewallRules.AddArguments(port, programPath))
        {
            if (!Run(argument, log, required: true)) installed = false;
        }
        if (installed)
        {
            try { File.WriteAllText(AppPaths.FirewallOkPath, DateTimeOffset.Now.ToString("o")); }
            catch (Exception) { }
            log("firewall rules added for TCP " + port + " and UDP 5353");
            return "";
        }
        log("firewall rules were not added (need an elevated process). " + explain.Replace(Environment.NewLine, " | "));
        return explain;
    }

    public static bool AlreadyNoted() => File.Exists(AppPaths.FirewallOkPath) || File.Exists(AppPaths.FirewallShownPath);

    public static void MarkShown()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.FirewallShownPath, DateTimeOffset.Now.ToString("o"));
        }
        catch (Exception) { }
    }

    private static bool Run(string arguments, Action<string> log, bool required)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
            {
                if (required) log("netsh did not start: " + arguments);
                return !required;
            }
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (required) log("netsh timed out: " + arguments);
                return !required;
            }
            if (process.ExitCode != 0 && required)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                log("netsh " + process.ExitCode + " " + arguments + " " + error);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (required) log("netsh failed: " + ex.Message);
            return !required;
        }
    }
}
