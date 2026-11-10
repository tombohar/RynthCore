using System.Text;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.WebPanel;

namespace WebPanelTests;

/// <summary>The web panel's pure policies: URL allowlist, redaction, visibility, layout, profile folder.</summary>
internal static unsafe class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static readonly string[] Dw = { "*.acdreamweave.com" };

    private static int Main()
    {
        Allowlist_ExactAndWildcard();
        Allowlist_LookalikesRejected();
        Allowlist_CaseIdnTrailingDot();
        Allowlist_BadEntries();
        Allowlist_ParseGood();
        BrowserSafe();
        BrowserGate();
        Redact();
        Visibility();
        Layout();
        Profile();
        ApiRules();
        NativeArgs();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} assertions)" : $"FAIL ({_failed} of {_checks} assertions)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name) { _case = name; Console.WriteLine("ok   " + name); }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL {_case}: {what}");
    }

    private static void Eq<T>(T got, T want, string what) =>
        Check(EqualityComparer<T>.Default.Equals(got, want), $"{what}: got {got}, want {want}");

    private static void BrowserGate()
    {
        Case("browser gate: off-list pages reach the system browser only after a click, rate-limited");
        var g = new WebPanelBrowserGate();
        Eq(g.Decide(userInitiated: false, 0), BrowserOpenVerdict.NotUserInitiated, "a page's own request (script, ad iframe) is dropped");
        Eq(g.Decide(userInitiated: true, 0), BrowserOpenVerdict.Open, "a click opens it");
        Eq(g.Decide(userInitiated: true, 500), BrowserOpenVerdict.TooSoon, "a second within the gap is dropped");
        Eq(g.Decide(userInitiated: false, 5000), BrowserOpenVerdict.NotUserInitiated, "no click is never opened, even after the gap");
        long t = 0;
        int opened = 1;
        for (int i = 0; i < 10; i++)
        {
            t += WebPanelBrowserGate.MinGapMs;
            if (g.Decide(true, t) == BrowserOpenVerdict.Open) opened++;
        }
        Eq(opened, WebPanelBrowserGate.MaxPerMinute, "at most MaxPerMinute a minute, however spaced");
        Eq(g.Decide(true, 61_000 + WebPanelBrowserGate.MinGapMs), BrowserOpenVerdict.Open, "the minute passes: opens again");
        var fresh = new WebPanelBrowserGate();
        Eq(fresh.Decide(true, long.MinValue + 1), BrowserOpenVerdict.Open, "a first open at any clock value opens");
    }

    private static void Allowlist_ExactAndWildcard()
    {
        Case("Allowlist_ExactAndWildcard");
        Check(WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.com/x", Dw), "www subdomain allowed");
        Check(WebPanelUrlPolicy.IsAllowed("https://a.b.acdreamweave.com/", Dw), "deep subdomain allowed");
        Check(!WebPanelUrlPolicy.IsAllowed("https://acdreamweave.com/", Dw), "wildcard does not match the suffix itself");
        Check(WebPanelUrlPolicy.IsAllowed("https://acdreamweave.com/", new[] { "acdreamweave.com" }), "exact entry matches");
        Check(!WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.com/", new[] { "acdreamweave.com" }), "exact entry does not match subdomain");
        Check(WebPanelUrlPolicy.IsAllowed("about:blank", Dw), "about:blank allowed");
        Check(!WebPanelUrlPolicy.IsAllowed(null, Dw), "null rejected");
        Check(!WebPanelUrlPolicy.IsAllowed("", Dw), "empty rejected");
        Check(!WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.com/", Array.Empty<string>()), "empty allowlist rejects all");
    }

    private static void Allowlist_LookalikesRejected()
    {
        Case("Allowlist_LookalikesRejected");
        foreach (string u in new[]
        {
            "https://www.acdreamweave.com.evil.com/",
            "https://evilacdreamweave.com/",
            "https://www.acdreamweave.com@evil.com/",
            "https://evil.com/?h=.acdreamweave.com",
            "http://www.acdreamweave.com/",
            "https://www.acdreamweave.com:8443/",
            "https://user:pw@www.acdreamweave.com/",
            "file:///C:/x",
            "javascript:alert(1)",
        })
            Check(!WebPanelUrlPolicy.IsAllowed(u, Dw), "rejects " + u);
        Check(!WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.com/" + new string('a', 5000), Dw), "over MaxUrlBytes rejected");
        Check(!WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.c\u2100/", Dw), "invalid IDN char fails closed (no throw)");
        Check(!WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.c" + "\uD800" + "/", Dw), "lone-surrogate host fails closed (no throw)");
    }

    private static void Allowlist_CaseIdnTrailingDot()
    {
        Case("Allowlist_CaseIdnTrailingDot");
        Check(WebPanelUrlPolicy.IsAllowed("https://WWW.AcDreamWeave.com./", Dw), "case and one trailing dot");
        Check(WebPanelUrlPolicy.IsAllowed("https://www.acdreamweave.com:443/", Dw), "explicit default port");
        string puny = new System.Globalization.IdnMapping().GetAscii("b\u00fccher.example");
        Check(WebPanelUrlPolicy.IsAllowed("https://b\u00fccher.example/", new[] { puny }), "unicode host matches its punycode entry");
        Check(WebPanelUrlPolicy.IsAllowed("https://" + puny + "/", new[] { puny }), "punycode host matches itself");
        Check(!WebPanelUrlPolicy.IsAllowed("https://bucher.example/", new[] { puny }), "ascii lookalike does not match punycode entry");
        Check(!WebPanelUrlPolicy.IsAllowed("https://xn--bcher-kva.example/", new[] { "bucher.example" }), "punycode host does not match other entry");
    }

    private static void Allowlist_BadEntries()
    {
        Case("Allowlist_BadEntries");
        foreach (string bad in new[] { "*", "*.com", "https://x.com", "x.com/path", "x.com:443", "a.*.com", "", "   ", "x.com.", "*.x.*.com", "a b.com", "*x.com" })
            Check(!WebPanelUrlPolicy.TryParseAllowlist(bad, out _), "rejects entry list '" + bad + "'");
        Check(!WebPanelUrlPolicy.TryParseAllowlist(null, out _), "null rejected");
        string seventeen = string.Join("\n", Enumerable.Range(0, 17).Select(i => $"h{i}.example.com"));
        Check(!WebPanelUrlPolicy.TryParseAllowlist(seventeen, out _), "17 entries rejected");
        string sixteen = string.Join("\n", Enumerable.Range(0, 16).Select(i => $"h{i}.example.com"));
        Check(WebPanelUrlPolicy.TryParseAllowlist(sixteen, out var e16) && e16.Length == 16, "16 entries accepted");
        Check(!WebPanelUrlPolicy.TryParseAllowlist("good.example.com\nbad/entry", out _), "one bad entry fails the list");
        Check(!WebPanelUrlPolicy.TryParseAllowlist(new string('a', 254) + ".com", out _), "over-long entry rejected");
    }

    private static void Allowlist_ParseGood()
    {
        Case("Allowlist_ParseGood");
        Check(WebPanelUrlPolicy.TryParseAllowlist("*.AcDreamWeave.com\r\nlocalhost\n", out var e), "parses");
        Eq(e.Length, 2, "entry count");
        Eq(e[0], "*.acdreamweave.com", "lower-cased wildcard");
        Eq(e[1], "localhost", "single-label host ok");
    }

    private static void BrowserSafe()
    {
        Case("BrowserSafe");
        Check(WebPanelUrlPolicy.IsBrowserSafe("https://example.com/a?b=c"), "https");
        Check(WebPanelUrlPolicy.IsBrowserSafe("http://example.com/"), "http");
        foreach (string u in new[] { "file:///C:/Windows/notepad.exe", "javascript:alert(1)", "ms-settings:network", "mailto:a@b.c", "/relative/path", "example.com", "https://u:p@example.com/", "https://u@example.com/", "", "  " })
            Check(!WebPanelUrlPolicy.IsBrowserSafe(u), "not safe: " + u);
        Check(!WebPanelUrlPolicy.IsBrowserSafe(null), "null not safe");
    }

    private static void Redact()
    {
        Case("Redact");
        string r = WebPanelUrlPolicy.Redact("https://www.acdreamweave.com/launch?code=SECRET&to=/Browse#f");
        Eq(r, "https://www.acdreamweave.com/launch", "query and fragment stripped");
        Check(!r.Contains("SECRET"), "secret absent");
        Eq(WebPanelUrlPolicy.Redact("https://user:pw@host.example:8443/p/q?x=1"), "https://host.example:8443/p/q", "userinfo dropped, port kept");
        Check(!WebPanelUrlPolicy.Redact("https://user:pw@host.example/p").Contains("pw"), "password absent");
        Eq(WebPanelUrlPolicy.Redact("not a url"), "<invalid url>", "garbage");
        Eq(WebPanelUrlPolicy.Redact(null), "<invalid url>", "null");
        Eq(WebPanelUrlPolicy.Redact(""), "<invalid url>", "empty");
        Check(!WebPanelUrlPolicy.Redact("mailto:secret@x.com").Contains("secret"), "non-hierarchical leaks nothing");
    }

    private static void Visibility()
    {
        Case("Visibility");
        // open, alive, !iconic, visible, !fgNull, fgPid, ourPid
        Eq(WebPanelWindowPolicy.Visible(true, true, false, true, false, 5, 5), (bool?)true, "same process shows");
        Eq(WebPanelWindowPolicy.Visible(true, true, false, true, false, 6, 5), (bool?)false, "other process hides");
        Eq(WebPanelWindowPolicy.Visible(true, true, false, true, true, 0, 5), (bool?)null, "null foreground keeps current");
        Eq(WebPanelWindowPolicy.Visible(true, true, true, true, false, 5, 5), (bool?)false, "iconic hides");
        Eq(WebPanelWindowPolicy.Visible(false, true, false, true, false, 5, 5), (bool?)false, "closed hides");
        Eq(WebPanelWindowPolicy.Visible(true, false, false, true, false, 5, 5), (bool?)false, "dead game hides");
        Eq(WebPanelWindowPolicy.Visible(true, true, false, false, false, 5, 5), (bool?)false, "invisible game hides");
        Eq(WebPanelWindowPolicy.Visible(false, true, false, true, true, 0, 5), (bool?)false, "closed beats null foreground");
        Eq(WebPanelWindowPolicy.Visible(true, true, true, true, true, 0, 5), (bool?)false, "iconic beats null foreground");
    }

    private static void Layout()
    {
        Case("Layout");
        var client = new PanelRect(100, 50, 1200, 800); // 1100 x 750
        var f = WebPanelWindowPolicy.Default(0, 0);
        Check(Math.Abs(f.W - 0.88) < 1e-9 && Math.Abs(f.H - 0.88) < 1e-9, "0 means 88 %");
        Check(Math.Abs(f.X - 0.06) < 1e-9 && Math.Abs(f.Y - 0.06) < 1e-9, "centered");
        var r = WebPanelWindowPolicy.Place(client, f);
        Eq(r.Width, 968, "88 % of 1100");
        Eq(r.Height, 660, "88 % of 750");
        Eq(r.Left - client.Left, client.Right - r.Right, "horizontally centered");
        Eq(r.Top - client.Top, client.Bottom - r.Bottom, "vertically centered");

        var full = WebPanelWindowPolicy.Default(120, 120);
        Check(full.W == 1.0 && full.H == 1.0 && full.X == 0 && full.Y == 0, "120 clamps to 100");
        var small = WebPanelWindowPolicy.Default(5, 5);
        Check(Math.Abs(small.W - 0.30) < 1e-9, "5 clamps to 30");
        Eq(WebPanelWindowPolicy.Place(client, full), client, "100 % fills the client");

        var tiny = new PanelRect(0, 0, 300, 200);
        Eq(WebPanelWindowPolicy.Place(tiny, WebPanelWindowPolicy.Default(0, 0)), tiny, "tiny client clamps to client");
        var mid = new PanelRect(0, 0, 600, 400);
        var m = WebPanelWindowPolicy.Place(mid, WebPanelWindowPolicy.Default(30, 30));
        Check(m.Width >= 480 && m.Height >= 320, "minimum size enforced");
        Check(m.Left >= mid.Left && m.Top >= mid.Top && m.Right <= mid.Right && m.Bottom <= mid.Bottom, "min size stays inside client");

        // Off-screen fractions stay inside.
        var off = WebPanelWindowPolicy.Place(client, new PanelFractions(0.95, 0.95, 0.5, 0.5));
        Check(off.Left >= client.Left && off.Top >= client.Top && off.Right <= client.Right && off.Bottom <= client.Bottom, "never outside client");

        // Round trip.
        var user = new PanelRect(300, 200, 1000, 700);
        var rt = WebPanelWindowPolicy.Place(client, WebPanelWindowPolicy.FromUserRect(client, user));
        Check(Math.Abs(rt.Left - user.Left) <= 1 && Math.Abs(rt.Top - user.Top) <= 1 &&
              Math.Abs(rt.Right - user.Right) <= 1 && Math.Abs(rt.Bottom - user.Bottom) <= 1, $"round trip within 1 px: {rt}");
        var wild = WebPanelWindowPolicy.FromUserRect(client, new PanelRect(-500, -500, 5000, 5000));
        Check(wild.X >= 0 && wild.Y >= 0 && wild.W <= 1 && wild.H <= 1 && wild.X + wild.W <= 1.000001, "FromUserRect clamps to 0..1");
    }

    // WebPanelApi's pure parts (WebPanelApiRules): the kill switch and shutdown refusals, the GetState Size check.
    private static void ApiRules()
    {
        Case("ApiRules");
        Eq(WebPanelApiRules.OpenRefusal(shuttingDown: false, enabled: true), 0, "enabled, running: go on");
        Eq(WebPanelApiRules.OpenRefusal(false, false), (int)WebPanelCode.Unavailable, "engine.json WebPanel false: Unavailable");
        Eq(WebPanelApiRules.OpenRefusal(true, true), (int)WebPanelCode.ShuttingDown, "shutting down: ShuttingDown");
        Eq(WebPanelApiRules.OpenRefusal(true, false), (int)WebPanelCode.ShuttingDown, "shutting down wins over the kill switch");
        Eq(WebPanelApiRules.CallRefusal(false), 0, "calls go on while running");
        Eq(WebPanelApiRules.CallRefusal(true), (int)WebPanelCode.ShuttingDown, "calls refused after shutdown");

        uint min = WebPanelApiRules.StateMinSize;
        Eq(min, 28u, "OffsetOf(Reserved) of the state struct (no pointer fields: 28 on x86 and x64)");
        Check(min > 0 && min < (uint)sizeof(WebPanelStateNative), "min size is inside the struct");
        Check(!WebPanelApiRules.StateArgsOk(null), "null state refused");
        WebPanelStateNative st = default;
        st.Size = min - 1;
        Check(!WebPanelApiRules.StateArgsOk(&st), "Size below OffsetOf(Reserved) refused");
        st.Size = min;
        Check(WebPanelApiRules.StateArgsOk(&st), "Size == OffsetOf(Reserved) accepted");
        st.Size = (uint)sizeof(WebPanelStateNative);
        Check(WebPanelApiRules.StateArgsOk(&st), "full Size accepted");

        st.Reserved[0] = 0xDEADBEEF;
        WebPanelApiRules.WriteState(&st, new WebPanelSnapshot(7, 3, 5, -9, 200, 4));
        Eq(st.Handle, 7, "handle written");
        Eq(st.Status, 3, "status written");
        Eq(st.Flags, 5u, "flags written");
        Eq(st.Error, -9, "error written");
        Eq(st.LastHttpStatus, 200, "http written");
        Eq(st.NavigationCount, 4u, "navigations written");
        Eq(st.Reserved[0], 0xDEADBEEFu, "Reserved untouched");
        Eq(WebPanelApiRules.NoPanel(9).Status, (int)WebPanelStatusCode.Superseded, "no panel yet: Superseded");
    }

    // WebPanelNativeArgs: the strings are copied out of the caller's buffers; bad arguments are BadArgs.
    private static void NativeArgs()
    {
        Case("NativeArgs");
        const string lad = @"C:\lad";
        Eq(WebPanelNativeArgs.TryBuildRequest(null, lad, "W", out _), (int)WebPanelCode.BadArgs, "null struct");

        int Build(string? url, string? allow, string? key, string? title, uint size, uint flags, out WebPanelRequest? req)
        {
            byte[] u = Z(url), a = Z(allow), k = Z(key), ti = Z(title);
            fixed (byte* pu = u, pa = a, pk = k, pt = ti)
            {
                var o = new WebPanelOpenNative
                {
                    Size = size, Flags = flags,
                    Url = url == null ? null : pu, Allowlist = allow == null ? null : pa,
                    ProfileKey = key == null ? null : pk, Title = title == null ? null : pt,
                };
                int rc = WebPanelNativeArgs.TryBuildRequest(&o, lad, "W", out req);
                Array.Fill(u, (byte)'Z'); Array.Fill(a, (byte)'Z'); // the caller's buffers die right after the call
                return rc;
            }
        }
        static byte[] Z(string? s) => s == null ? new byte[1] : Encoding.UTF8.GetBytes(s + "\0");
        uint full = (uint)sizeof(WebPanelOpenNative);

        Eq(Build("https://a.test/x?code=1", "a.test", "k", "T", full, 0xFF, out var r), 0, "good request");
        Eq(r?.Url, "https://a.test/x?code=1", "url copied before the buffer was overwritten");
        Eq(r?.Allowlist.Length, 1, "allowlist parsed");
        Eq(r?.Title, "T", "title copied");
        Eq(r?.Flags, 3u, "unknown flag bits dropped");
        Check(r != null && r.ProfileFolder.StartsWith(lad, StringComparison.Ordinal), "profile folder under LocalAppData");
        Eq(Build("https://a.test/", "a.test", null, null, full, 0, out r), 0, "null key and title");
        Eq(r?.Title, "", "null title is empty");
        Eq(Build("https://a.test/", "a.test", null, null, 31, 0, out _), (int)WebPanelCode.BadArgs, "Size below OffsetOf(Reserved)");
        Eq(Build(null, "a.test", null, null, full, 0, out _), (int)WebPanelCode.BadArgs, "no url");
        Eq(Build("", "a.test", null, null, full, 0, out _), (int)WebPanelCode.BadArgs, "empty url");
        Eq(Build("https://a.test/", null, null, null, full, 0, out _), (int)WebPanelCode.BadArgs, "no allowlist");
        Eq(Build("https://a.test/", "*.com", null, null, full, 0, out _), (int)WebPanelCode.BadArgs, "bad allowlist");
        Eq(Build("https://a.test/", "a.test", null, new string('t', 129), full, 0, out _), (int)WebPanelCode.BadArgs, "title over 128 bytes");
        Eq(Build("https://a.test/", "a.test", new string('k', 257), null, full, 0, out _), (int)WebPanelCode.BadArgs, "key over 256 bytes");
        Eq(Build("https://a.test/" + new string('p', 4096), "a.test", null, null, full, 0, out _), (int)WebPanelCode.BadArgs, "url over 4096 bytes");

        byte[] bad = { 0x68, 0xC3, 0x28, 0 }; // invalid UTF-8
        fixed (byte* pb = bad) Check(!WebPanelNativeArgs.TryReadUtf8(pb, 16, true, out _), "invalid UTF-8 refused");
    }

    private static void Profile()
    {
        Case("Profile");
        const string root = @"C:\Users\x\AppData\Local";
        string a = WebPanelProfile.Folder(root, "DreamWeave", "Alice");
        Eq(a, WebPanelProfile.Folder(root, "DreamWeave", "  ALICE "), "case/space-insensitive key");
        Check(a != WebPanelProfile.Folder(root, "DreamWeave", "Bob"), "different keys differ");
        Check(a != WebPanelProfile.Folder(root, "Other", "Alice"), "different worlds differ");
        string leaf = Path.GetFileName(a);
        Eq(leaf.Length, 32, "hex is 32 chars");
        Check(leaf.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), "lowercase hex");
        // SHA-256("alice") first 16 bytes.
        Eq(leaf, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("alice")), 0, 16).ToLowerInvariant(), "hash of lowered key");
        Eq(Path.GetFileName(WebPanelProfile.Folder(root, "W", "")), "default", "empty key");
        Eq(Path.GetFileName(WebPanelProfile.Folder(root, "W", null)), "default", "null key");

        string evil = WebPanelProfile.Folder(root, "Dream/Weave..", "k");
        Check(!evil.Contains(".."), "no dot-dot");
        Eq(Path.GetFileName(Path.GetDirectoryName(evil)!), "Dream_Weave__", "world sanitized");
        Eq(Path.GetFileName(Path.GetDirectoryName(WebPanelProfile.Folder(root, null, "k"))!), "_default", "null world");
        Eq(Path.GetFileName(Path.GetDirectoryName(WebPanelProfile.Folder(root, "  ", "k"))!), "_default", "blank world");
        Eq(Path.GetFileName(Path.GetDirectoryName(WebPanelProfile.Folder(root, new string('w', 100), "k"))!).Length, 64, "world capped at 64");
        Eq(Path.GetFileName(Path.GetDirectoryName(WebPanelProfile.Folder(root, @"..\..\x", "k"))!), "______x", "traversal neutralised");
        string full = Path.GetFullPath(evil);
        Check(full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "under localAppData");
        Check(evil.Contains(Path.Combine("RynthCore", "WebPanel")), "RynthCore\\WebPanel layout");
    }
}
