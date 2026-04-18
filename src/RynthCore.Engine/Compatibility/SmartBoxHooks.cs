using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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
    private const uint GameEventOpcode = 0x0000F7B0;

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
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint>)_originalDispatchSmartBoxEventPtr;
        
        if (!LoginLifecycleHooks.HasObservedLoginComplete)
        {
            CharacterCaptureHooks.ProcessPotentialCharacterMessage(blob, isGameEvent: false);
            return pOriginal(thisPtr, blob);
        }

        SmartBoxEventInfo info = ReadSmartBoxEventInfo(blob);
        uint status = pOriginal(thisPtr, blob);

        TryQueueHealthUpdate(blob, info);

        // Game events arrive as 0xF7B0 wrapper: [F7B0][playerId][seq][innerEventType][...]
        // Check for IdentifyObject response (inner event 0xC9) after the original processes it.
        if (info.Opcode == GameEventOpcode)
            TryHandleGameEvent(blob, info);
        else if (info.Opcode == 0xC9 && info.RawObjectId != 0)
            TryCacheVitalsFromIdentify(info.RawObjectId);

        if (info.RawObjectId != 0 &&
            (info.Opcode == PositionUpdateOpcode ||
             info.Opcode == PlayerPositionUpdateOpcode ||
             info.Opcode == VectorUpdateOpcode ||
             info.Opcode == UpdateObjectOpcode))
        {
            PluginManager.QueueUpdateObject(info.RawObjectId);
        }

        return status;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint DispatchGameEventDetour(IntPtr thisPtr, IntPtr blob)
    {
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint>)_originalDispatchGameEventPtr;

        // Log game event opcodes for discovery
        SmartBoxEventInfo info = ReadSmartBoxEventInfo(blob);
        TryQueueHealthUpdate(blob, info);

        // Always attempt capture from game events (pre-login)
        CharacterCaptureHooks.ProcessPotentialCharacterMessage(blob, isGameEvent: true);

        uint result = pOriginal(thisPtr, blob);

        // After the original handler processes 0xC9 (IdentifyObject response),
        // the weenie's qualities are populated — read maxHealth and cache it.
        if (info.Opcode == 0xC9 && info.RawObjectId != 0)
            TryCacheVitalsFromIdentify(info.RawObjectId);

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
            if (ClientObjectHooks.TryGetWeenieObjectPtr(targetId, out IntPtr pWeenie))
            {
                if (ObjectQualityCache.TryGetMaxHealth(pWeenie, out uint cached))
                {
                    maxHealth = cached;
                    currentHealth = (uint)Math.Round(maxHealth * Math.Clamp(ratio, 0f, 1f));
                }
            }

            PluginManager.QueueUpdateHealth(targetId, ratio, currentHealth, maxHealth);
        }
        catch
        {
        }
    }

    private static int _identifyLogCount;

    private static void TryHandleGameEvent(IntPtr blob, SmartBoxEventInfo info)
    {
        // 0xF7B0 game event layout: [0xF7B0(4)][playerId(4)][sequence(4)][innerEventType(4)][eventData...]
        // Minimum size: 16 bytes for the header
        if (blob == IntPtr.Zero || info.BlobSize < 16)
            return;

        try
        {
            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return;

            uint innerEvent = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, 12)));

            // 0xC9 = IdentifyObject response
            // Layout after inner event type: [objectId(4)][flags(4)][success(4)][data...]
            if (innerEvent == 0xC9 && info.BlobSize >= 20)
            {
                uint objectId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, 16)));
                if (objectId != 0)
                    TryCacheVitalsFromIdentify(objectId);
            }
        }
        catch
        {
        }
    }

    private static void TryCacheVitalsFromIdentify(uint objectId)
    {
        try
        {
            if (!ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out IntPtr pWeenie) || pWeenie == IntPtr.Zero)
                return;

            // We're on the game thread (DispatchGameEvent), so InqAttribute2ndStruct is safe.
            if (PlayerVitalsHooks.TryReadObjectMaxHealth(pWeenie, out uint maxHealth) && maxHealth > 0)
            {
                ObjectQualityCache.SetMaxHealth(pWeenie, maxHealth);

                int count = Interlocked.Increment(ref _identifyLogCount);
                if (count <= 12)
                    RynthLog.Verbose($"Compat: identify 0xC9 obj=0x{objectId:X8} maxHealth={maxHealth}");
            }
        }
        catch
        {
        }
    }

    private readonly record struct SmartBoxEventInfo(uint Opcode, uint RawObjectId, uint BlobSize);
}
