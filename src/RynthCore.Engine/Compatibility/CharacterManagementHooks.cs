using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class CharacterManagementHooks
{
    private const int UIFlowInstanceVa = 0x0083E72C;
    private const int PlayerSystemVa = 0x0087119C;

    // Phase B: resolve the two data globals by code-xref (operand at offset 2); VAs stay fallback.
    private static readonly byte?[] PatXrefUIFlow = [ 0x51, 0xA1, null, null, null, null, 0x8B, 0x4C ];
    private static readonly byte?[] PatXrefCPlayerSystem = [ 0xC7, 0x05, null, null, null, null, 0x00, 0x00, 0x00, 0x00, 0x83, 0xC6 ];
    private static int _uiFlowAddr = UIFlowInstanceVa;
    private static int _playerSystemAddr = PlayerSystemVa;
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

    // ── Main-thread char-select service (2026-09-30 main-thread audit) ─────────
    // The two char-select poll threads (RynthCore.AutoLoginPoll and
    // RynthCore.CharacterListCapture, plus the dormant RynthCore.AutoLogin) used to
    // call UIFlow::GetPersistantData, the CharacterSet GetIdentity/GetName/GetGid
    // accessors and CPlayerSystem::LogOnCharacter straight from their own threads,
    // and PtrToStringAnsi'd name buffers AC frees when char-select rebuilds the
    // set (create / delete / reconnect). A freed buffer there is an uncatchable
    // AV. Now:
    //  * MainThreadTick (AcMainThreadQueue.Drain, AC's main thread; pre-login it
    //    runs from the Client::UseTime drain) publishes the UIFlow mode every call
    //    and, at char-select, an immutable name/slot-count snapshot every 250 ms.
    //  * Off-thread TryReadCharacterNames / GetNativeCharacterSetSlotCount serve
    //    that snapshot; off-thread TryGetCurrentMode serves the published mode.
    //  * Off-thread TryLogOnCharacter parks the target in a one-request slot and
    //    waits a bounded time for the main thread to run the real call; on a
    //    timeout it withdraws the request (it can never fire late) and reports
    //    failure, which the poll loops already retry.
    // The main thread never waits on anyone: it only consumes what is there.
    private sealed class CharSelectSnapshot
    {
        public static readonly CharSelectSnapshot Empty = new(Array.Empty<string>(), 0);
        public readonly string[] Names;
        public readonly int SlotCount;
        public CharSelectSnapshot(string[] names, int slotCount) { Names = names; SlotCount = slotCount; }
    }

    private sealed class LogOnResult
    {
        public bool Ok;
        public string Matched = string.Empty;
        public uint AvatarId;
        public int SlotIndex = -1;
        public string Status = string.Empty;
    }

    private const int CharSelectSnapshotIntervalMs = 250;
    private const int LogOnRequestWaitMs = 500;     // one frame at char-select is ~16-50 ms
    private const int LogOnRunningGraceMs = 1000;   // extra wait once the main thread has taken it

    private static volatile CharSelectSnapshot _charSelectSnapshot = CharSelectSnapshot.Empty;
    private static long _nextCharSelectSnapshotMs;
    private static int _publishedMode;
    private static int _modePublished;               // 1 once MainThreadTick has read the mode

    // Login request slot: 0 idle, 1 pending (off-thread caller armed it),
    // 2 running (main thread took it), 3 done (result published).
    private static int _logOnState;
    private static string _logOnTarget = string.Empty;
    private static LogOnResult? _logOnResult;
    private static readonly object LogOnCallerLock = new(); // serialises OFF-thread callers only
    private static int _loggedNoMainThreadService;

    /// <summary>
    /// Main thread only (AcMainThreadQueue.Drain). Publishes the UIFlow mode, runs a
    /// pending login request, and refreshes the char-select snapshot (throttled).
    /// Idle in the world: a flag check and one aligned read.
    /// </summary>
    public static void MainThreadTick()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;

        if (TryReadModeRaw(out int mode))
        {
            Volatile.Write(ref _publishedMode, mode);
            Volatile.Write(ref _modePublished, 1);
            // Seeds UiFlowHooks' mode; drives its screen events when the UseNewMode hook isn't live.
            UiFlowHooks.OnModePolled(mode);
        }
        else
        {
            Volatile.Write(ref _modePublished, 0);
        }

        // A pending login request is serviced on the next drain, not throttled.
        if (Volatile.Read(ref _logOnState) == 1)
            RunPendingLogOn();

        if (LoginLifecycleHooks.HasObservedLoginComplete)
        {
            if (_charSelectSnapshot.Names.Length != 0)
                _charSelectSnapshot = CharSelectSnapshot.Empty;
            return;
        }

        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextCharSelectSnapshotMs))
            return;
        Volatile.Write(ref _nextCharSelectSnapshotMs, now + CharSelectSnapshotIntervalMs);

        // Never bind here: EnsureBound pattern-scans .text under a lock the poll
        // threads also take. They bind on their first pass; until then, skip.
        if (!Volatile.Read(ref _bound) || mode != CharacterManagementUI)
        {
            if (_charSelectSnapshot.Names.Length != 0 || _charSelectSnapshot.SlotCount != 0)
                _charSelectSnapshot = CharSelectSnapshot.Empty;
            return;
        }

        try
        {
            var names = new List<string>();
            ReadCharacterNamesCore(names);
            int slotCount = SlotCountCore();
            _charSelectSnapshot = new CharSelectSnapshot(names.ToArray(), slotCount);
        }
        catch
        {
            _charSelectSnapshot = CharSelectSnapshot.Empty;
        }
    }

    // Main thread: take the request (1 -> 2), run the real call, publish (-> 3).
    private static void RunPendingLogOn()
    {
        if (Interlocked.CompareExchange(ref _logOnState, 2, 1) != 1)
            return; // withdrawn by its caller in the meantime
        var result = new LogOnResult();
        try
        {
            string target = Volatile.Read(ref _logOnTarget);
            result.Ok = TryLogOnCharacterCore(target, out result.Matched, out result.AvatarId, out result.SlotIndex, out result.Status);
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Status = $"LogOnCharacter threw {ex.GetType().Name}: {ex.Message}";
        }
        Volatile.Write(ref _logOnResult, result);
        Volatile.Write(ref _logOnState, 3);
    }

    // Off-thread: arm the slot and wait (bounded) for the main thread's result.
    private static bool RequestLogOnFromMainThread(string targetCharacter, out string matchedCharacter, out uint avatarId, out int slotIndex, out string status)
    {
        matchedCharacter = string.Empty;
        avatarId = 0;
        slotIndex = -1;

        lock (LogOnCallerLock)
        {
            // A result a previous caller gave up on: discard it, the slot is ours.
            if (Volatile.Read(ref _logOnState) == 3)
                Volatile.Write(ref _logOnState, 0);
            if (Volatile.Read(ref _logOnState) != 0)
            {
                status = "Main thread is still running the previous LogOnCharacter request.";
                return false;
            }

            Volatile.Write(ref _logOnResult, null);
            Volatile.Write(ref _logOnTarget, targetCharacter);
            Volatile.Write(ref _logOnState, 1);

            long start = Environment.TickCount64;
            while (true)
            {
                int state = Volatile.Read(ref _logOnState);
                if (state == 3)
                {
                    LogOnResult? r = Volatile.Read(ref _logOnResult);
                    Volatile.Write(ref _logOnState, 0);
                    if (r == null) { status = "LogOnCharacter produced no result."; return false; }
                    matchedCharacter = r.Matched;
                    avatarId = r.AvatarId;
                    slotIndex = r.SlotIndex;
                    status = r.Status;
                    return r.Ok;
                }

                long waited = Environment.TickCount64 - start;
                if (state == 1 && waited >= LogOnRequestWaitMs)
                {
                    // Not taken yet: withdraw it so it can never run late.
                    if (Interlocked.CompareExchange(ref _logOnState, 0, 1) == 1)
                    {
                        if (Interlocked.Exchange(ref _loggedNoMainThreadService, 1) == 0)
                            RynthLog.Compat("CharacterManagement: LogOnCharacter request not serviced by AC's main thread within 500 ms (is the Client::UseTime drain hooked?); retrying on the next poll.");
                        status = "AC's main thread did not service the LogOnCharacter request in time.";
                        return false;
                    }
                    continue; // taken just now; wait for it
                }
                if (state == 2 && waited >= LogOnRequestWaitMs + LogOnRunningGraceMs)
                {
                    // Running on the main thread; its result is picked up (and
                    // discarded) by the next caller. Never blocks longer than this.
                    status = "LogOnCharacter still running on AC's main thread.";
                    return false;
                }
                if (!EngineThreads.Sleep(10))
                {
                    Interlocked.CompareExchange(ref _logOnState, 0, 1);
                    status = "Engine shutting down.";
                    return false;
                }
            }
        }
    }

    private static bool TryReadModeRaw(out int mode)
    {
        mode = 0;
        IntPtr uiFlowPtr = GetUiFlowPointer();
        if (uiFlowPtr == IntPtr.Zero)
            return false;

        mode = Marshal.ReadInt32(IntPtr.Add(uiFlowPtr, UIFlowCurModeOffset));
        return true;
    }

    public static bool TryGetCurrentMode(out int mode)
    {
        // Off the main thread, serve the mode MainThreadTick published (at most a
        // frame old). Before the first tick has run, fall back to the raw read:
        // one aligned 4-byte load from the long-lived UIFlow singleton.
        if (!MainThreadGuard.IsOnMainThread() && Volatile.Read(ref _modePublished) != 0)
        {
            mode = Volatile.Read(ref _publishedMode);
            return true;
        }

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

        // Bind here, on the caller's thread: pattern resolve reads only acclient's
        // .text, and doing it off-thread keeps the scan off AC's main thread.
        if (!EnsureBound())
        {
            status = _statusMessage;
            return false;
        }

        // Off AC's main thread (the auto-login poll threads): the CharacterSet reads
        // and CPlayerSystem::LogOnCharacter run on the main thread via the request
        // slot; this thread waits a bounded time for the result.
        if (!MainThreadGuard.IsOnMainThread())
            return RequestLogOnFromMainThread(targetCharacter, out matchedCharacter, out avatarId, out slotIndex, out status);

        return TryLogOnCharacterCore(targetCharacter, out matchedCharacter, out avatarId, out slotIndex, out status);
    }

    // The real lookup + LogOnCharacter. MAIN THREAD ONLY (MainThreadTick's request
    // slot, or a caller already on AC's main thread).
    private static bool TryLogOnCharacterCore(string targetCharacter, out string matchedCharacter, out uint avatarId, out int slotIndex, out string status)
    {
        matchedCharacter = string.Empty;
        avatarId = 0;
        slotIndex = -1;

        if (!MainThreadGuard.IsOnMainThread())
        {
            status = "Off AC's main thread.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetCharacter))
        {
            status = "No target character requested.";
            return false;
        }

        if (!Volatile.Read(ref _bound))
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
    /// Reads the character names in AC's native CharacterSet, in slot order
    /// (the order char-select lists them). Read-only: never calls
    /// LogOnCharacter. Only reads while CharacterManagementUI is the current
    /// mode, so the native set is never touched in the world. Returns false
    /// (empty list) when unbound, not at char-select, or the set isn't filled yet.
    /// </summary>
    public static bool TryReadCharacterNames(out List<string> names)
    {
        names = new List<string>();

        if (!EnsureBound())
            return false;

        // Off AC's main thread (the capture/auto-login polls): the main-thread
        // snapshot, taken only while CharacterManagementUI was up (<= 250 ms old).
        if (!MainThreadGuard.IsOnMainThread())
        {
            CharSelectSnapshot snap = _charSelectSnapshot;
            if (snap.Names.Length == 0)
                return false;
            if (!TryGetCurrentMode(out int offMode) || offMode != CharacterManagementUI)
                return false;
            names.AddRange(snap.Names);
            return true;
        }

        if (!TryGetCurrentMode(out int mode) || mode != CharacterManagementUI)
            return false;

        ReadCharacterNamesCore(names);
        return names.Count > 0;
    }

    // MAIN THREAD ONLY. Appends the native set's names in slot order.
    private static void ReadCharacterNamesCore(List<string> names)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;

        IntPtr charSetPtr = GetCharacterSetPointer();
        if (charSetPtr == IntPtr.Zero)
            return;

        for (int index = 0; index < MaxCharacterSlots; index++)
        {
            IntPtr identityPtr = _characterSetGetIdentity!(charSetPtr, index);
            if (identityPtr == IntPtr.Zero)
                continue;

            string? name = ReadAnsiString(_characterSetGetName!(charSetPtr, index));
            uint avatarId = _characterSetGetGid!(charSetPtr, index);
            if (string.IsNullOrWhiteSpace(name) || avatarId == 0)
                continue;

            names.Add(name.Trim());
        }
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

        // Off AC's main thread: the slot count from the main-thread snapshot.
        if (!MainThreadGuard.IsOnMainThread())
            return _charSelectSnapshot.SlotCount;

        return SlotCountCore();
    }

    // MAIN THREAD ONLY.
    private static int SlotCountCore()
    {
        if (!MainThreadGuard.IsOnMainThread())
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
                _uiFlowAddr = HookResolver.ResolveData(text, "CharMgmt.UIFlow", PatXrefUIFlow, 2, UIFlowInstanceVa).Address.ToInt32();
                _playerSystemAddr = HookResolver.ResolveData(text, "CharMgmt.CPlayerSystem", PatXrefCPlayerSystem, 2, PlayerSystemVa).Address.ToInt32();

                // Published last (volatile): MainThreadTick reads _bound without the
                // lock and then uses the delegates, so they must be visible first.
                Volatile.Write(ref _bound, _uiFlowGetPersistantData != null && _getPlayerSystem != null && _logOnCharacter != null
                         && _characterSetGetIdentity != null && _characterSetGetName != null && _characterSetGetGid != null);
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
            return Marshal.ReadIntPtr(new IntPtr(_uiFlowAddr));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr GetCharacterSetPointer()
    {
        // Calls UIFlow::GetPersistantData; the set it returns is rebuilt by the
        // main thread. Main thread only (every caller is a *Core method).
        if (!MainThreadGuard.IsOnMainThread())
            return IntPtr.Zero;

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

    /// <summary>
    /// The CPlayerSystem singleton for AcShortcuts (the player's shortcut list lives in its
    /// PlayerModule). AC's main thread; zero when it can't be read.
    /// </summary>
    internal static IntPtr PlayerSystemOnMainThread()
    {
        if (!MainThreadGuard.IsOnMainThread()) return IntPtr.Zero;
        var get = _getPlayerSystem;
        if (get != null)
        {
            try
            {
                IntPtr p = get();
                if (p != IntPtr.Zero) return p;
            }
            catch { }
        }
        IntPtr addr = new(_playerSystemAddr);
        if (_playerSystemAddr == 0 || !ClientObjectHooks.IsReadablePointer(addr)) return IntPtr.Zero;
        return Marshal.ReadIntPtr(addr);
    }

    private static IntPtr GetPlayerSystemPointer()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return IntPtr.Zero;

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
            return Marshal.ReadIntPtr(new IntPtr(_playerSystemAddr));
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
