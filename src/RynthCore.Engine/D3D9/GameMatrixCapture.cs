// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/GameMatrixCapture.cs
//  Captures the game's camera transform and perspective Projection matrix
//  for WorldToScreen projection.
//
//  AC sets View=identity and World=identity — it transforms all vertices
//  on the CPU before sending to D3D9. Only the Projection matrix is real.
//
//  Strategy:
//   - Hook SetTransform to capture the perspective Projection matrix.
//   - Read the camera position + rotation from SmartBox memory.
//   - Build the View matrix manually, then ViewProj = View * Proj.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.D3D9;

internal static unsafe class GameMatrixCapture
{
    private const uint D3DTS_PROJECTION = 3;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTransformD(IntPtr dev, uint state, float* pMatrix);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetViewportD(IntPtr dev, int* pViewport);

    // SetTransform hook
    private static SetTransformD? _originalSetTransform;
    private static SetTransformD? _hookDelegate;
    private static bool _hookInstalled;

    // Viewport
    private static GetViewportD? _getViewport;
    private static bool _viewportCached;
    private static uint _vpWidth;
    private static uint _vpHeight;
    private static bool _hasViewport;

    // Captured Projection matrix
    private static readonly float[] _perspProj = new float[16];
    private static bool _hasProj;

    // Computed matrices
    private static readonly float[] _viewMatrix = new float[16];
    private static readonly float[] _viewProj = new float[16];
    private static bool _hasValidViewProj;

    // Camera frame from SmartBox memory (same layout as UB Camera.cs)
    private const int CameraFrameOffset = 0x08;
    private const int CamRotOffset = 0x18;  // 3x3 rotation matrix
    private const int CamPosOffset = 0x3C;  // position (x, y, z)

    // Diagnostics
    private static int _framesSinceLog;
    private static bool _loggedFirstCapture;

    public static bool HasCapturedFrame => _hasValidViewProj;
    public static uint ViewportWidth => _vpWidth;
    public static uint ViewportHeight => _vpHeight;

    /// <summary>Copy the 4x4 View matrix (row-major) into the destination.</summary>
    public static void GetViewMatrix(ref float dest)
    {
        fixed (float* s = _viewMatrix)
        fixed (float* d = &dest)
        {
            for (int i = 0; i < 16; i++)
                d[i] = s[i];
        }
    }

    /// <summary>Copy the 4x4 Projection matrix (row-major) into the destination.</summary>
    public static void GetProjectionMatrix(ref float dest)
    {
        fixed (float* s = _perspProj)
        fixed (float* d = &dest)
        {
            for (int i = 0; i < 16; i++)
                d[i] = s[i];
        }
    }

    private static int SetTransformDetour(IntPtr dev, uint state, float* pMatrix)
    {
        if (pMatrix != null && state == D3DTS_PROJECTION)
        {
            bool isPerspective = Math.Abs(pMatrix[11]) > 0.01f;
            if (isPerspective)
            {
                for (int i = 0; i < 16; i++)
                    _perspProj[i] = pMatrix[i];
                _hasProj = true;
            }
        }
        return _originalSetTransform!(dev, state, pMatrix);
    }

