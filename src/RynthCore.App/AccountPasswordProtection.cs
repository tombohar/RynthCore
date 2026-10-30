using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RynthCore.App;

/// <summary>
/// Saved account passwords are encrypted with Windows DPAPI (CryptProtectData, CurrentUser
/// scope, the same call System.Security.Cryptography.ProtectedData makes) plus an app-specific
/// entropy constant, and kept in settings as base64 (LaunchAccountProfile.PasswordProtected).
/// Only the Windows user that saved a password can decrypt it; settings copied to another user
/// or PC can't be decrypted, and such an account shows "password needs re-entering".
///
/// Decrypt only at the moment of use (building the client's launch arguments) and never keep
/// the result in a long-lived object. Never log, display or copy a password.
///
/// Shared source: compiled into the launcher, RynthCore.Injector (--launch) and
/// tools\LauncherSettingsTests.
/// </summary>
internal static class AccountPasswordProtection
{
    // Changing this makes every saved password unreadable. Bump the suffix only with a migration.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RynthCore.Launcher.AccountPassword.v1");

    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>Encrypts a password for this Windows user. Empty in, empty out.</summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        try
        {
            return Convert.ToBase64String(Transform(plain, protect: true));
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>
    /// Decrypts a value written by <see cref="Protect"/>. False when it can't be read on this
    /// Windows user (copied from another user or PC, damaged base64, wrong entropy).
    /// </summary>
    public static bool TryUnprotect(string? protectedBase64, out string plainText)
    {
        plainText = string.Empty;
        if (string.IsNullOrEmpty(protectedBase64))
            return false;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[]? plain = null;
        try
        {
            plain = Transform(blob, protect: false);
            plainText = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (plain != null)
                Array.Clear(plain);
        }
    }

    /// <summary>True when the value decrypts on this Windows user (the password itself is discarded).</summary>
    public static bool CanUnprotect(string? protectedBase64) => TryUnprotect(protectedBase64, out _);

    private static byte[] Transform(byte[] input, bool protect)
    {
        GCHandle inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inputBlob = new DataBlob { cbData = input.Length, pbData = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };

            bool ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            byte[] result = new byte[output.cbData];
            if (output.cbData > 0)
                Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                // Wipe the native copy (the plain text, when decrypting) before freeing it.
                for (int i = 0; i < output.cbData; i++)
                    Marshal.WriteByte(output.pbData, i, 0);
                LocalFree(output.pbData);
            }
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
