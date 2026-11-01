namespace LanBridge.Core;

public static class FirewallRules
{
    public static string TcpName(int port) => "LanBridge TCP " + port;

    public const string MdnsName = "LanBridge mDNS";

    public const string ProgramName = "LanBridge Program";

    public static IReadOnlyList<string> AddArguments(int port, string? programPath)
    {
        var list = new List<string>
        {
            $"advfirewall firewall add rule name=\"{TcpName(port)}\" dir=in action=allow protocol=TCP localport={port} profile=any",
            $"advfirewall firewall add rule name=\"{MdnsName}\" dir=in action=allow protocol=UDP localport=5353 profile=any"
        };
        if (!string.IsNullOrWhiteSpace(programPath))
        {
            list.Add($"advfirewall firewall add rule name=\"{ProgramName}\" dir=in action=allow program=\"{programPath}\" enable=yes profile=any");
        }
        return list;
    }

    public static IReadOnlyList<string> DeleteArguments(int port, bool includeProgram)
    {
        var list = new List<string>
        {
            $"advfirewall firewall delete rule name=\"{TcpName(port)}\"",
            $"advfirewall firewall delete rule name=\"{MdnsName}\""
        };
        if (includeProgram) list.Add($"advfirewall firewall delete rule name=\"{ProgramName}\"");
        return list;
    }

    public static string Explain(int port, string? programPath)
    {
        var lines = new List<string>
        {
            "手机要发现这台电脑，Windows 防火墙需要放行 TCP " + port + "（兰桥）和 UDP 5353（mDNS）。",
            "请在第一次弹出防火墙提示时允许专用网络。如果手机仍然搜不到，用管理员终端执行："
        };
        foreach (var argument in AddArguments(port, programPath))
            lines.Add("netsh " + argument);
        return string.Join(Environment.NewLine, lines);
    }
}
