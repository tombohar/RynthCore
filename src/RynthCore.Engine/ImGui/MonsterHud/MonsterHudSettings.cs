// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/MonsterHudSettings.cs
//  Persisted settings of the monster nameplates (MonsterHud.cs), in the same
//  hand-editable key=value style as RadarSettingsStore:
//  %LocalAppData%\RynthCore\nameplate_settings.txt.
//
//  Threads: fields are read on AC's render thread every frame and written by
//  the Settings face (render thread) and /rc plates (AC's chat hook, the same
//  thread). File I/O never runs on the render thread: Load and Save both go
//  through UiBackgroundWriter, and the HUD draws nothing until Loaded is set,
//  so a player who turned it off never sees a frame of plates at startup.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>Which monsters get a plate.</summary>
internal enum PlateFilter
{
    /// <summary>Every attackable monster in range (nearest first, up to MaxPlates).</summary>
    All = 0,
    /// <summary>The selected target, monsters that hit you recently, and monsters that are hurt.</summary>
    Engaged = 1,
}

internal static class MonsterHudSettings
{
    private static readonly object Sync = new();
    private static volatile bool _loaded;
    private static bool _loadQueued;

    // ── Values (conservative defaults) ──────────────────────────────────
    public static bool Enabled = true;
    public static float MaxDistance = 35f;       // yards (AC units)
    public static int MaxPlates = 20;
    public static PlateFilter Filter = PlateFilter.All;
    public static bool ShowNames = true;
    public static bool ShowHpNumbers = true;
    public static bool ShowDistance = false;
    public static bool ShowLevel = true;
    public static bool ShowWeakness = true;      // "Weak to" pill (RynthAi's learned damage data)
    public static bool FadeWithDistance = true;
    public static float Scale = 1.0f;
    public static float Opacity = 0.9f;
    public static float HeightLift = 0.0f;       // extra metres above the estimated head height
    /// <summary>A left-click on a monster plate selects that monster in game.</summary>
    public static bool ClickToSelect = true;

    // Name-only labels over NPCs (vendors in gold) and, optionally, other players.
    // Counted apart from MaxPlates so they never push a monster plate out.
    public static bool NpcNames = true;
    public static float NpcMaxDistance = 30f;    // yards; short so a town isn't a wall of text
    public static int MaxNpcLabels = 15;
    public static bool PlayerNames = false;
    /// <summary>A health bar under other players' names, for those whose health the client has been told (selected, fellowship).</summary>
    public static bool PlayerBars = false;
    /// <summary>
    /// The name over portals (2026-10-28), in the radar's portal colour. On by default: portals are few and
    /// the label is only the name. Their own distance and count, so NPC names never crowd them out.
    /// </summary>
    public static bool PortalNames = true;
    public static float PortalMaxDistance = DefaultPortalDistance;   // yards; portals are worth seeing from further than NPCs
    public const float DefaultPortalDistance = 60f;
    /// <summary>At most this many portal names (nearest first; a selected portal always keeps its own). Not a setting.</summary>
    public const int MaxPortalLabels = 12;

    // Debuff icons on the monster plates (PlateDebuffs).
    public static bool ShowDebuffs = true;
    public static bool ShowOthersDebuff = true;  // "debuffed by others" marker (appraisal highlights)

