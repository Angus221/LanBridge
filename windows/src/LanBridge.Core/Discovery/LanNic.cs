using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanBridge.Core;

public sealed record LanNicChoice(
    IPAddress Address,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool HasGateway);

public static class LanNic
{
    public static bool IsVirtual(string? name, string? description)
    {
        var text = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
        string[] keys =
        [
            "virtual", "hyper-v", "hyperv", "vethernet", "vmware", "virtualbox", "vbox",
            "wsl", "docker", "loopback", "bluetooth", "vpn", "tap-windows", "tun",
            "teredo", "isatap", "npcap", "tunnel", "pseudo", "host-only", "host only",
            "sing-box", "clash", "wireguard"
        ];
        foreach (var key in keys)
        {
            if (text.Contains(key, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static bool IsPhysical(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx;

    public static int Rank(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return 9;
        if (b[0] == 192 && b[1] == 168) return 0;
        if (b[0] == 10) return 1;
        if (b[0] == 172) return 2;
        if (b[0] == 169 && b[1] == 254) return 4;
        return 3;
    }

    public static LanNicChoice? Select(IEnumerable<LanNicChoice> nics) =>
        nics
            .Where(nic => nic.Address.AddressFamily == AddressFamily.InterNetwork)
            .Where(nic => !IPAddress.IsLoopback(nic.Address))
            .Where(nic => LanAddress.IsPrivateLan(nic.Address))
            .Where(nic => nic.Type is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(nic => !IsVirtual(nic.Name, nic.Description))
            .OrderByDescending(nic => nic.HasGateway)
            .ThenByDescending(nic => IsPhysical(nic.Type))
            .ThenBy(nic => Rank(nic.Address))
            .ThenBy(nic => nic.Address.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

    public static IReadOnlyList<LanNicChoice> Scan()
    {
        var list = new List<LanNicChoice>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var props = nic.GetIPProperties();
            var gateway = props.GatewayAddresses.Any(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address));
            foreach (var unicast in props.UnicastAddresses)
            {
                list.Add(new LanNicChoice(unicast.Address, nic.Name, nic.Description, nic.NetworkInterfaceType, gateway));
            }
        }
        return list;
    }
}
