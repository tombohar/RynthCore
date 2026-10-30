// ============================================================================
//  RynthCore.PluginSdk - Net/ServerMessage.cs
//  One reassembled server-to-client message, as RynthPluginOnServerMessage gets it.
//
//  Plugin side (API v78+):
//    1. In Init (or Tick), tell the engine what you want:
//         Host.SetServerMessageInterest(
//             new uint[] { ServerOpcode.HearEmote, ServerOpcode.HearSoulEmote },
//             new uint[] { GameEventType.FellowshipFullUpdate, GameEventType.FriendsUpdate });
//    2. Export the callback (NativeAOT plugin):
//         [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnServerMessage",
//                               CallConvs = new[] { typeof(CallConvCdecl) })]
//         static unsafe void OnServerMessage(uint opcode, byte* data, int length)
//         {
//             var msg = ServerMessage.FromCallback(opcode, data, length);
//             if (msg.IsGameEvent && msg.EventType == GameEventType.FellowshipFullUpdate
//                 && FellowshipFullUpdate.TryParse(msg.EventBody, out var f)) { ... }
//         }
//  `data` is the message after its u32 opcode and is valid only during the call; copy
//  what you keep. Messages arrive on the plugin pump, in the order they finished
//  arriving, possibly before the client itself has applied them.
// ============================================================================

using System;
using System.Buffers.Binary;

namespace RynthCore.PluginSdk.Net;

/// <summary>A server message: its opcode and the bytes after the opcode. For game events (0xF7B0) also the event header.</summary>
public readonly ref struct ServerMessage
{
    public const uint GameEventOpcode = 0xF7B0;

    public uint Opcode { get; }

    /// <summary>The bytes after the u32 opcode.</summary>
    public ReadOnlySpan<byte> Body { get; }

    public ServerMessage(uint opcode, ReadOnlySpan<byte> body)
    {
        Opcode = opcode;
        Body = body;
    }

    /// <summary>Wraps RynthPluginOnServerMessage's arguments (no copy).</summary>
    public static unsafe ServerMessage FromCallback(uint opcode, byte* data, int length)
        => new(opcode, data != null && length > 0 ? new ReadOnlySpan<byte>(data, length) : ReadOnlySpan<byte>.Empty);

    /// <summary>From a whole message as it is on the wire (u32 opcode first).</summary>
    public static bool TryFromWire(ReadOnlySpan<byte> message, out ServerMessage m)
    {
        if (message.Length < 4)
        {
            m = default;
            return false;
        }
        m = new ServerMessage(BinaryPrimitives.ReadUInt32LittleEndian(message), message.Slice(4));
        return true;
    }

    /// <summary>True for a game event (opcode 0xF7B0) with its 12-byte event header.</summary>
    public bool IsGameEvent => Opcode == GameEventOpcode && Body.Length >= 12;

    /// <summary>Game event: the object it is for (the player).</summary>
    public uint EventObjectId => IsGameEvent ? BinaryPrimitives.ReadUInt32LittleEndian(Body) : 0;

    /// <summary>Game event: the server's per-player game event sequence.</summary>
    public uint EventSequence => IsGameEvent ? BinaryPrimitives.ReadUInt32LittleEndian(Body.Slice(4)) : 0;

    /// <summary>Game event: its type (see <see cref="GameEventType"/>); 0 for other messages.</summary>
    public uint EventType => IsGameEvent ? BinaryPrimitives.ReadUInt32LittleEndian(Body.Slice(8)) : 0;

    /// <summary>Game event: the bytes after the event type.</summary>
    public ReadOnlySpan<byte> EventBody => IsGameEvent ? Body.Slice(12) : ReadOnlySpan<byte>.Empty;
}

/// <summary>Top-level server message opcodes the SDK parses (and a few landmarks).</summary>
public static class ServerOpcode
{
    public const uint HearEmote = 0x01E0;
    public const uint HearSoulEmote = 0x01E2;
    public const uint HearSpeech = 0x02BB;
    public const uint HearRangedSpeech = 0x02BC;
    public const uint GameEvent = 0xF7B0;
    /// <summary>Pass in the opcode list to get every message.</summary>
    public const uint All = 0xFFFFFFFF;
}

/// <summary>Game event types (inside opcode 0xF7B0) the SDK parses.</summary>
public static class GameEventType
{
    public const uint PopUpString = 0x0004;
    public const uint FriendsUpdate = 0x0021;
    public const uint FellowshipQuit = 0x00A3;
    public const uint FellowshipDismiss = 0x00A4;
    public const uint ChannelBroadcast = 0x0147;
    public const uint ConfirmationRequest = 0x0274;
    public const uint AllegianceLoginNotification = 0x027A;
    public const uint HearDirectSpeech = 0x02BD;
    public const uint FellowshipFullUpdate = 0x02BE;
    public const uint FellowshipDisband = 0x02BF;
    public const uint FellowshipUpdateFellow = 0x02C0;
    public const uint TransientString = 0x02EB;
}
