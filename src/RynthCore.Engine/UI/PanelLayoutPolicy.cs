// ============================================================================
//  RynthCore.Engine - UI/PanelLayoutPolicy.cs
//  Panel layout per character: the pure part (no AC, no ImGui, no engine
//  state), so tools\PanelLayoutTests can compile it in. PanelLayout.cs does the
//  threads and the engine's stores; PanelLayoutFiles.cs the file operations.
//
//  A layout is the pair of files the engine keeps window places in:
//    panel_state.txt  docked panels, pop-outs, the bar (place, rows, popped
//                     out), plugin windows (v89) and script windows (v71);
//    imgui.ini        the windows plugins draw with ImGui themselves.
//  Every client on a PC used to share one pair, so whichever client saved last
//  won and the others came back from a reload or a restart with its places.
//  Now a client in the world uses its character's own pair:
//    %LOCALAPPDATA%\RynthCore\layouts\<server>\<character>\panel_state.txt, imgui.ini
//  At character select, while no character name is known, and with the setting
//  off (engine.json "PanelLayoutPerCharacter": false) it uses the PC-wide
//  default pair, the files every client used before: panel_state.txt beside
//  that folder and C:\Games\RynthSuite\RynthAi\imgui.ini. A character without
//  a layout yet starts from a copy of the default, so nothing moves the first
//  time. The key is the one the engine's other per-character files use: the
//  server and character of the launch context (SessionStateRegistry).
//
//  A client launched with no character picked (2026-11-05: the owner's client
//  that stops at character select) has no name in its launch context, so it
//  sat on the PC-wide default for the whole session, at every login and after
//  every hot reload, and saved its window places into the shared files that
//  seed new characters. The name is now the launch context's or, when that has
//  none, the player's own name read in the world (Identity). While no name is
//  known the default is only read: nothing writes it automatically, and Save
//  Layout Now doesn't either, with the setting on (MaySaveLayoutInUse); only
//  /rc layout default does. The client switches to its character's layout as
//  soon as the name is known, however late.
//
//  Here: who owns a layout and its file names, which layout a client uses,
//  what a new character's layout is seeded from, the session bookkeeping that
//  decides when to switch and when panels may draw (PanelLayoutSession), and
//  /rc layout's parsing and reply texts.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Engine.UI;

/// <summary>Whose layout: a character on a server (names as the launch context gives them).</summary>
internal sealed record LayoutOwner(string Server, string Character)
{
    // AC names are unique per server ignoring case, and the folders are case-insensitive too.
    public bool Equals(LayoutOwner? other) =>
        other is not null
        && string.Equals(Server, other.Server, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Character, other.Character, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Server), StringComparer.OrdinalIgnoreCase.GetHashCode(Character));
}

/// <summary>The two files of one layout.</summary>
internal readonly record struct LayoutFiles(string PanelState, string ImGuiIni)
{
    public string Folder => Path.GetDirectoryName(PanelState) ?? string.Empty;
}

/// <summary>What /rc layout asks for.</summary>
internal enum LayoutCommand { Invalid, Status, MakeDefault, ResetAsk, ResetConfirm }

/// <summary>The kinds of windows the engine hosts (ImGuiPanelHost): its own panels, plugins' manifest windows, script windows.</summary>
internal enum WindowKind { EnginePanel, PluginWindow, ScriptWindow }

/// <summary>What a layout switch does to one window: nothing, open it, close it (not saved), place it
/// from the layout, or place it and save it there (new to that layout).</summary>
internal enum SwitchStep { Leave, Open, Close, Place, PlaceAndSave }

/// <summary>Why a client in the world uses the PC-wide default (the status line says it).</summary>
internal enum DefaultReason { None, SettingOff, NotInWorld, NoCharacterName, WaitingForName, NotRunning }

internal static class PanelLayoutPolicy
{
    public const string PanelStateFileName = "panel_state.txt";
    public const string ImGuiIniFileName = "imgui.ini";
    public const string LayoutsFolder = "layouts";

    /// <summary>After login, how long a client waits for the character's name before it uses what it has.</summary>
    public const long IdentityWaitMs = 3000;

