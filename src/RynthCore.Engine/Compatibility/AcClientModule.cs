using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal readonly struct AcClientTextSection
{
    public AcClientTextSection(IntPtr moduleBase, int imageSize, int textBaseVa, byte[] bytes)
    {
        ModuleBase = moduleBase;
        ImageSize = imageSize;
        TextBaseVa = textBaseVa;
        Bytes = bytes;
    }

    public IntPtr ModuleBase { get; }
    public int ImageSize { get; }
    public int TextBaseVa { get; }
    public byte[] Bytes { get; }
}

internal static class AcClientModule
{
    private const int DefaultTextRva = 0x1000;
    private const int MaxTextSectionBytes = 0x400000;
    private const int SuspiciousTextSectionBytes = 0x500000;
    private const ushort ImageDosSignature = 0x5A4D;
    private const uint ImageNtSignature = 0x00004550;
    private const int DosHeaderPeOffset = 0x3C;
    private const int NtOptionalHeaderOffset = 0x18;
    private const int OptionalHeaderSizeOfImageOffset = 0x38;
    private static bool _loggedBaseOnce;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    public static bool TryReadTextSection(out AcClientTextSection textSection)
    {
        textSection = default;

        try
        {
            IntPtr moduleBase = GetModuleHandleW("acclient.exe");
            if (moduleBase == IntPtr.Zero)
                moduleBase = GetModuleHandleW(null);

            if (moduleBase == IntPtr.Zero)
            {
                RynthLog.Compat($"Compat: acclient.exe module not found (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            if (!TryReadImageSize(moduleBase, out int imageSize))
            {
                RynthLog.Compat("Compat: failed to read acclient.exe PE headers.");
                return false;
            }

            if (!_loggedBaseOnce)
            {
                _loggedBaseOnce = true;
                RynthLog.Verbose($"Compat: acclient.exe base=0x{moduleBase.ToInt32():X8}, size=0x{imageSize:X}");
            }

            int rawTextSize = imageSize - DefaultTextRva;
            int textSize = rawTextSize;
            if (textSize <= 0 || textSize > SuspiciousTextSectionBytes)
            {
                textSize = Math.Min(Math.Max(0, rawTextSize), MaxTextSectionBytes);
            }
            else
            {
                textSize = Math.Min(textSize, MaxTextSectionBytes);
            }

            if (textSize <= 0)
            {
                RynthLog.Compat("Compat: failed to derive a readable acclient .text window.");
                return false;
            }

            IntPtr textBase = IntPtr.Add(moduleBase, DefaultTextRva);
            byte[] bytes = new byte[textSize];
            Marshal.Copy(textBase, bytes, 0, bytes.Length);

            textSection = new AcClientTextSection(
                moduleBase,
                imageSize,
                moduleBase.ToInt32() + DefaultTextRva,
                bytes);

            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: failed to read acclient text section - {ex.Message}");
            return false;
        }
    }

    private static bool TryReadImageSize(IntPtr moduleBase, out int imageSize)
    {
        imageSize = 0;

        if ((ushort)Marshal.ReadInt16(moduleBase) != ImageDosSignature)
            return false;

        int peOffset = Marshal.ReadInt32(moduleBase, DosHeaderPeOffset);
        if (peOffset <= 0)
            return false;

        IntPtr ntHeaders = IntPtr.Add(moduleBase, peOffset);
        if ((uint)Marshal.ReadInt32(ntHeaders) != ImageNtSignature)
            return false;

        imageSize = Marshal.ReadInt32(ntHeaders, NtOptionalHeaderOffset + OptionalHeaderSizeOfImageOffset);
        return imageSize > DefaultTextRva;
    }
}