    public static void CaptureFrame(IntPtr pDevice)
    {
        if (pDevice == IntPtr.Zero)
            return;

        if (!_hookInstalled)
            InstallHook(pDevice);

        // Read viewport
        if (!_viewportCached)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pDevice);
            _getViewport = Marshal.GetDelegateForFunctionPointer<GetViewportD>(
                Marshal.ReadIntPtr(vtable, DeviceVTableIndex.GetViewport * IntPtr.Size));
            _viewportCached = true;
        }

        int* vp = stackalloc int[6];
        if (_getViewport!(pDevice, vp) >= 0 && vp[2] > 0 && vp[3] > 0)
        {
            _vpWidth = (uint)vp[2];
            _vpHeight = (uint)vp[3];
            _hasViewport = true;
        }

        if (_hasProj)
            BuildViewProjFromCamera();

        _framesSinceLog++;
        if (_framesSinceLog >= 3000)
        {
            _framesSinceLog = 0;
            RynthLog.D3D9($"GameMatrixCapture: valid={_hasValidViewProj} vp={_vpWidth}x{_vpHeight}");
        }
    }

    private static void BuildViewProjFromCamera()
    {
        if (!SmartBoxLocator.TryGetSmartBox(out IntPtr smartBox, out _, out _))
            return;

        IntPtr camFrame = smartBox + CameraFrameOffset;
        if (!SmartBoxLocator.IsMemoryReadable(camFrame + CamPosOffset, 12))
            return;

        // Read 3x3 rotation matrix
        float m11 = ReadFloat(camFrame + CamRotOffset + 0);
        float m12 = ReadFloat(camFrame + CamRotOffset + 4);
        float m13 = ReadFloat(camFrame + CamRotOffset + 8);
        float m21 = ReadFloat(camFrame + CamRotOffset + 12);
        float m22 = ReadFloat(camFrame + CamRotOffset + 16);
        float m23 = ReadFloat(camFrame + CamRotOffset + 20);
        float m31 = ReadFloat(camFrame + CamRotOffset + 24);
        float m32 = ReadFloat(camFrame + CamRotOffset + 28);
        float m33 = ReadFloat(camFrame + CamRotOffset + 32);

        // Read position (AC physics: x=EW, y=NS, z=height)
        float cx = ReadFloat(camFrame + CamPosOffset + 0);
        float cy = ReadFloat(camFrame + CamPosOffset + 4);
        float cz = ReadFloat(camFrame + CamPosOffset + 8);

        if (cx == 0f && cy == 0f && cz == 0f)
            return;

        // View matrix rotation block (D3D Y-up, with AC Y/Z swap).
        // AC camera rows: row1=Right, row2=Forward, row3=Up
        // After Y/Z component swap → D3D basis vectors as columns:
        //   col0 = Right_d3d   = (m11, m13, m12)
        //   col1 = Up_d3d      = (m31, m33, m32)
        //   col2 = Forward_d3d = (m21, m23, m22)
        _viewMatrix[0]  = m11; _viewMatrix[1]  = m31; _viewMatrix[2]  = m21; _viewMatrix[3]  = 0;
        _viewMatrix[4]  = m13; _viewMatrix[5]  = m33; _viewMatrix[6]  = m23; _viewMatrix[7]  = 0;
        _viewMatrix[8]  = m12; _viewMatrix[9]  = m32; _viewMatrix[10] = m22; _viewMatrix[11] = 0;

        // Translation = -CamPos_D3D * R_view
        // CamPos_D3D = (cx, cz, cy) after Y/Z swap
        float px = cx;   // EW
        float py = cz;   // height (D3D Y)
        float pz = cy;   // NS (D3D Z)

        _viewMatrix[12] = -(px * _viewMatrix[0] + py * _viewMatrix[4] + pz * _viewMatrix[8]);
        _viewMatrix[13] = -(px * _viewMatrix[1] + py * _viewMatrix[5] + pz * _viewMatrix[9]);
        _viewMatrix[14] = -(px * _viewMatrix[2] + py * _viewMatrix[6] + pz * _viewMatrix[10]);
        _viewMatrix[15] = 1;

        // ViewProj = View * Projection
        fixed (float* v = _viewMatrix)
        fixed (float* p = _perspProj)
        fixed (float* r = _viewProj)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    r[row * 4 + col] =
                        v[row * 4 + 0] * p[0 * 4 + col] +
                        v[row * 4 + 1] * p[1 * 4 + col] +
                        v[row * 4 + 2] * p[2 * 4 + col] +
                        v[row * 4 + 3] * p[3 * 4 + col];
        }

        if (!_loggedFirstCapture)
        {
            _loggedFirstCapture = true;
            RynthLog.D3D9($"GameMatrixCapture: camera pos=({cx:F2},{cy:F2},{cz:F2}) [EW,NS,Z]");
            RynthLog.D3D9($"GameMatrixCapture: View row0=[{_viewMatrix[0]:F4}, {_viewMatrix[1]:F4}, {_viewMatrix[2]:F4}, {_viewMatrix[3]:F4}]");
            RynthLog.D3D9($"GameMatrixCapture: View row1=[{_viewMatrix[4]:F4}, {_viewMatrix[5]:F4}, {_viewMatrix[6]:F4}, {_viewMatrix[7]:F4}]");
            RynthLog.D3D9($"GameMatrixCapture: View row2=[{_viewMatrix[8]:F4}, {_viewMatrix[9]:F4}, {_viewMatrix[10]:F4}, {_viewMatrix[11]:F4}]");
            RynthLog.D3D9($"GameMatrixCapture: View row3=[{_viewMatrix[12]:F4}, {_viewMatrix[13]:F4}, {_viewMatrix[14]:F4}, {_viewMatrix[15]:F4}]");
            RynthLog.D3D9($"GameMatrixCapture: viewport={_vpWidth}x{_vpHeight}");
        }

        _hasValidViewProj = true;
    }

    private static float ReadFloat(IntPtr address)
    {
        int bits = Marshal.ReadInt32(address);
        return BitConverter.Int32BitsToSingle(bits);
    }

    public static bool WorldToScreen(float wx, float wy, float wz, out float sx, out float sy)
    {
        sx = 0f;
        sy = 0f;

        if (!_hasValidViewProj || _vpWidth == 0 || _vpHeight == 0)
            return false;

        float clipX = wx * _viewProj[0] + wy * _viewProj[4] + wz * _viewProj[8]  + _viewProj[12];
        float clipY = wx * _viewProj[1] + wy * _viewProj[5] + wz * _viewProj[9]  + _viewProj[13];
        float clipW = wx * _viewProj[3] + wy * _viewProj[7] + wz * _viewProj[11] + _viewProj[15];

        if (clipW <= 0.001f)
            return false;

        float ndcX = clipX / clipW;
        float ndcY = clipY / clipW;

        sx = (ndcX + 1.0f) * 0.5f * _vpWidth;
        sy = (1.0f - ndcY) * 0.5f * _vpHeight;

        return true;
    }

    public static int WorldToScreenCallback(float wx, float wy, float wz, float* sx, float* sy)
    {
        bool result = WorldToScreen(wx, wy, wz, out float screenX, out float screenY);
        *sx = screenX;
        *sy = screenY;
        return result ? 1 : 0;
    }

    public static int GetViewportSizeCallback(uint* width, uint* height)
    {
        if (!_hasViewport || _vpWidth == 0 || _vpHeight == 0)
            return 0;

        *width = _vpWidth;
        *height = _vpHeight;
        return 1;
    }

    private static void InstallHook(IntPtr pDevice)
    {
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(pDevice);
            IntPtr setTransformAddr = Marshal.ReadIntPtr(vtable, DeviceVTableIndex.SetTransform * IntPtr.Size);

            _hookDelegate = new SetTransformD(SetTransformDetour);
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);
            _originalSetTransform = Marshal.GetDelegateForFunctionPointer<SetTransformD>(MinHook.HookCreate(setTransformAddr, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(setTransformAddr);
            _hookInstalled = true;

            RynthLog.D3D9("GameMatrixCapture: SetTransform hook installed.");
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"GameMatrixCapture: SetTransform hook FAILED — {ex.Message}");
        }
    }
}
