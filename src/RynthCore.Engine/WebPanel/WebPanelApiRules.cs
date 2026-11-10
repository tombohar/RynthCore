using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.WebPanel;

/// <summary>What WebPanelGetState reports for one handle.</summary>
internal readonly record struct WebPanelSnapshot(int Handle, int Status, uint Flags, int Error, int LastHttpStatus, uint NavigationCount);

/// <summary>
/// The pure decisions of the v94 ABI glue (WebPanelApi.cs), kept apart so tools\WebPanelTests can check them
/// without the engine: refusals, the GetState argument check, and writing the result struct.
/// </summary>
internal static unsafe class WebPanelApiRules
{
    /// <summary>0 to go on, else the refusal. Shutting down wins over the engine.json "WebPanel" kill switch.</summary>
    public static int OpenRefusal(bool shuttingDown, bool enabled) =>
        shuttingDown ? (int)WebPanelCode.ShuttingDown : enabled ? 0 : (int)WebPanelCode.Unavailable;

    /// <summary>Navigate, Close and GetState: 0 to go on, else ShuttingDown.</summary>
    public static int CallRefusal(bool shuttingDown) => shuttingDown ? (int)WebPanelCode.ShuttingDown : 0;

    /// <summary>OffsetOf(Reserved): the smallest Size a caller's WebPanelStateNative may declare (28 on x86 and x64: the struct has no pointer fields).</summary>
    public static uint StateMinSize
    {
        get
        {
            WebPanelStateNative s = default;
            return (uint)((byte*)s.Reserved - (byte*)&s);
        }
    }

    public static bool StateArgsOk(WebPanelStateNative* state) => state != null && state->Size >= StateMinSize;

    /// <summary>What a handle reads when no panel was ever opened.</summary>
    public static WebPanelSnapshot NoPanel(int handle) => new(handle, (int)WebPanelStatusCode.Superseded, 0, 0, 0, 0);

    /// <summary>Writes the fields before Reserved only (the caller's Size covers at least those).</summary>
    public static void WriteState(WebPanelStateNative* state, in WebPanelSnapshot s)
    {
        state->Handle = s.Handle;
        state->Status = s.Status;
        state->Flags = s.Flags;
        state->Error = s.Error;
        state->LastHttpStatus = s.LastHttpStatus;
        state->NavigationCount = s.NavigationCount;
    }
}
