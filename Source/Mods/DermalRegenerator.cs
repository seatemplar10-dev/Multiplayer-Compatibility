using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Dermal Regenerator by rselbo</summary>
    [MpCompatFor("rselbo.dermalregenerator.1.0.0")]
    class DermalRegenerator
    {
        private static Type compType;
        private static System.Reflection.FieldInfo autoHealField;

        public DermalRegenerator(ModContentPack mod)
        {
            if (!LabOff.Row(498)) // row 498: camera-gated Rand (GenView.ShouldSpawnMotesAt) differs per PC
            {
                PatchingUtilities.PatchPushPopRand("DermalRegenerator.Building_DermalRegeneratorNew:ThrowParticles");
                Log.Message("[MPCompat - Dermal Regenerator] row 498: 1 camera-gated Rand methods push/pop");
            }

            RegisterAutoHealRow511();
        }

        /// <summary>FIXES row 511: the 'AutomaticHeal' Command_Toggle in <c>DermalRegeneratorComp.CompGetGizmosExtra</c> flips
        /// <c>Props.AutomaticHeal</c> (a field of the shared CompProperties) on the clicking PC only, and the heal work giver reads it.
        /// The toggle lambda is <c>b__4_1</c> (ordinal 1; 0 = isActive), an instance method of the comp, synced as it is.
        /// Step 2: the value is not saved, so a postfix on <c>Building_DermalRegeneratorNew.ExposeData</c> scribes it (only written when true),
        /// which gives a joining PC the host's value.</summary>
        private static void RegisterAutoHealRow511()
        {
            if (LabOff.Row(511))
                return;

            try
            {
                compType = AccessTools.TypeByName("DermalRegenerator.DermalRegeneratorComp");
                autoHealField = AccessTools.Field("DermalRegenerator.CompProperties_DermalRegenerator:AutomaticHeal");
                MpCompat.RegisterLambdaMethod(compType, "CompGetGizmosExtra", 1);
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod("DermalRegenerator.Building_DermalRegeneratorNew:ExposeData"),
                    postfix: new HarmonyMethod(typeof(DermalRegenerator), nameof(PostExposeData)));
            }
            catch (Exception e)
            {
                Log.Error($"[MPCompat - Dermal Regenerator] FIXES row 511: auto heal toggle not synced: {e}");
                return;
            }

            Log.Message("[MPCompat - Dermal Regenerator] row 511: Dermal Regenerator auto heal toggle synced");
        }

        private static void PostExposeData(ThingWithComps __instance)
        {
            if (autoHealField == null) return;
            foreach (var comp in __instance.AllComps)
            {
                if (comp.GetType() != compType) continue;
                bool v = (bool)autoHealField.GetValue(comp.props);
                Scribe_Values.Look(ref v, "autoHealRow511", false);
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                    autoHealField.SetValue(comp.props, v);
                return;
            }
        }
    }
}
