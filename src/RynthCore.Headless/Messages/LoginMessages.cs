// ============================================================================
//  RynthCore.Headless - Messages/LoginMessages.cs
//  Login, character select, enter-world and logout messages, both directions.
// ============================================================================

using RynthCore.Headless.Net;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Messages;

internal static class Opcode
{
    // client -> server
    public const uint CharacterLogOff = 0xF653;            // also the server's "logged off" reply
    public const uint CharacterEnterWorld = 0xF657;
    public const uint CharacterEnterWorldRequest = 0xF7C8;
    public const uint GameAction = 0xF7B1;
    public const uint DddInterrogationResponse = 0xF7E6;

    // server -> client
    public const uint ObjectCreate = 0xF745;
    public const uint PlayerCreate = 0xF746;
    public const uint ObjectDelete = 0xF747;
    public const uint PlayerTeleport = 0xF751;
    public const uint CharacterList = 0xF658;
    public const uint CharacterError = 0xF659;
    public const uint GameEvent = 0xF7B0;
    public const uint AccountBoot = 0xF7DC;
    public const uint EnterWorldServerReady = 0xF7DF;
    public const uint ServerMessage = 0xF7E0;
    public const uint ServerName = 0xF7E1;
    public const uint DddInterrogation = 0xF7E5;
    public const uint DddEnd = 0xF7EA;
    public const uint HearSpeech = 0x02BB;
    public const uint HearRangedSpeech = 0x02BC;
    public const uint HearEmote = 0x01E0;
    public const uint HearSoulEmote = 0x01E2;
}

internal static class GameActionType
{
    public const uint Talk = 0x0015;
    public const uint LoginComplete = 0x00A1;
}

internal sealed record CharacterEntry(uint Id, string Name, uint DeleteSeconds);

internal sealed record CharacterListData(IReadOnlyList<CharacterEntry> Characters, uint Slots, string Account, bool TurbineChat);

internal static class LoginMessages
{
    public const string ClientVersion = "1802";
    public const uint AuthAccountPassword = 2;

    /// <summary>The LoginRequest optional section: version, auth type, time, account, password (plain text).</summary>
    public static byte[] LoginRequest(string account, string password, uint unixTime)
    {
        var rest = new WireWriter(64);
        rest.U32(AuthAccountPassword).U32(0).U32(unixTime).String16L(account).String16L("").String32L(password);
        var w = new WireWriter(80);
        w.String16L(ClientVersion).U32((uint)rest.Length).Bytes(rest.Span);
        return w.ToArray();
    }

    public static byte[] EnterWorldRequest() => new WireWriter(4).U32(Opcode.CharacterEnterWorldRequest).ToArray();

    public static byte[] EnterWorld(uint characterId, string account)
        => new WireWriter(32).U32(Opcode.CharacterEnterWorld).U32(characterId).String16L(account).ToArray();

    /// <summary>"We have no dat iterations to report": the server then ends the DDD exchange without patching.</summary>
    public static byte[] DddInterrogationResponse()
        => new WireWriter(12).U32(Opcode.DddInterrogationResponse).U32(1).I32(0).ToArray();

    public static byte[] LogOff() => new WireWriter(4).U32(Opcode.CharacterLogOff).ToArray();

    public static byte[] GameAction(uint sequence, uint type, ReadOnlySpan<byte> payload = default)
        => new WireWriter(12 + payload.Length).U32(Opcode.GameAction).U32(sequence).U32(type).Bytes(payload).ToArray();

    public static byte[] TalkPayload(string text) => new WireWriter(8 + text.Length).String16L(text).ToArray();

    public static bool TryParseCharacterList(ReadOnlySpan<byte> body, out CharacterListData list)
    {
        list = null!;
        var r = new NetReader(body);
        if (!r.U32(out _) || !r.ListCount(out int n, 8)) return false;
        var chars = new List<CharacterEntry>(n);
        for (int i = 0; i < n; i++)
        {
            if (!r.U32(out uint id) || !r.String16L(out string name) || !r.U32(out uint del)) return false;
            chars.Add(new CharacterEntry(id, name, del));
        }
        if (!r.U32(out _) || !r.U32(out uint slots) || !r.String16L(out string account) || !r.U32(out uint turbine))
            return false;
        list = new CharacterListData(chars, slots, account, turbine != 0);
        return true;
    }

    public static bool TryParseServerName(ReadOnlySpan<byte> body, out int online, out int max, out string name)
    {
        online = max = 0;
        name = "";
        var r = new NetReader(body);
        return r.I32(out online) && r.I32(out max) && r.String16L(out name);
    }

    /// <summary>0xF7E0: text, then a ChatMessageType.</summary>
    public static bool TryParseServerMessage(ReadOnlySpan<byte> body, out string text, out uint chatType)
    {
        chatType = 0;
        var r = new NetReader(body);
        return r.String16L(out text) && r.U32(out chatType);
    }
}
