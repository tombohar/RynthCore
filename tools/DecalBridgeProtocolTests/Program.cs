using RynthCore.DecalBridge;

namespace DecalBridgeProtocolTests;

/// <summary>
/// The bridge (producer of events, consumer of commands) and the engine (the reverse) each
/// open their own view of one mapping, as in a client; every record is checked on arrival.
/// </summary>
internal static unsafe class Program
{
    private static int _checks, _failed;
    private static string _case = "";
    private static int _pid = 0x7F000000 + Environment.ProcessId % 0x10000;   // not a real client's pid

    private static int Main()
    {
        Run("hello and the want mask", WantMask);
        Run("chat events in order", ChatInOrder);
        Run("event ring wrap-around", EventWrap);
        Run("event ring full, then recovers", EventFull);
        Run("command ring round trip + wrap", Commands);
        Run("oversized records are refused", Oversized);
        Run("another protocol version is not Valid", VersionMismatch);
        Run("corrupt record: reader resyncs at the writer", CorruptResync);
        Run("a second engine generation continues where the first stopped", GenerationHandOff);
        Run("v2 header fields (Decal device) fit and reach the engine", HeaderFields);
        Console.WriteLine(_failed == 0 ? $"PASS: {_checks} checks" : $"FAIL: {_failed} of {_checks} checks");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        _case = name;
        _pid++;   // fresh mapping per case
        try { test(); }
        catch (Exception ex) { Check(false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  [{_case}] FAILED: {what}");
    }

    private sealed record Rec(BridgeRecordKind Kind, uint A0, uint A1, byte[] Payload)
    {
        public string Text => System.Text.Encoding.Unicode.GetString(Payload);
    }

    private static List<Rec> DrainAll(BridgeChannel ch, bool commands = false, int max = int.MaxValue)
    {
        var got = new List<Rec>();
        BridgeChannel.RecordHandler h = (k, a0, a1, p, n) =>
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = p[i];
            got.Add(new Rec(k, a0, a1, b));
        };
        if (commands) ch.DrainCommands(h, max); else ch.Drain(h, max);
        return got;
    }

    private static void Want(BridgeChannel engine, params BridgeRecordKind[] kinds)
    {
        uint m = 0;
        foreach (var k in kinds) m |= BridgeLayout.Bit(k);
        engine.WriteInt(BridgeLayout.OffWantMask, unchecked((int)m));
    }

    private static void WantMask()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Check(bridge.Valid && engine.Valid, "both views valid");
        Check(BridgeChannel.Exists(_pid), "Exists sees the mapping");
        Check(bridge.WriteString(BridgeRecordKind.Hello, BridgeLayout.Version, 0, "hi"), "hello always goes");
        Check(!bridge.WriteString(BridgeRecordKind.ChatText, 1, 0, "unwanted"), "chat refused with an empty mask");
        Check(!bridge.Write(BridgeRecordKind.ServerMessage, 0xF7B0, 0, null, 0), "server message refused with an empty mask");
        Want(engine, BridgeRecordKind.ChatText);
        Check(bridge.WriteString(BridgeRecordKind.ChatText, 1, 0, "wanted"), "chat accepted once wanted");
        Check(!bridge.Write(BridgeRecordKind.CreateObject, 5, 0, null, 0), "create still refused");
        var got = DrainAll(engine);
        Check(got.Count == 2 && got[0].Kind == BridgeRecordKind.Hello && got[1].Text == "wanted", $"drained hello + chat ({got.Count})");
        Check(engine.ReadLong(BridgeLayout.OffDropped) == 0, "refused-by-mask is not counted as dropped");
    }

