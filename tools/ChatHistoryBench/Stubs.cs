// The two engine pieces ChatRouting.cs calls, without the engine. ChatModel's are copies
// of the real ones in src/RynthCore.Engine/UI/Data/ChatData.cs (MentionEligible, and
// LineInTab / LineVisible, which the chat face filters with).

namespace RynthCore.Engine.Compatibility
{
    internal static class RynthLog
    {
        public static int Lines;
        public static void UI(string message) { Lines++; }
    }
}

namespace RynthCore.Engine.UI.Data
{
    internal static class ChatModel
    {
        public static int FilterBumps;

        internal static bool MentionEligible(string channel, int chatType) =>
            (channel == "Chat" || channel == "Channels") && chatType is not (4 or 9 or 11);

        internal static void BumpFiltersVersion() => FilterBumps++;

        public static bool LineInTab(ChatLine line, string tab) => line.Route.ShowsIn(tab, line.Channel);

        public static bool LineVisible(ChatLine line, string tab, string search) =>
            LineInTab(line, tab)
            && (search.Length == 0 || line.FormattedText.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
