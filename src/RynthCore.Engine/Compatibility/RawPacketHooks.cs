using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks the game's recvfrom socket function to intercept all S2C UDP packets, and (for
/// the flight recorder, Net/FlightRecorder.cs) its sendto function to see C2S packets.
///
/// The acclient.exe data segment holds a pointer to the recvfrom implementation:
///   RecvFrom @ *(int*)0x007935AC   (WSOCK32 ordinal 17)
///   SendTo   @ *(int*)0x007935A4   (WSOCK32 ordinal 20; hooked only while engine.json
///                                   "FlightRecorder" is on, the default)
///
/// We read the function address, install a MinHook detour, call through to the
/// original on every receive, then hand the buffer to RawPacketParser for
/// opcode extraction. The hook is purely read-only and never modifies traffic.
/// It also feeds Net.ServerMessageStream (API v78), which copies fragment datagrams
/// for reassembly on the plugin pump while a plugin wants server messages.
/// </summary>
internal static class RawPacketHooks
{
    private const int RecvFromPtrAddr = unchecked((int)0x007935AC);
    private const int SendToPtrAddr = unchecked((int)0x007935A4);

    // Phase B: resolve the RecvFrom fn-ptr slot's address by code-xref ("call [slot]" =
    // FF 15 <addr>), operand at offset 2; the VA stays as fallback.
    private static readonly byte?[] PatXrefRecvFromSlot = [ 0xFF, 0x15, null, null, null, null, 0x8B, 0xF0, 0x85, 0xF6, 0x7E, 0x67 ];

    // The SendTo slot, same way: its one "call [slot]" site (operand at offset 2).
    private static readonly byte?[] PatXrefSendToSlot = [ 0xFF, 0x15, null, null, null, null, 0x8B, 0xF0, 0x83, 0xFE, 0xFF, 0x75 ];

    private static IntPtr _originalRecvFromPtr;
    private static IntPtr _originalSendToPtr;
    private static int _sendErrors;

    public static bool IsInstalled { get; private set; }
    public static bool IsSendInstalled { get; private set; }

    /// <summary>sendto calls that returned an error since the engine started.</summary>
    public static int SendErrors => Volatile.Read(ref _sendErrors);

    public static void Initialize()
    {
        // The engine's own readers of the server stream (the Inventory panel's item order) need the
        // datagrams from here on, not from the first frame pump: see ServerMessageStream.EnableEarly.
        Net.ServerMessageStream.EnableEarly();

        if (Plugins.EngineSettings.FlightRecorder)
        {
            try { InstallSendTo(); }
            catch (Exception ex) { RynthLog.Compat($"RawPacket: SendTo hook failed - {ex.Message}"); }
        }

        if (IsInstalled) return;

        int slotAddr = RecvFromPtrAddr;
        if (AcClientModule.TryReadTextSection(out AcClientTextSection text))
            slotAddr = HookResolver.ResolveData(text, "RawPacket.RecvFromSlot", PatXrefRecvFromSlot, 2, RecvFromPtrAddr).Address.ToInt32();

        unsafe
        {
            IntPtr fnPtrAddr = new IntPtr(slotAddr);
            if (!SmartBoxLocator.IsMemoryReadable(fnPtrAddr, 4))
            {
                RynthLog.Compat($"RawPacket: RecvFrom pointer address 0x{slotAddr:X8} not readable — skipping.");
                return;
            }

            IntPtr recvFromAddr = Marshal.ReadIntPtr(fnPtrAddr);
            if (recvFromAddr == IntPtr.Zero)
            {
                RynthLog.Compat("RawPacket: RecvFrom pointer is null — skipping.");
                return;
            }

            try
            {
                delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, IntPtr, IntPtr, int> pDetour = &RecvFromDetour;
                MinHook.Hook(recvFromAddr, (IntPtr)pDetour, out _originalRecvFromPtr);
                IsInstalled = true;
                RynthLog.Verbose($"RawPacket: RecvFrom hook installed @ 0x{recvFromAddr.ToInt32():X8}");
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"RawPacket: MinHook failed — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The sendto detour, for the flight recorder only: it reads the datagram before the real
    /// sendto runs and then calls it unchanged. Nothing managed runs after the real call
    /// except one Interlocked counter on an error, so WSAGetLastError still sees sendto's error.
    /// </summary>
    private static void InstallSendTo()
    {
        if (IsSendInstalled) return;

        int slotAddr = SendToPtrAddr;
        if (AcClientModule.TryReadTextSection(out AcClientTextSection text))
            slotAddr = HookResolver.ResolveData(text, "RawPacket.SendToSlot", PatXrefSendToSlot, 2, SendToPtrAddr).Address.ToInt32();

        unsafe
        {
            IntPtr fnPtrAddr = new IntPtr(slotAddr);
            if (!SmartBoxLocator.IsMemoryReadable(fnPtrAddr, 4))
            {
                RynthLog.Compat($"RawPacket: SendTo pointer address 0x{slotAddr:X8} not readable - skipping.");
                return;
            }
            IntPtr sendToAddr = Marshal.ReadIntPtr(fnPtrAddr);
            if (sendToAddr == IntPtr.Zero)
            {
                RynthLog.Compat("RawPacket: SendTo pointer is null - skipping.");
                return;
            }
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, IntPtr, int, int> pDetour = &SendToDetour;
            MinHook.Hook(sendToAddr, (IntPtr)pDetour, out _originalSendToPtr);
            IsSendInstalled = true;
            RynthLog.Verbose($"RawPacket: SendTo hook installed @ 0x{sendToAddr.ToInt32():X8}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int SendToDetour(IntPtr s, IntPtr buf, int len, int flags, IntPtr to, int tolen)
    {
        RecursionGuard.Tick("RawPacketHooks.SendTo");
        if (len > 0 && buf != IntPtr.Zero)
        {
            try { Net.FlightRecorder.OnOutbound((byte*)buf, len); }
            catch { /* never affect game traffic */ }
        }

        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, IntPtr, int, int> original =
            (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, IntPtr, int, int>)_originalSendToPtr;
        int sent = original(s, buf, len, flags, to, tolen);
        if (sent < 0)
            Interlocked.Increment(ref _sendErrors);
        return sent;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int RecvFromDetour(
        IntPtr s, IntPtr buf, int len, int flags, IntPtr from, IntPtr fromlen)
    {
        RecursionGuard.Tick("RawPacketHooks.RecvFrom");
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, IntPtr, IntPtr, int> original =
            (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, IntPtr, IntPtr, int>)_originalRecvFromPtr;

        int bytesRead = original(s, buf, len, flags, from, fromlen);

        if (bytesRead > 0)
        {
            // Managed exceptions (bounds errors, etc.) CAN be caught in NativeAOT.
            // This guard prevents any parser bug from crashing the client.
            try
            {
                // v78 server-message stream: a volatile check, and while a plugin wants
                // messages, one copy into a ring for the pump (never blocks, never allocates).
                // A MSG_PEEK (0x2) read leaves the datagram queued; the real read copies it.
                if ((flags & 0x2) == 0)
                {
                    Net.ServerMessageStream.OnDatagram((byte*)buf, bytesRead);
                    Net.FlightRecorder.OnInbound((byte*)buf, bytesRead);
                }
                RawPacketParser.Parse((byte*)buf, bytesRead);
            }
            catch
            {
                // silently swallow — never affect game traffic
            }
        }

        return bytesRead;
    }
}
