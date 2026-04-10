namespace RynthCore.Engine;

/// <summary>
/// Centralised logging — every log call in the Engine routes through here.
/// Toggle categories on/off at runtime to suppress entire subsystems.
/// Messages keep their existing prefix text (e.g. "D3D9VTable: scanning…").
/// </summary>
internal static class RynthLog
{
    // ── Category toggles (flip to false to silence a subsystem) ──────────

    internal static bool D3D9Enabled    = false;
    internal static bool CompatEnabled  = true;
    internal static bool RenderEnabled  = false;   // ImGui, DX9Backend, Win32Backend
    internal static bool PluginEnabled  = true;
    internal static bool UIEnabled      = false;

    // ── Category methods ─────────────────────────────────────────────────

    /// <summary>D3D9 subsystem: vtable, EndScene, bootstrapper, matrix capture, nav3D.</summary>
    internal static void D3D9(string msg)
    {
        if (D3D9Enabled) Write(msg);
    }

    /// <summary>Compatibility hooks: SmartBox, client objects, combat, movement, vitals, chat, etc.</summary>
    internal static void Compat(string msg)
    {
        if (CompatEnabled) Write(msg);
    }

    /// <summary>ImGui rendering: context, DX9 backend, Win32 input, shell.</summary>
    internal static void Render(string msg)
    {
        if (RenderEnabled) Write(msg);
    }

    /// <summary>Plugin system: loader, manager, lifecycle callbacks.</summary>
    internal static void Plugin(string msg)
    {
        if (PluginEnabled) Write(msg);
    }

    /// <summary>UI / Avalonia overlay subsystem.</summary>
    internal static void UI(string msg)
    {
        if (UIEnabled) Write(msg);
    }

    /// <summary>Verbose-only log (any category). Only written when VerboseLogging is on.</summary>
    internal static void Verbose(string msg)
    {
        if (EntryPoint.VerboseLogging) Write(msg);
    }

    /// <summary>Always-on log for critical / uncategorised messages.</summary>
    internal static void Info(string msg) => Write(msg);

    // ── Sink ─────────────────────────────────────────────────────────────

    private static void Write(string message) => EntryPoint.Log(message);
}
