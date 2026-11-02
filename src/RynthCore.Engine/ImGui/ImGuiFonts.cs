// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiFonts.cs
//  The ImGui font atlas (docs/IMGUI_PARITY_PLAN.md §4.4).
//
//  ImGui 1.91 has no dynamic font sizes, so every size a panel face uses is
//  baked once, at the DPI scale the context is created with:
//    Segoe UI 9/10/11/14 and Bold 11/14 (panel text, headers, radar compass),
//    Consolas 9/10/12 (Tracker, Log, Meta rows), Cascadia Mono 12 (Meta
//    source; Consolas if missing), and the chat monospace at 8-18 in steps
//    of 2 (the chat font slider snaps to the nearest).
//  The few symbol glyphs still drawn as text (SymbolCodepoints) come from the
//  main font itself (Segoe UI, Consolas and Cascadia Mono all have them); Segoe UI
//  Symbol is merged only into a font whose file lacks one (HasGlyphs). ImGui copies
//  the whole TTF into the atlas for every font it adds, and Segoe UI Symbol is
//  2.4 MB: merged into every size (~107 with the panel text sizes) it made ~250 MB
//  of the atlas's input data for three glyphs (2026-10-15). DX9Backend frees the
//  input data once the texture is uploaded (ClearInputData).
//
//  Narrow labels (2026-10-21): Barlow Semi Condensed SemiBold and Bold (OFL,
//  Assets/Fonts, embedded like Phosphor) at 9/10/11 and bold 11/13, for plugin
//  windows (GetUiFont Narrow*: the instrument dashboard's caps labels). They
//  get the icons but never Segoe UI Symbol (nothing draws those three with them).
//
//  Icons: the Phosphor icon font (MIT, Assets/Fonts/Phosphor.ttf, embedded in
//  this assembly) is merged into every baked font, the scaled sets included,
//  at the web font's code points (U+E000 up), so an icon is text
//  (PhosphorIcons). Only PhosphorIcons.Baked is merged, not the whole set.
//  Each font gets the icons at about 1.2x its em (Phosphor's em is its pixel
//  size; Segoe UI's is 0.75 of it), shifted down so the icon's centre sits on
//  the text's cap-height centre, on a fixed advance so icon columns line up.
//
//  Font files are read by Preload() on the engine's init worker, so AC's
//  render thread only copies bytes into the atlas. The native TTF buffers and
//  glyph ranges stay alive until Free() (after the context is destroyed).
//
//  The first atlas is baked off AC's thread too (2026-11-01, StartInitialBake): on a
//  hot reload with every client reloading at once, queueing the fonts, rasterizing
//  the atlas, the RGBA->BGRA copy and freeing the ~150 MB of TTF copies took 2.5-5 s
//  of AC's main thread in the first ImGui frame (the "[UiCost] frame 3611 ms" lines,
//  the main-thread hangs of every reload). The worker now does all of it; AC's thread
//  swaps the finished atlas in and copies ready BGRA rows into the texture.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal enum UiFont
{
    Ui10, Ui11, Ui14,
    UiBold11, UiBold14,
    Mono9, Mono10, Mono12,
    Code12,
    Chat8, Chat10, Chat12, Chat14, Chat16, Chat18,
    Ui9,
    // RynthAi dashboard: Cascadia Mono (Consolas if missing), like the Avalonia face.
    Dash9, Dash10, Dash11, Dash14,
    // RynthVision combat numbers: baked big so they stay sharp.
    NumBold24,
    // Scaled-up nameplate text and big combat numbers: picked by Sharp() so text is
    // shrunk from a bigger bake, never stretched from a smaller one.
    Ui18, NumBold48,
    // Narrow labels for plugin windows (Barlow Semi Condensed SemiBold, then Bold).
    Narrow9, Narrow10, Narrow11, NarrowBold11, NarrowBold13,
    Count,
}

internal static unsafe class ImGuiFonts
{
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // Main text: Basic Latin + Latin-1, general punctuation (— – … • ‘ ’ “ ”), arrows.
    // Plus the SymbolCodepoints (≤ ≥ ●), which the main fonts have.
    private static readonly ushort[] MainRanges = { 0x0020, 0x00FF, 0x2010, 0x2027, 0x2190, 0x21FF, 0x2264, 0x2265, 0x25CF, 0x25CF, 0 };

    // Symbols still drawn as text, merged from Segoe UI Symbol where the main font
    // lacks them. The panels' other symbols (⚔ ✦ ➤ ◫ ♥ ◆ ⚙ ▾ ▴ ↗ ↙ ◢ ✕ ☑ ☐ ▶ ▼ ▲ ◀ ■
    // ❏ ↺ ✎ − ⬡ ◎ ⚒ ...) are Phosphor icons now (PhosphorIcons). Arrows, bullets and
    // dashes are in MainRanges.
    private static readonly ushort[] SymbolCodepoints =
    {
        0x2264, 0x2265,           // ≤ ≥ (Meta rule list: "Prismatic Taper ≤ 50")
        0x25CF,                   // ● (Meta source: unapplied edits)
    };

    private static byte[]? _segoe, _segoeBold, _consolas, _cascadia, _symbol, _phosphor, _narrow, _narrowBold;

