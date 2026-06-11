// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/BitmapFont.cs
//  Dependency-free 5x7 bitmap font, ported from RynthCore2's bitmap_font.cpp.
//
//  Standard 5x7 glyph cells. Each glyph is 7 rows; the low 5 bits of each row
//  are the columns, bit 4 = leftmost. A set bit is a lit pixel cell. Lit cells
//  are emitted as rectangles through a caller-supplied sink so the renderer
//  (VitalHud) can turn each into a colored quad. No allocation, no texture.
// ═══════════════════════════════════════════════════════════════════════════

using System;

namespace RynthCore.Engine.D3D9;

internal static class BitmapFont
{
    /// <summary>Receives one lit pixel cell as an inclusive-exclusive rect.</summary>
    internal delegate void CellSink(int x0, int y0, int x1, int y1);

    public const int GlyphW = 5;
    public const int GlyphH = 7;
    private const int Advance = GlyphW + 1; // one-cell gap between glyphs

    // Glyph table row, in a fixed order; IndexOf maps a character to a row.
    //   0        = space
    //   1..10    = '0'..'9'
    //   11..36   = 'A'..'Z'
    //   37..45   = '/' ':' '.' ',' '-' '+' '%' '(' ')'
    private const int GSpace = 0;
    private const int GDigit0 = 1;
    private const int GLetterA = 11;
    private const int GSlash = 37;
    private const int GColon = 38;
    private const int GDot = 39;
    private const int GComma = 40;
    private const int GDash = 41;
    private const int GPlus = 42;
    private const int GPercent = 43;
    private const int GLParen = 44;
    private const int GRParen = 45;

    // 5x7 cells, bit 4 leftmost — transcribed verbatim from bitmap_font.cpp.
    private static readonly byte[][] Glyphs =
    [
        /* space */ [0, 0, 0, 0, 0, 0, 0],
        /* 0 */ [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        /* 1 */ [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        /* 2 */ [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        /* 3 */ [0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110],
        /* 4 */ [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        /* 5 */ [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        /* 6 */ [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        /* 7 */ [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        /* 8 */ [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        /* 9 */ [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        /* A */ [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        /* B */ [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        /* C */ [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        /* D */ [0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100],
        /* E */ [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        /* F */ [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        /* G */ [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111],
        /* H */ [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        /* I */ [0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        /* J */ [0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100],
        /* K */ [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
        /* L */ [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
        /* M */ [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
        /* N */ [0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001],
        /* O */ [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        /* P */ [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
        /* Q */ [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
        /* R */ [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
        /* S */ [0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110],
        /* T */ [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
        /* U */ [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        /* V */ [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
        /* W */ [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010],
        /* X */ [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
        /* Y */ [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
        /* Z */ [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
        /* / */ [0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000],
        /* : */ [0b00000, 0b00100, 0b00100, 0b00000, 0b00100, 0b00100, 0b00000],
        /* . */ [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100],
        /* , */ [0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b00100, 0b01000],
        /* - */ [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
        /* + */ [0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000],
        /* % */ [0b11001, 0b11010, 0b00010, 0b00100, 0b01000, 0b01011, 0b10011],
        /* ( */ [0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010],
        /* ) */ [0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000],
    ];

    // Maps an ASCII character to a glyph row, or -1 if unsupported (advance blank).
    private static int IndexOf(char c)
    {
        if (c >= '0' && c <= '9') return GDigit0 + (c - '0');
        if (c >= 'A' && c <= 'Z') return GLetterA + (c - 'A');
        if (c >= 'a' && c <= 'z') return GLetterA + (c - 'a'); // fold to uppercase
        return c switch
        {
            ' ' => GSpace,
            '/' => GSlash,
            ':' => GColon,
            '.' => GDot,
            ',' => GComma,
            '-' => GDash,
            '+' => GPlus,
            '%' => GPercent,
            '(' => GLParen,
            ')' => GRParen,
            _ => -1,
        };
    }

    /// <summary>Pixel height of a glyph at the given cell size.</summary>
    public static int Height(int cellPx)
    {
        if (cellPx < 1) cellPx = 1;
        return GlyphH * cellPx;
    }

    /// <summary>Pixel width of a rendered string at the given cell size. Takes a
    /// span so callers can measure stack-formatted text without allocating.</summary>
    public static int MeasureWidth(ReadOnlySpan<char> s, int cellPx)
    {
        if (s.IsEmpty) return 0;
        if (cellPx < 1) cellPx = 1;
        int n = s.Length;
        // n glyphs, each GlyphW wide, with (n-1) single-cell gaps between them.
        return (n * GlyphW + (n - 1)) * cellPx;
    }

    /// <summary>
    /// Emits the lit cells of <paramref name="s"/> at (x, y) at the given cell
    /// size by invoking <paramref name="sink"/> once per lit cell. Takes a span
    /// (string literals convert implicitly) so it stays allocation-free; the sink
    /// is a cached delegate on the caller side.
    /// </summary>
    public static void Emit(ReadOnlySpan<char> s, int x, int y, int cellPx, CellSink sink)
    {
        if (s.IsEmpty || sink == null) return;
        if (cellPx < 1) cellPx = 1;

        int penX = x;
        foreach (char ch in s)
        {
            int gi = IndexOf(ch);
            if (gi >= 0 && gi != GSpace)
            {
                byte[] rows = Glyphs[gi];
                for (int r = 0; r < GlyphH; r++)
                {
                    int bits = rows[r];
                    for (int c = 0; c < GlyphW; c++)
                    {
                        if (((bits >> (GlyphW - 1 - c)) & 1) != 0)
                        {
                            int x0 = penX + c * cellPx;
                            int y0 = y + r * cellPx;
                            sink(x0, y0, x0 + cellPx, y0 + cellPx);
                        }
                    }
                }
            }
            penX += Advance * cellPx;
        }
    }
}
