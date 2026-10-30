// ============================================================================
//  RynthCore.Engine - Compatibility/AcKeymapLive.cs
//
//  Changing the player's keys so every running client follows (Tom 2026-10-22:
//  the item bar's keys are the player's retail keys, and they change together;
//  with the retail toolbar back, its keys have changed too). Plugin API v90
//  KeyCapture / SetActionKey, for RynthDefective's bar, and /rc keymap.
//
//  AC keeps its keymap in memory: it loads the file when it starts and writes
//  it back when it closes, and its own Configure Keyboard changes only the
//  memory. So a change is, on AC's game thread after its tick:
//    1. this client's keys go to the file first (CInputManager_WIN32::
//       SaveKeyMap, vtable +0x30, what Configure Keyboard's "save as" does), so
//       a key the player changed there isn't lost;
//    2. the file's text is edited (AcKeymapText: the key comes off whatever had
//       it, and off RynthCore's own hotkeys, which would take it before AC);
//    3. it is loaded again the way AC loads it when it starts (gmClient::
//       InitKeymap): InitializeKeymap (+0x08, clears), AddKeyMap_(path) (+0x28,
//       the file), AddKeyMap(0x10000001) and AddKeyMap(1) (+0x2C, the game's
//       fixed maps from the dat);
//    4. the edits made (AcKeymapText.Edit: bind K to A / K off A / K off
//       everything) go in "<keymap>.rynthcore" beside it, one change per stamp.
//
//  Following (2026-10-27, a player: keys deleted in Configure Keyboard came back
//  mid-fight). Every other client looks at "<keymap>.rynthcore" once a second
//  (and before a change of its own) and makes each change it hasn't made yet to
//  ITS OWN keys: AC writes them (SaveKeyMap) to a scratch file in %TEMP%, the
//  same edits go in that text, and the scratch is loaded as above, then
//  deleted. Its keys changed in Configure Keyboard and not saved stay; the
//  shared change lands. It never loads the shared file for another client's
//  change (that file holds the changing client's keys, not its own), so a file
//  a closing client wrote (AC's own save) is never loaded either. Anything that
//  fails before the load leaves the keys as they are; an edited scratch that
//  didn't load is followed by the client's own keys as AC wrote them.
//  What was made last ("RYNTHCORE_KEYMAP_SEEN", the process's environment)
//  outlives an engine hot reload, so a reload makes nothing again; a new client
//  makes only changes newer than its start (AC loaded the file after older ones).
//  An older RynthCore's marker (the file's time alone: what changed isn't known)
//  is logged and not loaded: loading the whole file is what lost the keys.
//
//  A key for one of RynthCore's own commands (RynthDefective's slots past the
//  game's 18: "/rdef use 2") is an engine hotkey; it takes its key the same way
//  (off AC's actions too, so no key shows on two slots). Op.BindCommandOnly
//  (SetActionKey op 3, for RynthDefective's independent bar) sets the engine
//  hotkey alone: AC's keymap isn't touched, no client loads anything, and the
//  hotkey simply answers first.
//
//  ICIDM::s_cidm (the CInputManager*, by code-xref), the four functions and
//  PStringBase<char>(const char*) (0x00401340) are found by pattern only, and
//  the input manager's vtable must hold exactly those functions, or nothing is
//  called. The first change backs the file up as "<keymap>.before-rynthcore".
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Engine.Hotkeys;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcKeymapLive
{
    // ClientUISystem::UseTime: mov ebp,[ICIDM::s_cidm]; push esi; push edi; lea eax,[esp+0x10] (as LogoffOriginProbe).
    private static readonly byte?[] InputManagerXref = [0x8B, 0x2D, null, null, null, null, 0x56, 0x57, 0x8D, 0x44, 0x24, 0x10];
    // CInputManager_WIN32::InitializeKeymap: this->ClearKeyMap(); if (m_actionMap) m_actionMap->vt54(); return 1.
    private static readonly byte?[] InitializePattern =
        [0x56, 0x8B, 0xF1, 0x8B, 0x06, 0xFF, 0x50, 0x24, 0x8B, 0x8E, 0x8C, 0x00, 0x00, 0x00, 0x85, 0xC9, 0x5E, 0x74, 0x05, 0x8B, 0x11, 0xFF, 0x52, 0x54, 0xB0, 0x01, 0xC3];
    // CInputManager_WIN32::AddKeyMap_(PStringBase<char> file): PFileParser::LoadFromFile, CMasterInputMap::Merge.
    private static readonly byte?[] LoadFilePattern =
        [0xA1, null, null, null, null, 0x81, 0xEC, 0x68, 0x01, 0x00, 0x00, 0x85, 0xC0, 0x56, 0x57, 0x8B, 0xF9, 0x75, 0x0A, 0xB9, null, null, null, null, 0xE8];
    // CInputManager_WIN32::AddKeyMap(uint id): DBObj::GetByEnum(id, 10, 0x1D), CMasterInputMap::Merge.
    private static readonly byte?[] LoadDbPattern =
        [0x8B, 0x44, 0x24, 0x04, 0x56, 0x57, 0x6A, 0x1D, 0x6A, 0x0A, 0x50, 0x8B, 0xF9, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC4, 0x0C, 0x32, 0xC0, 0x85, 0xF6, 0x74, 0x1B, 0x53, 0x6A, 0x01, 0x56];
    // CInputManager_WIN32::SaveKeyMap(PStringBase<char> file): CMasterInputMap::ToFileNode, PFileParser::SaveToFile.
    private static readonly byte?[] SavePattern =
        [0xA1, null, null, null, null, 0x83, 0xEC, 0x20, 0x85, 0xC0, 0x56, 0x57, 0x8B, 0xF9, 0x75, 0x0A, 0xB9, null, null, null, null, 0xE8, null, null, null, null, 0x8B, 0x0D];
    // PStringBase<char>::PStringBase<char>(const char*): the string's characters, refcount at -0x10.
    private static readonly byte?[] StringPattern =
        [0x56, 0x8B, 0x74, 0x24, 0x08, 0x85, 0xF6, 0x57, 0x8B, 0xF9, 0x74, 0x35, 0x80, 0x3E, 0x00, 0x74, 0x30, 0x8B, 0xC6, 0x8D, 0x50, 0x01,
         0x8A, 0x08, 0x40, 0x84, 0xC9, 0x75, 0xF9, 0x2B, 0xC2, 0x50, 0x8B, 0xCF, 0xE8, null, null, null, null, 0x8B, 0x17];

    private const int VtInitialize = 0x08, VtLoadFile = 0x28, VtLoadDb = 0x2C, VtSave = 0x30;
    private const uint FixedMapA = 0x10000001, FixedMapB = 1;   // the dat's maps gmClient::InitKeymap merges after the file

    private static IntPtr _cidmSlot, _init, _loadFile, _loadDb, _save, _string;
    private static bool _initialized;
    private static string _status = "Not initialized.";
    public static string Status => _status;
    public static bool CanChange => _cidmSlot != IntPtr.Zero && _init != IntPtr.Zero && _loadFile != IntPtr.Zero
                                    && _loadDb != IntPtr.Zero && _save != IntPtr.Zero && _string != IntPtr.Zero;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _status = "acclient.exe not available.";
            return;
        }
        IntPtr Fn(string name, byte?[] pattern)
        {
            var r = HookResolver.Resolve(text, "AcKeymapLive." + name, pattern, 0);
            return r.Success && r.Source == HookResolver.ResolveSource.PatternScan ? r.Address : IntPtr.Zero;
        }
        var slot = HookResolver.ResolveData(text, "AcKeymapLive.ICIDM_s_cidm", InputManagerXref, 2, 0);
        _cidmSlot = slot.Success && slot.Source == HookResolver.ResolveSource.PatternScan ? slot.Address : IntPtr.Zero;
        _init = Fn("InitializeKeymap", InitializePattern);
        _loadFile = Fn("AddKeyMap_", LoadFilePattern);
        _loadDb = Fn("AddKeyMap", LoadDbPattern);
        _save = Fn("SaveKeyMap", SavePattern);
        _string = Fn("PStringBase_char_ctor", StringPattern);
        _status = CanChange ? "ready." : "missing " + string.Join(", ", Missing()) + ": keys can't be changed from here.";
        RynthLog.Compat("AcKeymapLive: " + _status);
    }

    private static IEnumerable<string> Missing()
    {
        if (_cidmSlot == IntPtr.Zero) yield return "the input manager";
        if (_init == IntPtr.Zero) yield return "InitializeKeymap";
        if (_loadFile == IntPtr.Zero) yield return "AddKeyMap_";
        if (_loadDb == IntPtr.Zero) yield return "AddKeyMap";
        if (_save == IntPtr.Zero) yield return "SaveKeyMap";
        if (_string == IntPtr.Zero) yield return "PStringBase";
    }

    /// <summary>The CInputManager, when its vtable holds the functions found (else zero: never called).</summary>
    private static IntPtr InputManager()
    {
        if (!CanChange || !ClientObjectHooks.IsReadableSpan(_cidmSlot, 4)) return IntPtr.Zero;
        IntPtr im = Marshal.ReadIntPtr(_cidmSlot);
        if (im == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(im, 4)) return IntPtr.Zero;
        IntPtr vt = Marshal.ReadIntPtr(im);
        if (vt == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(vt, VtSave + 4)) return IntPtr.Zero;
        bool ok = Marshal.ReadIntPtr(vt, VtInitialize) == _init && Marshal.ReadIntPtr(vt, VtLoadFile) == _loadFile
                  && Marshal.ReadIntPtr(vt, VtLoadDb) == _loadDb && Marshal.ReadIntPtr(vt, VtSave) == _save;
        if (!ok && !_vtWarned)
        {
            _vtWarned = true;
            RynthLog.Compat($"AcKeymapLive: the input manager's vtable 0x{vt.ToInt32():X8} isn't the expected one; keys won't be reloaded.");
        }
        return ok ? im : IntPtr.Zero;
    }

    private static bool _vtWarned;

    /// <summary>An AC PStringBase&lt;char&gt; of the path (one reference, handed to the callee); zero for a path AC can't take.</summary>
    private static IntPtr AcString(string path)
    {
        string p = path;
        if (!IsAscii(p))
        {
            // AC opens files with the ANSI functions: a name with other letters goes by its short (8.3) form.
            var sb = new StringBuilder(520);
            if (GetShortPathNameW(path, sb, sb.Capacity) > 0) p = sb.ToString();
            if (!IsAscii(p)) return IntPtr.Zero;
        }
        byte[] bytes = new byte[p.Length + 1];
        for (int i = 0; i < p.Length; i++) bytes[i] = (byte)p[i];
        IntPtr s = IntPtr.Zero;
        fixed (byte* b = bytes)
            ((delegate* unmanaged[Thiscall]<IntPtr*, byte*, IntPtr*>)_string)(&s, b);
        return s;
    }

    private static bool IsAscii(string s)
    {
        foreach (char c in s) if (c >= 0x80 || c == 0) return false;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);

    // ── Following another client's change (every client) ─────────────────────

    private const string SeenEnv = "RYNTHCORE_KEYMAP_SEEN";   // the newest change made here; the process's, so it outlives a hot reload
    private const int KeepChanges = 64;                      // changes kept in "<keymap>.rynthcore"

    private static long _seenStamp = -1;                     // -1: not read yet (SeenStamp)
    private static string _markerFile = string.Empty;
    private static DateTime _markerUtc = DateTime.MinValue;
    private static long _nextCheckMs;
    private static readonly DateTime ProcessStartUtc = SafeStartUtc();

    private static DateTime SafeStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    private static string MarkerPath(string keymap) => keymap + ".rynthcore";

    /// <summary>
    /// The newest change this client has made. After an engine hot reload: what the last generation made (so the
    /// reload makes nothing again). A new client: its start (AC loaded the file after anything older).
    /// </summary>
    private static long SeenStamp
    {
        get
        {
            if (_seenStamp < 0)
            {
                string? carried = null;
                try { carried = Environment.GetEnvironmentVariable(SeenEnv); } catch { }
                long start = ProcessStartUtc.Ticks;
                _seenStamp = long.TryParse(carried, NumberStyles.None, CultureInfo.InvariantCulture, out long s) && s > start ? s : start;
            }
            return _seenStamp;
        }
    }

    private static void MarkSeen(long stamp)
    {
        if (stamp <= SeenStamp) return;
        _seenStamp = stamp;
        try { Environment.SetEnvironmentVariable(SeenEnv, stamp.ToString(CultureInfo.InvariantCulture)); } catch { }
    }

    /// <summary>Game thread, after AC's tick: another client's change (once a second, and before one made here), then the queued changes.</summary>
    public static void Tick()
    {
        if (!LoginLifecycleHooks.HasObservedLoginComplete) return;
        if (CanChange)
        {
            long now = Environment.TickCount64;
            // Before a change of this client's own too: its keys, saved first, then hold the other client's change.
            if (now >= _nextCheckMs || !Requests.IsEmpty)
            {
                _nextCheckMs = now + 1000;
                try { FollowOthers(); }
                catch (Exception ex) { RynthLog.Compat($"AcKeymapLive: following another client's key change failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        // An engine hotkey alone (BindCommandOnly, a command's unbind) needs none of AC's functions.
        DrainRequests();
        if (CanChange && _dumpTo is { } dump)
        {
            _dumpTo = null;
            string said = SaveOwn(dump) ? $"AC's keys in memory written to {dump}." : "AC couldn't write its keys there.";
            RynthLog.Compat("AcKeymapLive: " + said);
            AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + said, 1);
        }
    }

    /// <summary>The changes in "&lt;keymap&gt;.rynthcore" this client hasn't made yet, made to its own keys. Game thread.</summary>
    private static void FollowOthers()
    {
        string file = AcKeymap.FilePath;
        if (file.Length == 0) return;
        string marker = MarkerPath(file);
        if (!File.Exists(marker)) return;
        DateTime w = File.GetLastWriteTimeUtc(marker);
        if (w == _markerUtc && string.Equals(marker, _markerFile, StringComparison.OrdinalIgnoreCase)) return;
        string text;
        try { text = File.ReadAllText(marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // being written: the next look
        _markerFile = marker;
        _markerUtc = w;

        List<AcKeymapText.Change> log = AcKeymapText.ParseChanges(text, out bool older);
        if (older)
        {
            // It says the file changed, not what: loading the whole file would throw away this client's keys changed in
            // Configure Keyboard and not saved (the bug this replaced). The change is in the file for the next start.
            if (w > ProcessStartUtc)
                RynthLog.Compat("AcKeymapLive: an older RynthCore changed a key in another client; not loaded here (the whole file would " +
                                "replace keys changed in this client and not saved). It is in the keymap for the next start.");
            return;
        }
        List<AcKeymapText.Change> pending = AcKeymapText.Pending(log, SeenStamp, Environment.ProcessId);
        long newest = AcKeymapText.Newest(log);
        if (pending.Count > 0)
        {
            try { Follow(pending); }
            catch (Exception ex)
            {
                RynthLog.Compat($"AcKeymapLive: another client changed keys; not made here ({ex.GetType().Name}: {ex.Message}), this client's keys are left as they are.");
            }
        }
        MarkSeen(newest);   // made or not (logged), each change is tried once
    }

    /// <summary>
    /// Another client's changes, made to this client's own keys: AC writes them to a scratch file, the same edits
    /// go in, and the scratch is loaded as AC loads its keymap. Anything failing before the load: logged, keys left
    /// as they are. Game thread.
    /// </summary>
    private static void Follow(List<AcKeymapText.Change> pending)
    {
        var what = new List<string>();
        foreach (AcKeymapText.Change c in pending)
            foreach (AcKeymapText.Edit e in c.Edits) what.Add(AcKeymapText.Describe(e));
        string changes = string.Join("; ", what);

        IntPtr im = InputManager();
        if (im == IntPtr.Zero)
        {
            RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); not made here: the input manager isn't there.");
            return;
        }
        string scratch = Path.Combine(Path.GetTempPath(), $"rynthcore-keymap-{Environment.ProcessId}.keymap");
        try
        {
            // There first, so a folder name AC's file functions can't take has a short (8.3) form.
            File.WriteAllBytes(scratch, Array.Empty<byte>());
            if (!SaveOwn(scratch))
            {
                RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); AC couldn't write this client's keys to {scratch}, so they are left as they are.");
                return;
            }
            // Latin-1: AC's bytes back exactly as it wrote them (the edits are ASCII).
            string own = File.ReadAllText(scratch, Encoding.Latin1);
            List<string> lines = AcKeymapText.Split(own, out string nl);
            if (AcKeymapText.Bindings(lines).Count == 0)
            {
                RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); AC wrote no keys for this client, so they are left as they are.");
                return;
            }
            int r = AcKeymapText.ApplyChanges(lines, pending, out string err);
            if (r < 0)
            {
                RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); it can't be made here ({err}), so this client's keys are left as they are.");
                return;
            }
            if (r == 0)
            {
                RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); this client has them already.");
                return;
            }
            File.WriteAllText(scratch, AcKeymapText.Join(lines, nl), Encoding.Latin1);
            int loaded = LoadKeys(im, scratch);
            if (loaded > 0)
            {
                AcKeymap.Invalidate();
                RynthLog.Compat($"AcKeymapLive: another client changed keys; made here too, this client's own keys kept ({changes}).");
                return;
            }
            if (loaded < 0)
            {
                RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); AC can't open {scratch}, so this client's keys are left as they are.");
                return;
            }
            // The edited keys didn't load (AC cleared them): this client's own back, as AC wrote them; else the file, as AC starts.
            bool back = false;
            try
            {
                File.WriteAllText(scratch, own, Encoding.Latin1);
                back = LoadKeys(im, scratch) > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            RynthLog.Compat($"AcKeymapLive: another client changed keys ({changes}); AC didn't load them here - " +
                            (back ? "this client's own keys loaded again." : "nor this client's own keys: loading the keymap file."));
            if (!back) Reload(AcKeymap.FilePath, "this client's own keys didn't load");
            AcKeymap.Invalidate();
        }
        finally
        {
            try { File.Delete(scratch); } catch { }
        }
    }

    /// <summary>
    /// AC's keys from <paramref name="file"/> the way gmClient::InitKeymap loads them: InitializeKeymap (clears),
    /// AddKeyMap_(file), the dat's two fixed maps. 1 loaded; 0 the file didn't load (the game's own keys now); -1
    /// nothing done (a name AC can't open: the keys as they were). Game thread.
    /// </summary>
    private static int LoadKeys(IntPtr im, string file)
    {
        IntPtr path = AcString(file);
        if (path == IntPtr.Zero)
        {
            RynthLog.Compat($"AcKeymapLive: AC can't open '{file}' (its name has letters AC's file functions don't take).");
            return -1;
        }
        IntPtr vt = Marshal.ReadIntPtr(im);
        var init = (delegate* unmanaged[Thiscall]<IntPtr, byte>)Marshal.ReadIntPtr(vt, VtInitialize);
        var load = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, byte>)Marshal.ReadIntPtr(vt, VtLoadFile);
        var db = (delegate* unmanaged[Thiscall]<IntPtr, uint, byte>)Marshal.ReadIntPtr(vt, VtLoadDb);
        init(im);
        bool loaded = load(im, path) != 0;
        if (!loaded) init(im);   // as gmClient::InitKeymap: a file that didn't load leaves the game's own keys
        db(im, FixedMapA);
        db(im, FixedMapB);
        return loaded ? 1 : 0;
    }

    /// <summary>Loads the shared keymap file into AC as AC does when it starts (this client's own change). Game thread.</summary>
    private static bool Reload(string file, string why)
    {
        IntPtr im = InputManager();
        if (im == IntPtr.Zero || file.Length == 0) return false;
        int r = LoadKeys(im, file);
        if (r < 0) return false;
        AcKeymap.Invalidate();
        RynthLog.Compat($"AcKeymapLive: keys loaded again from {Path.GetFileName(file)} ({why}){(r > 0 ? "" : " - the file didn't load: the game's default keys")}.");
        return r > 0;
    }

    /// <summary>This client's keys to the file (AC's own writer). Game thread.</summary>
    private static bool SaveOwn(string file)
    {
        IntPtr im = InputManager();
        if (im == IntPtr.Zero) return false;
        IntPtr path = AcString(file);
        if (path == IntPtr.Zero) return false;
        var save = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, byte>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(im), VtSave);
        return save(im, path) != 0;
    }

    // ── Changing (queued from any thread, done on the game thread) ───────────

    /// <summary>SetActionKey's ops (0 is Check). BindCommandOnly: an engine command's hotkey alone, AC's keymap untouched.</summary>
    public enum Op { Bind = 1, Unbind = 2, BindCommandOnly = 3 }

    private readonly record struct Request(string Action, string Key, Op Op, long QueuedMs);

    private static readonly ConcurrentQueue<Request> Requests = new();

    /// <summary>The engine hotkeys' commands look like this: "/rdef use 2".</summary>
    public static bool IsCommand(string action) => action.StartsWith('/');

    private static bool ValidAction(string action) =>
        action.Length is > 0 and <= 64 && (IsCommand(action) || System.Text.RegularExpressions.Regex.IsMatch(action, "^[A-Za-z0-9_]+$"));

    /// <summary>
    /// What <paramref name="keyText"/> ("Ctrl+F5") would take from others if bound to <paramref name="action"/>
    /// (a keymap action "UseQuickSlot_5", or an engine command "/rdef use 2"). 0 free, 1 taken (the message
    /// says from what), -1 can't (the message says why). Any thread.
    /// </summary>
    public static int Check(string action, string keyText, out string message)
    {
        message = string.Empty;
        action = action.Trim();
        if (!ValidAction(action)) { message = "Not an action."; return -1; }
        if (!HotkeyKeys.TryParse(keyText, out KeyCombo combo, out string err)) { message = err; return -1; }
        if (!IsCommand(action) && !CanChange) { message = "This client can't change AC's keys (" + _status + ")"; return -1; }

        var takes = new List<string>();
        string file = AcKeymap.FilePath;
        string? dik = AcKeymapText.DikForVk(combo.Vk);
        if (!IsCommand(action) && dik == null) { message = $"AC's keymap can't hold {combo}."; return -1; }
        if (file.Length > 0 && dik != null)
        {
            try
            {
                List<string> lines = AcKeymapText.Split(File.ReadAllText(file), out _);
                if (AcKeymapText.TryMeta(lines, combo.Mods, out uint meta, out string metaErr))
                {
                    foreach (AcKeymapText.Line l in AcKeymapText.Uses(lines, 0, dik, meta))
                        if (!l.Action.Equals(action, StringComparison.OrdinalIgnoreCase)) takes.Add(AcKeymapText.Describe(l.Action) + " (the game)");
                }
                else if (!IsCommand(action)) { message = metaErr; return -1; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message = "The keymap can't be read right now; try again.";
                return -1;
            }
        }
        foreach (HotkeyBinding b in HotkeyService.Current.Bindings)
            if (b.Combo == combo && !(b.IsCommand && SameCommand(b.Command, action)))
                takes.Add(HotkeyActions.Describe(b) + " (a RynthCore hotkey)");
        if (takes.Count == 0) return 0;
        message = $"{combo} is " + string.Join(", ", takes) + ".";
        return 1;
    }

    /// <summary>
    /// Queues binding or unbinding the key; done on the game thread. False (with why) when it can't be.
    /// <see cref="Op.BindCommandOnly"/>: an engine command ("/rdef use 3") only; its hotkey alone is set (off
    /// RynthCore's other hotkeys), AC's keymap is never touched, so it needs none of AC's keymap functions.
    /// </summary>
    public static bool Enqueue(string action, string keyText, Op op, out string message)
    {
        message = string.Empty;
        action = action.Trim();
        keyText = keyText.Trim();
        if (op is not (Op.Bind or Op.Unbind or Op.BindCommandOnly)) { message = "Unknown op."; return false; }
        if (!ValidAction(action)) { message = "Not an action."; return false; }
        if (op == Op.BindCommandOnly && !IsCommand(action)) { message = "Only a RynthCore command (\"/rdef use 3\") takes a key without AC's keymap."; return false; }
        if (keyText.Length == 0 || keyText.Length > 40) { message = "No key."; return false; }
        if (op is Op.Bind or Op.BindCommandOnly && !HotkeyKeys.TryParse(keyText, out _, out string err)) { message = err; return false; }
        // A command's unbind and BindCommandOnly change only RynthCore's hotkeys.
        if (!CanChange && (!IsCommand(action) || op == Op.Bind)) { message = "This client can't change AC's keys (" + _status + ")"; return false; }
        if (Requests.Count >= 16) { message = "Too many changes at once."; return false; }
        Requests.Enqueue(new Request(action, keyText, op, Environment.TickCount64));
        return true;
    }

    private static volatile string? _dumpTo;

    /// <summary>/rc keymap dump: AC's keys as it holds them in memory, written by AC to <paramref name="path"/> (game thread, next tick).</summary>
    public static void RequestDump(string path) => _dumpTo = path;

    /// <summary>Logout: nothing queued for the old character.</summary>
    public static void Reset()
    {
        while (Requests.TryDequeue(out _)) { }
    }

    private static void DrainRequests()
    {
        while (Requests.TryDequeue(out Request r))
        {
            if (Environment.TickCount64 - r.QueuedMs > 10000) continue;
            string result;
            try { result = Apply(r); }
            catch (Exception ex) { result = $"Changing the key failed: {ex.GetType().Name}: {ex.Message}"; }
            RynthLog.Compat("AcKeymapLive: " + result);
            if (result.Length > 0) AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + result, 1);
        }
    }

    private static string Apply(Request r)
    {
        string file = AcKeymap.FilePath;
        bool command = IsCommand(r.Action);

        if (r.Op == Op.Unbind)
        {
            if (command)
            {
                int n = RemoveHotkeys(b => b.IsCommand && SameCommand(b.Command, r.Action) && KeyMatches(b.Combo, r.Key));
                return n > 0 ? $"{r.Key} no longer does {r.Action}." : $"{r.Action} has no key {r.Key}.";
            }
            if (file.Length == 0) return "There is no AC keymap file to change.";
            Backup(file);
            SaveOwn(file);
            List<string> ul = AcKeymapText.Split(File.ReadAllText(file), out string unl);
            if (!UnbindAc(ul, r.Action, r.Key, out AcKeymapText.Edit unbound)) return $"{AcKeymapText.Describe(r.Action)} has no key {r.Key}.";
            WriteAndLoad(file, ul, unl, new[] { unbound });
            return $"{r.Key} no longer does {AcKeymapText.Describe(r.Action)}.";
        }

        HotkeyKeys.TryParse(r.Key, out KeyCombo combo, out _);
        string? dik = AcKeymapText.DikForVk(combo.Vk);
        if (!command && dik == null) return $"AC's keymap can't hold {combo}.";
        if (!command && file.Length == 0) return "There is no AC keymap file to change.";

        // AC's side: the key comes off AC's other actions (a command's hotkey would take it before AC anyway).
        // BindCommandOnly: the hotkey alone; AC keeps its keys (the hotkey answers first), nothing is loaded anywhere.
        var from = new List<string>();
        if (r.Op != Op.BindCommandOnly && file.Length > 0 && dik != null)
        {
            List<string> lines = AcKeymapText.Split(File.ReadAllText(file), out string nl);
            bool metaOk = AcKeymapText.TryMeta(lines, combo.Mods, out uint meta, out string metaErr);
            if (!command && !metaOk) return metaErr;
            if (metaOk && (!command || AcKeymapText.Uses(lines, 0, dik, meta).Count > 0))
            {
                // This client's keys first (a key changed in Configure Keyboard lives only in memory), then the edit.
                Backup(file);
                if (SaveOwn(file)) lines = AcKeymapText.Split(File.ReadAllText(file), out nl);
                else RynthLog.Compat("AcKeymapLive: couldn't save this client's keys first; changing the file as it is.");
                List<AcKeymapText.Line> taken;
                string mods = AcKeymapText.ModsOf(lines, meta);
                AcKeymapText.Edit edit;
                if (command)
                {
                    taken = AcKeymapText.Take(lines, 0, dik, meta);
                    edit = new AcKeymapText.Edit(AcKeymapText.EditKind.Take, string.Empty, 0, AcKeymapText.Canonical(dik), mods);
                }
                else
                {
                    if (!AcKeymapText.Bind(lines, r.Action, 0, dik, meta, out taken, out string bindErr)) return bindErr;
                    edit = new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, r.Action, 0, AcKeymapText.Canonical(dik), mods,
                        AcKeymapText.GroupOf(lines, r.Action, 0, dik, meta));
                }
                foreach (AcKeymapText.Line l in taken) from.Add(AcKeymapText.Describe(l.Action));
                WriteAndLoad(file, lines, nl, new[] { edit });
            }
        }

        // RynthCore's hotkeys: none other keeps the key; a command gets its binding.
        int hk = RemoveHotkeys(b => b.Combo == combo && !(command && b.IsCommand && SameCommand(b.Command, r.Action)));
        if (hk > 0) from.Add(hk == 1 ? "a RynthCore hotkey" : $"{hk} RynthCore hotkeys");
        if (command && !HotkeyService.Current.Bindings.Exists(b => b.Combo == combo && b.IsCommand && SameCommand(b.Command, r.Action)))
            HotkeyService.Add(new HotkeyBinding(combo, HotkeyActions.Command, r.Action));
        string what = command ? r.Action : AcKeymapText.Describe(r.Action);
        return $"{combo} now does {what}" + (from.Count > 0 ? " (taken from " + string.Join(", ", from) + ")." : ".");
    }

    /// <summary>
    /// The edited file written (backed up the first time) and loaded here, and its edits recorded in
    /// "&lt;keymap&gt;.rynthcore"; the others make them to their own keys within a second.
    /// </summary>
    private static void WriteAndLoad(string file, List<string> lines, string newline, IReadOnlyList<AcKeymapText.Edit> edits)
    {
        Backup(file);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, AcKeymapText.Join(lines, newline), Encoding.ASCII);
        MoveOver(tmp, file);
        try { Record(file, edits); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RynthLog.Compat($"AcKeymapLive: couldn't note the change for the other clients ({ex.Message}); they get it at their next start.");
        }
        Reload(file, "a key changed here");
    }

    /// <summary>The change added to "&lt;keymap&gt;.rynthcore" (the newest <see cref="KeepChanges"/> kept), written whole.</summary>
    private static void Record(string file, IReadOnlyList<AcKeymapText.Edit> edits)
    {
        string marker = MarkerPath(file);
        List<AcKeymapText.Change> log = new();
        try
        {
            // An older RynthCore's marker (or none): the log starts here.
            if (File.Exists(marker)) log = AcKeymapText.ParseChanges(File.ReadAllText(marker), out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        long stamp = AcKeymapText.NextStamp(log, DateTime.UtcNow.Ticks);
        log = AcKeymapText.Append(log, new AcKeymapText.Change(stamp, Environment.ProcessId, new List<AcKeymapText.Edit>(edits)), KeepChanges);
        string tmp = marker + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(tmp, AcKeymapText.FormatChanges(log), Encoding.ASCII);
        try { MoveOver(tmp, marker); }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        // Made here: never again by this client (nor by its next engine after a hot reload).
        MarkSeen(stamp);
        _markerFile = marker;
        _markerUtc = File.GetLastWriteTimeUtc(marker);
    }

    /// <summary>Replaces <paramref name="dest"/> with <paramref name="tmp"/>; a few tries, as another client may be reading it that moment.</summary>
    private static void MoveOver(string tmp, string dest)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(15);
            }
        }
    }

    /// <summary>
    /// Removes the action's key: by combo ("Ctrl+1") or by the text GetActionKeys showed ("Mouse4").
    /// <paramref name="edit"/>: the binding removed, for the other clients.
    /// </summary>
    private static bool UnbindAc(List<string> lines, string action, string keyText, out AcKeymapText.Edit edit)
    {
        edit = default;
        if (HotkeyKeys.TryParse(keyText, out KeyCombo combo, out _) && AcKeymapText.DikForVk(combo.Vk) is { } dik
            && AcKeymapText.TryMeta(lines, combo.Mods, out uint meta, out _)
            && AcKeymapText.Unbind(lines, action, 0, dik, meta))
        {
            edit = new AcKeymapText.Edit(AcKeymapText.EditKind.Unbind, action, 0, AcKeymapText.Canonical(dik), AcKeymapText.ModsOf(lines, meta));
            return true;
        }
        foreach (AcKeymapText.Line l in AcKeymapText.Bindings(lines))
        {
            if (!l.Action.Equals(action, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AcKeymap.Describe(new AcKeymap.Binding(l.Device, l.Dik, l.Meta)).Equals(keyText, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AcKeymapText.Unbind(lines, action, l.Device, l.Dik, l.Meta)) return false;
            edit = new AcKeymapText.Edit(AcKeymapText.EditKind.Unbind, action, l.Device, l.Dik, AcKeymapText.ModsOf(lines, l.Meta));
            return true;
        }
        return false;
    }

    private static void Backup(string file)
    {
        try
        {
            string b = file + ".before-rynthcore";
            if (!File.Exists(b)) File.Copy(file, b);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RynthLog.Compat($"AcKeymapLive: couldn't back up {file}: {ex.Message}");
        }
    }

    private static int RemoveHotkeys(Predicate<HotkeyBinding> match)
    {
        var list = new List<HotkeyBinding>(HotkeyService.Current.Bindings);
        int n = list.RemoveAll(match);
        if (n > 0) HotkeyService.SetAll(list);
        return n;
    }

    private static bool SameCommand(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool KeyMatches(KeyCombo combo, string keyText) =>
        HotkeyKeys.TryParse(keyText, out KeyCombo k, out _) && k == combo;

    // ── Keys of an engine command ───────────────────────────────────────────

    /// <summary>The engine hotkeys running <paramref name="command"/>, "F9, Ctrl+F9" (switched-off ones left out).</summary>
    public static string CommandKeysText(string command)
    {
        var sb = new StringBuilder();
        foreach (HotkeyBinding b in HotkeyService.Current.Bindings)
        {
            if (!b.Enabled || !b.IsCommand || !SameCommand(b.Command, command)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(HotkeyKeys.Format(b.Combo));
        }
        return sb.ToString();
    }
}
