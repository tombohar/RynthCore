// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/ViewportOffsets.cs
//  Byte offsets of ImGuiPlatformIO callback fields in the cimgui.dll shipped
//  with ImGui.NET 1.91.6.1 (docking branch).
//
//  WARNING: these do NOT match ImGui.NET's compiled struct layout. ImGui.NET
//  prepends 36 bytes of clipboard/IME/OpenInShell fields that this cimgui.dll
//  build does not have. Writing via ImGui.NET's ref properties lands 36 bytes
//  past the native field, so the native library sees NULL and asserts
//  "Platform init didn't install handlers".
//
//  Source of truth (disassembled cimgui.dll 1.91.6.1, x86):
//    ImGuiPlatformIO_Set_Platform_GetWindowPos → mov [pio+0x10], shim
//    ImGuiPlatformIO_Set_Platform_GetWindowSize → mov [pio+0x18], shim
//    igUpdatePlatformWindows first callback dispatch → call [g+0x37B8]
//    with ImGuiContext PlatformIO offset → pio field 0 = Platform_CreateWindow
//
//  Struct size (from constructor memset): 0x74 (116 bytes). Fields 0x00..0x5F
//  hold 24 callback pointers; 0x60..0x73 hold Monitors + Viewports ImVectors.
// ═══════════════════════════════════════════════════════════════════════════

namespace RynthCore.Engine.ImGuiBackend;

internal static class ViewportOffsets
{
    // Platform_* (18 callbacks — this cimgui.dll build lacks GetWindowWorkAreaInsets)
    public const int PlatformCreateWindow         = 0;
    public const int PlatformDestroyWindow        = 4;
    public const int PlatformShowWindow           = 8;
    public const int PlatformSetWindowPos         = 12;
    public const int PlatformGetWindowPos         = 16;   // confirmed via _Set_ helper disasm
    public const int PlatformSetWindowSize        = 20;
    public const int PlatformGetWindowSize        = 24;   // confirmed via _Set_ helper disasm
    public const int PlatformSetWindowFocus       = 28;
    public const int PlatformGetWindowFocus       = 32;
    public const int PlatformGetWindowMinimized   = 36;
    public const int PlatformSetWindowTitle       = 40;
    public const int PlatformSetWindowAlpha       = 44;
    public const int PlatformUpdateWindow         = 48;
    public const int PlatformRenderWindow         = 52;
    public const int PlatformSwapBuffers          = 56;
    public const int PlatformGetWindowDpiScale    = 60;
    public const int PlatformOnChangedViewport    = 64;
    public const int PlatformCreateVkSurface      = 68;

    // Renderer_* (5 callbacks) — RendererCreateWindow at +72 confirmed by
    // igUpdatePlatformWindows disasm: [ebx+0x3800] = [pio + 0x48] = +72.
    public const int RendererCreateWindow         = 72;
    public const int RendererDestroyWindow        = 76;
    public const int RendererSetWindowSize        = 80;
    public const int RendererRenderWindow         = 84;
    public const int RendererSwapBuffers          = 88;
}
