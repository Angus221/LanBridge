using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanBridge.Core;

namespace LanBridge.App;

public sealed class MdnsAdvertiser : IDisposable
{
    public const int Port = 5353;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private readonly Func<(string DeviceId, string DeviceName, int Port, string Fingerprint)> _identity;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;

    public MdnsAdvertiser(Func<(string DeviceId, string DeviceName, int Port, string Fingerprint)> identity, Action<string> log)
    {
        _identity = identity;
        _log = log;
    }

    public void Start() => _ = Task.Run(Loop);

    public void Announce()
    {
        var socket = _socket;
        if (socket == null) return;
        try { Send(socket, new IPEndPoint(Group, Port)); }
        catch (Exception ex) { _log("mdns send: " + ex.Message); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket?.Dispose(); } catch (Exception) { }
    }

    public static IReadOnlyList<IPAddress> LocalIpv4()
    {
        var list = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(address)) continue;
                if (!LanAddress.IsPrivateLan(address)) continue;
                list.Add(address);
            }
        }
        return list;
    }

    public static IPAddress? Preferred(IReadOnlyList<IPAddress> addresses)
    {
        static int Rank(IPAddress address)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 192 && b[1] == 168) return 0;
            if (b[0] == 10) return 1;
            if (b[0] == 172) return 2;
            return 3;
        }
        return addresses.OrderBy(Rank).ThenBy(address => address.ToString()).FirstOrDefault();
    }

    private void Loop()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            foreach (var address in LocalIpv4())
            {
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address)); }
                catch (Exception ex) { _log("mdns join " + address + ": " + ex.Message); }
            }
            try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); }
            catch (Exception) { }
            _socket = socket;
            Send(socket, new IPEndPoint(Group, Port));
            var buffer = new byte[2048];
            var last = DateTime.UtcNow;
            while (!_cts.IsCancellationRequested)
            {
                if (socket.Poll(1_000_000, SelectMode.SelectRead) && socket.Available > 0)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try { length = socket.ReceiveFrom(buffer, ref remote); }
                    catch (SocketException) { continue; }
                    if (length > 0) Handle(socket, buffer.AsSpan(0, length).ToArray(), remote);
                }
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(20))
                {
                    last = DateTime.UtcNow;
                    Send(socket, new IPEndPoint(Group, Port));
                }
            }
        }
        catch (Exception ex) { _log("mdns stopped: " + ex.Message); }
    }

    private void Handle(Socket socket, byte[] packet, EndPoint remote)
    {
        try
        {
            if (!MdnsCodec.IsQuery(packet)) return;
            var identity = _identity();
            var instance = MdnsCodec.InstanceLabel(identity.DeviceName, identity.DeviceId);
            var host = MdnsCodec.HostLabel(identity.DeviceId);
            var questions = MdnsCodec.ReadQuestions(packet);
            if (!MdnsCodec.MatchesServiceQuery(questions, instance, host)) return;
            var unicast = questions.Any(question => question.Unicast);
            Send(socket, unicast ? remote : new IPEndPoint(Group, Port));
        }
        catch (Exception ex) { _log("mdns query: " + ex.Message); }
    }

    private void Send(Socket socket, EndPoint destination)
    {
        var identity = _identity();
        var addresses = LocalIpv4();
        if (addresses.Count == 0) return;
        var name = identity.DeviceName;
        if (name.Length > 40) name = name[..40];
        var txt = new Dictionary<string, string>
        {
            ["id"] = identity.DeviceId,
            ["name"] = name,
            ["ver"] = ProtocolConstants.Version.ToString(),
            ["fp"] = identity.Fingerprint,
            ["role"] = "pc",
            ["port"] = identity.Port.ToString()
        };
        var packet = MdnsCodec.BuildAnnouncement(
            MdnsCodec.InstanceLabel(identity.DeviceName, identity.DeviceId),
            identity.Port,
            MdnsCodec.HostLabel(identity.DeviceId),
            addresses,
            txt);
        socket.SendTo(packet, destination);
    }
}
