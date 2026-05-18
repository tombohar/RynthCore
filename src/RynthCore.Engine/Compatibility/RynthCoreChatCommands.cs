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
/// The single use case today is recovering the RynthCore overlay bar when a
/// user has dragged it off the visible client area: its only in-overlay
/// "Rs" reset button is on the bar itself, so it is unreachable once the bar
/// is. <c>/rc resetbar</c> (and the launcher button that writes it to
/// dispatch.txt) is the out-of-band recovery.
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

            default:
                // Consume anything else under the /rc namespace so a typo
                // does not leak into world chat.
                RynthLog.Compat($"RynthCoreChatCommands: unknown subcommand '/rc {sub}'.");
                return true;
        }
    }
}
