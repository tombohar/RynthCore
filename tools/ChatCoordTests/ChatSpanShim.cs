// Stand-in for the engine's ChatSpan (UI/Data/ChatRouting.cs, which drags the whole rule engine in):
// ChatCoords.LinkSpans only reads Start, Length and Argb.
namespace RynthCore.Engine.UI.Data;

internal readonly record struct ChatSpan(int Start, int Length, uint Argb);
