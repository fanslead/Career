using System.Security.Cryptography;
using System.Text;

namespace Career.Infrastructure.Secrets;

internal static class MasterKeyProvider
{
    private const int KeyLength = 32;
    private const int MaximumTextKeyLength = 4096;

    public static ValueTask<byte[]> GetAsync(
        string workspaceDirectory,
        string workspaceId,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsMacOS())
        {
            return ValueTask.FromResult(MacKeychainMasterKeyStore.GetOrCreate(workspaceId, createIfMissing));
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsDpapiMasterKeyStore.GetOrCreateAsync(workspaceDirectory, createIfMissing, cancellationToken);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxKeyFileAsync(cancellationToken);
        }

        throw new PlatformNotSupportedException("Encrypted workspace secrets require macOS, Windows, or Linux.");
    }

    private static async ValueTask<byte[]> ReadLinuxKeyFileAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("CAREER_VAULT_KEY_FILE is supported only on Linux.");
        }

        string? configuredPath = Environment.GetEnvironmentVariable("CAREER_VAULT_KEY_FILE");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("CAREER_VAULT_KEY_FILE must point to a protected 32-byte or base64 workspace key file.");
        }

        string path = Path.GetFullPath(configuredPath);
        try
        {
            var fileInfo = new FileInfo(path);
            FileSystemInfo? target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                path = target.FullName;
            }

            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if ((mode & UnixFileMode.UserRead) == 0 || (mode & ~allowed) != 0)
            {
                throw new InvalidOperationException("CAREER_VAULT_KEY_FILE must be owner-readable with mode 0600 or 0400 and no group/other access.");
            }

            fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length is <= 0 or > MaximumTextKeyLength)
            {
                throw new InvalidOperationException("CAREER_VAULT_KEY_FILE must contain a 32-byte or base64-encoded key.");
            }

            byte[] fileBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (fileBytes.Length == KeyLength)
            {
                return fileBytes;
            }

            try
            {
                if (fileBytes.Any(value => value > 0x7f))
                {
                    throw new InvalidOperationException("CAREER_VAULT_KEY_FILE must contain a 32-byte or base64-encoded key.");
                }

                string encoded = Encoding.ASCII.GetString(fileBytes).Trim();
                byte[] key;
                try
                {
                    key = Convert.FromBase64String(encoded);
                }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException("CAREER_VAULT_KEY_FILE does not contain a valid base64 key.", exception);
                }

                if (key.Length != KeyLength)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException("CAREER_VAULT_KEY_FILE key must decode to exactly 32 bytes.");
                }

                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileBytes);
            }
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException("CAREER_VAULT_KEY_FILE does not exist; refusing to create or replace a workspace key.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new InvalidOperationException("CAREER_VAULT_KEY_FILE does not exist; refusing to create or replace a workspace key.", exception);
        }
    }
}