    /// <summary>A switch that hasn't finished this long after it was asked for no longer keeps panels from drawing.</summary>
    public const long ApplyGiveUpMs = 5000;

    // ── who, and the file names ────────────────────────────────────────

    /// <summary>The layout's owner, or null while no character name is known.</summary>
    public static LayoutOwner? Owner(string? server, string? character)
    {
        string c = (character ?? string.Empty).Trim();
        if (c.Length == 0) return null;
        return new LayoutOwner((server ?? string.Empty).Trim(), c);
    }

    /// <summary>
    /// The layout's owner from what the client knows (2026-11-05): the launch context's character, or
    /// when it has none (a client launched to character select) the name of the player in the world.
    /// The launch context comes first: it is the key the other per-character files use. Null while
    /// neither is known.
    /// </summary>
    public static LayoutOwner? Identity(string? server, string? launchCharacter, string? liveCharacter) =>
        Owner(server, string.IsNullOrWhiteSpace(launchCharacter) ? liveCharacter : launchCharacter);

    /// <summary>"Name on Server" (just the name when the server isn't known), for replies and the log.</summary>
    public static string Describe(LayoutOwner who) =>
        who.Server.Length > 0 ? who.Character + " on " + who.Server : who.Character;

    /// <summary>The PC-wide default pair: today's files.</summary>
    public static LayoutFiles DefaultFiles(string localRoot, string defaultIniPath) =>
        new(Path.Combine(localRoot, PanelStateFileName), defaultIniPath);

    /// <summary>A character's pair: &lt;localRoot&gt;\layouts\&lt;server&gt;\&lt;character&gt;\.</summary>
    public static LayoutFiles CharacterFiles(string localRoot, LayoutOwner who)
    {
        string dir = Path.Combine(localRoot, LayoutsFolder, SafeSegment(who.Server), SafeSegment(who.Character));
        return new LayoutFiles(Path.Combine(dir, PanelStateFileName), Path.Combine(dir, ImGuiIniFileName));
    }

    /// <summary>The files for <paramref name="who"/>; null = the PC-wide default.</summary>
    public static LayoutFiles FilesFor(string localRoot, string defaultIniPath, LayoutOwner? who) =>
        who is null ? DefaultFiles(localRoot, defaultIniPath) : CharacterFiles(localRoot, who);

    private const int MaxSegment = 64;

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// <paramref name="name"/> as one folder name: characters Windows doesn't allow become '_', no
    /// trailing dot or space, not a device name, at most 64 characters; "_" for an empty name. A name
    /// that had to change gets "-" and a hash of the original, so two names that clean up alike
    /// (a server "A:B" and one "A?B") keep apart. Names that are already safe stay as they are.
    /// </summary>
    public static string SafeSegment(string? name)
    {
        string original = (name ?? string.Empty).Trim();
        if (original.Length == 0) return "_";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(original.Length);
        foreach (char ch in original)
            sb.Append(ch < 32 || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        string s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length > MaxSegment) s = s.Substring(0, MaxSegment).TrimEnd('.', ' ');
        string stem = s.Split('.')[0];
        if (s.Length == 0 || Array.Exists(ReservedNames, r => r.Equals(stem, StringComparison.OrdinalIgnoreCase)))
            s = "_" + s;
        if (s == original) return s;
        uint h = 2166136261;                       // FNV-1a over the lower-cased original
        foreach (char ch in original.ToLowerInvariant())
            h = (h ^ ch) * 16777619;
        return s + "-" + h.ToString("x8", CultureInfo.InvariantCulture);
    }

    // ── which layout ─────────────────────────────────────────────────────

    /// <summary>The layout a client uses: its character's while in the world with the setting on and a
    /// name known; otherwise (null) the PC-wide default.</summary>
    public static LayoutOwner? Choose(bool perCharacter, bool inWorld, LayoutOwner? identity) =>
        perCharacter && inWorld ? identity : null;

    /// <summary>Which of a new character's files are copied from the default: each one the character
    /// doesn't have yet and the default has (a missing default file leaves the character's missing too).</summary>
    public static (bool PanelState, bool Ini) SeedPlan(bool characterPanelState, bool characterIni, bool defaultPanelState, bool defaultIni) =>
        (!characterPanelState && defaultPanelState, !characterIni && defaultIni);

