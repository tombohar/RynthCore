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
    private const int AutoLoginDelayMs = 800;
    private const int AutoLoginWindowWaitMs = 10000;
    private const int AutoLoginWindowPollMs = 250;
    private const int AutoLoginAttempts = 3;
    private const int AutoLoginAttemptDelayMs = 600;
    private const int AutoLoginDoubleClickGapMs = 100;
    private const int DirectAutoLoginAttempts = 40;
    private const int DirectAutoLoginAttemptDelayMs = 250;

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

            RynthLog.Verbose($"CharacterCapture: Parsed {characters.Count} chars ({slotCount} slots): {string.Join(", ", characters)}");

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

        if (Interlocked.Exchange(ref _autoLoginScheduled, 1) != 0)
            return;

        List<string> fallbackCharacters = characters.Count > 0
            ? new List<string>(characters)
            : [];
        int finalSlots = slotCount > 0 ? slotCount : fallbackCharacters.Count;
        int fallbackIndex = fallbackCharacters.FindIndex(c => string.Equals(c, targetCharacter, StringComparison.OrdinalIgnoreCase));
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

            if (CharacterManagementHooks.TryLogOnCharacter(targetCharacter, out string matchedCharacter, out uint avatarId, out string directStatus))
            {
                RynthLog.Verbose(
                    $"CharacterCapture: Direct auto-login succeeded for '{matchedCharacter}' (target '{targetCharacter}', avatar 0x{avatarId:X8}) on attempt {attempt}/{DirectAutoLoginAttempts}.");
                return;
            }

            lastDirectStatus = directStatus;
            if (attempt < DirectAutoLoginAttempts)
                Thread.Sleep(DirectAutoLoginAttemptDelayMs);
        }

        RynthLog.Compat($"CharacterCapture: Direct auto-login did not succeed for '{targetCharacter}' - {lastDirectStatus}");

        int index = fallbackCharacters.FindIndex(c => string.Equals(c, targetCharacter, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || slotCount <= 0)
        {
            RynthLog.Compat($"CharacterCapture: No click fallback is available for '{targetCharacter}' after direct login failure.");
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
