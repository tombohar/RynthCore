// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MiniMapFace.cs
//  The Mini Map: a small window with the same map as the World Map, following you and turned so the
//  way you face is up (both switchable from the right-click menu), without a toolbar. The wheel
//  zooms, a drag pans (Follow off), a double click comes back to you; right-click for Follow /
//  Rotate / zoom / Go here / Open the World Map. Opened from the RynthNav panel's Mini button.
//  Its zoom, Follow and Rotate are its own (WorldMapSettings.Mini); the layers are shared with the
//  World Map's Show menu.
// ============================================================================

using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class MiniMapFace : IImGuiPanel
{
    public const string Title = "Mini Map";

    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(290, 310), new Vector2(150, 150), EdgeToEdge: true, GripInBody: true, NoPopOut: true),
            () => new MiniMapFace());
    }

    private readonly WorldMapView _view = new(mini: true);
    private bool _charsSub;

    public void OnShown() => WorldMapFeeds.Subscribe();

    public void OnHidden()
    {
        WorldMapFeeds.Unsubscribe();
        WorldMapFeeds.ReleaseCharacters(ref _charsSub);
    }

    public void Draw()
    {
        WorldMapFeeds.SyncCharacters(ref _charsSub);
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        if (size.X < 40 || size.Y < 40) return;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        try
        {
            RynthNavStatus? st = UiSources.RynthNav.Current?.Value;
            MapPlayer pl = WorldMapView.GatherPlayer(st, UiSources.Radar.Current?.Value);
            CharacterRow[]? chars = WorldMapSettings.Characters ? UiSources.Characters.Current?.Value?.Rows : null;
            _view.Blocked = null;
            _view.Draw(origin + new Vector2(1, 1), origin + size - new Vector2(1, 1), pl, st, chars);

            string tip = _view.HoverText;
            if (tip.Length > 0)
            {
                var dl = ImGuiNET.ImGui.GetWindowDrawList();
                Vector2 ts = ImGuiNET.ImGui.CalcTextSize(tip);
                Vector2 a = origin + new Vector2(6, 6), b = a + ts + new Vector2(10, 6);
                if (b.X < origin.X + size.X - 4)
                {
                    dl.AddRectFilled(a, b, RynthTheme.Argb(0xD00A121A), 4);
                    dl.AddText(a + new Vector2(5, 3), FaceKit.Text, tip);
                }
            }
            WorldMapSettings.FlushIfDue();
        }
        finally { ImGuiNET.ImGui.PopFont(); }
    }
}