    /// <summary>
    /// Whether a save (imgui.ini or panel_state.txt; Dear ImGui's dirty timer, a moved window, shutdown,
    /// a hot reload, Save Layout Now) may write the layout in use. Always a character's layout. The
    /// PC-wide default only with the setting off (the shared files, as before). With the setting on
    /// the default is only read (2026-11-05): a client on it is at character select or doesn't know
    /// its character yet, and every such client saving its places there made them overwrite each
    /// other and seeded new characters with them. /rc layout default writes it (PanelLayoutFiles).
    /// </summary>
    public static bool MaySaveLayoutInUse(bool characterLayout, bool perCharacter) =>
        characterLayout || !perCharacter;

    /// <summary>Engine panels and plugin windows open and close with the layout; script windows when their owner says (2026-11-05).</summary>
    public static bool FollowsSavedOpenState(WindowKind kind) => kind != WindowKind.ScriptWindow;

    /// <summary>
    /// What a layout switch does to one window (ImGuiPanelHost.ApplyLayoutSwitch): <paramref name="shown"/>
    /// now; <paramref name="saved"/> the new layout has a row for it, <paramref name="savedOpen"/> saved
    /// open; <paramref name="remember"/> Remember Open Windows. A plugin window follows the layout like an
    /// engine panel (2026-11-05): it registers from its plugin's manifest seconds after a login, so one
    /// registered while another layout was in use (a name that came late, a relogin) had that layout's
    /// open state and kept it.
    /// </summary>
    public static SwitchStep StepAtSwitch(WindowKind kind, bool shown, bool remember, bool saved, bool savedOpen)
    {
        bool follows = FollowsSavedOpenState(kind) && remember && saved;
        if (!shown) return follows && savedOpen ? SwitchStep.Open : SwitchStep.Leave;
        if (follows && !savedOpen) return SwitchStep.Close;
        return saved ? SwitchStep.Place : SwitchStep.PlaceAndSave;
    }

    /// <summary>Why the window places weren't saved (the default with the setting on): Save Layout Now's reply and the log.</summary>
    public static string DefaultReadOnlyText(bool inWorld) =>
        "with Panel Layout Per Character on, the PC-wide default layout is only read ("
        + ReasonText(inWorld ? DefaultReason.NoCharacterName : DefaultReason.NotInWorld)
        + "); /rc layout default makes a character's layout the default";

    // ── /rc layout ───────────────────────────────────────────────────────

    public const string Usage = "Usage: /rc layout [status] | default | reset [confirm]";

