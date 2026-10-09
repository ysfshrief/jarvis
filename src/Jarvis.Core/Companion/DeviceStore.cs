using System.Security.Cryptography;
using System.Text;
using Jarvis.Core.Events;
using Jarvis.Core.Persistence;

namespace Jarvis.Core.Companion;

public sealed record PairedDevice(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen, string? LastAddress, DateTimeOffset? RevokedAt)
{
    public bool Active => RevokedAt is null;
}

/// <summary>
/// Phones paired with JARVIS. Pairing uses a one-time code shown on this PC (valid five minutes); the phone
/// receives a long random token once, and JARVIS keeps only its SHA-256 hash. Revoking a device ends its access.
/// </summary>
public sealed class DeviceStore(JarvisDatabase db, IEventBus events)
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private const int MaxWrongCodes = 10;
    private readonly Dictionary<string, DateTimeOffset> _codes = [];
    private int _wrong;

    /// <summary>A fresh one-time pairing code like "K7QM-3XWP".</summary>
    public (string Code, DateTimeOffset Expires) NewPairingCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"; // no 0/O, 1/I/L, U
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 8);
        var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
        var expires = DateTimeOffset.Now + CodeLifetime;
        lock (_gate)
        {
            foreach (var k in _codes.Where(kv => kv.Value < DateTimeOffset.Now).Select(kv => kv.Key).ToList()) _codes.Remove(k);
            _codes[code] = expires;
            _wrong = 0;
        }
        return (code, expires);
    }

    /// <summary>Exchanges a valid code for a device token (returned once). Null when the code is wrong or expired.</summary>
    public (PairedDevice Device, string Token)? Pair(string code, string deviceName, string? address)
    {
        code = code.Trim().ToUpperInvariant();
        lock (_gate)
        {
            if (!_codes.TryGetValue(code, out var exp) || exp < DateTimeOffset.Now)
            {
                // Someone guessing: after a few wrong codes every open code stops working until a new one is shown.
                if (++_wrong >= MaxWrongCodes) _codes.Clear();
                return null;
            }
            _codes.Remove(code); // single use
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var name = string.IsNullOrWhiteSpace(deviceName) ? "Phone" : deviceName.Trim()[..Math.Min(40, deviceName.Trim().Length)];
        var d = new PairedDevice(Guid.NewGuid().ToString("n"), name, DateTimeOffset.Now, DateTimeOffset.Now, address, null);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO devices(id, name, token_hash, created_at, last_seen, last_address) VALUES ($id, $n, $h, $t, $t, $a);";
        cmd.Parameters.AddWithValue("$id", d.Id);
        cmd.Parameters.AddWithValue("$n", d.Name);
        cmd.Parameters.AddWithValue("$h", Hash(token));
        cmd.Parameters.AddWithValue("$t", JarvisDatabase.Format(d.CreatedAt));
        cmd.Parameters.AddWithValue("$a", (object?)address ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        events.Publish(EventTypes.DevicesChanged, new { paired = d.Name });
        return (d, token);
    }

    /// <summary>The active device a token belongs to (and records that it was seen), or null.</summary>
    public PairedDevice? Authenticate(string? token, string? address)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        string? id;
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            // A 256-bit random token: looking up its hash is safe against guessing.
            cmd.CommandText = "SELECT id FROM devices WHERE token_hash = $h AND revoked_at IS NULL;";
            cmd.Parameters.AddWithValue("$h", Hash(token));
            id = cmd.ExecuteScalar() as string;
            if (id is null) return null;
            cmd.CommandText = "UPDATE devices SET last_seen = $t, last_address = $a WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$t", JarvisDatabase.Now());
            cmd.Parameters.AddWithValue("$a", (object?)address ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return List().FirstOrDefault(d => d.Id == id);
    }

    public bool Revoke(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET revoked_at = $t WHERE id = $id AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        var n = cmd.ExecuteNonQuery();
        if (n > 0) events.Publish(EventTypes.DevicesChanged, new { revoked = id });
        return n > 0;
    }

    public IReadOnlyList<PairedDevice> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, created_at, last_seen, last_address, revoked_at FROM devices ORDER BY created_at DESC;";
        using var r = cmd.ExecuteReader();
        var list = new List<PairedDevice>();
        while (r.Read())
            list.Add(new PairedDevice(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)),
                r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5))));
        return list;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
