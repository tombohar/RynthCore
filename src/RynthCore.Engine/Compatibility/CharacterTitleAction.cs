// ============================================================================
//  RynthCore.Engine - Compatibility/CharacterTitleAction.cs
//  Choosing the character's displayed title (game action 0x002C TitleSet), as
//  the retail Character panel's Titles page does with its button (element
//  0x10000535 -> CM_Social::Event_SetDisplayCharacterTitle(titleId)).
//  "/rc title <id>" calls it; RynthDreamWeave's Skills panel (its Titles tab)
//  sends that command, so no plugin API is needed (2026-11-07).
//
//  The sender, read off acclient.exe 2026-11-07: 0x006A6680,
//    bool __cdecl CM_Social::Event_SetDisplayCharacterTitle(uint titleId)
//  prologue 83 EC 0C 53 56 57 E8 (as the combat senders), and at +0x5A
//  "mov dword ptr [edx], 0x2C" (C7 02 2C 00 00 00) then the id. (The Ghidra
//  index also names 0x0049AA1D so; that is a mislabelled thunk.) The address
//  is used only when those bytes are there; otherwise the 0x2C store is looked
//  for with the same prologue before it, and used only when exactly one fits.
//
//  A game-action send (AC blob + send queue): AC's main thread only. A chat
//  command runs inside AC's outgoing-chat hook, which is that thread.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class CharacterTitleAction
{
    private const int KnownVa = 0x006A6680;
    private const int StoreOffset = 0x5A;
    private static readonly byte[] Prologue = [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8];
    private static readonly byte[] TitleSetStore = [0xC7, 0x02, 0x2C, 0x00, 0x00, 0x00];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SetTitleDelegate(uint titleId);

    private static SetTitleDelegate? _send;
    private static bool _probed;
    private static string _status = "not looked for yet";

    public static string Status => _status;

    private static void Probe()
    {
        _probed = true;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _status = "acclient.exe not available";
            return;
        }
        byte[] bytes = text.Bytes;
        int known = KnownVa - text.TextBaseVa;
        int func = -1;
        if (known >= 0 && known + StoreOffset + TitleSetStore.Length <= bytes.Length
            && PatternScanner.VerifyBytes(bytes, known, Prologue)
            && PatternScanner.VerifyBytes(bytes, known + StoreOffset, TitleSetStore))
            func = known;
        else
        {
            // Another client build: every 0x2C store with this prologue close before it; exactly one.
            int found = -1, count = 0;
            for (int at = PatternScanner.FindPattern(bytes, TitleSetStore); at >= 0;
                 at = PatternScanner.FindPatternInRegion(bytes, TitleSetStore, at + 1, bytes.Length))
            {
                int p = PatternScanner.FindPrologueBefore(bytes, at, Prologue, 0x80);
                if (p >= 0 && at - p == StoreOffset) { found = p; count++; }
            }
            if (count == 1) func = found;
            else
            {
                _status = count == 0 ? "the title sender (0x2C) wasn't found in this client" : $"{count} candidates for the title sender; none used";
                RynthLog.Compat("CharacterTitleAction: " + _status);
                return;
            }
        }
        int va = text.TextBaseVa + func;
        _send = Marshal.GetDelegateForFunctionPointer<SetTitleDelegate>(new IntPtr(va));
        _status = $"CM_Social::Event_SetDisplayCharacterTitle at 0x{va:X8}";
        RynthLog.Compat("CharacterTitleAction: " + _status);
    }

    /// <summary>Sends TitleSet for <paramref name="titleId"/>; why not, or null when sent. AC's main thread.</summary>
    public static string? SetTitle(uint titleId)
    {
        if (titleId == 0) return "no title id";
        if (!MainThreadGuard.IsOnMainThread()) return "not on AC's thread";
        if (!_probed) Probe();
        if (_send == null) return _status;
        try
        {
            return _send(titleId) ? null : "AC didn't send it";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
