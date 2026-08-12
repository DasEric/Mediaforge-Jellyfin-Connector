using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>Encrypts the MediaForge API key outside Jellyfin's public plugin configuration.</summary>
public sealed class SecretStore
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(Plugin.PluginGuid);
    private readonly object _sync = new();
    private readonly string _keyPath;
    private readonly string _secretPath;

    public SecretStore(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        Directory.CreateDirectory(dataPath);
        _keyPath = Path.Combine(dataPath, "connector-secret.key");
        _secretPath = Path.Combine(dataPath, "mediaforge-api-key.bin");
    }

    public bool HasApiKey => GetApiKey() is not null;

    public string? GetApiKey()
    {
        lock (_sync)
        {
            if (!File.Exists(_secretPath) || !File.Exists(_keyPath))
            {
                return null;
            }

            byte[]? key = null;
            try
            {
                key = File.ReadAllBytes(_keyPath);
                var payload = File.ReadAllBytes(_secretPath);
                if (key.Length != KeySize || payload.Length <= 1 + NonceSize + TagSize || payload[0] != 1)
                {
                    return null;
                }

                var nonce = payload.AsSpan(1, NonceSize);
                var tag = payload.AsSpan(1 + NonceSize, TagSize);
                var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);
                var plaintext = new byte[ciphertext.Length];
                try
                {
                    using var aes = new AesGcm(key, TagSize);
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
                    var value = Encoding.UTF8.GetString(plaintext);
                    return IsValid(value) ? value : null;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Fail closed. Do not include file contents or cryptographic details in logs/exceptions.
                return null;
            }
            finally
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
    }

    public void SetApiKey(string apiKey)
    {
        var value = apiKey?.Trim() ?? string.Empty;
        if (!IsValid(value))
        {
            throw new ArgumentException("Der API-Key ist leer, zu lang oder enthält ungültige Steuerzeichen.", nameof(apiKey));
        }

        lock (_sync)
        {
            var key = LoadOrCreateKey();
            var plaintext = Encoding.UTF8.GetBytes(value);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagSize];
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

                var payload = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
                payload[0] = 1;
                nonce.CopyTo(payload, 1);
                tag.CopyTo(payload, 1 + nonce.Length);
                ciphertext.CopyTo(payload, 1 + nonce.Length + tag.Length);
                WriteAtomic(_secretPath, payload);
                RestrictUnixPermissions(_secretPath);
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public void ClearApiKey()
    {
        lock (_sync)
        {
            if (File.Exists(_secretPath))
            {
                File.Delete(_secretPath);
            }
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length != KeySize)
            {
                CryptographicOperations.ZeroMemory(existing);
                throw new CryptographicException("Invalid connector key file.");
            }

            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            using var stream = new FileStream(_keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(key);
            stream.Flush(flushToDisk: true);
            RestrictUnixPermissions(_keyPath);
            return key;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            CryptographicOperations.ZeroMemory(key);
            return File.ReadAllBytes(_keyPath);
        }
    }

    private static void WriteAtomic(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Secret directory is unavailable.");
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsValid(string value)
        => value.Length is > 0 and <= 512
            && value.All(character => character is >= '!' and <= '~');

    private static void RestrictUnixPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
