using RynthCore.Engine.D3D9;

namespace AcGameDeviceTests;

/// <summary>Finding AC's own D3D9 device without creating one (AcGameDevicePolicy).</summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    // RenderDeviceD3D::SetFFAmbientColor32 on the 4,841,472-byte client (0x005A3EB0), first 40 bytes.
    private static readonly byte[] SetAmbient =
    [
        0x56, 0x8B, 0xF1, 0x8A, 0x86, 0x70, 0x04, 0x00, 0x00, 0x84, 0xC0, 0x57, 0x8B, 0x7C, 0x24, 0x0C,
        0x75, 0x08, 0x39, 0xBE, 0xA0, 0x04, 0x00, 0x00, 0x74, 0x1B, 0x8B, 0x86, 0x68, 0x04, 0x00, 0x00,
        0x8B, 0x08, 0x57, 0x68, 0x8B, 0x00, 0x00, 0x00,
    ];

    // PrimD3DRender::UpdateLightsInternal at 0x0059C15E .. the call at 0x0059C175 (E8 36 7D 00 00 -> 0x005A3EB0).
    private static readonly byte[] CallSite =
    [
        0x8B, 0x0D, 0x40, 0x03, 0x87, 0x00, 0xC1, 0xE0, 0x08, 0x0B, 0xC3, 0xC1, 0xE0, 0x08, 0x0B, 0xC7,
        0xC1, 0xE0, 0x08, 0x55, 0x0B, 0xC6, 0x50, 0xE8, 0x36, 0x7D, 0x00, 0x00,
    ];

    private static int Main()
    {
        OffsetFromTheClient();
        OffsetAnywhereInABuffer();
        OffsetRefusesOtherCode();
        OffsetRefusesImplausible();
        CallSiteLands();
        JudgeDevices();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} assertions)" : $"FAIL ({_failed} of {_checks} assertions)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name) { _case = name; Console.WriteLine("ok   " + name); }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL {_case}: {what}");
    }

    private static void Eq(int got, int want, string what) => Check(got == want, $"{what}: got 0x{got:X}, want 0x{want:X}");

    private static void OffsetFromTheClient()
    {
        Case("the device offset is read from the client's SetFFAmbientColor32");
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(SetAmbient, 0), 0x468, "offset");
        Eq(AcGameDevicePolicy.DeviceOffsetFallback, 0x468, "fallback matches the client");
    }

    private static void OffsetAnywhereInABuffer()
    {
        Case("the offset is read where the match is, not at 0");
        byte[] buf = new byte[100];
        SetAmbient.CopyTo(buf, 37);
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(buf, 37), 0x468, "at 37");
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(buf, 36), -1, "one byte off");
        byte[] moved = (byte[])SetAmbient.Clone();
        moved[28] = 0x70; moved[29] = 0x05;
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(moved, 0), 0x570, "another build's offset");
    }

    private static void OffsetRefusesOtherCode()
    {
        Case("code that isn't the device read gives no offset");
        foreach (int i in new[] { 26, 27, 32, 33, 34, 35, 36 })
        {
            byte[] bad = (byte[])SetAmbient.Clone();
            bad[i] ^= 0xFF;
            Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(bad, 0), -1, $"byte {i} changed");
        }
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(SetAmbient.AsSpan(0, 36), 0), -1, "cut short");
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(SetAmbient, -1), -1, "negative start");
        Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(ReadOnlySpan<byte>.Empty, 0), -1, "empty");
    }

    private static void OffsetRefusesImplausible()
    {
        Case("an implausible offset is refused");
        foreach ((byte lo, byte hi, byte b2) in new[] { ((byte)0x69, (byte)0x04, (byte)0), ((byte)0x00, (byte)0x00, (byte)0), ((byte)0x04, (byte)0x00, (byte)0), ((byte)0x00, (byte)0x80, (byte)0), ((byte)0x68, (byte)0x04, (byte)0x01) })
        {
            byte[] bad = (byte[])SetAmbient.Clone();
            bad[28] = lo; bad[29] = hi; bad[30] = b2;
            Eq(AcGameDevicePolicy.DeviceOffsetFromSetAmbient(bad, 0), -1, $"offset bytes {lo:X2} {hi:X2} {b2:X2}");
        }
        Check(AcGameDevicePolicy.IsPlausibleOffset(0x468), "0x468 plausible");
        Check(!AcGameDevicePolicy.IsPlausibleOffset(-4), "negative not plausible");
    }

    private static void CallSiteLands()
    {
        Case("the global's call site must call SetFFAmbientColor32");
        const int siteVa = 0x0059C15E, callAt = 23;
        Check(AcGameDevicePolicy.CallLandsOn(CallSite, callAt, siteVa + callAt, 0x005A3EB0), "lands on 0x005A3EB0");
        Check(!AcGameDevicePolicy.CallLandsOn(CallSite, callAt, siteVa + callAt, 0x005A3EC0), "not another function");
        Check(!AcGameDevicePolicy.CallLandsOn(CallSite, callAt, siteVa + callAt + 1, 0x005A3EB0), "VA matters");
        Check(!AcGameDevicePolicy.CallLandsOn(CallSite, callAt - 1, siteVa + callAt - 1, 0x005A3EB0), "not an E8");
        Check(!AcGameDevicePolicy.CallLandsOn(CallSite.AsSpan(0, 26), callAt, siteVa + callAt, 0x005A3EB0), "cut short");
        Check(!AcGameDevicePolicy.CallLandsOn(CallSite, -1, siteVa - 1, 0x005A3EB0), "negative");
    }

    private static void JudgeDevices()
    {
        Case("only a device whose EndScene, Reset and Present are d3d9's is used");
        const long d3d9 = 0x61E00000, other = 0x10000000;
        Check(AcGameDevicePolicy.JudgeDevice(d3d9, d3d9, d3d9, d3d9) == null, "all in d3d9 = good");
        Check(AcGameDevicePolicy.JudgeDevice(0, 0, 0, 0) != null, "no d3d9 loaded");
        Check(AcGameDevicePolicy.JudgeDevice(d3d9, other, d3d9, d3d9) != null, "EndScene elsewhere (a wrapper's vtable)");
        Check(AcGameDevicePolicy.JudgeDevice(d3d9, 0, d3d9, d3d9) != null, "EndScene in no module");
        Check(AcGameDevicePolicy.JudgeDevice(d3d9, d3d9, other, d3d9) != null, "Reset elsewhere");
        Check(AcGameDevicePolicy.JudgeDevice(d3d9, d3d9, d3d9, 0) != null, "Present in no module");
        Check(AcGameDevicePolicy.JudgeDevice(d3d9, other, other, other)?.Contains("EndScene") == true, "the first bad slot is named");
    }
}