    /// <summary>"/rc layout" and its arguments (any case, any spacing).</summary>
    public static LayoutCommand ParseCommand(string? args)
    {
        string a = string.Join(' ', (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return a switch
        {
            "" or "status" => LayoutCommand.Status,
            "default" => LayoutCommand.MakeDefault,
            "reset" => LayoutCommand.ResetAsk,
            "reset confirm" => LayoutCommand.ResetConfirm,
            _ => LayoutCommand.Invalid,
        };
    }

    public static string ReasonText(DefaultReason why) => why switch
    {
        DefaultReason.SettingOff => "Panel layout per character is off (Settings > Display)",
        DefaultReason.NotInWorld => "no character is in the world",
        DefaultReason.NoCharacterName => "this client doesn't know its character's name yet, so window places aren't saved until it does",
        DefaultReason.WaitingForName => "still waiting for the character's name",
        DefaultReason.NotRunning => "panel layouts aren't running in this client",
        _ => "the layout is still switching",
    };

    /// <summary>/rc layout: the layout in use and its files, and the default's.</summary>
    public static string[] StatusLines(LayoutOwner? inUse, LayoutFiles files, DefaultReason why, LayoutFiles defaults, bool perCharacter)
    {
        if (inUse is not null)
            return new[]
            {
                $"Panel layout: {Describe(inUse)}'s own: {files.PanelState} and {files.ImGuiIni}.",
                $"New characters start from the default ({defaults.PanelState} and {defaults.ImGuiIni}): /rc layout default makes this layout that default, /rc layout reset puts this character back to it.",
            };
        if (why == DefaultReason.NotRunning)
            return new[] { $"Can't show the panel layout: {ReasonText(why)} (the engine's panels aren't up)." };
        string line = $"Panel layout: the PC-wide default ({ReasonText(why)}): {files.PanelState} and {files.ImGuiIni}.";
        return perCharacter
            ? new[] { line, "Each character gets its own layout, a copy of this default, when it logs in." }
            : new[] { line };
    }

    /// <summary>
    /// Settings > Display's line under "Panel Layout Per Character": whose layout is in use and the
    /// folder its files are in (a character's), or the PC-wide default and why.
    /// </summary>
    public static string SettingsLine(LayoutOwner? inUse, LayoutFiles files, DefaultReason why)
    {
        if (inUse is not null)
            return $"In use: {Describe(inUse)}'s own layout ({files.Folder}).";
        if (why == DefaultReason.NotRunning)
            return "In use: none (" + ReasonText(why) + ").";
        return $"In use: the PC-wide default ({ReasonText(why)}): {files.PanelState}.";
    }

    /// <summary>Settings > Display's reset confirmation: what "Reset this character's layout" will do.</summary>
    public static string ResetConfirmText(LayoutOwner? who) =>
        who is null
            ? "No character's own layout is in use, so there is nothing to reset."
            : $"Replace {Describe(who)}'s panel layout with the default for new characters?\nOpen panels move to the default's places. This can't be undone.";

    /// <summary>Why /rc layout default or reset can't run (null: it can), with the usage.</summary>
    public static string? CharacterLayoutProblem(string verb, bool running, bool perCharacter, bool inWorld, LayoutOwner? inUse, bool switching)
    {
        string why;
        if (!running) why = ReasonText(DefaultReason.NotRunning);
        else if (!perCharacter) why = ReasonText(DefaultReason.SettingOff);
        else if (!inWorld) why = ReasonText(DefaultReason.NotInWorld);
        else if (switching) why = "the layout is still switching; try again in a moment";
        else if (inUse is null) why = ReasonText(DefaultReason.NoCharacterName);
        else return null;
        return $"Can't {verb}: {why}. {Usage}";
    }

    public static string ResetAskReply(LayoutOwner who, LayoutFiles defaults) =>
        $"This replaces {Describe(who)}'s panel layout with the default for new characters ({defaults.PanelState}): open panels move to the default's places. To go ahead: /rc layout reset confirm.";

    public static string ResetDoneReply(LayoutOwner who) =>
        $"{Describe(who)}'s panel layout was reset from the default: the panels are in the default's places now.";

    public static string MakeDefaultDoneReply(LayoutOwner who, LayoutFiles defaults) =>
        $"{Describe(who)}'s panel layout is now the default for new characters ({defaults.PanelState} and {defaults.ImGuiIni}). Characters that already have a layout keep their own.";

    public static string FailedReply(string what, string error) => $"Can't {what}: {error}.";

    public const string BusyReply = "Can't: a panel layout change is already waiting; try again in a moment. " + Usage;
}

/// <summary>A switch to start: write out <see cref="From"/> (when it is a character's), seed or reseed
/// <see cref="To"/>'s files, load them. One at a time, in order with the other UI writes.</summary>
internal readonly record struct LayoutSwitch(int Gen, LayoutOwner? From, bool FromKnown, LayoutOwner? To, bool Reseed, long ForLoginMs)
{
    /// <summary>The layout switched away from is a character's and is kept (not a reset): its files are written out first.</summary>
    public bool FlushFrom => FromKnown && From is not null && !Reseed;
}

/// <summary>
/// One client's layout bookkeeping, render thread only: which layout it should use, when to start a
/// switch, and whether panels may draw. A switch is asked for (Tick), done in the background
/// (PanelLayout: files, then the stores), then applied on the render thread (MarkApplied), one at a time.
/// After a login (or the hot reload that marks one), panels draw only once the character's layout is
/// applied, so they never show first at the default places and then jump.
/// </summary>
internal sealed class PanelLayoutSession
{
    private int _gen;                 // the last switch asked for
    private LayoutOwner? _target;     // ... and the layout it goes to
    private bool _asked;              // a switch was asked for at least once
    private long _askedAtMs;
    private long _askedForLogin;      // the login (LoginCompleteAtMs) that switch was decided for; 0 = not in the world
    private int _appliedGen;
    private LayoutOwner? _applied;
    private long _readyForLogin;      // the login whose layout is applied; 0 = none
    private bool _reseedWanted;       // /rc layout reset confirm: the next switch copies the default over the character's files

    /// <summary>The layout applied last (null = the default). Meaningful once <see cref="AnyApplied"/>.</summary>
    public LayoutOwner? AppliedLayout => _applied;
    public bool AnyApplied => _appliedGen > 0;
    /// <summary>A switch was asked for and isn't applied yet.</summary>
    public bool InFlight => _asked && _appliedGen != _gen;
    /// <summary>The layout the client should use, as of the last Tick; meaningful while <see cref="Decided"/>.</summary>
    public LayoutOwner? Wanted { get; private set; }
    /// <summary>False only in the world while the character's name isn't settled yet.</summary>
    public bool Decided { get; private set; }

    /// <summary>The next switch reseeds the character's files from the default (a reset).</summary>
    public void WantReseed() => _reseedWanted = true;

    /// <summary>
    /// Once a frame. <paramref name="identityRecorded"/>: the login's identity is recorded
    /// (SessionStateRegistry); <paramref name="identity"/> is who the client is now (PanelLayoutPolicy.Identity:
    /// the recorded name, else the player's own; null: no character name yet).
    /// Returns a switch to start, or null.
    /// </summary>
    public LayoutSwitch? Tick(long nowMs, bool perCharacter, bool inWorld, long loginAtMs, bool identityRecorded, LayoutOwner? identity)
    {
        if (!inWorld)
        {
            Decided = true;
            Wanted = null;
        }
        // (A login time of 0 in the world: the flag is set, its time not yet; wait for it.)
        // Recorded without a name (a client launched to character select, 2026-11-05): the player's
        // own name is on its way too, so that waits as long as an unrecorded one. After the wait the
        // default is used (read only) and the next Tick that has a name switches to its layout.
        else if (loginAtMs != 0 && ((identityRecorded && identity is not null) || nowMs - loginAtMs >= PanelLayoutPolicy.IdentityWaitMs))
        {
            Decided = true;
            Wanted = PanelLayoutPolicy.Choose(perCharacter, inWorld, identity);
        }
        else
        {
            Decided = false;
            _reseedWanted = false;   // a new login: a reset asked for the old one is dropped
            return null;   // the name is on its way: no switch, and panels wait (ReadyToDraw)
        }

        if (InFlight) return null;   // one switch at a time; the next Tick after it is applied looks again
        long forLogin = inWorld ? loginAtMs : 0;
        bool reseed = _reseedWanted && Wanted is not null && Equals(Wanted, _applied);
        _reseedWanted = false;
        if (_asked && Equals(Wanted, _target) && !reseed)
        {
            // Already in use (a relogin to the layout the client had, or the default with the setting off).
            if (forLogin != 0) _readyForLogin = forLogin;
            return null;
        }
        var sw = new LayoutSwitch(++_gen, _asked ? _target : null, _asked, Wanted, reseed, forLogin);
        _target = Wanted;
        _asked = true;
        _askedAtMs = nowMs;
        _askedForLogin = forLogin;
        return sw;
    }

    /// <summary>Switch <paramref name="gen"/> is applied on the render thread (its files are in the stores and the ImGui context).</summary>
    public void MarkApplied(int gen)
    {
        if (gen != _gen || gen == _appliedGen) return;
        _appliedGen = gen;
        _applied = _target;
        if (_askedForLogin != 0) _readyForLogin = _askedForLogin;
    }

    /// <summary>
    /// True when panels may draw: outside the world always (nothing draws there), in the world once the
    /// layout decided for this login is applied. A switch stuck past ApplyGiveUpMs stops holding them.
    /// </summary>
    public bool ReadyToDraw(long nowMs, bool inWorld, long loginAtMs)
    {
        if (!inWorld) return true;
        if (loginAtMs != 0 && _readyForLogin == loginAtMs) return true;
        return Decided && InFlight && nowMs - _askedAtMs >= PanelLayoutPolicy.ApplyGiveUpMs;
    }
}