    // The player's own plate (PlayerPlate): health / stamina / mana over your head.
    public static bool SelfPlate = true;
    public static bool SelfNumbers = false;      // "cur/max" beside each bar
    public static bool SelfName = false;
    public static bool SelfHideFirstPerson = true;
    /// <summary>
    /// Fixed on screen (default since 2026-10-05: a plate tied to the head jitters while you move) or
    /// following the character (SelfPosition, SelfHideFirstPerson). Fixed draws with the normal UI.
    /// </summary>
    public static SelfPlacement SelfPlacement = SelfPlacement.Fixed;
    /// <summary>Fixed mode: the plate can't be dragged (and never takes a click). Unlock to move it.</summary>
    public static bool SelfLocked = true;
    /// <summary>Fixed mode: where the bars' centre sits, as a fraction of the screen (kept on screen when drawn).</summary>
    public static float SelfFixedX = DefaultSelfFixedX, SelfFixedY = DefaultSelfFixedY;
    /// <summary>Just under a third-person character, clear of the chat in the bottom-left corner.</summary>
    public const float DefaultSelfFixedX = 0.5f, DefaultSelfFixedY = 0.68f;
    // Your plate's look (2026-10-27), each piece on its own; all on = the look before they existed.
    // Your plate only: the monster plates share the bar painter but always draw all of it.
    /// <summary>The dark track and 1 px black edge behind each of your bars.</summary>
    public static bool SelfBarTrack = true;
    /// <summary>The pale gold "chip" that shows what a drop took, then drains.</summary>
    public static bool SelfBarChip = true;
    /// <summary>The black outline around your plate's text (numbers, name).</summary>
    public static bool SelfTextOutline = true;
    /// <summary>The dark band behind the "cur / max" numbers.</summary>
    public static bool SelfNumberBand = true;
    /// <summary>"Debuffs on you": icons for harmful spells on you under your mana bar (only while you have one).</summary>
    public static bool SelfDebuffs = true;

    // Combat text (CombatText): floating numbers and the kill burst.
    public static bool Numbers = true;
    public static bool NumDealt = true;
    public static bool NumTaken = true;
    public static bool NumHeals = true;
    public static bool NumKills = true;
    /// <summary>Plates, numbers and your plate stay inside the screen when the camera closes in (walls).</summary>
    public static bool KeepOnScreen = true;
    /// <summary>"+N stam" / "+N mana" over you when a spell, potion or kit restores them.</summary>
    public static bool NumRestores = true;
    /// <summary>Combat number size (1 = the 24 px bake) and how long they stay (1 = default).</summary>
    public static float NumSize = 1.0f, NumTime = 1.0f;
    /// <summary>XP / Lum / Radiance gain text: size and how long it stays (1 = default).</summary>
    public static float GainSize = 1.0f, GainTime = 1.0f;
    /// <summary>Where the player plate sits relative to the character.</summary>
    public static SelfPlatePosition SelfPosition = SelfPlatePosition.Above;
    public const float MinNumSize = 0.5f, MaxNumSize = 2.5f, MinNumTime = 0.5f, MaxNumTime = 3.0f;

    // Gain text over the player: XP, Luminance, Radiance.
    public static bool Gains = true;
    public static bool GainXp = true;
    public static bool GainLum = true;
    public static bool GainRadiance = true;

    // Per-plate-type sizing (2026-10-05), multipliers on today's look (1 = unchanged), applied on
    // top of Scale. Bar width / height and the text drawn in or over the plate, each on its own.
    // NPC and player names are name-only labels (no bar), so they only have a text size.
    /// <summary>Monster plates: the health bar's width and height, and their text (name, level, HP, weak-to, distance).</summary>
    public static float MonsterBarWidth = 1.0f, MonsterBarHeight = 1.0f, MonsterTextSize = 1.0f;
    /// <summary>Name labels over NPCs and vendors, and over portals.</summary>
    public static float NpcTextSize = 1.0f;
    /// <summary>Name labels over other players.</summary>
    public static float PlayerTextSize = 1.0f;
    /// <summary>Your own plate: the three bars' width and height, and their text (cur / max numbers, your name).</summary>
    public static float SelfBarWidth = 1.0f, SelfBarHeight = 1.0f, SelfTextSize = 1.0f;
    public const float MinBarWidth = 0.5f, MaxBarWidth = 2.5f;
    public const float MinBarHeight = 0.5f, MaxBarHeight = 3.0f;
    public const float MinTextSize = 0.5f, MaxTextSize = 2.0f;
    /// <summary>
    /// "Debuff icon size" (2026-10-28), in percent of the icons' size before it existed (100 = unchanged), on
    /// top of Look > Scale: the monster plates' row and your own plate's "Debuffs on you" row, each on its own
    /// and apart from every text size. The icon, its glyph, countdown line, stack digit, "+N" and spacing all
    /// follow it (DebuffRowMetrics).
    /// </summary>
    public static int MonsterDebuffIconSize = DefaultDebuffIconSize, SelfDebuffIconSize = DefaultDebuffIconSize;
    public const int DefaultDebuffIconSize = 100, MinDebuffIconSize = 50, MaxDebuffIconSize = 200;

