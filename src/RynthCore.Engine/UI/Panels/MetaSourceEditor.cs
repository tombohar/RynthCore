// =============================================================================
//  RynthCore.Engine — UI/Panels/MetaSourceEditor.cs
//  AvaloniaEdit-based source editor for the Meta panel's Source view.
//  Provides syntax highlighting (programmatic, NativeAOT-friendly — no XSHD)
//  and context-aware Ctrl+Space completion driven by the live MetaPanel
//  payload (states / nav files / embedded nav keys).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RynthCore.Engine.UI.Panels;

internal static class MetaSourceEditor
{
    // ── .af vocabulary (mirrors MetaSchema in RynthSuite plugin) ──────────────
    // Top-level structural keywords.
    private static readonly HashSet<string> StructKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "STATE", "IF", "DO", "NAV",
    };

    // Condition keywords as they appear in .af source (keyword form, not labels).
    internal static readonly string[] ConditionKeywords =
    {
        "Never", "Always", "All", "Any", "ChatMatch", "MainSlotsLE",
        "SecsInStateGE", "Death", "VendorOpen", "VendorClosed",
        "ItemCountLE", "ItemCountGE",
        "MobsInDist_Name", "MobsInDist_Priority",
        "NeedToBuff", "NoMobsInDist", "BlockE", "CellE",
        "IntoPortal", "ExitPortal", "Not",
        "PSecsInStateGE", "SecsOnSpellGE", "SecsOnSpellLE",
        "BuPercentGE", "DistToRteGE", "Expr",
        "ChatCapture", "NavEmpty",
        "MainHealthLE", "MainHealthPHE", "MainManaLE", "MainManaPHE",
        "MainStamLE", "VitaePHE",
    };

    internal static readonly string[] ActionKeywords =
    {
        "None", "Chat", "SetState", "EmbedNav", "DoAll",
        "CallState", "Return", "DoExpr", "ChatExpr",
        "SetWatchdog", "ClearWatchdog", "GetOpt", "SetOpt",
        "CreateView", "DestroyView", "DestroyAllViews",
    };

    private static readonly HashSet<string> CondSet = new(ConditionKeywords, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ActSet  = new(ActionKeywords, StringComparer.OrdinalIgnoreCase);

    // ── Colorblind-aware palette ─────────────────────────────────────────────
    // Differentiation by both hue AND weight/style so it reads without colour.
    private static readonly IBrush BrStruct  = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6)); // teal, bold
    private static readonly IBrush BrCond    = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33)); // amber, bold
    private static readonly IBrush BrAction  = new SolidColorBrush(Color.FromRgb(0x7B, 0xB8, 0xFF)); // light blue
    private static readonly IBrush BrComment = new SolidColorBrush(Color.FromRgb(0x6A, 0x7A, 0x8A)); // grey, italic
    private static readonly IBrush BrBrace   = new SolidColorBrush(Color.FromRgb(0xCC, 0x88, 0xCC)); // pink/violet
    private static readonly IBrush BrNumber  = new SolidColorBrush(Color.FromRgb(0x99, 0xE6, 0x99)); // green
    private static readonly IBrush BrText    = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2)); // default
    private static readonly IBrush BrBg      = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush BrBorder  = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));

    // ── Public factory ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a TextEditor wired to the meta source view. <paramref name="getText"/>
    /// is read once at construction; <paramref name="onTextChanged"/> fires on every
    /// edit. <paramref name="getStates"/> / <paramref name="getNavs"/> are queried
    /// lazily when the completion popup opens so suggestions track live payload.
    /// </summary>
    public static TextEditor Create(
        Func<string> getText,
        Action<string> onTextChanged,
        Func<IReadOnlyList<string>> getStates,
        Func<IReadOnlyList<string>> getNavs)
    {
        var editor = new TextEditor
        {
            ShowLineNumbers       = true,
            Background            = BrBg,
            Foreground            = BrText,
            BorderBrush           = BrBorder,
            BorderThickness       = new Thickness(1),
            FontFamily            = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize              = 12,
            MinHeight             = 320,
            Padding               = new Thickness(4),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Options =
            {
                ConvertTabsToSpaces            = false,
                IndentationSize                = 4,
                HighlightCurrentLine           = true,
                ShowSpaces                     = false,
                ShowTabs                       = false,
                EnableHyperlinks               = false,
                EnableEmailHyperlinks          = false,
                AllowScrollBelowDocument       = true,
            },
        };

        // Initial text.
        editor.Document = new TextDocument(getText() ?? string.Empty);

        // Syntax highlighter.
        editor.TextArea.TextView.LineTransformers.Add(new MetaColorizer());

        // Text-changed fan-out — keep ps.SourceText in sync.
        editor.Document.TextChanged += (_, _) => onTextChanged(editor.Document.Text);

        // Completion (Ctrl+Space, also after ':' or '.').
        CompletionWindow? completionWindow = null;
        void OpenCompletion(bool autoTriggered)
        {
            if (completionWindow != null) return;

            var items = BuildSuggestionsForContext(editor, getStates, getNavs);
            if (items.Count == 0) return;

            completionWindow = new CompletionWindow(editor.TextArea);
            foreach (var s in items)
                completionWindow.CompletionList.CompletionData.Add(new MetaCompletionItem(s.Text, s.Kind));

            // For auto-trigger don't force-select; user might just want to type freely.
            completionWindow.CompletionList.IsFiltering = true;
            if (!autoTriggered) completionWindow.CompletionList.SelectedItem ??= completionWindow.CompletionList.CompletionData.FirstOrDefault();

            completionWindow.Closed += (_, _) => completionWindow = null;
            completionWindow.Show();
        }

        editor.TextArea.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                e.Handled = true;
                OpenCompletion(autoTriggered: false);
            }
        };

        // Auto-trigger after typing ':' followed by a space-or-letter, so
        // "IF:<space>" pops the conditions list without Ctrl+Space.
        editor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text == " ")
            {
                var caret = editor.TextArea.Caret.Offset;
                if (caret >= 2 && editor.Document.GetCharAt(caret - 2) == ':')
                    OpenCompletion(autoTriggered: true);
            }
        };

        return editor;
    }

    // ── Context-aware suggestion builder ─────────────────────────────────────

    private enum SuggestionKind { Struct, Cond, Action, State, Nav, Generic }

    private readonly record struct Suggestion(string Text, SuggestionKind Kind);

    private static List<Suggestion> BuildSuggestionsForContext(
        TextEditor editor,
        Func<IReadOnlyList<string>> getStates,
        Func<IReadOnlyList<string>> getNavs)
    {
        var caret = editor.TextArea.Caret.Offset;
        var doc = editor.Document;

        // Read the current line up to the caret.
        var line = doc.GetLineByOffset(caret);
        string prefix = doc.GetText(line.Offset, Math.Max(0, caret - line.Offset));
        string trimmed = prefix.TrimStart();
        string trimmedNoTrailing = trimmed.TrimEnd();

        var states = SafeList(getStates);
        var navs   = SafeList(getNavs);

        // After IF: → conditions
        if (StartsWithKeyword(trimmedNoTrailing, "IF:", out var rest))
        {
            // If the user already typed a known cond keyword followed by a space,
            // start suggesting state/nav names where meaningful (SetState/CallState
            // appear as action data, not condition; keep conditions list here).
            return ConditionKeywords.Select(s => new Suggestion(s, SuggestionKind.Cond)).ToList();
        }

        // After DO: → actions
        if (StartsWithKeyword(trimmedNoTrailing, "DO:", out rest))
        {
            // If user typed "DO: SetState " or "DO: CallState " → states
            if (StartsWithKeyword(rest.TrimStart(), "SetState", out _) ||
                StartsWithKeyword(rest.TrimStart(), "CallState", out _))
                return states.Select(s => new Suggestion(s, SuggestionKind.State)).ToList();

            if (StartsWithKeyword(rest.TrimStart(), "EmbedNav", out _))
                return navs.Select(s => new Suggestion(s, SuggestionKind.Nav)).ToList();

            return ActionKeywords.Select(s => new Suggestion(s, SuggestionKind.Action)).ToList();
        }

        // After STATE: → existing state names + free-form (user types new)
        if (StartsWithKeyword(trimmedNoTrailing, "STATE:", out _))
            return states.Select(s => new Suggestion(s, SuggestionKind.State)).ToList();

        // After NAV: → nav names
        if (StartsWithKeyword(trimmedNoTrailing, "NAV:", out _))
            return navs.Select(s => new Suggestion(s, SuggestionKind.Nav)).ToList();

        // Beginning of line → structural keywords
        return new List<Suggestion>
        {
            new("STATE:", SuggestionKind.Struct),
            new("IF:",    SuggestionKind.Struct),
            new("DO:",    SuggestionKind.Struct),
            new("NAV:",   SuggestionKind.Struct),
        };
    }

    private static bool StartsWithKeyword(string text, string keyword, out string rest)
    {
        if (text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            rest = text.Substring(keyword.Length);
            return true;
        }
        rest = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> SafeList(Func<IReadOnlyList<string>> fn)
    {
        try { return fn() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    // ── Colorizer ─────────────────────────────────────────────────────────────

    private sealed class MetaColorizer : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var doc = CurrentContext.Document;
            int start = line.Offset;
            int end   = line.EndOffset;
            if (end <= start) return;

            string text = doc.GetText(start, end - start);

            // ── Comments: ~~ … to EOL (handles both "~~ }" and "~~ @disabled") ──
            int commentIdx = text.IndexOf("~~", StringComparison.Ordinal);
            int contentEnd = commentIdx >= 0 ? commentIdx : text.Length;
            if (commentIdx >= 0)
            {
                ChangeLinePart(start + commentIdx, end, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(BrComment);
                    el.TextRunProperties.SetTypeface(new Typeface(
                        el.TextRunProperties.Typeface.FontFamily,
                        FontStyle.Italic));
                });
            }

            // ── Token sweep over the non-comment portion ──────────────────────
            int i = 0;
            while (i < contentEnd)
            {
                char c = text[i];

                // Whitespace
                if (c == ' ' || c == '\t') { i++; continue; }

                // Braces { }
                if (c == '{' || c == '}')
                {
                    ChangeLinePart(start + i, start + i + 1, el =>
                        el.TextRunProperties.SetForegroundBrush(BrBrace));
                    i++;
                    continue;
                }

                // Numbers (decimal, hex like A9B40000, signed)
                if (char.IsDigit(c) || (c == '-' && i + 1 < contentEnd && char.IsDigit(text[i + 1])))
                {
                    int j = i + 1;
                    while (j < contentEnd && (char.IsLetterOrDigit(text[j]) || text[j] == '.' || text[j] == '_'))
                        j++;
                    ChangeLinePart(start + i, start + j, el =>
                        el.TextRunProperties.SetForegroundBrush(BrNumber));
                    i = j;
                    continue;
                }

                // Identifier / keyword
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i + 1;
                    while (j < contentEnd && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                        j++;
                    string tok = text.Substring(i, j - i);

                    // STATE / IF / DO / NAV followed by ':' = struct keyword
                    bool isStruct = StructKeywords.Contains(tok)
                                    && j < contentEnd && text[j] == ':';

                    IBrush? brush = null;
                    bool bold = false;
                    if (isStruct)        { brush = BrStruct;  bold = true; }
                    else if (CondSet.Contains(tok)) { brush = BrCond;   bold = true; }
                    else if (ActSet.Contains(tok))  { brush = BrAction; }

                    if (brush != null)
                    {
                        int tokStart = start + i;
                        int tokEnd   = start + j;
                        bool boldCopy = bold;
                        IBrush brushCopy = brush;
                        ChangeLinePart(tokStart, tokEnd, el =>
                        {
                            el.TextRunProperties.SetForegroundBrush(brushCopy);
                            if (boldCopy)
                                el.TextRunProperties.SetTypeface(new Typeface(
                                    el.TextRunProperties.Typeface.FontFamily,
                                    FontStyle.Normal, FontWeight.Bold));
                        });
                        if (isStruct && j < contentEnd && text[j] == ':')
                        {
                            // colour the trailing ':' too
                            ChangeLinePart(start + j, start + j + 1, el =>
                                el.TextRunProperties.SetForegroundBrush(BrStruct));
                            j++;
                        }
                    }
                    i = j;
                    continue;
                }

                i++;
            }
        }
    }

    // ── Completion item ───────────────────────────────────────────────────────

    private sealed class MetaCompletionItem : ICompletionData
    {
        private readonly string _text;
        private readonly SuggestionKind _kind;
        public MetaCompletionItem(string text, SuggestionKind kind)
        {
            _text = text;
            _kind = kind;
        }
        public Avalonia.Media.IImage? Image => null;
        public string Text => _text;
        public object Content => _text;
        public object Description => _kind switch
        {
            SuggestionKind.Struct => "structural keyword",
            SuggestionKind.Cond   => "condition",
            SuggestionKind.Action => "action",
            SuggestionKind.State  => "meta state",
            SuggestionKind.Nav    => "nav route",
            _ => string.Empty,
        };
        public double Priority => _kind switch
        {
            SuggestionKind.Struct => 100,
            SuggestionKind.Cond   => 90,
            SuggestionKind.Action => 90,
            SuggestionKind.State  => 80,
            SuggestionKind.Nav    => 80,
            _ => 0,
        };
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, _text);
    }
}
