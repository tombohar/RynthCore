// ============================================================================
//  RynthCore.Engine - Compatibility/VaultFeed.cs
//  The Aelrynth account vault (the Aeshnidae.Mule mod) for the Inventory panel's Vault view.
//
//  The vault is storage the retail client is never shown, so the contents exist only on the server:
//  "/mule-data [page]" answers one "~ael1" chat line a page (Net/AelrynthVaultWire.cs parses it).
//  This feed asks for the pages, reads them, hides them from chat, and publishes the whole vault.
//  Built on the same plan as MasteryFeed (read that for the reasoning behind the hiding):
//
//    - only ever on Aelrynth (ServerInfo.IsAelrynth): another server would answer "Unknown command";
//      if Aelrynth does anyway (the mod switched off) the feed stops asking until the next login;
//    - asked when the Vault view opens (unless the last read is under 30 s old) and on request;
//    - a reply to OUR request (sent in the last 15 s) is eaten before AC draws it; a line the player
//      asked for by typing /mule-data is read but left in chat;
//    - a vault the server says is still loading is asked again a few times, and is never shown empty;
//    - a vault that changed between pages (a deposit mid-read) is read again, not shown half and half.
//
//  The one action: Withdraw sends "@mule withdraw 0x<id>" (the player chose the item), then reads again.
//
//  Threads: MainThreadTick and OnIncomingLine run on AC's main thread (the only place a command is
//  sent). The panel's calls set fields under a small lock; Snapshot is readable anywhere.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Engine.Net;

namespace RynthCore.Engine.Compatibility;

/// <summary>A whole vault as last read. Immutable.</summary>
internal sealed class VaultSnapshot
{
    public required VaultItem[] Items { get; init; }
    public required int Max { get; init; }
    public required long ReceivedMs { get; init; }
}

internal static class VaultFeed
{
    private const int ReplyWindowMs = 15_000, StaleMs = 30_000, RetryLoadingMs = 3000, MaxRetries = 5;
    private const int AfterWithdrawMs = 1500;

    private static readonly object Gate = new();

    // Any thread, under Gate.
    private static long _wantAtMs;          // 0 = no read wanted
    private static string? _command;        // a player action to send (withdraw)
    private static string _commandLabel = "";

    // Main thread only.
    private static uint _playerId;
    private static long _lastSendMs;
    private static int _expectPage = 1;
    private static int _retries;
    private static string? _nextCommand;    // the next page, sent on the next tick
    private static long _nextCommandAtMs;
    private static readonly List<VaultItem> Acc = new();

    private static volatile VaultSnapshot? _snapshot;
    private static long _awaitingUntilMs;
    private static volatile bool _unsupported;
    private static volatile string _status = "not asked yet";

    /// <summary>The last whole read, or null (none this login). Any thread.</summary>
    public static VaultSnapshot? Snapshot => _snapshot;
    public static bool Unsupported => _unsupported;
    public static string Status => _status;
    public static bool Awaiting => Environment.TickCount64 < Volatile.Read(ref _awaitingUntilMs);

    /// <summary>
    /// The Vault button belongs here: on Aelrynth, with our chat hook in (it hides the reply). It stays when the
    /// server has no vault command yet (<see cref="Unsupported"/>): the view says so, and Refresh asks again.
    /// </summary>
    public static bool Available => ServerInfo.IsAelrynth && ChatCallbackHooks.IncomingInstalled;

    /// <summary>The Vault view opened: read unless the last read is fresh. Any thread.</summary>
    public static void ViewOpened()
    {
        if (!Available || _unsupported) return;
        VaultSnapshot? s = _snapshot;
        if (s == null || Environment.TickCount64 - s.ReceivedMs > StaleMs)
            RequestAt(Environment.TickCount64);
    }

    /// <summary>Read now (the main thread paces it). Any thread.</summary>
    public static void RequestRefresh()
    {
        if (!Available) return;
        _unsupported = false;   // the player asked again: the vault mod may have gone live since
        RequestAt(Environment.TickCount64);
    }

    /// <summary>Takes one item out: "/mule withdraw 0x&lt;id&gt;", then reads the vault again. Any thread.</summary>
    public static bool Withdraw(VaultItem item)
    {
        if (!Available || item.Id == 0) return false;
        lock (Gate)
        {
            if (_command != null) return false;
            _command = "@mule withdraw 0x" + item.Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            _commandLabel = item.Name;
        }
        return true;
    }

    private static void RequestAt(long atMs)
    {
        lock (Gate)
        {
            if (_wantAtMs == 0 || atMs < _wantAtMs)
                _wantAtMs = Math.Max(1, atMs);
        }
    }

    // ── AC's main thread ────────────────────────────────────────────────

    internal static void MainThreadTick(bool playerLive)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        long now = Environment.TickCount64;
        uint me = LogoutLifecycleHooks.HasObservedLogout ? 0 : ClientHelperHooks.GetPlayerId();
        if (me != _playerId)
            NewLogin(me);
        if (me == 0 || !playerLive)
            return;

