using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 cu27 (FIXES 571). Show Me Your Hands: HandDrawer.shouldSkipPawnPostDraw reads
    /// ShowMeYourHandsMod.instance.Settings.ShowOnRace, which is null when the settings file has no ShowOnRace node
    /// (Scribe_Collections.Look nulls it). SMYH only fills it in a MainMenu OnGUI postfix (UpdateValidRaces), which never runs
    /// when the game boots straight into a map, so every pawn draw threw a NullReferenceException (invisible pawns).
    /// Fix: before the check, if ShowOnRace is null, create it and fill it as UpdateValidRaces does (humanlike races on),
    /// without writing the settings file. Draw-side only: mod settings dictionary, no sim state, no Rand.</summary>
    [MpCompatFor("mlie.showmeyourhands")]
    class FullCust571
    {
        public const string Tag = "[MPCompat - FULL1127 cu27] ";
        const string Target = "ShowMeYourHands.HandDrawer:shouldSkipPawnPostDraw";

        static System.Reflection.FieldInfo instanceF, settingsF, showOnRaceF;

        public FullCust571(ModContentPack mod)
        {
            var modType = AccessTools.TypeByName("ShowMeYourHands.ShowMeYourHandsMod");
            var setType = AccessTools.TypeByName("ShowMeYourHands.ShowMeYourHandsModSettings");
            instanceF = modType == null ? null : AccessTools.Field(modType, "instance");
            settingsF = modType == null ? null : AccessTools.Field(modType, "Settings");
            showOnRaceF = setType == null ? null : AccessTools.Field(setType, "ShowOnRace");
            var m = AccessTools.Method(Target);
            if (m == null || instanceF == null || settingsF == null || showOnRaceF == null)
            {
                Log.Error(Tag + "Show Me Your Hands: member not found (method " + (m != null) + " instance " + (instanceF != null)
                    + " Settings " + (settingsF != null) + " ShowOnRace " + (showOnRaceF != null) + ")");
                return;
            }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullCust571), nameof(EnsureShowOnRace)));
            Log.Message(Tag + "Show Me Your Hands: ShowOnRace null guard registered");
        }

        public static void EnsureShowOnRace()
        {
            try
            {
                var inst = instanceF.GetValue(null);
                if (inst == null) return;
                var settings = settingsF.GetValue(inst);
                if (settings == null || showOnRaceF.GetValue(settings) != null) return;
                var dict = new Dictionary<string, bool>();
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (def.race != null && def.defName != null)
                        dict[def.defName] = def.race.Humanlike;
                showOnRaceF.SetValue(settings, dict);
                Log.Message(Tag + "ShowOnRace was null, filled " + dict.Count + " races");
            }
            catch (Exception e)
            {
                Log.Error(Tag + "EnsureShowOnRace: " + e);
            }
        }
    }
}