    private const string PhosphorResource = "RynthCore.Engine.Assets.Fonts.Phosphor.ttf";
    private const string NarrowResource = "RynthCore.Engine.Assets.Fonts.BarlowSemiCondensed-SemiBold.ttf";
    private const string NarrowBoldResource = "RynthCore.Engine.Assets.Fonts.BarlowSemiCondensed-Bold.ttf";
    // Icon size relative to the text's em, capped at the line height (x0.95) for
    // fonts whose em fills the line (Consolas): bigger icons climb out of the line.
    private const float IconScale = 1.2f, IconMaxOfLine = 0.95f;
    // Where a Phosphor icon's box centre sits above the baseline, in its em
    // (the 1024-unit box runs from -64 to 960).
    private const float IconCentreEm = 448f / 1024f;
    // Phosphor's regular strokes are 1/16 em: at 9-13 px they rasterize thin and grey.
    private const float IconRasterizerMultiply = 1.2f;
    private static volatile bool _preloaded;
    // Fonts the last AddAll merged Segoe UI Symbol into (their file lacked a symbol glyph).
    private static int _symbolMerges;
    private static readonly List<IntPtr> NativeBuffers = new();
    private static readonly ImFontPtr[] Fonts = new ImFontPtr[(int)UiFont.Count];

    // Per-panel text size (UI.PanelTextScale): the same fonts baked bigger, one
    // set per size in use. Get() returns the set for CurrentStep, which the panel
    // host sets while it draws a panel, so every face's text grows without any
    // face code changing. Baked (not stretched), so the big text stays sharp.
    // The sizes self-drawn windows are drawn at (UI.PanelDrawScale) are baked too:
    // a window at 160% draws its text from the 160% set (StepsToBake).
    private static readonly Dictionary<int, ImFontPtr[]> ScaledFonts = new();

    /// <summary>Text size step of the panel being drawn (0 = normal). Render thread.</summary>
    public static int CurrentStep;

    // Stopwatch timestamp the requested rebuild is due at; 0 = none requested.
    private static long _rebuildDue;

    /// <summary>
    /// A panel's text size changed: rebuild the font atlas before a frame at least
    /// <paramref name="delayMs"/> from now. Each request moves the time out, so a run
    /// of changes (clicks, wheel notches) re-bakes once, after the last. Any thread.
    /// </summary>
    public static void RequestRebuild(int delayMs = 0)
    {
        long due = Stopwatch.GetTimestamp() + Math.Max(0, delayMs) * Stopwatch.Frequency / 1000;
        if (due == 0) due = 1;
        System.Threading.Volatile.Write(ref _rebuildDue, due);
    }

    /// <summary>True once per request, when it is due. Render thread, before NewFrame.</summary>
    public static bool TakeRebuildRequest()
    {
        long due = System.Threading.Volatile.Read(ref _rebuildDue);
        if (due == 0 || Stopwatch.GetTimestamp() < due) return false;
        // A newer request made meanwhile keeps its own (later) time.
        return System.Threading.Interlocked.CompareExchange(ref _rebuildDue, 0, due) == due;
    }

    /// <summary>
    /// The baked size step closest to <paramref name="step"/> (0 = normal): the step
    /// itself once baked. Between a size change and the re-bake it lands on, a panel
    /// keeps drawing at a nearby baked size instead of dropping to normal. Render thread.
    /// </summary>
    public static int BakedStepNear(int step)
    {
        if (step <= 0 || ScaledFonts.ContainsKey(step)) return Math.Max(0, step);
        int best = 0;
        foreach (int baked in ScaledFonts.Keys)
            if (Math.Abs(baked - step) < Math.Abs(best - step)) best = baked;
        return best;
    }

    // Native copies of the font files and glyph ranges, made once: rebuilds reuse them.
    private static IntPtr _nSegoe, _nSegoeBold, _nMono, _nCode, _nSymbol, _nPhosphor, _nNarrow, _nNarrowBold;
    private static ushort* _mainRanges, _symbolRanges, _iconRanges;

    /// <summary>Reads the font files. Engine init worker thread; safe to skip (Build reads them itself).</summary>
    public static void Preload()
    {
        if (_preloaded) return;
        _segoe = TryRead("segoeui.ttf");
        _segoeBold = TryRead("segoeuib.ttf");
        _consolas = TryRead("consola.ttf");
        _cascadia = TryRead("CascadiaMono.ttf");
        _symbol = TryRead("seguisym.ttf");
        _phosphor = TryReadResource(PhosphorResource, "icons will not draw");
        _narrow = TryReadResource(NarrowResource, "plugin windows' narrow labels use their fallback");
        _narrowBold = TryReadResource(NarrowBoldResource, "plugin windows' narrow labels use their fallback");
        _preloaded = true;
    }

    /// <summary>The text size steps any panel uses, and those the self-drawn windows' sizes need.</summary>
    private static List<int> StepsToBake(float scale)
    {
        List<int> steps = RynthCore.Engine.UI.PanelTextScale.StepsInUse();
        foreach (int step in RynthCore.Engine.UI.PanelDrawScale.FontStepsInUse(scale))
            if (!steps.Contains(step)) steps.Add(step);
        return steps;
    }

