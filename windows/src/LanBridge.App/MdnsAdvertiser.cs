using System.Net;
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
    private IPAddress? _lan;

    public MdnsAdvertiser(Func<(string DeviceId, string DeviceName, int Port, string Fingerprint)> identity, Action<string> log)
    {
        _identity = identity;
        _log = log;
    }

    public void Start() => _ = Task.Run(Loop);

    public void Announce()
    {
        var socket = _socket;
        var lan = _lan;
        if (socket == null || lan == null) return;
        try { Send(socket, new IPEndPoint(Group, Port), null); }
        catch (Exception ex) { _log("mdns send: " + ex.Message); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket?.Dispose(); } catch (Exception) { }
    }

    public static IReadOnlyList<IPAddress> LocalIpv4()
    {
        var chosen = LanNic.Select(LanNic.Scan());
        return chosen == null ? [] : [chosen.Address];
    }

    public static IPAddress? Preferred(IReadOnlyList<IPAddress> addresses) => addresses.FirstOrDefault();

    private void Loop()
    {
        Socket? socket = null;
        try
        {
            var choice = LanNic.Select(LanNic.Scan());
            if (choice == null)
            {
                _log("mdns: no physical lan interface");
                return;
            }
            _lan = choice.Address;
            _log("mdns: interface " + choice.Name + " " + choice.Address + " gateway=" + choice.HasGateway);
            socket = Bind(choice.Address);
            _socket = socket;
            var fastUntil = DateTime.UtcNow.AddSeconds(45);
            var last = DateTime.MinValue;
            var buffer = new byte[4096];
            while (!_cts.IsCancellationRequested)
            {
                var interval = DateTime.UtcNow < fastUntil ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15);
                if (DateTime.UtcNow - last >= interval)
                {
                    last = DateTime.UtcNow;
                    try { Send(socket, new IPEndPoint(Group, Port), null); }
                    catch (Exception ex) { _log("mdns send: " + ex.Message); }
                }
                if (!socket.Poll(500_000, SelectMode.SelectRead) || socket.Available <= 0) continue;
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try { length = socket.ReceiveFrom(buffer, ref remote); }
                catch (SocketException) { continue; }
                if (length > 0) Handle(socket, buffer.AsSpan(0, length).ToArray(), remote);
            }
        }
        catch (Exception ex) { _log("mdns stopped: " + ex.Message); }
        finally
        {
            try { socket?.Dispose(); } catch (Exception) { }
        }
    }

    private Socket Bind(IPAddress lan)
    {
        Exception? last = null;
        foreach (var endpoint in new IPEndPoint[] { new(lan, Port), new(IPAddress.Any, Port) })
        {
            var socket = Open(lan);
            try
            {
                socket.Bind(endpoint);
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, lan));
                }
                catch (Exception ex) { _log("mdns join " + lan + ": " + ex.Message); }
                _log("mdns bound " + socket.LocalEndPoint);
                return socket;
            }
            catch (Exception ex)
            {
                last = ex;
                _log("mdns bind " + endpoint + " failed: " + ex.Message);
                socket.Dispose();
            }
        }
        var sendOnly = Open(lan);
        sendOnly.Bind(new IPEndPoint(lan, 0));
        _log("mdns send-only " + sendOnly.LocalEndPoint + " (5353 busy: " + last?.Message + ")");
        return sendOnly;
    }

    private static Socket Open(IPAddress lan)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = false;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); }
        catch (Exception) { }
        SetMulticastInterface(socket, lan);
        return socket;
    }

    private static void SetMulticastInterface(Socket socket, IPAddress address)
    {
        // IPv4 IP_MULTICAST_IF wants the address bytes. BitConverter layout matches that on little-endian Windows.
        var option = BitConverter.ToInt32(address.GetAddressBytes(), 0);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, option);
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
            if (questions.Any(question => question.Unicast))
                Send(socket, remote, packet);
            Send(socket, new IPEndPoint(Group, Port), packet);
        }
        catch (Exception ex) { _log("mdns query: " + ex.Message); }
    }

    private void Send(Socket socket, EndPoint destination, byte[]? query)
    {
        var lan = _lan;
        if (lan == null) return;
        var identity = _identity();
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
        var instance = MdnsCodec.InstanceLabel(identity.DeviceName, identity.DeviceId);
        var host = MdnsCodec.HostLabel(identity.DeviceId);
        var packet = query == null
            ? MdnsCodec.BuildAnnouncement(instance, identity.Port, host, [lan], txt)
            : MdnsCodec.BuildResponse(query, instance, identity.Port, host, [lan], txt);
        socket.SendTo(packet, destination);
    }
}
