// ============================================================================
//  RynthCore.Engine - ImGui/BarMenuHover.cs
//  The timing of the bar icon dropdown (a plugin window's "barMenu", plugin API
//  v95): pure, so tools/SectionsTests compiles it as is. ImGuiBar feeds it once
//  a frame.
//
//  An icon must be hovered OpenDelay before its dropdown opens (a sweep of the
//  mouse across the bar opens nothing). Once open, the dropdown stays while the
//  mouse is over the icon or the dropdown, and for CloseDelay after it leaves
//  both (the gap between them is crossed within that). Hovering another icon
//  while one is open switches at once. After a click (Close) the same icon does
//  not reopen until the mouse has left it and hovered it again.
// ============================================================================

namespace RynthCore.Engine.ImGuiBackend;

internal sealed class BarMenuHover
{
    public const double OpenDelay = 0.15, CloseDelay = 0.30;
    /// <summary>Frames further apart than this (or a clock that runs backwards) reset the state.</summary>
    public const double ResetGap = 0.5;

    private string? _hovered;        // the icon being hovered, and since when (_hoverStart)
    private double _hoverStart;
    private double? _leftAt;         // when the mouse left both the icon and the dropdown (open only)
    private string? _closedByClick;  // the icon hovered when Close() ran: ignored until it is left
    private double _lastNow = double.NegativeInfinity;

    /// <summary>The bar button id whose dropdown is open, else null.</summary>
    public string? OpenFor { get; private set; }

    /// <summary>
    /// Once per frame. <paramref name="hoveredButton"/>: id of the menu-bearing bar button under the mouse, else null.
    /// <paramref name="overMenu"/>: the mouse is over the open dropdown. <paramref name="now"/>: seconds.
    /// </summary>
    public void Update(string? hoveredButton, bool overMenu, double now)
    {
        // The clock is one ImGui context's (the popped-out bar has its own, starting near 0), and a bar that
        // wasn't drawn for a while sends no frames: either way, start from a closed dropdown.
        if (now < _lastNow || now - _lastNow > ResetGap)
        {
            OpenFor = null;
            _hovered = null;
            _hoverStart = now;
            _leftAt = null;
            _closedByClick = null;
        }
        _lastNow = now;

        if (_closedByClick != null && hoveredButton != _closedByClick) _closedByClick = null;
        string? hovered = hoveredButton != null && hoveredButton == _closedByClick ? null : hoveredButton;

        if (hovered != _hovered) { _hovered = hovered; _hoverStart = now; }

        if (OpenFor != null)
        {
            if (hovered != null) { OpenFor = hovered; _leftAt = null; }
            else if (overMenu) _leftAt = null;
            else
            {
                _leftAt ??= now;
                if (now - _leftAt.Value >= CloseDelay) { OpenFor = null; _leftAt = null; }
            }
        }
        else if (hovered != null && now - _hoverStart >= OpenDelay)
        {
            OpenFor = hovered;
            _leftAt = null;
        }
    }

    /// <summary>An item was clicked, or the bar went away.</summary>
    public void Close()
    {
        OpenFor = null;
        _leftAt = null;
        _closedByClick = _hovered;
    }
}
