// ============================================================================
//  RynthCore.Engine - Net/AelrynthVaultWire.cs
//  The Aelrynth account vault's wire format (the Aeshnidae.Mule mod's /mule-data).
//
//  The vault is storage the retail client is never shown, so the only way to see inside is to ask:
//  "/mule-data [page]" answers ONE chat line a page, "~ael1 " + JSON:
//
//    {"t":"mule","ready":true,"page":1,"pages":3,"count":57,"max":1000,"items":[
//       {"o":objectId,"w":wcid,"n":name,"i":icon,"u":underlay,"v":overlay,"s":stack,"m":maxStack,"y":itemType,"e":uiEffects}, ...]}
//    {"t":"mule","ready":false,"why":"loading"}     (still loading on the server: never an empty vault)
//
//  A field the reader does not know is ignored, so the mod can add fields. Pure code (no engine state);
//  compiled into tools/NetMessageTests (with Net/ChatOrigin.cs).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RynthCore.Engine.Net;

/// <summary>One item in the vault, with what an icon needs. Immutable once parsed.</summary>
internal sealed class VaultItem
{
    public uint Id, Wcid, Icon, Underlay, Overlay, ItemType, Effects;
    public int Stack = 1, MaxStack;
    public string Name = string.Empty;
}

/// <summary>One page of the vault as the server sent it.</summary>
internal sealed class VaultPage
{
    public bool Ready;
    /// <summary>Why a not-ready reply was not ready ("loading", "closed", "fault", "error").</summary>
    public string Why = string.Empty;
    public int Page = 1, Pages = 1, Count, Max;
    public List<VaultItem> Items = new();
}

internal static class AelrynthVaultWire
{
    /// <summary>The command as the engine sends it: talk text starting with @, which ACE runs as the command (ChatCommandDispatcher.SendServerCommand).</summary>
    public const string Command = "@mule-data";
    public const string CommandName = "mule-data";
    private const string Prefix = "~ael1 ";
    private const int MaxItemsPerPage = 200, MaxPages = 80;

    /// <summary>The command for a page (1 = the bare command).</summary>
    public static string PageCommand(int page) => page <= 1 ? Command : Command + " " + page.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A "~ael1" data line whose kind is "mule" (another mod's data line is not ours). The prefix
    /// must start the line: a player's copy has their name in front (ChatOrigin).
    /// </summary>
    public static bool IsMuleLine(string? line)
    {
        if (!ChatOrigin.StartsWithMarker(line, Prefix)) return false;
        return line!.IndexOf("\"t\":\"mule\"", ChatOrigin.ContentStart(line), StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// ACE's reply to a command it does not have, "Unknown command: mule-data" (GameActionTalk names
    /// the bare command; a leading / or @ is allowed). Anchored at the line's start.
    /// </summary>
    public static bool IsUnknownCommand(string? line)
    {
        const string marker = "Unknown command:";
        if (!ChatOrigin.StartsWithMarker(line, marker, StringComparison.OrdinalIgnoreCase)) return false;
        string named = line!.Substring(ChatOrigin.ContentStart(line) + marker.Length).Trim().TrimStart('/', '@');
        return string.Equals(named, CommandName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The page in a mule line; false when it is not one or is too damaged to read.</summary>
    public static bool TryParse(string? line, out VaultPage page)
    {
        page = new VaultPage();
        if (!IsMuleLine(line)) return false;
        try
        {
            int at = ChatOrigin.ContentStart(line!) + Prefix.Length;
            using JsonDocument doc = JsonDocument.Parse(line.AsMemory(at));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            page.Ready = root.TryGetProperty("ready", out JsonElement r) && r.ValueKind == JsonValueKind.True;
            page.Why = Str(root, "why");
            if (!page.Ready) return true;

            page.Page = Math.Max(1, Int(root, "page", 1));
            page.Pages = Math.Min(Math.Max(1, Int(root, "pages", 1)), MaxPages);
            page.Count = Math.Max(0, Int(root, "count", 0));
            page.Max = Math.Max(0, Int(root, "max", 0));
            if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in items.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (page.Items.Count >= MaxItemsPerPage) break;
                    page.Items.Add(new VaultItem
                    {
                        Id = UInt(e, "o"), Wcid = UInt(e, "w"), Name = Str(e, "n"),
                        Icon = UInt(e, "i"), Underlay = UInt(e, "u"), Overlay = UInt(e, "v"),
                        Stack = Math.Max(1, Int(e, "s", 1)), MaxStack = Int(e, "m", 0),
                        ItemType = UInt(e, "y"), Effects = UInt(e, "e"),
                    });
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static uint UInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint u) ? u : 0u;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
