using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Engine.UI.Data;

namespace ChatHistoryBench;

/// <summary>
/// What a longer chat history costs, and whether ImGui scrolls the way ChatFace's
/// follow fix needs (see the csproj header). Prints tables; exit 1 if a scroll check fails.
/// </summary>
internal static unsafe class Program
{
    private static readonly int[] Sizes = { 500, 2000, 5000, 10000 };
    private static int _checks, _failed;

    private static int Main(string[] args)
    {
        Console.WriteLine($"ChatHistoryBench: {(Environment.Is64BitProcess ? "x64" : "x86")} process, .NET {Environment.Version}, {Environment.ProcessorCount} cores");
#if !HAVE_RYNTHCHAT
        Console.WriteLine("RynthSuite checkout not found: plugin-side numbers skipped.");
#endif
        InitImGui();
        ScrollChecks();
        HistoryChecks();
        Memory();
        Cpu();
        Console.WriteLine();
        Console.WriteLine($"Checks: {_checks - _failed}/{_checks} passed.");
        return _failed == 0 ? 0 : 1;
    }

    // ── Realistic lines ──────────────────────────────────────────────────

    private static readonly string[] Names = { "Drakkon", "Aja", "Bob Smith", "Lucy", "Malar Zhavik", "Tusker Slayer", "Kel of the Vale" };
    private static readonly string[] Monsters = { "Tusker Guard", "Olthoi Swarm Nymph", "Mattekar", "Banderling Raider", "Shadow Child", "Drudge Ravener" };
    private static readonly string[] Spells = { "Incantation of Strength Self", "Aura of Defender", "Focus Self VII", "Incantation of Blade Protection Self" };
    private static readonly string[] Words = (
        "anyone selling mana stones need buffs at the portal north of town lfg for the olthoi dungeon " +
        "key drop rate is terrible tonight wts pyreal motes lol thanks for the fellow invite where is " +
        "the lifestone heading to the hub in a minute who has a legendary key ok see you there").Split(' ');

    /// <summary>One AC chat line: (text as AC prints it, ChatMessageType), 60-150 characters.</summary>
    private static (string Text, int Type) MakeLine(Random r)
    {
        int target = r.Next(60, 151);
        string name = Names[r.Next(Names.Length)], mob = Monsters[r.Next(Monsters.Length)];
        int k = r.Next(100);
        (string head, int type, bool speech) = k switch
        {
            < 20 => ($"You hit {mob} for {r.Next(5, 400)} points of slashing damage!", 0x16, false),
            < 35 => ($"{mob} grazes your foot for {r.Next(1, 90)} points of bludgeoning damage!", 0x15, false),
            < 45 => ($"You cast {Spells[r.Next(Spells.Length)]} on yourself, refreshing {Spells[r.Next(Spells.Length)]}", 0x07, false),
            < 50 => ("The spell consumed the following components: Prismatic Taper", 0x07, false),
            < 62 => ($"[{(r.Next(2) == 0 ? "General" : "Trade")}] {name} says, \"", 0x08, true),
            < 72 => ($"{name} says, \"", 0x02, true),
            < 77 => ($"{name} tells you, \"", 0x03, true),
            < 82 => ($"[Fellowship] {name} says, \"", 0x13, true),
            < 88 => ($"[RynthAi] Meta state Combat -> Loot ({r.Next(1, 30)} corpses)", 0x00, false),
            < 92 => ("You're too busy!", 0x1A, false),
            _ => ($"You've earned {r.Next(1000, 900000)} experience.", 0x0D, false),
        };
        var sb = new System.Text.StringBuilder(head, 160);
        if (speech)
        {
            if (r.Next(12) == 0) sb.Append("Drakkon ");   // someone mentions you now and then
            while (sb.Length < target - 1) sb.Append(Words[r.Next(Words.Length)]).Append(' ');
            sb.Length = Math.Min(sb.Length, target - 1);
            sb.Append('"');
        }
        else
        {
            while (sb.Length < target) sb.Append(sb.Length == head.Length ? " (" : " ").Append(Words[r.Next(Words.Length)]);
            if (sb.Length > target) sb.Length = target;
        }
        return (sb.ToString(), type);
    }

