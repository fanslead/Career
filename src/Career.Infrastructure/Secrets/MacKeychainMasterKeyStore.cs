using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Career.Infrastructure.Secrets;

internal static partial class MacKeychainMasterKeyStore
{
    private const int KeyLength = 32;
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private const byte UserInteractionDisallowed = 0;
    private static readonly object UserInteractionLock = new();

    public static byte[] GetOrCreate(string workspaceId, bool createIfMissing)
    {
        lock (UserInteractionLock)
        {
            int status = Security.SecKeychainGetUserInteractionAllowed(out byte originalUserInteractionAllowed);
            if (status != Success)
            {
                throw new InvalidOperationException($"macOS Keychain user-interaction state could not be read (status {status}).");
            }

            byte[]? key = null;
            try
            {
                status = Security.SecKeychainSetUserInteractionAllowed(UserInteractionDisallowed);
                if (status != Success)
                {
                    throw new InvalidOperationException($"macOS Keychain user interaction could not be disabled (status {status}).");
                }

                key = GetOrCreateWithoutUserInteraction(workspaceId, createIfMissing);
            }
            finally
            {
                status = Security.SecKeychainSetUserInteractionAllowed(originalUserInteractionAllowed);
                if (status != Success)
                {
                    if (key is not null)
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }

                    throw new InvalidOperationException($"macOS Keychain user-interaction state could not be restored (status {status}).");
                }
            }

            return key!;
        }
    }

    private static byte[] GetOrCreateWithoutUserInteraction(string workspaceId, bool createIfMissing)
    {
        if (TryRead(workspaceId, out byte[]? existingKey))
        {
            return existingKey!;
        }

        if (!createIfMissing)
        {
            throw new InvalidOperationException("Workspace vault master key is missing from macOS Keychain; refusing to generate a replacement.");
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            using CfObjectHandle attributes = CreateItemDictionary(workspaceId, key);
            int status = Security.SecItemAdd(attributes, IntPtr.Zero);
            if (status == Success)
            {
                return key;
            }

            CryptographicOperations.ZeroMemory(key);
            if (status == DuplicateItem && TryRead(workspaceId, out existingKey))
            {
                return existingKey!;
            }

            throw new InvalidOperationException($"macOS Keychain could not store the workspace vault master key (status {status}).");
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static bool TryRead(string workspaceId, out byte[]? key)
    {
        using CfObjectHandle query = CreateQueryDictionary(workspaceId);
        int status = Security.SecItemCopyMatching(query, out IntPtr result);
        if (status == ItemNotFound)
        {
            key = null;
            return false;
        }

        if (status != Success)
        {
            throw new InvalidOperationException($"macOS Keychain could not read the workspace vault master key (status {status}).");
        }

        using var data = new CfObjectHandle(result);
        nint length = CoreFoundation.CFDataGetLength(data);
        if (length != KeyLength)
        {
            throw new InvalidDataException("macOS Keychain workspace vault master key has an invalid length.");
        }

        IntPtr bytes = CoreFoundation.CFDataGetBytePtr(data);
        if (bytes == IntPtr.Zero)
        {
            throw new InvalidDataException("macOS Keychain workspace vault master key could not be read.");
        }

        key = new byte[KeyLength];
        Marshal.Copy(bytes, key, 0, key.Length);
        return true;
    }

    private static CfObjectHandle CreateQueryDictionary(string workspaceId)
    {
        CfObjectHandle dictionary = CreateDictionary();
        try
        {
            SetCommonAttributes(dictionary, workspaceId);
            CoreFoundation.CFDictionarySetValueRaw(dictionary, Security.ReturnDataKey, Security.BooleanTrue);
            CoreFoundation.CFDictionarySetValueRaw(dictionary, Security.MatchLimitKey, Security.MatchLimitOne);
            return dictionary;
        }
        catch
        {
            dictionary.Dispose();
            throw;
        }
    }

    private static CfObjectHandle CreateItemDictionary(string workspaceId, byte[] key)
    {
        CfObjectHandle dictionary = CreateDictionary();
        try
        {
            SetCommonAttributes(dictionary, workspaceId);
            using CfObjectHandle value = CoreFoundation.CreateData(key);
            CoreFoundation.CFDictionarySetValue(dictionary, Security.ValueDataKey, value);
            return dictionary;
        }
        catch
        {
            dictionary.Dispose();
            throw;
        }
    }

    private static CfObjectHandle CreateDictionary()
    {
        IntPtr dictionary = CoreFoundation.CFDictionaryCreateMutable(
            IntPtr.Zero, 0, CoreFoundation.DictionaryKeyCallbacks, CoreFoundation.DictionaryValueCallbacks);
        return dictionary == IntPtr.Zero
            ? throw new InvalidOperationException("macOS Keychain query could not be created.")
            : new CfObjectHandle(dictionary);
    }

    private static void SetCommonAttributes(CfObjectHandle dictionary, string workspaceId)
    {
        using CfObjectHandle service = CoreFoundation.CreateString("Career.WorkspaceVault");
        using CfObjectHandle account = CoreFoundation.CreateString(workspaceId);
        CoreFoundation.CFDictionarySetValueRaw(dictionary, Security.ClassKey, Security.GenericPasswordClass);
        CoreFoundation.CFDictionarySetValue(dictionary, Security.ServiceKey, service);
        CoreFoundation.CFDictionarySetValue(dictionary, Security.AccountKey, account);
    }

    private sealed class CfObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CfObjectHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle()
        {
            CoreFoundation.CFRelease(handle);
            return true;
        }
    }

    private sealed class NativeLibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeLibraryHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        public static NativeLibraryHandle Load(string path) => new(NativeLibrary.Load(path));

        protected override bool ReleaseHandle()
        {
            NativeLibrary.Free(handle);
            return true;
        }
    }

    private static partial class CoreFoundation
    {
        private const string Framework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8Encoding = 0x08000100;
        private static readonly NativeLibraryHandle Library = NativeLibraryHandle.Load(Framework);

        internal static readonly IntPtr DictionaryKeyCallbacks = NativeLibrary.GetExport(
            Library.DangerousGetHandle(), "kCFTypeDictionaryKeyCallBacks");
        internal static readonly IntPtr DictionaryValueCallbacks = NativeLibrary.GetExport(
            Library.DangerousGetHandle(), "kCFTypeDictionaryValueCallBacks");

        public static CfObjectHandle CreateString(string value)
        {
            IntPtr result = Imports.CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);
            return result == IntPtr.Zero
                ? throw new InvalidOperationException("macOS Keychain string could not be created.")
                : new CfObjectHandle(result);
        }

        public static CfObjectHandle CreateData(byte[] bytes)
        {
            IntPtr result = Imports.CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            return result == IntPtr.Zero
                ? throw new InvalidOperationException("macOS Keychain key data could not be created.")
                : new CfObjectHandle(result);
        }

        public static IntPtr GetConstant(string name)
        {
            IntPtr symbol = NativeLibrary.GetExport(Library.DangerousGetHandle(), name);
            return Marshal.ReadIntPtr(symbol);
        }

        private static partial class Imports
        {
            [LibraryImport(Framework, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
            internal static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

            [LibraryImport(Framework, EntryPoint = "CFDataCreate")]
            internal static partial IntPtr CFDataCreate(IntPtr allocator, [In] byte[] bytes, nint length);

            [LibraryImport(Framework, EntryPoint = "CFDictionaryCreateMutable")]
            internal static partial IntPtr CFDictionaryCreateMutable(
                IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);

            [LibraryImport(Framework, EntryPoint = "CFDictionarySetValue")]
            internal static partial void CFDictionarySetValue(CfObjectHandle dictionary, IntPtr key, CfObjectHandle value);

            [LibraryImport(Framework, EntryPoint = "CFDictionarySetValue")]
            internal static partial void CFDictionarySetValueRaw(CfObjectHandle dictionary, IntPtr key, IntPtr value);

            [LibraryImport(Framework, EntryPoint = "CFDataGetLength")]
            internal static partial nint CFDataGetLength(CfObjectHandle data);

            [LibraryImport(Framework, EntryPoint = "CFDataGetBytePtr")]
            internal static partial IntPtr CFDataGetBytePtr(CfObjectHandle data);

            [LibraryImport(Framework, EntryPoint = "CFRelease")]
            internal static partial void CFRelease(IntPtr cfObject);
        }

        internal static IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks)
            => Imports.CFDictionaryCreateMutable(allocator, capacity, keyCallbacks, valueCallbacks);
        internal static void CFDictionarySetValue(CfObjectHandle dictionary, IntPtr key, CfObjectHandle value)
            => Imports.CFDictionarySetValue(dictionary, key, value);
        internal static void CFDictionarySetValueRaw(CfObjectHandle dictionary, IntPtr key, IntPtr value)
            => Imports.CFDictionarySetValueRaw(dictionary, key, value);
        internal static nint CFDataGetLength(CfObjectHandle data) => Imports.CFDataGetLength(data);
        internal static IntPtr CFDataGetBytePtr(CfObjectHandle data) => Imports.CFDataGetBytePtr(data);
        internal static void CFRelease(IntPtr cfObject) => Imports.CFRelease(cfObject);
    }

    private static partial class Security
    {
        private const string Framework = "/System/Library/Frameworks/Security.framework/Security";
        private static readonly NativeLibraryHandle Library = NativeLibraryHandle.Load(Framework);

        internal static readonly IntPtr ClassKey = Constant("kSecClass");
        internal static readonly IntPtr GenericPasswordClass = Constant("kSecClassGenericPassword");
        internal static readonly IntPtr ServiceKey = Constant("kSecAttrService");
        internal static readonly IntPtr AccountKey = Constant("kSecAttrAccount");
        internal static readonly IntPtr ValueDataKey = Constant("kSecValueData");
        internal static readonly IntPtr ReturnDataKey = Constant("kSecReturnData");
        internal static readonly IntPtr MatchLimitKey = Constant("kSecMatchLimit");
        internal static readonly IntPtr MatchLimitOne = Constant("kSecMatchLimitOne");
        internal static readonly IntPtr BooleanTrue = CoreFoundation.GetConstant("kCFBooleanTrue");

        private static IntPtr Constant(string name)
        {
            IntPtr symbol = NativeLibrary.GetExport(Library.DangerousGetHandle(), name);
            return Marshal.ReadIntPtr(symbol);
        }

        private static partial class Imports
        {
            [LibraryImport(Framework, EntryPoint = "SecKeychainGetUserInteractionAllowed")]
            internal static partial int SecKeychainGetUserInteractionAllowed(out byte state);

            [LibraryImport(Framework, EntryPoint = "SecKeychainSetUserInteractionAllowed")]
            internal static partial int SecKeychainSetUserInteractionAllowed(byte state);

            [LibraryImport(Framework, EntryPoint = "SecItemAdd")]
            internal static partial int SecItemAdd(CfObjectHandle attributes, IntPtr result);

            [LibraryImport(Framework, EntryPoint = "SecItemCopyMatching")]
            internal static partial int SecItemCopyMatching(CfObjectHandle query, out IntPtr result);
        }

        internal static int SecKeychainGetUserInteractionAllowed(out byte state)
            => Imports.SecKeychainGetUserInteractionAllowed(out state);
        internal static int SecKeychainSetUserInteractionAllowed(byte state)
            => Imports.SecKeychainSetUserInteractionAllowed(state);
        internal static int SecItemAdd(CfObjectHandle attributes, IntPtr result) => Imports.SecItemAdd(attributes, result);
        internal static int SecItemCopyMatching(CfObjectHandle query, out IntPtr result) => Imports.SecItemCopyMatching(query, out result);
    }
}
