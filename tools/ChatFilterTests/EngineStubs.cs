// The two engine pieces ChatRouting.cs calls, without the engine (same stand-ins as
// tools/ChatHistoryBench/Stubs.cs): RynthLog.UI and ChatModel's MentionEligible / BumpFiltersVersion.

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
    }
}
