// The engine file ProgressionData.cs has these two dependencies; stand-ins keep the test free of the engine.
namespace RynthCore.Engine.Plugins
{
    internal static class EnlTestAnchor { }   // ProgressionData.cs has "using RynthCore.Engine.Plugins;"
}

namespace RynthCore.Engine.UI.Data
{
    internal static class PluginExportBinder
    {
        public static IntPtr Resolve(string pluginNamePart, string exportName) => IntPtr.Zero;
    }
}