    private static void ChatInOrder()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Want(engine, BridgeRecordKind.ChatText, BridgeRecordKind.ChatBarEnter);
        string[] lines = { "", "a", "ab", "abc", "Welcome to Asheron's Call\n", new string('x', 1000), "ünïcödé ✓" };
        for (int i = 0; i < lines.Length; i++)
            Check(bridge.WriteString(i % 2 == 0 ? BridgeRecordKind.ChatText : BridgeRecordKind.ChatBarEnter, (uint)i, 7, lines[i]), $"write {i}");
        var got = DrainAll(engine);
        Check(got.Count == lines.Length, $"count {got.Count}");
        for (int i = 0; i < Math.Min(got.Count, lines.Length); i++)
            Check(got[i].A0 == i && got[i].A1 == 7 && got[i].Text == lines[i], $"record {i} ('{got[i].Text}')");
        Check(DrainAll(engine).Count == 0, "nothing left");
        Check(bridge.ReadLong(BridgeLayout.OffRecords) == lines.Length, "records counter");
    }

    private static void EventWrap()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Want(engine, BridgeRecordKind.ServerMessage);
        var rnd = new Random(1234);
        uint next = 0, expect = 0;
        long total = 0;
        bool orderOk = true, contentOk = true;
        // ~3x the ring through it, drained in uneven bites so the writer wraps at odd offsets.
        while (total < 3L * BridgeLayout.EventRingSize)
        {
            int burst = rnd.Next(1, 60);
            for (int i = 0; i < burst; i++)
            {
                int len = rnd.Next(0, 9000);
                var data = new byte[len];
                for (int j = 0; j < len; j++) data[j] = (byte)(next + j);
                if (!bridge.WriteBytes(BridgeRecordKind.ServerMessage, next, (uint)len, data))
                    break;
                next++;
                total += len + 16;
            }
            foreach (var r in DrainAll(engine, max: rnd.Next(1, 80)))
            {
                if (r.A0 != expect) orderOk = false;
                if (r.Payload.Length != r.A1) contentOk = false;
                for (int j = 0; j < r.Payload.Length && contentOk; j++)
                    if (r.Payload[j] != (byte)(r.A0 + j)) contentOk = false;
                expect++;
            }
        }
        foreach (var r in DrainAll(engine)) { if (r.A0 != expect) orderOk = false; expect++; }
        Check(orderOk, "sequence in order across wraps");
        Check(contentOk, "payload bytes intact across wraps");
        Check(expect == next, $"every record arrived ({expect}/{next})");
        Check(bridge.ReadLong(BridgeLayout.OffDropped) == 0, "nothing dropped while drained");
    }

    private static void EventFull()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Want(engine, BridgeRecordKind.ServerMessage);
        var data = new byte[200 * 1024];
        int written = 0;
        while (bridge.WriteBytes(BridgeRecordKind.ServerMessage, (uint)written, 0, data)) written++;
        Check(written == BridgeLayout.EventRingSize / (data.Length + 16), $"filled with {written} records");
        Check(bridge.ReadLong(BridgeLayout.OffDropped) == 1, "one drop counted");
        var got = DrainAll(engine);
        Check(got.Count == written, $"all {written} drained ({got.Count})");
        Check(bridge.WriteBytes(BridgeRecordKind.ServerMessage, 999, 0, data), "writes again after the drain");
        got = DrainAll(engine);
        Check(got.Count == 1 && got[0].A0 == 999, "the new record arrives");
    }

    private static void Commands()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Check(!bridge.HasCommands, "empty at start");
        Check(engine.WriteCommand(BridgeRecordKind.CmdInvokeChatParser, 0, 0, "/loc"), "chat command");
        Check(engine.WriteCommand(BridgeRecordKind.CmdSalvagePanelAdd, 0x80000123, 0, null), "salvage add");
        Check(engine.WriteCommand(BridgeRecordKind.CmdSalvagePanelSalvage, 0, 0, null), "salvage go");
        Check(bridge.HasCommands, "bridge sees commands");
        var got = DrainAll(bridge, commands: true);
        Check(got.Count == 3 && got[0].Text == "/loc" && got[1].A0 == 0x80000123 && got[2].Kind == BridgeRecordKind.CmdSalvagePanelSalvage, "commands in order");
        Check(bridge.ReadLong(BridgeLayout.OffCmdDone) == 3, "done counter");
        Check(!bridge.HasCommands, "empty after drain");
        // The events ring is untouched by commands.
        Check(DrainAll(engine).Count == 0, "no command leaks into the event ring");
        // Wrap the 64 KB command ring many times.
        int sent = 0, seen = 0;
        bool ok = true;
        for (int round = 0; round < 400; round++)
        {
            for (int i = 0; i < 7; i++)
                if (engine.WriteCommand(BridgeRecordKind.CmdInvokeChatParser, (uint)sent, 0, "/tell Someone " + new string('z', (sent * 37) % 900))) sent++;
            foreach (var r in DrainAll(bridge, commands: true))
            {
                if (r.A0 != seen || !r.Text.StartsWith("/tell Someone ")) ok = false;
                seen++;
            }
        }
        Check(ok && seen == sent && sent == 2800, $"command wrap ({seen}/{sent})");
        // Full command ring: counted as dropped, never blocks.
        int n = 0;
        while (engine.WriteCommand(BridgeRecordKind.CmdInvokeChatParser, 0, 0, new string('q', 4000))) n++;
        Check(n > 0 && engine.ReadLong(BridgeLayout.OffCmdDropped) == 1, $"full command ring drops ({n} fit)");
    }

    private static void Oversized()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Want(engine, BridgeRecordKind.ServerMessage);
        Check(!bridge.WriteBytes(BridgeRecordKind.ServerMessage, 1, 0, new byte[BridgeLayout.MaxRecord + 1]), "event over MaxRecord refused");
        Check(!engine.WriteCommand(BridgeRecordKind.CmdInvokeChatParser, 0, 0, new string('x', BridgeLayout.CommandRingSize)), "command over a quarter of its ring refused");
        Check(bridge.WriteBytes(BridgeRecordKind.ServerMessage, 2, 0, new byte[10]), "a normal one still goes");
        var got = DrainAll(engine);
        Check(got.Count == 1 && got[0].A0 == 2, "only the normal one arrives");
    }

    private static void VersionMismatch()
    {
        using var older = BridgeChannel.OpenOrCreate(_pid);
        older.WriteInt(BridgeLayout.OffVersion, 1);   // a version-1 (spike) side created it
        using var newer = BridgeChannel.OpenOrCreate(_pid);
        Check(!newer.Valid, "version 1 mapping is not Valid for version 2");
    }

    private static void CorruptResync()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Want(engine, BridgeRecordKind.ChatText);
        bridge.WriteString(BridgeRecordKind.ChatText, 1, 0, "one");
        bridge.WriteString(BridgeRecordKind.ChatText, 2, 0, "two");
        // Stomp the second record's length field (header + command ring + first record of 24 bytes).
        byte* ring = engine.Header + BridgeLayout.HeaderSize + BridgeLayout.CommandRingSize;
        *(int*)(ring + 24) = 3;
        var got = DrainAll(engine);
        Check(got.Count == 1 && got[0].Text == "one", "first record read, corrupt one skipped");
        Check(engine.ReadLong(BridgeLayout.OffReadPos) == engine.ReadLong(BridgeLayout.OffWritePos), "reader at the writer");
        bridge.WriteString(BridgeRecordKind.ChatText, 3, 0, "three");
        got = DrainAll(engine);
        Check(got.Count == 1 && got[0].Text == "three", "later records flow again");
    }

    private static void GenerationHandOff()
    {
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        var gen1 = BridgeChannel.OpenOrCreate(_pid);
        Want(gen1, BridgeRecordKind.ChatText);
        bridge.WriteString(BridgeRecordKind.ChatText, 1, 0, "before reload");
        Check(DrainAll(gen1).Count == 1, "gen 1 reads");
        bridge.WriteString(BridgeRecordKind.ChatText, 2, 0, "during reload");
        gen1.Dispose();   // generation unloads; the bridge keeps the mapping
        using var gen2 = BridgeChannel.OpenOrCreate(_pid);
        Check(gen2.Valid, "gen 2 attaches to the same mapping");
        var got = DrainAll(gen2);
        Check(got.Count == 1 && got[0].Text == "during reload", "gen 2 gets what arrived in between, nothing twice");
    }

    // The header fields added to v2 (the device Decal renders with, for DecalInGameImGui):
    // inside the header, clear of every other field and of the prefix area, and a value the
    // bridge writes is what the engine reads.
    private static void HeaderFields()
    {
        int[] ints = { BridgeLayout.OffMagic, BridgeLayout.OffVersion, BridgeLayout.OffBridgeState, BridgeLayout.OffBridgeBeats,
            BridgeLayout.OffEngineAttached, BridgeLayout.OffEngineGeneration, BridgeLayout.OffBridgePid, BridgeLayout.OffReplayRequest,
            BridgeLayout.OffReplayDone, BridgeLayout.OffWantMask, BridgeLayout.OffD3DDevice, BridgeLayout.OffDecalHwnd };
        int[] longs = { BridgeLayout.OffWritePos, BridgeLayout.OffReadPos, BridgeLayout.OffDropped, BridgeLayout.OffRecords,
            BridgeLayout.OffCmdWritePos, BridgeLayout.OffCmdReadPos, BridgeLayout.OffCmdDropped, BridgeLayout.OffCmdDone };
        var used = new System.Collections.Generic.HashSet<int>();
        bool overlap = false;
        foreach (int o in ints) for (int b = 0; b < 4; b++) overlap |= !used.Add(o + b);
        foreach (int o in longs) for (int b = 0; b < 8; b++) overlap |= !used.Add(o + b);
        Check(!overlap, "header fields don't overlap");
        int max = 0;
        foreach (int b in used) max = Math.Max(max, b);
        Check(max < BridgeLayout.OffPrefixes, "header fields end before the prefix area");
        using var bridge = BridgeChannel.OpenOrCreate(_pid);
        bridge.WriteInt(BridgeLayout.OffD3DDevice, 0x12C1B300);
        using var engine = BridgeChannel.OpenOrCreate(_pid);
        Check(engine.ReadInt(BridgeLayout.OffD3DDevice) == 0x12C1B300, "device field reaches the engine");
    }
}
