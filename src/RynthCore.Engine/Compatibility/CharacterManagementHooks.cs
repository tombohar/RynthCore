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
    {
        matchedCharacter = string.Empty;
        avatarId = 0;

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
            if (!string.Equals(candidateName, targetCharacter, StringComparison.OrdinalIgnoreCase))
                continue;

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

    private static bool EnsureBound()
    {
        lock (BindLock)
        {
            if (_bindAttempted)
                return _bound;

            _bindAttempted = true;
            try
            {
                _uiFlowGetPersistantData = Marshal.GetDelegateForFunctionPointer<UIFlowGetPersistantDataDelegate>(new IntPtr(UIFlowGetPersistantDataVa));
                _getPlayerSystem = Marshal.GetDelegateForFunctionPointer<GetPlayerSystemDelegate>(new IntPtr(GetPlayerSystemVa));
                _logOnCharacter = Marshal.GetDelegateForFunctionPointer<LogOnCharacterDelegate>(new IntPtr(LogOnCharacterVa));
                _characterSetGetIdentity = Marshal.GetDelegateForFunctionPointer<CharacterSetGetIdentityDelegate>(new IntPtr(CharacterSetGetIdentityVa));
                _characterSetGetName = Marshal.GetDelegateForFunctionPointer<CharacterSetGetNameDelegate>(new IntPtr(CharacterSetGetNameVa));
                _characterSetGetGid = Marshal.GetDelegateForFunctionPointer<CharacterSetGetGidDelegate>(new IntPtr(CharacterSetGetGidVa));
                _bound = true;
                _statusMessage = "Bound.";
                RynthLog.Compat("CharacterManagement: Bound UIFlow and direct LogOnCharacter entry points.");
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
