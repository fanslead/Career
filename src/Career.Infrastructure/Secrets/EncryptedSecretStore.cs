using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Career.Core;

namespace Career.Infrastructure.Secrets;

public sealed class EncryptedSecretStore : ISecretStore
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] FileHeader = "CRVLT001"u8.ToArray();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string stateDirectory;
    private readonly string workspaceId;
    private readonly string vaultPath;
    private readonly SemaphoreSlim gate;

    public EncryptedSecretStore(string workspaceDirectory, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        stateDirectory = Path.Combine(Path.GetFullPath(workspaceDirectory), "state");
        this.workspaceId = workspaceId;
        vaultPath = Path.Combine(stateDirectory, "vault.enc");
        gate = Gates.GetOrAdd(vaultPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadedVault? loaded = await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return null;
            }

            try
            {
                return loaded.Secrets.TryGetValue(reference, out string? secret) ? secret : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loaded.Key);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveAsync(string reference, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(secret);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AtomicFile.EnsurePrivateDirectory(stateDirectory);
            LoadedVault? loaded = await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> secrets = loaded?.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
            byte[] key = loaded?.Key ?? await MasterKeyProvider.GetAsync(
                stateDirectory, workspaceId, createIfMissing: true, cancellationToken).ConfigureAwait(false);
            byte[]? encrypted = null;

            try
            {
                secrets[reference] = secret;
                encrypted = Encrypt(secrets, key);
                await AtomicFile.WriteAsync(stateDirectory, vaultPath, encrypted,
                    overwrite: loaded is not null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (encrypted is not null)
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadedVault? loaded = await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return;
            }

            byte[]? encrypted = null;
            try
            {
                if (!loaded.Secrets.Remove(reference))
                {
                    return;
                }

                encrypted = Encrypt(loaded.Secrets, loaded.Key);
                await AtomicFile.WriteAsync(stateDirectory, vaultPath, encrypted,
                    overwrite: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loaded.Key);
                if (encrypted is not null)
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LoadedVault?> LoadExistingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(vaultPath))
        {
            return null;
        }

        AtomicFile.EnsurePrivateDirectory(stateDirectory);
        byte[] encrypted = await File.ReadAllBytesAsync(vaultPath, cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] key = await MasterKeyProvider.GetAsync(
                stateDirectory, workspaceId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            try
            {
                return new LoadedVault(Decrypt(encrypted, key), key);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private byte[] Encrypt(Dictionary<string, string> secrets, byte[] key)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, VaultJsonContext.Default.SecretDictionary);
        byte[] associatedData = CreateAssociatedData();
        byte[] output = new byte[FileHeader.Length + NonceLength + TagLength + plaintext.Length];
        try
        {
            FileHeader.CopyTo(output, 0);
            Span<byte> nonce = output.AsSpan(FileHeader.Length, NonceLength);
            RandomNumberGenerator.Fill(nonce);
            Span<byte> tag = output.AsSpan(FileHeader.Length + NonceLength, TagLength);
            Span<byte> ciphertext = output.AsSpan(FileHeader.Length + NonceLength + TagLength);

            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return output;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private Dictionary<string, string> Decrypt(byte[] encrypted, byte[] key)
    {
        int minimumLength = FileHeader.Length + NonceLength + TagLength;
        if (encrypted.Length <= minimumLength || !encrypted.AsSpan(0, FileHeader.Length).SequenceEqual(FileHeader))
        {
            throw new InvalidDataException("Encrypted vault has an invalid format.");
        }

        ReadOnlySpan<byte> nonce = encrypted.AsSpan(FileHeader.Length, NonceLength);
        ReadOnlySpan<byte> tag = encrypted.AsSpan(FileHeader.Length + NonceLength, TagLength);
        ReadOnlySpan<byte> ciphertext = encrypted.AsSpan(minimumLength);
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] associatedData = CreateAssociatedData();
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return JsonSerializer.Deserialize(plaintext, VaultJsonContext.Default.SecretDictionary)
                ?? throw new InvalidDataException("Encrypted vault does not contain a secret map.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Encrypted vault authentication failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Encrypted vault JSON is corrupt.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private byte[] CreateAssociatedData()
    {
        byte[] workspaceBytes = Encoding.UTF8.GetBytes(workspaceId);
        byte[] associatedData = new byte[FileHeader.Length + workspaceBytes.Length];
        FileHeader.CopyTo(associatedData, 0);
        workspaceBytes.CopyTo(associatedData, FileHeader.Length);
        CryptographicOperations.ZeroMemory(workspaceBytes);
        return associatedData;
    }

    private sealed record LoadedVault(Dictionary<string, string> Secrets, byte[] Key);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "SecretDictionary")]
internal partial class VaultJsonContext : JsonSerializerContext;

internal static class AtomicFile
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static async Task WriteAsync(
        string directory,
        string destination,
        byte[] content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the original write failure; stale temporary files contain ciphertext only.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original write failure; stale temporary files contain ciphertext only.
            }
        }
    }

    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }
}
