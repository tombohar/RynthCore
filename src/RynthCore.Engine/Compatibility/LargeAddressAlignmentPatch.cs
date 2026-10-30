using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Rewrites acclient's signed "pointer % 4" padding (<see cref="LargeAddressAlignmentScan"/>)
/// to <c>and r, 3</c> in memory, so pack/unpack of buffers above 2 GB pads 0..3 bytes like
/// it does below 2 GB. Without it, a large-address-aware client corrupts dat and network
/// unpacks whenever the heap hands AC a buffer above 0x80000000 (long open-world sessions).
///
/// The patch is never undone: an unloading engine generation leaves it in place and the next
/// generation finds every site already patched. acclient.exe on disk is not touched.
/// </summary>
internal static class LargeAddressAlignmentPatch
{
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const ushort ImageDosSignature = 0x5A4D;
    private const uint ImageNtSignature = 0x00004550;
    private const int SectionHeaderSize = 40;

    private static string _statusMessage = "Not applied yet.";

    public static string StatusMessage => _statusMessage;

    /// <summary>Sites rewritten by this engine generation.</summary>
    public static int PatchedCount { get; private set; }

    /// <summary>Sites a previous engine generation had already rewritten.</summary>
    public static int AlreadyPatchedCount { get; private set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    public static void Initialize()
    {
        if (!Plugins.EngineSettings.EnableLargeAddressAlignmentFix)
        {
            _statusMessage = "Disabled via engine.json (EnableLargeAddressAlignmentFix=false).";
            RynthLog.Info($"Compat: large-address alignment fix - {_statusMessage}");
            return;
        }

        IntPtr moduleBase = GetModuleHandleW("acclient.exe");
        if (moduleBase == IntPtr.Zero)
            moduleBase = GetModuleHandleW(null);
        if (moduleBase == IntPtr.Zero || !TryGetTextSection(moduleBase, out int textRva, out int textSize))
        {
            _statusMessage = "acclient .text section not found.";
            RynthLog.Compat($"Compat: large-address alignment fix skipped - {_statusMessage}");
            return;
        }

        IntPtr textBase = IntPtr.Add(moduleBase, textRva);
        byte[] text = new byte[textSize];
        Marshal.Copy(textBase, text, 0, textSize);
        List<AlignmentSite> sites = LargeAddressAlignmentScan.Find(text, textBase.ToInt32());

        int patched = 0, already = 0, failed = 0;
        foreach (AlignmentSite site in sites)
        {
            if (site.AlreadyPatched)
            {
                already++;
                continue;
            }
            if (TryWriteHighByte(new IntPtr(site.ImmHighByteVa)))
                patched++;
            else
                failed++;
        }

        if (patched > 0)
            FlushInstructionCache(GetCurrentProcess(), textBase, (UIntPtr)(uint)textSize);

        PatchedCount = patched;
        AlreadyPatchedCount = already;
        _statusMessage = $"{patched} site(s) patched, {already} already patched, {failed} failed ({sites.Count} found).";
        if (sites.Count == 0 || failed > 0)
            RynthLog.Compat($"Compat: large-address alignment fix - {_statusMessage}");
        else
            RynthLog.Info($"Compat: large-address alignment fix - {_statusMessage}");
    }

    // One byte, protection restored per site: other hooks may hold different protections on
    // neighbouring .text pages, so the whole section is never flipped at once.
    private static bool TryWriteHighByte(IntPtr address)
    {
        if (!VirtualProtect(address, (UIntPtr)1u, PAGE_EXECUTE_READWRITE, out uint oldProtect))
        {
            RynthLog.Compat($"Compat: large-address alignment fix - VirtualProtect 0x{address.ToInt32():X8} failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            // Re-check the live byte: the scan worked on a copy.
            if (Marshal.ReadByte(address) != LargeAddressAlignmentScan.StockHighByte)
                return false;
            Marshal.WriteByte(address, LargeAddressAlignmentScan.PatchedHighByte);
            return true;
        }
        finally
        {
            VirtualProtect(address, (UIntPtr)1u, oldProtect, out _);
        }
    }

    private static bool TryGetTextSection(IntPtr moduleBase, out int rva, out int size)
    {
        rva = 0;
        size = 0;
        if ((ushort)Marshal.ReadInt16(moduleBase) != ImageDosSignature)
            return false;
        int peOffset = Marshal.ReadInt32(moduleBase, 0x3C);
        IntPtr nt = IntPtr.Add(moduleBase, peOffset);
        if (peOffset <= 0 || (uint)Marshal.ReadInt32(nt) != ImageNtSignature)
            return false;

        int sectionCount = (ushort)Marshal.ReadInt16(nt, 6);
        int optionalHeaderSize = (ushort)Marshal.ReadInt16(nt, 20);
        IntPtr section = IntPtr.Add(nt, 24 + optionalHeaderSize);
        for (int i = 0; i < sectionCount; i++, section = IntPtr.Add(section, SectionHeaderSize))
        {
            if (Marshal.PtrToStringAnsi(section, 8).TrimEnd('\0') != ".text")
                continue;
            size = Marshal.ReadInt32(section, 8);
            rva = Marshal.ReadInt32(section, 12);
            return size > 0 && rva > 0;
        }
        return false;
    }
}
