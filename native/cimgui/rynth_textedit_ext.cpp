// ============================================================================
//  RynthCore cimgui build - C exports for ImGuiColorTextEdit (santaclose fork,
//  fetched into _textedit and patched by textedit-rynth.patch).
//
//  The engine P/Invokes these by hand (ImGui/TextEditorNative.cs). Every call
//  is made on AC's render thread inside an ImGui frame (Create reads the style,
//  so it needs a current context too). Strings are UTF-8; returned pointers
//  stay valid until the next call of the same export.
//
//  Languages: 1 = Meta (.af source: coloured by the tokenizer below from the
//  vocabulary the engine passes in), 2 = Lua (the fork's own definition).
// ============================================================================

#include <algorithm>
#include <string>
#include <unordered_set>
#include "TextEditor.h"

#define RYNTH_EXPORT extern "C" __declspec(dllexport)

struct RynthTextEditAccess
{
    typedef TextEditor::PaletteIndex PI;

    static std::unordered_set<std::string>& StructWords() { static std::unordered_set<std::string> s; return s; }
    static std::unordered_set<std::string>& ConditionWords() { static std::unordered_set<std::string> s; return s; }
    static std::unordered_set<std::string>& ActionWords() { static std::unordered_set<std::string> s; return s; }

    static bool IsWordStart(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }
    static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
    static bool IsWordChar(char c) { return IsWordStart(c) || IsDigit(c); }

    // Mirrors MetaSourceEditor.MetaColorizer: "~~" comments to the end of the
    // line, braces, numbers (hex and signed too), STATE:/IF:/DO:/NAV: (the
    // colon included), condition keywords, action keywords.
    static bool TokenizeMeta(const char* in_begin, const char* in_end, const char*& out_begin, const char*& out_end, PI& color)
    {
        while (in_begin < in_end && (*in_begin == ' ' || *in_begin == '\t'))
            in_begin++;
        if (in_begin == in_end)
        {
            out_begin = out_end = in_end;
            color = PI::Default;
            return true;
        }

        const char* p = in_begin;
        out_begin = p;
        char c = *p;

        if (c == '~' && p + 1 < in_end && p[1] == '~')
        {
            out_end = in_end;
            color = PI::Comment;
            return true;
        }
        if (c == '{' || c == '}')
        {
            out_end = p + 1;
            color = PI::Punctuation;
            return true;
        }
        if (IsDigit(c) || (c == '-' && p + 1 < in_end && IsDigit(p[1])))
        {
            p++;
            while (p < in_end && (IsWordChar(*p) || *p == '.'))
                p++;
            out_end = p;
            color = PI::Number;
            return true;
        }
        if (IsWordStart(c))
        {
            p++;
            while (p < in_end && IsWordChar(*p))
                p++;
            std::string word(in_begin, p);
            std::transform(word.begin(), word.end(), word.begin(), [](char ch) { return (char)::toupper((unsigned char)ch); });
            color = PI::Default;
            if (p < in_end && *p == ':' && StructWords().count(word) != 0)
            {
                p++;
                color = PI::Keyword;
            }
            else if (ConditionWords().count(word) != 0)
                color = PI::KnownIdentifier;
            else if (ActionWords().count(word) != 0)
                color = PI::PreprocIdentifier;
            out_end = p;
            return true;
        }

        // Anything else (punctuation, UTF-8 bytes): one byte, default colour.
        out_end = p + 1;
        color = PI::Default;
        return true;
    }

    static const TextEditor::LanguageDefinition& MetaLanguage()
    {
        static TextEditor::LanguageDefinition def;
        static bool built = false;
        if (!built)
        {
            built = true;
            def.mName = "Meta";
            def.mTokenize = &TokenizeMeta;
            def.mSingleLineComment = "~~";    // Ctrl+/ toggles "~~"
            // An empty block-comment start matches everywhere in ColorizeInternal,
            // so give it one that never occurs in text.
            def.mCommentStart = "\x01\x02";
            def.mCommentEnd = "\x02\x01";
            def.mPreprocChar = '\x01';
            def.mCaseSensitive = false;
            def.mTokenizerOwnsComments = true;   // TokenizeMeta colours "~~" itself
        }
        return def;
    }

    static void SetLanguage(TextEditor* ed, int language)
    {
        if (language == 1)
        {
            ed->SetLanguageDefinition(TextEditor::LanguageDefinitionId::None);
            ed->mLanguageDefinition = &MetaLanguage();
            ed->Colorize();
        }
        else if (language == 2)
            ed->SetLanguageDefinition(TextEditor::LanguageDefinitionId::Lua);
        else
            ed->SetLanguageDefinition(TextEditor::LanguageDefinitionId::None);
    }

    static void SetPalette(TextEditor* ed, const ImU32* colors, int count)
    {
        int n = std::min(count, (int)PI::Max);
        for (int i = 0; i < n; i++)
            ed->mPalette[i] = colors[i];
    }

    static void CursorScreenPos(TextEditor* ed, float* x, float* y)
    {
        TextEditor::Coordinates at = ed->GetSanitizedCursorCoordinates();
        *x = ed->mTextOrigin.x + ed->mTextStart + ed->TextDistanceToLineStart(at);
        *y = ed->mTextOrigin.y + (at.mLine + 1) * ed->mCharAdvance.y;
    }

    static std::string LinePrefix(TextEditor* ed)
    {
        TextEditor::Coordinates at = ed->GetSanitizedCursorCoordinates();
        return ed->GetText(TextEditor::Coordinates(at.mLine, 0), at);
    }

