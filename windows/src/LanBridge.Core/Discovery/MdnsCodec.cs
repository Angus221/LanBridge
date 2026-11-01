using System.Net;
using System.Text;

namespace LanBridge.Core;

public readonly record struct DnsQuestion(string Name, ushort Type, bool Unicast);

public readonly record struct DnsRecord(string Name, ushort Type, ushort Class, uint Ttl, byte[] Rdata);

public static class MdnsCodec
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;

    public static string InstanceLabel(string deviceName, string deviceId)
    {
        var sb = new StringBuilder();
        foreach (var c in deviceName)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-') sb.Append(c);
            else if (char.IsWhiteSpace(c)) sb.Append('-');
        }
        var stem = sb.ToString().Trim('-');
        if (stem.Length == 0) stem = "LanBridge";
        if (stem.Length > 40) stem = stem[..40];
        var compact = deviceId.Replace("-", "", StringComparison.Ordinal);
        if (compact.Length > 6) compact = compact[..6];
        var label = stem + "-" + compact;
        return label.Length > 63 ? label[..63] : label;
    }

    public static string HostLabel(string deviceId)
    {
        var compact = deviceId.Replace("-", "", StringComparison.Ordinal);
        if (compact.Length > 12) compact = compact[..12];
        return "lanbridge-" + compact.ToLowerInvariant();
    }

    public const string ServiceName = "_lanbridge._tcp.local";

    public static byte[] BuildAnnouncement(
        string instanceLabel,
        int port,
        string hostLabel,
        IReadOnlyList<IPAddress> ipv4,
        IReadOnlyDictionary<string, string> txt) =>
        Build(0, [], instanceLabel, port, hostLabel, ipv4, txt);

    public static byte[] BuildResponse(
        byte[] query,
        string instanceLabel,
        int port,
        string hostLabel,
        IReadOnlyList<IPAddress> ipv4,
        IReadOnlyDictionary<string, string> txt)
    {
        var id = query.Length >= 2 ? (ushort)((query[0] << 8) | query[1]) : (ushort)0;
        return Build(id, ReadQuestions(query), instanceLabel, port, hostLabel, ipv4, txt);
    }

    private static byte[] Build(
        ushort id,
        IReadOnlyList<DnsQuestion> questions,
        string instanceLabel,
        int port,
        string hostLabel,
        IReadOnlyList<IPAddress> ipv4,
        IReadOnlyDictionary<string, string> txt)
    {
        var service = ServiceName;
        var instance = instanceLabel + "._lanbridge._tcp.local";
        var host = hostLabel + ".local";
        var enumerate = questions.Any(question =>
            question.Name.TrimEnd('.').Equals("_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase));
        var writer = new Writer();
        writer.U16(id);
        writer.U16(0x8400);
        writer.U16((ushort)questions.Count);
        writer.U16((ushort)(3 + ipv4.Count + (enumerate ? 1 : 0)));
        writer.U16(0);
        writer.U16(0);
        foreach (var question in questions)
        {
            writer.Name(question.Name);
            writer.U16(question.Type);
            writer.U16(question.Unicast ? (ushort)0x8001 : (ushort)1);
        }
        if (enumerate)
            WritePtr(writer, "_services._dns-sd._udp.local", service, 120, flush: false);
        WritePtr(writer, service, instance, 120, flush: false);
        WriteSrv(writer, instance, host, port, 120);
        WriteTxt(writer, instance, txt, 120);
        foreach (var address in ipv4)
            WriteA(writer, host, address, 120);
        return writer.ToArray();
    }

    public static bool IsQuery(byte[] packet)
    {
        if (packet.Length < 12) return false;
        var flags = (ushort)((packet[2] << 8) | packet[3]);
        return (flags & 0x8000) == 0;
    }

    public static List<DnsQuestion> ReadQuestions(byte[] packet)
    {
        var list = new List<DnsQuestion>();
        if (packet.Length < 12) return list;
        var offset = 0;
        _ = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        var qd = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(packet, ref offset);
            if (offset + 4 > packet.Length) break;
            var type = ReadU16(packet, ref offset);
            var klass = ReadU16(packet, ref offset);
            list.Add(new DnsQuestion(name, type, (klass & 0x8000) != 0));
        }
        return list;
    }

    public static List<DnsRecord> ReadAnswers(byte[] packet)
    {
        var list = new List<DnsRecord>();
        if (packet.Length < 12) return list;
        var offset = 0;
        _ = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        var qd = ReadU16(packet, ref offset);
        var an = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        _ = ReadU16(packet, ref offset);
        for (var i = 0; i < qd; i++)
        {
            _ = ReadName(packet, ref offset);
            offset += 4;
        }
        for (var i = 0; i < an; i++)
        {
            var name = ReadName(packet, ref offset);
            var type = ReadU16(packet, ref offset);
            var klass = ReadU16(packet, ref offset);
            var ttl = ReadU32(packet, ref offset);
            var len = ReadU16(packet, ref offset);
            if (offset + len > packet.Length) break;
            var data = new byte[len];
            Buffer.BlockCopy(packet, offset, data, 0, len);
            offset += len;
            list.Add(new DnsRecord(name, type, klass, ttl, data));
        }
        return list;
    }

    public static string ReadName(byte[] packet, int offset)
    {
        var cursor = offset;
        return ReadName(packet, ref cursor);
    }

    public static bool MatchesServiceQuery(IEnumerable<DnsQuestion> questions, string instanceLabel, string hostLabel)
    {
        var service = "_lanbridge._tcp.local";
        var instance = (instanceLabel + "._lanbridge._tcp.local").ToLowerInvariant();
        var host = (hostLabel + ".local").ToLowerInvariant();
        foreach (var question in questions)
        {
            var name = question.Name.TrimEnd('.').ToLowerInvariant();
            if (name == service && question.Type is TypePtr or TypeAny) return true;
            if (name == instance && question.Type is TypeSrv or TypeTxt or TypeAny) return true;
            if (name == host && question.Type is TypeA or TypeAny) return true;
            if (name == "_services._dns-sd._udp.local" && question.Type is TypePtr or TypeAny) return true;
        }
        return false;
    }

    public static string DecodePtr(byte[] packet, byte[] rdata)
    {
        var offset = 0;
        return ReadName(rdata, ref offset, packet);
    }

    private static void WritePtr(Writer writer, string name, string target, uint ttl, bool flush)
    {
        writer.Name(name);
        writer.U16(TypePtr);
        writer.U16(flush ? (ushort)0x8001 : (ushort)1);
        writer.U32(ttl);
        var nested = new Writer();
        nested.Name(target);
        var raw = nested.ToArray();
        writer.U16((ushort)raw.Length);
        writer.Bytes(raw);
    }

    private static void WriteSrv(Writer writer, string name, string target, int port, uint ttl)
    {
        writer.Name(name);
        writer.U16(TypeSrv);
        writer.U16(0x8001);
        writer.U32(ttl);
        var nested = new Writer();
        nested.U16(0);
        nested.U16(0);
        nested.U16((ushort)port);
        nested.Name(target);
        var raw = nested.ToArray();
        writer.U16((ushort)raw.Length);
        writer.Bytes(raw);
    }

    private static void WriteTxt(Writer writer, string name, IReadOnlyDictionary<string, string> txt, uint ttl)
    {
        writer.Name(name);
        writer.U16(TypeTxt);
        writer.U16(0x8001);
        writer.U32(ttl);
        var nested = new Writer();
        if (txt.Count == 0) nested.Bytes(new byte[] { 0 });
        foreach (var pair in txt)
        {
            var bytes = Encoding.UTF8.GetBytes(pair.Key + "=" + pair.Value);
            if (bytes.Length > 255) bytes = bytes[..255];
            nested.Bytes(new[] { (byte)bytes.Length });
            nested.Bytes(bytes);
        }
        var raw = nested.ToArray();
        writer.U16((ushort)raw.Length);
        writer.Bytes(raw);
    }

    private static void WriteA(Writer writer, string name, IPAddress address, uint ttl)
    {
        writer.Name(name);
        writer.U16(TypeA);
        writer.U16(0x8001);
        writer.U32(ttl);
        var raw = address.GetAddressBytes();
        writer.U16((ushort)raw.Length);
        writer.Bytes(raw);
    }

    private static ushort ReadU16(byte[] packet, ref int offset)
    {
        var value = (ushort)((packet[offset] << 8) | packet[offset + 1]);
        offset += 2;
        return value;
    }

    private static uint ReadU32(byte[] packet, ref int offset)
    {
        var value = (uint)((packet[offset] << 24) | (packet[offset + 1] << 16) | (packet[offset + 2] << 8) | packet[offset + 3]);
        offset += 4;
        return value;
    }

    private static string ReadName(byte[] packet, ref int offset) => ReadName(packet, ref offset, packet);

    private static string ReadName(byte[] packet, ref int offset, byte[] pointerSpace)
    {
        var labels = new List<string>();
        var jumped = false;
        var guard = 0;
        var end = offset;
        while (guard++ < 128 && offset < packet.Length)
        {
            var len = packet[offset];
            if (len == 0)
            {
                if (!jumped) end = offset + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length) break;
                var ptr = ((len & 0x3F) << 8) | packet[offset + 1];
                if (!jumped) end = offset + 2;
                jumped = true;
                offset = ptr;
                packet = pointerSpace;
                continue;
            }
            offset++;
            if (offset + len > packet.Length) break;
            labels.Add(Encoding.UTF8.GetString(packet, offset, len));
            offset += len;
            if (!jumped) end = offset;
        }
        offset = end;
        return string.Join('.', labels);
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        public void U16(ushort value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }
        public void U32(uint value)
        {
            U16((ushort)(value >> 16));
            U16((ushort)value);
        }
        public void Bytes(byte[] data) => _stream.Write(data);
        public void Name(string dotted)
        {
            foreach (var label in dotted.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var raw = Encoding.UTF8.GetBytes(label);
                if (raw.Length > 63) raw = raw[..63];
                _stream.WriteByte((byte)raw.Length);
                _stream.Write(raw);
            }
            _stream.WriteByte(0);
        }
        public byte[] ToArray() => _stream.ToArray();
    }
}
