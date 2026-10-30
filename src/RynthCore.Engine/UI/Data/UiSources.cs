// ============================================================================
//  RynthCore.Engine - UI/Data/UiSources.cs
//  The hub's sources, one instance each. Touching this class registers them
//  with UiDataHub; a source is only polled while a panel face subscribes.
// ============================================================================

using System;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal static class UiSources
{
    public static readonly PluginStatusSource PluginStatus = new();
    public static readonly LogSource Log = new();
    public static readonly TrackerSource Tracker = new();
    public static readonly RynthAiSource RynthAi = new();
    public static readonly PatrolSource Patrol = new();
    public static readonly ProgressionSource Progression = new();
    public static readonly RadarSource Radar = new();
    public static readonly SettingsSource Settings = new();
    public static readonly MetaSource Meta = new();
    public static readonly LuaSource Lua = new();
    public static readonly NavSource Nav = new();
    public static readonly ItemsSource Items = new();
    public static readonly VisionSource Vision = new();
    public static readonly RynthNavSource RynthNav = new();
    public static readonly RynthNavAtlasSource RynthNavAtlas = new();
    public static readonly RynthNavArrowWatch RynthNavArrowWatch = new();
    public static readonly RynthSortWatch RynthSortWatch = new();
    public static readonly ChatSource Chat = new();
    public static readonly DamageSource Damage = new();
    public static readonly MonsterRulesSource MonsterRules = new();
    public static readonly MonsterDetailSource MonsterDetail = new();
    public static readonly LootEditSource LootEdit = new();
    public static readonly SenseSource Sense = new();
    public static readonly BountiesSource Bounties = new();
    public static readonly IltStatusSource IltStatus = new();
    public static readonly CharactersSource Characters = new();

    static UiSources()
    {
        UiDataHub.Register(Bounties);
        UiDataHub.Register(IltStatus);
        UiDataHub.Register(Settings);
        UiDataHub.Register(Meta);
        UiDataHub.Register(LootEdit);
        UiDataHub.Register(Lua);
        UiDataHub.Register(Nav);
        UiDataHub.Register(Items);
        UiDataHub.Register(Vision);
        UiDataHub.Register(RynthNav);
        UiDataHub.Register(RynthNavAtlas);
        UiDataHub.Register(RynthNavArrowWatch);
        UiDataHub.Register(RynthSortWatch);
        UiDataHub.Register(Chat);
        UiDataHub.Register(Damage);
        UiDataHub.Register(MonsterRules);
        UiDataHub.Register(MonsterDetail);
        UiDataHub.Register(Radar);
        UiDataHub.Register(RynthAi);
        UiDataHub.Register(Patrol);
        UiDataHub.Register(Progression);
        UiDataHub.Register(PluginStatus);
        UiDataHub.Register(Log);
        UiDataHub.Register(Tracker);
        UiDataHub.Register(Sense);
        UiDataHub.Register(Characters);
    }
}

/// <summary>One row of the Status panel's plugin list, preformatted.</summary>
internal readonly record struct PluginStatusRow(string Label, string State, bool IsError);

/// <summary>
/// Loaded plugins with version and state, for the Status panel. Engine-local:
/// reads PluginManager's list on the pump thread (which owns it) instead of
/// from a UI thread as the Avalonia panel used to.
/// </summary>
internal sealed class PluginStatusSource : UiSource<PluginStatusRow[]>
{
    public PluginStatusSource() : base("PluginStatus", periodMs: 1000) { }

    protected internal override void Poll()
    {
        var plugins = PluginManager.Plugins;
        var rows = new PluginStatusRow[plugins.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            LoadedPlugin p = plugins[i];
            string version = p.VersionString.Length > 0 ? " " + p.VersionString : "";
            string state = p.Failed ? "FAILED" : p.Initialized ? "OK" : "pending";
            rows[i] = new PluginStatusRow(p.DisplayName + version, state, p.Failed);
        }

        PluginStatusRow[]? previous = Current?.Value;
        if (previous == null || !previous.AsSpan().SequenceEqual(rows))
            Publish(rows);
    }

    protected internal override void Reset() => ClearSnapshot();
}
