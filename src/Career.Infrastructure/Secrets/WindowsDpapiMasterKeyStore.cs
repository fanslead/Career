using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Career.Infrastructure.Secrets;

internal static class WindowsDpapiMasterKeyStore
{
    private const int KeyLength = 32;
    internal const int MaximumProtectedKeyLength = 1024 * 1024;
    private const string ProtectedKeyFileName = "vault.key.dpapi";

    public static async ValueTask<byte[]> GetOrCreateAsync(
        string workspaceDirectory,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(workspaceDirectory, ProtectedKeyFileName);
        if (File.Exists(path))
        {
            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (!createIfMissing)
        {
            throw new InvalidOperationException("Workspace vault master key is missing; refusing to generate a replacement.");
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        byte[]? protectedKey = null;
        try
        {
            protectedKey = WindowsDpapi.Protect(key);
            try
            {
                await AtomicFile.WriteAsync(workspaceDirectory, path, protectedKey,
                    overwrite: false, cancellationToken).ConfigureAwait(false);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally
        {
            if (protectedKey is not null)
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
    }

    private static async ValueTask<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] protectedKey = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (protectedKey.Length is <= 0 or > MaximumProtectedKeyLength)
            {
                throw new InvalidDataException("DPAPI workspace vault key file is corrupt.");
            }

            byte[] key = WindowsDpapi.Unprotect(protectedKey);
            if (key.Length == KeyLength)
            {
                return key;
            }

            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("DPAPI workspace vault key has an invalid length.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }
}

internal static partial class WindowsDpapi
{
    private const uint UiForbidden = 0x1;

    public static unsafe byte[] Protect(byte[] plaintext)
    {
        fixed (byte* inputPointer = plaintext)
        {
            DataBlob input = new(plaintext.Length, (IntPtr)inputPointer);
            if (NativeMethods.CryptProtectData(in input, "Career.WorkspaceVault", IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, UiForbidden, out DataBlob output) == 0)
            {
                throw new CryptographicException($"Windows DPAPI could not protect the workspace vault key (error {Marshal.GetLastPInvokeError()}).");
            }

            using var outputMemory = new LocalAllocHandle(output.Data);
            if (output.Size <= 0 || output.Size > WindowsDpapiMasterKeyStore.MaximumProtectedKeyLength)
            {
                throw new CryptographicException("Windows DPAPI returned an invalid protected key length.");
            }

            byte[] protectedBytes = new byte[output.Size];
            Marshal.Copy(output.Data, protectedBytes, 0, protectedBytes.Length);
            return protectedBytes;
        }
    }

    public static unsafe byte[] Unprotect(byte[] protectedBytes)
    {
        fixed (byte* inputPointer = protectedBytes)
        {
            DataBlob input = new(protectedBytes.Length, (IntPtr)inputPointer);
            if (NativeMethods.CryptUnprotectData(in input, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, UiForbidden, out DataBlob output) == 0)
            {
                throw new CryptographicException($"Windows DPAPI could not unprotect the workspace vault key (error {Marshal.GetLastPInvokeError()}).");
            }

            using var outputMemory = new LocalAllocHandle(output.Data);
            try
            {
                if (output.Size <= 0 || output.Size > WindowsDpapiMasterKeyStore.MaximumProtectedKeyLength)
                {
                    throw new CryptographicException("Windows DPAPI returned an invalid workspace key length.");
                }

                byte[] plaintext = new byte[output.Size];
                Marshal.Copy(output.Data, plaintext, 0, plaintext.Length);
                return plaintext;
            }
            finally
            {
                if (output.Data != IntPtr.Zero && output.Size > 0 &&
                    output.Size <= WindowsDpapiMasterKeyStore.MaximumProtectedKeyLength)
                {
                    CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Size));
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int size, IntPtr data)
    {
        public readonly int Size = size;
        public readonly IntPtr Data = data;
    }

    private sealed class LocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LocalAllocHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => NativeMethods.LocalFree(handle) == IntPtr.Zero;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("Crypt32.dll", EntryPoint = "CryptProtectData", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial int CryptProtectData(
            in DataBlob input, string description, IntPtr optionalEntropy, IntPtr reserved,
            IntPtr prompt, uint flags, out DataBlob output);

        [LibraryImport("Crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
        internal static partial int CryptUnprotectData(
            in DataBlob input, IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
            IntPtr prompt, uint flags, out DataBlob output);

        [LibraryImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
        internal static partial IntPtr LocalFree(IntPtr memory);
    }
}
