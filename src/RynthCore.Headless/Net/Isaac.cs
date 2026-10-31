// ============================================================================
//  RynthCore.Headless - Net/Isaac.cs
//  Bob Jenkins' ISAAC (public domain), with AC's seeding: the generator is initialised
//  as if the seed array were all zero, then a = b = c = the 32-bit connection seed and
//  one round runs. Words are handed out from index 255 down to 0, then another round.
//
//  ServerSeed keys the server's packets, ClientSeed keys ours; each encrypted packet
//  takes one word.
// ============================================================================

namespace RynthCore.Headless.Net;

internal sealed class Isaac
{
    private const uint Golden = 0x9E3779B9;

    private readonly uint[] _mem = new uint[256];
    private readonly uint[] _rsl = new uint[256];
    private uint _a, _b, _c;
    private int _next;

    public Isaac(uint seed)
    {
        uint a = Golden, b = Golden, c = Golden, d = Golden, e = Golden, f = Golden, g = Golden, h = Golden;
        for (int i = 0; i < 4; i++)
            Mix(ref a, ref b, ref c, ref d, ref e, ref f, ref g, ref h);

        // Standard two-pass fill; _rsl is all zero here, so pass one adds nothing.
        for (int pass = 0; pass < 2; pass++)
        {
            uint[] src = pass == 0 ? _rsl : _mem;
            for (int i = 0; i < 256; i += 8)
            {
                a += src[i]; b += src[i + 1]; c += src[i + 2]; d += src[i + 3];
                e += src[i + 4]; f += src[i + 5]; g += src[i + 6]; h += src[i + 7];
                Mix(ref a, ref b, ref c, ref d, ref e, ref f, ref g, ref h);
                _mem[i] = a; _mem[i + 1] = b; _mem[i + 2] = c; _mem[i + 3] = d;
                _mem[i + 4] = e; _mem[i + 5] = f; _mem[i + 6] = g; _mem[i + 7] = h;
            }
        }

        _a = _b = _c = seed;
        Round();
    }

    /// <summary>The next key word.</summary>
    public uint Next()
    {
        uint v = _rsl[_next];
        if (_next == 0)
        {
            Round();
            _next = 255;
        }
        else
        {
            _next--;
        }
        return v;
    }

    private void Round()
    {
        _b += ++_c;
        for (int i = 0; i < 256; i++)
        {
            uint x = _mem[i];
            switch (i & 3)
            {
                case 0: _a ^= _a << 13; break;
                case 1: _a ^= _a >> 6; break;
                case 2: _a ^= _a << 2; break;
                case 3: _a ^= _a >> 16; break;
            }
            _a += _mem[(i + 128) & 255];
            uint y = _mem[(int)((x >> 2) & 255)] + _a + _b;
            _mem[i] = y;
            _b = _mem[(int)((y >> 10) & 255)] + x;
            _rsl[i] = _b;
        }
        _next = 255;
    }

    private static void Mix(ref uint a, ref uint b, ref uint c, ref uint d, ref uint e, ref uint f, ref uint g, ref uint h)
    {
        a ^= b << 11; d += a; b += c;
        b ^= c >> 2;  e += b; c += d;
        c ^= d << 8;  f += c; d += e;
        d ^= e >> 16; g += d; e += f;
        e ^= f << 10; h += e; f += g;
        f ^= g >> 4;  a += f; g += h;
        g ^= h << 8;  b += g; h += a;
        h ^= a >> 9;  c += h; a += b;
    }
}
