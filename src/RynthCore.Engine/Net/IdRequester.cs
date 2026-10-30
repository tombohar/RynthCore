// ============================================================================
//  RynthCore.Engine - Net/IdRequester.cs
//
//  Who asked for an IdentifyObject (0xC8) the engine sends, so the flight recorder and
//  the per-minute counters can tell the engine's appraisals apart from AC's and Decal's.
//
//  Before: the recorder tagged every 0xC8 [cli] (FlightCapture had no case for it), so
//  3211 identifies in one session's dump could not be split between AutoId, the plugins
//  and the client itself.
//
//  The marker is call-scoped and thread-local: whoever calls CombatActionHooks.RequestId
//  wraps the call in EngineSendMarker.Enter(who). When the call is queued for AC's main
//  thread, the requester rides with the queued entry (PendingIdQueue) and the drain
//  re-enters the marker around the real send. Only a send made under a marker is
//  registered with the recorder (FlightCapture.EngineSend), so a 0xC8 nobody marked stays
//  [cli]: AC itself or Decal.
//
//  Pure: no engine state. Compiled into tools\EngineRobustnessTests and
//  tools\FlightRecorderTests.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Net;

/// <summary>Who asked for an engine 0xC8. The byte value is what the flight ring stores.</summary>
internal enum IdRequester : byte
{
    /// <summary>No marker: not an engine send (AC itself, Decal), or an unmarked engine path.</summary>
    None = 0,
    AutoIdTarget = 1,
    AutoIdContainer = 2,
    AutoIdInventory = 3,
    /// <summary>AutoIdMode All only: an object that is not the target, in an open container or in the pack.</summary>
    AutoIdWorld = 4,
    /// <summary>A plugin through the host RequestId (plugin API).</summary>
    Plugin = 5,
    /// <summary>The engine's delayed login self-identify (max vitals).</summary>
    Self = 6,
    /// <summary>The engine's Inventory panel identifying the item under the cursor.</summary>
    Ui = 7,
}

internal static class IdRequesters
{
    public const int Count = 8;

    /// <summary>The flight-recorder label after "eng:" ("" for None or an unknown code).</summary>
    public static string Label(uint code) => code switch
    {
        1 => "autoid-target",
        2 => "autoid-container",
        3 => "autoid-inventory",
        4 => "autoid-world",
        5 => "plugin",
        6 => "self",
        7 => "ui",
        _ => "",
    };

    /// <summary>Short heartbeat field names (none contains fps=, plug=, login= or qd=).</summary>
    public static string Short(IdRequester who) => who switch
    {
        IdRequester.AutoIdTarget => "tgt",
        IdRequester.AutoIdContainer => "ctr",
        IdRequester.AutoIdInventory => "inv",
        IdRequester.AutoIdWorld => "wld",
        IdRequester.Plugin => "plg",
        IdRequester.Self => "slf",
        IdRequester.Ui => "ui",
        _ => "unm",
    };
}

/// <summary>The thread-local "this 0xC8 is the engine's" marker around a RequestId call.</summary>
internal static class EngineSendMarker
{
    [ThreadStatic] private static IdRequester t_current;

    /// <summary>The requester marked on this thread (None outside any scope).</summary>
    public static IdRequester Current => t_current;

    /// <summary>Marks this thread until the returned scope is disposed (scopes nest).</summary>
    public static Scope Enter(IdRequester who)
    {
        IdRequester previous = t_current;
        t_current = who;
        return new Scope(previous);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly IdRequester _previous;
        internal Scope(IdRequester previous) { _previous = previous; }
        public void Dispose() => t_current = _previous;
    }
}

/// <summary>Engine 0xC8 sends that really went out, per requester (running totals; any thread).</summary>
internal static class IdSendStats
{
    private static readonly long[] _sent = new long[IdRequesters.Count];

    public static void CountSent(IdRequester who)
    {
        int i = (int)who;
        if ((uint)i < (uint)_sent.Length) Interlocked.Increment(ref _sent[i]);
    }

    public static long Sent(IdRequester who)
    {
        int i = (int)who;
        return (uint)i < (uint)_sent.Length ? Interlocked.Read(ref _sent[i]) : 0;
    }
}
