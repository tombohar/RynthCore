// ============================================================================
//  RynthCore.Engine - Net/ChatOrigin.cs
//  Telling a server line from a player's imitation of one.
//
//  The engine eats its own "~ael1 " data replies (/mastery-data, /mule-data)
//  and ACE's "Unknown command: ..." refusals before AC draws them. Another
//  player can type the same text, so the readers need both checks here:
//    - the chat type is not one a player writes in (ACE ChatMessageType, which
//      is the client's eChatTypes: ACE.Entity/Enum/ChatMessageType.cs);
//    - the line STARTS with the marker. The client puts the speaker in front
//      of player text ("Name says, ...", "Name tells you, ...", "Name waves"),
//      so a player's copy never starts with it.
//  Pure code (no engine state); compiled into tools/NetMessageTests.
//  Plugins have the same rules in RynthSuite Plugins/Shared/ChatSource.cs.
// ============================================================================

using System;

namespace RynthCore.Engine.Net;

internal static class ChatOrigin
{
    // Chat types whose text another player writes.
    public const uint Speech = 0x02;
    public const uint Tell = 0x03;
    public const uint OutgoingTell = 0x04;
    public const uint Channel = 0x08;
    public const uint ChannelSend = 0x09;
    public const uint Social = 0x0A;
    public const uint SocialSend = 0x0B;
    public const uint Emote = 0x0C;
    public const uint Allegiance = 0x12;
    public const uint Fellowship = 0x13;

    /// <summary>True when a line of this chat type is a player's words.</summary>
    public static bool IsPlayerAuthored(uint chatType) => chatType switch
    {
        Speech or Tell or OutgoingTell or Channel or ChannelSend
            or Social or SocialSend or Emote or Allegiance or Fellowship => true,
        _ => false,
    };

    /// <summary>The index just past <paramref name="line"/>'s leading whitespace.</summary>
    public static int ContentStart(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    /// <summary>True when <paramref name="line"/>, past leading whitespace, starts with <paramref name="marker"/>.</summary>
    public static bool StartsWithMarker(string? line, string marker, StringComparison comparison = StringComparison.Ordinal)
        => line != null && line.AsSpan(ContentStart(line)).StartsWith(marker.AsSpan(), comparison);
}
