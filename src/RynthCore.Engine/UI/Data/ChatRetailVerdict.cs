// ============================================================================
//  RynthCore.Engine - UI/Data/ChatRetailVerdict.cs
//  Whether RynthChat's Hide rules / standard filters keep an incoming line out of
//  AC's own chat window too (ChatModel.ShouldHideInRetail, asked from the plugin
//  pump before a held line's verdict). The pure part, so tools/ChatFilterTests can
//  run the real router over it: which channel the RynthChat plugin files the line
//  under, and whether the rules hide it.
//
//  Idea from Silentkelpie (rynth PR 6, 2026.10.16.23). The channel order follows the
//  plugin's ChatBuffer.Add: RynthAi item info, then "[Rynth..." output, then a
//  [General]/[Trade]-style tag, then the chat type.
// ============================================================================

using System;

namespace RynthCore.Engine.UI.Data;

internal static class ChatRetailVerdict
{
    /// <summary>RynthAi's item info prefix (RynthChat's ChatClassifier.IsItemInfo): the Items tab.</summary>
    public const string ItemInfoPrefix = "[RynthAi Item] ";

    /// <summary>
    /// The channel tab the RynthChat plugin files an incoming line under, so a rule held to a
    /// channel ("chan:Channels") sees the same channel for the retail verdict as for RynthChat.
    /// </summary>
    public static string ChannelFor(string line, int chatType)
    {
        if (line.StartsWith(ItemInfoPrefix, StringComparison.Ordinal)) return "Items";
        if (line.StartsWith("[Rynth", StringComparison.OrdinalIgnoreCase)) return "Rynth";
        if (IsChannelTagged(line)) return "Channels";
        return ChannelForType(chatType);
    }

    /// <summary>The channel tab for an AC ChatMessageType alone.</summary>
    public static string ChannelForType(int chatType) => (uint)chatType switch
    {
        0x02 or 0x03 or 0x04 or 0x0A or 0x0B or 0x0C or 0x12 or 0x13 => "Chat",
        // 0x08/0x09 = retail channel; 0x1C (28) = ILT/ACECustom tagged [Trade]/[General] lines.
        0x08 or 0x09 or 0x1C => "Channels",
        0x00 or 0x05 or 0x0D or 0x14 or 0x17 or 0x18 or 0x19 or 0x1F => "System",
        0x06 or 0x07 or 0x11 or 0x15 or 0x16 => "Combat",
        _ => "Other",
    };

    /// <summary>The RynthChat plugin's channel tags (ChatClassifier.IsChannelTagged): "[Trade] ..." etc.</summary>
    public static bool IsChannelTagged(string text) =>
        text.Length > 0 && text[0] == '['
        && (text.StartsWith("[General]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Trade]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[LFG]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Roleplay]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Society]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Celestial Hand]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Eldrytch Web]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Radiant Blood]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Admin]", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="router"/> (the rules, then the ticked standard filters) hides the
    /// line. A rule or filter that only moves or copies a line to a tab is not a hide: that line
    /// still prints in AC's window.
    /// </summary>
    public static bool IsHidden(ChatRouter router, string line, int chatType) =>
        router.Evaluate(line, ChannelFor(line, chatType), chatType, null).Hidden;
}