    /// <summary>A baked font. Before Build (or if it failed) this is ImGui's default font.</summary>
    public static ImFontPtr Get(UiFont font)
    {
        if (CurrentStep > 0 && ScaledFonts.TryGetValue(CurrentStep, out ImFontPtr[]? set))
        {
            ImFontPtr s = set[(int)font];
            if (s.NativePtr != null) return s;
        }
        ImFontPtr f = Fonts[(int)font];
        return f.NativePtr != null ? f : ImGuiNET.ImGui.GetIO().FontDefault;
    }

    private static readonly UiFont[] SharpRegular = { UiFont.Ui9, UiFont.Ui10, UiFont.Ui11, UiFont.Ui14, UiFont.Ui18 };
    private static readonly UiFont[] SharpBold = { UiFont.UiBold11, UiFont.UiBold14, UiFont.NumBold24, UiFont.NumBold48 };

    /// <summary>
    /// The smallest baked font at least <paramref name="px"/> tall (else the biggest), for text
    /// drawn at a scaled size: shrinking a bigger bake stays sharp, stretching a smaller one blurs.
    /// Draw with the returned font at <paramref name="px"/>.
    /// </summary>
    public static ImFontPtr Sharp(float px, bool bold)
    {
        UiFont[] set = bold ? SharpBold : SharpRegular;
        ImFontPtr best = Get(set[0]);
        for (int i = 0; i < set.Length; i++)
        {
            ImFontPtr f = Fonts[(int)set[i]];
            if (f.NativePtr == null) continue;
            best = f;
            if (f.FontSize >= px - 0.5f) break;
        }
        return best;
    }

    /// <summary>The baked chat font nearest to <paramref name="points"/> (8-18).</summary>
    public static ImFontPtr Chat(float points)
    {
        int step = Math.Clamp((int)MathF.Round((points - 8f) / 2f), 0, 5);
        return Get(UiFont.Chat8 + step);
    }

    /// <summary>
    /// Adds every font to the current context's atlas at <paramref name="scale"/>
    /// (DPI / 96). AC thread, before the atlas texture is first built.
    /// </summary>
    public static void Build(ImGuiIOPtr io, float scale)
    {
        long t0 = Stopwatch.GetTimestamp();
        PrepareNativeData();
        Compatibility.MemoryUse.Mark("font files read");

        ScaledFonts.Clear();
        AddAll(io.Fonts, scale, StepsToBake(scale), Fonts, ScaledFonts);

        if (Fonts[(int)UiFont.Ui11].NativePtr != null)
            io.NativePtr->FontDefault = Fonts[(int)UiFont.Ui11].NativePtr;
        else
            io.Fonts.AddFontDefault(); // no Segoe UI: ImGui's built-in font keeps text readable

        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        RynthLog.UI($"ImGuiFonts: {CountBaked()} fonts queued at scale {scale:0.##} in {ms:0.0} ms " +
                        $"(Cascadia Mono {(_cascadia != null ? "found" : "missing, using Consolas")}, " +
                        $"Barlow {(_narrow != null && _narrowBold != null ? "embedded" : "missing")}, " +
                        $"symbols from the main fonts{(_symbolMerges > 0 ? $" (Segoe UI Symbol merged into {_symbolMerges})" : "")}, " +
                        $"icons {(_phosphor != null ? $"{IconCount()} merged" : "missing")}).");
        Compatibility.MemoryUse.Mark("ImGui fonts queued");
    }

    /// <summary>The font files and glyph ranges as native memory, made once (rebuilds reuse them). AC thread.</summary>
    private static void PrepareNativeData()
    {
        if (!_preloaded) Preload();
        if (_mainRanges != null) return;
        byte[]? mono = _consolas;
        byte[]? code = _cascadia ?? _consolas;
        _mainRanges = PinRanges(MainRanges);
        _symbolRanges = PinRanges(SymbolRanges());
        _iconRanges = PinRanges(IconRanges());
        _nSegoe = ToNative(_segoe); _nSegoeBold = ToNative(_segoeBold); _nMono = ToNative(mono);
        _nCode = ToNative(code); _nSymbol = ToNative(_symbol); _nPhosphor = ToNative(_phosphor);
        _nNarrow = ToNative(_narrow); _nNarrowBold = ToNative(_narrowBold);
    }

