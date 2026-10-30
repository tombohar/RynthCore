// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SkillsFace.cs
//  The Skills panel (docs/IMGUI_SKILLS.md): a modern stand-in for the retail
//  character and skills windows.
//
//    - XP summary: level, unassigned XP, total XP, XP to the next level, skill
//      credits, luminance and vitae when they apply.
//    - Skills tab: grouped Specialized / Trained / Untrained / Unusable like
//      retail (groups fold); each row: dat icon, name, buffed, base, XP for the
//      next raise, a raise button; search and sort (name, buffed, base, next
//      raise; either direction). A click opens the row: ranks, XP spent, the
//      skill's description and its buffs with time left.
//    - Attributes & Vitals tab: the same rows for the six attributes and the
//      three vital maximums.
//    - Raise: "+1" raises one rank at once; the caret beside it drops down +10,
//      +100 and Max (each with its XP cost; Max asks first only when it spends
//      over half the unassigned XP). PlayerTraining.Raise(defer) queues it for
//      AC's main thread outside the ImGui frame; raise buttons stay locked until
//      the new values arrive (or 5 s pass). Train (skill credits) always asks first.
//    - Aelrynth only (ServerInfo.IsAelrynth): skill mastery from the SkillMastery
//      mod's /mastery-data reply (MasteryFeed). A crown badge with the mastery
//      points on trained rows that have any; the open row shows points, bonus,
//      the next raise's Radiance price against the banked Radiance (the reply's
//      balance plus the Bank mod's "Radiance earned" since), and "Raise mastery",
//      which always asks first and then sends the mod's "/raise <skill> 1". The
//      summary shows the banked Radiance; hovering it explains mastery. On any
//      other server none of this is drawn and nothing is sent.
//
//  Data: PlayerProgressHooks (main-thread snapshot; Want keeps it coming while
//  the panel draws), SkillDat (portal.dat skill and XP tables), PortalSpellTable
//  (spell names). Rows and their strings are built only when the snapshot, the
//  dat tables, the search or the sort change; the open row's time-left text
//  once a second. AC's render thread only; no AC reads, locks or file I/O here.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend.Hud;
using RynthCore.Engine.UI.ScriptWindows;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class SkillsFace : IImGuiPanel
{
    public const string Title = "Skills";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(480, 640), new Vector2(380, 320), EdgeToEdge: true, GripInBody: true),
        () => new SkillsFace());

    /// <summary>Numbers change on raises, buffs and XP: 4 Hz idle when popped out.</summary>
    public int PopOutIdleHz => 4;

    // ── Palette (FaceKit's, plus the vitals') ────────────────────────────
    private static readonly uint Mana = RynthTheme.Argb(0xFF4DA6FF), Dim = RynthTheme.Argb(0xFF7F8FA0),
        GroupBg = RynthTheme.Argb(0xFF0F1A24), MasteryColor = RynthTheme.Argb(0xFFC9A2FF);
    private static uint ExpandedBg => RynthTheme.Selected;

    private const float RowH = 24f, IconSize = 20f, ColBuffed = 46f, ColBase = 46f, ColCost = 76f, ColButton = 52f,
        CaretW = 16f, MultiW = 190f;

    private enum RowKind { Skill, Attribute, Vital }
    private enum SortKey { Name, Buffed, Base, Cost }

    private static readonly string[] GroupNames = { "Specialized", "Trained", "Untrained", "Unusable" };
    private static readonly string[] SortNames = { "Name", "Buffed", "Base", "Next raise" };
    private static readonly string[] AttributeNames = { "", "Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self" };
    private static readonly string[] AttributeGlyphs =
    {
        "", PhosphorIcons.Barbell, PhosphorIcons.Heartbeat, PhosphorIcons.SneakerMove,
        PhosphorIcons.Crosshair, PhosphorIcons.Brain, PhosphorIcons.Sparkle,
    };
    private static readonly string[] VitalNames = { "", "Health", "Stamina", "Mana" };
    private static readonly string[] VitalGlyphs = { "", PhosphorIcons.Heart, PhosphorIcons.Lightning, PhosphorIcons.Drop };
    private static readonly uint[] VitalColors = { 0, Red, Amber, Mana };

    private sealed class BuffLine
    {
        public uint Spell;
        public string Name = "";
        public string Amount = "";
        public bool Good;
        public double Expiry;
        public uint Category;
        public float Strength;      // |value| (additive) or |value - 1| (multiplicative)
        public int Surpassed;       // weaker ones in the same spell category, not shown
        public string Time = "";
    }

    private sealed class Row
    {
        public RowKind Kind;
        public uint Stype;           // skill id, attribute 1..6, vital 1..3
        public int Key;              // Kind << 8 | Stype: ImGui id, expanded / confirm lookups
        public int Group;            // skills: 0 specialized .. 3 unusable
        public string Name = "";
        public string Description = "";
        public uint Icon;            // skills: dat icon (0x06 texture)
        public string Glyph = "";    // attributes / vitals: Phosphor icon
        public uint GlyphColor;
        public int Buffed, Base;
        public string BuffedText = "", BaseText = "", CostText = "";
        public float BuffedW, BaseW, CostW;
        public uint BuffedColor, CostColor;
        public uint[]? Table;        // XP table for raising; null: can't be raised with XP
        public uint Ranks, Spent;
        public bool Max;
        public long NextCost = -1;   // XP for one raise; -1 max or not raisable
        public int TrainCost;        // untrained skills: credits to train (portal TrainedCost); 0 = can't
        public int SpecializeCost;   // trained skills: more credits to specialize (SpecializedCost - TrainedCost)
        public string Detail = "";
        public string Detail2 = "";   // a second, wrapped line (specializing)
        public readonly List<BuffLine> Buffs = new();
        public int BuffsUp, BuffsDown;
        // Aelrynth skill mastery (trained skills, from the last /mastery-data reply).
        public MasterySkill? Mastery;
        public string MasteryText = "";     // the row's badge ("3.7"); empty = no badge
        public float MasteryW;
        public string MasteryLine = "";     // expanded row: points and what they add
        public string MasteryLine2 = "";    // expanded row: the next raise's price, or why not
        public string MasteryHover = "";    // the badge's tooltip
    }

    // ── State ───────────────────────────────────────────────────────────
    private int _tab;                                 // 0 skills, 1 attributes & vitals, 2 progression (ILT worlds)
    private SortKey _sort = SortKey.Name;
    private bool _descending;
    private readonly bool[] _collapsed = { false, false, false, true };
    private readonly byte[] _search = new byte[64];
    private string _filter = "";
    private int _expandedKey = -1;
    private long _detailSecond = -1;

    private PlayerProgress? _snap;
    private SkillDatTables? _dat;
    private bool _spellsReady;
    private bool _dirty = true, _orderDirty = true;
    private float _widthFont = -1;

    private readonly List<Row> _skills = new();
    private readonly List<Row> _attributes = new();
    private readonly List<Row> _vitals = new();
    private readonly List<Row>[] _groups = { new(), new(), new(), new() };
    private readonly Dictionary<int, Row> _byKey = new();

    private string[] _summary = Array.Empty<string>();
    private uint[] _summaryColors = Array.Empty<uint>();

    private readonly Picker _picker = new("##skills_sort");

    // Raise confirm and the lock after a raise.
    private int _confirmKey = -1;
    private int _confirmCount = 1;
    private bool _confirmTrain;                       // the confirm trains with credits instead of raising
    // The caret's +10 / +100 / Max drop-down.
    private int _multiKey = -1;
    private Vector2 _multiAt;
    private bool _multiOpenRequested;
    private bool _confirmOpenRequested;
    private Vector2 _confirmAt;
    private bool _pending;
    private long _pendingUntilMs;
    private long _pendingUnassigned;
    private int _pendingCredits;
    private int _pendingVersion;

    private string _status = "";
    private uint _statusColor;
    private long _statusUntilMs;

    // Aelrynth skill mastery: only while ServerInfo.IsAelrynth (nothing shown or sent elsewhere).
    private bool _aelrynth;
    private bool _masteryUnsupported;
    private MasterySnapshot? _mastery;
    private long _radiance = -1;                      // spendable Radiance, estimated once a second
    private long _radianceSecond = -1;
    private int _radiancePart = -1;                   // its index in _summary (tooltip)
    private string _masteryTip = "";
    private bool _confirmMastery;                     // the confirm raises mastery with Radiance
    private bool _masteryPending;
    private long _masteryPendingUntilMs;
    private int _masteryPendingVersion;

    // Shown / hidden exactly once per visible stretch, however often the host (or the Oracle embed) asks:
    // the Progression subscription it takes in OnShownCore is given back in OnHiddenCore, once.
    private readonly ShownLatch _life;

    public SkillsFace() => _life = new ShownLatch(OnShownCore, OnHiddenCore);

    public void OnShown() => _life.Show();

    public void OnHidden() => _life.Hide();

    private void OnShownCore()
    {
        PlayerProgressHooks.Want();
        PlayerProgressHooks.RefreshSoon();
        SkillDat.EnsureLoadQueued();
        PortalSpellTable.EnsureLoadQueued();
        MasteryFeed.PanelOpened();                    // asks only on Aelrynth, and only if the last reply is stale
        ProgressionShown();
    }

    private void OnHiddenCore() => ProgressionHidden();

    public void Draw()
    {
        int req = Interlocked.Exchange(ref _requestedTab, -1);
        if (req >= 0) _tab = req;
        PlayerProgressHooks.Want();
        Refresh();
        ApplyProgressionRequest();
        if (_tab == 2 && !ProgressionTabVisible) _tab = 0;

        float w = Begin(out Vector2 origin, out Vector2 size);
        DrawBody(w, origin, size, showOwnTabs: true);
        End(origin, size);

        _picker.Draw();
        DrawMulti();
        DrawConfirm();
        DrawProgressionConfirm();
    }

    /// <summary>
    /// Oracle-hosted body (the old Skills panel's tabs, without its frame or tab strip).
    /// Called each frame the Oracle window shows a Skills / Attributes / Progress tab.
    /// The first frame runs OnShown (via OracleSkillsEmbed's latch); OracleSkillsEmbed.Clear,
    /// the Oracle window closing and the idle sweep run OnHidden. AC thread.
    /// </summary>
    public static void DrawEmbedded(int tab)
    {
        if (_embed == null)
        {
            _embed = new SkillsFace();
            OracleSkillsEmbed.Bind(_embed.OnShown, _embed.OnHidden);
        }
        OracleSkillsEmbed.NoteDrawn(Environment.TickCount64);
        _embed.DrawHosted(tab);
    }

    /// <summary>Selects Skills (0), Attributes (1) or Progress (2) on the next standalone Draw.</summary>
    public static void RequestTab(int tab)
    {
        tab = tab < 0 ? 0 : tab > 2 ? 2 : tab;
        Volatile.Write(ref _requestedTab, tab);
    }

    private static int _requestedTab = -1;

    /// <summary>Shared instance that draws inside Oracle (no separate Skills window).</summary>
    private static SkillsFace? _embed;

    private void DrawHosted(int tab)
    {
        _tab = tab < 0 ? 0 : tab > 2 ? 2 : tab;
        PlayerProgressHooks.Want();
        Refresh();
        ApplyProgressionRequest();
        // Progress tab on a world without ILT: the tab stays selected and DrawProgression explains.

        Vector2 avail = ImGuiNET.ImGui.GetContentRegionAvail();
        float w = Math.Max(40f, avail.X);
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        DrawBody(w, origin, new Vector2(w, Math.Max(120f, avail.Y)), showOwnTabs: false);

        _picker.Draw();
        DrawMulti();
        DrawConfirm();
        DrawProgressionConfirm();
    }

    private void DrawBody(float w, Vector2 origin, Vector2 size, bool showOwnTabs)
    {
        DrawSummary(w);
        if (showOwnTabs) DrawTabs(w);
        if (_tab == 0) DrawToolbar(w);
        if (_tab != 2) DrawColumnHeader(w, _tab == 0 ? "Skill" : "Attribute");

        float bodyH = Math.Max(60, showOwnTabs
            ? Remaining(origin, size) - GripOverlap()
            : size.Y - (ImGuiNET.ImGui.GetCursorScreenPos().Y - origin.Y));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##skills_body", new Vector2(w, bodyH));
        float bw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        EnsureWidths();
        if (_tab == 2)
            DrawProgression(bw);
        else if (_snap == null || !_snap.SkillTableRead && !_snap.AttributeCacheRead)
            Message("Waiting for your character's data...", bw);
        else if (_dat == null)
            Message("Loading skill names from the client's portal.dat: " + SkillDat.Status, bw);
        else if (_tab == 0)
            DrawSkills(bw);
        else
            DrawAttributes(bw);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();
    }

    // ── Data ────────────────────────────────────────────────────────────

    private void Refresh()
    {
        PlayerProgress? snap = PlayerProgressHooks.Current;
        SkillDatTables? dat = SkillDat.Tables;
        bool spells = PortalSpellTable.Ready;
        long now = Environment.TickCount64;
        long second = now / 1000;

        // Mastery: the flag and the feed's snapshot are volatile reads; the Radiance
        // estimate (property-cache lookups) once a second, a rebuild only when it moved.
        bool aelrynth = ServerInfo.IsAelrynth;
        MasterySnapshot? mastery = aelrynth ? MasteryFeed.Snapshot : null;
        if (mastery != null && (second != _radianceSecond || mastery != _mastery))
        {
            _radianceSecond = second;
            long est = MasteryFeed.EstimateRadiance(mastery);
            if (est != _radiance) { _radiance = est; _dirty = true; }
        }
        if (_masteryPending && (now > _masteryPendingUntilMs || (mastery != null && mastery.Version != _masteryPendingVersion)))
            _masteryPending = false;
        bool unsupported = aelrynth && MasteryFeed.Unsupported;
        if (unsupported != _masteryUnsupported) { _masteryUnsupported = unsupported; _dirty = true; }

        if (snap != _snap || dat != _dat || spells != _spellsReady || aelrynth != _aelrynth || mastery != _mastery || _dirty)
        {
            _snap = snap;
            _dat = dat;
            _spellsReady = spells;
            _aelrynth = aelrynth;
            _mastery = mastery;
            if (mastery == null) _radiance = -1;
            _dirty = false;
            Build();
            _orderDirty = true;
            _detailSecond = -1;
        }
        if (_orderDirty)
        {
            _orderDirty = false;
            Order();
        }

        if (_pending && (now > _pendingUntilMs
            || (snap != null && snap.Version != _pendingVersion
                && (snap.UnassignedXp != _pendingUnassigned || snap.SkillCredits != _pendingCredits))))
            _pending = false;

        // The open row's time-left text, once a second.
        if (second != _detailSecond && _byKey.TryGetValue(_expandedKey, out Row? open))
        {
            _detailSecond = second;
            double serverNow = TimeSyncHooks.GetCurrentServerTime();
            foreach (BuffLine b in open.Buffs) b.Time = TimeLeft(b.Expiry, serverNow);
        }
    }

    private void Build()
    {
        _skills.Clear();
        _attributes.Clear();
        _vitals.Clear();
        _byKey.Clear();
        _widthFont = -1;
        PlayerProgress? s = _snap;
        SkillDatTables? dat = _dat;
        _masteryTip = _aelrynth && _mastery?.Data is { Ok: true } md ? MasteryTip(md) : "";
        if (s == null || dat == null)
        {
            BuildSummary(s, dat);
            return;
        }

        foreach (SkillDatEntry e in dat.Skills.Values)
        {
            if (e.Id < 1 || e.Id > PlayerProgress.MaxSkill) continue;
            uint cls = s.SkillClass[e.Id];
            var r = new Row
            {
                Kind = RowKind.Skill, Stype = e.Id, Name = e.Name, Description = e.Description, Icon = e.Icon,
                Group = cls >= 3 ? 0 : cls == 2 ? 1 : e.UsableUntrained ? 2 : 3,
                Table = cls >= 3 ? dat.SpecializedXp : cls == 2 ? dat.TrainedXp : null,
                Ranks = s.SkillRanks[e.Id], Spent = s.SkillXpSpent[e.Id],
                Buffed = s.SkillBuffed[e.Id], Base = s.SkillBase[e.Id],
            };
            bool known = s.SkillLevelKnown[e.Id] && (r.Group != 3 || r.Base > 0);
            Finish(r, s, known);
            if (r.Table == null)
            {
                // Skills the dat prices at 0 credits are trained for free by the server.
                r.TrainCost = Math.Max(0, e.TrainedCost);
                r.CostText = r.TrainCost > 0 ? r.TrainCost.ToString(CultureInfo.InvariantCulture) + " cr" : "—";
                r.CostColor = r.TrainCost > 0 && r.TrainCost <= s.SkillCredits ? Teal : Dim;
                string train = r.TrainCost > 0
                    ? $"Training costs {Credits(r.TrainCost)} (you have {s.SkillCredits})."
                    : "The skill table has no credit price for it, so it can't be trained from here.";
                r.Detail = (r.Group == 3 ? "Needs training to use. " : "Untrained. ") + train;
            }
            else
            {
                r.Detail = RankDetail(r) + (s.SkillInit[e.Id] > 0 ? $"  ·  innate +{s.SkillInit[e.Id]}" : "")
                    + (cls >= 3 ? "  ·  specialized" : "  ·  trained");
                if (cls == 2)
                {
                    r.SpecializeCost = Math.Max(0, e.SpecializedCost - e.TrainedCost);
                    if (r.SpecializeCost > 0)
                        r.Detail2 = $"Specializing costs {Credits(r.SpecializeCost)} more (you have {s.SkillCredits}). "
                            + "The server has no client action for it: it takes this skill's Gem of Enlightenment.";
                }
            }
            CollectBuffs(r, s, PlayerProgressHooks.ModSkill, e.Id);
            if (r.Table != null && _aelrynth) ApplyMastery(r);
            Add(_skills, r);
        }

        for (uint a = 1; a <= 6; a++)
        {
            var r = new Row
            {
                Kind = RowKind.Attribute, Stype = a, Name = AttributeNames[a], Glyph = AttributeGlyphs[a], GlyphColor = Teal,
                Table = dat.AttributeXp, Ranks = s.AttrRanks[a], Spent = s.AttrXpSpent[a],
                Buffed = (int)s.AttrBuffed[a], Base = (int)s.AttrBase[a],
            };
            Finish(r, s, s.AttrBase[a] > 0 || s.AttrBuffed[a] > 0);
            r.Detail = $"Innate {s.AttrInit[a]}  ·  " + RankDetail(r);
            CollectBuffs(r, s, PlayerProgressHooks.ModAttribute, a);
            Add(_attributes, r);
        }

        for (uint v = 1; v <= 3; v++)
        {
            var r = new Row
            {
                Kind = RowKind.Vital, Stype = v, Name = VitalNames[v], Glyph = VitalGlyphs[v], GlyphColor = VitalColors[v],
                Table = dat.VitalXp, Ranks = s.VitalRanks[v], Spent = s.VitalXpSpent[v],
                Buffed = (int)s.VitalBuffed[v], Base = (int)s.VitalBase[v],
            };
            Finish(r, s, s.VitalBase[v] > 0 || s.VitalBuffed[v] > 0);
            r.Detail = RankDetail(r) + "  ·  maximum shown";
            // Vital buffs are keyed by the maximum: 1 health, 3 stamina, 5 mana.
            CollectBuffs(r, s, PlayerProgressHooks.ModSecondAttribute, v * 2 - 1);
            Add(_vitals, r);
        }

        BuildSummary(s, dat);
    }

    // ── Aelrynth skill mastery ───────────────────────────────────────────

    /// <summary>"3.7": ranks as points, the way the server prints them (Mastery.Points).</summary>
    private static string PointsText(int ranks, int perPoint) =>
        perPoint <= 1
            ? ranks.ToString("N0", CultureInfo.InvariantCulture)
            : (ranks / perPoint).ToString("N0", CultureInfo.InvariantCulture) + "." + (ranks % perPoint).ToString(CultureInfo.InvariantCulture);

    /// <summary>What one raise buys, in words.</summary>
    private static string RaiseWord(int perPoint) => perPoint switch
    {
        <= 1 => "one skill point",
        10 => "a tenth of a skill point",
        _ => $"1/{perPoint} of a skill point",
    };

    /// <summary>The hover text that explains mastery, from the server's own settings.</summary>
    private static string MasteryTip(MasteryData d)
    {
        string start = d.RequireCap
            ? "Once a skill reaches its retail maximum, you can keep raising it with Radiance from your account bank."
            : "You can raise trained skills past what experience buys, with Radiance from your account bank.";
        string per = d.PerPoint <= 1
            ? "Each raise adds one point to the skill."
            : $"Each raise is {RaiseWord(d.PerPoint)}; every {d.PerPoint} raises add one point to the skill.";
        string ceiling = d.CeilingPoints > 0
            ? $" Mastery stops at {d.CeilingPoints:N0} points per skill."
            : " There is no ceiling: each raise costs more than the last.";
        return "Skill mastery (Aelrynth)\n" + start + " " + per + ceiling
            + "\nMastery is kept through enlightenment, and buffs and vitae count on it as on retail skill points."
            + "\nThe crown number is the skill's mastery points; open a skill for the price of its next raise.";
    }

    /// <summary>A trained skill's mastery badge and detail lines, from the last /mastery-data reply.</summary>
    private void ApplyMastery(Row r)
    {
        MasterySnapshot? snap = _mastery;
        if (snap == null)
        {
            r.MasteryLine = MasteryFeed.NoChatHook
                ? "Mastery: not available under Decal."
                : MasteryFeed.Unsupported
                ? "Mastery: this server didn't answer the mastery request."
                : "Mastery: no data yet (asked when this panel opens).";
            return;
        }
        MasteryData d = snap.Data;
        if (!d.Ok)
        {
            r.MasteryLine = "Mastery: the server couldn't read your mastery" + (d.Why.Length > 0 ? $" ({d.Why})." : ".");
            return;
        }
        if (!d.Skills.TryGetValue((int)r.Stype, out MasterySkill? m))
        {
            r.MasteryLine = "Mastery: the server sent nothing for this skill.";
            return;
        }
        r.Mastery = m;
        string points = m.Points.Length > 0 ? m.Points : PointsText(m.Ranks, d.PerPoint);
        if (m.Ranks > 0) r.MasteryText = points;
        r.MasteryLine = m.Ranks > 0
            ? $"Mastery {points} points  ·  +{m.Bonus:N0} on the skill  ·  {m.Ranks:N0} raise{(m.Ranks == 1 ? "" : "s")} bought"
            : "Mastery: none yet";
        CanRaiseMastery(r, out string why);
        r.MasteryLine2 = why;
        r.MasteryHover = r.MasteryLine + "\n" + why + "\n\n" + _masteryTip;
    }

    /// <summary>Whether the row's "Raise mastery" works; <paramref name="why"/> is the price line or the reason not.</summary>
    private bool CanRaiseMastery(Row r, out string why)
    {
        MasterySkill? m = r.Mastery;
        MasteryData? d = _mastery?.Data;
        if (m == null || d == null) { why = "No mastery data for this skill yet."; return false; }
        if (!d.Enabled) { why = "Skill mastery is turned off on this server right now."; return false; }
        if (m.Ceiling) { why = $"At the mastery ceiling ({d.CeilingPoints:N0} points)."; return false; }
        if (m.Locked) { why = "Raise it to its retail maximum with experience first; mastery starts there."; return false; }
        if (m.Next <= 0) { why = "The server gave no price for the next raise."; return false; }
        string price = $"Next raise ({RaiseWord(d.PerPoint)}): {RadianceText(m.Next)} Radiance";
        long have = _radiance >= 0 ? _radiance : d.Radiance;
        if (m.Next > have) { why = $"{price}; you have {RadianceText(have)} banked."; return false; }
        if (_masteryPending) { why = $"{price}. Waiting for the last raise to land..."; return false; }
        int afford = Math.Max(m.Afford, 1);
        why = $"{price}; you have {RadianceText(have)} banked"
            + (m.Afford > 1 ? $", enough for about {afford:N0} raises." : ".");
        return true;
    }

    /// <summary>Radiance with separators, and past a trillion in short form (prices reach 2e15).</summary>
    private static string RadianceText(long v) =>
        Math.Abs(v) >= 1_000_000_000_000L
            ? ((double)v).ToString("0.00e0", CultureInfo.InvariantCulture)
            : v.ToString("N0", CultureInfo.InvariantCulture);

    private void Add(List<Row> list, Row r)
    {
        r.Key = ((int)r.Kind << 8) | (int)r.Stype;
        list.Add(r);
        _byKey[r.Key] = r;
    }

    /// <summary>Value texts and colours, and the next raise's cost when the row has an XP table.</summary>
    private static void Finish(Row r, PlayerProgress s, bool known)
    {
        r.BuffedText = known ? r.Buffed.ToString(CultureInfo.InvariantCulture) : "—";
        r.BaseText = known ? r.Base.ToString(CultureInfo.InvariantCulture) : "—";
        r.BuffedColor = !known ? Dim : r.Buffed > r.Base ? Green : r.Buffed < r.Base ? Red : Text;
        if (r.Table == null) return;
        r.Max = SkillDat.IsMax(r.Table, r.Ranks);
        r.NextCost = r.Max ? -1 : SkillDat.CostOf(r.Table, r.Ranks, r.Spent, 1);
        if (r.Max || r.NextCost < 0)
        {
            r.CostText = "max";
            r.CostColor = Amber;
        }
        else
        {
            r.CostText = Compact(r.NextCost);
            r.CostColor = r.NextCost <= s.UnassignedXp ? Teal : Dim;
        }
    }

    private static string RankDetail(Row r)
    {
        int top = Math.Max(0, (r.Table?.Length ?? 1) - 1);
        string toMax = r.Max || r.Table == null ? "at the top rank"
            : "to max " + Compact(Math.Max(0L, (long)r.Table[top] - r.Spent));
        return $"Ranks {r.Ranks:N0}/{top:N0}  ·  XP spent {r.Spent:N0}  ·  {toMax}";
    }

    private static string Credits(int n) => n == 1 ? "1 credit" : $"{n} credits";

    /// <summary>The snapshot's buffs on one stat, strongest per spell category first.</summary>
    private void CollectBuffs(Row r, PlayerProgress s, uint typeFlag, uint key)
    {
        for (int i = 0; i < s.BuffCount; i++)
        {
            if ((s.BuffType[i] & typeFlag) == 0 || s.BuffKey[i] != key) continue;
            float val = s.BuffVal[i];
            bool mult = (s.BuffType[i] & PlayerProgressHooks.ModMultiplicative) != 0;
            float strength = mult ? MathF.Abs(val - 1f) : MathF.Abs(val);
            uint cat = s.BuffCategory[i];
            // Only the strongest of a spell category counts (AC's rule is the highest power;
            // the value is what the snapshot has). The weaker ones are counted, not listed.
            BuffLine? same = null;
            if (cat != 0)
                foreach (BuffLine b in r.Buffs)
                    if (b.Category == cat) { same = b; break; }
            string amount = mult
                ? "×" + val.ToString("0.##", CultureInfo.InvariantCulture)
                : (val >= 0 ? "+" : "") + val.ToString("0.##", CultureInfo.InvariantCulture);
            bool good = mult ? val >= 1f : val >= 0f;
            if (same != null)
            {
                same.Surpassed++;
                if (strength <= same.Strength) continue;
                same.Spell = s.BuffSpell[i];
                same.Name = SpellName(s.BuffSpell[i]);
                same.Amount = amount;
                same.Good = good;
                same.Expiry = s.BuffExpiry[i];
                same.Strength = strength;
                continue;
            }
            r.Buffs.Add(new BuffLine
            {
                Spell = s.BuffSpell[i], Name = SpellName(s.BuffSpell[i]), Amount = amount, Good = good,
                Expiry = s.BuffExpiry[i], Category = cat, Strength = strength,
            });
        }
        r.Buffs.Sort((a, b) => a.Good != b.Good ? (a.Good ? -1 : 1) : string.CompareOrdinal(a.Name, b.Name));
        foreach (BuffLine b in r.Buffs)
        {
            if (b.Good) r.BuffsUp++; else r.BuffsDown++;
        }
    }

    private static string SpellName(uint spell) =>
        PortalSpellTable.TryGetName(spell, out string n) ? n : $"Spell {spell}";

    private void BuildSummary(PlayerProgress? s, SkillDatTables? dat)
    {
        var parts = new List<string>();
        var colors = new List<uint>();
        void Part(string t, uint c) { parts.Add(t); colors.Add(c); }
        if (s == null)
        {
            Part("No character data yet", Dim);
        }
        else
        {
            Part($"Level {s.Level}", Amber);
            Part($"Unassigned {s.UnassignedXp:N0} XP", s.UnassignedXp > 0 ? Teal : Mute);
            Part($"Total {s.TotalXp:N0} XP", Mute);
            if (dat != null && s.Level >= 1 && s.Level + 1 < dat.LevelXp.Length)
            {
                long toNext = (long)Math.Min(dat.LevelXp[s.Level + 1], (ulong)long.MaxValue) - s.TotalXp;
                if (toNext > 0) Part($"Next level in {toNext:N0}", Mute);
            }
            Part(s.SkillCredits == 1 ? "1 skill credit" : $"{s.SkillCredits} skill credits", s.SkillCredits > 0 ? Teal : Mute);
            if (s.MaxLuminance > 0) Part($"Luminance {s.Luminance:N0} / {s.MaxLuminance:N0}", Mute);
            if (s.Vitae > 0f && s.Vitae < 0.999f) Part($"Vitae {s.Vitae * 100f:0}%", Red);
        }

        // Aelrynth: the Radiance a mastery raise spends (hover explains mastery).
        _radiancePart = -1;
        if (_aelrynth && s != null)
        {
            MasteryData? d = _mastery?.Data;
            if (d != null && d.Ok)
            {
                _radiancePart = parts.Count;
                Part(PhosphorIcons.Sun + " Radiance " + RadianceText(_radiance >= 0 ? _radiance : d.Radiance) + " banked",
                    d.Enabled ? MasteryColor : Dim);
            }
            else if (MasteryFeed.NoChatHook)
            {
                Part(PhosphorIcons.Crown + " Mastery: not available under Decal", Dim);
            }
            else if (MasteryFeed.Unsupported)
            {
                Part(PhosphorIcons.Crown + " No mastery on this server", Dim);
            }
            else
            {
                Part(PhosphorIcons.Crown + " Mastery: asking the server...", Dim);
            }
        }
        _summary = parts.ToArray();
        _summaryColors = colors.ToArray();
    }

    /// <summary>Skills into their groups, filtered and sorted.</summary>
    private void Order()
    {
        foreach (List<Row> g in _groups) g.Clear();
        foreach (Row r in _skills)
        {
            if (_filter.Length > 0 && r.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _groups[r.Group].Add(r);
        }
        Comparison<Row> cmp = _sort switch
        {
            SortKey.Buffed => (a, b) => Then(a.Buffed.CompareTo(b.Buffed), a, b),
            SortKey.Base => (a, b) => Then(a.Base.CompareTo(b.Base), a, b),
            SortKey.Cost => (a, b) => Then(CostKey(a).CompareTo(CostKey(b)), a, b),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        foreach (List<Row> g in _groups)
        {
            g.Sort(cmp);
            if (_descending) g.Reverse();
        }
    }

    private static int Then(int c, Row a, Row b) =>
        c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static long CostKey(Row r) => r.NextCost >= 0 ? r.NextCost : long.MaxValue;

    /// <summary>Text widths are cached per row; measured again when the font size changes.</summary>
    private void EnsureWidths()
    {
        float fs = ImGuiNET.ImGui.GetFontSize();
        if (fs == _widthFont) return;
        _widthFont = fs;
        foreach (Row r in _byKey.Values)
        {
            r.BuffedW = ImGuiNET.ImGui.CalcTextSize(r.BuffedText).X;
            r.BaseW = ImGuiNET.ImGui.CalcTextSize(r.BaseText).X;
            r.CostW = ImGuiNET.ImGui.CalcTextSize(r.CostText).X;
            r.MasteryW = r.MasteryText.Length > 0 ? ImGuiNET.ImGui.CalcTextSize(r.MasteryText).X : 0;
        }
    }

    // ── Header pieces ───────────────────────────────────────────────────

    private void DrawSummary(float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float lineH = ImGuiNET.ImGui.GetTextLineHeight();
        float x = start.X, y = start.Y;
        for (int i = 0; i < _summary.Length; i++)
        {
            float tw = ImGuiNET.ImGui.CalcTextSize(_summary[i]).X;
            if (x > start.X && x + tw > start.X + w) { x = start.X; y += lineH + 2; }
            dl.AddText(new Vector2(x, y), _summaryColors[i], _summary[i]);
            if (i == _radiancePart && _masteryTip.Length > 0
                && ImGuiNET.ImGui.IsMouseHoveringRect(new Vector2(x, y), new Vector2(x + tw, y + lineH)))
                SafeImGui.SetTooltip(_masteryTip);
            x += tw + 14;
        }
        long now = Environment.TickCount64;
        string status = _pending ? "Sent... waiting for the server" : now < _statusUntilMs ? _status : "";
        if (status.Length > 0)
        {
            y += lineH + 2;
            dl.AddText(new Vector2(start.X, y), _pending ? Teal : _statusColor, status);
        }
        else if ((!PlayerTraining.Available || !PlayerTraining.TrainAvailable) && _snap != null)
        {
            y += lineH + 2;
            dl.AddText(new Vector2(start.X, y), Dim, !PlayerTraining.Available
                ? "Raising isn't available on this client build (the raise functions weren't found)."
                : "Training isn't available on this client build (the train function wasn't found).");
        }
        NextLine(start, y - start.Y + lineH + 6);
    }

    private void DrawTabs(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        // The Progression tab only exists on ILT-like worlds (RynthAi's snapshot says so).
        int count = ProgressionTabVisible ? 3 : 2;
        float tabW = (w - 4 * (count - 1)) / count;
        if (Button("##tab_skills", "Skills", p, new Vector2(tabW, 22), _tab == 0 ? Teal : Text, _tab == 0 ? Selected : BtnFill,
                border: _tab == 0 ? Teal : 0))
            _tab = 0;
        if (Button("##tab_attrs", "Attributes & Vitals", new Vector2(p.X + tabW + 4, p.Y), new Vector2(tabW, 22),
                _tab == 1 ? Teal : Text, _tab == 1 ? Selected : BtnFill, border: _tab == 1 ? Teal : 0))
            _tab = 1;
        if (count == 3 && Button("##tab_prog", "Progression", new Vector2(p.X + 2 * (tabW + 4), p.Y), new Vector2(tabW, 22),
                _tab == 2 ? Teal : Text, _tab == 2 ? Selected : BtnFill, border: _tab == 2 ? Teal : 0))
            _tab = 2;
        NextLine(p, 26);
    }

    private void DrawToolbar(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float sortW = 120, dirW = 24;
        float searchW = Math.Max(80, w - sortW - dirW - 8);
        if (TextBox("##skills_search", _search, p, searchW, "Search skills", out _))
        {
            string f = Utf8(_search).Trim();
            if (f != _filter) { _filter = f; _orderDirty = true; }
        }
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.MagnifyingGlass,
            new Vector2(p.X + searchW - 22, p.Y), new Vector2(22, 22), Dim);
        var sortPos = new Vector2(p.X + searchW + 4, p.Y);
        if (Button("##skills_sort_btn", "Sort: " + SortNames[(int)_sort], sortPos, new Vector2(sortW, 22), Text, BtnFill))
            _picker.Open(sortPos + new Vector2(0, 24), SortNames, (int)_sort, i => { _sort = (SortKey)i; _orderDirty = true; }, sortW);
        if (IconButton("##skills_dir", _descending ? PhosphorIcons.SortDescending : PhosphorIcons.SortAscending,
                new Vector2(sortPos.X + sortW + 4, p.Y), new Vector2(dirW, 22), Teal, BtnFill, font: UiFont.Ui11))
        {
            _descending = !_descending;
            _orderDirty = true;
        }
        SafeImGui.SetItemTooltip(_descending ? "Highest first" : "Lowest first");
        NextLine(p, 26);
    }

    private static void DrawColumnHeader(float w, string first)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr f = ImGuiFonts.Get(UiFont.Ui9);
        float y = p.Y + (18 - f.FontSize) * 0.5f;
        dl.AddRectFilled(p, p + new Vector2(w, 18), GroupBg);
        dl.AddText(f, f.FontSize, new Vector2(p.X + 8 + IconSize, y), Mute, first);
        float right = p.X + w - 4 - ColButton - 4;
        RightText(dl, f, right, y, Mute, "Cost");
        right -= ColCost;
        RightText(dl, f, right, y, Mute, "Base");
        right -= ColBase;
        RightText(dl, f, right, y, Mute, "Buffed");
        NextLine(p, 20);
    }

    private static void RightText(ImDrawListPtr dl, ImFontPtr f, float right, float y, uint col, string text)
    {
        float tw = f.CalcTextSizeA(f.FontSize, float.MaxValue, 0f, text).X;
        dl.AddText(f, f.FontSize, new Vector2(right - tw, y), col, text);
    }

    // ── Lists ───────────────────────────────────────────────────────────

    private void DrawSkills(float w)
    {
        bool any = false;
        for (int g = 0; g < _groups.Length; g++)
        {
            List<Row> rows = _groups[g];
            if (rows.Count == 0 && _filter.Length > 0) continue;
            any = true;
            GroupHeader(g, rows.Count, w);
            if (_collapsed[g]) continue;
            for (int i = 0; i < rows.Count; i++) DrawRow(rows[i], w, i);
        }
        if (!any) Message($"No skill matches \"{_filter}\".", w);
    }

    private void DrawAttributes(float w)
    {
        Section("Attributes", w);
        for (int i = 0; i < _attributes.Count; i++) DrawRow(_attributes[i], w, i);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
        Section("Vitals (maximum)", w);
        for (int i = 0; i < _vitals.Count; i++) DrawRow(_vitals[i], w, i);
    }

    private void GroupHeader(int g, int count, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.PushID(1000 + g);
        if (ImGuiNET.ImGui.InvisibleButton("##grp", new Vector2(w, 22))) _collapsed[g] = !_collapsed[g];
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        ImGuiNET.ImGui.PopID();
        dl.AddRectFilled(p, p + new Vector2(w, 22), hot ? Lighten(Selected) : Selected);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), _collapsed[g] ? PhosphorIcons.CaretRight : PhosphorIcons.CaretDown,
            p + new Vector2(4, 1), new Vector2(18, 20), Amber);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + 26, p.Y + (22 - bold.FontSize) * 0.5f), Amber,
            $"{GroupNames[g]} ({count})");
        NextLine(p, 23);
    }

    private static void Section(string title, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, 22), Selected);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + 8, p.Y + (22 - bold.FontSize) * 0.5f), Amber, title);
        NextLine(p, 23);
    }

    private static void Message(string text, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(8, 8);
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w - 24);   // window-local x
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    private void DrawRow(Row r, float w, int index)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool expanded = r.Key == _expandedKey;
        if (!ImGuiNET.ImGui.IsRectVisible(p, p + new Vector2(w, RowH)))
        {
            NextLine(p, RowH);
            if (expanded) DrawDetail(r, w);
            return;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, RowH), expanded ? ExpandedBg : index % 2 == 0 ? RowAlt : PanelBg);
        float textY = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        bool unusable = r.Kind == RowKind.Skill && r.Group == 3;

        // Icon.
        var iconMin = new Vector2(p.X + 4, p.Y + (RowH - IconSize) * 0.5f);
        if (r.Icon != 0)
        {
            if (ScriptIcons.TryGet(ScriptIconKind.Icon, r.Icon, out IntPtr tex))
                dl.AddImage(tex, iconMin, iconMin + new Vector2(IconSize, IconSize), Vector2.Zero, Vector2.One,
                    unusable ? 0x80FFFFFFu : 0xFFFFFFFFu);
            else
                dl.AddRectFilled(iconMin, iconMin + new Vector2(IconSize, IconSize), BtnFill, 2);
        }
        else if (r.Glyph.Length > 0)
        {
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), r.Glyph, iconMin, new Vector2(IconSize, IconSize), r.GlyphColor);
        }

        // Right-hand columns, from the right edge in.
        float right = p.X + w - 4;
        var btnMin = new Vector2(right - ColButton, p.Y + 1);
        right -= ColButton + 4;
        float costRight = right;
        right -= ColCost;
        float baseRight = right;
        right -= ColBase;
        float buffedRight = right;
        right -= ColBuffed;

        // Name, with the buff marks after it; the mastery badge (Aelrynth) at its right end.
        float nameX = p.X + 8 + IconSize;
        float nameMax = right - 4;
        Vector2 badgeMin = default, badgeMax = default;
        if (r.MasteryText.Length > 0)
        {
            float bw = 15 + r.MasteryW;
            badgeMin = new Vector2(Math.Max(nameX, nameMax - bw), p.Y);
            badgeMax = new Vector2(nameMax, p.Y + RowH);
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.Crown, badgeMin, new Vector2(14, RowH), MasteryColor);
            dl.AddText(new Vector2(badgeMin.X + 15, textY), MasteryColor, r.MasteryText);
            nameMax = badgeMin.X - 4;
        }
        dl.PushClipRect(new Vector2(nameX, p.Y), new Vector2(nameMax, p.Y + RowH), true);
        dl.AddText(new Vector2(nameX, textY), unusable ? Dim : Text, r.Name);
        dl.PopClipRect();
        if (r.BuffsUp + r.BuffsDown > 0)
        {
            float nx = Math.Min(nameX + ImGuiNET.ImGui.CalcTextSize(r.Name).X + 4, nameMax - 16);
            ImFontPtr f11 = ImGuiFonts.Get(UiFont.Ui11);
            if (r.BuffsUp > 0)
                PhosphorIcons.DrawCentered(dl, f11, PhosphorIcons.Sparkle, new Vector2(nx, p.Y), new Vector2(14, RowH), Green);
            if (r.BuffsDown > 0)
                PhosphorIcons.DrawCentered(dl, f11, PhosphorIcons.TrendDown, new Vector2(nx + (r.BuffsUp > 0 ? 14 : 0), p.Y),
                    new Vector2(14, RowH), Red);
        }

        dl.AddText(new Vector2(buffedRight - r.BuffedW, textY), r.BuffedColor, r.BuffedText);
        dl.AddText(new Vector2(baseRight - r.BaseW, textY), Mute, r.BaseText);
        dl.AddText(new Vector2(costRight - r.CostW, textY), r.CostColor, r.CostText);

        // Row click opens the details (everything but the raise button).
        ImGuiNET.ImGui.PushID(r.Key);
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        if (ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(Math.Max(1, w - ColButton - 8), RowH)))
        {
            _expandedKey = expanded ? -1 : r.Key;
            _detailSecond = -1;
        }
        if (ImGuiNET.ImGui.IsItemHovered() && !expanded)
            dl.AddRect(p, p + new Vector2(w, RowH), BtnBord);
        if (r.MasteryText.Length > 0 && _masteryTip.Length > 0 && ImGuiNET.ImGui.IsMouseHoveringRect(badgeMin, badgeMax))
            SafeImGui.SetTooltip(r.MasteryHover);

        // The row's action buttons: "+1" (raise one rank now, no confirm) and a caret that
        // opens +10 / +100 / Max on trained rows, attributes and vitals; Train (skill
        // credits, with a confirm) on untrained skills. Greyed with the reason when they can't act.
        var btnSize = new Vector2(ColButton, RowH - 2);
        if (r.Table != null)
        {
            var oneSize = new Vector2(ColButton - CaretW - 2, RowH - 2);
            bool canRaise = CanRaise(r, out string why);
            if (Button("##raise1", r.Max ? "max" : "+1", btnMin, oneSize, Teal, BtnFill, canRaise, border: canRaise ? Teal : 0)
                && _snap != null)
                SendRaise(r, _snap, 1);
            SafeImGui.SetItemTooltip(why);
            bool canMore = !r.Max && PlayerTraining.Available && !_pending;
            var caretMin = new Vector2(btnMin.X + oneSize.X + 2, btnMin.Y);
            if (IconButton("##raiseN", PhosphorIcons.CaretDown, caretMin, new Vector2(CaretW, RowH - 2), Teal, BtnFill,
                    canMore, font: UiFont.Ui11))
                OpenMulti(r, caretMin + new Vector2(CaretW - MultiW, RowH));
            SafeImGui.SetItemTooltip(canMore ? "Raise more: +10, +100 or Max"
                : r.Max ? "At the top rank" : _pending ? "Waiting for the last raise to land"
                : "Raising isn't available on this client build");
        }
        else if (r.Kind == RowKind.Skill)
        {
            bool canTrain = CanTrain(r, out string why);
            if (Button("##train", "Train", btnMin, btnSize, Amber, BtnFill, canTrain, border: canTrain ? Amber : 0))
                OpenConfirm(r, train: true);
            SafeImGui.SetItemTooltip(why);
        }
        ImGuiNET.ImGui.PopID();
        NextLine(p, RowH);

        if (expanded) DrawDetail(r, w);
    }

    /// <summary>Whether the row's raise button works, and its tooltip.</summary>
    private bool CanRaise(Row r, out string why)
    {
        if (r.Max) { why = "At the top rank"; return false; }
        if (!PlayerTraining.Available) { why = "Raising isn't available on this client build"; return false; }
        if (_pending) { why = "Waiting for the last raise to land"; return false; }
        long have = _snap?.UnassignedXp ?? 0;
        if (r.NextCost < 0) { why = "Can't be raised"; return false; }
        if (r.NextCost > have)
        {
            why = $"Next raise: {r.NextCost:N0} XP (you have {have:N0} unassigned)";
            return false;
        }
        why = $"Raise to {r.Base + 1}: {r.NextCost:N0} XP";
        return true;
    }

    /// <summary>Whether an untrained skill's Train button works, and its tooltip.</summary>
    private bool CanTrain(Row r, out string why)
    {
        int have = _snap?.SkillCredits ?? 0;
        if (r.TrainCost <= 0) { why = "No credit price in the skill table"; return false; }
        if (!PlayerTraining.TrainAvailable) { why = "Training isn't available on this client build"; return false; }
        if (_pending) { why = "Waiting for the last change to land"; return false; }
        if (r.TrainCost > have)
        {
            why = $"Training costs {Credits(r.TrainCost)}; you have {have}";
            return false;
        }
        why = $"Train {r.Name}: {Credits(r.TrainCost)} (you have {have})";
        return true;
    }

    private void DrawDetail(Row r, float w)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float x = start.X + 8 + IconSize;
        float lineH = ImGuiNET.ImGui.GetTextLineHeight() + 3;
        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        float y = start.Y + 3;

        dl.AddText(new Vector2(x, y), Mute, r.Detail);
        y += lineH;
        if (r.Detail2.Length > 0)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + (start.X + w - 8 - x));   // window-local x
            ImGuiNET.ImGui.TextUnformatted(r.Detail2);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopStyleColor();
            y = ImGuiNET.ImGui.GetCursorScreenPos().Y + 3;
        }
        if (r.MasteryLine.Length > 0)
            y = DrawMasteryDetail(r, x, y, start.X + w - 8, lineH);
        if (r.Description.Length > 0)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGuiNET.ImGui.PushFont(f9);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Dim);
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + (start.X + w - 8 - x));   // window-local x
            ImGuiNET.ImGui.TextUnformatted(r.Description);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopFont();
            y = ImGuiNET.ImGui.GetCursorScreenPos().Y + 3;
        }

        if (r.Buffs.Count == 0)
        {
            dl.AddText(new Vector2(x, y), Dim, "No buffs or debuffs on this.");
            y += lineH;
        }
        foreach (BuffLine b in r.Buffs)
        {
            var icon = new Vector2(x, y);
            if (PortalSpellTable.TryGetIcon(b.Spell, out uint iconId) && iconId != 0
                && ScriptIcons.TryGet(ScriptIconKind.Icon, iconId, out IntPtr tex))
                dl.AddImage(tex, icon, icon + new Vector2(16, 16));
            else
                PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), b.Good ? PhosphorIcons.Sparkle : PhosphorIcons.TrendDown,
                    icon, new Vector2(16, 16), b.Good ? Green : Red);
            float tx = x + 20;
            dl.AddText(new Vector2(tx, y), b.Good ? Green : Red, b.Amount);
            tx += 48;
            string name = b.Surpassed > 0 ? $"{b.Name}  (+{b.Surpassed} weaker)" : b.Name;
            float timeW = b.Time.Length > 0 ? ImGuiNET.ImGui.CalcTextSize(b.Time).X : 0;
            float nameMax = start.X + w - 12 - timeW - 8;
            dl.PushClipRect(new Vector2(tx, y), new Vector2(Math.Max(tx + 1, nameMax), y + lineH), true);
            dl.AddText(new Vector2(tx, y), Text, name);
            dl.PopClipRect();
            if (timeW > 0)
            {
                PhosphorIcons.DrawCentered(dl, f9, PhosphorIcons.Hourglass, new Vector2(nameMax - 2, y), new Vector2(10, lineH - 3), Dim);
                dl.AddText(new Vector2(start.X + w - 12 - timeW, y), Mute, b.Time);
            }
            y += Math.Max(lineH, 18);
        }
        float h = y - start.Y + 4;
        dl.AddLine(new Vector2(start.X, start.Y + h - 1), new Vector2(start.X + w, start.Y + h - 1), BtnBord);
        NextLine(start, h);
    }

    /// <summary>
    /// The open row's mastery block (Aelrynth): points and bonus, the next raise's price
    /// or why it can't be bought, and "Raise mastery" (asks first). Returns the next y.
    /// </summary>
    private float DrawMasteryDetail(Row r, float x, float y, float right, float lineH)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.Crown, new Vector2(x, y), new Vector2(14, lineH - 3), MasteryColor);
        dl.AddText(new Vector2(x + 18, y), MasteryColor, r.MasteryLine);
        if (_masteryTip.Length > 0)
        {
            float tw = ImGuiNET.ImGui.CalcTextSize(r.MasteryLine).X;
            if (ImGuiNET.ImGui.IsMouseHoveringRect(new Vector2(x, y), new Vector2(x + 18 + tw, y + lineH)))
                SafeImGui.SetTooltip(_masteryTip);
        }
        y += lineH;
        if (r.MasteryLine2.Length > 0)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x + 18, y));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + Math.Max(40, right - x - 18));   // window-local x
            ImGuiNET.ImGui.TextUnformatted(r.MasteryLine2);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopStyleColor();
            y = ImGuiNET.ImGui.GetCursorScreenPos().Y + 3;
        }
        if (r.Mastery != null)
        {
            bool can = CanRaiseMastery(r, out string why);
            ImGuiNET.ImGui.PushID(r.Key + 0x10000);
            if (Button("##mastery_raise", PhosphorIcons.Crown + " Raise mastery", new Vector2(x + 18, y), new Vector2(130, 22),
                    MasteryColor, BtnFill, can, border: can ? MasteryColor : 0))
                OpenMasteryConfirm(r);
            SafeImGui.SetItemTooltip(can ? why + "\nAsks before it spends anything." : why);
            ImGuiNET.ImGui.PopID();
            y += 26;
        }
        return y;
    }

    private void OpenMasteryConfirm(Row r)
    {
        _confirmKey = r.Key;
        _confirmTrain = false;
        _confirmMastery = true;
        _confirmCount = 1;
        _confirmAt = ImGuiNET.ImGui.GetIO().MousePos + new Vector2(-280, 8);
        _confirmOpenRequested = true;
    }

    /// <summary>The mastery confirm: one raise for Radiance, sent as the mod's own "/raise &lt;skill&gt; 1".</summary>
    private void MasteryConfirmBody(Row r)
    {
        const float width = 300;
        MasterySkill m = r.Mastery!;
        MasteryData d = _mastery!.Data;
        bool ok = CanRaiseMastery(r, out string why);
        long have = _radiance >= 0 ? _radiance : d.Radiance;

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        Label($"Raise {r.Name} mastery", MasteryColor);
        ImGuiNET.ImGui.PopFont();
        Label($"One raise: {RaiseWord(d.PerPoint)}.", Text);
        Label($"Mastery {PointsText(m.Ranks, d.PerPoint)} → {PointsText(m.Ranks + 1, d.PerPoint)} points", Text);
        Label($"Cost: {RadianceText(m.Next)} Radiance", ok ? Teal : Red);
        Label(m.Next <= have
            ? $"Banked: about {RadianceText(have)} → {RadianceText(have - m.Next)}"
            : $"Banked: about {RadianceText(have)} (not enough)", Mute);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + width);
        Label(ok
            ? "Radiance spent on mastery can't be taken back. The server checks the price and your balance again, "
              + "and its answer appears in chat."
            : why, Dim);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopFont();

        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
        if (Button("##mastery_go", PhosphorIcons.Crown + " Raise", p, new Vector2(120, 24), Text, StartBg, ok))
        {
            SendMasteryRaise(r);
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        if (Button("##mastery_cancel", "Cancel", new Vector2(p.X + 128, p.Y), new Vector2(90, 24), Text, BtnFill))
            ImGuiNET.ImGui.CloseCurrentPopup();
        NextLine(p, 28);
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0));
    }

    private void SendMasteryRaise(Row r)
    {
        if (r.Mastery == null || _mastery == null) return;
        if (MasteryFeed.QueueRaise(r.Mastery, 1, out string why))
        {
            _masteryPending = true;
            _masteryPendingUntilMs = Environment.TickCount64 + 8000;
            _masteryPendingVersion = _mastery.Version;
            SetStatus($"Sent: raise {r.Name} mastery for {RadianceText(r.Mastery.Next)} Radiance.", MasteryColor);
        }
        else
        {
            SetStatus("Mastery raise not sent: " + why, Red);
        }
    }

    // ── Raise confirm ───────────────────────────────────────────────────

    /// <summary>Train (credits), or a Max raise that spends over half the unassigned XP (<paramref name="count"/> ranks).</summary>
    private void OpenConfirm(Row r, bool train, int count = 1)
    {
        _confirmKey = r.Key;
        _confirmTrain = train;
        _confirmMastery = false;
        _confirmCount = count;
        _confirmAt = ImGuiNET.ImGui.GetIO().MousePos + new Vector2(-280, 8);
        _confirmOpenRequested = true;
    }

    // ── Raise more: the +10 / +100 / Max drop-down ───────────────────────

    private void OpenMulti(Row r, Vector2 at)
    {
        _multiKey = r.Key;
        _multiAt = at;
        _multiOpenRequested = true;
    }

    /// <summary>
    /// The caret's drop-down: +10, +100 and Max, each with its XP cost, greyed when it
    /// can't be bought. A click raises at once; Max asks first only when it would spend
    /// more than half the unassigned XP. Drawn at the end of Draw, outside the body child.
    /// </summary>
    private void DrawMulti()
    {
        const string id = "##skills_multi";
        if (_multiOpenRequested)
        {
            _multiOpenRequested = false;
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Clamp(_multiAt.X, 0, Math.Max(0, display.X - MultiW)),
                Math.Clamp(_multiAt.Y, 0, Math.Max(0, display.Y - 90))));
            ImGuiNET.ImGui.OpenPopup(id);
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3, 3));
        bool open = ImGuiNET.ImGui.BeginPopup(id);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            if (!_byKey.TryGetValue(_multiKey, out Row? r) || r.Table == null || _snap == null || r.Max)
            {
                ImGuiNET.ImGui.CloseCurrentPopup();
                return;
            }
            PlayerProgress s = _snap;
            long have = s.UnassignedXp;
            int maxLeft = Math.Max(0, r.Table.Length - 1 - (int)Math.Min(r.Ranks, (uint)int.MaxValue));
            int affordable = SkillDat.Affordable(r.Table, r.Ranks, r.Spent, have);
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            float y = p.Y;
            MultiItem(r, s, "##m10", "+10", 10, maxLeft, have, p.X, ref y);
            MultiItem(r, s, "##m100", "+100", 100, maxLeft, have, p.X, ref y);
            MultiItem(r, s, "##mmax", affordable > 0 ? $"Max (+{affordable})" : "Max", affordable, maxLeft, have, p.X, ref y);
            NextLine(p, y - p.Y);
            ImGuiNET.ImGui.PopFont();
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    /// <summary>One drop-down line: label left, XP cost right; greyed with the reason when it can't act.</summary>
    private void MultiItem(Row r, PlayerProgress s, string id, string label, int n, int maxLeft, long have, float x, ref float y)
    {
        const float h = 22;
        long cost = n > 0 && n <= maxLeft ? SkillDat.CostOf(r.Table!, r.Ranks, r.Spent, n) : -1;
        bool isMax = id == "##mmax";
        string right, why;
        bool ok = false;
        if (n <= 0 || cost < 0)
        {
            right = isMax ? "not enough XP" : "past top rank";
            why = isMax ? $"The next raise costs {r.NextCost:N0} XP; you have {have:N0}"
                : $"Only {maxLeft} rank{(maxLeft == 1 ? "" : "s")} left to the top";
        }
        else if (cost > have)
        {
            right = Compact(cost) + " XP";
            why = $"Costs {cost:N0} XP; you have {have:N0} unassigned";
        }
        else
        {
            right = Compact(cost) + " XP";
            ok = !_pending && PlayerTraining.Available;
            why = $"Raise {r.Name} {r.Base} → {r.Base + n} for {cost:N0} XP"
                + (isMax && cost * 2 > have ? " (asks first: over half your unassigned XP)" : "");
        }
        var pos = new Vector2(x, y);
        var size = new Vector2(MultiW - 6, h);
        if (Button(id, label, pos, size, ok ? Text : Dim, BtnFill, ok, leftAlign: true))
        {
            if (isMax && cost * 2 > have)
                OpenConfirm(r, train: false, count: n);
            else
                SendRaise(r, s, n);
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        SafeImGui.SetItemTooltip(why);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float tw = ImGuiNET.ImGui.CalcTextSize(right).X;
        dl.AddText(new Vector2(x + size.X - 6 - tw, y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), ok ? Teal : Dim, right);
        y += h + 2;
    }

    private void DrawConfirm()
    {
        const string id = "##skills_raise";
        if (_confirmOpenRequested)
        {
            _confirmOpenRequested = false;
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Clamp(_confirmAt.X, 0, Math.Max(0, display.X - 300)),
                Math.Clamp(_confirmAt.Y, 0, Math.Max(0, display.Y - 200))));
            ImGuiNET.ImGui.OpenPopup(id);
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool open = ImGuiNET.ImGui.BeginPopup(id);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            // Closes when the row no longer fits the action (it got trained meanwhile, say).
            if (!_byKey.TryGetValue(_confirmKey, out Row? r) || _snap == null
                || (_confirmMastery ? r.Mastery == null || _mastery == null || !_aelrynth
                    : _confirmTrain ? r.Table != null || r.Kind != RowKind.Skill || r.TrainCost <= 0 : r.Table == null))
            {
                ImGuiNET.ImGui.CloseCurrentPopup();
                return;
            }
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
            try
            {
                if (_confirmMastery) MasteryConfirmBody(r);
                else if (_confirmTrain) TrainConfirmBody(r, _snap);
                else ConfirmBody(r, _snap);
            }
            finally { ImGuiNET.ImGui.PopFont(); }
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    /// <summary>The one confirm a raise can get: Max spending over half the unassigned XP.</summary>
    private void ConfirmBody(Row r, PlayerProgress s)
    {
        const float width = 280;
        uint[] table = r.Table!;
        long have = s.UnassignedXp;
        int maxLeft = Math.Max(0, table.Length - 1 - (int)Math.Min(r.Ranks, (uint)int.MaxValue));
        int n = Math.Clamp(_confirmCount, 1, Math.Max(1, maxLeft));
        long cost = SkillDat.CostOf(table, r.Ranks, r.Spent, n);

        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        ImGuiNET.ImGui.PushFont(bold);
        Label($"Raise {r.Name} +{n}", Amber);
        ImGuiNET.ImGui.PopFont();
        Label($"Base {r.Base} → {r.Base + n}   (rank {r.Ranks} → {r.Ranks + (uint)n})", Text);

        bool ok = cost > 0 && cost <= have && cost <= uint.MaxValue && !_pending && PlayerTraining.Available;
        Label(cost < 0 ? "Past the top rank." : $"Cost: {cost:N0} XP", ok ? Teal : Red);
        Label(cost >= 0 && cost <= have
            ? $"Unassigned: {have:N0} → {have - cost:N0}"
            : $"Unassigned: {have:N0} (not enough)", Mute);
        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        ImGuiNET.ImGui.PushFont(f9);
        Label("That is more than half of your unassigned XP. Spent XP can't be taken back.", Dim);
        ImGuiNET.ImGui.PopFont();

        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
        if (Button("##raise_go", PhosphorIcons.ArrowCircleUp + " Raise", p, new Vector2(120, 24), Text, StartBg, ok))
        {
            SendRaise(r, s, n);
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        if (Button("##raise_cancel", "Cancel", new Vector2(p.X + 128, p.Y), new Vector2(90, 24), Text, BtnFill))
            ImGuiNET.ImGui.CloseCurrentPopup();
        NextLine(p, 28);
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>The train confirm: credits for untrained -> trained (0x0047 TrainSkill).</summary>
    private void TrainConfirmBody(Row r, PlayerProgress s)
    {
        const float width = 300;
        int have = s.SkillCredits;
        int cost = r.TrainCost;
        bool ok = cost > 0 && cost <= have && !_pending && PlayerTraining.TrainAvailable;

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        Label("Train " + r.Name, Amber);
        ImGuiNET.ImGui.PopFont();
        Label(r.Group == 3 ? "Unusable → trained: you can use it and raise it with XP."
                           : "Untrained → trained: you can raise it with XP.", Text);
        Label($"Cost: {Credits(cost)}", ok ? Teal : Red);
        Label(cost <= have ? $"Skill credits: {have} → {have - cost}" : $"Skill credits: {have} (not enough)", Mute);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + width);
        Label("Getting the credits back means untraining it with a Gem of Forgetfulness. "
            + "Specializing later takes this skill's Gem of Enlightenment.", Dim);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopFont();

        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
        if (Button("##train_go", PhosphorIcons.GraduationCap + " Train", p, new Vector2(120, 24), Text, StartBg, ok))
        {
            SendTrain(r, s);
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        if (Button("##train_cancel", "Cancel", new Vector2(p.X + 128, p.Y), new Vector2(90, 24), Text, BtnFill))
            ImGuiNET.ImGui.CloseCurrentPopup();
        NextLine(p, 28);
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0));
    }

    private void SendTrain(Row r, PlayerProgress s)
    {
        if (PlayerTraining.TrainSkill(r.Stype, (uint)r.TrainCost, defer: true))
        {
            MarkPending(s);
            SetStatus($"Sent: train {r.Name} for {Credits(r.TrainCost)}.", Teal);
        }
        else
        {
            SetStatus("The train couldn't be sent (the client's train function isn't bound, or the queue is full).", Red);
        }
    }

    private void MarkPending(PlayerProgress s)
    {
        _pending = true;
        _pendingUntilMs = Environment.TickCount64 + 5000;
        _pendingUnassigned = s.UnassignedXp;
        _pendingCredits = s.SkillCredits;
        _pendingVersion = s.Version;
        PlayerProgressHooks.RefreshSoon();
    }

    /// <summary>
    /// Raises <paramref name="n"/> ranks at once: the XP is worked out from the current
    /// snapshot, checked against the unassigned XP and the top rank, then queued for AC's
    /// main thread. Locks the raise buttons until the new numbers arrive (or 5 s).
    /// </summary>
    private void SendRaise(Row r, PlayerProgress s, int n)
    {
        if (_pending || r.Table == null || n < 1) return;
        long cost = SkillDat.CostOf(r.Table, r.Ranks, r.Spent, n);
        if (cost <= 0 || cost > s.UnassignedXp || cost > uint.MaxValue)
        {
            SetStatus($"Not raised: {r.Name} +{n} costs {(cost < 0 ? "more ranks than are left" : cost.ToString("N0", CultureInfo.InvariantCulture) + " XP")}.", Red);
            return;
        }
        PlayerTraining.TrainKind kind = r.Kind switch
        {
            RowKind.Attribute => PlayerTraining.TrainKind.Attribute,
            RowKind.Vital => PlayerTraining.TrainKind.Vital,
            _ => PlayerTraining.TrainKind.Skill,
        };
        uint stype = r.Kind == RowKind.Vital ? r.Stype * 2 - 1 : r.Stype;   // vitals by their maximum
        if (PlayerTraining.Raise(kind, stype, (uint)cost, defer: true))
        {
            MarkPending(s);
            SetStatus($"Sent: raise {r.Name} +{n} for {cost:N0} XP.", Teal);
        }
        else
        {
            SetStatus("The raise couldn't be sent (the client's raise functions aren't bound, or the queue is full).", Red);
        }
    }

    private void SetStatus(string text, uint color)
    {
        _status = text;
        _statusColor = color;
        _statusUntilMs = Environment.TickCount64 + 6000;
    }

    // ── Formatting ──────────────────────────────────────────────────────

    private static string Compact(long v)
    {
        if (v >= 1_000_000_000) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (v >= 1_000_000) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (v >= 10_000) return (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return v.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string TimeLeft(double expiry, double serverNow)
    {
        if (expiry >= 1e12) return "no end";   // no duration: item spells while worn, and the like
        if (serverNow <= 0) return "";
        double left = expiry - serverNow;
        if (left <= 0) return "ending";
        long s = (long)left;
        if (s >= 3600) return $"{s / 3600}h {s % 3600 / 60:00}m";
        if (s >= 60) return $"{s / 60}m {s % 60:00}s";
        return $"{s}s";
    }
}
