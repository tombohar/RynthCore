using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class SmartBoxHooks
{
    private const int NetBlobBufPtrOffset = 0x2C;
    private const int NetBlobBufSizeOffset = 0x30;
    private const uint PositionUpdateOpcode = 0x0000F74C;
    private const uint PlayerPositionUpdateOpcode = 0x0000F74B;
    private const uint VectorUpdateOpcode = 0x0000F74E;
    private const uint UpdateObjectOpcode = 0x0000F7DB;

    // ACSmartBox::DispatchSmartBoxEvent
    // 83 EC 08 53 8B 5C 24 10 8B 53 30 83 FA 04 8B 43 2C 56 8B F1 89 44 24 14 89 54 24 08 72 ?? 8B 08
    private static readonly byte?[] DispatchSmartBoxEventPattern =
    [
        0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10,
        0x8B, 0x53, 0x30, 0x83, 0xFA, 0x04, 0x8B, 0x43,
        0x2C, 0x56, 0x8B, 0xF1, 0x89, 0x44, 0x24, 0x14,
        0x89, 0x54, 0x24, 0x08, 0x72, null, 0x8B, 0x08
    ];

    // ACGame::DispatchGameEvent
    // 83 EC 08 53 8B 5C 24 10 8B 53 30 83 FA 04 8B 43 2C 56 8B F1 89 44 24 14 89 54 24 08 72 ?? 8B 08
    private static readonly byte?[] DispatchGameEventPattern =
    [
        0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10,
        0x8B, 0x53, 0x30, 0x83, 0xFA, 0x04, 0x8B, 0x43,
        0x2C, 0x56, 0x8B, 0xF1, 0x89, 0x44, 0x24, 0x14,
        0x89, 0x54, 0x24, 0x08, 0x72, null, 0x8B, 0x08
    ];

    private static IntPtr _originalDispatchSmartBoxEventPtr;
    private static IntPtr _originalDispatchGameEventPtr;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    private static readonly object _initLock = new();
    public static void Initialize()
    {
        lock (_initLock)
        {
            if (IsInstalled)
                return;

            if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            {
                _statusMessage = "acclient.exe not available.";
                return;
            }

            try
            {
                unsafe
                {
                    var hookedAddresses = new HashSet<IntPtr>();

                    // Hook SmartBox Event
                    int sbOff = PatternScanner.FindPattern(textSection.Bytes, DispatchSmartBoxEventPattern);
                    if (sbOff >= 0)
                    {
                        IntPtr sbAddr = new IntPtr(textSection.TextBaseVa + sbOff);
                        if (hookedAddresses.Add(sbAddr))
                        {
                            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint> pSbDetour = &DispatchSmartBoxEventDetour;
                            MinHook.Hook(sbAddr, (IntPtr)pSbDetour, out _originalDispatchSmartBoxEventPtr);
                            RynthLog.Verbose($"Compat: smartbox hook ready @ 0x{sbAddr.ToInt32():X8}");
                        }
                    }
                    else RynthLog.Compat("Compat: smartbox pattern not found.");

                    // Hook Game Event
                    int geOff = PatternScanner.FindPattern(textSection.Bytes, DispatchGameEventPattern);
                    if (geOff >= 0)
                    {
                        IntPtr geAddr = new IntPtr(textSection.TextBaseVa + geOff);
                        if (hookedAddresses.Add(geAddr))
                        {
                            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint> pGeDetour = &DispatchGameEventDetour;
                            MinHook.Hook(geAddr, (IntPtr)pGeDetour, out _originalDispatchGameEventPtr);
                            RynthLog.Verbose($"Compat: game-event hook ready @ 0x{geAddr.ToInt32():X8}");
                        }
                        else RynthLog.Compat("Compat: game-event pattern matched already-hooked address.");
                    }
                    else RynthLog.Compat("Compat: game-event pattern not found.");
                }

                IsInstalled = true;
                _statusMessage = "Hooks installed.";
            }
            catch (Exception ex)
            {
                _statusMessage = ex.Message;
                RynthLog.Compat($"Compat: hooks failed - {ex.Message}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint DispatchSmartBoxEventDetour(IntPtr thisPtr, IntPtr blob)
    {
        MainThreadGuard.RecordIfFirst();
        RecursionGuard.Tick("SmartBoxHooks.DispatchSmartBoxEvent");
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint>)_originalDispatchSmartBoxEventPtr;

        if (!LoginLifecycleHooks.HasObservedLoginComplete)
        {
            try { CharacterCaptureHooks.ProcessPotentialCharacterMessage(blob, isGameEvent: false); } catch { }
            return pOriginal(thisPtr, blob);
        }

        SmartBoxEventInfo info = ReadSmartBoxEventInfo(blob);
        uint status = pOriginal(thisPtr, blob);

        try
        {
            TryQueueHealthUpdate(blob, info);

            if (info.RawObjectId != 0 &&
                (info.Opcode == PositionUpdateOpcode ||
                 info.Opcode == PlayerPositionUpdateOpcode ||
                 info.Opcode == VectorUpdateOpcode ||
                 info.Opcode == UpdateObjectOpcode))
            {
                PluginManager.QueueUpdateObject(info.RawObjectId);
            }
        }
        catch { }

        return status;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint DispatchGameEventDetour(IntPtr thisPtr, IntPtr blob)
    {
        MainThreadGuard.RecordIfFirst();
        // Engine-side BusyCount watchdog: piggyback on this detour (fires
        // on every server game event while in-world, runs on AC's main
        // thread). If busy count has been positive for > 5s, force-clear
        // it. Self-heals from desync between our tracker and AC's m_cBusy
        // when the user can't reach the chat box to run `/ra clearbusy`.
        try { BusyCountHooks.CheckWatchdog(); } catch { }
        RecursionGuard.Tick("SmartBoxHooks.DispatchGameEvent");
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint>)_originalDispatchGameEventPtr;

        SmartBoxEventInfo info = default;
        try
        {
            info = ReadSmartBoxEventInfo(blob);
            TryQueueHealthUpdate(blob, info);
            CharacterCaptureHooks.ProcessPotentialCharacterMessage(blob, isGameEvent: true);
        }
        catch { }

        uint result = pOriginal(thisPtr, blob);
        return result;
    }

    private static SmartBoxEventInfo ReadSmartBoxEventInfo(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return default;

        try
        {
            uint blobSize = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(blob, NetBlobBufSizeOffset)));
            if (blobSize < sizeof(uint))
                return new SmartBoxEventInfo(0, 0, blobSize);

            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return new SmartBoxEventInfo(0, 0, blobSize);

            uint opcode = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            uint rawObjectId = blobSize >= 8
                ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, sizeof(uint))))
                : 0;
            return new SmartBoxEventInfo(opcode, rawObjectId, blobSize);
        }
        catch
        {
            return default;
        }
    }

    private static void TryQueueHealthUpdate(IntPtr blob, SmartBoxEventInfo info)
    {
        if (info.Opcode != 0x01C0 || info.BlobSize < 12 || blob == IntPtr.Zero)
            return;

        try
        {
            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return;

            uint targetId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, 4)));
            float ratio = Marshal.PtrToStructure<float>(IntPtr.Add(payloadPtr, 8));

            uint maxHealth = 0;
            uint currentHealth = 0;
            // Appraisal-only: emit an absolute health pair ONLY from a real
            // appraisal's wire-parsed CreatureProfile (id-keyed). With no
            // appraisal, leave max=0 so the UI falls back to a % — the
            // pointer-cache and creature Inq maxes are unreliable on ACE and
            // fabricated wildly wrong values (e.g. 50/50 for a 190-hp mob).
            if (ObjectQualityCache.TryGetCreatureVitals(targetId, out CreatureVitals exact) && exact.MaxHealth > 0)
            {
                maxHealth = exact.MaxHealth;
                currentHealth = (uint)Math.Round(maxHealth * Math.Clamp(ratio, 0f, 1f));
            }

            PluginManager.QueueUpdateHealth(targetId, ratio, currentHealth, maxHealth);
        }
        catch
        {
        }
    }



    // WireParseIdentifyFromBlob / TryCacheVitalsFromIdentify removed 2026-05-18:
    // calling CombatActionHooks.TryParseIdentifyResponse from an [UnmanagedCallersOnly]
    // detour on AC's main thread triggers ObjectQualityCache dictionary growth
    // (allocation) for each newly-identified mob → GC during the reverse-P/Invoke
    // transition → RhpReversePInvokeAttachOrTrapThread2 STATUS_FAIL_FAST (silent
    // process death, no dialog). Mob health display falls back to ratio (%) mode;
    // absolute values can be restored via a pre-allocated pump-thread ring buffer.

    private readonly record struct SmartBoxEventInfo(uint Opcode, uint RawObjectId, uint BlobSize);
}