    // ── Ranges (shared by the Settings face, the command and Load) ──────
    public const float MinDistance = 5f, MaxDistanceLimit = 120f;
    public const int MinPlates = 1, MaxPlatesLimit = 60;
    public const float MinNpcDistance = 5f, MaxNpcDistanceLimit = 80f;
    public const int MinNpcLabels = 1, MaxNpcLabelsLimit = 40;
    public const float MinPortalDistance = 5f, MaxPortalDistanceLimit = 120f;
    public const float MinScale = 0.6f, MaxScale = 2.0f;
    public const float MinOpacity = 0.2f, MaxOpacity = 1.0f;
    public const float MinLift = -1.5f, MaxLift = 3.0f;

    /// <summary>True once the file has been read (or found missing). Any thread.</summary>
    public static bool Loaded => _loaded;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "nameplate_settings.txt");

    /// <summary>Queues the one-time read off the calling thread. Any thread; cheap after the first call.</summary>
    public static void EnsureLoadQueued()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loadQueued) return;
            _loadQueued = true;
        }
        UiBackgroundWriter.Enqueue("nameplate settings (load)", LoadNow);
    }

    private static void LoadNow()
    {
        lock (Sync)
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                ApplyLines(File.ReadAllLines(path));
            }
            catch { /* best-effort: defaults stay */ }
            finally { _loaded = true; }
        }
    }

    /// <summary>Applies the file's key=value lines (unknown keys and bad values are skipped). Also the tests' entry.</summary>
    internal static void ApplyLines(string[] lines)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "enabled": Enabled = val == "1"; break;
                case "maxDistance": if (TryF(val, out float d)) MaxDistance = Math.Clamp(d, MinDistance, MaxDistanceLimit); break;
                case "maxPlates":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                        MaxPlates = Math.Clamp(n, MinPlates, MaxPlatesLimit);
                    break;
                case "filter": Filter = val.Equals("engaged", StringComparison.OrdinalIgnoreCase) ? PlateFilter.Engaged : PlateFilter.All; break;
                case "showNames": ShowNames = val == "1"; break;
                case "showHp": ShowHpNumbers = val == "1"; break;
                case "showDistance": ShowDistance = val == "1"; break;
                case "showLevel": ShowLevel = val == "1"; break;
                case "showWeakness": ShowWeakness = val == "1"; break;
                case "fade": FadeWithDistance = val == "1"; break;
                case "scale": if (TryF(val, out float s)) Scale = Math.Clamp(s, MinScale, MaxScale); break;
                case "opacity": if (TryF(val, out float o)) Opacity = Math.Clamp(o, MinOpacity, MaxOpacity); break;
                case "clickSelect": ClickToSelect = val == "1"; break;
                case "heightLift": if (TryF(val, out float h)) HeightLift = Math.Clamp(h, MinLift, MaxLift); break;
                case "npcNames": NpcNames = val == "1"; break;
                case "npcDistance": if (TryF(val, out float nd)) NpcMaxDistance = Math.Clamp(nd, MinNpcDistance, MaxNpcDistanceLimit); break;
                case "npcMax":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nm))
                        MaxNpcLabels = Math.Clamp(nm, MinNpcLabels, MaxNpcLabelsLimit);
                    break;
                case "playerNames": PlayerNames = val == "1"; break;
                case "playerBars": PlayerBars = val == "1"; break;
                case "portalNames": PortalNames = val == "1"; break;
                case "portalDistance": if (TryF(val, out float pd)) PortalMaxDistance = Math.Clamp(pd, MinPortalDistance, MaxPortalDistanceLimit); break;
                case "showDebuffs": ShowDebuffs = val == "1"; break;
                case "showOthersDebuff": ShowOthersDebuff = val == "1"; break;
                case "selfPlate": SelfPlate = val == "1"; break;
                case "selfNumbers": SelfNumbers = val == "1"; break;
                case "selfName": SelfName = val == "1"; break;
                case "selfHideFirstPerson": SelfHideFirstPerson = val == "1"; break;
                case "selfPlacement": SelfPlacement = val.Equals("follow", StringComparison.OrdinalIgnoreCase) ? SelfPlacement.Follow : SelfPlacement.Fixed; break;
                case "selfLocked": SelfLocked = val == "1"; break;
                case "selfFixedX": if (TryF(val, out float fx)) SelfFixedX = Math.Clamp(fx, 0f, 1f); break;
                case "selfFixedY": if (TryF(val, out float fy)) SelfFixedY = Math.Clamp(fy, 0f, 1f); break;
                case "selfBarTrack": SelfBarTrack = val == "1"; break;
                case "selfBarChip": SelfBarChip = val == "1"; break;
                case "selfTextOutline": SelfTextOutline = val == "1"; break;
                case "selfNumberBand": SelfNumberBand = val == "1"; break;
                case "selfDebuffs": SelfDebuffs = val == "1"; break;
                case "numbers": Numbers = val == "1"; break;
                case "numDealt": NumDealt = val == "1"; break;
                case "numTaken": NumTaken = val == "1"; break;
                case "numHeals": NumHeals = val == "1"; break;
                case "numKills": NumKills = val == "1"; break;
                case "keepOnScreen": KeepOnScreen = val == "1"; break;
                case "numRestores": NumRestores = val == "1"; break;
                case "numSize": if (TryF(val, out float ns)) NumSize = Math.Clamp(ns, MinNumSize, MaxNumSize); break;
                case "numTime": if (TryF(val, out float nt)) NumTime = Math.Clamp(nt, MinNumTime, MaxNumTime); break;
                case "selfPosition": if (Enum.TryParse(val, true, out SelfPlatePosition sp)) SelfPosition = sp; break;
                case "gainSize": if (TryF(val, out float gs)) GainSize = Math.Clamp(gs, MinNumSize, MaxNumSize); break;
                case "gainTime": if (TryF(val, out float gt)) GainTime = Math.Clamp(gt, MinNumTime, MaxNumTime); break;
                case "gains": Gains = val == "1"; break;
                case "gainXp": GainXp = val == "1"; break;
                case "gainLum": GainLum = val == "1"; break;
                case "gainRadiance": GainRadiance = val == "1"; break;
                case "monsterBarWidth": if (TryF(val, out float mbw)) MonsterBarWidth = Math.Clamp(mbw, MinBarWidth, MaxBarWidth); break;
                case "monsterBarHeight": if (TryF(val, out float mbh)) MonsterBarHeight = Math.Clamp(mbh, MinBarHeight, MaxBarHeight); break;
                case "monsterTextSize": if (TryF(val, out float mts)) MonsterTextSize = Math.Clamp(mts, MinTextSize, MaxTextSize); break;
                case "npcTextSize": if (TryF(val, out float nts)) NpcTextSize = Math.Clamp(nts, MinTextSize, MaxTextSize); break;
                case "playerTextSize": if (TryF(val, out float pts)) PlayerTextSize = Math.Clamp(pts, MinTextSize, MaxTextSize); break;
                case "selfBarWidth": if (TryF(val, out float sbw)) SelfBarWidth = Math.Clamp(sbw, MinBarWidth, MaxBarWidth); break;
                case "selfBarHeight": if (TryF(val, out float sbh)) SelfBarHeight = Math.Clamp(sbh, MinBarHeight, MaxBarHeight); break;
                case "selfTextSize": if (TryF(val, out float sts)) SelfTextSize = Math.Clamp(sts, MinTextSize, MaxTextSize); break;
                case "monsterDebuffIconSize":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mdi))
                        MonsterDebuffIconSize = Math.Clamp(mdi, MinDebuffIconSize, MaxDebuffIconSize);
                    break;
                case "selfDebuffIconSize":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sdi))
                        SelfDebuffIconSize = Math.Clamp(sdi, MinDebuffIconSize, MaxDebuffIconSize);
                    break;
            }
        }
    }

    private static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>
    /// A typed debuff icon size (/rv plates debuffs size, /rv plates self debuffs size) as a percent in range:
    /// "150", "150%", or a factor "1.5" / "1.5x" (up to 3 reads as a factor). False for junk.
    /// </summary>
    internal static bool TryParseDebuffIconSize(string? text, out int percent)
    {
        percent = DefaultDebuffIconSize;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        bool factor = false;
        if (t.EndsWith('%')) t = t[..^1].TrimEnd();
        else if (t.EndsWith('x') || t.EndsWith('X')) { t = t[..^1].TrimEnd(); factor = true; }
        if (!TryF(t, out float v) || !float.IsFinite(v)) return false;
        if (factor || v <= 3f) v *= 100f;
        percent = (int)MathF.Round(Math.Clamp(v, MinDebuffIconSize, MaxDebuffIconSize));
        return true;
    }

    /// <summary>Writes the settings in the background. Any thread.</summary>
    public static void Save() => UiBackgroundWriter.Enqueue("nameplate settings", SaveNow);

    private static void SaveNow()
    {
        lock (Sync)
        {
            try
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var sw = new StreamWriter(path, append: false);
                WriteAll(sw);
            }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>Every setting as the file's key=value lines. Also the tests' entry.</summary>
    internal static void WriteAll(TextWriter sw)
    {
        sw.WriteLine("# RynthCore monster nameplates - auto-generated, hand-edits OK. /rc plates in game.");
        sw.WriteLine($"enabled={B(Enabled)}");
        sw.WriteLine(FormattableString.Invariant($"maxDistance={MaxDistance:0.#}"));
        sw.WriteLine(FormattableString.Invariant($"maxPlates={MaxPlates}"));
        sw.WriteLine($"filter={(Filter == PlateFilter.Engaged ? "engaged" : "all")}");
        sw.WriteLine($"showNames={B(ShowNames)}");
        sw.WriteLine($"showHp={B(ShowHpNumbers)}");
        sw.WriteLine($"showDistance={B(ShowDistance)}");
        sw.WriteLine($"showLevel={B(ShowLevel)}");
        sw.WriteLine($"showWeakness={B(ShowWeakness)}");
        sw.WriteLine($"fade={B(FadeWithDistance)}");
        sw.WriteLine(FormattableString.Invariant($"scale={Scale:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"opacity={Opacity:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"heightLift={HeightLift:0.##}"));
        sw.WriteLine($"clickSelect={B(ClickToSelect)}");
        sw.WriteLine($"npcNames={B(NpcNames)}");
        sw.WriteLine(FormattableString.Invariant($"npcDistance={NpcMaxDistance:0.#}"));
        sw.WriteLine(FormattableString.Invariant($"npcMax={MaxNpcLabels}"));
        sw.WriteLine($"playerNames={B(PlayerNames)}");
        sw.WriteLine($"playerBars={B(PlayerBars)}");
        sw.WriteLine($"portalNames={B(PortalNames)}");
        sw.WriteLine(FormattableString.Invariant($"portalDistance={PortalMaxDistance:0.#}"));
        sw.WriteLine($"showDebuffs={B(ShowDebuffs)}");
        sw.WriteLine($"showOthersDebuff={B(ShowOthersDebuff)}");
        sw.WriteLine($"selfPlate={B(SelfPlate)}");
        sw.WriteLine($"selfNumbers={B(SelfNumbers)}");
        sw.WriteLine($"selfName={B(SelfName)}");
        sw.WriteLine($"selfHideFirstPerson={B(SelfHideFirstPerson)}");
        sw.WriteLine($"selfPlacement={(SelfPlacement == SelfPlacement.Follow ? "follow" : "fixed")}");
        sw.WriteLine($"selfLocked={B(SelfLocked)}");
        sw.WriteLine(FormattableString.Invariant($"selfFixedX={SelfFixedX:0.####}"));
        sw.WriteLine(FormattableString.Invariant($"selfFixedY={SelfFixedY:0.####}"));
        sw.WriteLine($"selfBarTrack={B(SelfBarTrack)}");
        sw.WriteLine($"selfBarChip={B(SelfBarChip)}");
        sw.WriteLine($"selfTextOutline={B(SelfTextOutline)}");
        sw.WriteLine($"selfNumberBand={B(SelfNumberBand)}");
        sw.WriteLine($"selfDebuffs={B(SelfDebuffs)}");
        sw.WriteLine($"numbers={B(Numbers)}");
        sw.WriteLine($"numDealt={B(NumDealt)}");
        sw.WriteLine($"numTaken={B(NumTaken)}");
        sw.WriteLine($"numHeals={B(NumHeals)}");
        sw.WriteLine($"numKills={B(NumKills)}");
        sw.WriteLine($"keepOnScreen={B(KeepOnScreen)}");
        sw.WriteLine($"numRestores={B(NumRestores)}");
        sw.WriteLine(FormattableString.Invariant($"numSize={NumSize:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"numTime={NumTime:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"gainSize={GainSize:0.##}"));
        sw.WriteLine($"selfPosition={SelfPosition}");
        sw.WriteLine(FormattableString.Invariant($"gainTime={GainTime:0.##}"));
        sw.WriteLine($"gains={B(Gains)}");
        sw.WriteLine($"gainXp={B(GainXp)}");
        sw.WriteLine($"gainLum={B(GainLum)}");
        sw.WriteLine($"gainRadiance={B(GainRadiance)}");
        sw.WriteLine(FormattableString.Invariant($"monsterBarWidth={MonsterBarWidth:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"monsterBarHeight={MonsterBarHeight:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"monsterTextSize={MonsterTextSize:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"npcTextSize={NpcTextSize:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"playerTextSize={PlayerTextSize:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"selfBarWidth={SelfBarWidth:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"selfBarHeight={SelfBarHeight:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"selfTextSize={SelfTextSize:0.##}"));
        sw.WriteLine(FormattableString.Invariant($"monsterDebuffIconSize={MonsterDebuffIconSize}"));
        sw.WriteLine(FormattableString.Invariant($"selfDebuffIconSize={SelfDebuffIconSize}"));
    }

    private static string B(bool v) => v ? "1" : "0";

    /// <summary>Back to the defaults (and saved). Render / chat thread.</summary>
    public static void ResetToDefaults()
    {
        Enabled = true; MaxDistance = 35f; MaxPlates = 20; Filter = PlateFilter.All;
        ShowNames = true; ShowHpNumbers = true; ShowDistance = false; ShowLevel = true; ShowWeakness = true;
        FadeWithDistance = true; Scale = 1.0f; Opacity = 0.9f; HeightLift = 0f; ClickToSelect = true;
        ShowDebuffs = true; ShowOthersDebuff = true;
        NpcNames = true; NpcMaxDistance = 30f; MaxNpcLabels = 15; PlayerNames = false; PlayerBars = false;
        PortalNames = true; PortalMaxDistance = DefaultPortalDistance;
        SelfPlate = true; SelfNumbers = false; SelfName = false; SelfHideFirstPerson = true;
        SelfPlacement = SelfPlacement.Fixed; SelfLocked = true; SelfFixedX = DefaultSelfFixedX; SelfFixedY = DefaultSelfFixedY;
        SelfBarTrack = true; SelfBarChip = true; SelfTextOutline = true; SelfNumberBand = true; SelfDebuffs = true;
        Numbers = true; NumDealt = true; NumTaken = true; NumHeals = true; NumKills = true;
        NumSize = 1.0f; NumTime = 1.0f; NumRestores = true; KeepOnScreen = true;
        GainSize = 1.0f; GainTime = 1.0f; SelfPosition = SelfPlatePosition.Above;
        Gains = true; GainXp = true; GainLum = true; GainRadiance = true;
        ResetSizesNoSave();
        Save();
    }

    /// <summary>Every plate type's bar and text size back to 1, the debuff icon sizes to 100 % (and saved). Render / chat thread.</summary>
    public static void ResetSizes()
    {
        ResetSizesNoSave();
        Save();
    }

    private static void ResetSizesNoSave()
    {
        MonsterBarWidth = 1.0f; MonsterBarHeight = 1.0f; MonsterTextSize = 1.0f;
        NpcTextSize = 1.0f; PlayerTextSize = 1.0f;
        SelfBarWidth = 1.0f; SelfBarHeight = 1.0f; SelfTextSize = 1.0f;
        MonsterDebuffIconSize = DefaultDebuffIconSize; SelfDebuffIconSize = DefaultDebuffIconSize;
    }

    /// <summary>The fixed plate back to its default spot (and saved). Render / chat thread.</summary>
    public static void ResetSelfPosition()
    {
        SelfFixedX = DefaultSelfFixedX;
        SelfFixedY = DefaultSelfFixedY;
        Save();
    }

    /// <summary>
    /// Locks or unlocks your plate (not saved: the caller saves). Only a plate fixed on screen can be
    /// dragged, so unlocking one that follows your character fixes it on screen first, at its saved
    /// spot. True when that switch happened. Render / chat thread.
    /// </summary>
    public static bool SetSelfLocked(bool locked)
    {
        SelfLocked = locked;
        if (locked || SelfPlacement == SelfPlacement.Fixed) return false;
        SelfPlacement = SelfPlacement.Fixed;
        return true;
    }

    /// <summary>Your plate can be dragged right now: fixed on screen and unlocked.</summary>
    public static bool SelfMovable => SelfPlacement == SelfPlacement.Fixed && !SelfLocked;

    /// <summary>Every master switch at once ("/rv plates all on|off").</summary>
    public static void SetAll(bool on)
    {
        Enabled = on; ShowDebuffs = on; SelfPlate = on; Numbers = on; Gains = on; NpcNames = on; PortalNames = on;
        if (!on) { PlayerNames = false; PlayerBars = false; }   // "all on" leaves player names and bars as chosen (off by default)
    }

    /// <summary>One line for chat: the current state.</summary>
    public static string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"RynthVision nameplates: monsters {(Enabled ? "ON" : "OFF")} ({(Filter == PlateFilter.Engaged ? "engaged only" : "all monsters")}, " +
        $"{MaxDistance:0}yd, max {MaxPlates}, scale {Scale:0.##}, opacity {Opacity:0.##}, lift {HeightLift:0.#}m; " +
        $"names {OnOff(ShowNames)}, hp {OnOff(ShowHpNumbers)}, level {OnOff(ShowLevel)}, weak {OnOff(ShowWeakness)}, " +
        $"distance {OnOff(ShowDistance)}, fade {OnOff(FadeWithDistance)}, click to select {OnOff(ClickToSelect)}), debuffs {OnOff(ShowDebuffs)} (others {OnOff(ShowOthersDebuff)}); " +
        $"NPC names {OnOff(NpcNames)} ({NpcMaxDistance:0}yd, max {MaxNpcLabels}), player names {OnOff(PlayerNames)} (bars {OnOff(PlayerBars)}), " +
        $"portal names {OnOff(PortalNames)} ({PortalMaxDistance:0}yd); " +
        $"self {OnOff(SelfPlate)} ({(SelfPlacement == SelfPlacement.Fixed ? "fixed on screen, " + (SelfLocked ? "locked" : "unlocked") : "follows you, " + SelfPosition.ToString().ToLowerInvariant())}, " +
        $"numbers {OnOff(SelfNumbers)}, name {OnOff(SelfName)}, hide in first person {OnOff(SelfHideFirstPerson)}, " +
        $"debuffs on you {OnOff(SelfDebuffs)}, track {OnOff(SelfBarTrack)}, damage trail {OnOff(SelfBarChip)}, " +
        $"text outline {OnOff(SelfTextOutline)}, number band {OnOff(SelfNumberBand)}); " +
        $"combat text {OnOff(Numbers)} (dealt {OnOff(NumDealt)}, taken {OnOff(NumTaken)}, heals {OnOff(NumHeals)}, kills {OnOff(NumKills)}); " +
        $"gains {OnOff(Gains)} (xp {OnOff(GainXp)}, lum {OnOff(GainLum)}, radiance {OnOff(GainRadiance)}); " +
        $"sizes: monster bar {MonsterBarWidth:0.##}x{MonsterBarHeight:0.##} text {MonsterTextSize:0.##} debuff icons {MonsterDebuffIconSize}%, " +
        $"NPC and portal text {NpcTextSize:0.##}, player text {PlayerTextSize:0.##}, " +
        $"your bars {SelfBarWidth:0.##}x{SelfBarHeight:0.##} text {SelfTextSize:0.##} debuff icons {SelfDebuffIconSize}%.");

    private static string OnOff(bool v) => v ? "on" : "off";
}

/// <summary>Player plate placement: over the head, under the feet, or beside the character.</summary>
internal enum SelfPlatePosition { Above, Below, Left, Right }

/// <summary>Player plate: fixed at a saved screen spot (draggable when unlocked), or following the character.</summary>
internal enum SelfPlacement { Fixed, Follow }