    // ── The engine side, as ChatSource does it ───────────────────────────

    private static ChatRouter MakeRouter()
    {
        ChatFilterRule Rule(ChatMatchMode mode, string pattern, ChatRuleAction action, string tab = "", string when = "",
            ChatColorMode color = ChatColorMode.None)
        {
            var r = new ChatFilterRule { Mode = mode, Pattern = pattern, Action = action, Tab = tab, When = when, ColorMode = color };
            r.Recompile();
            return r;
        }
        // A rule set like a player's: hide spam, sort, copy tells, colour.
        ChatFilterRule[] rules =
        {
            Rule(ChatMatchMode.Contains, "You're too busy!", ChatRuleAction.Hide),
            Rule(ChatMatchMode.StartsWith, "The spell consumed the following components", ChatRuleAction.Hide),
            Rule(ChatMatchMode.Regex, @"^\[RynthAi\]", ChatRuleAction.Move, "Bot"),
            Rule(ChatMatchMode.Regex, @"tells you, """, ChatRuleAction.Copy, "Tells", color: ChatColorMode.Line),
            Rule(ChatMatchMode.Regex, @"^\[(Trade|LFG)\]", ChatRuleAction.Move, "Trade"),
            Rule(ChatMatchMode.Regex, @"^You cast .* on yourself", ChatRuleAction.Move, "Buffs", "type:7"),
            Rule(ChatMatchMode.Regex, @"\b(Olthoi|Tusker)\b", ChatRuleAction.Color, color: ChatColorMode.Match),
            Rule(ChatMatchMode.Contains, "Critical hit", ChatRuleAction.Color, color: ChatColorMode.Line),
        };
        var mention = new Regex(@"(?<![\w'])(?:Drakkon)(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(ChatFilterRule.TimeoutMs));
        return new ChatRouter(rules, mention, "Drakkon", countHits: true);
    }

    private static long _nextId = 1;

    /// <summary>ChatSource.PollLines' parse + construct + route for one read (no AC-link stripping: no links here).</summary>
    private static List<ChatLine> ReadLines(string json, ChatRouter router)
    {
        RynthChatLineDto[]? dtos = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatLineDtoArray);
        var added = new List<ChatLine>(dtos?.Length ?? 0);
        foreach (RynthChatLineDto dto in dtos ?? Array.Empty<RynthChatLineDto>())
        {
            var line = new ChatLine(_nextId++, dto.Ts ?? "", dto.Chan ?? "Other", dto.Type, dto.Sender, dto.Text ?? "") { Seq = dto.Seq };
            line.Route = router.Evaluate(line, null);
            added.Add(line);
        }
        return added;
    }

#if HAVE_RYNTHCHAT
    private static RynthCore.Plugin.RynthChat.ChatBuffer FilledPlugin(int n, Random r)
    {
        var b = new RynthCore.Plugin.RynthChat.ChatBuffer(n);
        for (int i = 0; i < n; i++) { var (t, ty) = MakeLine(r); b.Add(t, ty); }
        return b;
    }
#endif

    /// <summary>N engine lines, routed, built the way ChatSource builds them (from the plugin's JSON when it's here).</summary>
    private static ChatLine[] EngineLines(int n, Random r, ChatRouter router)
    {
#if HAVE_RYNTHCHAT
        string json = FilledPlugin(n, r).BuildJson(0);
#else
        var dtos = new RynthChatLineDto[n];
        for (int i = 0; i < n; i++) { var (t, ty) = MakeLine(r); dtos[i] = new RynthChatLineDto { Seq = (ulong)i + 1, Ts = "12:34:56", Chan = "Combat", Type = ty, Text = t }; }
        string json = JsonSerializer.Serialize(dtos, RynthChatJsonContext.Default.RynthChatLineDtoArray);
#endif
        return ReadLines(json, router).ToArray();
    }