    // Completion: replace the `bytes` characters before the cursor with `text`,
    // as one undo step.
    static void ReplaceBeforeCursor(TextEditor* ed, int bytes, const char* text)
    {
        if (ed->mReadOnly)
            return;
        ed->ClearExtraCursors();
        ed->ClearSelections();
        TextEditor::Coordinates end = ed->GetSanitizedCursorCoordinates();
        int endIndex = ed->GetCharacterIndexR(end);
        int startIndex = std::max(0, endIndex - std::max(0, bytes));
        TextEditor::Coordinates start(end.mLine, ed->GetCharacterColumn(end.mLine, startIndex));

        TextEditor::UndoRecord u;
        u.mBefore = ed->mState;
        if (start < end)
        {
            ed->SetSelection(start, end);
            u.mOperations.push_back({ ed->GetSelectedText(), start, end, TextEditor::UndoOperationType::Delete });
            ed->DeleteSelection();
        }
        TextEditor::Coordinates insertAt = ed->GetSanitizedCursorCoordinates();
        ed->InsertTextAtCursor(text);
        u.mOperations.push_back({ std::string(text), insertAt, ed->GetSanitizedCursorCoordinates(), TextEditor::UndoOperationType::Add });
        u.mAfter = ed->mState;
        ed->AddUndo(u);
    }
};

static void FillWordSet(std::unordered_set<std::string>& set, const char* words)
{
    set.clear();
    if (words == nullptr)
        return;
    std::string current;
    for (const char* p = words;; p++)
    {
        if (*p == '\n' || *p == '\0')
        {
            if (!current.empty())
                set.insert(current);
            current.clear();
            if (*p == '\0')
                break;
        }
        else
            current.push_back((char)::toupper((unsigned char)*p));
    }
}

RYNTH_EXPORT TextEditor* RynthTE_Create(void)
{
    TextEditor* ed = new TextEditor();
    ed->SetShowWhitespacesEnabled(false);
    return ed;
}

RYNTH_EXPORT void RynthTE_Destroy(TextEditor* ed)
{
    delete ed;
}

/// Newline-separated words, matched case-insensitively. Applies to every Meta editor.
RYNTH_EXPORT void RynthTE_SetMetaVocabulary(const char* structWords, const char* conditionWords, const char* actionWords)
{
    FillWordSet(RynthTextEditAccess::StructWords(), structWords);
    FillWordSet(RynthTextEditAccess::ConditionWords(), conditionWords);
    FillWordSet(RynthTextEditAccess::ActionWords(), actionWords);
}

RYNTH_EXPORT void RynthTE_SetLanguage(TextEditor* ed, int language)
{
    RynthTextEditAccess::SetLanguage(ed, language);
}

/// `count` ImU32 colours in TextEditor::PaletteIndex order.
RYNTH_EXPORT void RynthTE_SetPalette(TextEditor* ed, const ImU32* colors, int count)
{
    RynthTextEditAccess::SetPalette(ed, colors, count);
}

RYNTH_EXPORT void RynthTE_SetOptions(TextEditor* ed, int readOnly, int showWhitespaces, int showLineNumbers, int tabSize)
{
    ed->SetReadOnlyEnabled(readOnly != 0);
    ed->SetShowWhitespacesEnabled(showWhitespaces != 0);
    ed->SetShowLineNumbersEnabled(showLineNumbers != 0);
    ed->SetTabSize(tabSize);
}

RYNTH_EXPORT void RynthTE_SetText(TextEditor* ed, const char* utf8)
{
    ed->SetText(utf8 != nullptr ? std::string(utf8) : std::string());
}

RYNTH_EXPORT const char* RynthTE_GetText(TextEditor* ed, int* length)
{
    static std::string text;
    text = ed->GetText();
    if (length != nullptr)
        *length = (int)text.size();
    return text.c_str();
}

RYNTH_EXPORT unsigned RynthTE_GetChangeCount(TextEditor* ed)
{
    return ed->GetChangeCount();
}

/// Draws the editor as a child window; returns whether it has keyboard focus.
RYNTH_EXPORT int RynthTE_Render(TextEditor* ed, const char* id, float width, float height, int border)
{
    return ed->Render(id, false, ImVec2(width, height), border != 0) ? 1 : 0;
}

RYNTH_EXPORT void RynthTE_GetCursor(TextEditor* ed, int* line, int* column)
{
    ed->GetCursorPosition(*line, *column);
}

RYNTH_EXPORT void RynthTE_SetCursor(TextEditor* ed, int line, int charIndex)
{
    ed->SetCursorPosition(line, charIndex);
}

/// Screen position of the bottom-left corner of the cursor's cell (valid after Render).
RYNTH_EXPORT void RynthTE_GetCursorScreenPos(TextEditor* ed, float* x, float* y)
{
    RynthTextEditAccess::CursorScreenPos(ed, x, y);
}

/// The cursor's line up to the cursor.
RYNTH_EXPORT const char* RynthTE_GetLinePrefix(TextEditor* ed, int* length)
{
    static std::string prefix;
    prefix = RynthTextEditAccess::LinePrefix(ed);
    if (length != nullptr)
        *length = (int)prefix.size();
    return prefix.c_str();
}

RYNTH_EXPORT void RynthTE_ReplaceBeforeCursor(TextEditor* ed, int bytes, const char* utf8)
{
    RynthTextEditAccess::ReplaceBeforeCursor(ed, bytes, utf8 != nullptr ? utf8 : "");
}

/// While set, the editor ignores Up/Down/PageUp/PageDown/Enter/Tab/Escape (the host's completion list uses them).
RYNTH_EXPORT void RynthTE_SetCompletionKeysHeld(TextEditor* ed, int held)
{
    ed->SetCompletionKeysHeld(held != 0);
}

RYNTH_EXPORT void RynthTE_RequestFocus(TextEditor* ed)
{
    ed->RequestFocus();
}
