// ============================================================================
//  RynthCore.Headless - Host/Actions/ChatLineRouter.cs
//  Where a chat-bar line goes once no plugin has eaten it (the engine's
//  ChatCommandDispatcher, minus AC's chat bar):
//    bare text, and '@' server commands      -> Talk (0x0015) as typed
//    /say /s, /emote, /me, /tell /t Name, msg -> Talk, Emote (0x01DF), SoulEmote (0x01E1),
//                                               Tell (0x005D)
//    any other "/command args"                -> Talk "@command args": ACE runs Talk text
//                                               that starts with '@' as a server command
//                                               (GameActionTalk), and a server command typed
//                                               with '/' reaches it that way from a retail
//                                               client too
//    chat channels (/a /f /g /trade ...), /r /retell, the engine's /rc
//                                             -> not available headless (refused)
// ============================================================================

namespace RynthCore.Headless.Host.Actions;

public enum ChatRouteKind { None, Say, Emote, SoulEmote, Tell, Unsupported }

public readonly record struct ChatRoute(ChatRouteKind Kind, string Text, string Target = "", string Why = "");

public static class ChatLineRouter
{
    // AC's own channel and reply commands: they need AC's chat rooms or its tell memory.
    private static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "f", "g", "p", "v", "c", "m", "general", "trade", "lfg", "roleplay", "society", "olthoi",
        "allegiance", "fellowship", "patron", "vassals", "covassals", "monarch",
        "r", "reply", "rt", "retell", "rc",
    };

    public static ChatRoute Route(string? line)
    {
        string t = (line ?? "").Trim();
        if (t.Length == 0) return new ChatRoute(ChatRouteKind.None, "");
        if (t[0] != '/') return new ChatRoute(ChatRouteKind.Say, t);

        int space = t.IndexOf(' ');
        string cmd = (space < 0 ? t.Substring(1) : t.Substring(1, space - 1)).ToLowerInvariant();
        string rest = space < 0 ? "" : t.Substring(space + 1).Trim();
        switch (cmd)
        {
            case "":
                return new ChatRoute(ChatRouteKind.None, "");
            case "say":
            case "s":
                return rest.Length > 0 ? new ChatRoute(ChatRouteKind.Say, rest) : new ChatRoute(ChatRouteKind.None, "");
            case "emote":
            case "em":
            case "e":
                return rest.Length > 0 ? new ChatRoute(ChatRouteKind.Emote, rest) : new ChatRoute(ChatRouteKind.None, "");
            case "me":
                return rest.Length > 0 ? new ChatRoute(ChatRouteKind.SoulEmote, rest) : new ChatRoute(ChatRouteKind.None, "");
            case "tell":
            case "t":
            {
                int comma = rest.IndexOf(',');
                string name = comma > 0 ? rest.Substring(0, comma).Trim() : "";
                string msg = comma > 0 ? rest.Substring(comma + 1).Trim() : "";
                return name.Length > 0 && msg.Length > 0
                    ? new ChatRoute(ChatRouteKind.Tell, msg, name)
                    : new ChatRoute(ChatRouteKind.Unsupported, t, Why: "usage: /tell Name, message");
            }
        }
        if (Unsupported.Contains(cmd))
            return new ChatRoute(ChatRouteKind.Unsupported, t, Why: $"/{cmd} isn't available headless");
        return new ChatRoute(ChatRouteKind.Say, "@" + t.Substring(1));
    }
}
