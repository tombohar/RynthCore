// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/HudFeed.cs
//  The one-line entry points the engine's existing hooks call for the
//  RynthVision overlays (nameplates, debuffs, self plate, combat text), and
//  the gate that keeps them off unless RynthVision is installed.
//
//  Callers (all existing chokepoints, no new hooks):
//    PluginManager.QueueChatWindowText   -> OnChat
//    PluginManager.QueueSelectedTargetChange -> OnSelection
//    PluginManager.QueueDeleteObject      -> OnDeleted
//    CombatActionHooks.CastSpell          -> OnCast
//    SmartBoxHooks (0x01B1 / 0x01B2 / 0x01AD) -> OnDamageEvent, OnKillMessage
//    AppraisalHooks (0xC9 CreatureProfile) -> OnAppraisal
//    MonsterHudData.RecordHealth          -> OnHealth
//  All on AC's main thread; each call is a few compares when the line or
//  event isn't one of ours, and never throws into the hook.
//
//  Gate: the overlays belong to RynthVision, so they run only when
//  RynthCore.Plugin.RynthVision.dll is in engine.json's PluginPaths (the list
//  EntryPoint gates the Vision panel on), re-read every few seconds.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class HudFeed
{
    private const string VisionDll = "RynthCore.Plugin.RynthVision.dll";
    private static volatile bool _visionInstalled;
    private static long _nextGateCheck;

    /// <summary>True when the RynthVision plugin is in the plugin list. Any thread; re-checked every 5 s.</summary>
    public static bool VisionInstalled
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= Volatile.Read(ref _nextGateCheck))
            {
                Volatile.Write(ref _nextGateCheck, now + Stopwatch.Frequency * 5);
                _visionInstalled = HasVision();
            }
            return _visionInstalled;
        }
    }

    private static bool HasVision()
    {
        try
        {
            IReadOnlyList<string> paths = Plugins.EngineSettings.PluginPaths;
            for (int i = 0; i < paths.Count; i++)
                if (string.Equals(Path.GetFileName(paths[i]), VisionDll, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>Cheap gate for the feeds: the cached flag only (Update refreshes it).</summary>
    private static bool On => _visionInstalled && MonsterHudSettings.Loaded;

    public static void OnChat(string? line)
    {
        if (!On || string.IsNullOrEmpty(line)) return;
        try
        {
            line = line.TrimEnd('\r', '\n');   // same instance when there is nothing to trim
            if (MonsterHudSettings.Enabled && MonsterHudSettings.ShowDebuffs) PlateDebuffs.OnChat(line);
            CombatText.OnChat(line);
        }
        catch (Exception ex) { Log("chat", ex); }
    }

    public static void OnCast(uint targetId, int spellId)
    {
        if (!On || !MonsterHudSettings.ShowDebuffs) return;
        try { PlateDebuffs.NoteCast(targetId, spellId); }
        catch (Exception ex) { Log("cast", ex); }
    }

    public static void OnSelection(uint id)
    {
        if (!On) return;
        try { PlateDebuffs.NoteSelection(id); }
        catch (Exception ex) { Log("selection", ex); }
    }

    public static void OnDeleted(uint id)
    {
        if (!On) return;
        try
        {
            PlateDebuffs.Remove(id);
            CombatText.OnDeleted(id);
        }
        catch (Exception ex) { Log("delete", ex); }
    }

    /// <summary><paramref name="share"/>: the hit's damage / the monster's max health (AttackerNotification), 0 = unknown.</summary>
    public static void OnDamageEvent(bool attacker, string? name, uint damage, bool crit, double share = 0)
    {
        if (!On) return;
        try
        {
            CombatText.OnDamageEvent(attacker, name, damage, crit);
            if (attacker && share > 0 && MonsterHudSettings.Enabled && !string.IsNullOrEmpty(name))
                MonsterHud.NoteDealtHit(name, damage, share);
        }
        catch (Exception ex) { Log("damage", ex); }
    }

    public static void OnKillMessage(string? message)
    {
        if (!On) return;
        try { CombatText.OnKillMessage(message); }
        catch (Exception ex) { Log("kill", ex); }
    }

    public static void OnAppraisal(uint id, uint enchantmentBitfield)
    {
        if (!On || !MonsterHudSettings.ShowDebuffs) return;
        try { PlateDebuffs.OnAppraisal(id, enchantmentBitfield); }
        catch (Exception ex) { Log("appraisal", ex); }
    }

    /// <summary><paramref name="prev"/> is -1 when this is the first observation.</summary>
    public static void OnHealth(uint id, float prev, float ratio, uint max)
    {
        if (!On) return;
        try
        {
            if (ratio <= 0.001f && prev > 0.001f) PlateDebuffs.Remove(id);   // died
            CombatText.OnHealth(id, prev, ratio, max);
        }
        catch (Exception ex) { Log("health", ex); }
    }

    private static int _errors;

    private static void Log(string what, Exception ex)
    {
        if (++_errors <= 10)
            RynthLog.UI($"Nameplates: {what} feed failed - {ex.GetType().Name}: {ex.Message}");
    }
}
