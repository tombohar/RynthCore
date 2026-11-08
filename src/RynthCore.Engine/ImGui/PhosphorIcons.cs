// ============================================================================
//  RynthCore.Engine - ImGui/PhosphorIcons.cs
//  The Phosphor icons (MIT, Assets/Fonts/Phosphor.ttf; licence in
//  THIRD-PARTY-NOTICES.md) as the characters that draw them. ImGuiFonts merges
//  the glyphs into every baked font at the web font's code points (the private
//  use area), so an icon is just text: put one in a label, or hand it to
//  DrawCentered for an icon-only button. The names are Phosphor's.
//
//  Only the glyphs in Baked are in the atlas (about 20 fonts plus the scaled
//  copies, so the whole 1,500-glyph set would be wasted space). A constant
//  that a face uses must be in Baked, or it draws as nothing.
//  tools/check_phosphor_icons.py checks the code points against the font and
//  that Baked covers every icon the ImGui code uses.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class PhosphorIcons
{
    public const string AppWindow = "\uE5DA";
    public const string ArrowCircleUp = "\uE030";
    public const string ArrowCounterClockwise = "\uE038";
    public const string ArrowDown = "\uE03E";
    public const string ArrowFatDown = "\uE518";
    public const string ArrowFatLinesDown = "\uE524";
    public const string ArrowLeft = "\uE058";
    public const string ArrowRight = "\uE06C";
    public const string ArrowSquareIn = "\uE5DC";
    public const string ArrowSquareOut = "\uE5DE";
    public const string ArrowUUpLeft = "\uE08A";
    public const string ArrowUUpRight = "\uE08C";
    public const string ArrowUp = "\uE08E";
    public const string ArrowsClockwise = "\uE094";
    public const string ArrowsInLineVertical = "\uE532";
    public const string ArrowsOutCardinal = "\uE0A4";
    public const string ArrowsOutLineVertical = "\uE536";
    public const string Backpack = "\uE922";
    public const string Bag = "\uE0B0";
    public const string Barbell = "\uE0B6";
    public const string BatteryCharging = "\uE0BA";
    public const string Binoculars = "\uEA64";
    public const string Brain = "\uE74E";
    public const string CaretDown = "\uE136";
    public const string CaretLeft = "\uE138";
    public const string CaretRight = "\uE13A";
    public const string CaretUp = "\uE13C";
    public const string CastleTurret = "\uE9D0";
    public const string ChartBar = "\uE150";
    public const string ChartLineUp = "\uE156";
    public const string ChatCircleText = "\uE16E";
    public const string Check = "\uE182";
    public const string CheckCircle = "\uE184";
    public const string CheckSquare = "\uE186";
    public const string ClipboardText = "\uE198";
    public const string ClockCounterClockwise = "\uE1A0";
    public const string Code = "\uE1BC";
    public const string Coins = "\uE78E";
    public const string Compass = "\uE1C8";
    public const string Copy = "\uE1CA";
    public const string Crosshair = "\uE1D6";
    public const string CrosshairSimple = "\uE1D8";
    public const string Crown = "\uE614";
    public const string DotsSixVertical = "\uEAE2";
    public const string DotsThree = "\uE1FE";
    public const string DotsThreeVertical = "\uE208";
    public const string Drop = "\uE210";
    public const string Eraser = "\uE21E";
    public const string Eye = "\uE220";
    public const string FileCode = "\uE914";
    public const string FilePlus = "\uE236";
    public const string Files = "\uE710";
    public const string Fire = "\uE242";
    public const string Flask = "\uE79E";
    public const string FloppyDisk = "\uE248";
    public const string FolderOpen = "\uE256";
    public const string Footprints = "\uEA88";
    public const string Funnel = "\uE266";
    public const string Gear = "\uE270";
    public const string Gift = "\uE276";
    public const string GlobeHemisphereWest = "\uE28C";
    public const string GraduationCap = "\uE62C";
    public const string Hammer = "\uE80E";
    public const string Heart = "\uE2A8";
    public const string HeartBreak = "\uEBE8";
    public const string Heartbeat = "\uE2AC";
    public const string HourglassMedium = "\uE2B8";
    public const string Hourglass = "\uE2B2";
    public const string Info = "\uE2CE";
    public const string Keyboard = "\uE2D8";
    public const string Knife = "\uE636";
    public const string Lightning = "\uE2DE";
    public const string Leaf = "\uE2DA";
    public const string ListChecks = "\uEADC";
    public const string Lock = "\uE2FA";
    public const string LockOpen = "\uE306";
    public const string MagicWand = "\uE6B6";
    public const string MagnifyingGlass = "\uE30C";
    public const string MapPin = "\uE316";
    public const string MapPinPlus = "\uE314";
    public const string MapTrifold = "\uE31A";
    public const string Minus = "\uE32A";
    public const string Monitor = "\uE32E";
    public const string Moon = "\uE330";   // baked for plugins: RynthDefective's Day/Night timer (2026-11-04)
    public const string NavigationArrow = "\uEADE";
    public const string Path = "\uE39C";
    public const string PencilSimple = "\uE3B4";
    public const string PersonSimpleRun = "\uE730";
    public const string Play = "\uE3D0";
    public const string Package = "\uE390";
    public const string Plus = "\uE3D4";
    public const string SlidersHorizontal = "\uE434";
    public const string PushPin = "\uE3E2";
    public const string Power = "\uE3DA";
    public const string Resize = "\uED6E";
    public const string Robot = "\uE762";
    public const string Ruler = "\uE6B8";
    public const string Scales = "\uE750";
    public const string Scissors = "\uEAE0";
    public const string Scroll = "\uEB7A";
    public const string Shield = "\uE40A";
    public const string ShieldSlash = "\uE410";
    public const string ShieldWarning = "\uE412";
    public const string Skull = "\uE916";
    public const string SneakerMove = "\uED60";
    public const string Snowflake = "\uE5AA";
    public const string SortAscending = "\uE444";
    public const string SortDescending = "\uE446";
    public const string Sparkle = "\uE6A2";
    public const string Spiral = "\uE9FA";
    public const string Square = "\uE45E";
    public const string SquaresFour = "\uE464";
    public const string Star = "\uE46A";
    public const string Stop = "\uE46C";
    public const string Storefront = "\uE470";
    public const string Sun = "\uE472";
    public const string Sword = "\uE5BA";
    public const string Target = "\uE47C";
    public const string TerminalWindow = "\uEAE8";
    public const string TextAa = "\uE6EE";
    public const string Trash = "\uE4A6";
    public const string TrendDown = "\uE4AC";
    public const string TreasureChest = "\uEDE2";
    public const string User = "\uE4C2";
    public const string UsersThree = "\uE68E";
    public const string Warning = "\uE4E0";
    public const string WarningCircle = "\uE4E2";
    public const string Wrench = "\uE5D4";
    public const string X = "\uE4F6";
    public const string XCircle = "\uE4F8";

    /// <summary>
    /// The icons merged into the atlas: every one the ImGui faces use. Add a
    /// constant here when a face starts using it (ImGuiFonts reads this once,
    /// at the first atlas build).
    /// </summary>
    public const string Baked =
        AppWindow + ArrowCounterClockwise + ArrowDown + ArrowLeft + ArrowRight + ArrowSquareIn + ArrowSquareOut
        + ArrowUp + ArrowsClockwise + ArrowsInLineVertical + ArrowsOutLineVertical + Backpack + Bag + BatteryCharging
        + CaretDown + CaretLeft + CaretRight + CaretUp + ChartBar + ChartLineUp + ChatCircleText + Check + CheckSquare + ClipboardText + Code
        + Compass + Copy + Crosshair + DotsSixVertical + DotsThree + Drop + Eraser + Eye + FileCode + FilePlus + Files
        + FloppyDisk + Footprints + Funnel + Gear + Hammer + Heart + Heartbeat + Info + Lightning + ListChecks + Lock
        + LockOpen + MagicWand + MagnifyingGlass + MapPin + MapPinPlus + MapTrifold + Minus + Monitor + NavigationArrow
        + PencilSimple + Play + Plus + Resize + Robot + Ruler + Skull + SneakerMove + Sparkle + Spiral + Square
        + Stop + Storefront + Sword + TerminalWindow + Trash + TreasureChest + Warning + X
        + ArrowFatDown + ArrowFatLinesDown + Fire + Flask + HeartBreak + Knife + ShieldSlash + ShieldWarning
        + Snowflake + TrendDown + UsersThree
        + Coins + Gift + HourglassMedium + Scales + Scissors + Shield + SortAscending + SquaresFour
        + ArrowCircleUp + Barbell + Brain + GraduationCap + Hourglass + SortDescending + Package
        + Binoculars + Crown + Sun
        + CastleTurret + ClockCounterClockwise + GlobeHemisphereWest + Path + Star + Target + User
        + TextAa + Leaf + Keyboard
        + PushPin + SlidersHorizontal + Wrench
        + Scroll
        + Moon;

    /// <summary>
    /// Draws <paramref name="glyph"/> (one icon) centred in the box at
    /// <paramref name="min"/>, by the glyph's own bounds rather than the line
    /// box, so icon-only buttons look centred whatever font carries the icon.
    /// Falls back to the text size when the glyph isn't baked.
    /// </summary>
    public static void DrawCentered(ImDrawListPtr dl, ImFontPtr font, string glyph, Vector2 min, Vector2 size, uint color) =>
        DrawCentered(dl, font, font.FontSize, glyph, min, size, color);

    /// <summary>
    /// DrawCentered at <paramref name="px"/> pixels instead of the font's own size
    /// (world overlays that scale with distance); the glyph box scales with it.
    /// Never pushes a font, so it also works outside any window.
    /// </summary>
    public static void DrawCentered(ImDrawListPtr dl, ImFontPtr font, float px, string glyph, Vector2 min, Vector2 size, uint color)
    {
        if (glyph.Length == 0 || px <= 0) return;
        float k = font.FontSize > 0 ? px / font.FontSize : 1f;
        Vector2 centre;
        // ImFontGlyph (1.91): a 4-byte bitfield, AdvanceX, then X0 Y0 X1 Y1. Read by
        // offset: ImGui.NET's generated struct splits the bitfield into three fields.
        byte* g = font.NativePtr == null ? null : (byte*)ImGuiNative.ImFont_FindGlyphNoFallback(font.NativePtr, glyph[0]);
        if (g != null && glyph.Length == 1)
        {
            float* box = (float*)(g + 8);
            centre = new Vector2((box[0] + box[2]) * 0.5f, (box[1] + box[3]) * 0.5f) * k;
        }
        else
        {
            centre = font.CalcTextSizeA(px, float.MaxValue, 0f, glyph) * 0.5f;
        }
        Vector2 at = min + size * 0.5f - centre;
        dl.AddText(font, px, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, glyph);
    }
}
