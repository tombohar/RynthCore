using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;

namespace CharacterTitlesTests;

/// <summary>CharacterTitles (plugin API v75) without AC: hand-built ACE title events.</summary>
internal static unsafe class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        Environment.SetEnvironmentVariable("RYNTHCORE_CHAR_TITLES", null);

        // Nothing received yet.
        Check(Copy(0x50000001, out _, out _) == -1, "unknown before any event");

        // 0x0029 at login: [type][1][current][count][ids...], as ACE's GameEventCharacterTitle writes it.
        Send(TitleList(current: 9, 1, 9, 42, 42));
        Check(Copy(0x50000001, out uint[] ids, out uint cur) == 3 && cur == 9 && ids.SequenceEqual(new uint[] { 1, 9, 42 }),
            "title list (duplicate dropped)");

        // Received before the player id was known (owner 0): adopted by the first reader, then kept.
        Check(Copy(0x50000002, out _, out _) == -1, "another character's list isn't served");

        // 0x002B: a new title, then a display change.
        Send(Update(77, display: false));
        Check(Copy(0x50000001, out ids, out cur) == 4 && cur == 9 && ids.Contains(77u), "a new title is added");
        Send(Update(42, display: true));
        Check(Copy(0x50000001, out ids, out cur) == 4 && cur == 42, "a display change sets the current title");

        // Malformed: a count past the end is ignored (the old list stays).
        byte[] bad = TitleList(current: 1, 5, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(12), 1000);
        Send(bad);
        Check(Copy(0x50000001, out ids, out _) == 4, "a count past the end is refused");
        Check(!CharacterTitles.TryParseUpdate(new byte[8], out _, out _), "a short update is refused");

        // The hot-reload mirror round-trips.
        string? env = Environment.GetEnvironmentVariable("RYNTHCORE_CHAR_TITLES");
        Check(CharacterTitles.TryParseMirror(env, out uint owner, out uint mcur, out List<uint> mids)
              && owner == 0x50000001 && mcur == 42 && mids.Count == 4, "mirror: " + env);
        Check(!CharacterTitles.TryParseMirror("garbage", out _, out _, out _), "a bad mirror is refused");

        // Ids only: ask for the count first (ids null).
        uint c2 = 0;
        Check(CharacterTitles.Copy(0x50000001, null, 0, &c2) == 4 && c2 == 42, "count without ids");

        Console.WriteLine($"{_checks - _failed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static int Copy(uint player, out uint[] ids, out uint current)
    {
        uint[] buf = new uint[64];
        uint cur = 0;
        int n;
        fixed (uint* p = buf) n = CharacterTitles.Copy(player, p, buf.Length, &cur);
        ids = n > 0 ? buf.Take(n).ToArray() : Array.Empty<uint>();
        current = cur;
        return n;
    }

    private static void Send(byte[] msg)
    {
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(msg);
        fixed (byte* p = msg) CharacterTitles.OnGameEvent(type, (IntPtr)p, (uint)msg.Length, playerId: 0);
    }

    private static byte[] TitleList(uint current, params uint[] ids)
    {
        var b = new byte[16 + ids.Length * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, CharacterTitles.EventCharacterTitle);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), current);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)ids.Length);
        for (int i = 0; i < ids.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16 + i * 4), ids[i]);
        return b;
    }

    private static byte[] Update(uint title, bool display)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, CharacterTitles.EventUpdateTitle);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), title);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), display ? 1u : 0u);
        return b;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }
}
