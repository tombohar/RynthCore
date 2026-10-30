// ============================================================================
//  RynthCore.DecalBridge - BridgeFilter.cs
//
//  A Decal network filter written only against Decal's PUBLIC plugin API
//  (Decal.Adapter: FilterBase, CoreManager, WorldFilter, CharacterFilter, HooksWrapper).
//  It lets the RynthCore engine run in the same acclient.exe as Decal without the two
//  patching the same AC functions ("bridge mode", docs/DECAL_BRIDGE_PLAN.md):
//
//    events   -> engine: chat lines, chat-bar commands (and eats the engine's own
//                commands), login/logoff; optionally object create/release/change and
//                raw server messages. The engine says which kinds it wants (want mask).
//    commands <- engine: run on AC's main thread through Decal's public API - chat lines
//                (InvokeChatParser) and the salvage panel (SalvagePanelAdd/Salvage).
//
//  A network filter (not a plugin) so it starts with Decal, before login.
//
//  Idle unless RynthCore is in the same client: registering it (the launcher does, for
//  "Decal + RynthCore" accounts) makes every Decal client of that Windows user load it,
//  but until the RynthCore loader shows up in the process it only counts frames. It never
//  blocks, never throws into Decal, and holds nothing of the engine's.
//  RYNTHCORE_DECAL_BRIDGE=0 in a client's environment keeps it off entirely.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace RynthCore.DecalBridge
{
    [FriendlyName("RynthCore Decal Bridge")]
    public sealed unsafe class BridgeFilter : FilterBase
    {
        private const string LogDir = @"C:\Games\RynthCore\Logs";
        private const string LoaderModule = "RynthCore.Loader.dll";
        private const int ArmCheckEveryFrames = 120;   // ~2 s at 60 fps

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        private BridgeChannel _ch;
        private int _pid;
        private bool _disabled;
        private bool _worldWired;
        private bool _invoking;         // true while a command of ours runs InvokeChatParser
        private int _frames;
        private string[] _prefixes = new string[0];
        private int _prefixStamp = -1;
        private long _serverMessages;
        private int _loggedServer;
        private int _loggedChatBar;
        private bool _devicePublished;

        protected override void Startup()
        {
            _pid = Process.GetCurrentProcess().Id;
            if (Environment.GetEnvironmentVariable("RYNTHCORE_DECAL_BRIDGE") == "0")
            {
                _disabled = true;
                return;
            }
            try
            {
                Core.RenderFrame += OnRenderFrame;
                TryArm();
            }
            catch (Exception ex)
            {
                Log("startup failed: " + ex);
            }
        }

        protected override void Shutdown()
        {
            if (_disabled)
                return;
            try
            {
                Core.RenderFrame -= OnRenderFrame;
                if (_ch != null)
                {
                    ServerDispatch -= OnServerDispatch;
                    Core.ChatBoxMessage -= OnChatBoxMessage;
                    Core.CommandLineText -= OnCommandLineText;
                    Core.ItemSelected -= OnItemSelected;
                    Core.FilterInitComplete -= OnFilterInitComplete;
                    Core.PluginInitComplete -= OnFilterInitComplete;
                }
                if (_worldWired)
                {
                    Core.WorldFilter.CreateObject -= OnCreateObject;
                    Core.WorldFilter.ReleaseObject -= OnReleaseObject;
                    Core.WorldFilter.ChangeObject -= OnChangeObject;
                    Core.CharacterFilter.LoginComplete -= OnLoginComplete;
                    Core.CharacterFilter.Logoff -= OnLogoff;
                }
            }
            catch { }
            if (_ch != null)
            {
                Log("shutdown after " + _serverMessages + " server messages, " +
                    _ch.ReadLong(BridgeLayout.OffRecords) + " records, " + _ch.ReadLong(BridgeLayout.OffDropped) + " dropped, " +
                    _ch.ReadLong(BridgeLayout.OffCmdDone) + " commands run");
                _ch.WriteInt(BridgeLayout.OffBridgeState, 0);
                _ch.Dispose();
                _ch = null;
            }
        }

        // Arms once the RynthCore loader is in this client (injected with Decal, or later).
        private void TryArm()
        {
            if (_ch != null || GetModuleHandleW(LoaderModule) == IntPtr.Zero)
                return;
            BridgeChannel ch = BridgeChannel.OpenOrCreate(_pid);
            if (!ch.Valid)
            {
                Log("mapping has another protocol version (magic 0x" + ch.ReadInt(BridgeLayout.OffMagic).ToString("X8") +
                    ", version " + ch.ReadInt(BridgeLayout.OffVersion) + ", ours " + BridgeLayout.Version +
                    ") - the engine and the bridge are from different builds; staying idle.");
                ch.Dispose();
                _disabled = true;
                return;
            }
            _ch = ch;
            _ch.WriteInt(BridgeLayout.OffBridgePid, _pid);
            _ch.WriteString(BridgeRecordKind.Hello, BridgeLayout.Version, 0,
                "RynthCore Decal Bridge " + typeof(BridgeFilter).Assembly.GetName().Version + " on " +
                Environment.Version + " (" + (IntPtr.Size * 8) + "-bit)");
            ServerDispatch += OnServerDispatch;
            Core.ChatBoxMessage += OnChatBoxMessage;
            Core.CommandLineText += OnCommandLineText;
            Core.ItemSelected += OnItemSelected;
            // WorldFilter / CharacterFilter exist once Decal's filters are up.
            Core.FilterInitComplete += OnFilterInitComplete;
            Core.PluginInitComplete += OnFilterInitComplete;
            TryWireWorld();
            _ch.WriteInt(BridgeLayout.OffBridgeState, 1);   // the engine waits for this
            Log("armed (RynthCore is in this client); mapping " + BridgeLayout.MappingName(_pid) + ", world wired=" + _worldWired);
        }

        private void OnFilterInitComplete(object sender, EventArgs e)
        {
            TryWireWorld();
        }

        private void TryWireWorld()
        {
            if (_worldWired) return;
            try
            {
                WorldFilter wf = Core.WorldFilter;
                CharacterFilter cf = Core.CharacterFilter;
                if (wf == null || cf == null) return;
                wf.CreateObject += OnCreateObject;
                wf.ReleaseObject += OnReleaseObject;
                wf.ChangeObject += OnChangeObject;
                cf.LoginComplete += OnLoginComplete;
                cf.Logoff += OnLogoff;
                _worldWired = true;
                Log("WorldFilter and CharacterFilter wired (Decal's world filters are up).");
            }
            catch (Exception ex)
            {
                Log("wiring WorldFilter failed: " + ex.Message);
            }
        }

        // ── once per frame, AC's main thread ─────────────────────────────────
        private void OnRenderFrame(object sender, EventArgs e)
        {
            try
            {
                if (_disabled)
                    return;
                if (_ch == null)
                {
                    if (++_frames % ArmCheckEveryFrames == 0)
                        TryArm();
                    return;
                }
                if (!_devicePublished)
                    PublishDevice();
                if (_ch.HasCommands)
                    _ch.DrainCommands(RunCommand, 64);
                ServeReplay();
            }
            catch (Exception ex)
            {
                Log("frame handler failed: " + ex.Message);
            }
        }

        // The device Decal renders with, through Decal's public IDecalCore.GetD3DDevice: an
        // address for the engine to read (its vtable says whether Decal wraps AC's device).
        // No reference is kept and nothing is called on it here.
        private void PublishDevice()
        {
            _devicePublished = true;
            try
            {
                Guid iid = new Guid("D0223B96-BF7A-43FD-92BD-A43B0D82B9EB");   // IID_IDirect3DDevice9
                object dev = Core.Decal.Underlying.GetD3DDevice(ref iid);
                if (dev == null) { Log("GetD3DDevice returned null"); return; }
                IntPtr unk = Marshal.GetIUnknownForObject(dev);
                IntPtr d3d;
                int hr = Marshal.QueryInterface(unk, ref iid, out d3d);
                if (hr >= 0 && d3d != IntPtr.Zero)
                {
                    _ch.WriteInt(BridgeLayout.OffD3DDevice, d3d.ToInt32());
                    Marshal.Release(d3d);
                }
                Marshal.Release(unk);
                Marshal.ReleaseComObject(dev);
                _ch.WriteInt(BridgeLayout.OffDecalHwnd, Core.Decal.Hwnd.ToInt32());
                Log("Decal's D3D device 0x" + _ch.ReadInt(BridgeLayout.OffD3DDevice).ToString("X8") + " (QI hr=0x" + hr.ToString("X8") +
                    "), hwnd 0x" + Core.Decal.Hwnd.ToInt32().ToString("X"));
            }
            catch (Exception ex)
            {
                Log("GetD3DDevice failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // An engine generation that takes objects from the bridge starts with none (a hot
        // reload, or an engine injected late): it bumps the replay request.
        private void ServeReplay()
        {
            int want = _ch.ReadInt(BridgeLayout.OffReplayRequest);
            if (want == 0 || want == _ch.ReadInt(BridgeLayout.OffReplayDone) || !_worldWired)
                return;
            int n = 0;
            if (_ch.Wants(BridgeRecordKind.CreateObject))
            {
                foreach (WorldObject o in Core.WorldFilter.GetAll())
                {
                    _ch.Write(BridgeRecordKind.CreateObject, unchecked((uint)o.Id), 1, null, 0);
                    n++;
                }
            }
            _ch.WriteInt(BridgeLayout.OffReplayDone, want);
            Log("replayed " + n + " world objects for engine request " + want);
        }

        private void RunCommand(BridgeRecordKind kind, uint arg0, uint arg1, byte* payload, int len)
        {
            string failure = null;
            try
            {
                switch (kind)
                {
                    case BridgeRecordKind.CmdInvokeChatParser:
                        string line = len >= 2 ? new string((char*)payload, 0, len / 2) : string.Empty;
                        _invoking = true;
                        try { Core.Actions.InvokeChatParser(line); }
                        finally { _invoking = false; }
                        break;
                    case BridgeRecordKind.CmdSalvagePanelAdd:
                        Core.Actions.SalvagePanelAdd(unchecked((int)arg0));
                        break;
                    case BridgeRecordKind.CmdSalvagePanelSalvage:
                        Core.Actions.SalvagePanelSalvage();
                        break;
                    default:
                        failure = "unknown command " + (int)kind;
                        break;
                }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
            }
            if (failure != null)
            {
                Log("command " + kind + " failed: " + failure);
                _ch.WriteString(BridgeRecordKind.CommandResult, (uint)kind, 0, failure);
            }
        }

        // ── forwarders (AC's main thread; never block, never throw) ──────────
        private void OnServerDispatch(object sender, NetworkMessageEventArgs e)
        {
            try
            {
                _serverMessages++;
                if (!_ch.Wants(BridgeRecordKind.ServerMessage))
                    return;   // the engine reads these from its own hooks: don't copy them
                Message m = e.Message;
                byte[] raw = m.RawData;
                if (_loggedServer < 4)
                {
                    _loggedServer++;
                    Log("server message type=0x" + m.Type.ToString("X4") + " len=" + (raw == null ? 0 : raw.Length));
                }
                _ch.WriteBytes(BridgeRecordKind.ServerMessage, unchecked((uint)m.Type), 0, raw);
            }
            catch { }
        }

        private void OnChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try { _ch.WriteString(BridgeRecordKind.ChatText, unchecked((uint)e.Color), unchecked((uint)e.Target), e.Text); }
            catch { }
        }

        private void OnCommandLineText(object sender, ChatParserInterceptEventArgs e)
        {
            try
            {
                // A line the engine sent through InvokeChatParser: it already went through the
                // engine's own dispatch; let Decal and AC have it.
                if (_invoking)
                    return;
                string text = e.Text ?? string.Empty;
                // The engine's commands (and its plugins') must not reach the server: eat the
                // lines whose prefix the engine published; forward every line either way.
                bool eat = MatchesPrefix(text);
                if (eat) e.Eat = true;
                if (_loggedChatBar < 10)
                {
                    _loggedChatBar++;
                    Log("chat bar: '" + text + "' eaten=" + eat);
                }
                _ch.WriteString(BridgeRecordKind.ChatBarEnter, eat ? 1u : 0u, 0, text);
            }
            catch { }
        }

        private void OnItemSelected(object sender, ItemSelectedEventArgs e)
        {
            try { _ch.Write(BridgeRecordKind.ItemSelected, unchecked((uint)e.ItemGuid), 0, null, 0); } catch { }
        }

        private void OnCreateObject(object sender, CreateObjectEventArgs e)
        {
            try { _ch.Write(BridgeRecordKind.CreateObject, unchecked((uint)e.New.Id), 0, null, 0); } catch { }
        }

        private void OnReleaseObject(object sender, ReleaseObjectEventArgs e)
        {
            try { _ch.Write(BridgeRecordKind.ReleaseObject, unchecked((uint)e.Released.Id), 0, null, 0); } catch { }
        }

        private void OnChangeObject(object sender, ChangeObjectEventArgs e)
        {
            try { _ch.Write(BridgeRecordKind.ChangeObject, unchecked((uint)e.Changed.Id), (uint)e.Change, null, 0); } catch { }
        }

        private void OnLoginComplete(object sender, EventArgs e)
        {
            try { _ch.Write(BridgeRecordKind.LoginComplete, unchecked((uint)Core.CharacterFilter.Id), 0, null, 0); } catch { }
        }

        private void OnLogoff(object sender, LogoffEventArgs e)
        {
            try { _ch.Write(BridgeRecordKind.Logoff, (uint)e.Type, 0, null, 0); } catch { }
        }

        // ── helpers ────────────────────────────────────────────────────────────
        private bool MatchesPrefix(string text)
        {
            if (_ch.ReadInt(BridgeLayout.OffEngineAttached) == 0)
                return false;   // no engine to run it: let AC have the line
            // The engine rewrites the prefix list when it attaches; re-read it when it changes.
            int gen = _ch.ReadInt(BridgeLayout.OffEngineGeneration);
            if (gen != _prefixStamp)
            {
                _prefixStamp = gen;
                char* p = (char*)(_ch.Header + BridgeLayout.OffPrefixes);
                int n = 0;
                while (n < BridgeLayout.PrefixChars && p[n] != '\0') n++;
                _prefixes = new string(p, 0, n).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }
            foreach (string pre in _prefixes)
            {
                if (text.StartsWith(pre, StringComparison.OrdinalIgnoreCase) &&
                    (text.Length == pre.Length || text[pre.Length] == ' '))
                    return true;
            }
            return false;
        }

        private void Log(string msg)
        {
            try
            {
                File.AppendAllText(System.IO.Path.Combine(LogDir, "DecalBridge." + _pid + ".log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
