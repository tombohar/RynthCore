// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/Nav3DRenderer.cs
//  Data store for 3D navigation markers. Plugins submit geometry each frame
//  via AddRing/AddLine; DX9Backend.RenderNav3D reads it during EndScene.
// ═══════════════════════════════════════════════════════════════════════════

namespace RynthCore.Engine.D3D9;

internal static class Nav3DRenderer
{
    private const int MaxRings = 256;
    private const int MaxLines = 512;

    // Ring: center(x,y,z) + radius + thickness + color
    private static readonly float[] _ringX = new float[MaxRings];
    private static readonly float[] _ringY = new float[MaxRings];
    private static readonly float[] _ringZ = new float[MaxRings];
    private static readonly float[] _ringRadius = new float[MaxRings];
    private static readonly float[] _ringThick = new float[MaxRings];
    private static readonly uint[] _ringColor = new uint[MaxRings];
    private static int _ringCount;

    // Line: endpoints + thickness + color
    private static readonly float[] _lineX1 = new float[MaxLines];
    private static readonly float[] _lineY1 = new float[MaxLines];
    private static readonly float[] _lineZ1 = new float[MaxLines];
    private static readonly float[] _lineX2 = new float[MaxLines];
    private static readonly float[] _lineY2 = new float[MaxLines];
    private static readonly float[] _lineZ2 = new float[MaxLines];
    private static readonly float[] _lineThick = new float[MaxLines];
    private static readonly uint[] _lineColor = new uint[MaxLines];
    private static int _lineCount;

    public static int RingCount => _ringCount;
    public static int LineCount => _lineCount;

    public static void ClearFrame()
    {
        _ringCount = 0;
        _lineCount = 0;
    }

    public static void AddRing(float wx, float wy, float wz, float radius, float thickness, uint colorArgb)
    {
        if (_ringCount >= MaxRings) return;
        int i = _ringCount++;
        _ringX[i] = wx; _ringY[i] = wy; _ringZ[i] = wz;
        _ringRadius[i] = radius;
        _ringThick[i] = thickness;
        _ringColor[i] = colorArgb;
    }

    public static void AddLine(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
    {
        if (_lineCount >= MaxLines) return;
        int i = _lineCount++;
        _lineX1[i] = x1; _lineY1[i] = y1; _lineZ1[i] = z1;
        _lineX2[i] = x2; _lineY2[i] = y2; _lineZ2[i] = z2;
        _lineThick[i] = thickness;
        _lineColor[i] = colorArgb;
    }

    public static void GetRing(int i, out float x, out float y, out float z,
        out float radius, out float thickness, out uint color)
    {
        x = _ringX[i]; y = _ringY[i]; z = _ringZ[i];
        radius = _ringRadius[i]; thickness = _ringThick[i]; color = _ringColor[i];
    }

    public static void GetLine(int i, out float x1, out float y1, out float z1,
        out float x2, out float y2, out float z2, out float thickness, out uint color)
    {
        x1 = _lineX1[i]; y1 = _lineY1[i]; z1 = _lineZ1[i];
        x2 = _lineX2[i]; y2 = _lineY2[i]; z2 = _lineZ2[i];
        thickness = _lineThick[i]; color = _lineColor[i];
    }

    // C ABI callbacks for plugin contract
    public static void Nav3DClearCallback() => ClearFrame();
    public static void Nav3DAddRingCallback(float wx, float wy, float wz, float radius, float thickness, uint color)
        => AddRing(wx, wy, wz, radius, thickness, color);
    public static void Nav3DAddLineCallback(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint color)
        => AddLine(x1, y1, z1, x2, y2, z2, thickness, color);
}
