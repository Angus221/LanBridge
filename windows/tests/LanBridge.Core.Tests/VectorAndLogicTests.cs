using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace LanBridge.Core.Tests;

public class VectorAndLogicTests
{
    private static JsonDocument Vectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "test-vectors.json");
            if (File.Exists(candidate)) return JsonDocument.Parse(File.ReadAllText(candidate));
            dir = dir.Parent;
        }
        throw new FileNotFoundException("docs/test-vectors.json");
    }

    [Fact]
    public void Sha256_matches_vectors()
    {
        using var doc = Vectors();
        foreach (var item in doc.RootElement.GetProperty("sha256").EnumerateArray())
        {
            var actual = Hashing.Sha256Hex(item.GetProperty("input").GetString() ?? "");
            Assert.Equal(item.GetProperty("hex").GetString(), actual);
        }
    }

    [Fact]
    public void Normalize_matches_vectors()
    {
        using var doc = Vectors();
        foreach (var item in doc.RootElement.GetProperty("normalize").EnumerateArray())
        {
            var input = item.GetProperty("input").GetString() ?? "";
            Assert.Equal(item.GetProperty("expected").GetString(), TextNormalizer.Normalize(input));
        }
    }

    [Fact]
    public void Sanitize_matches_vectors()
    {
        using var doc = Vectors();
        foreach (var item in doc.RootElement.GetProperty("sanitize").EnumerateArray())
        {
            var input = item.GetProperty("input").GetString() ?? "";
            Assert.Equal(item.GetProperty("expected").GetString(), FileNameSanitizer.Sanitize(input));
        }
    }

    [Fact]
    public void Sensitive_filter_matches_vectors()
    {
        using var doc = Vectors();
        foreach (var item in doc.RootElement.GetProperty("sensitive").EnumerateArray())
        {
            var input = item.GetProperty("input").GetString() ?? "";
            Assert.Equal(item.GetProperty("sensitive").GetBoolean(), SensitiveTextFilter.IsSensitive(input));
        }
    }

    [Fact]
    public void Ecdsa_vector_verifies_and_canonical_matches()
    {
        using var doc = Vectors();
        var ecdsa = doc.RootElement.GetProperty("ecdsa");
        var canonical = RequestAuth.Canonical(
            ecdsa.GetProperty("timestampMs").GetInt64(),
            ecdsa.GetProperty("nonce").GetString()!,
            ecdsa.GetProperty("method").GetString()!,
            ecdsa.GetProperty("path").GetString()!,
            ecdsa.GetProperty("contentSha256").GetString());
        Assert.Equal(ecdsa.GetProperty("canonical").GetString(), canonical);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ecdsa.GetProperty("publicKeySpkiBase64").GetString()!), out _);
        var signature = Convert.FromBase64String(ecdsa.GetProperty("signatureDerBase64").GetString()!);
        Assert.True(RequestAuth.Verify(key, canonical, signature));
        signature[10] ^= 0x01;
        Assert.False(RequestAuth.Verify(key, canonical, signature));
    }

    [Fact]
    public void Receive_policy_matches_vectors()
    {
        using var doc = Vectors();
        foreach (var item in doc.RootElement.GetProperty("receive").EnumerateArray())
        {
            var decision = ReceivePolicy.Evaluate(
                item.GetProperty("name").GetString(),
                item.GetProperty("size").GetInt64(),
                item.GetProperty("free").GetInt64(),
                item.GetProperty("max").GetInt64(),
                item.GetProperty("auto").GetBoolean(),
                item.GetProperty("active").GetInt32());
            var expected = item.GetProperty("action").GetString();
            Assert.Equal(expected, decision.Action switch
            {
                ReceiveAction.Accept => "accept",
                ReceiveAction.Confirm => "confirm",
                _ => "reject"
            });
            if (item.TryGetProperty("reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                Assert.Equal(reason.GetString(), decision.Reason);
            Assert.Equal(item.GetProperty("safe").GetString(), decision.SafeName);
        }
    }

    [Fact]
    public void Pairing_window_locks_after_five_failures_and_expires()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var window = new PairingWindow("042813", now.AddMinutes(2));
        Assert.Equal(PinCheckResult.Mismatch, window.Check("000000", now));
        Assert.Equal(PinCheckResult.Mismatch, window.Check("000000", now));
        Assert.Equal(PinCheckResult.Mismatch, window.Check("111111", now));
        Assert.Equal(PinCheckResult.Mismatch, window.Check("short", now));
        Assert.Equal(PinCheckResult.Locked, window.Check("999999", now));
        Assert.Equal(PinCheckResult.Locked, window.Check("042813", now));

        var fresh = new PairingWindow("042813", now.AddMinutes(2));
        Assert.Equal(PinCheckResult.Closed, fresh.Check("042813", now.AddMinutes(2)));
        Assert.Equal(PinCheckResult.Match, fresh.Check("042813", now.AddMinutes(2).AddSeconds(-1)));
    }

    [Fact]
    public void Timestamp_and_nonce_rules()
    {
        var now = 1_700_000_000_000;
        Assert.True(RequestAuth.TimestampFresh(now, now));
        Assert.True(RequestAuth.TimestampFresh(now - RequestAuth.WindowMs, now));
        Assert.False(RequestAuth.TimestampFresh(now - RequestAuth.WindowMs - 1, now));
        Assert.True(RequestAuth.NonceFormatOk("abcdef0123456789"));
        Assert.True(RequestAuth.NonceFormatOk(new string('a', 32)));
        Assert.False(RequestAuth.NonceFormatOk("ABCDEF"));
        Assert.False(RequestAuth.NonceFormatOk("short"));
        var cache = new NonceCache();
        Assert.True(cache.TryUse("dev", "abcdef0123456789", now));
        Assert.False(cache.TryUse("dev", "abcdef0123456789", now + 10));
        Assert.True(cache.TryUse("other", "abcdef0123456789", now + 10));
    }

    [Fact]
    public void Clipboard_guard_suppresses_echo()
    {
        var guard = new ClipboardGuard();
        Assert.True(guard.ShouldAutoSend("abc", 1_000));
        guard.MarkSent("abc");
        Assert.False(guard.ShouldAutoSend("abc", 1_100));
        Assert.True(guard.ShouldAutoSend("def", 1_100));
        guard.MarkApplied("def", 2_000, 5_000);
        Assert.False(guard.ShouldAutoSend("def", 6_000));
        Assert.True(guard.ShouldAutoSend("def", 7_001));
    }

    [Fact]
    public void Lan_address_classification()
    {
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("127.0.0.1")));
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("10.1.2.3")));
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("192.168.1.20")));
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("172.16.5.5")));
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("169.254.1.1")));
        Assert.False(LanAddress.IsPrivateLan(IPAddress.Parse("172.32.0.1")));
        Assert.False(LanAddress.IsPrivateLan(IPAddress.Parse("8.8.8.8")));
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("fe80::1")));
        Assert.True(LanAddress.IsPrivateLan(IPAddress.Parse("fd00::1")));
        Assert.False(LanAddress.IsPrivateLan(IPAddress.Parse("2001:4860:4860::8888")));
    }

    [Fact]
    public void Atomic_commit_keeps_only_matching_hash()
    {
        var dir = Directory.CreateTempSubdirectory("lanbridge-atomic").FullName;
        var part = Path.Combine(dir, "a.part");
        var finalPath = Path.Combine(dir, "a.txt");
        File.WriteAllText(part, "LanBridge");
        var hash = Hashing.Sha256Hex("LanBridge");
        AtomicFile.CommitVerified(part, finalPath, hash);
        Assert.False(File.Exists(part));
        Assert.Equal("LanBridge", File.ReadAllText(finalPath));

        var bad = Path.Combine(dir, "b.part");
        File.WriteAllText(bad, "nope");
        Assert.Throws<IntegrityException>(() => AtomicFile.CommitVerified(bad, Path.Combine(dir, "b.txt"), hash));
        Assert.False(File.Exists(bad));
        Assert.False(File.Exists(Path.Combine(dir, "b.txt")));
    }

    [Fact]
    public void Unique_names_and_path_jail()
    {
        var seen = new HashSet<string>();
        var first = FileNameSanitizer.Unique("a.txt", seen.Contains);
        seen.Add(first);
        var second = FileNameSanitizer.Unique("a.txt", seen.Contains);
        Assert.Equal("a.txt", first);
        Assert.Equal("a (1).txt", second);
        var dir = Directory.CreateTempSubdirectory("lanbridge-jail").FullName;
        Assert.True(PathSafety.IsUnderDirectory(dir, Path.Combine(dir, "ok.txt")));
        Assert.False(PathSafety.IsUnderDirectory(dir, Path.Combine(dir, "..", "escape.txt")));
    }

    [Fact]
    public void Certificate_fingerprint_is_stable_sha256()
    {
        using var cert = CertificateFactory.Create("11111111-1111-1111-1111-111111111111");
        var fp = CertFingerprints.Sha256(cert);
        Assert.Equal(64, fp.Length);
        Assert.Equal(fp, fp.ToLowerInvariant());
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void Pairing_link_matches_the_documented_sample()
    {
        var link = PairingLink.Build(
            "192.168.1.20",
            47821,
            "11111111-1111-1111-1111-111111111111",
            "1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749",
            "042813",
            1800000000);
        Assert.Equal(
            "lanbridge://pair?v=1&host=192.168.1.20&port=47821&id=11111111-1111-1111-1111-111111111111&fp=1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749&pin=042813&exp=1800000000",
            link);
    }

    [Fact]
    public void Mdns_announcement_roundtrip()
    {
        var txt = new Dictionary<string, string>
        {
            ["id"] = "11111111-1111-1111-1111-111111111111",
            ["fp"] = "1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749",
            ["name"] = "书房电脑",
            ["ver"] = "1",
            ["role"] = "pc"
        };
        var packet = MdnsCodec.BuildAnnouncement("StudyPC", 47821, "lanbridge-111111111111", new[] { IPAddress.Parse("192.168.1.20") }, txt);
        Assert.False(MdnsCodec.IsQuery(packet));
        var answers = MdnsCodec.ReadAnswers(packet);
        Assert.Contains(answers, record => record.Type == MdnsCodec.TypePtr && record.Name == "_lanbridge._tcp.local");
        var srv = Assert.Single(answers, record => record.Type == MdnsCodec.TypeSrv);
        var port = (srv.Rdata[4] << 8) | srv.Rdata[5];
        Assert.Equal(47821, port);
        var txtRecord = Assert.Single(answers, record => record.Type == MdnsCodec.TypeTxt);
        var text = System.Text.Encoding.UTF8.GetString(txtRecord.Rdata);
        Assert.Contains("fp=1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749", text, StringComparison.Ordinal);
        var address = Assert.Single(answers, record => record.Type == MdnsCodec.TypeA);
        Assert.Equal(new byte[] { 192, 168, 1, 20 }, address.Rdata);

        var query = BuildPtrQuery("_lanbridge._tcp.local");
        Assert.True(MdnsCodec.IsQuery(query));
        var questions = MdnsCodec.ReadQuestions(query);
        Assert.True(MdnsCodec.MatchesServiceQuery(questions, "StudyPC", "lanbridge-111111111111"));
    }

    [Fact]
    public void Mdns_response_echoes_the_query_and_advertises_only_the_given_address()
    {
        var query = BuildPtrQuery("_lanbridge._tcp.local", 0x1234);
        var txt = new Dictionary<string, string> { ["fp"] = "ab", ["port"] = "47821", ["role"] = "pc" };
        var packet = MdnsCodec.BuildResponse(query, "StudyPC", 47821, "lanbridge-111111111111", new[] { IPAddress.Parse("192.168.1.20") }, txt);
        Assert.False(MdnsCodec.IsQuery(packet));
        Assert.Equal(0x12, packet[0]);
        Assert.Equal(0x34, packet[1]);
        var answers = MdnsCodec.ReadAnswers(packet);
        Assert.Contains(answers, record => record.Type == MdnsCodec.TypePtr && record.Name == "_lanbridge._tcp.local");
        var address = Assert.Single(answers, record => record.Type == MdnsCodec.TypeA);
        Assert.Equal(new byte[] { 192, 168, 1, 20 }, address.Rdata);
        Assert.Equal(47821, (answers.Single(record => record.Type == MdnsCodec.TypeSrv).Rdata[4] << 8) | answers.Single(record => record.Type == MdnsCodec.TypeSrv).Rdata[5]);
    }

    [Fact]
    public void Lan_nic_skips_loopback_and_virtual_adapters()
    {
        var choices = new[]
        {
            new LanNicChoice(IPAddress.Loopback, "Loopback", "Loopback Pseudo-Interface", NetworkInterfaceType.Loopback, false),
            new LanNicChoice(IPAddress.Parse("172.22.160.1"), "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false),
            new LanNicChoice(IPAddress.Parse("192.168.56.1"), "VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1", NetworkInterfaceType.Ethernet, false),
            new LanNicChoice(IPAddress.Parse("10.0.0.8"), "Wi-Fi", "Intel Wireless-AC", NetworkInterfaceType.Wireless80211, true)
        };
        var chosen = LanNic.Select(choices);
        Assert.NotNull(chosen);
        Assert.Equal("10.0.0.8", chosen.Address.ToString());
        Assert.Null(LanNic.Select(Array.Empty<LanNicChoice>()));
    }

    [Fact]
    public void Firewall_rules_cover_the_app_port_and_mdns()
    {
        var explain = FirewallRules.Explain(47821, @"C:\LanBridge\LanBridge.exe");
        Assert.Contains("47821", explain, StringComparison.Ordinal);
        Assert.Contains("5353", explain, StringComparison.Ordinal);
        Assert.Contains("UDP", explain, StringComparison.Ordinal);
        var add = FirewallRules.AddArguments(47821, @"C:\LanBridge\LanBridge.exe");
        Assert.Contains(add, line => line.Contains("protocol=TCP", StringComparison.Ordinal) && line.Contains("localport=47821", StringComparison.Ordinal));
        Assert.Contains(add, line => line.Contains("protocol=UDP", StringComparison.Ordinal) && line.Contains("localport=5353", StringComparison.Ordinal));
    }

    private static byte[] BuildPtrQuery(string name, ushort id = 0)
    {
        var ms = new MemoryStream();
        void U16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(id);
        U16(0);
        U16(1);
        U16(0);
        U16(0);
        U16(0);
        foreach (var label in name.Split('.'))
        {
            var raw = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)raw.Length);
            ms.Write(raw);
        }
        ms.WriteByte(0);
        U16(12);
        U16(1);
        return ms.ToArray();
    }
}
