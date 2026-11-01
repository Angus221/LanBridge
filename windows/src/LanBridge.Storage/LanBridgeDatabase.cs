using LanBridge.Core;
using Microsoft.Data.Sqlite;

namespace LanBridge.Storage;

public sealed class LanBridgeDatabase : ITrustedDeviceStore, ITransferHistory, IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();

    public LanBridgeDatabase(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version(version)
                SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            CREATE TABLE IF NOT EXISTS peers (
                device_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                platform TEXT NOT NULL,
                public_key_spki BLOB NOT NULL,
                paired_at INTEGER NOT NULL,
                last_seen INTEGER,
                revoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS transfers (
                id TEXT PRIMARY KEY,
                direction TEXT NOT NULL,
                kind TEXT NOT NULL,
                peer_id TEXT,
                peer_name TEXT,
                display_name TEXT NOT NULL,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                bytes_done INTEGER NOT NULL DEFAULT 0,
                sha256 TEXT,
                local_path TEXT,
                status TEXT NOT NULL,
                error TEXT,
                created_at INTEGER NOT NULL,
                finished_at INTEGER,
                text_preview TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    public TrustedDevice? FindUsable(string deviceId)
    {
        var found = FindPeer(deviceId);
        return found is { Revoked: false } ? found : null;
    }

    public IReadOnlyList<TrustedDevice> ListActive()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM peers WHERE revoked = 0 ORDER BY paired_at DESC";
            using var reader = command.ExecuteReader();
            var list = new List<TrustedDevice>();
            while (reader.Read()) list.Add(ReadPeer(reader));
            return list;
        }
    }

    public void Save(TrustedDevice device)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO peers(device_id, name, platform, public_key_spki, paired_at, last_seen, revoked)
                VALUES ($id, $name, $platform, $key, $paired, $seen, 0)
                ON CONFLICT(device_id) DO UPDATE SET
                    name = excluded.name,
                    platform = excluded.platform,
                    public_key_spki = excluded.public_key_spki,
                    paired_at = excluded.paired_at,
                    revoked = 0;
                """;
            command.Parameters.AddWithValue("$id", device.DeviceId);
            command.Parameters.AddWithValue("$name", device.Name);
            command.Parameters.AddWithValue("$platform", device.Platform);
            command.Parameters.Add("$key", SqliteType.Blob).Value = device.PublicKeySpki;
            command.Parameters.AddWithValue("$paired", device.PairedAtUnixMs);
            command.Parameters.AddWithValue("$seen", (object?)device.LastSeenUnixMs ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void Revoke(string deviceId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE peers SET revoked = 1 WHERE device_id = $id";
            command.Parameters.AddWithValue("$id", deviceId);
            command.ExecuteNonQuery();
        }
    }

    public void Touch(string deviceId, long unixMs)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE peers SET last_seen = $seen WHERE device_id = $id";
            command.Parameters.AddWithValue("$seen", unixMs);
            command.Parameters.AddWithValue("$id", deviceId);
            command.ExecuteNonQuery();
        }
    }

    public void Rename(string deviceId, string name)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE peers SET name = $name WHERE device_id = $id";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$id", deviceId);
            command.ExecuteNonQuery();
        }
    }

    public void Upsert(TransferRecord record)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO transfers(id, direction, kind, peer_id, peer_name, display_name, size_bytes, bytes_done, sha256, local_path, status, error, created_at, finished_at, text_preview)
                VALUES ($id, $dir, $kind, $peer, $peerName, $name, $size, $done, $sha, $path, $status, $error, $created, $finished, $preview)
                ON CONFLICT(id) DO UPDATE SET
                    direction = excluded.direction,
                    kind = excluded.kind,
                    peer_id = excluded.peer_id,
                    peer_name = excluded.peer_name,
                    display_name = excluded.display_name,
                    size_bytes = excluded.size_bytes,
                    bytes_done = excluded.bytes_done,
                    sha256 = excluded.sha256,
                    local_path = excluded.local_path,
                    status = excluded.status,
                    error = excluded.error,
                    finished_at = excluded.finished_at,
                    text_preview = excluded.text_preview;
                """;
            Bind(command, record);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TransferRecord> Recent(int limit)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM transfers ORDER BY created_at DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<TransferRecord>();
            while (reader.Read()) list.Add(ReadTransfer(reader));
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM transfers";
            command.ExecuteNonQuery();
        }
    }

    public TransferRecord? Find(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM transfers WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTransfer(reader) : null;
        }
    }

    public void Dispose() { }

    private TrustedDevice? FindPeer(string deviceId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM peers WHERE device_id = $id";
            command.Parameters.AddWithValue("$id", deviceId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPeer(reader) : null;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Bind(SqliteCommand command, TransferRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$dir", record.Direction);
        command.Parameters.AddWithValue("$kind", record.Kind);
        command.Parameters.AddWithValue("$peer", (object?)record.PeerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$peerName", (object?)record.PeerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$done", record.BytesDone);
        command.Parameters.AddWithValue("$sha", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)record.LocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedAt);
        command.Parameters.AddWithValue("$finished", (object?)record.FinishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$preview", (object?)record.TextPreview ?? DBNull.Value);
    }

    private static TrustedDevice ReadPeer(SqliteDataReader reader) => new()
    {
        DeviceId = reader.GetString(reader.GetOrdinal("device_id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Platform = reader.GetString(reader.GetOrdinal("platform")),
        PublicKeySpki = (byte[])reader["public_key_spki"],
        PairedAtUnixMs = reader.GetInt64(reader.GetOrdinal("paired_at")),
        LastSeenUnixMs = reader.IsDBNull(reader.GetOrdinal("last_seen")) ? null : reader.GetInt64(reader.GetOrdinal("last_seen")),
        Revoked = reader.GetInt64(reader.GetOrdinal("revoked")) != 0
    };

    private static TransferRecord ReadTransfer(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Direction = reader.GetString(reader.GetOrdinal("direction")),
        Kind = reader.GetString(reader.GetOrdinal("kind")),
        PeerId = reader.IsDBNull(reader.GetOrdinal("peer_id")) ? null : reader.GetString(reader.GetOrdinal("peer_id")),
        PeerName = reader.IsDBNull(reader.GetOrdinal("peer_name")) ? null : reader.GetString(reader.GetOrdinal("peer_name")),
        Name = reader.GetString(reader.GetOrdinal("display_name")),
        Size = reader.GetInt64(reader.GetOrdinal("size_bytes")),
        BytesDone = reader.GetInt64(reader.GetOrdinal("bytes_done")),
        Sha256 = reader.IsDBNull(reader.GetOrdinal("sha256")) ? null : reader.GetString(reader.GetOrdinal("sha256")),
        LocalPath = reader.IsDBNull(reader.GetOrdinal("local_path")) ? null : reader.GetString(reader.GetOrdinal("local_path")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
        CreatedAt = reader.GetInt64(reader.GetOrdinal("created_at")),
        FinishedAt = reader.IsDBNull(reader.GetOrdinal("finished_at")) ? null : reader.GetInt64(reader.GetOrdinal("finished_at")),
        TextPreview = reader.IsDBNull(reader.GetOrdinal("text_preview")) ? null : reader.GetString(reader.GetOrdinal("text_preview"))
    };
}

public sealed class JsonSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonSettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, ProtocolJson.Options) ?? new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var json = System.Text.Json.JsonSerializer.Serialize(settings, ProtocolJson.Options);
            File.WriteAllText(_path, json);
        }
    }
}
