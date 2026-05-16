// Phase A spike — WGL_NV_DX_interop2 architectural test.
//
// What it does end-to-end:
//   1. Open a normal Win32 window and create a plain D3D9 device on it.
//      (Direct3DCreate9, NOT Ex — exactly what AC ships.)
//   2. Create a 'producer' window+DC and bring up a real OpenGL 3.3 core context.
//   3. Confirm WGL_NV_DX_interop2 is in the WGL extensions string.
//      (Spike A8 — driver coverage. Hard-fail with a clear log if missing.)
//   4. Open the D3D9 device for interop and register a D3DUSAGE_RENDERTARGET
//      texture under it as a GL texture.
//   5. Per frame:
//        - Lock the texture for GL.
//        - Bind FBO, set viewport, clear to an animated color (proves the GL
//          path is actually writing into the D3D9 resource).
//        - Unlock.
//        - Use the D3D9 texture in a normal D3D9 fullscreen quad to the window.
//   6. Log frame timing every second so spike A3 (lock/unlock cost) gets data.
//
// This is intentionally Skia-free for the first cut. Skia layers on top
// trivially once the chain works (replace the glClearColor with a Skia render
// into the same FBO). Proving the chain first means a Skia rendering bug is
// disambiguated from an interop bug.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WglDxInterop;

internal static unsafe class Program
{
    private const int WindowWidth = 1280;
    private const int WindowHeight = 720;

    private static IntPtr _hwnd;
    private static bool _quitRequested;

    // Holding the WndProc delegate alive for the lifetime of the window —
    // RegisterClassExW stores a function pointer; if the delegate is GC'd
    // we get a callback into freed memory.
    private static Win32.WndProcDelegate? _wndProc;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.WriteLine("[spike] WGL_NV_DX_interop2 architectural test");

        try
        {
            CreateMainWindow();

            // Spike branch — `--ex` flag selects D3D9Ex+shared-handle path so we can
            // empirically test whether WGL_NV_DX_interop2 needs Ex's shared handles.
            // Default is plain D3D9 to mirror AC's reality.
            bool useEx = Array.IndexOf(args, "--ex") >= 0;

            using var d3d = new D3D9Host();
            if (useEx) d3d.InitializeEx(_hwnd, WindowWidth, WindowHeight);
            else       d3d.Initialize(_hwnd, WindowWidth, WindowHeight);
            Console.WriteLine($"[spike] D3D9{(d3d.IsEx ? "Ex" : "")} device created on adapter {d3d.AdapterIndex}.");

            // GL context lives on the SAME thread as the message loop and D3D9.
            // For the engine integration, the GL context will live on Avalonia's
            // STA thread; here on the spike main thread is fine.
            //
            // Use a SEPARATE hidden window for the GL context so the GL default
            // framebuffer doesn't compete with the D3D9 swap chain on the visible
            // window. NVIDIA's WGL_NV_DX_interop layer is documented as needing
            // this isolation for D3D9 sources.
            IntPtr glHwnd = CreateHiddenGlWindow();
            IntPtr glContext = CreateGlContext(glHwnd, out IntPtr hdcUsed);
            if (glContext == IntPtr.Zero)
                return Fail("Could not create OpenGL context.");

            string wglExt = Gl.GetWglExtensionsString(hdcUsed);
            Console.WriteLine($"[spike] WGL extensions: {wglExt.Length} chars.");
            bool hasInterop2 = wglExt.Contains("WGL_NV_DX_interop2");
            bool hasInterop1 = wglExt.Contains("WGL_NV_DX_interop");
            Console.WriteLine($"[spike] WGL_NV_DX_interop = {hasInterop1}, WGL_NV_DX_interop2 = {hasInterop2}");
            if (!hasInterop2 && !hasInterop1)
                return Fail("Driver does not advertise WGL_NV_DX_interop. Spike cannot run on this hardware.");

            var gl = Gl.LoadAll();
            Console.WriteLine($"[spike] GL_VENDOR   = {Gl.GetGlString(gl, Gl.GL_VENDOR)}");
            Console.WriteLine($"[spike] GL_RENDERER = {Gl.GetGlString(gl, Gl.GL_RENDERER)}");
            Console.WriteLine($"[spike] GL_VERSION  = {Gl.GetGlString(gl, Gl.GL_VERSION)}");

            IntPtr wglDevice = gl.wglDXOpenDeviceNV(d3d.Device);
            if (wglDevice == IntPtr.Zero)
                return Fail($"wglDXOpenDeviceNV failed (LastError=0x{Marshal.GetLastWin32Error():X}).");
            Console.WriteLine($"[spike] wglDXOpenDeviceNV → 0x{wglDevice:X}");

            using var tex = new InteropTexture(gl, d3d, wglDevice, WindowWidth, WindowHeight);
            Console.WriteLine($"[spike] Registered D3D9 RT 0x{tex.D3D9Texture:X} as GL tex {tex.GlTexture}, FBO {tex.GlFramebuffer}, wglObj 0x{tex.WglObject:X}.");

            RunRenderLoop(d3d, gl, tex);

            // Teardown order matters — Unregister + CloseDeviceNV before D3D9 release.
            gl.wglDXCloseDeviceNV(wglDevice);
            Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            Gl.wglDeleteContext(glContext);

            Console.WriteLine("[spike] Clean exit.");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail($"Unhandled: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine($"[spike] FAILED: {msg}");
        return 1;
    }

    // ─── Window ───────────────────────────────────────────────────────────
    private static void CreateMainWindow()
    {
        _wndProc = StaticWndProc;
        IntPtr wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);

        var wc = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = wndProcPtr,
            hInstance = Win32.GetModuleHandleW(null),
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni("WglDxInteropSpike"),
        };

