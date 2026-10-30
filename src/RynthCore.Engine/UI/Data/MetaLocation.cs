// ============================================================================
//  RynthCore.Engine - UI/Data/MetaLocation.cs
//  The Meta editor's "Set current" text for Landblock == / Landcell ==
//  conditions (2026-10-05). Self-contained: RynthSuite's RynthAiHostTests
//  link this file and check that RynthAi's MetaManager matches the text it
//  writes against the same position.
//
//  How RynthAi reads them (MetaManager.EvaluateCondition):
//    Landblock ==  hex; a value above FFFF is shifted down 16 bits (metaf and
//                  VTank write the full 8 digits, "A9B40000"), then compared
//                  with cellId >> 16.
//    Landcell ==   hex, compared with the whole cell id ("A9B4001C").
//  So "Set current" writes 8 upper-case hex digits, the form the field hints
//  show: the landblock with its low word zeroed, the landcell as it is.
// ============================================================================

using System.Globalization;

namespace RynthCore.Engine.UI.Data;

internal static class MetaLocation
{
    /// <summary>Condition indexes (MetaVocabulary.ConditionNames) that take a position.</summary>
    public const int LandblockCondition = 16, LandcellCondition = 17;

    /// <summary>True for Landblock == and Landcell ==.</summary>
    public static bool TakesPosition(int condition) => condition is LandblockCondition or LandcellCondition;

    /// <summary>"A9B40000": the landblock of <paramref name="cellId"/>.</summary>
    public static string LandblockText(uint cellId) => (cellId & 0xFFFF0000u).ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>"A9B4001C": the whole cell id.</summary>
    public static string LandcellText(uint cellId) => cellId.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The text for <paramref name="condition"/> at <paramref name="cellId"/>; null for other conditions.</summary>
    public static string? Text(int condition, uint cellId) => condition switch
    {
        LandblockCondition => LandblockText(cellId),
        LandcellCondition => LandcellText(cellId),
        _ => null,
    };
}
