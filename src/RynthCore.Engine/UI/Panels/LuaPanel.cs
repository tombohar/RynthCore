// ============================================================================
//  RynthCore.Engine — UI/Panels/LuaPanel.cs
//  Lua scripts for RynthAi: script list, editor, Run/Stop, console.
//
//  Data goes through UiDataHub (UI/Data/LuaData.cs): UiSources.Lua polls
//  RynthPluginGetLuaJson on the pump thread and LuaCommands sends
//  RynthPluginSendLuaCommand there; the ImGui face (ImGui/Panels/LuaFace.cs)
//  reads the same snapshot.
//
//  Controls are built once and updated in place: a full rebuild every poll
//  (the NavPanel pattern) would reset the editor's caret while typing. The
//  script list uses plain buttons, not a dropdown, because overlay popups
//  blank the docked panels.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class LuaPanel
{
    // Lazy: see the dispatcher-poison invariant in EntryPoint.InitWorker.
    private static IBrush? _amber, _green, _mute, _text, _shell, _btnFill, _btnBord, _startBg, _stopBg, _consoleBg;
    private static IBrush ColAmber   => _amber   ??= new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static IBrush ColGreen   => _green   ??= new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static IBrush ColMute    => _mute    ??= new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static IBrush ColTextDim => _text    ??= new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static IBrush ColShellBg => _shell   ??= new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static IBrush ColBtnFill => _btnFill ??= new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static IBrush ColBtnBord => _btnBord ??= new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static IBrush ColStartBg => _startBg ??= new SolidColorBrush(Color.FromRgb(0x19, 0x61, 0x19));
    private static IBrush ColStopBg  => _stopBg  ??= new SolidColorBrush(Color.FromRgb(0x80, 0x19, 0x19));
    private static IBrush ColConsole => _consoleBg ??= new SolidColorBrush(Color.FromRgb(0x05, 0x09, 0x0D));

    // =========================================================================
    //  Create
    // =========================================================================

    public static Control Create()
    {

        var root = new Border
        {
            Background      = ColShellBg,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            MinWidth        = 380,
        };
        var content = new StackPanel { Margin = new Thickness(6), Spacing = 4 };
        root.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content,
        };

        // ── Status + Run/Stop ────────────────────────────────────────────────
        var statusText = new TextBlock
        {
            Text = "Lua: idle", Foreground = ColAmber, FontSize = 11, FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var runBtn  = MakeBtn("▶ Run");
        runBtn.Background = ColStartBg;
        var stopBtn = MakeBtn("■ Stop");
        stopBtn.Background = ColStopBg;
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(statusText, 0);
        Grid.SetColumn(runBtn, 1);
        Grid.SetColumn(stopBtn, 2);
        topRow.Children.Add(statusText);
        topRow.Children.Add(runBtn);
        topRow.Children.Add(stopBtn);
        content.Children.Add(topRow);

        // ── Script list (LuaScripts folder) ──────────────────────────────────
        content.Children.Add(new TextBlock { Text = "Scripts (click to open):", Foreground = ColMute, FontSize = 10 });
        var scriptWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(scriptWrap);

        // ── Name + Save ──────────────────────────────────────────────────────
        var nameBox = new TextBox
        {
            Watermark = "Script name...",
            Background = ColBtnFill, Foreground = ColTextDim,
            BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            FontSize = 10, Height = 22, Padding = new Thickness(4, 1),
            Margin = new Thickness(0, 0, 4, 0),
        };
        var saveBtn   = MakeBtn("Save");
        var deleteBtn = MakeBtn("Delete");
        var newBtn    = MakeBtn("New");
        var saveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(saveBtn, 1);
        Grid.SetColumn(newBtn, 2);
        Grid.SetColumn(deleteBtn, 3);
        saveRow.Children.Add(nameBox);
        saveRow.Children.Add(saveBtn);
        saveRow.Children.Add(newBtn);
        saveRow.Children.Add(deleteBtn);
        content.Children.Add(saveRow);

        // ── Editor ───────────────────────────────────────────────────────────
        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab    = true,
            TextWrapping  = TextWrapping.NoWrap,
            FontFamily    = new FontFamily("Consolas, Courier New, monospace"),
            FontSize      = 11,
            Height        = 240,
            Background    = ColBtnFill, Foreground = ColTextDim,
            BorderBrush   = ColBtnBord, BorderThickness = new Thickness(1),
            Padding       = new Thickness(4),
            Watermark     = "-- Lua script\nprint('hello from ' .. me.name)",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        content.Children.Add(editor);

        // ── Console ──────────────────────────────────────────────────────────
        var clearBtn = MakeBtn("Clear");
        var consoleHdr = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var consoleLbl = new TextBlock { Text = "Console", Foreground = ColMute, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(consoleLbl, 0);
        Grid.SetColumn(clearBtn, 1);
        consoleHdr.Children.Add(consoleLbl);
        consoleHdr.Children.Add(clearBtn);
        content.Children.Add(consoleHdr);

        var consoleText = new SelectableTextBlock
        {
            Foreground   = ColGreen,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
        };
        var consoleScroll = new ScrollViewer
        {
            Height = 150,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = consoleText,
        };
        content.Children.Add(new Border
        {
            Background = ColConsole, BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            Padding = new Thickness(4), Child = consoleScroll,
        });

        // ── Actions ──────────────────────────────────────────────────────────
        runBtn.Click += (_, _) =>
            LuaCommands.Send("run", nameBox.Text?.Trim() ?? string.Empty, editor.Text ?? string.Empty);
        stopBtn.Click  += (_, _) => LuaCommands.Send("stop");
        clearBtn.Click += (_, _) => { LuaCommands.Send("clearConsole"); consoleText.Text = string.Empty; };
        saveBtn.Click  += (_, _) =>
        {
            string name = nameBox.Text?.Trim() ?? string.Empty;
            if (name.Length == 0) { consoleText.Text += "\n[panel] Type a script name first."; return; }
            LuaCommands.Send("save", name, editor.Text ?? string.Empty);
        };
        newBtn.Click += (_, _) => { editor.Text = string.Empty; nameBox.Text = string.Empty; };
        deleteBtn.Click += (_, _) =>
        {
            string name = nameBox.Text?.Trim() ?? string.Empty;
            if (name.Length > 0) LuaCommands.Send("delete", name);
        };

        // ── Poll ─────────────────────────────────────────────────────────────
        int lastConsoleSeq = -1, lastLoadSeq = -1;
        string lastScripts = "\0";
        bool firstFetch = true;
        long seenVersion = -1;

        void Refresh()
        {
            var snap = UiSources.Lua.Current;
            if (snap == null)
            {
                statusText.Text = "Lua: RynthLua not connected";
                return;
            }
            if (snap.Version == seenVersion) return;
            seenVersion = snap.Version;
            LuaPayload d = snap.Value;

            statusText.Text = d.Running
                ? $"Running: {(d.ScriptName.Length > 0 ? d.ScriptName : "(editor)")}"
                : (d.Status.Length > 0 ? d.Status : "Lua: idle");
            statusText.Foreground = d.Running ? ColGreen : ColAmber;

            string joined = string.Join("\n", d.Scripts);
            if (joined != lastScripts)
            {
                lastScripts = joined;
                scriptWrap.Children.Clear();
                if (d.Scripts.Count == 0)
                    scriptWrap.Children.Add(new TextBlock { Text = "(none yet: write one and Save)", Foreground = ColMute, FontSize = 10 });
                foreach (string s in d.Scripts)
                {
                    string name = s;
                    var b = MakeBtn(name);
                    b.Click += (_, _) => LuaCommands.Send("load", name);
                    scriptWrap.Children.Add(b);
                }
            }

            // Take a loaded script into the editor; on first open, restore the
            // plugin's current editor text without counting it as a new load.
            if (d.LoadSeq != lastLoadSeq)
            {
                bool take = !firstFetch || string.IsNullOrEmpty(editor.Text);
                lastLoadSeq = d.LoadSeq;
                if (take)
                {
                    editor.Text = d.LoadedText;
                    nameBox.Text = d.LoadedName;
                }
            }

            if (d.ConsoleSeq != lastConsoleSeq)
            {
                lastConsoleSeq = d.ConsoleSeq;
                consoleText.Text = d.Console;
                consoleScroll.ScrollToEnd();
            }
            firstFetch = false;
        }

        // Reads the hub's snapshot (fetched every 500 ms while subscribed, and
        // right after each command).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Refresh();
        // Stop with the visual tree (RadarPanel idiom); restart on attach since
        // drag/resize fires Detached→Attached.
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Lua.Subscribe();
            UiSources.Lua.RequestRefresh();
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Lua.Unsubscribe();
        };

        Refresh();
        return root;
    }

    private static Button MakeBtn(string text) => new Button
    {
        Content         = text,
        Background      = ColBtnFill,
        Foreground      = ColTextDim,
        BorderBrush     = ColBtnBord,
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(3),
        Padding         = new Thickness(8, 3),
        FontSize        = 10,
        Height          = 24,
        Margin          = new Thickness(0, 0, 4, 4),
    };
}
