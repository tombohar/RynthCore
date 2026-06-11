using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Engine-level <c>/rc ...</c> chat commands. Intercepted on every chat-entry
/// path BEFORE plugin pre-dispatch and before the line reaches AC's chat:
/// <list type="bullet">
///   <item>typed in-game — <see cref="ChatCallbackHooks"/> OutgoingChatDetour</item>
///   <item>dispatch.txt / OnLoginCommandRunner / Host.InvokeChatParser —
///         <see cref="ChatCommandDispatcher.Dispatch"/></item>
/// </list>
/// Commands:
/// <list type="bullet">
///   <item><c>/rc resetbar</c> — recover the RynthCore overlay bar when it has
///         been dragged off the visible client area (its in-overlay "Rs" reset
///         button rides the bar itself, so it's unreachable once the bar is).
///         The launcher button writes this to dispatch.txt as the out-of-band
///         recovery.</item>
///   <item><c>/rc vitals</c> (alias <c>/rc hud</c>) — toggle the custom D3D9
///         Health/Stamina/Mana HUD on/off (persisted to engine.json).</item>
/// </list>
/// </summary>
internal static class RynthCoreChatCommands
{
    private const string Prefix = "/rc ";

    /// <summary>
    /// True if <paramref name="line"/> was a RynthCore engine command and has
    /// been handled — the caller must consume it (do NOT forward to AC chat
    /// or to plugins).
    /// </summary>
    public static bool TryHandle(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string sub = trimmed.Substring(Prefix.Length).Trim();

        switch (sub.ToLowerInvariant())
        {
            case "resetbar":
            case "barreset":
                RynthLog.Compat("RynthCoreChatCommands: /rc resetbar — resetting overlay bar position.");
                ImGuiBackend.RynthCoreShell.RequestExternalReset();
                return true;

            case "vitals":
            case "hud":
            {
                // Toggle the custom D3D9 vital HUD. Setter persists to engine.json
                // so it survives relaunch. (No AC-chat echo — the AddTextToScroll
                // path has a known AV risk; the HUD appearing/vanishing confirms.)
                bool now = !Plugins.EngineSettings.DrawCustomVitalBars;
                Plugins.EngineSettings.DrawCustomVitalBars = now;
                RynthLog.Compat($"RynthCoreChatCommands: /rc vitals — custom vital HUD {(now ? "ON" : "OFF")}.");
                return true;
            }

            default:
                // Consume anything else under the /rc namespace so a typo
                // does not leak into world chat.
                RynthLog.Compat($"RynthCoreChatCommands: unknown subcommand '/rc {sub}'.");
                return true;
        }
    }
}