    /// <summary>
    /// Queues every font into <paramref name="atlas"/>: the normal set into
    /// <paramref name="baseSet"/>, a bigger copy per text size step into
    /// <paramref name="scaled"/>. Touches no ImGui context and no static font
    /// table, so the off-thread re-bake runs it too (after PrepareNativeData).
    /// </summary>
    private static void AddAll(ImFontAtlasPtr atlas, float scale, List<int> steps, ImFontPtr[] baseSet,
        Dictionary<int, ImFontPtr[]> scaled)
    {
        byte[]? mono = _consolas;
        byte[]? code = _cascadia ?? _consolas;
        ushort* mainRanges = _mainRanges;
        ushort* symbolRanges = _symbolRanges;
        ushort* iconRanges = _iconRanges;
        IntPtr segoe = _nSegoe, segoeBold = _nSegoeBold, consolas = _nMono, codeFont = _nCode, symbol = _nSymbol,
            phosphor = _nPhosphor, narrow = _nNarrow, narrowBold = _nNarrowBold;

        // Each base font's em and cap height, to size and place the icons merged into it.
        FaceMetrics mSegoe = FaceMetrics.Read(_segoe), mSegoeBold = FaceMetrics.Read(_segoeBold),
            mMono = FaceMetrics.Read(mono), mCode = FaceMetrics.Read(code),
            mNarrow = FaceMetrics.Read(_narrow), mNarrowBold = FaceMetrics.Read(_narrowBold);

        ImFontPtr[] target = baseSet;
        float factor = 1f;

        // Whether each main font file lacks a symbol glyph (only then is Segoe UI Symbol merged in).
        _symbolMerges = 0;
        var needsSymbol = new Dictionary<byte[], bool>(ReferenceEqualityComparer.Instance);
        bool NeedsSymbol(byte[] b)
        {
            if (!needsSymbol.TryGetValue(b, out bool need))
                needsSymbol[b] = need = !HasGlyphs(b, SymbolCodepoints);
            return need;
        }

        // symbols: false for the narrow label faces (no ≤ ≥ ● drawn with them; Segoe UI Symbol is 2.4 MB per merge).
        void Add(UiFont id, IntPtr data, byte[]? bytes, FaceMetrics metrics, float px, bool symbols = true)
        {
            if (data == IntPtr.Zero || bytes == null) return;
            float size = MathF.Round(px * scale * factor);
            ImFontConfigPtr cfg = ImGuiNative.ImFontConfig_ImFontConfig();
            try
            {
                cfg.FontDataOwnedByAtlas = false;
                target[(int)id] = atlas.AddFontFromMemoryTTF(data, bytes.Length, size, cfg, (IntPtr)mainRanges);
                if (symbols && symbol != IntPtr.Zero && _symbol != null && NeedsSymbol(bytes))
                {
                    _symbolMerges++;
                    cfg.MergeMode = true;
                    cfg.GlyphMinAdvanceX = 0;
                    atlas.AddFontFromMemoryTTF(symbol, _symbol.Length, size, cfg, (IntPtr)symbolRanges);
                }
                if (phosphor != IntPtr.Zero && _phosphor != null)
                {
                    // Phosphor's em is its pixel size (ascent + descent = 1 em).
                    float em = size * metrics.EmPerPx;
                    float iconPx = MathF.Max(1f, MathF.Round(MathF.Min(em * IconScale, size * IconMaxOfLine)));
                    cfg.MergeMode = true;
                    // Whole-pixel advances, so horizontal oversampling buys nothing: 1 halves the icons' atlas space.
                    cfg.PixelSnapH = true;
                    cfg.OversampleH = 1;
                    cfg.GlyphMinAdvanceX = iconPx;
                    // Down (+y) from the shared baseline: the icon's centre onto the cap-height centre.
                    cfg.GlyphOffset = new Vector2(0, MathF.Round(IconCentreEm * iconPx - metrics.CapHalfEm * em));
                    cfg.RasterizerMultiply = IconRasterizerMultiply;
                    atlas.AddFontFromMemoryTTF(phosphor, _phosphor.Length, iconPx, cfg, (IntPtr)iconRanges);
                }
            }
            finally
            {
                ImGuiNative.ImFontConfig_destroy(cfg.NativePtr);
            }
        }

        Add(UiFont.Ui11, segoe, _segoe, mSegoe, 11); // first added = io.FontDefault unless set below
        Add(UiFont.Ui10, segoe, _segoe, mSegoe, 10);
        Add(UiFont.Ui14, segoe, _segoe, mSegoe, 14);
        Add(UiFont.Ui9, segoe, _segoe, mSegoe, 9);
        Add(UiFont.UiBold11, segoeBold, _segoeBold, mSegoeBold, 11);
        Add(UiFont.UiBold14, segoeBold, _segoeBold, mSegoeBold, 14);
        Add(UiFont.Mono9, consolas, mono, mMono, 9);
        Add(UiFont.Mono10, consolas, mono, mMono, 10);
        Add(UiFont.Mono12, consolas, mono, mMono, 12);
        Add(UiFont.Code12, codeFont, code, mCode, 12);
        Add(UiFont.Dash9, codeFont, code, mCode, 9);
        Add(UiFont.Dash10, codeFont, code, mCode, 10);
        Add(UiFont.Dash11, codeFont, code, mCode, 11);
        Add(UiFont.Dash14, codeFont, code, mCode, 14);
        Add(UiFont.NumBold24, segoeBold, _segoeBold, mSegoeBold, 24);
        Add(UiFont.Ui18, segoe, _segoe, mSegoe, 18);
        Add(UiFont.NumBold48, segoeBold, _segoeBold, mSegoeBold, 48);
        for (int i = 0; i < 6; i++)
            Add(UiFont.Chat8 + i, consolas, mono, mMono, 8 + i * 2);
        AddNarrow();

        // The narrow label faces (plugin windows), in the base set and in each scaled one.
        void AddNarrow()
        {
            Add(UiFont.Narrow9, narrow, _narrow, mNarrow, 9, symbols: false);
            Add(UiFont.Narrow10, narrow, _narrow, mNarrow, 10, symbols: false);
            Add(UiFont.Narrow11, narrow, _narrow, mNarrow, 11, symbols: false);
            Add(UiFont.NarrowBold11, narrowBold, _narrowBold, mNarrowBold, 11, symbols: false);
            Add(UiFont.NarrowBold13, narrowBold, _narrowBold, mNarrowBold, 13, symbols: false);
        }

        // Bigger copies for each panel text size in use (chat keeps its own size setting).
        foreach (int step in steps)
        {
            target = new ImFontPtr[(int)UiFont.Count];
            factor = RynthCore.Engine.UI.PanelTextScale.Factor(step);
            Add(UiFont.Ui11, segoe, _segoe, mSegoe, 11);
            Add(UiFont.Ui10, segoe, _segoe, mSegoe, 10);
            Add(UiFont.Ui14, segoe, _segoe, mSegoe, 14);
            Add(UiFont.Ui9, segoe, _segoe, mSegoe, 9);
            Add(UiFont.UiBold11, segoeBold, _segoeBold, mSegoeBold, 11);
            Add(UiFont.UiBold14, segoeBold, _segoeBold, mSegoeBold, 14);
            Add(UiFont.Mono9, consolas, mono, mMono, 9);
            Add(UiFont.Mono10, consolas, mono, mMono, 10);
            Add(UiFont.Mono12, consolas, mono, mMono, 12);
            Add(UiFont.Code12, codeFont, code, mCode, 12);
            Add(UiFont.Dash9, codeFont, code, mCode, 9);
            Add(UiFont.Dash10, codeFont, code, mCode, 10);
            Add(UiFont.Dash11, codeFont, code, mCode, 11);
            Add(UiFont.Dash14, codeFont, code, mCode, 14);
            // Plugin windows draw their icons from Ui18's (GetUiFont Ui18: about 16 px icons at
            // normal size), so it grows with the panel's text size too, and stays sharp.
            Add(UiFont.Ui18, segoe, _segoe, mSegoe, 18);
            AddNarrow();
            scaled[step] = target;
        }
    }

