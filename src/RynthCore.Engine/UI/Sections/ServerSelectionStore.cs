// ============================================================================
//  RynthCore.Engine - UI/Sections/ServerSelectionStore.cs
//  Applies a server pick (ServerSelection.Plan) to the saved per-character
//  overrides (ServerForceStore) and rebuilds the verdicts, so the menus, the
//  "servermenu" push and plugin API v86 (GetServerStateJson) follow at once.
//  Used by the Diagnostics page's server picker and "/rc server pick".
//
//  Threads: any (ServerForceStore locks; the rebuild runs on the next frame).
// ============================================================================

using System.Collections.Generic;

namespace RynthCore.Engine.UI.Sections;

internal static class ServerSelectionStore
{
    /// <summary>
    /// Picks <paramref name="pick"/> (a server id, or <see cref="ServerSelection.Auto"/>).
    /// <paramref name="message"/> says what happened, for chat. False when the pick names no
    /// known server or an override couldn't be saved.
    /// </summary>
    public static bool Apply(string? pick, out string message)
    {
        IReadOnlyList<ServerState> servers = SectionRegistry.Current.Servers;
        List<(string Id, ServerForce Force)>? plan = ServerSelection.Plan(servers, pick);
        if (plan == null)
        {
            message = $"No server called '{pick}'. /rc server lists them.";
            return false;
        }

        string where = string.Empty;
        foreach ((string id, ServerForce force) in plan)
        {
            if (ServerForceStore.Get(id) == force) continue;
            if (!ServerForceStore.Set(id, force, out where))
            {
                message = $"Server pick not saved: {where}.";
                SectionRegistry.Invalidate();
                return false;
            }
        }
        SectionRegistry.Invalidate();

        bool auto = string.IsNullOrWhiteSpace(pick) || pick.Trim().Equals(ServerSelection.Auto, System.StringComparison.OrdinalIgnoreCase);
        string saved = where.Length > 0 ? $" ({where})" : string.Empty;
        message = auto
            ? "Server: detected again (every override back to auto)" + saved + "."
            : $"Server: '{pick!.Trim()}' picked for this character; every other server off" + saved + ".";
        RynthLog.Milestone("ServerSelection: " + message);
        return true;
    }
}
