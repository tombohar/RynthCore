using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Reads the current account name via Client::GetInstance() + Client::GetAccountName().
/// Captures the world/server name by hooking ECM_Login::SendNotice_WorldName (cdecl),
/// with a fallback to the launcher's per-process JSON context file.
///
/// Client::GetInstance() — static cdecl, Map: 000104C0 → live VA: 0x004114C0
/// Client::GetAccountName() — thiscall, Map: 00000D90 → live VA: 0x00401D90
///   Returns accountID* where first field is PStringBase&lt;char&gt; (4-byte ptr to PSRefBuffer&lt;char&gt;).
///
/// ECM_Login::SendNotice_WorldName — cdecl, Map: 00292A60 → live VA: 0x00693A60
///   Called once during login when the server announces the world name.
///   Parameter: AC1Legacy::PStringBase&lt;char&gt; const& — same PSRefBuffer layout as account name.
///
/// PSRefBuffer&lt;char&gt; layout (confirmed from ClientObjectHooks InqString):
///   +0:  Turbine_RefCount { vfptr(4), m_cRef(4) } = 8 bytes
///   +8:  m_len (Int32, includes null terminator)
///   +20: m_data[] (ANSI string)
/// </summary>
internal static class AccountHooks
{
    private const int ReferenceClientGetInstance    = 0x004114C0;
    private const int ReferenceClientGetAccountName = 0x00401D90;
    private const int SendNoticeWorldNameVa         = 0x00693A60;
    private const int PStringBufferLenOffset        = 8;
    private const int PStringBufferDataOffset       = 20;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ClientGetInstanceDelegate();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr ClientGetAccountNameDelegate(IntPtr clientPtr);

