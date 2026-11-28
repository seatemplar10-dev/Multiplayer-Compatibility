using System;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FULL1127 wave P2: Custom Base Framework. The editor (Window_Hauptfenster and the windows it opens:
    /// Window_Baukasten, Window_Fraktion, Window_Beutetabelle, Window_Listenwahl) writes defs and is opened
    /// from the in-game Architect tab "CBF base editor" (Designator_OpenEditor), so it is blocked in MP.
    /// As Above So Below: nothing to do, the mod scans at startup on every PC (ABColumnNetworksBoot).
    /// </summary>
    [MpCompatFor("evileyeguy.custombaseframework")]
    class FullP2CustomBaseFramework
    {
        public const string Tag = "[MPCompat - FULL1127 P2] ";
        public const string Target = "CustomBaseFramework.Window_Hauptfenster:Oeffnen";

        public FullP2CustomBaseFramework(ModContentPack mod)
        {
            var m = AccessTools.Method(Target);
            if (m == null)
            {
                Log.Error(Tag + "CustomBaseFramework: method " + Target + " not found");
                return;
            }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullP2CustomBaseFramework), nameof(BlockEditor)));
            Log.Message(Tag + "CustomBaseFramework: editor opener blocked in multiplayer registered");
        }

        public static bool BlockEditor()
        {
            if (!MP.IsInMultiplayer) return true;
            Messages.Message("The base editor is not available in multiplayer.", MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}