    // ── Re-bake off AC's thread ─────────────────────────────────────────
    // A text size change needs a new atlas. Rasterizing it took 180-300 ms with one
    // or two enlarged sizes (about 1 s with all ten), which froze the game for that
    // long when it ran on the render thread. Instead a worker queues and builds a
    // NEW atlas (no ImGui context involved) while frames go on with the old one;
    // then, between frames, the main context and every pop-out are pointed at the
    // new atlas, its texture is made, and the old atlas is destroyed on another
    // worker. AC's thread only pays for the swap and the texture upload.
    //
    // ImGui's allocator hook writes to the current context's debug counters from
    // any thread, so no context may be destroyed while a worker allocates or frees:
    // WaitForWorkers() runs first (ImGuiPopOuts.Close, EngineFrameController.Shutdown).

    private sealed class Bake
    {
        public IntPtr Atlas;              // ImFontAtlas*, in no context yet; 0 = the bake failed
        public readonly ImFontPtr[] Base = new ImFontPtr[(int)UiFont.Count];
        public readonly Dictionary<int, ImFontPtr[]> Scaled = new();
        public int Steps;
        public double Ms;
        public string? Error;
        // The atlas pixels as D3D9 A8R8G8B8 rows (BGRA in memory), made by the worker
        // (NativeMemory); handed on by SwapIn. 0 when the bake failed.
        public IntPtr Bgra;
        public int Width, Height;
    }

    // The swapped-in atlas's BGRA copy, until DX9Backend uploads it (TakeBgraPixels). Render thread.
    private static IntPtr _bgra;
    private static int _bgraWidth, _bgraHeight;

    private static System.Threading.Thread? _bakeThread;
    private static Bake? _bakeDone;                       // set by the worker as it ends
    private static float _bakeScale;                      // the scale _bakeThread bakes for
    private static readonly List<System.Threading.Thread> FreeThreads = new();

    /// <summary>
    /// Render thread, before NewFrame, with the main context current. Starts a re-bake
    /// when one is due, and swaps a finished one in. True when <paramref name="io"/>'s
    /// atlas changed: the caller makes its texture. <paramref name="what"/> says what happened.
    /// </summary>
    public static bool PumpRebuild(ImGuiIOPtr io, float scale, out string what)
    {
        what = "";
        if (_bakeThread != null)
            return SwapFinished(io, scale, out what);   // false while baking: frames go on with the old atlas

        if (!TakeRebuildRequest()) return false;
        PrepareNativeData();
        List<int> steps = StepsToBake(scale);
        try
        {
            _bakeDone = null;
            _bakeScale = scale;
            _bakeThread = EngineThreads.Start("ImGuiFonts.Bake", () => BakeWorker(scale, steps, prepare: false));
            return false;
        }
        catch (Exception ex)
        {
            _bakeThread = null;
            io.Fonts.Clear();
            Build(io, scale);
            what = $"no bake thread ({ex.GetType().Name}); re-baked on the render thread";
            return true;
        }
    }