    // ECM_Login::SendNotice_WorldName(AC1Legacy::PStringBase<char> const& i_strName) → bool
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendNoticeWorldNameDelegate(IntPtr pstringPtr);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint   AllocationProtect;
        public IntPtr RegionSize;
        public uint   State;
        public uint   Protect;
        public uint   Type;
    }
    private const uint MEM_COMMIT    = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD    = 0x100;

    private static ClientGetInstanceDelegate?    _getClientInstance;
    private static ClientGetAccountNameDelegate? _getAccountName;
    private static SendNoticeWorldNameDelegate?  _originalSendNoticeWorldName;
    private static SendNoticeWorldNameDelegate?  _sendNoticeWorldNameDetour;
    private static string? _cachedAccountName;
    private static string? _cachedWorldName;

    public static bool IsInitialized     { get; private set; }
    public static bool WorldHookInstalled { get; private set; }

    public static void Initialize()
    {
        try
        {
            _getClientInstance = Marshal.GetDelegateForFunctionPointer<ClientGetInstanceDelegate>(
                new IntPtr(ReferenceClientGetInstance));
            _getAccountName = Marshal.GetDelegateForFunctionPointer<ClientGetAccountNameDelegate>(
                new IntPtr(ReferenceClientGetAccountName));
            IsInitialized = true;
            RynthLog.Verbose("Compat: account hooks ready.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: account hooks failed - {ex.Message}");
        }

        HookWorldName();
    }

    private static void HookWorldName()
    {
        try
        {
            if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            {
                RynthLog.Compat("Compat: world-name hook skipped - acclient.exe not available.");
                return;
            }

            int funcOff = SendNoticeWorldNameVa - textSection.TextBaseVa;
            if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
            {
                RynthLog.Compat($"Compat: world-name hook failed - VA 0x{SendNoticeWorldNameVa:X8} out of text section range.");
                return;
            }

            byte firstByte = textSection.Bytes[funcOff];
            if (firstByte is 0x00 or 0xCC or 0xC3)
            {
                RynthLog.Compat($"Compat: world-name hook failed - invalid opcode 0x{firstByte:X2} @ 0x{SendNoticeWorldNameVa:X8}.");
                return;
            }

            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _sendNoticeWorldNameDetour = SendNoticeWorldNameDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_sendNoticeWorldNameDetour);
            _originalSendNoticeWorldName = Marshal.GetDelegateForFunctionPointer<SendNoticeWorldNameDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);

            WorldHookInstalled = true;
            RynthLog.Verbose($"Compat: world-name hook ready - ECM_Login::SendNotice_WorldName @ 0x{targetAddress.ToInt32():X8}.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: world-name hook failed - {ex.Message}");
        }
    }

    private static byte SendNoticeWorldNameDetour(IntPtr pstringPtr)
    {
        byte result = 0;
        try { result = _originalSendNoticeWorldName!(pstringPtr); } catch { }

        try
        {
            if (pstringPtr != IntPtr.Zero && IsReadable(pstringPtr))
            {
                // AC1Legacy::PStringBase<char>: first field is PSRefBuffer<char>*
                IntPtr bufferPtr = Marshal.ReadIntPtr(pstringPtr);
                if (bufferPtr != IntPtr.Zero && IsReadable(bufferPtr + PStringBufferDataOffset))
                {
                    int len = Marshal.ReadInt32(bufferPtr + PStringBufferLenOffset);
                    if (len > 1)
                    {
                        string? name = Marshal.PtrToStringAnsi(bufferPtr + PStringBufferDataOffset, len - 1);
                        if (!string.IsNullOrEmpty(name))
                        {
                            _cachedWorldName = name;
                            RynthLog.Verbose($"Compat: world name captured - \"{name}\".");
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Returns the current account name. Result is cached after the first successful read.
    /// </summary>
    public static bool TryGetAccountName(out string name)
    {
        name = string.Empty;

        if (_cachedAccountName != null)
        {
            name = _cachedAccountName;
            return true;
        }

        if (_getClientInstance == null || _getAccountName == null)
            return false;

        try
        {
            IntPtr clientPtr = _getClientInstance();
            if (clientPtr == IntPtr.Zero || !IsReadable(clientPtr))
                return false;

            IntPtr accountIdPtr = _getAccountName(clientPtr);
            if (accountIdPtr == IntPtr.Zero || !IsReadable(accountIdPtr))
                return false;

            // accountID.name is PStringBase<char> — a 4-byte pointer to PSRefBuffer<char>
            IntPtr bufferPtr = Marshal.ReadIntPtr(accountIdPtr);
            if (bufferPtr == IntPtr.Zero || !IsReadable(bufferPtr + PStringBufferDataOffset))
                return false;

            int len = Marshal.ReadInt32(bufferPtr + PStringBufferLenOffset);
            if (len <= 1)
                return false;

            string? str = Marshal.PtrToStringAnsi(bufferPtr + PStringBufferDataOffset, len - 1);
            if (string.IsNullOrEmpty(str))
                return false;

            _cachedAccountName = str;
            name = str;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the world/server name. Populated by the ECM_Login::SendNotice_WorldName hook
    /// when the server announces it during login. Falls back to the RynthCore launcher's
    /// per-process JSON context file if the hook hasn't fired yet.
    /// </summary>
    public static bool TryGetWorldName(out string name)
    {
        name = string.Empty;

        if (_cachedWorldName != null)
        {
            name = _cachedWorldName;
            return true;
        }

        // Fallback: read from launcher context file (only present if launched via RynthCore)
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string path = System.IO.Path.Combine(appData, "RynthCore", "launch_contexts",
                $"launch_context_{Environment.ProcessId}.json");

            if (!System.IO.File.Exists(path))
                return false;

            string json = System.IO.File.ReadAllText(path);

            const string key = "\"ServerName\":\"";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            int start = idx + key.Length;
            int end = json.IndexOf('"', start);
            if (end <= start)
                return false;

            string serverName = json.Substring(start, end - start);
            if (string.IsNullOrEmpty(serverName))
                return false;

            _cachedWorldName = serverName;
            name = serverName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReadable(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return false;
        if (VirtualQuery(ptr, out var mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        if ((mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;
        return true;
    }
}
