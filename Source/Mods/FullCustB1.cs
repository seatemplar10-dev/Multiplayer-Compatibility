using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Altered Carbon: needlecast gizmos (start/end) rewrite the target pawn via NeuralData.OverwritePawn.</summary>
    [MpCompatFor("hlx.ultratechalteredcarbon")]
    internal class FullCustB1AlteredCarbon
    {
        public FullCustB1AlteredCarbon(ModContentPack mod)
        {
            var neural = AccessTools.TypeByName("AlteredCarbon.Hediff_NeuralStack");
            var remote = AccessTools.TypeByName("AlteredCarbon.Hediff_RemoteStack");
            MP.RegisterSyncMethod(neural, "NeedlecastTo");
            MP.RegisterSyncMethod(remote, "EndNeedlecasting");
            Log.Message("[MPCompat - FULL1127 B1] hlx.ultratechalteredcarbon: NeedlecastTo, EndNeedlecasting registered");
        }
    }

    /// <summary>Medieval Overhaul: fuel and stew pot ITabs use cloned thing filter UIs, so MP never sets the filter context.</summary>
    [MpCompatFor("dankpyon.medieval.overhaul")]
    internal class FullCustB1MedievalOverhaul
    {
        public FullCustB1MedievalOverhaul(ModContentPack mod)
        {
            foreach (var name in new[] { "MedievalOverhaul.ITab_Fuel", "MedievalOverhaul.ITab_StewPot" })
            {
                var type = AccessTools.TypeByName(name);
                MpCompat.harmony.Patch(AccessTools.Method(type, "FillTab"),
                    prefix: new HarmonyMethod(typeof(FullCustB1MedievalOverhaul), nameof(PreFillTab)),
                    postfix: new HarmonyMethod(typeof(FullCustB1MedievalOverhaul), nameof(PostFillTab)));
            }
            Log.Message("[MPCompat - FULL1127 B1] dankpyon.medieval.overhaul: ITab_Fuel/ITab_StewPot thing filter context registered");
        }

        private static void PreFillTab(ITab __instance)
        {
            if (__instance.SelThing is ThingWithComps thing)
                MP.SetThingFilterContext(new FuelFilterContext(thing));
        }

        private static void PostFillTab() => MP.SetThingFilterContext(null);

        private static ThingComp FuelComp(ThingWithComps thing)
        {
            var handler = AccessTools.TypeByName("MedievalOverhaul.ICompFuelHandler");
            var stat = AccessTools.TypeByName("MedievalOverhaul.CompRefuelableStat");
            return thing.AllComps.FirstOrDefault(c => handler != null && handler.IsInstanceOfType(c))
                ?? thing.AllComps.FirstOrDefault(c => stat != null && stat.IsInstanceOfType(c));
        }

        private record FuelFilterContext(ThingWithComps Thing) : ThingFilterContext
        {
            public override ThingFilter Filter
            {
                get
                {
                    var comp = FuelComp(Thing);
                    return comp == null ? null : AccessTools.Property(comp.GetType(), "AllowedFuelFilter")?.GetValue(comp) as ThingFilter;
                }
            }

            public override ThingFilter ParentFilter
            {
                get
                {
                    var comp = FuelComp(Thing);
                    var props = comp?.props;
                    return props == null ? null : AccessTools.Field(props.GetType(), "fuelFilter")?.GetValue(props) as ThingFilter;
                }
            }
        }
    }
}