    // ── Memory ───────────────────────────────────────────────────────────

    private static long Heap()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(true);
    }

    /// <summary>Bytes held by what <paramref name="make"/> returns.</summary>
    private static long Held(Func<object> make)
    {
        make();   // once first, so lazy statics (regex caches, colour tables) aren't counted
        long before = Heap();
        object o = Make(make);
        long after = Heap();
        GC.KeepAlive(o);
        return after - before;
    }

    // Not inlined: the temporaries of make() (JSON, DTOs, the plugin ring the engine lines
    // were read from) must be unreachable when Held measures.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object Make(Func<object> make) => make();

    private static void Memory()
    {
        Console.WriteLine();
        Console.WriteLine("Managed memory (x86 object sizes), measured with GC.GetTotalMemory:");
        Console.WriteLine($"{"lines",8} | {"engine lines",13} | {"plugin ring",12} | {"face lists",11} | {"total",9} | {"per 1,000",10}");
        var router = MakeRouter();
        foreach (int n in Sizes)
        {
            long engine = Held(() => EngineLines(n, new Random(n), router));
#if HAVE_RYNTHCHAT
            long plugin = Held(() => FilledPlugin(n, new Random(n + 1)));
#else
            long plugin = 0;
#endif
            ChatLine[] lines = EngineLines(n, new Random(n), router);
            long face = Held(() =>
            {
                // ChatFace's per-line lists (_vis, _h, _lay, _tops), sized like List growth leaves them.
                var vis = new List<ChatLine>(lines);
                var h = new List<float>(new float[n]);
                var lay = new List<object?>(new object?[n]);
                var tops = new float[n + 1];
                return new object[] { vis, h, lay, tops };
            });
            long total = engine + plugin + face;
            Console.WriteLine($"{n,8:N0} | {Mb(engine),13} | {Mb(plugin),12} | {Mb(face),11} | {Mb(total),9} | {Mb(total * 1000 / n),10}");
        }
        Console.WriteLine("(The face's highlighted lines also keep a hand layout; first read of the whole ring is transient: see 'first read'.)");
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.00} MB";

    // ── CPU ──────────────────────────────────────────────────────────────

    /// <summary>Median milliseconds per call of <paramref name="body"/> (setup outside the timing).</summary>
    private static double Time(Action body, int iters = 50, int rounds = 7)
    {
        for (int i = 0; i < Math.Min(iters, 10); i++) body();   // warm up
        var samples = new double[rounds];
        for (int s = 0; s < rounds; s++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < iters; i++) body();
            samples[s] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / iters;
        }
        Array.Sort(samples);
        return samples[rounds / 2];
    }

    private static string Ms(double ms) => ms < 0.01 ? $"{ms * 1000:0.0} us" : ms < 1 ? $"{ms * 1000:0} us" : $"{ms:0.0} ms";

    private static readonly string[] Tabs = { "All", "Chat", "Channels", "System", "Combat", "Rynth", "Items", "Other", "Bot", "Tells", "Trade", "Buffs" };

    private static void Cpu()
    {
        Console.WriteLine();
        Console.WriteLine("CPU, median per call (one batch = 3 new lines, the buffer full):");
        var rows = new List<(string Name, string Where, string[] Cells)>();
        void Row(string name, string where, Func<int, double> run)
        {
            var cells = new string[Sizes.Length];
            for (int i = 0; i < Sizes.Length; i++) cells[i] = Ms(run(Sizes[i]));
            rows.Add((name, where, cells));
        }

#if HAVE_RYNTHCHAT
        Row("plugin BuildJson, 3 new (0.2.1)", "pump, per poll", n =>
        {
            var r = new Random(n);
            var b = new OldChatBuffer(n);
            for (int i = 0; i < n; i++) { var (t, ty) = MakeLine(r); b.Add(t, ty); }
            ulong seq = (ulong)n;
            var adds = Enumerable.Range(0, 64).Select(_ => MakeLine(r)).ToArray();
            int a = 0;
            return Time(() =>
            {
                for (int i = 0; i < 3; i++) { var (t, ty) = adds[a++ & 63]; b.Add(t, ty); }
                string json = b.BuildJson(seq);
                seq += 3;
                GC.KeepAlive(json);
            }, 200);
        });
        Row("plugin BuildJson, 3 new (0.3.0)", "pump, per poll", n =>
        {
            var r = new Random(n);
            var b = FilledPlugin(n, r);
            ulong seq = (ulong)n;
            var adds = Enumerable.Range(0, 64).Select(_ => MakeLine(r)).ToArray();
            int a = 0;
            return Time(() =>
            {
                for (int i = 0; i < 3; i++) { var (t, ty) = adds[a++ & 63]; b.Add(t, ty); }
                string json = b.BuildJson(seq);
                seq += 3;
                GC.KeepAlive(json);
            }, 200);
        });
        Row("plugin BuildJson, nothing new (0.2.1)", "pump, per poll", n =>
        {
            var r = new Random(n);
            var b = new OldChatBuffer(n);
            for (int i = 0; i < n; i++) { var (t, ty) = MakeLine(r); b.Add(t, ty); }
            return Time(() => GC.KeepAlive(b.BuildJson((ulong)n)), 200);
        });
        Row("plugin BuildJson, nothing new (0.3.0)", "pump, per poll", n =>
        {
            var b = FilledPlugin(n, new Random(n));
            return Time(() => GC.KeepAlive(b.BuildJson((ulong)n)), 200);
        });
#endif
        Row("engine read: parse+route 3 lines", "pump, per poll", n =>
        {
            var router = MakeRouter();
            var r = new Random(n);
            var dtos = Enumerable.Range(0, 3).Select(i => { var (t, ty) = MakeLine(r); return new RynthChatLineDto { Seq = (ulong)i + 1, Ts = "12:34:56", Chan = "Combat", Type = ty, Text = t }; }).ToArray();
            string json = JsonSerializer.Serialize(dtos, RynthChatJsonContext.Default.RynthChatLineDtoArray);
            return Time(() => GC.KeepAlive(ReadLines(json, router)), 200);
        });
        Row("engine merge into new array", "pump, per poll", n =>
        {
            var router = MakeRouter();
            ChatLine[] kept = EngineLines(n, new Random(n), router);
            var added = EngineLines(3, new Random(1), router).ToList();
            return Time(() => { kept = ChatHistory.Merge(kept, added, n); }, 200);
        });
        Row("first read: whole ring, JSON+parse+route", "pump, once per bind/reload", n =>
        {
            var router = MakeRouter();
#if HAVE_RYNTHCHAT
            var b = FilledPlugin(n, new Random(n));
            return Time(() =>
            {
                string json = b.BuildJson(0);
                IntPtr p = Marshal.StringToHGlobalAnsi(json);   // RynthChatGetScrollbackJson
                string got = Marshal.PtrToStringAnsi(p)!;       // ChatSource
                Marshal.FreeHGlobal(p);
                GC.KeepAlive(ReadLines(got, router));
            }, 3, 5);
#else
            return double.NaN;
#endif
        });
        Row("re-route every kept line (rule edit)", "pump, per rule change", n =>
        {
            var router = MakeRouter();
            ChatLine[] kept = EngineLines(n, new Random(n), router);
            return Time(() =>
            {
                router.BeginPass();
                try { foreach (ChatLine line in kept) line.Route = router.Evaluate(line, null); }
                finally { router.EndPass(); }
            }, 3, 5);
        });
        Row("face rebuild: tab Combat", "AC thread, tab switch", n =>
        {
            ChatLine[] kept = EngineLines(n, new Random(n), MakeRouter());
            var vis = new List<ChatLine>(n);
            return Time(() => { vis.Clear(); foreach (var l in kept) if (ChatModel.LineVisible(l, "Combat", "")) vis.Add(l); }, 20);
        });
        Row("face rebuild: All + search \"tusker\"", "AC thread, search keystroke", n =>
        {
            ChatLine[] kept = EngineLines(n, new Random(n), MakeRouter());
            var vis = new List<ChatLine>(n);
            return Time(() => { vis.Clear(); foreach (var l in kept) if (ChatModel.LineVisible(l, "All", "tusker")) vis.Add(l); }, 20);
        });
        Row("face new lines: filter 3 + drop 3 + tops", "AC thread, per batch", n =>
        {
            ChatLine[] kept = EngineLines(n, new Random(n), MakeRouter());
            var vis = new List<ChatLine>(kept); var h = new List<float>(new float[n]); var lay = new List<object?>(new object?[n]);
            var tops = new float[n + 1];
            var added = EngineLines(3, new Random(2), MakeRouter());
            return Time(() =>
            {
                vis.RemoveRange(0, 3); h.RemoveRange(0, 3); lay.RemoveRange(0, 3);
                foreach (var l in added) if (ChatModel.LineVisible(l, "All", "")) { vis.Add(l); h.Add(13f); lay.Add(null); }
                for (int i = 0; i < vis.Count; i++) tops[i + 1] = tops[i] + h[i];
            }, 200);
        });
        Row("unread counts, full recount (before)", "AC thread, per batch (was)", n =>
        {
            ChatLine[] kept = EngineLines(n, new Random(n), MakeRouter());
            var seen = Tabs.ToDictionary(t => t, _ => 0L);   // worst case: no tab visited since the start
            return Time(() => UnreadFull(kept, seen), 10);
        });
        Row("unread counts, incremental (now)", "AC thread, per batch", n =>
        {
            ChatLine[] kept = EngineLines(n, new Random(n), MakeRouter());
            var seen = Tabs.ToDictionary(t => t, _ => 0L);
            long countedTo = kept[^4].Id;   // all but the newest 3 already counted
            return Time(() => UnreadIncremental(kept, seen, countedTo), 200);
        });
        Row("measure wrapped heights, every line", "AC thread, resize/font/rebuild", n =>
        {
            ChatLine[] kept = EngineLines(n, new Random(n), MakeRouter());
            double ms = 0;
            InFrame(() => ms = Time(() => { foreach (var l in kept) GC.KeepAlive(ImGui.CalcTextSize(l.Text, false, 380f).Y); }, 3, 5));
            return ms;
        });

        int nameW = rows.Max(r => r.Name.Length), whereW = rows.Max(r => r.Where.Length);
        Console.WriteLine($"{"".PadRight(nameW)} | {"thread / when".PadRight(whereW)} | " + string.Join(" | ", Sizes.Select(s => $"{s,9:N0}")));
        foreach (var (name, where, cells) in rows)
            Console.WriteLine($"{name.PadRight(nameW)} | {where.PadRight(whereW)} | " + string.Join(" | ", cells.Select(c => $"{c,9}")));
    }

    private static int UnreadFull(ChatLine[] lines, Dictionary<string, long> seen)
    {
        int total = 0;
        foreach (string tab in Tabs)
        {
            long s = seen[tab];
            for (int i = lines.Length - 1; i >= 0 && lines[i].Id > s; i--)
                if (ChatModel.LineInTab(lines[i], tab)) total++;
        }
        return total;
    }

    private static int UnreadIncremental(ChatLine[] lines, Dictionary<string, long> seen, long countedTo)
    {
        int total = 0;
        foreach (string tab in Tabs)
        {
            long floor = Math.Max(seen[tab], countedTo);
            for (int i = lines.Length - 1; i >= 0 && lines[i].Id > floor; i--)
                if (ChatModel.LineInTab(lines[i], tab)) total++;
        }
        return total;
    }

    // ── ImGui ────────────────────────────────────────────────────────────

    private static void InitImGui()
    {
        IntPtr ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        ImGuiIOPtr io = ImGui.GetIO();
        io.NativePtr->IniFilename = null;
        io.DisplaySize = new Vector2(1280, 800);
        io.DeltaTime = 1f / 60f;
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string font = new[] { "CascadiaMono.ttf", "consola.ttf" }.Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists) ?? "";
        if (font.Length > 0) io.Fonts.AddFontFromFileTTF(font, 13f);
        else io.Fonts.AddFontDefault();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _, out int _);
        io.Fonts.SetTexID((IntPtr)1);
        Console.WriteLine($"ImGui {ImGui.GetVersion()} (the engine's cimgui.dll), font {(font.Length > 0 ? Path.GetFileName(font) : "default")} 13 px");
    }

    private static void InFrame(Action body)
    {
        ImGui.GetIO().DeltaTime = 1f / 60f;
        ImGui.NewFrame();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(600, 500));
        ImGui.Begin("host", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
        body();
        ImGui.End();
        ImGui.EndFrame();
    }

    private const float ViewH = 200f;

    /// <summary>
    /// One frame of a chat-like list: content <paramref name="total"/> high. "before" is
    /// ChatFace until 2026-10-11 (SetScrollHereY at the end of the frame); "now" hands the
    /// content size and the scroll to this frame's BeginChild. <paramref name="setScroll"/>
    /// (now only): the scroll to ask for, or null. Returns (scroll, max) as seen inside the child.
    /// </summary>
    private static (float Scroll, float Max) ListFrame(string id, float total, bool now, float? setScroll, bool follow)
    {
        (float, float) seen = default;
        InFrame(() =>
        {
            if (now)
            {
                ImGui.SetNextWindowContentSize(new Vector2(0, total));
                if (setScroll is float s) ImGui.SetNextWindowScroll(new Vector2(-1, s));
            }
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 10f);
            ImGui.BeginChild(id, new Vector2(300, ViewH), ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);
            ImGui.PopStyleVar(2);
            seen = (ImGui.GetScrollY(), ImGui.GetScrollMaxY());
            ImGui.Dummy(new Vector2(280, total));
            if (!now && follow && total > ViewH) ImGui.SetScrollHereY(1f);
            ImGui.EndChild();
        });
        return seen;
    }

    private static void Check(string what, bool ok, string detail)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}: {detail}");
    }

    /// <summary>ChatHistory's arithmetic and RynthChat's resizable ring (newest kept, seq untouched).</summary>
    private static void HistoryChecks()
    {
        Console.WriteLine();
        Console.WriteLine("History checks:");
        Check("Clamp: absent is the default, steps of 500, 500..10000",
            ChatHistory.Clamp(0) == 2000 && ChatHistory.Clamp(-5) == 2000 && ChatHistory.Clamp(1) == 500
            && ChatHistory.Clamp(2240) == 2000 && ChatHistory.Clamp(2260) == 2500 && ChatHistory.Clamp(99999) == 10000,
            $"0->{ChatHistory.Clamp(0)}, 1->{ChatHistory.Clamp(1)}, 2260->{ChatHistory.Clamp(2260)}, 99999->{ChatHistory.Clamp(99999)}");
        int[] old = Enumerable.Range(1, 10).ToArray();
        int[] m1 = ChatHistory.Merge(old, new[] { 11, 12, 13 }, 10);
        int[] m2 = ChatHistory.Merge(old, new[] { 11, 12 }, 20);
        int[] m3 = ChatHistory.Merge(old, Enumerable.Range(11, 30).ToArray(), 10);
        Check("Merge keeps the newest, in order", m1.SequenceEqual(Enumerable.Range(4, 10)) && m2.SequenceEqual(Enumerable.Range(1, 12))
            && m3.SequenceEqual(Enumerable.Range(31, 10)), $"[{m1[0]}..{m1[^1]}], [{m2[0]}..{m2[^1]}], [{m3[0]}..{m3[^1]}]");
        int[]? t1 = ChatHistory.Trim(old, 4);
        Check("Trim drops the oldest, or nothing", t1 != null && t1.SequenceEqual(new[] { 7, 8, 9, 10 }) && ChatHistory.Trim(old, 10) == null, "");
#if HAVE_RYNTHCHAT
        var r = new Random(5);
        var b = new RynthCore.Plugin.RynthChat.ChatBuffer(500);
        for (int i = 0; i < 700; i++) { var (t, ty) = MakeLine(r); b.Add(t, ty); }
        ulong[] Seqs(string json) => JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatLineDtoArray)!.Select(d => d.Seq).ToArray();
        ulong[] all = Seqs(b.BuildJson(0));
        Check("ring of 500 after 700 lines: seq 201..700", all.Length == 500 && all[0] == 201 && all[^1] == 700, $"{all.Length} lines, {all[0]}..{all[^1]}");
        b.SetCapacity(2000);
        for (int i = 0; i < 100; i++) { var (t, ty) = MakeLine(r); b.Add(t, ty); }
        all = Seqs(b.BuildJson(0));
        Check("grown to 2000: keeps all 500, then grows; seq carries on", all.Length == 600 && all[0] == 201 && all[^1] == 800
            && all.Zip(all.Skip(1)).All(p => p.Second == p.First + 1), $"{all.Length} lines, {all[0]}..{all[^1]}");
        b.SetCapacity(100);
        all = Seqs(b.BuildJson(0));
        ulong[] since = Seqs(b.BuildJson(790));
        Check("shrunk to 100: the newest 100; 'since' still works", all.Length == 100 && all[0] == 701 && all[^1] == 800
            && since.SequenceEqual(Enumerable.Range(791, 10).Select(x => (ulong)x)) && b.BuildJson(800) == "[]" && b.BuildJson(5000) == "[]",
            $"{all.Length} lines, {all[0]}..{all[^1]}; since 790: {since.Length}");
        Check("capacity clamped to 100..10000", b.SetCapacity(5) == 100 && b.SetCapacity(50000) == 10000 && b.Capacity == 10000, "");
        var old500 = new OldChatBuffer(500);
        var new500 = new RynthCore.Plugin.RynthChat.ChatBuffer(500);
        var r1 = new Random(9);
        bool same = true;
        for (int i = 0; i < 1200; i++)
        {
            var (t, ty) = MakeLine(r1);
            old500.Add(t, ty);
            new500.Add(t, ty);
            if (i % 37 == 0)
                foreach (ulong s in new ulong[] { 0, (ulong)Math.Max(0, i - 3), (ulong)i, (ulong)(i + 1), (ulong)(i + 5) })
                {
                    string a = old500.BuildJson(s), c = new500.BuildJson(s);
                    // Same lines; only the timestamp may differ (each buffer stamps its own Add).
                    if (!Seqs(a).SequenceEqual(Seqs(c))) same = false;
                }
        }
        Check("0.3.0 returns the same lines as 0.2.1 for any 'since'", same, "");