        // The next page of a read under way goes out at once; a retry waits for its time.
        if (_nextCommand != null && now >= _nextCommandAtMs)
        {
            string cmd = _nextCommand;
            _nextCommand = null;
            if (ChatCommandDispatcher.SendServerCommand(cmd))
                Volatile.Write(ref _awaitingUntilMs, now + ReplyWindowMs);
            else
                _status = "could not send the request";
            return;
        }

        // Idle: two loads, no lock.
        if (Volatile.Read(ref _wantAtMs) == 0 && Volatile.Read(ref _command) == null)
            return;
        string? action;
        string label;
        long wantAt;
        lock (Gate) { action = _command; label = _commandLabel; wantAt = _wantAtMs; }

        if (!Available)
        {
            lock (Gate) { _wantAtMs = 0; _command = null; }
            return;
        }

        if (action != null)
        {
            lock (Gate) { _command = null; }
            if (ChatCommandDispatcher.SendServerCommand(action))
            {
                _status = $"asked the server to give back {label}";
                RynthLog.Compat($"VaultFeed: sent {action}");
                RequestAt(now + AfterWithdrawMs);
            }
            else
            {
                _status = "could not send the withdraw";
            }
            return;
        }

        if (wantAt != 0 && now >= wantAt && !Awaiting && now - _lastSendMs >= 1000)
        {
            lock (Gate) { _wantAtMs = 0; }
            _lastSendMs = now;
            Acc.Clear();
            _expectPage = 1;
            _retries = 0;
            if (ChatCommandDispatcher.SendServerCommand(AelrynthVaultWire.PageCommand(1)))
            {
                Volatile.Write(ref _awaitingUntilMs, now + ReplyWindowMs);
                _status = "reading the vault...";
            }
            else
            {
                _status = "could not send the request (the client's talk function isn't bound)";
            }
        }
    }

    private static void NewLogin(uint me)
    {
        _playerId = me;
        _snapshot = null;
        _unsupported = false;
        _nextCommand = null;
        Acc.Clear();
        _expectPage = 1;
        _retries = 0;
        Volatile.Write(ref _awaitingUntilMs, 0);
        lock (Gate) { _wantAtMs = 0; _command = null; }
        _status = me != 0 ? "not asked yet" : "logged out";
    }

    /// <summary>
    /// Every incoming chat line, from the chat hook (AC's main thread), before AC draws it.
    /// True = eat it (our own request's reply). Never throws.
    /// </summary>
    internal static bool OnIncomingLine(string line)
    {
        try
        {
            if (line.Length < 8 || !ServerInfo.IsAelrynth)
                return false;
            bool awaiting = Awaiting;
            if (AelrynthVaultWire.IsMuleLine(line))
            {
                // A reply nobody here asked for (the RynthInventory plugin's own read, or one the player typed)
                // is left for them; the plugin eats its own.
                if (!awaiting)
                    return false;
                OnReply(line);
                return true;
            }
            if (awaiting && AelrynthVaultWire.IsUnknownCommand(line))
            {
                _unsupported = true;
                Volatile.Write(ref _awaitingUntilMs, 0);
                _status = "the vault is not open on this server yet";
                RynthLog.Compat("VaultFeed: the server answered Unknown command: mule-data - not asking again this login.");
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void OnReply(string line)
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _awaitingUntilMs, 0);
        if (!AelrynthVaultWire.TryParse(line, out VaultPage p))
        {
            _status = "the server's reply could not be read";
            return;
        }
        if (!p.Ready)
        {
            // Still loading on the server: ask again shortly, never show it as empty.
            if (p.Why == "loading" && ++_retries <= MaxRetries)
            {
                _status = "the vault is still loading on the server";
                Restart(now + RetryLoadingMs);
            }
            else
            {
                _status = p.Why.Length > 0 ? $"the vault is not available ({p.Why})" : "the vault is not available";
            }
            return;
        }
        if (p.Page != _expectPage)
            return;   // a page twice, or out of turn: wait for the one asked

        Acc.AddRange(p.Items);
        if (p.Page < p.Pages)
        {
            _expectPage = p.Page + 1;
            _nextCommand = AelrynthVaultWire.PageCommand(_expectPage);
            _nextCommandAtMs = now;
            return;
        }
        // The last page. A total that disagrees with what was read means the vault changed under the read.
        if (p.Count != Acc.Count && ++_retries <= MaxRetries)
        {
            _status = "the vault changed while reading; reading again";
            Restart(now + 500);
            return;
        }
        _snapshot = new VaultSnapshot { Items = Acc.ToArray(), Max = p.Max, ReceivedMs = now };
        Acc.Clear();
        _status = $"{_snapshot.Items.Length} item(s)";
    }

    private static void Restart(long atMs)
    {
        Acc.Clear();
        _expectPage = 1;
        _nextCommand = AelrynthVaultWire.PageCommand(1);
        _nextCommandAtMs = atMs;
    }
}