        ushort atom = Win32.RegisterClassExW(ref wc);
        if (atom == 0)
            throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");

        _hwnd = Win32.CreateWindowExW(
            0, "WglDxInteropSpike", "WGL_NV_DX_interop2 spike",
            Win32.WS_OVERLAPPEDWINDOW | Win32.WS_VISIBLE,
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, WindowWidth, WindowHeight,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");

        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_CLOSE:
                _quitRequested = true;
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static IntPtr CreateHiddenGlWindow()
    {
        IntPtr hwnd = Win32.CreateWindowExW(
            0, "WglDxInteropSpike", "GL helper",
            Win32.WS_POPUP,
            0, 0, 16, 16,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"GL helper CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
        return hwnd;
    }

    // ─── GL context bootstrap ─────────────────────────────────────────────
    private static IntPtr CreateGlContext(IntPtr hwnd, out IntPtr hdcOut)
    {
        IntPtr hdc = Win32.GetDC(hwnd);
        if (hdc == IntPtr.Zero) { hdcOut = IntPtr.Zero; return IntPtr.Zero; }

        var pfd = new Win32.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Win32.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = (uint)(Win32.PFD_DRAW_TO_WINDOW | Win32.PFD_SUPPORT_OPENGL | Win32.PFD_DOUBLEBUFFER),
            iPixelType = Win32.PFD_TYPE_RGBA,
            cColorBits = 32,
            cAlphaBits = 8,
            cDepthBits = 0,
            cStencilBits = 0,
            iLayerType = Win32.PFD_MAIN_PLANE,
        };

        int format = Win32.ChoosePixelFormat(hdc, ref pfd);
        if (format == 0) { hdcOut = IntPtr.Zero; return IntPtr.Zero; }
        if (!Win32.SetPixelFormat(hdc, format, ref pfd)) { hdcOut = IntPtr.Zero; return IntPtr.Zero; }

        IntPtr glrc = Gl.wglCreateContext(hdc);
        if (glrc == IntPtr.Zero) { hdcOut = IntPtr.Zero; return IntPtr.Zero; }
        if (!Gl.wglMakeCurrent(hdc, glrc)) { hdcOut = IntPtr.Zero; return IntPtr.Zero; }

        // Try to upgrade to GL 3.3 core via wglCreateContextAttribsARB.
        // If unavailable (very old driver) the fallback 1.x context is used —
        // that may or may not have FBO support. Fail gracefully.
        var createAttribs = Gl.TryResolve<Gl.wglCreateContextAttribsARBD>("wglCreateContextAttribsARB");
        if (createAttribs != null)
        {
            int* attribs = stackalloc int[]
            {
                Gl.WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                Gl.WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                Gl.WGL_CONTEXT_PROFILE_MASK_ARB, Gl.WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
                0,
            };

            IntPtr coreCtx = createAttribs(hdc, IntPtr.Zero, attribs);
            if (coreCtx != IntPtr.Zero)
            {
                Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                Gl.wglDeleteContext(glrc);
                glrc = coreCtx;
                Gl.wglMakeCurrent(hdc, glrc);
                Console.WriteLine("[spike] Upgraded to GL 3.3 core context.");
            }
            else
            {
                Console.WriteLine("[spike] wglCreateContextAttribsARB returned null — staying on legacy context.");
            }
        }
        else
        {
            Console.WriteLine("[spike] wglCreateContextAttribsARB not available — staying on legacy context.");
        }

        hdcOut = hdc;
        return glrc;
    }

    // ─── Render loop ──────────────────────────────────────────────────────
    private static void RunRenderLoop(D3D9Host d3d, Gl.Bindings gl, InteropTexture tex)
    {
        var sw = Stopwatch.StartNew();
        long frameStartTicks;
        long lockTotalTicks = 0;
        long unlockTotalTicks = 0;
        long unlockMaxTicks = 0;
        int frame = 0;
        long secondAnchor = sw.ElapsedTicks;
        int framesThisSecond = 0;

        while (!_quitRequested)
        {
            // Drain Win32 messages.
            while (Win32.PeekMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT)
                {
                    _quitRequested = true;
                    break;
                }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            if (_quitRequested) break;

            frameStartTicks = sw.ElapsedTicks;

            // ── GL phase: lock → render to FBO → unlock ──
            long lockStart = sw.ElapsedTicks;
            if (!tex.Lock())
            {
                Console.Error.WriteLine($"[spike] wglDXLockObjectsNV failed at frame {frame}.");
                break;
            }
            long lockEnd = sw.ElapsedTicks;
            lockTotalTicks += (lockEnd - lockStart);

            // Bind the interop FBO and clear to an animated color so it's visually
            // obvious the GL path is writing into the D3D9 texture. Swizzle is
            // intentional: we want to see whether glClearColor's RGBA is sampled
            // as RGBA or as BGRA when drawn through D3D9's quad shader path.
            // Spike question A2 — color format mapping.
            float t = (frame % 600) / 600.0f;
            float r = (float)(0.5 + 0.5 * Math.Sin(t * 2 * Math.PI));
            float g = (float)(0.5 + 0.5 * Math.Sin(t * 2 * Math.PI + 2.0));
            float b = (float)(0.5 + 0.5 * Math.Sin(t * 2 * Math.PI + 4.0));

            gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, tex.GlFramebuffer);
            gl.glViewport(0, 0, tex.Width, tex.Height);
            gl.glClearColor(r, g, b, 1.0f);
            gl.glClear(Gl.GL_COLOR_BUFFER_BIT);
            gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);
            gl.glFlush();

            long unlockStart = sw.ElapsedTicks;
            if (!tex.Unlock())
            {
                Console.Error.WriteLine($"[spike] wglDXUnlockObjectsNV failed at frame {frame}.");
                break;
            }
            long unlockEnd = sw.ElapsedTicks;
            long unlockTicks = unlockEnd - unlockStart;
            unlockTotalTicks += unlockTicks;
            if (unlockTicks > unlockMaxTicks) unlockMaxTicks = unlockTicks;

            // ── D3D9 phase: render the texture as a fullscreen quad ──
            d3d.Clear(0xFF000000); // opaque black, so quad alpha is visible
            d3d.BeginScene();
            d3d.DrawTexturedQuad(tex.D3D9Texture, WindowWidth, WindowHeight);
            d3d.EndScene();
            d3d.Present();

            frame++;
            framesThisSecond++;

            // Per-second telemetry — feeds Spike question A3.
            long now = sw.ElapsedTicks;
            if ((now - secondAnchor) >= Stopwatch.Frequency)
            {
                double avgLockMs = (lockTotalTicks / (double)Stopwatch.Frequency) * 1000.0 / Math.Max(framesThisSecond, 1);
                double avgUnlockMs = (unlockTotalTicks / (double)Stopwatch.Frequency) * 1000.0 / Math.Max(framesThisSecond, 1);
                double maxUnlockMs = (unlockMaxTicks / (double)Stopwatch.Frequency) * 1000.0;
                Console.WriteLine($"[spike] frame={frame} fps={framesThisSecond} lock={avgLockMs:F3}ms avgUnlock={avgUnlockMs:F3}ms maxUnlock={maxUnlockMs:F3}ms");

                lockTotalTicks = 0;
                unlockTotalTicks = 0;
                unlockMaxTicks = 0;
                framesThisSecond = 0;
                secondAnchor = now;
            }
        }
    }
}
