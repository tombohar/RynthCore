// ============================================================================
//  RynthCore.Engine - UI/Data/UiSource.cs
//  One piece of panel data kept fresh by UiDataHub (docs/IMGUI_PARITY_PLAN.md
//  §4.3). A source polls on the plugin pump thread and publishes an immutable
//  snapshot; the ImGui faces (AC's render thread) and the Avalonia faces (the
//  Avalonia UI thread) only ever read the published snapshot.
// ============================================================================

using System.Diagnostics;
using System.Threading;

namespace RynthCore.Engine.UI.Data;

/// <summary>An immutable published value. Readers compare Version to detect change.</summary>
internal sealed class UiSnapshot<T> where T : class
{
    public UiSnapshot(long version, T value)
    {
        Version = version;
        Value = value;
    }

    public long Version { get; }
    public T Value { get; }
}

internal abstract class UiSource
{
    private int _subscribers;
    private long _nextDueTicks;
    private volatile bool _refreshRequested;
    private volatile bool _refreshAfterPluginTick;

    protected UiSource(string name, int periodMs)
    {
        Name = name;
        PeriodMs = periodMs;
    }

    public string Name { get; }

    /// <summary>Minimum time between polls while subscribed.</summary>
    public int PeriodMs { get; }

    /// <summary>
    /// A panel face that shows this data calls Subscribe when it opens and
    /// Unsubscribe when it closes; a source with no subscribers is not polled.
    /// Any thread.
    /// </summary>
    public void Subscribe() => Interlocked.Increment(ref _subscribers);

    public void Unsubscribe()
    {
        if (Interlocked.Decrement(ref _subscribers) < 0)
            Interlocked.Exchange(ref _subscribers, 0);
    }

    public bool HasSubscribers => Volatile.Read(ref _subscribers) > 0;

    /// <summary>Poll on the next hub step regardless of the period (e.g. after a command). Any thread.</summary>
    public void RequestRefresh() => _refreshRequested = true;

    /// <summary>
    /// Poll after the plugin's next tick, not before: for exports that only
    /// queue a command the plugin applies on its tick (Meta). Until then the
    /// source is not polled at all, so no snapshot from before the command
    /// lands in between. Pump thread (from a hub command).
    /// </summary>
    public void RequestRefreshAfterPluginTick() => _refreshAfterPluginTick = true;

    /// <summary>Hub, at the start of a step (the plugin has ticked since the last step).</summary>
    internal void PromoteDeferredRefresh()
    {
        if (!_refreshAfterPluginTick) return;
        _refreshAfterPluginTick = false;
        _refreshRequested = true;
    }

    internal bool IsDue(long now) => !_refreshAfterPluginTick && (_refreshRequested || now >= _nextDueTicks);

    /// <summary>
    /// A source whose poll took <c>cost</c> waits at least CostBackoff times that before its
    /// next poll, so no source can take more than ~1/CostBackoff of the plugin pump however
    /// big its data gets. Below PeriodMs / CostBackoff (3.3 ms for the radar's 33 ms) nothing
    /// changes. Added 2026-10-02: in a crowded dungeon the radar's poll (snapshot build, JSON,
    /// parse; 300+ markers) took 4-7 ms every 33 ms on the thread the plugin tick runs on.
    /// </summary>
    internal const int CostBackoff = 10;

    internal void MarkPolled(long now, long costTicks = 0)
    {
        _refreshRequested = false;
        long period = Stopwatch.Frequency * PeriodMs / 1000;
        long backoff = costTicks > 0 ? costTicks * CostBackoff : 0;
        _nextDueTicks = now + (backoff > period ? backoff : period);
    }

    /// <summary>Fetch and publish. Pump thread, after the plugin tick; may call plugin exports.</summary>
    protected internal abstract void Poll();

    /// <summary>
    /// Plugins were unloaded (RL / rescan): drop cached export bindings and
    /// anything derived from the old plugin copy. Pump thread.
    /// </summary>
    protected internal virtual void Reset() { }
}

/// <summary>A source that publishes a <typeparamref name="T"/> snapshot.</summary>
internal abstract class UiSource<T> : UiSource where T : class
{
    private UiSnapshot<T>? _current;

    protected UiSource(string name, int periodMs) : base(name, periodMs) { }

    /// <summary>The latest snapshot, or null before the first poll / after a reset. Any thread; never blocks.</summary>
    public UiSnapshot<T>? Current => Volatile.Read(ref _current);

    protected void Publish(T value)
    {
        long version = (Volatile.Read(ref _current)?.Version ?? 0) + 1;
        Volatile.Write(ref _current, new UiSnapshot<T>(version, value));
    }

    protected void ClearSnapshot() => Volatile.Write(ref _current, null);
}