    /// <summary>
    /// The first atlas of a context, baked on a worker (see the header): reading the font files if
    /// the init worker hasn't, making their native copies, queueing every font, rasterizing, the
    /// BGRA copy and freeing the TTF copies all happen there. AC thread, once the scale is known,
    /// or the init worker before login (EngineFrameController.StartEarlyFontBake; never both at
    /// once: the EndScene hook goes in after that call). False when no worker could be started
    /// (the caller then calls <see cref="Build"/>).
    /// </summary>
    public static bool StartInitialBake(float scale)
    {
        if (_bakeThread != null)
        {
            // Started early, on the init worker before login (EngineFrameController.StartEarlyFontBake),
            // for the scale the game window had then: kept when the context gets that scale too.
            if (_bakeScale == scale) return true;
            RynthLog.UI($"ImGuiFonts: the atlas baked before login is for scale {_bakeScale:0.##}, the context's is {scale:0.##}: baking again.");
            DropBake();
        }
        List<int> steps = RynthCore.Engine.UI.PanelTextScale.StepsInUse();
        try
        {
            _bakeDone = null;
            _bakeScale = scale;
            _bakeThread = EngineThreads.Start("ImGuiFonts.Bake", () => BakeWorker(scale, steps, prepare: true));
            return true;
        }
        catch (Exception ex)
        {
            _bakeThread = null;
            RynthLog.UI($"ImGuiFonts: no bake thread for the first atlas ({ex.GetType().Name}: {ex.Message}); baking it on the render thread.");
            return false;
        }
    }

    /// <summary>Waits for the running bake and frees what it made, never swapped in.</summary>
    private static void DropBake()
    {
        _bakeThread?.Join();
        _bakeThread = null;
        Bake? pending = _bakeDone;
        _bakeDone = null;
        if (pending == null) return;
        if (pending.Atlas != IntPtr.Zero) ImGuiNative.ImFontAtlas_destroy((ImFontAtlas*)pending.Atlas);
        FreeBgraPixels(pending.Bgra);
    }

    /// <summary>
    /// Render thread, with <paramref name="io"/>'s context current: swaps a finished bake (the
    /// first atlas, or a re-bake) into it. True when the atlas changed (the caller makes its
    /// texture); false while none is running or it is still baking.
    /// </summary>
    public static bool SwapFinished(ImGuiIOPtr io, float scale, out string what)
    {
        what = "";
        if (_bakeThread == null) return false;
        Bake? done = System.Threading.Volatile.Read(ref _bakeDone);
        if (done == null) return false;
        _bakeThread.Join();
        _bakeThread = null;
        _bakeDone = null;
        if (done.Atlas == IntPtr.Zero)
        {
            // Failed off-thread: the old way, here.
            io.Fonts.Clear();
            Build(io, scale);
            what = $"off-thread bake failed ({done.Error}); baked on the render thread";
            return true;
        }
        SwapIn(io, done);
        what = $"baked off-thread in {done.Ms:0.0} ms: {CountBaked()} fonts at scale {scale:0.##}, {done.Steps} enlarged size(s) in use, "
            + $"icons {(_phosphor != null ? $"{IconCount()} merged" : "missing")}"
            + (_symbolMerges > 0 ? $", Segoe UI Symbol merged into {_symbolMerges}" : "");
        return true;
    }

    /// <summary>
    /// The swapped-in atlas's pixels as A8R8G8B8 rows, made by the bake worker, when they match
    /// <paramref name="width"/> x <paramref name="height"/>: the caller copies them into the texture
    /// and frees them with <see cref="FreeBgraPixels"/>. False (nothing to free) otherwise: the
    /// caller converts ImGui's RGBA itself. Either way the copy is no longer held here. Render thread.
    /// </summary>
    public static bool TakeBgraPixels(int width, int height, out IntPtr bgra)
    {
        bgra = _bgra;
        bool match = bgra != IntPtr.Zero && _bgraWidth == width && _bgraHeight == height;
        if (bgra != IntPtr.Zero && !match) FreeBgraPixels(bgra);
        _bgra = IntPtr.Zero;
        _bgraWidth = _bgraHeight = 0;
        if (!match) bgra = IntPtr.Zero;
        return match;
    }

    public static void FreeBgraPixels(IntPtr bgra)
    {
        if (bgra != IntPtr.Zero) NativeMemory.Free((void*)bgra);
    }

    private static void BakeWorker(float scale, List<int> steps, bool prepare)
    {
        var bake = new Bake { Steps = steps.Count };
        long t0 = Stopwatch.GetTimestamp();
        ImFontAtlas* atlas = null;
        try
        {
            // The first bake makes the native copies of the font files here; a re-bake finds
            // them made (PumpRebuild prepares them first). One bake runs at a time, and nothing
            // else touches them until it is joined (SwapFinished, WaitForWorkers).
            if (prepare)
            {
                PrepareNativeData();
                Compatibility.MemoryUse.Mark("font files read (bake worker)");
            }
            atlas = ImGuiNative.ImFontAtlas_ImFontAtlas();
            var ptr = new ImFontAtlasPtr(atlas);
            AddAll(ptr, scale, steps, bake.Base, bake.Scaled);
            if (bake.Base[(int)UiFont.Ui11].NativePtr == null)
                ptr.AddFontDefault(); // no Segoe UI: ImGui's built-in font keeps text readable
            ptr.Build();
            // The RGBA copy the texture is made from, and its BGRA rows for D3D9, here too:
            // the swap then only copies rows into the texture.
            ptr.GetTexDataAsRGBA32(out IntPtr rgba, out int w, out int h);
            if (rgba != IntPtr.Zero && w > 0 && h > 0)
            {
                bake.Bgra = (IntPtr)NativeMemory.Alloc((nuint)w * (nuint)h, 4);
                FontPixels.RgbaToBgra(new ReadOnlySpan<uint>((void*)rgba, w * h), new Span<uint>((void*)bake.Bgra, w * h));
                bake.Width = w;
                bake.Height = h;
            }
            // The glyphs are rasterized: the atlas's copies of the font files (~150 MB with
            // every size) are never read again (DX9Backend's note), so they go here, not on
            // AC's thread. The pixels and TexReady stay.
            ptr.ClearInputData();
            if (prepare) Compatibility.MemoryUse.Mark("font atlas baked (bake worker)");
            bake.Atlas = (IntPtr)atlas;
            atlas = null;
        }
        catch (Exception ex)
        {
            bake.Error = $"{ex.GetType().Name}: {ex.Message}";
            FreeBgraPixels(bake.Bgra);
            bake.Bgra = IntPtr.Zero;
        }
        finally
        {
            if (atlas != null) ImGuiNative.ImFontAtlas_destroy(atlas);
            bake.Ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            System.Threading.Volatile.Write(ref _bakeDone, bake);
        }
    }

