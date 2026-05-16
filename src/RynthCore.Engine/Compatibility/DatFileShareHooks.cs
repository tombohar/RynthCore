using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// IAT-hooks acclient.exe's <c>kernel32!CreateFileW</c> and <c>CreateFileA</c>
/// imports to force maximally-permissive share semantics on AC's data files.
///
/// Why this exists:
///   <see cref="MultiClientHooks"/> patches AC's <c>OpenDataFile</c> high-level
///   <c>openFlags</c> bit 0x2, which selects AC's "shared" branch — but that
///   branch still translates to a CreateFile call whose <c>dwShareMode</c> may
///   not be wide enough to coexist with handles already held by Decal-injected
///   clients launched by other launchers (Thwargle etc.). Windows file-sharing
///   compatibility is decided by the kernel at CreateFile time, not by AC's
///   internal flag.
///
///   This hook intercepts CreateFile right before the kernel sees it, and for
///   the known DAT paths rewrites:
///     dwDesiredAccess = GENERIC_READ                                          (drop WRITE — DATs are read-only after patch)
///     dwShareMode     = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
///   Other paths pass through untouched.
///
/// Why IAT-hook instead of MinHook on the kernel32 export:
///   Decal coexistence. Decal almost certainly already detours kernel32!CreateFile
///   (that's how Decal-injected Thwargle clients can multibox without a
///   second AC install). IAT-hooking acclient.exe's import slot intercepts only
///   acclient.exe's own calls; our detour's call into the saved IAT pointer
///   still routes through any Decal MinHook trampoline on the kernel32 export.
///   No collision; both hooks fire in order.
/// </summary>
internal static class DatFileShareHooks
{
    // We force-share by file *extension* (.dat) rather than an explicit name
    // list. Stock retail AC ships portal.dat / cell.dat / client_*.dat, but
    // private servers commonly add split client-cell DATs (client_cell_1.dat,
    // client_cell_2.dat, ...) and other variants we can't enumerate up front.
    // Filtering on extension catches them all without per-server tuning.
    // AC only opens .dat files via CreateFile for game data; non-DAT writes
    // (logs, prefs, screenshots) pass through unmodified.
    private const string DatExtension = ".dat";

