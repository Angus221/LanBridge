using System.Security.Cryptography;
using LanBridge.Core;
using LanBridge.Storage;
using Xunit;

namespace LanBridge.Storage.Tests;

public class StorageTests
{
    [Fact]
    public void Peers_roundtrip_revoke_and_history()
    {
        var dir = Directory.CreateTempSubdirectory("lanbridge-db").FullName;
        var db = new LanBridgeDatabase(Path.Combine(dir, "lanbridge.db"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var id = Guid.NewGuid().ToString("D");
        db.Save(new TrustedDevice
        {
            DeviceId = id,
            Name = "Mate 60",
            Platform = "android",
            PublicKeySpki = spki,
            PairedAtUnixMs = 10
        });
        var loaded = db.FindUsable(id);
        Assert.NotNull(loaded);
        Assert.Equal("Mate 60", loaded!.Name);
        Assert.Equal(spki, loaded.PublicKeySpki);
        db.Rename(id, "Mate");
        db.Touch(id, 99);
        loaded = db.FindUsable(id);
        Assert.Equal("Mate", loaded!.Name);
        Assert.Equal(99, loaded.LastSeenUnixMs);
        db.Revoke(id);
        Assert.Null(db.FindUsable(id));
        Assert.Empty(db.ListActive());

        db.Upsert(new TransferRecord
        {
            Id = "t1",
            Direction = "in",
            Kind = "file",
            Name = "a.txt",
            Size = 3,
            BytesDone = 3,
            Status = "completed",
            CreatedAt = 5,
            Sha256 = "abc",
            LocalPath = "/tmp/a.txt"
        });
        db.Upsert(new TransferRecord
        {
            Id = "t1",
            Direction = "in",
            Kind = "file",
            Name = "a.txt",
            Size = 3,
            BytesDone = 3,
            Status = "completed",
            CreatedAt = 5,
            FinishedAt = 8
        });
        var recent = db.Recent(10);
        Assert.Single(recent);
        Assert.Equal(8, recent[0].FinishedAt);
        db.Clear();
        Assert.Empty(db.Recent(10));
    }

    [Fact]
    public void Settings_roundtrip()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("lanbridge-settings").FullName, "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = store.Load();
        Assert.False(settings.ClipboardSyncEnabled);
        Assert.True(settings.FilterSensitive);
        settings.DeviceName = "书房电脑";
        settings.ClipboardSyncEnabled = true;
        settings.IncomingDirectory = "/tmp/in";
        store.Save(settings);
        var again = new JsonSettingsStore(path).Load();
        Assert.Equal("书房电脑", again.DeviceName);
        Assert.True(again.ClipboardSyncEnabled);
        Assert.Equal("/tmp/in", again.IncomingDirectory);
        Assert.Equal("Ctrl+Alt+V", again.HotkeySend);
        Assert.Equal("Ctrl+Alt+L", again.HotkeySidebar);
        again.HotkeySend = "Ctrl+Shift+V";
        again.SetClipboardEnabled("phone-a", false);
        store.Save(again);
        var third = new JsonSettingsStore(path).Load();
        Assert.Equal("Ctrl+Shift+V", third.HotkeySend);
        Assert.False(third.ClipboardEnabledFor("phone-a"));
        Assert.True(third.ClipboardEnabledFor("phone-b"));
    }
}