    /// <summary>Points the main context and every pop-out at the baked atlas; the old one is freed off-thread.</summary>
    private static void SwapIn(ImGuiIOPtr io, Bake bake)
    {
        ImFontAtlas* old = io.NativePtr->Fonts;
        ImFontAtlas* fresh = (ImFontAtlas*)bake.Atlas;
        io.NativePtr->Fonts = fresh;   // the main context owns (and at shutdown deletes) whatever atlas it holds
        io.NativePtr->FontDefault = bake.Base[(int)UiFont.Ui11].NativePtr;   // null: the atlas's first font
        Array.Copy(bake.Base, Fonts, Fonts.Length);
        ScaledFonts.Clear();
        foreach (KeyValuePair<int, ImFontPtr[]> kv in bake.Scaled) ScaledFonts[kv.Key] = kv.Value;
        FreeBgraPixels(_bgra);   // a copy never uploaded (each swap is uploaded straight after)
        _bgra = bake.Bgra;
        _bgraWidth = bake.Width;
        _bgraHeight = bake.Height;
        ImGuiPopOuts.ReplaceFontAtlas((IntPtr)old, (IntPtr)fresh);
        if (old == null || old == fresh) return;
        IntPtr oldPtr = (IntPtr)old;
        try
        {
            lock (FreeThreads)
            {
                FreeThreads.RemoveAll(t => !t.IsAlive);
                FreeThreads.Add(EngineThreads.Start("ImGuiFonts.Free",
                    () => ImGuiNative.ImFontAtlas_destroy((ImFontAtlas*)oldPtr)));
            }
        }
        catch
        {
            ImGuiNative.ImFontAtlas_destroy(old);   // no thread: free it here
        }
    }

    /// <summary>
    /// Waits for a running re-bake and atlas frees. Before destroying any ImGui
    /// context (see the note above). AC thread. A finished bake stays to be swapped in.
    /// </summary>
    public static void WaitForWorkers()
    {
        _bakeThread?.Join();
        System.Threading.Thread[] frees;
        lock (FreeThreads) { frees = FreeThreads.ToArray(); FreeThreads.Clear(); }
        foreach (System.Threading.Thread t in frees) t.Join();
    }

    /// <summary>Frees the native font buffers. Only after the ImGui context is destroyed.</summary>
    public static void Free()
    {
        // A worker may still read the buffers below (a bake) or free an atlas.
        WaitForWorkers();
        DropBake();   // baked, never swapped in
        FreeBgraPixels(_bgra);
        _bgra = IntPtr.Zero;
        _bgraWidth = _bgraHeight = 0;
        foreach (IntPtr p in NativeBuffers)
            NativeMemory.Free((void*)p);
        NativeBuffers.Clear();
        Array.Clear(Fonts);
        ScaledFonts.Clear();
        _mainRanges = _symbolRanges = _iconRanges = null;
        _nSegoe = _nSegoeBold = _nMono = _nCode = _nSymbol = _nPhosphor = _nNarrow = _nNarrowBold = IntPtr.Zero;
    }

    private static int CountBaked()
    {
        int n = 0;
        foreach (ImFontPtr f in Fonts) if (f.NativePtr != null) n++;
        return n;
    }

    private static ushort[] SymbolRanges()
    {
        var ranges = new ushort[SymbolCodepoints.Length * 2 + 1];
        for (int i = 0; i < SymbolCodepoints.Length; i++)
        {
            ranges[i * 2] = SymbolCodepoints[i];
            ranges[i * 2 + 1] = SymbolCodepoints[i];
        }
        return ranges; // trailing 0 terminator
    }

    /// <summary>PhosphorIcons.Baked as glyph ranges: sorted, runs of neighbours joined, 0-terminated.</summary>
    private static ushort[] IconRanges()
    {
        var points = new SortedSet<ushort>();
        foreach (char c in PhosphorIcons.Baked) points.Add(c);
        var ranges = new List<ushort>();
        int start = -1, prev = -1;
        foreach (ushort cp in points)
        {
            if (start < 0) start = cp;
            else if (cp != prev + 1) { ranges.Add((ushort)start); ranges.Add((ushort)prev); start = cp; }
            prev = cp;
        }
        if (start >= 0) { ranges.Add((ushort)start); ranges.Add((ushort)prev); }
        ranges.Add(0);
        return ranges.ToArray();
    }

