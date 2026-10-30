using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class DeleteObjectHooks
{
    private const int DeleteObjectVa = 0x00558330;
    // Verified unique + lands at 0x00558330 offline (tools/pe_pattern.py).
    private static readonly byte?[] DeleteObjectPattern =
    [
        0x56, 0x57, 0x8B, 0xF9, 0xE8, null, null, null, null, 0x85, 0xC0, 0x8B, 0x74, 0x24, 0x0C
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int DeleteObjectDelegate(IntPtr thisPtr, uint objectId);

    private static DeleteObjectDelegate? _originalDeleteObject;
    private static DeleteObjectDelegate? _deleteObjectDetour;
    private static IntPtr _targetAddress;
    private static IntPtr _objectMaint;   // ACCObjectMaint* seen on AC's own deletes (RemoveClientObject)
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        HookResolver.ResolveResult resolved = HookResolver.Resolve(textSection, "DeleteObject.DeleteObject", DeleteObjectPattern, DeleteObjectVa);
        if (!resolved.Success)
        {
            _statusMessage = $"ACCObjectMaint::DeleteObject unresolved (VA 0x{DeleteObjectVa:X8}).";
            RynthLog.Compat($"Compat: delete-object hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = resolved.Address;
            _deleteObjectDetour = DeleteObjectDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_deleteObjectDetour);
            _originalDeleteObject = Marshal.GetDelegateForFunctionPointer<DeleteObjectDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCObjectMaint::DeleteObject @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: delete-object hook ready - DeleteObject=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: delete-object hook failed - {ex.Message}");
        }
    }

    private static int DeleteObjectDetour(IntPtr thisPtr, uint objectId)
    {
        RecursionGuard.Tick("DeleteObjectHooks.DeleteObject");
        if (thisPtr != IntPtr.Zero) _objectMaint = thisPtr;
        if (objectId != 0)
        {
            ObjectFreshness.Forget(objectId);
            PaletteCache.Remove(objectId);
            ClientObjectHooks.ForgetCreateSeed(objectId);
            PluginManager.QueueDeleteObject(objectId);
            AutoIdService.OnClientRelease(objectId);
        }

        return _originalDeleteObject!(thisPtr, objectId);
    }

    // ── v83 RemoveClientObject: drop a "ghost" from the client (2026-10-12) ──────────────
    // A ghost is an object the client still draws but the server no longer has for this player
    // (Drakkon in 0x6146: four Olthoi he stood and swung at for 25 minutes; every attack was
    // answered by an immediate AttackDone, they never moved and never died). Nothing ever deletes
    // them: only the server's DeleteObject would, and it doesn't know the client still has them.
    // This runs the same client function every server DeleteObject ends in -
    // ACCObjectMaint::DeleteObject(this, id), the one this hook watches - so the client forgets
    // the object the way it forgets a corpse that decays. If the object is real after all, the
    // server's next CreateObject for it (when it comes back into view) brings it back.
    // Refused: no ACCObjectMaint seen yet (no delete since the hook went in), the player, anything
    // that isn't a creature, players, and any object the server sent a message about in the last
    // RemoveMinQuietMs (it is real).

    /// <summary>An object the server talked about this recently is never removed.</summary>
    public const long RemoveMinQuietMs = 5000;

    public enum RemoveResult { Removed = 1, Refused = 0, Unavailable = -1, Queued = 2 }

    /// <summary>True once AC's own DeleteObject has run (the ACCObjectMaint pointer is known).</summary>
    public static bool CanRemove => IsInstalled && _objectMaint != IntPtr.Zero;

    /// <summary>Why <paramref name="objectId"/> may not be removed, or null when it may.</summary>
    public static string? RemoveRefusal(uint objectId, long nowMs)
    {
        if (objectId == 0) return "no object";
        uint player = ClientHelperHooks.CachedPlayerId;
        if (player == 0 || objectId == player) return "the player";
        if (!ClientObjectHooks.TryGetSnapshotItemType(objectId, out uint type)) return "not in the client's object table";
        if ((type & 0x10) == 0) return "not a creature";
        if (ClientObjectHooks.TryGetSnapshotPwdInfo(objectId, out uint bf, out _, out _) && (bf & 0x8) != 0) return "a player";
        long age = ObjectFreshness.AgeMs(objectId, nowMs);
        if (age >= 0 && age < RemoveMinQuietMs) return $"the server sent a message about it {age} ms ago";
        return null;
    }

    /// <summary>Remove <paramref name="objectId"/> from the client (see above). Off AC's main thread
    /// it is queued and checked again when it runs.</summary>
    public static RemoveResult RemoveClientObject(uint objectId, out string? refusal)
    {
        refusal = null;
        if (!CanRemove) { refusal = "the client's DeleteObject isn't bound yet"; return RemoveResult.Unavailable; }
        refusal = RemoveRefusal(objectId, Environment.TickCount64);
        if (refusal != null) return RemoveResult.Refused;
        // PR #8's gate: also queued on the main thread while a death/portal transition holds work
        // (the queue refuses it then, so the caller sees Refused and asks again later).
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueRemoveClientObject(objectId) ? RemoveResult.Queued : RemoveResult.Refused;
        return RemoveNow(objectId) ? RemoveResult.Removed : RemoveResult.Refused;
    }

    /// <summary>AC's main thread: the checks again, then ACCObjectMaint::DeleteObject.</summary>
    internal static bool RemoveNow(uint objectId)
    {
        if (!CanRemove || !MainThreadGuard.IsOnMainThread()) return false;
        string? why = RemoveRefusal(objectId, Environment.TickCount64);
        if (why != null)
        {
            RynthLog.Compat($"RemoveClientObject 0x{objectId:X8}: not removed - {why}.");
            return false;
        }
        try
        {
            // Through the detour's original: the hook's own bookkeeping (plugins' OnDeleteObject,
            // caches) runs as for a server delete.
            DeleteObjectDetour(_objectMaint, objectId);
            RynthLog.Compat($"RemoveClientObject 0x{objectId:X8}: removed from the client (ghost).");
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RemoveClientObject 0x{objectId:X8} failed - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
