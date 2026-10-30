// ============================================================================
//  RynthCore.Engine - D3D9/AmbientLightPolicy.cs
//  The pure part of the ambient light floor (D3D9/AmbientLight.cs, plugin API
//  v91, 2026-10-27): the colour AC's world ambient becomes when a plugin has
//  asked for at least some light. Compiled into tools\AmbientLightTests.
//
//  Colours are D3DCOLOR (0xAARRGGBB), the form AC hands to
//  RenderDeviceD3D::SetFFAmbientColor32. The floor's alpha byte means nothing
//  (D3D ignores D3DRS_AMBIENT's alpha) and AC's own alpha byte is kept, so a
//  floor never changes anything but the three colour channels. A floor whose
//  colour channels are all 0 is "off": AC's colour passes unchanged.
// ============================================================================

namespace RynthCore.Engine.D3D9;

internal static class AmbientLightPolicy
{
    /// <summary>The colour bits of a floor; 0 = no floor.</summary>
    public static uint FloorRgb(uint floorArgb) => floorArgb & 0x00FFFFFFu;

    /// <summary>
    /// AC's ambient <paramref name="client"/> with <paramref name="floorArgb"/> under it: per channel
    /// the brighter of the two, AC's alpha byte kept. Never darker than AC's own (outdoors at noon a
    /// dim floor changes nothing); floor 0 returns <paramref name="client"/> as it is.
    /// </summary>
    public static uint Apply(uint client, uint floorArgb)
    {
        uint floor = FloorRgb(floorArgb);
        if (floor == 0) return client;
        uint r = Max((client >> 16) & 0xFF, (floor >> 16) & 0xFF);
        uint g = Max((client >> 8) & 0xFF, (floor >> 8) & 0xFF);
        uint b = Max(client & 0xFF, floor & 0xFF);
        return (client & 0xFF000000u) | (r << 16) | (g << 8) | b;
    }

    private static uint Max(uint a, uint b) => a > b ? a : b;
}
