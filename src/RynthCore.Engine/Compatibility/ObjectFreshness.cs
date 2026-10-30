// ============================================================================
//  RynthCore.Engine - Compatibility/ObjectFreshness.cs
//  When the server last sent anything about each object (2026-10-12, "ghost"
//  monsters: objects the client still draws but the server no longer has for
//  this player - no swing at them ever animates, they never move or die).
//
//  Fed by SmartBoxHooks.DispatchSmartBoxEventDetour with every object-addressed
//  message (create, position, motion, vector, state, parent, pickup, effects,
//  object description, update object); a delete forgets the id. Read by the
//  plugin API's RemoveClientObject (v83) as a safety check: an object the server
//  talked about in the last few seconds is real and is never removed.
//  AC's main thread writes; any thread reads (lock). Bounded: a full table is
//  cleared wholesale (advisory data).
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

internal static class ObjectFreshness
{
    private static readonly object Sync = new();
    private static readonly Dictionary<uint, long> LastMessage = new(1024);
    private const int MaxEntries = 16384;

    /// <summary>True for server messages whose u32 after the opcode is the object they are about.</summary>
    public static bool IsObjectMessage(uint opcode) => opcode switch
    {
        0xF745 => true,   // CreateObject
        0xF748 => true,   // UpdatePosition
        0xF749 => true,   // ParentEvent
        0xF74A => true,   // PickupEvent
        0xF74B => true,   // SetState
        0xF74C => true,   // UpdateMotion
        0xF74E => true,   // VectorUpdate
        0xF754 => true,   // PlayScriptId
        0xF755 => true,   // PlayEffect
        0xF625 => true,   // ObjDescEvent
        0xF7DB => true,   // UpdateObject
        _ => false,
    };

    /// <summary>A server message <paramref name="opcode"/> about <paramref name="objectId"/> arrived at <paramref name="nowMs"/>.</summary>
    public static void Note(uint opcode, uint objectId, long nowMs)
    {
        if (objectId == 0) return;
        if (opcode == 0xF747) { Forget(objectId); return; }   // DeleteObject
        if (!IsObjectMessage(opcode)) return;
        lock (Sync)
        {
            if (LastMessage.Count >= MaxEntries && !LastMessage.ContainsKey(objectId)) LastMessage.Clear();
            LastMessage[objectId] = nowMs;
        }
    }

    public static void Forget(uint objectId)
    {
        lock (Sync) LastMessage.Remove(objectId);
    }

    /// <summary>Milliseconds since the server last sent a message about <paramref name="objectId"/>,
    /// or -1 when it sent none since the engine started watching (or the table was cleared).</summary>
    public static long AgeMs(uint objectId, long nowMs)
    {
        lock (Sync)
            return LastMessage.TryGetValue(objectId, out long t) ? Math.Max(0, nowMs - t) : -1;
    }

    public static void Clear()
    {
        lock (Sync) LastMessage.Clear();
    }
}