    private const uint GENERIC_READ          = 0x80000000;
    private const uint FILE_SHARE_READ       = 0x00000001;
    private const uint FILE_SHARE_WRITE      = 0x00000002;
    private const uint FILE_SHARE_DELETE     = 0x00000004;
    private const uint FILE_SHARE_ALL        = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    private const uint PAGE_READWRITE        = 0x04;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate IntPtr CreateFileWDelegate(
        IntPtr lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi, SetLastError = true)]
    private delegate IntPtr CreateFileADelegate(
        IntPtr lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    // Pinned via static field — Marshal.GetFunctionPointerForDelegate keeps a
    // weak handle, so the delegate must be reachable for the process lifetime.
    private static CreateFileWDelegate? _wDetour;
    private static CreateFileADelegate? _aDetour;

    // The IAT slot pointer (the address inside the IAT array, NOT the original
    // function address) — needed for the unhook path.
    private static IntPtr _wIatSlot;
    private static IntPtr _aIatSlot;

    // What was in the IAT slot when we patched it. Could be the real
    // kernel32!CreateFile, or another hooker's detour if they got there first.
    private static IntPtr _wOriginalPtr;
    private static IntPtr _aOriginalPtr;

    private static CreateFileWDelegate? _wOriginal;
    private static CreateFileADelegate? _aOriginal;

    private static string _statusMessage = "Not probed yet.";

    public static bool IsEnabled { get; private set; }
    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        // Diagnostic kill-switch. Set "EnableDatShareHook": false in
        // %APPDATA%\RynthCore\engine.json to skip detouring CreateFileA/W
        // entirely. Used to isolate this hook as a crash suspect.
        if (!Plugins.EngineSettings.EnableDatShareHook)
        {
            _statusMessage = "DAT share hook disabled via engine.json (EnableDatShareHook=false).";
            RynthLog.Info($"Compat: {_statusMessage}");
            return;
        }

        // Gate behind AllowMultipleClients. If the user has multi-client off,
        // they're not multiboxing, so we have no reason to widen DAT sharing.
        if (!LauncherSettings.AllowMultipleClientsEnabled)
        {
            _statusMessage = "Multi-client disabled — DAT share hook skipped.";
            RynthLog.Verbose($"Compat: {_statusMessage}");
            return;
        }

        IsEnabled = true;

        try
        {
            IntPtr moduleBase = AcClientGetModuleBase();
            if (moduleBase == IntPtr.Zero)
            {
                _statusMessage = "acclient.exe module base not found.";
                RynthLog.Compat($"Compat: DAT share hook failed - {_statusMessage}");
                return;
            }

            // Hook the kernel32 exports directly with MinHook so we catch ALL
            // callers in the process — acclient.exe itself, the CRT layer
            // (msvcrt.dll, which is how AC's fopen-style DAT loads route), and
            // any plugin or runtime DLL. IAT-hooking acclient.exe alone misses
            // CRT-mediated calls because they go through msvcrt's own IAT slot.
            IntPtr hKernel32 = GetModuleHandleW("kernel32.dll");
            if (hKernel32 == IntPtr.Zero)
            {
                _statusMessage = "kernel32.dll module handle not found.";
                RynthLog.Compat($"Compat: DAT share hook failed - {_statusMessage}");
                return;
            }

            // Two-phase install. Phase 1: create both hooks (inactive),
            // assign typed trampolines into _aOriginal/_wOriginal. Phase 2:
            // memory barrier then Enable. Critical ordering: the detour can
            // fire from ANY caller in the process the moment Enable returns,
            // and the detour calls _aOriginal/_wOriginal — those fields MUST
            // be set first or the first invocation null-derefs and kills the
            // engine init thread.
            IntPtr aTarget = GetProcAddress(hKernel32, "CreateFileA");
            IntPtr wTarget = GetProcAddress(hKernel32, "CreateFileW");

            int patched = 0;
            if (aTarget != IntPtr.Zero)
            {
                _aDetour = CreateFileADetour;
                IntPtr aDetourPtr = Marshal.GetFunctionPointerForDelegate(_aDetour);
                _aOriginalPtr = MinHook.HookCreate(aTarget, aDetourPtr);
                _aOriginal = Marshal.GetDelegateForFunctionPointer<CreateFileADelegate>(_aOriginalPtr);
                patched++;
            }

            if (wTarget != IntPtr.Zero)
            {
                _wDetour = CreateFileWDetour;
                IntPtr wDetourPtr = Marshal.GetFunctionPointerForDelegate(_wDetour);
                _wOriginalPtr = MinHook.HookCreate(wTarget, wDetourPtr);
                _wOriginal = Marshal.GetDelegateForFunctionPointer<CreateFileWDelegate>(_wOriginalPtr);
                patched++;
            }

            // Publish trampolines to other cores before enabling.
            Thread.MemoryBarrier();

            if (aTarget != IntPtr.Zero)
            {
                MinHook.Enable(aTarget);
                RynthLog.Info($"Compat: DAT share - hooked kernel32!CreateFileA @ 0x{aTarget.ToInt32():X8} (trampoline 0x{_aOriginalPtr.ToInt32():X8}).");
            }
            else
            {
                RynthLog.Compat("Compat: DAT share - kernel32!CreateFileA export not found.");
            }

            if (wTarget != IntPtr.Zero)
            {
                MinHook.Enable(wTarget);
                RynthLog.Info($"Compat: DAT share - hooked kernel32!CreateFileW @ 0x{wTarget.ToInt32():X8} (trampoline 0x{_wOriginalPtr.ToInt32():X8}).");
            }
            else
            {
                RynthLog.Compat("Compat: DAT share - kernel32!CreateFileW export not found.");
            }

            if (patched == 0)
            {
                _statusMessage = "MinHook on kernel32 CreateFile exports failed.";
                RynthLog.Compat($"Compat: DAT share hook failed - {_statusMessage}");
                return;
            }

            IsInstalled = true;
            _statusMessage = $"DAT share hook installed ({patched} kernel32 export(s) detoured).";
            RynthLog.Info($"Compat: {_statusMessage}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"DAT share hook exception: {ex.Message}";
            RynthLog.Compat($"Compat: DAT share hook failed - {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    /// <summary>
    /// Diagnostic — log every kernel32 import name we find that looks file- or
    /// memory-mapping-related. Useful when the standard CreateFileW/A hook
    /// doesn't catch DAT opens, because it tells us what API AC actually uses.
    /// </summary>
    private static unsafe void EnumerateKernel32FileImports(IntPtr moduleBase)
    {
        try
        {
            byte* basePtr = (byte*)moduleBase;
            int peOffset = *(int*)(basePtr + 0x3C);
            byte* optHeader = basePtr + peOffset + 24;
            uint importDirRva = *(uint*)(optHeader + 96 + 8 * 1);
            if (importDirRva == 0)
                return;

            byte* descriptor = basePtr + importDirRva;
            for (; ; descriptor += 20)
            {
                uint nameRva = *(uint*)(descriptor + 12);
                uint firstThunk = *(uint*)(descriptor + 16);
                if (nameRva == 0 && firstThunk == 0) break;

                string? dllName = Marshal.PtrToStringAnsi((IntPtr)(basePtr + nameRva));
                if (!string.Equals(dllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                uint origFirstThunk = *(uint*)(descriptor + 0);
                uint nameTableRva = origFirstThunk != 0 ? origFirstThunk : firstThunk;
                uint* names = (uint*)(basePtr + nameTableRva);
                int i = 0;
                var hits = new System.Text.StringBuilder("Compat: DAT share - kernel32 file/mmap imports in acclient.exe IAT:");
                int hitCount = 0;
                while (names[i] != 0)
                {
                    uint thunk = names[i];
                    if ((thunk & 0x80000000u) == 0)
                    {
                        string? candidate = Marshal.PtrToStringAnsi((IntPtr)(basePtr + thunk + 2));
                        if (candidate != null &&
                            (candidate.Contains("File", StringComparison.Ordinal) ||
                             candidate.Contains("Mapping", StringComparison.Ordinal) ||
                             candidate.Contains("MapView", StringComparison.Ordinal) ||
                             candidate.StartsWith("_l", StringComparison.Ordinal) ||
                             candidate.StartsWith("_open", StringComparison.Ordinal)))
                        {
                            hits.Append(' ').Append(candidate);
                            hitCount++;
                        }
                    }
                    i++;
                }
                RynthLog.Info(hitCount > 0 ? hits.ToString() : "Compat: DAT share - no file-related kernel32 imports found in acclient.exe IAT.");
                return;
            }

            RynthLog.Info("Compat: DAT share - kernel32.dll import descriptor not found in acclient.exe.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: DAT share - import enumeration failed: {ex.Message}");
        }
    }

    private static IntPtr AcClientGetModuleBase()
    {
        IntPtr moduleBase = GetModuleHandleW("acclient.exe");
        if (moduleBase == IntPtr.Zero)
            moduleBase = GetModuleHandleW(null);
        return moduleBase;
    }

    /// <summary>
    /// Walks the import directory of <paramref name="moduleBase"/> looking for
    /// kernel32.dll's import descriptor, then locates the named import
    /// <paramref name="importName"/>'s IAT slot. Returns the slot's absolute
    /// address (not the function pointer it contains) plus the current
    /// pointer value.
    /// </summary>
    private static unsafe bool TryPatchIatSlot(IntPtr moduleBase, string importName, out IntPtr iatSlot, out IntPtr currentPtr)
    {
        iatSlot = IntPtr.Zero;
        currentPtr = IntPtr.Zero;

        byte* basePtr = (byte*)moduleBase;
        int peOffset = *(int*)(basePtr + 0x3C);
        if (peOffset <= 0 || *(uint*)(basePtr + peOffset) != 0x00004550) // "PE\0\0"
            return false;

        // OPTIONAL_HEADER starts at peOffset + 24 (4-byte sig + 20-byte FILE_HEADER).
        // PE32 magic is 0x010B; DataDirectory starts at OptionalHeader+96.
        byte* optHeader = basePtr + peOffset + 24;
        if (*(ushort*)optHeader != 0x010B)
            return false;

        uint importDirRva = *(uint*)(optHeader + 96 + 8 * 1); // DataDirectory[1] = IMPORT
        if (importDirRva == 0)
            return false;

        byte* descriptor = basePtr + importDirRva;
        // Each IMAGE_IMPORT_DESCRIPTOR is 20 bytes; terminated by all zeros.
        for (; ; descriptor += 20)
        {
            uint nameRva = *(uint*)(descriptor + 12);
            uint firstThunk = *(uint*)(descriptor + 16);
            if (nameRva == 0 && firstThunk == 0)
                break; // End of descriptor list.

            string? dllName = Marshal.PtrToStringAnsi((IntPtr)(basePtr + nameRva));
            if (!string.Equals(dllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(dllName, "KERNEL32.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            uint origFirstThunk = *(uint*)(descriptor + 0);
            // INT (Import Name Table) — prefer OriginalFirstThunk if present;
            // some bound imports may have it zeroed, in which case the IAT
            // itself is still a valid name table pre-binding. Both arrays are
            // parallel, 4-byte entries on PE32.
            uint nameTableRva = origFirstThunk != 0 ? origFirstThunk : firstThunk;
            uint* names = (uint*)(basePtr + nameTableRva);
            int i = 0;
            while (names[i] != 0)
            {
                uint thunk = names[i];
                // High bit set = import by ordinal — no name to match.
                if ((thunk & 0x80000000u) == 0)
                {
                    // IMAGE_IMPORT_BY_NAME = { WORD Hint; CHAR Name[]; }
                    string? candidate = Marshal.PtrToStringAnsi((IntPtr)(basePtr + thunk + 2));
                    if (string.Equals(candidate, importName, StringComparison.Ordinal))
                    {
                        iatSlot = (IntPtr)(basePtr + firstThunk + 4 * i);
                        currentPtr = Marshal.ReadIntPtr(iatSlot);
                        return currentPtr != IntPtr.Zero;
                    }
                }
                i++;
            }
        }

        return false;
    }

    private static bool WriteIatPointer(IntPtr iatSlot, IntPtr newValue)
    {
        // IAT lives in .rdata which is read-only after the loader binds it.
        // Flip protection, write, restore. Use the original-protection value
        // from VirtualProtect so we put the page back exactly the way we
        // found it (some loaders mark IAT pages as PAGE_READWRITE post-binding,
        // some PAGE_READONLY).
        if (!VirtualProtect(iatSlot, sizeof(int), PAGE_READWRITE, out uint oldProtect))
        {
            RynthLog.Compat($"Compat: DAT share - VirtualProtect(RW) on IAT slot 0x{iatSlot.ToInt32():X8} failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            Marshal.WriteIntPtr(iatSlot, newValue);
        }
        finally
        {
            VirtualProtect(iatSlot, sizeof(int), oldProtect, out _);
        }

        return true;
    }

    private static bool IsDatPath(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && path.EndsWith(DatExtension, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLastError();

    private static IntPtr CreateFileWDetour(
        IntPtr lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile)
    {
        string? path = null;
        bool isDat = false;
        bool isAnyDatExt = false;
        uint origAccess = dwDesiredAccess;
        uint origShare = dwShareMode;

        try
        {
            path = Marshal.PtrToStringUni(lpFileName);
            isDat = IsDatPath(path);
            isAnyDatExt = path != null && path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);
            if (isDat)
            {
                dwDesiredAccess = GENERIC_READ;
                dwShareMode = FILE_SHARE_ALL;
            }
        }
        catch
        {
        }

        IntPtr result = _wOriginal!(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes,
            dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);

        if (isAnyDatExt)
        {
            uint err = result == new IntPtr(-1) ? GetLastError() : 0;
            string fname = Path.GetFileName(path!);
            string note = isDat ? "filtered" : "passthrough";
            RynthLog.Info($"Compat: DAT share - W [{note}]: {fname} access=0x{origAccess:X8}->0x{dwDesiredAccess:X8} share=0x{origShare:X}->0x{dwShareMode:X} result=0x{result.ToInt32():X8}{(err != 0 ? $" GetLastError={err}" : "")}");
        }

        return result;
    }

    private static IntPtr CreateFileADetour(
        IntPtr lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile)
    {
        string? path = null;
        bool isDat = false;
        bool isAnyDatExt = false;
        uint origAccess = dwDesiredAccess;
        uint origShare = dwShareMode;

        try
        {
            path = Marshal.PtrToStringAnsi(lpFileName);
            isDat = IsDatPath(path);
            isAnyDatExt = path != null && path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);
            if (isDat)
            {
                dwDesiredAccess = GENERIC_READ;
                dwShareMode = FILE_SHARE_ALL;
            }
        }
        catch
        {
        }

        IntPtr result = _aOriginal!(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes,
            dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);

        if (isAnyDatExt)
        {
            uint err = result == new IntPtr(-1) ? GetLastError() : 0;
            string fname = Path.GetFileName(path!);
            string note = isDat ? "filtered" : "passthrough";
            RynthLog.Info($"Compat: DAT share - A [{note}]: {fname} access=0x{origAccess:X8}->0x{dwDesiredAccess:X8} share=0x{origShare:X}->0x{dwShareMode:X} result=0x{result.ToInt32():X8}{(err != 0 ? $" GetLastError={err}" : "")}");
        }

        return result;
    }
}
