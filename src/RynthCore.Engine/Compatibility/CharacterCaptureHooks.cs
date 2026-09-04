using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using RynthCore.App;

namespace RynthCore.Engine.Compatibility;

/// <summary>
    /// Captures the character list from the 0xF658 server packet, persists it per-account,
    /// and optionally performs a native auto-login sequence with click fallback.
/// </summary>
internal static class CharacterCaptureHooks
{
    private const uint PostConnectReadyOpcode = 0x0000F7EA;
    private const uint CharacterListOpcode = 0x0000F658;
    private const int NetBlobBufPtrOffset = 0x2C;
    private const int NetBlobBufSizeOffset = 0x30;

    // Auto-login click geometry (matches ThwargFilter LoginCharacterTools)
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const int XCharList = 121;
    private const int YTopOfBox = 209;
    private const int YBottomOfBox = 532;

    /// <summary>
    /// Rows the character list box is divided into, regardless of how many
    /// characters exist. The grid is fixed: two characters are drawn in the top
    /// two rows with empty space below, not spread across the box.
    ///
    /// Dividing by the character count instead put every row after the first in
    /// the wrong place — with two characters it aimed at y=451, which is row 9
    /// and empty. Solved from three observations: a click at y=249 selected the
    /// character drawn in row 1, and 249 is that row's centre only if the box
    /// holds 12 rows. The two failures at y=330 and y=451 land on rows 4 and 9,
    /// both empty, which agrees.
    /// </summary>
    private const int CharacterListRows = 12;
    private const int AutoLoginDelayMs = 2000;
    private const int AutoLoginWindowWaitMs = 10000;
    private const int AutoLoginWindowPollMs = 250;
    private const int AutoLoginAttempts = 15;
    private const int AutoLoginAttemptDelayMs = 600;
    private const int AutoLoginDoubleClickGapMs = 100;
    private const int DirectAutoLoginAttempts = 40;
    private const int DirectAutoLoginAttemptDelayMs = 250;
    private const int CharacterManagementUIMode = 0x1000000A;
    private const int GamePlayUIMode = 0x10000008;

    // Poll-driven auto-login. The packet-driven path (ScheduleAutoLoginIfRequested,
    // fed by InnerDispatcherDetour) is dead on this client: InnerDispatcherHook was
    // disabled 2026-05-14 because its pattern walk-back stalls ACE in "Entering
    // World", and SmartBox does not carry the pre-login 0xF658 char-list packet.
    // Result: zero CharacterCapture activity, auto-login never fired. This poll
    // drives login off the native CharacterSet directly (CharacterManagementHooks),
    // which needs no packet — it just watches the UIFlow mode and retries until
    // login completes. Replaces the dead trigger; the packet path is left intact
    // (harmless, never invoked) in case InnerDispatcher is ever re-enabled.
    private const int AutoLoginPollIntervalMs = 600;
    private const int AutoLoginPollHardTimeoutMs = 120_000;
    private const int AutoLoginDirectFailsBeforeClick = 4;
    private static int _autoLoginPollStarted;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static int _autoLoginScheduled;

    // Latches once auto-login has fired in this process. The character-list packet
    // (0xF658) arrives every time the client sees char-select — including the
    // logout-to-char-select transition — and without this latch we'd re-trigger
    // auto-login the moment the user manually logs out. One-shot per process: if
    // the user wants to swap characters, they can pick from char-select manually,
    // or relaunch.
    private static int _autoLoginEverFired;

    /// <summary>
    /// Starts the poll-driven auto-login worker. Call once from InitWorker.
    /// No-ops if no TargetCharacter is configured in the launch context, or if
    /// the worker is already running. Safe to call before the client window or
    /// char-select UI exists — the worker waits for them.
    /// </summary>
    public static void Initialize()
    {
        (_, _, string targetCharacter) = ReadLaunchContext();
        if (string.IsNullOrWhiteSpace(targetCharacter))
        {
            RynthLog.Verbose("CharacterCapture: Initialize - no TargetCharacter in launch context; auto-login disabled.");
            return;
        }

        if (Interlocked.Exchange(ref _autoLoginPollStarted, 1) != 0)
            return;

        var thread = new Thread(AutoLoginPollLoop)
        {
            Name = "RynthCore.AutoLoginPoll",
            IsBackground = true
        };
        thread.Start();
        RynthLog.Info($"CharacterCapture: Poll-driven auto-login armed for '{targetCharacter}'.");
    }

