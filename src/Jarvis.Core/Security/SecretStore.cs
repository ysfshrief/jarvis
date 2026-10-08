using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core.Security;

/// <summary>Encrypts/decrypts small blobs for the current user.</summary>
public interface ISecretProtector
{
    string Name { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Write-only store for API keys and other secrets. Values never go back to the UI.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    bool Remove(string name);
    IReadOnlyList<string> Names();
    string ProtectionName { get; }
}

/// <summary>Secret dictionary persisted as a single encrypted file.</summary>
public sealed class FileSecretStore(JarvisPaths paths, ISecretProtector protector) : ISecretStore
{
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    public string ProtectionName => protector.Name;

    public string? Get(string name)
    {
        lock (_gate) return Load().GetValueOrDefault(name);
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            var all = Load();
            all[name] = value;
            Save(all);
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(name)) return false;
            Save(all);
            return true;
        }
    }

    public IReadOnlyList<string> Names()
    {
        lock (_gate) return Load().Keys.Order().ToList();
    }

    private Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(paths.SecretsPath)) return _cache = new();
        var plain = protector.Unprotect(File.ReadAllBytes(paths.SecretsPath));
        return _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
    }

    private void Save(Dictionary<string, string> all)
    {
        var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(all));
        var tmp = paths.SecretsPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, paths.SecretsPath, overwrite: true);
        _cache = all;
    }
}

/// <summary>
/// Fallback protector for non-Windows development: AES-GCM with a random key stored in a
/// user-only file next to the data. Windows builds use DPAPI instead (bound to the Windows account).
/// </summary>
public sealed class KeyFileProtector : ISecretProtector
{
    private readonly byte[] _key;

    public KeyFileProtector(JarvisPaths paths)
    {
        var keyPath = Path.Combine(paths.DataDir, "secret.key");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, _key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string Name => "AES-GCM key file";

    public byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data)
    {
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(12, 16);
        var cipher = data.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

/// <summary>PIN hashing for the optional dashboard lock (PBKDF2-SHA256).</summary>
public static class PinHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
