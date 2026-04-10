using System;
using System.Reflection;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace NexSuite.Plugins.RynthAi
{
    public static class DecalInspector
    {
        public static void DumpType(Type t, CoreManager core)
        {
            core.Actions.AddChatText("====== " + t.FullName + " ======", 3);

            core.Actions.AddChatText("-- PROPERTIES --", 3);
            foreach (var p in t.GetProperties())
            {
                core.Actions.AddChatText(p.PropertyType.Name + " " + p.Name, 3);
            }

            core.Actions.AddChatText("-- METHODS --", 3);
            foreach (var m in t.GetMethods())
            {
                core.Actions.AddChatText(m.ReturnType.Name + " " + m.Name, 3);
            }
        }

        public static void DumpDecalAPI(CoreManager core)
        {
            DumpType(typeof(WorldObject), core);
            DumpType(typeof(CharacterFilter), core);
            DumpType(typeof(CoreManager), core);

            // These are the important ones for navigation
            DumpType(typeof(HooksWrapper), core);
            DumpType(typeof(WorldFilter), core);
        }
    }
}