#endif
    }

    private static void ScrollChecks()
    {
        Console.WriteLine();
        Console.WriteLine("Scroll checks (a 200 px list; 'gap' = how far the newest line's bottom is from the list's bottom in that frame):");

        // Before: following with SetScrollHereY. New lines (40 px) arrive with nothing dropped,
        // then a full buffer drops 60 px off the top while 20 px arrive.
        for (int f = 0; f < 4; f++) ListFrame("before", 1000, false, null, true);
        var a = ListFrame("before", 1040, false, null, true);
        float gapGrow = 1040 - (a.Scroll + ViewH);
        var b = ListFrame("before", 1040, false, null, true);
        var c = ListFrame("before", 1000, false, null, true);
        float gapShrink = 1000 - (c.Scroll + ViewH);
        var d = ListFrame("before", 1000, false, null, true);
        Console.WriteLine($"  before: 40 px arrive -> gap {gapGrow:0} px that frame, {1040 - (b.Scroll + ViewH):0} the next; " +
                          $"60 px leave the top as 20 arrive -> gap {gapShrink:0} px (blank under the text), {1000 - (d.Scroll + ViewH):0} the next");
        Check("the old follow lags a frame (the bounce)", gapGrow > 0.5f && Math.Abs(gapShrink) > 0.5f,
            "reproduced: each batch is first drawn at the old scroll");

        // Now: the content size and the scroll go into this frame's BeginChild.
        for (int f = 0; f < 4; f++) ListFrame("now", 1000, true, 1000, true);
        var e = ListFrame("now", 1040, true, 1040, true);
        var g = ListFrame("now", 1000, true, 1000, true);
        var h = ListFrame("now", 1000.4f, true, 1000.4f, true);
        Check("following: newest line at the bottom in the same frame (grow)", Math.Abs(1040 - (e.Scroll + ViewH)) <= 0.5f && Math.Abs(e.Scroll - e.Max) < 0.01f,
            $"scroll {e.Scroll:0.#} of max {e.Max:0.#}");
        Check("following: newest line at the bottom in the same frame (shrink)", Math.Abs(1000 - (g.Scroll + ViewH)) <= 0.5f && Math.Abs(g.Scroll - g.Max) < 0.01f,
            $"scroll {g.Scroll:0.#} of max {g.Max:0.#}");
        Check("following: still pinned with a fractional height", h.Scroll >= h.Max - 24f && Math.Abs(1000.4f - (h.Scroll + ViewH)) < 1f,
            $"scroll {h.Scroll:0.##} of max {h.Max:0.##} (stays within the 24 px stick threshold)");

        // Scrolled up: lines of a fractional height (13.3 px) leave the top of a full buffer as
        // as many arrive, 60 batches. The view moves up by exactly what left, fractions carried.
        float total = 5000, start = 3000, scroll = start, carry = 0, worst = 0, worstNoCarry = 0, noCarry = start;
        ListFrame("up", total, true, start, false);
        for (int i = 1; i <= 60; i++)
        {
            const float dropped = 13.3f;
            float target = scroll + carry - dropped;
            float applied = MathF.Round(Math.Max(0, target));
            carry = target > 0 ? target - applied : 0;
            var s = ListFrame("up", total, true, applied, false);
            scroll = s.Scroll;
            float exact = start - i * dropped;
            worst = Math.Max(worst, Math.Abs(scroll - exact));
            noCarry = MathF.Round(noCarry - dropped);
            worstNoCarry = Math.Max(worstNoCarry, Math.Abs(noCarry - exact));
        }
        Check("scrolled up: the view holds still across 60 batches (no creep)", worst <= 0.51f,
            $"off by at most {worst:0.00} px (without carrying fractions: {worstNoCarry:0.00} px)");

        // Scrolled up, nothing dropped (buffer not full yet): no scroll is set, nothing moves.
        var u0 = ListFrame("up2", 3000, true, 1200, false);
        var u1 = ListFrame("up2", 3040, true, null, false);
        var u2 = ListFrame("up2", 3080, true, null, false);
        Check("scrolled up: new lines below leave the view where it is", u1.Scroll == u0.Scroll && u2.Scroll == u0.Scroll,
            $"scroll {u0.Scroll:0} -> {u1.Scroll:0} -> {u2.Scroll:0}");
    }
}
