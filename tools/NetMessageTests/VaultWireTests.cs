namespace NetMessageTests;

using RynthCore.Engine.Net;

// Net/AelrynthVaultWire: the vault's "~ael1" mule lines (Aeshnidae.Mule's /mule-data).
internal static unsafe partial class Program
{
    private static void VaultWireTests()
    {
        const string A = "{\"o\":2147483649,\"w\":100,\"n\":\"Acid Assagai\",\"i\":100667000,\"u\":100667001,\"v\":100667002,\"s\":3,\"m\":100,\"y\":1,\"e\":1}";
        const string B = "{\"o\":2147483650,\"w\":101,\"n\":\"Pyreal\",\"i\":100667003,\"y\":8192,\"future\":\"ignored\"}";
        string page = "~ael1 {\"t\":\"mule\",\"ready\":true,\"page\":2,\"pages\":3,\"count\":57,\"max\":1000,\"items\":[" + A + "," + B + "]}";

        Case("vault wire: a page");
        Check(AelrynthVaultWire.IsMuleLine(page), "recognised as a mule line");
        Check(AelrynthVaultWire.TryParse(page, out VaultPage p), "parses");
        Check(p.Ready, "ready");
        Eq(p.Page, 2, "page"); Eq(p.Pages, 3, "pages"); Eq(p.Count, 57, "count"); Eq(p.Max, 1000, "max"); Eq(p.Items.Count, 2, "items");
        VaultItem a = p.Items[0];
        Eq(a.Id, 2147483649u, "id"); Eq(a.Name, "Acid Assagai", "name"); Eq(a.Icon, 100667000u, "icon"); Eq(a.Underlay, 100667001u, "underlay");
        Eq(a.Overlay, 100667002u, "overlay"); Eq(a.Stack, 3, "stack"); Eq(a.MaxStack, 100, "max stack"); Eq(a.ItemType, 1u, "item type"); Eq(a.Effects, 1u, "effects");
        Eq(p.Items[1].Stack, 1, "no stack field is a stack of one");
        Eq(p.Items[1].Underlay, 0u, "no underlay is none");

        Case("vault wire: not ready is not empty");
        Check(AelrynthVaultWire.TryParse("~ael1 {\"t\":\"mule\",\"ready\":false,\"why\":\"loading\"}", out VaultPage n), "parses");
        Check(!n.Ready && n.Why == "loading" && n.Items.Count == 0, "not ready, and says why");

        Case("vault wire: other lines");
        Check(!AelrynthVaultWire.IsMuleLine("~ael1 {\"t\":\"inspect\",\"ok\":true}"), "the Companion's line is not ours");
        Check(!AelrynthVaultWire.IsMuleLine("Welcome to Aelrynth"), "chat is not ours");
        Check(!AelrynthVaultWire.IsMuleLine(null), "null");
        Check(!AelrynthVaultWire.TryParse("~ael1 {\"t\":\"mule\",\"ready\":tru", out _), "damaged JSON is refused");
        Check(AelrynthVaultWire.IsUnknownCommand("Unknown command: mule-data"), "ACE's reply for a missing command");
        Check(!AelrynthVaultWire.IsUnknownCommand("Unknown command: mastery-data"), "another command's reply is not ours");

        Case("vault wire: a player's copy of a server line is not ours");
        Check(!AelrynthVaultWire.IsMuleLine("Bob says, \"" + page + "\""), "spoken data line");
        Check(!AelrynthVaultWire.IsMuleLine("Bob " + page), "emoted data line (no quote)");
        Check(!AelrynthVaultWire.TryParse("[Allegiance] Bob says, \"" + page + "\"", out _), "a channel's copy does not parse");
        Check(AelrynthVaultWire.IsMuleLine("  " + page), "leading whitespace is fine");
        Check(AelrynthVaultWire.TryParse("  " + page, out _), "and parses");
        Check(!AelrynthVaultWire.IsUnknownCommand("Bob tells you, \"Unknown command: mule-data\""), "a told refusal");
        Check(!AelrynthVaultWire.IsUnknownCommand("Unknown command: mule-data-extra"), "a longer command name");
        Check(AelrynthVaultWire.IsUnknownCommand("Unknown command: /mule-data"), "a slash before the name is allowed");
        Check(ChatOrigin.IsPlayerAuthored(ChatOrigin.Emote) && ChatOrigin.IsPlayerAuthored(ChatOrigin.Tell), "emotes and tells are players'");
        Check(!ChatOrigin.IsPlayerAuthored(0) && !ChatOrigin.IsPlayerAuthored(0x0F), "Broadcast and Help (ACE's replies) are not");
        Eq(AelrynthVaultWire.PageCommand(1), "@mule-data", "first page is the bare command");
        Eq(AelrynthVaultWire.PageCommand(3), "@mule-data 3", "later pages say which");

        Case("vault wire: absurd sizes are bounded");
        string many = "~ael1 {\"t\":\"mule\",\"ready\":true,\"page\":1,\"pages\":99999,\"count\":1,\"max\":1,\"items\":[" + string.Join(",", System.Linq.Enumerable.Repeat(B, 500)) + "]}";
        Check(AelrynthVaultWire.TryParse(many, out VaultPage big), "parses");
        Check(big.Items.Count <= 200 && big.Pages <= 80, "items and pages are capped");
    }
}
