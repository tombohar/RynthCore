using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class CharacterManagementHooks
{
    private const int UIFlowInstanceVa = 0x0083E72C;
    private const int PlayerSystemVa = 0x0087119C;
    private const int UIFlowCurModeOffset = 0x8C;
    private const int UIFlowDataOffset = 0x98;
    private const int UIPersistantDataCharacterSetOffset = 0x04;
    private const int CharacterManagementUI = 0x1000000A;
    private const int GamePlayUI = 0x10000008;
    private const int MaxCharacterSlots = 20;

    private const int UIFlowGetPersistantDataVa = 0x0051DFB0;
    private const int GetPlayerSystemVa = 0x0055E1D0;
    private const int LogOnCharacterVa = 0x00560600;
    private const int CharacterSetGetIdentityVa = 0x004E8B20;
    private const int CharacterSetGetNameVa = 0x004FE980;
    private const int CharacterSetGetGidVa = 0x004FE9B0;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr UIFlowGetPersistantDataDelegate(IntPtr uiFlowPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetPlayerSystemDelegate();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte LogOnCharacterDelegate(IntPtr playerSystemPtr, uint avatarId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr CharacterSetGetIdentityDelegate(IntPtr charSetPtr, int index);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr CharacterSetGetNameDelegate(IntPtr charSetPtr, int index);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint CharacterSetGetGidDelegate(IntPtr charSetPtr, int index);

    private static readonly object BindLock = new();
    private static bool _bindAttempted;
    private static bool _bound;
    private static string _statusMessage = "Not bound.";
    private static UIFlowGetPersistantDataDelegate? _uiFlowGetPersistantData;
    private static GetPlayerSystemDelegate? _getPlayerSystem;
    private static LogOnCharacterDelegate? _logOnCharacter;
    private static CharacterSetGetIdentityDelegate? _characterSetGetIdentity;
    private static CharacterSetGetNameDelegate? _characterSetGetName;
    private static CharacterSetGetGidDelegate? _characterSetGetGid;

    // ── Pattern-resolved binding (1a hardening, 2026-06-05) ─────────────
    // The *Va consts above are FALLBACKs; these signatures (verified unique + landing exactly
    // at the VA offline via tools/pe_pattern.py) are the source of truth, surviving AC-patch /
    // ACE-rebuild drift. GetName/GetGid are sibling accessors differing only in a trailing
    // field offset (0x08 vs 0x04) — pinned literally to stay unique.
    private static readonly byte?[] PatUIFlowGetPersistantData = [ 0x8B, 0x81, 0x98, 0x00, 0x00, 0x00, 0xC3, 0x90 ];
    private static readonly byte?[] PatGetPlayerSystem = [ 0xA1, 0x9C, 0x11, 0x87, 0x00, 0xC3, 0x90, 0x90 ];
    private static readonly byte?[] PatLogOnCharacter = [ 0x56, 0x8B, 0xF1, 0x8A, 0x86, 0x10, 0x02, 0x00 ];
    private static readonly byte?[] PatCharacterSetGetIdentity = [ 0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x14, 0x73, 0x0F ];
    private static readonly byte?[] PatCharacterSetGetName = [ 0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x14, 0x73, 0x13, 0x8B, 0x49, 0x0C, 0xC1, 0xE0, 0x04, 0x03, 0xC1, 0x8B, 0x48, 0x04, 0x85, 0xC9, 0x74, 0x04, 0x85, 0xC0, 0x75, 0x05, 0x33, 0xC0, 0xC2, 0x04, 0x00, 0x8B, 0x40, 0x08 ];
    private static readonly byte?[] PatCharacterSetGetGid = [ 0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x14, 0x73, 0x13, 0x8B, 0x49, 0x0C, 0xC1, 0xE0, 0x04, 0x03, 0xC1, 0x8B, 0x48, 0x04, 0x85, 0xC9, 0x74, 0x04, 0x85, 0xC0, 0x75, 0x05, 0x33, 0xC0, 0xC2, 0x04, 0x00, 0x8B, 0x40, 0x04 ];

    private static T? Bind<T>(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa) where T : Delegate
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return r.Success ? Marshal.GetDelegateForFunctionPointer<T>(r.Address) : null;
    }

    public static string StatusMessage => _statusMessage;

    public static bool TryGetCurrentMode(out int mode)
    {
        mode = 0;
        IntPtr uiFlowPtr = GetUiFlowPointer();
        if (uiFlowPtr == IntPtr.Zero)
            return false;

        mode = Marshal.ReadInt32(IntPtr.Add(uiFlowPtr, UIFlowCurModeOffset));
        return true;
    }

    public static bool TryLogOnCharacter(string targetCharacter, out string matchedCharacter, out uint avatarId, out string status)
        => TryLogOnCharacter(targetCharacter, out matchedCharacter, out avatarId, out _, out status);

    /// <summary>
    /// Same as the 4-arg overload, but also reports the matched character's
    /// slot index in AC's native CharacterSet (-1 when no match was found).
    /// The slot index is what the click fallback in CharacterCaptureHooks
    /// needs to compute the y-offset for the character-list double-click.
    /// </summary>
    public static bool TryLogOnCharacter(string targetCharacter, out string matchedCharacter, out uint avatarId, out int slotIndex, out string status)
    {
        matchedCharacter = string.Empty;
        avatarId = 0;
        slotIndex = -1;

        if (string.IsNullOrWhiteSpace(targetCharacter))
        {
            status = "No target character requested.";
            return false;
        }

        if (!EnsureBound())
        {
            status = _statusMessage;
            return false;
        }

        bool hasMode = TryGetCurrentMode(out int mode);
        if (hasMode && mode == GamePlayUI)
        {
            status = "Client is already in GamePlayUI.";
            return false;
        }

        IntPtr charSetPtr = GetCharacterSetPointer();
        if (charSetPtr == IntPtr.Zero)
        {
            status = hasMode
                ? mode == CharacterManagementUI
                    ? "CharacterManagementUI is active, but the character set is not available yet."
                    : $"Current UI mode is 0x{mode:X8}; character set is not available yet."
                : "UIFlow/character set is not available yet.";
            return false;
        }

        var availableCharacters = new List<string>();
        for (int index = 0; index < MaxCharacterSlots; index++)
        {
            IntPtr identityPtr = _characterSetGetIdentity!(charSetPtr, index);
            if (identityPtr == IntPtr.Zero)
                continue;

            IntPtr namePtr = _characterSetGetName!(charSetPtr, index);
            string? candidateName = ReadAnsiString(namePtr);
            uint candidateAvatarId = _characterSetGetGid!(charSetPtr, index);
            if (string.IsNullOrWhiteSpace(candidateName) || candidateAvatarId == 0)
                continue;

            availableCharacters.Add(candidateName);
            // Admin/staff character names render with a leading '+' in the AC
            // client UI but the cached/launcher-side name often lacks it (or the
            // server packet does). Match prefix-tolerantly so 'Buffi' matches
            // '+Buffi' and vice versa.
            if (!CharacterNamesMatch(candidateName, targetCharacter))
                continue;

            slotIndex = index;

            IntPtr playerSystemPtr = GetPlayerSystemPointer();
            if (playerSystemPtr == IntPtr.Zero)
            {
                status = "Player system is not available yet.";
                return false;
            }

            byte result = _logOnCharacter!(playerSystemPtr, candidateAvatarId);
            if (result == 0)
            {
                status = hasMode
                    ? $"LogOnCharacter rejected avatar 0x{candidateAvatarId:X8} ('{candidateName}') while mode=0x{mode:X8}."
                    : $"LogOnCharacter rejected avatar 0x{candidateAvatarId:X8} ('{candidateName}').";
                return false;
            }

            matchedCharacter = candidateName;
            avatarId = candidateAvatarId;
            status = $"Issued LogOnCharacter for avatar 0x{candidateAvatarId:X8}.";
            return true;
        }

        status = availableCharacters.Count > 0
            ? $"Target '{targetCharacter}' not found in native character set [{string.Join(", ", availableCharacters)}]."
            : "Native character set is empty or not ready yet.";
        return false;
    }

    /// <summary>
    /// Returns the count of populated character slots in AC's native
    /// CharacterSet (slots where GetIdentity returns non-null). Used by the
    /// click fallback in CharacterCaptureHooks when the 0xF658 packet parser
    /// produced an empty list — typical on ACE where the packet shape differs
    /// from retail.
    /// </summary>
    public static int GetNativeCharacterSetSlotCount()
    {
        if (!EnsureBound())
            return 0;

        IntPtr charSetPtr = GetCharacterSetPointer();
        if (charSetPtr == IntPtr.Zero)
            return 0;

        int count = 0;
        for (int index = 0; index < MaxCharacterSlots; index++)
        {
            IntPtr identityPtr;
            try { identityPtr = _characterSetGetIdentity!(charSetPtr, index); }
            catch { return count; }

            if (identityPtr != IntPtr.Zero)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Compares two AC character names tolerantly of the leading '+' that the
    /// client adds to admin/staff names. e.g. 'Buffi' matches '+Buffi'.
    /// </summary>
    internal static bool CharacterNamesMatch(string? a, string? b)
    {
        string lhs = (a ?? string.Empty).Trim().TrimStart('+');
        string rhs = (b ?? string.Empty).Trim().TrimStart('+');
        return lhs.Length > 0 && string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EnsureBound()
    {
        lock (BindLock)
        {
            if (_bindAttempted)
                return _bound;

            _bindAttempted = true;
            try
            {
                if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
                {
                    _bound = false;
                    _statusMessage = "acclient .text not readable for pattern resolve.";
                    RynthLog.Compat($"CharacterManagement: bind failed - {_statusMessage}");
                    return _bound;
                }

                _uiFlowGetPersistantData = Bind<UIFlowGetPersistantDataDelegate>(text, "CharMgmt.UIFlowGetPersistantData", PatUIFlowGetPersistantData, UIFlowGetPersistantDataVa);
                _getPlayerSystem = Bind<GetPlayerSystemDelegate>(text, "CharMgmt.GetPlayerSystem", PatGetPlayerSystem, GetPlayerSystemVa);
                _logOnCharacter = Bind<LogOnCharacterDelegate>(text, "CharMgmt.LogOnCharacter", PatLogOnCharacter, LogOnCharacterVa);
                _characterSetGetIdentity = Bind<CharacterSetGetIdentityDelegate>(text, "CharMgmt.CharacterSetGetIdentity", PatCharacterSetGetIdentity, CharacterSetGetIdentityVa);
                _characterSetGetName = Bind<CharacterSetGetNameDelegate>(text, "CharMgmt.CharacterSetGetName", PatCharacterSetGetName, CharacterSetGetNameVa);
                _characterSetGetGid = Bind<CharacterSetGetGidDelegate>(text, "CharMgmt.CharacterSetGetGid", PatCharacterSetGetGid, CharacterSetGetGidVa);

                _bound = _uiFlowGetPersistantData != null && _getPlayerSystem != null && _logOnCharacter != null
                         && _characterSetGetIdentity != null && _characterSetGetName != null && _characterSetGetGid != null;
                _statusMessage = _bound ? "Bound." : "One or more character-management addresses failed to resolve.";
                if (_bound)
                    RynthLog.Verbose("CharacterManagement: Bound UIFlow and direct LogOnCharacter entry points.");
                else
                    RynthLog.Compat($"CharacterManagement: bind incomplete - {_statusMessage}");
            }
            catch (Exception ex)
            {
                _bound = false;
                _statusMessage = ex.Message;
                RynthLog.Compat($"CharacterManagement: Failed to bind native entry points - {ex.Message}");
            }

            return _bound;
        }
    }

    private static IntPtr GetUiFlowPointer()
    {
        try
        {
            return Marshal.ReadIntPtr(new IntPtr(UIFlowInstanceVa));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr GetCharacterSetPointer()
    {
        IntPtr uiFlowPtr = GetUiFlowPointer();
        if (uiFlowPtr == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr persistantDataPtr = IntPtr.Zero;
        try
        {
            persistantDataPtr = _uiFlowGetPersistantData!(uiFlowPtr);
        }
        catch
        {
            persistantDataPtr = IntPtr.Zero;
        }

        if (persistantDataPtr == IntPtr.Zero)
        {
            try
            {
                persistantDataPtr = Marshal.ReadIntPtr(IntPtr.Add(uiFlowPtr, UIFlowDataOffset));
            }
            catch
            {
                persistantDataPtr = IntPtr.Zero;
            }
        }

        return persistantDataPtr != IntPtr.Zero
            ? IntPtr.Add(persistantDataPtr, UIPersistantDataCharacterSetOffset)
            : IntPtr.Zero;
    }

    private static IntPtr GetPlayerSystemPointer()
    {
        try
        {
            IntPtr playerSystemPtr = _getPlayerSystem!();
            if (playerSystemPtr != IntPtr.Zero)
                return playerSystemPtr;
        }
        catch
        {
            // Fall back to the known global if the helper call is unavailable.
        }

        try
        {
            return Marshal.ReadIntPtr(new IntPtr(PlayerSystemVa));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string? ReadAnsiString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return null;
        }
    }
}