    private static int IconCount() => new HashSet<char>(PhosphorIcons.Baked).Count;

    /// <summary>
    /// True when the TrueType font has a glyph for every code point (its cmap, format 4:
    /// the Basic Multilingual Plane). False when unreadable, so the caller falls back to
    /// merging. Pure: tools\PanelTextScaleTests can drive it.
    /// </summary>
    internal static bool HasGlyphs(byte[]? ttf, ushort[] codepoints)
    {
        if (ttf == null || ttf.Length < 12) return false;
        try
        {
            static int U16(byte[] b, int at) => b[at] << 8 | b[at + 1];
            static int U32(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
            int cmap = -1;
            int tables = U16(ttf, 4);
            for (int i = 0; i < tables; i++)
            {
                int rec = 12 + i * 16;
                if (U32(ttf, rec) == 0x636D6170) { cmap = U32(ttf, rec + 8); break; }   // "cmap"
            }
            if (cmap < 0) return false;
            int subtables = U16(ttf, cmap + 2);
            for (int s = 0; s < subtables; s++)
            {
                int sub = cmap + U32(ttf, cmap + 4 + s * 8 + 4);
                if (U16(ttf, sub) != 4) continue;
                int segX2 = U16(ttf, sub + 6), ends = sub + 14, starts = ends + segX2 + 2;
                int deltas = starts + segX2, offsets = deltas + segX2;
                bool all = true;
                foreach (ushort cp in codepoints)
                {
                    int glyph = 0;
                    for (int k = 0; k < segX2 / 2; k++)
                    {
                        int end = U16(ttf, ends + k * 2), start = U16(ttf, starts + k * 2);
                        if (cp > end) continue;
                        if (cp < start) break;
                        int delta = (short)U16(ttf, deltas + k * 2), ro = U16(ttf, offsets + k * 2);
                        if (ro == 0) glyph = (cp + delta) & 0xFFFF;
                        else
                        {
                            glyph = U16(ttf, offsets + k * 2 + ro + 2 * (cp - start));
                            if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                        }
                        break;
                    }
                    if (glyph == 0) { all = false; break; }
                }
                return all;
            }
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// A TrueType font's em as a fraction of the ImGui pixel size (ImGui sizes a
    /// font so hhea ascent - descent = the size) and half its cap height in em.
    /// Read from the head, hhea and OS/2 tables; Segoe UI's values when unreadable.
    /// </summary>
    private readonly record struct FaceMetrics(float EmPerPx, float CapHalfEm)
    {
        private static readonly FaceMetrics Fallback = new(2048f / 2724f, 1434f / 2048f * 0.5f);

        public static FaceMetrics Read(byte[]? ttf)
        {
            if (ttf == null || ttf.Length < 12) return Fallback;
            try
            {
                int upm = 0, ascent = 0, descent = 0, cap = 0;
                int tables = U16(ttf, 4);
                for (int i = 0; i < tables; i++)
                {
                    int rec = 12 + i * 16;
                    uint tag = (uint)(ttf[rec] << 24 | ttf[rec + 1] << 16 | ttf[rec + 2] << 8 | ttf[rec + 3]);
                    int off = ttf[rec + 8] << 24 | ttf[rec + 9] << 16 | ttf[rec + 10] << 8 | ttf[rec + 11];
                    if (tag == 0x68656164) upm = U16(ttf, off + 18);                                         // head
                    else if (tag == 0x68686561) { ascent = S16(ttf, off + 4); descent = S16(ttf, off + 6); } // hhea
                    else if (tag == 0x4F532F32 && U16(ttf, off) >= 2) cap = S16(ttf, off + 88);              // OS/2
                }
                if (upm <= 0 || ascent - descent <= 0) return Fallback;
                if (cap <= 0) cap = (int)(upm * 0.7f);
                return new FaceMetrics((float)upm / (ascent - descent), cap * 0.5f / upm);
            }
            catch (IndexOutOfRangeException)
            {
                return Fallback;
            }
        }

        private static int U16(byte[] b, int at) => b[at] << 8 | b[at + 1];
        private static int S16(byte[] b, int at) => (short)(b[at] << 8 | b[at + 1]);
    }

    private static ushort* PinRanges(ushort[] ranges)
    {
        var p = (ushort*)NativeMemory.Alloc((nuint)ranges.Length, sizeof(ushort));
        ranges.AsSpan().CopyTo(new Span<ushort>(p, ranges.Length));
        NativeBuffers.Add((IntPtr)p);
        return p;
    }

    private static IntPtr ToNative(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return IntPtr.Zero;
        void* p = NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        NativeBuffers.Add((IntPtr)p);
        return (IntPtr)p;
    }

    /// <summary>A font embedded in this assembly (the Phosphor icons, the narrow labels); null when missing.</summary>
    private static byte[]? TryReadResource(string name, string whenMissing)
    {
        try
        {
            using Stream? stream = typeof(ImGuiFonts).Assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                RynthLog.UI($"ImGuiFonts: embedded font {name} missing; {whenMissing}.");
                return null;
            }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ImGuiFonts: could not read {name} - {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static byte[]? TryRead(string file)
    {
        try
        {
            string path = Path.Combine(FontsDir, file);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ImGuiFonts: could not read {file} - {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
