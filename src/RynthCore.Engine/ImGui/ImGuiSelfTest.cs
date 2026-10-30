// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiSelfTest.cs
//  Refuses to start ImGui unless the loaded cimgui.dll is RynthCore's own
//  build (native/cimgui, scripts/Build-Cimgui.ps1) and its struct layout
//  matches ImGui.NET 1.91.6.1.
//
//  Every managed ImGui struct accessor (ImDrawData, ImGuiIO, ImFontAtlas, ...)
//  reads native memory at ImGui.NET's compiled offsets, and every enum value
//  is passed straight through. A different native build silently shifts both:
//  the DLL bundled until 2026-09 was 1.89.7, and even the stock NuGet 1.91.6
//  x86 DLL disagrees with its own wrapper (64-bit ImTextureID). This check
//  turns a mismatch into a logged refusal instead of memory corruption on
//  AC's thread.
// ============================================================================

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ImGuiSelfTest
{
    /// <summary>IMGUI_VERSION of the native build ImGui.NET 1.91.6.1 wraps.</summary>
    internal const string ExpectedVersion = "1.91.6";

    /// <summary>Must equal RynthImGui_BuildTag() in native/cimgui/rynth_cimgui_ext.cpp.</summary>
    /// <summary>Outcome of the last Run, for /rc imgui diag ("not run" before the first).</summary>
    public static string LastResult { get; private set; } = "not run";

    internal const string ExpectedBuildTag = "rynth-cimgui 1.91.6dock ImTextureID=void* soft-assert layout-1 textedit-264bee4";

    /// <summary>
    /// True when <paramref name="cimguiModule"/> is our cimgui build and its
    /// layout matches the managed wrapper. Safe to call before any context
    /// exists. On a mismatch it logs why and returns false; the caller must
    /// not create an ImGui context.
    /// </summary>
    public static bool Run(IntPtr cimguiModule)
    {
        try
        {
            // igGetVersion exists in every cimgui and has no side effects, so
            // it goes first: older builds compile IM_ASSERT in as a hard abort,
            // and the layout calls below would trip it on a mismatch.
            string version = ImGuiNET.ImGui.GetVersion() ?? "";
            if (version != ExpectedVersion)
                return Fail($"cimgui.dll reports ImGui {version}, ImGui.NET expects {ExpectedVersion}");

            if (cimguiModule == IntPtr.Zero
                || !NativeLibrary.TryGetExport(cimguiModule, "RynthImGui_BuildTag", out IntPtr buildTagFn)
                || !NativeLibrary.TryGetExport(cimguiModule, "RynthImGui_GetLayout", out IntPtr getLayoutFn))
                return Fail("cimgui.dll is not the RynthCore build (no RynthImGui_* exports); rebuild with scripts/Build-Cimgui.ps1");

            string tag = Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<IntPtr>)buildTagFn)()) ?? "";
            if (tag != ExpectedBuildTag)
                return Fail($"cimgui build tag '{tag}', expected '{ExpectedBuildTag}'");

            (string Name, int Value)[] expected = ExpectedLayout();
            var getLayout = (delegate* unmanaged[Cdecl]<int*, int, int>)getLayoutFn;
            int nativeCount = getLayout(null, 0);
            if (nativeCount < expected.Length)
                return Fail($"layout table has {nativeCount} entries, expected {expected.Length}");

            int* native = stackalloc int[nativeCount];
            getLayout(native, nativeCount);
            int mismatches = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                if (native[i] == expected[i].Value) continue;
                mismatches++;
                RynthLog.UI($"ImGuiSelfTest: layout[{i}] {expected[i].Name}: native={native[i]} managed={expected[i].Value}");
            }
            if (mismatches > 0)
                return Fail($"{mismatches} layout mismatch(es) between cimgui.dll and ImGui.NET");

            // Dear ImGui's own check (asserts are soft in our build).
            if (!ImGuiNET.ImGui.DebugCheckVersionAndDataLayout(ExpectedVersion,
                    (uint)sizeof(ImGuiIO), (uint)sizeof(ImGuiStyle), (uint)sizeof(Vector2),
                    (uint)sizeof(Vector4), (uint)sizeof(ImDrawVert), sizeof(ushort)))
                return Fail("DebugCheckVersionAndDataLayout reported a mismatch");

            ImGuiAssertLog.Bind(cimguiModule);
            LastResult = $"OK - {tag}";
            RynthLog.UI($"ImGuiSelfTest: OK - {tag}, {expected.Length} layout checks passed.");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Fail(string why)
    {
        LastResult = "FAILED - " + why;
        RynthLog.UI($"ImGuiSelfTest: FAILED - {why}. ImGui disabled.");
        return false;
    }

    private static int Off(void* structBase, void* field) => (int)((byte*)field - (byte*)structBase);

    /// <summary>
    /// The managed side of the layout table, in the index order of
    /// RynthImGui_GetLayout (native/cimgui/rynth_cimgui_ext.cpp). Append only.
    /// </summary>
    internal static (string Name, int Value)[] ExpectedLayout()
    {
        // Offsets are taken from zeroed stack locals (&((T*)null)->Field
        // null-checks in .NET).
        ImDrawCmd cmd = default;
        ImDrawList list = default;
        ImDrawData data = default;
        ImFontAtlas atlas = default;
        ImGuiIO io = default;
        return new (string, int)[]
        {
            ("table version", 1),
            ("sizeof(ImTextureID)", sizeof(IntPtr)),
            ("sizeof(ImWchar)", sizeof(ushort)),
            ("sizeof(ImDrawIdx)", sizeof(ushort)),
            ("sizeof(ImDrawVert)", sizeof(ImDrawVert)),
            ("sizeof(ImDrawCmd)", sizeof(ImDrawCmd)),
            ("ImDrawCmd.TextureId", Off(&cmd, &cmd.TextureId)),
            ("ImDrawCmd.VtxOffset", Off(&cmd, &cmd.VtxOffset)),
            ("ImDrawCmd.IdxOffset", Off(&cmd, &cmd.IdxOffset)),
            ("ImDrawCmd.ElemCount", Off(&cmd, &cmd.ElemCount)),
            ("ImDrawCmd.UserCallback", Off(&cmd, &cmd.UserCallback)),
            ("sizeof(ImDrawList)", sizeof(ImDrawList)),
            ("ImDrawList.VtxBuffer", Off(&list, &list.VtxBuffer)),
            ("sizeof(ImDrawData)", sizeof(ImDrawData)),
            ("ImDrawData.CmdLists", Off(&data, &data.CmdLists)),
            ("ImDrawData.DisplayPos", Off(&data, &data.DisplayPos)),
            ("ImDrawData.DisplaySize", Off(&data, &data.DisplaySize)),
            ("sizeof(ImFontAtlas)", sizeof(ImFontAtlas)),
            ("ImFontAtlas.TexID", Off(&atlas, &atlas.TexID)),
            ("sizeof(ImFont)", sizeof(ImFont)),
            // Pinned to the native size: ImGui.NET's generator expands the
            // Colored:1/Visible:1/Codepoint:30 bitfield into three uints, so
            // its ImFontGlyph is 48 bytes and wrong under ANY native build.
            // Never read glyphs through ImGui.NET's ImFontGlyph/ImFontGlyphPtr.
            ("sizeof(ImFontGlyph) [native]", 40),
            ("sizeof(ImFontConfig)", sizeof(ImFontConfig)),
            ("sizeof(ImGuiIO)", sizeof(ImGuiIO)),
            ("ImGuiIO.Fonts", Off(&io, &io.Fonts)),
            ("ImGuiIO.WantCaptureMouse", Off(&io, &io.WantCaptureMouse)),
            ("ImGuiIO.WantTextInput", Off(&io, &io.WantTextInput)),
            ("sizeof(ImGuiStyle)", sizeof(ImGuiStyle)),
            ("sizeof(ImGuiViewport)", sizeof(ImGuiViewport)),
            ("sizeof(ImGuiPlatformIO)", sizeof(ImGuiPlatformIO)),
            ("sizeof(ImGuiInputTextCallbackData)", sizeof(ImGuiInputTextCallbackData)),
            ("sizeof(ImGuiListClipper)", sizeof(ImGuiListClipper)),
            ("sizeof(ImGuiPayload)", sizeof(ImGuiPayload)),
            ("sizeof(ImGuiTableSortSpecs)", sizeof(ImGuiTableSortSpecs)),
            ("sizeof(ImGuiTableColumnSortSpecs)", sizeof(ImGuiTableColumnSortSpecs)),
            ("sizeof(ImGuiStorage)", sizeof(ImGuiStorage)),
            ("sizeof(ImGuiWindowClass)", sizeof(ImGuiWindowClass)),
            ("sizeof(ImGuiSizeCallbackData)", sizeof(ImGuiSizeCallbackData)),
            ("ImGuiCol.COUNT", (int)ImGuiCol.COUNT),
            ("ImGuiKey.NamedKey_END", (int)ImGuiKey.NamedKey_END),
            ("ImGuiStyleVar.COUNT", (int)ImGuiStyleVar.COUNT),
            ("ImGuiMouseCursor.COUNT", (int)ImGuiMouseCursor.COUNT),
        };
    }
}