    /// <summary>
    /// Watches the native UIFlow mode and drives LogOnCharacter directly off
    /// AC's native CharacterSet. Retries every tick while char-select is up,
    /// so a too-early attempt or a missed click just gets re-tried — until
    /// login completes, the client enters the world, or the hard timeout hits.
    /// Hard-stops the instant mode==GamePlayUI or LoginComplete is observed:
    /// poking LogOn pointers while in-game has been correlated with AC self-
    /// exiting (see ScheduleAutoLoginIfRequested's notes).
    /// </summary>
    private static void AutoLoginPollLoop()
    {
        (_, _, string targetCharacter) = ReadLaunchContext();
        if (string.IsNullOrWhiteSpace(targetCharacter))
            return;

        long charSelectFirstSeenTick = 0;
        int directFailCount = 0;
        bool issuedDirect = false;

        try
        {
            while (true)
            {
                Thread.Sleep(AutoLoginPollIntervalMs);

                if (LoginLifecycleHooks.HasObservedLoginComplete)
                {
                    RynthLog.Info($"CharacterCapture: Auto-login complete for '{targetCharacter}' (LoginComplete observed).");
                    Interlocked.Exchange(ref _autoLoginEverFired, 1);
                    return;
                }

                // One-shot: if the (currently dead) packet path ever fires and
                // latches, stand down so we don't double-drive.
                if (Volatile.Read(ref _autoLoginEverFired) != 0)
                {
                    RynthLog.Verbose($"CharacterCapture: Poll standing down for '{targetCharacter}' - auto-login already fired elsewhere.");
                    return;
                }

                if (!CharacterManagementHooks.TryGetCurrentMode(out int mode))
                    continue; // UIFlow not ready yet — keep waiting.

                if (mode == GamePlayUIMode)
                {
                    RynthLog.Compat($"CharacterCapture: Poll stopping for '{targetCharacter}' - client already in GamePlayUI.");
                    Interlocked.Exchange(ref _autoLoginEverFired, 1);
                    return;
                }

                if (mode != CharacterManagementUIMode)
                {
                    // Still in connect/logo/transition — don't start the timeout
                    // clock until char-select has actually appeared.
                    continue;
                }

                if (charSelectFirstSeenTick == 0)
                {
                    charSelectFirstSeenTick = Environment.TickCount64;
                    RynthLog.Info($"CharacterCapture: Char-select up — driving direct auto-login for '{targetCharacter}'.");
                }

                // Find the slot, then click it through AC's own character list.
                //
                // Calling LogOnCharacter directly does log the character in, but
                // the client then starts a logoff it can never complete: the
                // world loads, "Logging off..." appears in the same millisecond,
                // and repeats until the client is killed. Confirmed by disabling
                // auto-login and logging in by hand, which is stable. The
                // function reports failure while doing it, too, so its return
                // value cannot be used to detect the problem.
                //
                // Clicking goes through the same path a player uses, which does
                // whatever setup the direct call skips.
                if (!CharacterManagementHooks.TryFindCharacterSlot(
                        targetCharacter, out string matched, out uint avatarId, out int slotIndex,
                        out int drawnCount, out string status))
                {
                    directFailCount++;
                    RynthLog.Info($"CharacterCapture: Cannot locate '{targetCharacter}' yet (attempt {directFailCount}) - {status}");
                    continue;
                }

                if (drawnCount <= 0 || slotIndex < 0)
                    continue;

                if (!issuedDirect)
                {
                    issuedDirect = true;
                    float rowHeight = (YBottomOfBox - YTopOfBox) / (float)CharacterListRows;
                    int clickY = (int)(YTopOfBox + (rowHeight / 2.0f) + (rowHeight * slotIndex));
                    RynthLog.Info(
                        $"CharacterCapture: Clicking '{matched}' (target '{targetCharacter}', avatar 0x{avatarId:X8}) " +
                        $"at drawn row {slotIndex} of {drawnCount} character(s) -> y={clickY} " +
                        $"(box {YTopOfBox}..{YBottomOfBox} / {CharacterListRows} rows, rowHeight={rowHeight:F1}). {status}");
                }

                TryClickCharacterSlot(targetCharacter, slotIndex, CharacterListRows);

                // Re-check before sleeping. The client can be in the world well
                // inside one poll interval — 337ms in an observed case — and this
                // loop must not still be poking at char-select when that happens.
                if (LoginLifecycleHooks.HasObservedLoginComplete ||
                    (CharacterManagementHooks.TryGetCurrentMode(out int postMode) &&
                     postMode == GamePlayUIMode))
                {
                    RynthLog.Info($"CharacterCapture: '{targetCharacter}' reached the world — standing down.");
                    Interlocked.Exchange(ref _autoLoginEverFired, 1);
                    return;
                }

                if (charSelectFirstSeenTick != 0 &&
                    Environment.TickCount64 - charSelectFirstSeenTick > AutoLoginPollHardTimeoutMs)
                {
                    RynthLog.Compat($"CharacterCapture: Auto-login for '{targetCharacter}' gave up after {AutoLoginPollHardTimeoutMs / 1000}s at char-select. Last status: {status}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CharacterCapture: Auto-login poll loop crashed - {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _autoLoginPollStarted, 0);
        }
    }

    /// <summary>
    /// Single char-list double-click at the slot for <paramref name="slotIndex"/>.
    /// Fallback for when the direct native LogOnCharacter path isn't taking.
    /// </summary>
    private static void TryClickCharacterSlot(string targetCharacter, int slotIndex, int slotCount)
    {
        try
        {
            if (CharacterManagementHooks.TryGetCurrentMode(out int m) && m == GamePlayUIMode)
                return;

            IntPtr hwnd = WaitForGameWindow();
            if (hwnd == IntPtr.Zero)
                return;

            SetForegroundWindow(hwnd);

            float nameSize = (YBottomOfBox - YTopOfBox) / (float)slotCount;
            int yOffset = (int)(YTopOfBox + (nameSize / 2.0f) + (nameSize * slotIndex));

            RynthLog.Verbose($"CharacterCapture: Click fallback for '{targetCharacter}' at slot {slotIndex}/{slotCount} ({XCharList},{yOffset}).");
            PostMouseClick(hwnd, XCharList, yOffset);
            Thread.Sleep(AutoLoginDoubleClickGapMs);
            PostMouseClick(hwnd, XCharList, yOffset);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CharacterCapture: Click fallback threw - {ex.Message}");
        }
    }

    public static void ProcessPotentialCharacterMessage(IntPtr blob, bool isGameEvent = false)
    {
        if (blob == IntPtr.Zero)
            return;

        try
        {
            uint blobSize = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(blob, NetBlobBufSizeOffset)));
            if (blobSize < 4)
                return;

            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return;

            uint opcode = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            if (opcode == PostConnectReadyOpcode)
            {
                RynthLog.Verbose($"CharacterCapture: [{(isGameEvent ? "GameEvent" : "SmartBox")}] Observed post-connect packet (0xF7EA).");
                LogoBypassHooks.NotifyPostConnectObserved();
                return;
            }

            if (opcode != CharacterListOpcode)
                return;

            RynthLog.Verbose($"CharacterCapture: [{(isGameEvent ? "GameEvent" : "SmartBox")}] Found CharacterList (0xF658)!");
            LogoBypassHooks.NotifyCharacterListObserved();
            (List<string> characters, int slotCount) = ParseAndSaveCharacterList(payloadPtr, blobSize);
            ScheduleAutoLoginIfRequested(characters, slotCount);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CharacterCapture: Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Called from InnerDispatcherDetour where the buffer is a raw pre-login message (no blob wrapper).
    /// The first 4 bytes are the opcode.
    /// </summary>
    public static void ProcessRawCharacterMessage(IntPtr buffer, uint size)
    {
        if (buffer == IntPtr.Zero || size < 4)
            return;

        try
        {
            uint opcode = unchecked((uint)Marshal.ReadInt32(buffer));
            if (opcode == PostConnectReadyOpcode)
            {
                RynthLog.Verbose("CharacterCapture: [InnerDispatcher] Observed post-connect packet (0xF7EA).");
                LogoBypassHooks.NotifyPostConnectObserved();
                return;
            }

            if (opcode != CharacterListOpcode)
                return;

            RynthLog.Verbose("CharacterCapture: [InnerDispatcher] Processing 0xF658 raw buffer.");
            LogoBypassHooks.NotifyCharacterListObserved();
            (List<string> characters, int slotCount) = size >= 12
                ? ParseAndSaveCharacterList(buffer, size)
                : (new List<string>(), 0);
            ScheduleAutoLoginIfRequested(characters, slotCount);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CharacterCapture: Raw error: {ex.Message}");
        }
    }

    private static (List<string> characters, int slotCount) ParseAndSaveCharacterList(IntPtr payloadPtr, uint blobSize)
    {
        var characters = new List<string>();
        int slotCount = 0;

        try
        {
            int offset = 4; // Skip opcode
            int characterCount = Marshal.ReadInt32(IntPtr.Add(payloadPtr, offset));
            offset += 4;

            if (characterCount < 0 || characterCount > 20)
            {
                RynthLog.Compat($"CharacterCapture: Implausible character count {characterCount} - skipping.");
                return (characters, slotCount);
            }

            characters = new List<string>(characterCount);
            for (int i = 0; i < characterCount; i++)
            {
                offset += 4; // Skip Character GUID
                short nameLen = Marshal.ReadInt16(IntPtr.Add(payloadPtr, offset));
                offset += 2;

                if (nameLen > 0 && nameLen < 128)
                {
                    byte[] nameBytes = new byte[nameLen];
                    Marshal.Copy(IntPtr.Add(payloadPtr, offset), nameBytes, 0, nameLen);
                    string name = Encoding.Default.GetString(nameBytes);
                    characters.Add(name);
                }

                offset += nameLen;
                if (offset % 4 != 0)
                    offset += 4 - (offset % 4);
                offset += 4; // Skip Delete Timeout
            }

            characters.Sort(StringComparer.OrdinalIgnoreCase);

            slotCount = (int)blobSize > offset + 3
                ? Marshal.ReadInt32(IntPtr.Add(payloadPtr, offset))
                : characters.Count;

            RynthLog.Info($"CharacterCapture: Parsed {characters.Count} chars ({slotCount} slots): {string.Join(", ", characters)}");

            if (characters.Count > 0)
            {
                (string accountName, string serverName, _) = ReadLaunchContext();
                SaveCharacterList(characters, accountName, serverName);
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CharacterCapture: Parse error: {ex.Message}");
        }

        return (characters, slotCount);
    }

    private static (string accountName, string serverName, string targetCharacter) ReadLaunchContext()
    {
        try
        {
            string rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore");

            string processPath = Path.Combine(rootDir, "launch_contexts", $"launch_context_{Environment.ProcessId}.json");
            (string accountName, string serverName, string targetCharacter) processContext = ReadLaunchContextFile(processPath);
            if (!string.IsNullOrWhiteSpace(processContext.accountName) ||
                !string.IsNullOrWhiteSpace(processContext.serverName) ||
                !string.IsNullOrWhiteSpace(processContext.targetCharacter))
            {
                return processContext;
            }

            SessionStateRecord? sessionHint = SessionStateStore.TryReadForProcess(Environment.ProcessId);
            if (sessionHint != null &&
                (!string.IsNullOrWhiteSpace(sessionHint.AccountName) ||
                 !string.IsNullOrWhiteSpace(sessionHint.ServerName) ||
                 !string.IsNullOrWhiteSpace(sessionHint.TargetCharacter)))
            {
                return (sessionHint.AccountName, sessionHint.ServerName, sessionHint.TargetCharacter);
            }

            return ReadLaunchContextFile(Path.Combine(rootDir, "launch_context.json"));
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static (string accountName, string serverName, string targetCharacter) ReadLaunchContextFile(string filePath)
    {
        if (!File.Exists(filePath))
            return (string.Empty, string.Empty, string.Empty);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(filePath));
        JsonElement root = doc.RootElement;
        string accountName = root.TryGetProperty("AccountName", out JsonElement an) ? an.GetString() ?? string.Empty : string.Empty;
        string serverName = root.TryGetProperty("ServerName", out JsonElement sn) ? sn.GetString() ?? string.Empty : string.Empty;
        string target = root.TryGetProperty("TargetCharacter", out JsonElement tc) ? tc.GetString() ?? string.Empty : string.Empty;
        return (accountName, serverName, target);
    }

    private static void SaveCharacterList(List<string> characters, string accountName, string serverName)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(accountName))
            {
                CharacterCacheStore.Write(accountName, serverName, characters);
                RynthLog.Verbose(
                    !string.IsNullOrWhiteSpace(serverName)
                        ? $"CharacterCapture: Saved {characters.Count} chars for '{accountName}' on '{serverName}'."
                        : $"CharacterCapture: Saved {characters.Count} chars for '{accountName}' (no server name in context).");
            }
            else
            {
                RynthLog.Info("CharacterCapture: Character list observed but no account name was available; character cache not written.");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Info($"CharacterCapture: Save error: {ex.Message}");
        }
    }

    private static void ScheduleAutoLoginIfRequested(List<string> characters, int slotCount)
    {
        (_, _, string targetCharacter) = ReadLaunchContext();
        if (string.IsNullOrWhiteSpace(targetCharacter))
            return;

        // If the client is already past character-select, do NOT schedule any
        // auto-login work. The 0xF658 character-list packet can arrive in
        // transitional states (logout-to-char-select, hot-reload replay, etc.),
        // and starting a 10-second polling loop that pokes at native LogOn
        // pointers while the player is in-game has been correlated with AC
        // self-exiting via MSVCR70 a few seconds later.
        if (LoginLifecycleHooks.HasObservedLoginComplete)
        {
            RynthLog.Verbose($"CharacterCapture: Skipping auto-login schedule for '{targetCharacter}' - login already complete.");
            Interlocked.Exchange(ref _autoLoginEverFired, 1);
            return;
        }

        if (CharacterManagementHooks.TryGetCurrentMode(out int currentMode) && currentMode == 0x10000008 /* GamePlayUI */)
        {
            RynthLog.Compat($"CharacterCapture: Skipping auto-login schedule for '{targetCharacter}' - client is already in GamePlayUI.");
            Interlocked.Exchange(ref _autoLoginEverFired, 1);
            return;
        }

        // One-shot per process: if auto-login already fired (and the user has
        // since logged back out), don't re-trigger. This keeps a manual logout
        // from instantly re-logging the same character.
        if (Interlocked.CompareExchange(ref _autoLoginEverFired, 1, 0) != 0)
        {
            RynthLog.Verbose($"CharacterCapture: Auto-login already fired this session — skipping for '{targetCharacter}'.");
            return;
        }

        if (Interlocked.Exchange(ref _autoLoginScheduled, 1) != 0)
            return;

        List<string> fallbackCharacters = characters.Count > 0
            ? new List<string>(characters)
            : [];
        int finalSlots = slotCount > 0 ? slotCount : fallbackCharacters.Count;
        int fallbackIndex = fallbackCharacters.FindIndex(c => CharacterManagementHooks.CharacterNamesMatch(c, targetCharacter));
        int logoDelayMs = LogoBypassHooks.GetRecommendedAutoLoginDelayMs();
        int scheduledDelayMs = Math.Max(AutoLoginDelayMs, logoDelayMs);

        var thread = new Thread(() =>
        {
            try
            {
                Thread.Sleep(scheduledDelayMs);
                PerformAutoLogin(fallbackCharacters, finalSlots, targetCharacter);
            }
            finally
            {
                Interlocked.Exchange(ref _autoLoginScheduled, 0);
            }
        })
        {
            Name = "RynthCore.AutoLogin",
            IsBackground = true
        };
        thread.Start();

        RynthLog.Verbose(
            fallbackIndex >= 0
                ? $"CharacterCapture: Auto-login scheduled for '{targetCharacter}' in {scheduledDelayMs}ms (native direct login first, fallback slot index {fallbackIndex}, logoDelay={logoDelayMs}ms)."
                : $"CharacterCapture: Auto-login scheduled for '{targetCharacter}' in {scheduledDelayMs}ms (native direct login only so far, logoDelay={logoDelayMs}ms).");
    }

    private static void PerformAutoLogin(List<string> fallbackCharacters, int slotCount, string targetCharacter)
    {
        string lastDirectStatus = "Direct login did not run.";
        for (int attempt = 1; attempt <= DirectAutoLoginAttempts; attempt++)
        {
            if (LoginLifecycleHooks.HasObservedLoginComplete)
            {
                RynthLog.Verbose($"CharacterCapture: Auto-login for '{targetCharacter}' skipped because login is already complete.");
                return;
            }

            // Hard early-out: if the client has already entered GamePlayUI, the player
            // is in the world. Continuing to poll TryLogOnCharacter (which keeps
            // dereferencing UIFlow + PlayerSystem pointers) has been correlated with
            // AC self-exiting via MSVCR70 ~20s later. Treat this as "done", not a
            // failure to be retried.
            if (CharacterManagementHooks.TryGetCurrentMode(out int gpMode) && gpMode == 0x10000008 /* GamePlayUI */)
            {
                RynthLog.Compat($"CharacterCapture: Auto-login for '{targetCharacter}' aborted on attempt {attempt}/{DirectAutoLoginAttempts} - client is already in GamePlayUI. Last direct status: {lastDirectStatus}");
                return;
            }

            if (CharacterManagementHooks.TryLogOnCharacter(targetCharacter, out string matchedCharacter, out uint avatarId, out string directStatus))
            {
                RynthLog.Verbose(
                    $"CharacterCapture: Direct auto-login succeeded for '{matchedCharacter}' (target '{targetCharacter}', avatar 0x{avatarId:X8}) on attempt {attempt}/{DirectAutoLoginAttempts}.");
                return;
            }

            lastDirectStatus = directStatus;

            // Belt-and-braces: TryLogOnCharacter itself reports "Client is already in
            // GamePlayUI" when mode==GamePlayUI. If we somehow raced past the
            // pre-call mode check above, still bail on the status.
            if (directStatus.IndexOf("already in GamePlayUI", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                RynthLog.Compat($"CharacterCapture: Auto-login for '{targetCharacter}' aborted on attempt {attempt}/{DirectAutoLoginAttempts} - {directStatus}");
                return;
            }

            if (attempt < DirectAutoLoginAttempts)
                Thread.Sleep(DirectAutoLoginAttemptDelayMs);
        }

        RynthLog.Compat($"CharacterCapture: Direct auto-login did not succeed for '{targetCharacter}' - {lastDirectStatus}");

        int index = fallbackCharacters.FindIndex(c => CharacterManagementHooks.CharacterNamesMatch(c, targetCharacter));
        if (index < 0 || slotCount <= 0)
        {
            RynthLog.Compat($"CharacterCapture: No click fallback is available for '{targetCharacter}' after direct login failure.");
            return;
        }

        // Final guard: the click fallback drives a foreground window via PostMessage.
        // If, by the time we reach the fallback, the client is in GamePlayUI (eg the
        // user clicked through during the retry window), do NOT spam clicks at the
        // game viewport.
        if (CharacterManagementHooks.TryGetCurrentMode(out int fbMode) && fbMode == 0x10000008 /* GamePlayUI */)
        {
            RynthLog.Compat($"CharacterCapture: Skipping click fallback for '{targetCharacter}' - client is already in GamePlayUI.");
            return;
        }

        IntPtr hwnd = WaitForGameWindow();
        if (hwnd == IntPtr.Zero)
        {
            RynthLog.Compat($"CharacterCapture: Auto-login for '{targetCharacter}' - no visible game window found, skipping.");
            return;
        }

        SetForegroundWindow(hwnd);

        float nameSize = (YBottomOfBox - YTopOfBox) / (float)slotCount;
        int yOffset = (int)(YTopOfBox + (nameSize / 2.0f) + (nameSize * index));

        for (int attempt = 1; attempt <= AutoLoginAttempts; attempt++)
        {
            // Login made it through (either by our earlier click or by the user) — stop clicking.
            if (LoginLifecycleHooks.HasObservedLoginComplete)
            {
                RynthLog.Verbose($"CharacterCapture: Click fallback for '{targetCharacter}' aborted on attempt {attempt}/{AutoLoginAttempts} - login complete.");
                return;
            }

            if (CharacterManagementHooks.TryGetCurrentMode(out int clickMode) && clickMode == GamePlayUIMode)
            {
                RynthLog.Compat($"CharacterCapture: Click fallback for '{targetCharacter}' aborted on attempt {attempt}/{AutoLoginAttempts} - client is already in GamePlayUI.");
                return;
            }

            // Only click while char-select is actually visible. The 0xF658 packet can arrive
            // before the UI has transitioned (logos still dismissing, etc.) — wasting clicks
            // outside char-select is what made earlier runs "give up too soon."
            bool charSelectReady = CharacterManagementHooks.TryGetCurrentMode(out int curMode) && curMode == CharacterManagementUIMode;
            if (!charSelectReady)
            {
                RynthLog.Verbose($"CharacterCapture: Click fallback waiting for char-select UI on attempt {attempt}/{AutoLoginAttempts} (mode=0x{curMode:X8}).");
                Thread.Sleep(AutoLoginAttemptDelayMs);
                continue;
            }

            RynthLog.Verbose($"CharacterCapture: Auto-login attempt {attempt}/{AutoLoginAttempts} for '{targetCharacter}' at ({XCharList}, {yOffset}).");

            PostMouseClick(hwnd, XCharList, yOffset);
            Thread.Sleep(AutoLoginDoubleClickGapMs);
            PostMouseClick(hwnd, XCharList, yOffset);

            if (attempt < AutoLoginAttempts)
                Thread.Sleep(AutoLoginAttemptDelayMs);
        }

        RynthLog.Verbose($"CharacterCapture: Auto-login double-click sequence complete for '{targetCharacter}'.");
    }

    private static IntPtr WaitForGameWindow()
    {
        uint pid = GetCurrentProcessId();
        int elapsedMs = 0;
        while (elapsedMs < AutoLoginWindowWaitMs)
        {
            IntPtr hwnd = FindVisibleProcessWindow(pid);
            if (hwnd != IntPtr.Zero)
                return hwnd;

            Thread.Sleep(AutoLoginWindowPollMs);
            elapsedMs += AutoLoginWindowPollMs;
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindVisibleProcessWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == pid && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static IntPtr MakeLParam(int x, int y) =>
        (IntPtr)unchecked((uint)((y << 16) | (x & 0xFFFF)));

    private static void PostMouseClick(IntPtr hwnd, int x, int y)
    {
        IntPtr lParam = MakeLParam(x, y);
        PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lParam);
        PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)0x0001, lParam);
        Thread.Sleep(80);
        PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
    }
}
