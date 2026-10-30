// ============================================================================
//  RynthCore cimgui build - Dear ImGui user config (IMGUI_USER_CONFIG).
//  Replaces upstream cimgui's cimconfig.h (which only did #undef NDEBUG).
// ============================================================================
#pragma once

// ImGui.NET 1.91.6.1 declares ImTextureID as IntPtr (pointer-sized). Dear ImGui
// 1.91.4+ defaults it to ImU64, which on x86 makes ImDrawCmd, ImDrawList and
// ImFontAtlas wider than ImGui.NET's structs and shifts every by-value texture
// argument (SetTexID, Image, AddImage, ...). This is why the stock NuGet x86
// cimgui.dll does not match its own wrapper. A named typedef (not a bare
// "void*" macro) keeps functional casts like ImTextureID() valid.
typedef void* RynthImTextureID;
#define ImTextureID RynthImTextureID

// Soft asserts. A failed IM_ASSERT is recorded in a small native ring buffer
// (rynth_cimgui_ext.cpp) that the engine polls and logs; control flow then
// continues exactly as in a release build with asserts compiled out. The
// handler never calls into managed code (a managed exception inside a native
// frame fail-fasts the process).
#ifdef __cplusplus
extern "C"
#endif
void RynthImGui_ReportAssert(const char* expr, const char* file, int line);

#define IM_ASSERT(_EXPR) do { if (!(_EXPR)) RynthImGui_ReportAssert(#_EXPR, __FILE__, __LINE__); } while (0)
