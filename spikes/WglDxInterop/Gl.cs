// OpenGL + WGL bootstrap. We need a real GL context (not ANGLE) to access the
// WGL_NV_DX_interop2 extension, which is the whole point of the spike.
//
// Sequence:
//   1. Create a hidden Win32 window and DC.
//   2. ChoosePixelFormat / SetPixelFormat with a basic 32-bit RGBA / no depth format.
//   3. wglCreateContext → fallback GL 1.x context, just to bootstrap.
//   4. wglMakeCurrent the fallback so we can call wglGetProcAddress for ARB extensions.
//   5. (Optional) wglCreateContextAttribsARB to upgrade to 3.3 core. Skia GL needs this.
//   6. Load WGL_NV_DX_interop2 entry points via wglGetProcAddress.
//   7. Confirm the extension string contains "WGL_NV_DX_interop2".
//
// Spike question A7 (multi-adapter): on a laptop with dGPU+iGPU, the default GL
// context may be on the wrong adapter. Confirming this requires hardware to test.
// For now, the spike just uses whatever the default DC ends up on.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WglDxInterop;

internal static unsafe class Gl
{
    // ─── opengl32.dll core ────────────────────────────────────────────────
    [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool   wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] public static extern bool   wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] public static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] public static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr wglGetProcAddress(string name);

    // ─── core GL we need (resolved via wglGetProcAddress for >=GL 1.5 / ARB) ──
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glClearD(uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glClearColorD(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glViewportD(int x, int y, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glFinishD();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glFlushD();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate IntPtr glGetStringD(uint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glGenTexturesD(int n, uint* textures);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glDeleteTexturesD(int n, uint* textures);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glBindTextureD(uint target, uint texture);

    // FBO entry points (GL 3.0 / ARB_framebuffer_object)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glGenFramebuffersD(int n, uint* framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glDeleteFramebuffersD(int n, uint* framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glBindFramebufferD(uint target, uint framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void glFramebufferTexture2DD(uint target, uint attachment, uint textarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint glCheckFramebufferStatusD(uint target);

    // GL constants we need
    public const uint GL_VENDOR     = 0x1F00;
    public const uint GL_RENDERER   = 0x1F01;
    public const uint GL_VERSION    = 0x1F02;
    public const uint GL_EXTENSIONS = 0x1F03;
    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_TEXTURE_RECTANGLE = 0x84F5;
    public const uint GL_FRAMEBUFFER          = 0x8D40;
    public const uint GL_COLOR_ATTACHMENT0    = 0x8CE0;
    public const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint GL_COLOR_BUFFER_BIT     = 0x00004000;

    // ─── WGL_ARB_extensions_string + WGL_ARB_create_context ────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr wglGetExtensionsStringARBD(IntPtr hdc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr wglCreateContextAttribsARBD(IntPtr hdc, IntPtr hShareContext, int* attribList);

    public const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    public const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    public const int WGL_CONTEXT_PROFILE_MASK_ARB  = 0x9126;
    public const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    // ─── WGL_NV_DX_interop2 ────────────────────────────────────────────────
    // Function signatures from the registered extension spec at
    // https://registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop.txt
    // (DX_interop2 is the same surface; v2 just lifts the D3D11 restriction.)
    public const uint WGL_ACCESS_READ_ONLY_NV     = 0x00000000;
    public const uint WGL_ACCESS_READ_WRITE_NV    = 0x00000001;
    public const uint WGL_ACCESS_WRITE_DISCARD_NV = 0x00000002;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr wglDXOpenDeviceNVD(IntPtr dxDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool   wglDXCloseDeviceNVD(IntPtr hDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr wglDXRegisterObjectNVD(IntPtr hDevice, IntPtr dxObject, uint name, uint type, uint access);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool   wglDXUnregisterObjectNVD(IntPtr hDevice, IntPtr hObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool   wglDXLockObjectsNVD(IntPtr hDevice, int count, IntPtr* objects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool   wglDXUnlockObjectsNVD(IntPtr hDevice, int count, IntPtr* objects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool   wglDXSetResourceShareHandleNVD(IntPtr dxObject, IntPtr shareHandle);

    public sealed class Bindings
    {
        public glClearD glClear = null!;
        public glClearColorD glClearColor = null!;
        public glViewportD glViewport = null!;
        public glFinishD glFinish = null!;
        public glFlushD glFlush = null!;
        public glGetStringD glGetString = null!;
        public glGenTexturesD glGenTextures = null!;
        public glDeleteTexturesD glDeleteTextures = null!;
        public glBindTextureD glBindTexture = null!;

        public glGenFramebuffersD glGenFramebuffers = null!;
        public glDeleteFramebuffersD glDeleteFramebuffers = null!;
        public glBindFramebufferD glBindFramebuffer = null!;
        public glFramebufferTexture2DD glFramebufferTexture2D = null!;
        public glCheckFramebufferStatusD glCheckFramebufferStatus = null!;

        public wglDXOpenDeviceNVD wglDXOpenDeviceNV = null!;
        public wglDXCloseDeviceNVD wglDXCloseDeviceNV = null!;
        public wglDXRegisterObjectNVD wglDXRegisterObjectNV = null!;
        public wglDXUnregisterObjectNVD wglDXUnregisterObjectNV = null!;
        public wglDXLockObjectsNVD wglDXLockObjectsNV = null!;
        public wglDXUnlockObjectsNVD wglDXUnlockObjectsNV = null!;
        public wglDXSetResourceShareHandleNVD? wglDXSetResourceShareHandleNV; // optional; only needed for D3D9 shared HANDLE textures

        public bool Loaded;
    }

    public static T Resolve<T>(string name) where T : Delegate
    {
        IntPtr proc = wglGetProcAddress(name);
        if (proc == IntPtr.Zero || proc == new IntPtr(1) || proc == new IntPtr(2) || proc == new IntPtr(3) || proc == new IntPtr(-1))
        {
            // wglGetProcAddress returns 1/2/3/-1 on some drivers when the name isn't a real ICD entry; fall through to opengl32 itself.
            IntPtr opengl32 = Win32.LoadLibraryA("opengl32.dll");
            proc = Win32.GetProcAddress(opengl32, name);
        }
        if (proc == IntPtr.Zero)
            throw new InvalidOperationException($"GL proc not found: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(proc);
    }

    public static T? TryResolve<T>(string name) where T : Delegate
    {
        IntPtr proc = wglGetProcAddress(name);
        if (proc == IntPtr.Zero || proc == new IntPtr(1) || proc == new IntPtr(2) || proc == new IntPtr(3) || proc == new IntPtr(-1))
        {
            IntPtr opengl32 = Win32.LoadLibraryA("opengl32.dll");
            proc = Win32.GetProcAddress(opengl32, name);
        }
        if (proc == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(proc);
    }

    public static Bindings LoadAll()
    {
        var b = new Bindings
        {
            glClear              = Resolve<glClearD>("glClear"),
            glClearColor         = Resolve<glClearColorD>("glClearColor"),
            glViewport           = Resolve<glViewportD>("glViewport"),
            glFinish             = Resolve<glFinishD>("glFinish"),
            glFlush              = Resolve<glFlushD>("glFlush"),
            glGetString          = Resolve<glGetStringD>("glGetString"),
            glGenTextures        = Resolve<glGenTexturesD>("glGenTextures"),
            glDeleteTextures     = Resolve<glDeleteTexturesD>("glDeleteTextures"),
            glBindTexture        = Resolve<glBindTextureD>("glBindTexture"),

            glGenFramebuffers       = Resolve<glGenFramebuffersD>("glGenFramebuffers"),
            glDeleteFramebuffers    = Resolve<glDeleteFramebuffersD>("glDeleteFramebuffers"),
            glBindFramebuffer       = Resolve<glBindFramebufferD>("glBindFramebuffer"),
            glFramebufferTexture2D  = Resolve<glFramebufferTexture2DD>("glFramebufferTexture2D"),
            glCheckFramebufferStatus = Resolve<glCheckFramebufferStatusD>("glCheckFramebufferStatus"),

            wglDXOpenDeviceNV       = Resolve<wglDXOpenDeviceNVD>("wglDXOpenDeviceNV"),
            wglDXCloseDeviceNV      = Resolve<wglDXCloseDeviceNVD>("wglDXCloseDeviceNV"),
            wglDXRegisterObjectNV   = Resolve<wglDXRegisterObjectNVD>("wglDXRegisterObjectNV"),
            wglDXUnregisterObjectNV = Resolve<wglDXUnregisterObjectNVD>("wglDXUnregisterObjectNV"),
            wglDXLockObjectsNV      = Resolve<wglDXLockObjectsNVD>("wglDXLockObjectsNV"),
            wglDXUnlockObjectsNV    = Resolve<wglDXUnlockObjectsNVD>("wglDXUnlockObjectsNV"),

            wglDXSetResourceShareHandleNV = TryResolve<wglDXSetResourceShareHandleNVD>("wglDXSetResourceShareHandleNV"),

            Loaded = true,
        };

        return b;
    }

    public static string GetGlString(Bindings b, uint name)
    {
        IntPtr p = b.glGetString(name);
        return p == IntPtr.Zero ? "<null>" : Marshal.PtrToStringAnsi(p) ?? "<null>";
    }

    public static string GetWglExtensionsString(IntPtr hdc)
    {
        var fn = TryResolve<wglGetExtensionsStringARBD>("wglGetExtensionsStringARB");
        if (fn == null) return string.Empty;
        IntPtr p = fn(hdc);
        return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p) ?? string.Empty;
    }
}
