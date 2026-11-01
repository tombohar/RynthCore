// ============================================================================
//  RynthCore.Engine - UI/Data/LuaLiveReload.cs
//  The Lua panel's live reload toggle (2026-10-31: the owner asked for buttons,
//  not only commands): RynthLua's /lua livereload on|off, shown when its panel
//  payload says LiveReload (a RynthLua without live reload sends nothing, and
//  the toggle isn't drawn). What it says and the /lua arguments a click sends
//  (LuaCommands.Chat). Pure, so tools\ChatCommandReplyTests compiles it in.
// ============================================================================

namespace RynthCore.Engine.UI.Data;

internal static class LuaLiveReload
{
    /// <summary>The toggle's caption, or null when this RynthLua has no live reload (the toggle isn't drawn).</summary>
    public static string? Label(bool? on) => on switch
    {
        true => "Live reload: on",
        false => "Live reload: off",
        _ => null,
    };

    /// <summary>The /lua arguments a click sends: the other value.</summary>
    public static string ClickArgs(bool on) => on ? "livereload off" : "livereload on";

    public static string Tip(bool on) =>
        (on ? "On: a running script restarts when you save its file (or one it requires). Click to turn it off."
            : "Off: saving a script's file doesn't restart it. Click to turn live reload on.")
        + "\nAlso: /lua livereload on|off";
}
