// ============================================================================
//  RynthCore.Engine - ImGui/IconSampling.cs
//  Item icons are 32x32 pictures drawn bigger; retail draws them point-sampled (crisp, hard-edged
//  pixels, so its black outline stays a black line) and ImGui's default is a linear filter that blurs
//  them. A face wraps its icon draws in Begin / End: two draw-list "callbacks" that DX9Backend answers by
//  switching sampler 0's filter for the commands after them (no managed code runs in the render path).
//  Every list starts back on linear (SetupRenderState), so a missed End cannot leak past its list.
// ============================================================================

using System;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static class IconSampling
{
    public const int PointSentinel = -101, LinearSentinel = -102;

    /// <summary>The draws after this are point-sampled.</summary>
    public static void Begin(ImDrawListPtr dl) => dl.AddCallback(new IntPtr(PointSentinel), IntPtr.Zero);

    /// <summary>The draws after this are linear again.</summary>
    public static void End(ImDrawListPtr dl) => dl.AddCallback(new IntPtr(LinearSentinel), IntPtr.Zero);
}
