// ============================================================================
//  RynthCore.Headless - Host/RetailErrorText.cs
//  Some server refusals carry only an error code (a WeenieError, or an InventoryServerSaveFailed
//  with one); the retail client prints its own text for them in the chat window. Plugins and metas
//  watch that chat (e.g. a meta leaving a hive whose quest item is on its timer), so the host prints
//  the same line. Only codes the retail client shows a line for are listed.
// ============================================================================

namespace RynthCore.Headless.Host;

internal static class RetailErrorText
{
    private static readonly Dictionary<uint, string> Texts = new()
    {
        [0x001D] = "You're too busy!",
        [0x043E] = "You have solved this quest too recently!",
        [0x043F] = "You have solved this quest too many times!",
    };

    /// <summary>The chat line the retail client prints for this error code, or null when it prints none.</summary>
    public static string? For(uint code) => Texts.TryGetValue(code, out string? t) ? t : null;
}
