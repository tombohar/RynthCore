// ============================================================================
//  RynthCore cimgui build - extra exports compiled into cimgui.dll.
//
//  RynthImGui_BuildTag      identifies this build (the engine refuses others).
//  RynthImGui_GetLayout     sizes/offsets/enum counts the engine compares
//                           against ImGui.NET's structs before creating a
//                           context (see ImGuiSelfTest.cs; same index order).
//  RynthImGui_ReportAssert  IM_ASSERT target (rynth_imconfig.h); records into
//                           a ring buffer the engine polls from AC's thread.
// ============================================================================

#include "imgui.h"
#include <stddef.h>
#include <stdio.h>
#include <string.h>

#define RYNTH_EXPORT extern "C" __declspec(dllexport)

// ---- Build tag -------------------------------------------------------------
// Bump the trailing number whenever the layout table or config changes.
RYNTH_EXPORT const char* RynthImGui_BuildTag(void)
{
    return "rynth-cimgui 1.91.6dock ImTextureID=void* soft-assert layout-1 textedit-264bee4";
}

// ---- Layout table ------------------------------------------------------------
// Index order is a contract with ImGuiSelfTest.ExpectedLayout(); append only.
RYNTH_EXPORT int RynthImGui_GetLayout(int* out, int capacity)
{
    const int values[] =
    {
        1,                                              //  0 table version
        (int)sizeof(ImTextureID),                       //  1
        (int)sizeof(ImWchar),                           //  2
        (int)sizeof(ImDrawIdx),                         //  3
        (int)sizeof(ImDrawVert),                        //  4
        (int)sizeof(ImDrawCmd),                         //  5
        (int)offsetof(ImDrawCmd, TextureId),            //  6
        (int)offsetof(ImDrawCmd, VtxOffset),            //  7
        (int)offsetof(ImDrawCmd, IdxOffset),            //  8
        (int)offsetof(ImDrawCmd, ElemCount),            //  9
        (int)offsetof(ImDrawCmd, UserCallback),         // 10
        (int)sizeof(ImDrawList),                        // 11
        (int)offsetof(ImDrawList, VtxBuffer),           // 12
        (int)sizeof(ImDrawData),                        // 13
        (int)offsetof(ImDrawData, CmdLists),            // 14
        (int)offsetof(ImDrawData, DisplayPos),          // 15
        (int)offsetof(ImDrawData, DisplaySize),         // 16
        (int)sizeof(ImFontAtlas),                       // 17
        (int)offsetof(ImFontAtlas, TexID),              // 18
        (int)sizeof(ImFont),                            // 19
        (int)sizeof(ImFontGlyph),                       // 20
        (int)sizeof(ImFontConfig),                      // 21
        (int)sizeof(ImGuiIO),                           // 22
        (int)offsetof(ImGuiIO, Fonts),                  // 23
        (int)offsetof(ImGuiIO, WantCaptureMouse),       // 24
        (int)offsetof(ImGuiIO, WantTextInput),          // 25
        (int)sizeof(ImGuiStyle),                        // 26
        (int)sizeof(ImGuiViewport),                     // 27
        (int)sizeof(ImGuiPlatformIO),                   // 28
        (int)sizeof(ImGuiInputTextCallbackData),        // 29
        (int)sizeof(ImGuiListClipper),                  // 30
        (int)sizeof(ImGuiPayload),                      // 31
        (int)sizeof(ImGuiTableSortSpecs),               // 32
        (int)sizeof(ImGuiTableColumnSortSpecs),         // 33
        (int)sizeof(ImGuiStorage),                      // 34
        (int)sizeof(ImGuiWindowClass),                  // 35
        (int)sizeof(ImGuiSizeCallbackData),             // 36
        (int)ImGuiCol_COUNT,                            // 37
        (int)ImGuiKey_NamedKey_END,                     // 38
        (int)ImGuiStyleVar_COUNT,                       // 39
        (int)ImGuiMouseCursor_COUNT,                    // 40
    };
    const int count = (int)(sizeof(values) / sizeof(values[0]));
    if (out != NULL)
        for (int i = 0; i < count && i < capacity; i++)
            out[i] = values[i];
    return count;
}

// ---- Soft asserts ------------------------------------------------------------
// ImGui runs on one thread (AC's render thread); the engine polls on the same
// thread, so plain statics are enough. The ring keeps the last 16 failures.
static const int kAssertSlots = 16;
static const int kAssertTextLen = 256;
static int g_assertCount;
static char g_assertText[kAssertSlots][kAssertTextLen];

extern "C" void RynthImGui_ReportAssert(const char* expr, const char* file, int line)
{
    const char* base = file;
    for (const char* p = file; *p; p++)
        if (*p == '\\' || *p == '/')
            base = p + 1;
    char* slot = g_assertText[g_assertCount % kAssertSlots];
    _snprintf_s(slot, kAssertTextLen, _TRUNCATE, "%s:%d: %s", base, line, expr);
    g_assertCount++;
}

// Total failures since load (monotonic; wraps only after 2^31).
RYNTH_EXPORT int RynthImGui_GetAssertCount(void)
{
    return g_assertCount;
}

// Copies failure number `index` (0-based, < count) into buf. Returns the
// length copied, or -1 when that entry was already overwritten or not yet made.
RYNTH_EXPORT int RynthImGui_GetAssertText(int index, char* buf, int capacity)
{
    if (buf == NULL || capacity <= 0 || index < 0 || index >= g_assertCount || index < g_assertCount - kAssertSlots)
        return -1;
    const char* src = g_assertText[index % kAssertSlots];
    int len = (int)strnlen(src, kAssertTextLen);
    if (len >= capacity) len = capacity - 1;
    memcpy(buf, src, (size_t)len);
    buf[len] = '\0';
    return len;
}
