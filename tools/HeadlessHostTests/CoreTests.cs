// ============================================================================
//  CoreTests - the host's core slots (version, player id, names, screen mode).
// ============================================================================

using System.Runtime.InteropServices;

namespace HeadlessHostTests;

internal static unsafe class CoreTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("core", Run, order: 0);

    private static void Run()
    {
        Fixture f = Fixture.Shared;
        using var _ = f.Context.Enter();
        T.Eq(f.Api->Version, 92u, "API version");
        T.Check(f.Api->ImGuiContext == IntPtr.Zero && f.Api->GameHwnd == IntPtr.Zero, "no ImGui, no window");
        T.Eq(((delegate* unmanaged[Cdecl]<uint>)f.Api->GetPlayerIdFn)(), 0x50000001u, "GetPlayerId while in the world");
        T.Eq(Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<IntPtr>)f.Api->GetAccountNameFn)()), Fixture.Account, "account name");
        T.Eq(Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<IntPtr>)f.Api->GetWorldNameFn)()), "RynthAce", "world name");
        int mode = 0;
        T.Check(((delegate* unmanaged[Cdecl]<int*, int>)f.Api->GetScreenModeFn)(&mode) == 1 && mode == 0x10000008, "screen mode: in world");
        T.Eq(((delegate* unmanaged[Cdecl]<uint>)f.Api->GetClientHookFlagsFn)(), 0x1FFFFu, "client hook flags");
    }
}
