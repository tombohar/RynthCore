// One unit of "shared" texture state:
//   - A D3D9 RT texture on AC's-emulated device (POOL_DEFAULT, USAGE_RENDERTARGET).
//   - A GL texture name + FBO bound to that texture via wglDXRegisterObjectNV.
//   - The interop handle returned by Register that we Lock/Unlock per frame.
//
// The Phase A spike doesn't render Skia yet — it just clears the FBO to a known
// animated color so we can verify the chain visually. Skia integration is
// straight after the chain is proven.

using System;
using System.Runtime.InteropServices;

namespace WglDxInterop;

internal sealed unsafe class InteropTexture : IDisposable
{
    public IntPtr D3D9Texture { get; private set; }
    public IntPtr D3D9Surface { get; private set; }
    public uint   GlTexture   { get; private set; }
    public uint   GlFramebuffer { get; private set; }
    public IntPtr WglObject   { get; private set; }
    public int    Width       { get; }
    public int    Height      { get; }

    private readonly Gl.Bindings _gl;
    private readonly D3D9Host _d3d;
    private readonly IntPtr _wglDevice;

    public InteropTexture(Gl.Bindings gl, D3D9Host d3d, IntPtr wglDevice, int width, int height)
    {
        _gl = gl;
        _d3d = d3d;
        _wglDevice = wglDevice;
        Width = width;
        Height = height;

        // Step 1 — D3D9 RT texture. If Ex, get a shared handle too.
        IntPtr sharedHandle = IntPtr.Zero;
        if (d3d.IsEx)
        {
            D3D9Texture = d3d.CreateRenderTargetTextureWithSharedHandle(width, height, out sharedHandle);
            Console.WriteLine($"[spike] D3D9Ex RT created with sharedHandle=0x{sharedHandle:X8}");
        }
        else
        {
            D3D9Texture = d3d.CreateRenderTargetTexture(width, height);
        }

        // Step 1b — WGL_NV_DX_interop on D3D9 only supports SURFACES, not the
        // ID3DTexture wrapper. Get the level-0 IDirect3DSurface9 and register
        // that. The GL side still sees it as GL_TEXTURE_2D.
        D3D9Surface = d3d.GetTextureSurfaceLevel0(D3D9Texture);

        // Step 1c — If we have a shared handle, register it BEFORE Register.
        // This is required by NVIDIA's implementation when the resource has
        // a shared handle, even when the GL context's interop device matches
        // the D3D device that created the resource.
        if (sharedHandle != IntPtr.Zero && gl.wglDXSetResourceShareHandleNV != null)
        {
            bool setOk = gl.wglDXSetResourceShareHandleNV(D3D9Surface, sharedHandle);
            Console.WriteLine($"[spike] wglDXSetResourceShareHandleNV(surface) → {setOk}");
            if (!setOk)
                throw new InvalidOperationException($"wglDXSetResourceShareHandleNV failed (LastError=0x{Marshal.GetLastWin32Error():X}).");
        }

        // Step 2 — GL texture name to receive the binding.
        uint glTex = 0;
        gl.glGenTextures(1, &glTex);
        if (glTex == 0) throw new InvalidOperationException("glGenTextures returned 0.");
        GlTexture = glTex;

        // Step 3 — Register the D3D9 surface as a GL texture under the GL context.
        // Try a sequence of (target, access) combinations and (texture vs surface) until one
        // succeeds. NVIDIA's WGL_NV_DX_interop driver has historically been picky about
        // which D3D9 source types it accepts.
        var attempts = new[] {
            (Label: "surface, GL_TEXTURE_2D, WRITE_DISCARD",  Obj: D3D9Surface, Type: Gl.GL_TEXTURE_2D,        Access: Gl.WGL_ACCESS_WRITE_DISCARD_NV),
            (Label: "surface, GL_TEXTURE_2D, READ_WRITE",     Obj: D3D9Surface, Type: Gl.GL_TEXTURE_2D,        Access: Gl.WGL_ACCESS_READ_WRITE_NV),
            (Label: "surface, GL_TEXTURE_RECTANGLE, RW",      Obj: D3D9Surface, Type: Gl.GL_TEXTURE_RECTANGLE, Access: Gl.WGL_ACCESS_READ_WRITE_NV),
            (Label: "texture, GL_TEXTURE_2D, WRITE_DISCARD",  Obj: D3D9Texture, Type: Gl.GL_TEXTURE_2D,        Access: Gl.WGL_ACCESS_WRITE_DISCARD_NV),
            (Label: "texture, GL_TEXTURE_2D, READ_WRITE",     Obj: D3D9Texture, Type: Gl.GL_TEXTURE_2D,        Access: Gl.WGL_ACCESS_READ_WRITE_NV),
        };

        IntPtr wglObj = IntPtr.Zero;
        string lastFail = "";
        foreach (var a in attempts)
        {
            wglObj = gl.wglDXRegisterObjectNV(_wglDevice, a.Obj, GlTexture, a.Type, a.Access);
            if (wglObj != IntPtr.Zero)
            {
                Console.WriteLine($"[spike] wglDXRegisterObjectNV SUCCESS via: {a.Label}");
                break;
            }
            lastFail = $"{a.Label} → LastError=0x{Marshal.GetLastWin32Error():X}";
            Console.WriteLine($"[spike] wglDXRegisterObjectNV FAIL via: {lastFail}");
        }

        if (wglObj == IntPtr.Zero)
            throw new InvalidOperationException($"wglDXRegisterObjectNV failed in all variants. Last: {lastFail}");

        WglObject = wglObj;

        // Step 4 — Build an FBO with the GL texture as color attachment 0.
        // FBO setup is done while the texture is NOT locked (legal per spec; the
        // attachment binding is a GL state operation that doesn't touch contents).
        // The first lock is the point at which GL can actually clear the texture.
        uint fbo = 0;
        gl.glGenFramebuffers(1, &fbo);
        GlFramebuffer = fbo;

        gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, fbo);
        gl.glFramebufferTexture2D(Gl.GL_FRAMEBUFFER, Gl.GL_COLOR_ATTACHMENT0, Gl.GL_TEXTURE_2D, GlTexture, 0);

