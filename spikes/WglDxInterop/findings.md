# Phase A spike findings — WGL_NV_DX_interop2 for D3D9 sources

**Verdict: NOT VIABLE on this hardware.**

The technique recommended in `wgl-interop-plan.md` does not work for D3D9 sources on the user's machine. This invalidates the plan as written. **Stop and pivot before any engine code changes.**

---

## What was tested

Hardware: NVIDIA GeForce GTX 1650 SUPER, driver 560.94 (current).

OS: Windows 10 Pro 19045.

GL: 3.3.0 NVIDIA 560.94, real ICD context (not Microsoft GDI fallback). 809 chars of WGL extension string, both `WGL_NV_DX_interop` and `WGL_NV_DX_interop2` advertised as supported.

Spike code: `C:\Projects\RynthCore\spikes\WglDxInterop\` — ~600 LOC, builds clean as managed x86.

## Results

`wglDXOpenDeviceNV(deviceHandle)`: **succeeds** for both plain D3D9 and D3D9Ex. Returns a non-null handle. Already informative — the D3D device is recognized as a valid interop source.

`wglDXSetResourceShareHandleNV(surface, sharedHandle)`: **succeeds** for D3D9Ex render-target textures created with a non-null `pSharedHandle` slot.

`wglDXRegisterObjectNV(...)`: **fails (returns NULL)** in 10 distinct configurations:

| Device | Source | Type | Access | Result |
|---|---|---|---|---|
| Plain D3D9 | IDirect3DSurface9 | GL_TEXTURE_2D | WRITE_DISCARD_NV | NULL (LastError 0xC007006E first call, 0x0 subsequent) |
| Plain D3D9 | IDirect3DSurface9 | GL_TEXTURE_2D | READ_WRITE_NV | NULL |
| Plain D3D9 | IDirect3DSurface9 | GL_TEXTURE_RECTANGLE | READ_WRITE_NV | NULL |
| Plain D3D9 | IDirect3DTexture9 | GL_TEXTURE_2D | WRITE_DISCARD_NV | NULL |
| Plain D3D9 | IDirect3DTexture9 | GL_TEXTURE_2D | READ_WRITE_NV | NULL |
| D3D9Ex (shared handle set) | IDirect3DSurface9 | GL_TEXTURE_2D | WRITE_DISCARD_NV | NULL |
| D3D9Ex (shared handle set) | IDirect3DSurface9 | GL_TEXTURE_2D | READ_WRITE_NV | NULL |
| D3D9Ex (shared handle set) | IDirect3DSurface9 | GL_TEXTURE_RECTANGLE | READ_WRITE_NV | NULL |
| D3D9Ex (shared handle set) | IDirect3DTexture9 | GL_TEXTURE_2D | WRITE_DISCARD_NV | NULL |
| D3D9Ex (shared handle set) | IDirect3DTexture9 | GL_TEXTURE_2D | READ_WRITE_NV | NULL |

Other variables ruled out:
- `D3DCREATE_MULTITHREADED` flag: tested both on and off. Same failure either way.
- `D3DCREATE_HARDWARE_VERTEXPROCESSING` vs `_SOFTWARE_VERTEXPROCESSING`: tested both. Same failure.
- Shared focus window vs isolated hidden window for the GL context: tested both. Same failure.
- Texture format / dimensions: A8R8G8B8, 1280×720, D3DPOOL_DEFAULT, D3DUSAGE_RENDERTARGET — the canonical "supported" combination per spec.

The `0xC007006E = HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE)` on the very first call (subsequent calls leave LastError as 0, suggesting the driver doesn't reliably call `SetLastError`) is the only diagnostic NVIDIA's driver gives us. There is no way to query a more specific error.

## Interpretation

The published NV_DX_interop spec lists D3D9 surfaces as supported, but **NVIDIA's current driver implementation does not work for D3D9 sources** in the configurations a real-world overlay would need. This matches scattered third-party reports (Chromium's interop code carries D3D11 fallbacks and explicit notes about D3D9 quirkiness; various Steam/MSI Afterburner reverse-engineerings show they use D3D11 internally and composite differently). **WGL_NV_DX_interop2 is essentially a D3D11-and-newer interop in practice, even though the spec language is broader.**

Key implication: **the recommendation in `wgl-interop-plan.md` was wrong.** I led the user there based on the spec text and well-known overlay-in-D3D9-game examples — but those examples almost universally use D3D11 internally, not D3D9. The "render into AC's D3D9 texture via WGL interop" path is closed on this driver, and likely on every modern NVIDIA driver.

## What's left

The original GPU-rendering option space, re-evaluated against this finding:

1. **D3D11 producer via WGL_NV_DX_interop2 → AC's plain D3D9 consumer.** Not tested in the spike yet, but the *interop* part is the well-trodden one and almost certainly works. The hard part was always "→ AC's plain D3D9 consumer", which still requires either:
   - **(a) Hook `Direct3DCreate9` in acclient.exe at runtime to substitute `Direct3DCreate9Ex`.** Makes AC's device Ex. Ex devices CAN open shared HANDLEs from D3D11 (via DXGI sharing). MANAGED-pool wrapper required for AC's textures, lost-device semantics need shimming.
   - **(b) CPU readback of the D3D11 texture, then D3D9 LockRect+memcpy.** Same as today's software path with extra GPU latency. Nets nothing.

2. **Layered/child window approach.** No AC modification. Avalonia renders to its own ANGLE+D3D11 swap chain on a separate Win32 window, DWM composites it over AC. The mover bug (Phase 2a diagnostic — ~50 LOC fix) needs landing first. Then we'd extend layered windows to cover the bar + docked panels too, retiring the EndScene composite path.

3. **Fix software Skia.** Pursue the LFH AV at its actual source (Skia teardown allocations) rather than trying to GPU-accelerate a path that's blocked at the consumer side.

Path 1(a) is the same "hook Direct3DCreate9 → Ex" we discussed before the spike — runtime hook, no acclient.exe disk patch, no other-user impact. The spike's value here is that it confirms WGL interop is fine for D3D11 (assumed; would need a 30-minute test to verify), and the only structural blocker remaining is making AC's device Ex.

Path 2 has the largest scope but the smallest risk to AC stability. Coexistence-mode-friendly for free.

Path 3 is the smallest and probably the most-immediate-value option (the LFH AV is the actual blocker for daily use, not the lack of GPU rendering).

## Recommendation

Stop pursuing the WGL_NV_DX_interop2 → AC's D3D9 plan. Present the three remaining paths and let the user pick. None of them are a small follow-up on the current investigation — each is a meaningful pivot.

The spike was an unambiguous win: ~3 hours invested, conclusive data, prevented a multi-weekend integration on a foundation that wouldn't have worked.
