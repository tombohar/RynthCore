using System;
using System.Text;
using NLua;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using System.IO;

namespace NexSuite.Plugins.RynthAi
{
    public class LuaManager : IDisposable
    {
        private Lua _lua;
        private readonly PluginCore _core;
        private bool _isDisposed;
        public void Execute(string script) => ExecuteString(script);

        public LuaManager(PluginCore core)
        {
            _core = core;
            Init();
        }

        public void Init()
        {
            try
            {
                // 1. Clean up old state if re-initializing to prevent memory leaks
                _lua?.Dispose();

                // 2. Initialize the fresh NLua state
                _lua = new Lua();
                _lua.LoadCLRPackage();

                // 3. Instantiate the Bridge
                // This class acts as the middle-man, housing GoTo, Stop, and UsePortal logic
                var bridge = new LuaBridge(_core);

                // --- THE API BRIDGE: Expose the Bridge as "RynthAi" ---
                // Usage in Lua: RynthAi:GoTo("33.5N", "44.8E")
                _lua["RynthAi"] = bridge;

                // --- SHORTCUTS: Direct manager access for advanced scripting ---
                if (_core.UI != null)
                {
                    _lua["Combat"] = _core.UI.CombatManager;
                    _lua["Settings"] = _core.UI.Settings;
                    _lua["Nav"] = _core.UI.Settings.CurrentRoute;
                }

                // Expose the raw Decal CoreManager (e.g., Core.WorldFilter)
                _lua["Core"] = _core.PluginCoreManager;

                // --- REDIRECT PRINT ---
                // Replaces Lua's default print() with our custom C# method 
                // which sends text to both AC Chat and the UI Console.
                _lua.RegisterFunction("print", this, typeof(LuaManager).GetMethod("LuaPrint"));

                LuaPrint("Lua Engine Initialized.");
            }
            catch (Exception ex)
            {
                LogError("Init Error: " + ex.Message);
            }
        }

        public void ExecuteString(string script)
        {
            try
            {
                if (string.IsNullOrEmpty(script) || _lua == null) return;
                _lua.DoString(script);
            }
            catch (NLua.Exceptions.LuaException ex)
            {
                // This will now show you the EXACT line number and error in AC chat
                LogError("Lua Script Error: " + ex.Message);
            }
            catch (Exception ex)
            {
                LogError("Engine Error: " + ex.Message);
            }
        }
        public void LuaPrint(object msg)
        {
            // 1. Format the basic string and timestamp
            string text = msg?.ToString() ?? "nil";
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string formattedMsg = $"[{timestamp}] {text}";

            // 2. Always output to Asheron's Call Chat (Green text)
            // This is safe even if the UI window isn't open yet
            _core.PluginHost.Actions.AddChatText($"[RynthLua] {text}", 5);

            // 3. Safely update the ImGui Console Tab
            // We wrap everything in this check so the plugin doesn't crash 
            // if you try to print something before the UI is fully loaded.
            if (_core.UI?.Settings != null)
            {
                // Append the new line to the console
                _core.UI.Settings.LuaConsoleOutput += $"\n{formattedMsg}";

                // Limit console size to roughly 10k characters to prevent memory bloat
                if (_core.UI.Settings.LuaConsoleOutput.Length > 10000)
                {
                    // Trims the oldest 5,000 characters
                    _core.UI.Settings.LuaConsoleOutput = _core.UI.Settings.LuaConsoleOutput.Substring(5000);
                }
            }
        }

        public void LoadMainScript()
        {
            try
            {
                string scriptDir = Path.Combine(_core.AssemblyDirectory, "Scripts");
                if (!Directory.Exists(scriptDir)) Directory.CreateDirectory(scriptDir);

                string path = Path.Combine(scriptDir, "main.lua");

                if (File.Exists(path))
                {
                    string code = File.ReadAllText(path);
                    ExecuteString(code);
                    LuaPrint("Main script loaded from: " + path);
                }
                else
                {
                    // Create a dummy file so the user has a template
                    File.WriteAllText(path, "-- RynthAi Main Lua Script\nfunction OnBotTick()\nend");
                    LuaPrint("Created template main.lua at: " + path);
                }
            }
            catch (Exception ex)
            {
                LogError("LoadMainScript Error: " + ex.Message);
            }
        }

        private void LogError(string msg)
        {
            _core.PluginHost.Actions.AddChatText($"[RynthLua Error] {msg}", 2);
            _core.UI.Settings.LuaConsoleOutput += $"\n[ERROR] {msg}";
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _lua?.Dispose();
                _isDisposed = true;
            }
        }
    }
}