        uint status = gl.glCheckFramebufferStatus(Gl.GL_FRAMEBUFFER);
        gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);

        if (status != Gl.GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"FBO incomplete after attaching interop texture, status=0x{status:X}.");
    }

    /// <summary>Lock the D3D9 texture so the GL context has exclusive write access.</summary>
    public bool Lock()
    {
        IntPtr h = WglObject;
        return _gl.wglDXLockObjectsNV(_wglDevice, 1, &h);
    }

    /// <summary>Release the D3D9 texture back to D3D9. After this, AC's render thread can sample.</summary>
    public bool Unlock()
    {
        IntPtr h = WglObject;
        return _gl.wglDXUnlockObjectsNV(_wglDevice, 1, &h);
    }

    public void Dispose()
    {
        if (WglObject != IntPtr.Zero)
        {
            _gl.wglDXUnregisterObjectNV(_wglDevice, WglObject);
            WglObject = IntPtr.Zero;
        }
        if (GlFramebuffer != 0)
        {
            uint fbo = GlFramebuffer;
            _gl.glDeleteFramebuffers(1, &fbo);
            GlFramebuffer = 0;
        }
        if (GlTexture != 0)
        {
            uint tex = GlTexture;
            _gl.glDeleteTextures(1, &tex);
            GlTexture = 0;
        }
        if (D3D9Surface != IntPtr.Zero)
        {
            _d3d.ReleaseObject(D3D9Surface);
            D3D9Surface = IntPtr.Zero;
        }
        if (D3D9Texture != IntPtr.Zero)
        {
            _d3d.ReleaseObject(D3D9Texture);
            D3D9Texture = IntPtr.Zero;
        }
    }
}
