using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Prisoner Arena by Mlie. Bell tab / gizmo actions all write saved bell state from UI code.</summary>
    [MpCompatFor("Mlie.PrisonerArena")]
    internal class PrisonerArenaL1
    {
        static Type bellType, utilType, fighterType;
        static FieldInfo f1, f2, fighterP;
        static MethodInfo validPawns;

        public PrisonerArenaL1(ModContentPack mod)
        {
            bellType = AccessTools.TypeByName("ArenaBell.Building_Bell");
            utilType = AccessTools.TypeByName("ArenaBell.ITab_BellManagerUtility");
            fighterType = AccessTools.TypeByName("ArenaBell.Fighter");
            var comp = AccessTools.TypeByName("ArenaBell.CompBell");
            f1 = AccessTools.Field(bellType, "fighter1");
            f2 = AccessTools.Field(bellType, "fighter2");
            fighterP = AccessTools.Field(fighterType, "p");
            validPawns = AccessTools.Method(utilType, "allValidPawnsOnMap");
            if (validPawns == null || fighterP == null) throw new Exception("PrisonerArena: allValidPawnsOnMap or Fighter.p not found");

            // ITab buttons
            MP.RegisterSyncMethod(AccessTools.Method(bellType, "Brawl"));
            MP.RegisterSyncMethod(AccessTools.Method(bellType, "TryCancelBrawl"));
            MP.RegisterSyncMethod(AccessTools.Method(bellType, "EndBrawl"));
            // gizmos: radius / audience (private instance methods bound as Command_Action.action) and the two circle/square lambdas
            foreach (var n in new[] { "increaseRad", "decreaseRad", "increaseAudience", "decreaseAudience" })
                MP.RegisterSyncMethod(AccessTools.Method(comp, n));
            MpCompat.RegisterLambdaMethod(comp, "CompGetGizmosExtra", 0, 1);
            // rules menus (float menu local functions capturing `bell`)
            foreach (var (parent, fn) in new[] { ("OpenFightTypeMenu", "DeathAction"), ("OpenFightTypeMenu", "DownedAction"), ("OpenRewardTypeMenu", "FreedomAction"), ("OpenRewardTypeMenu", "GloryAction") })
            {
                var m = MpMethodUtil.GetLocalFunc(utilType, parent, localFunc: fn);
                MP.RegisterSyncDelegate(utilType, m.DeclaringType.Name, m.Name, new[] { "bell" });
            }
            // fighter pick menus: rebuild the menu with a synced setter
            MP.RegisterSyncMethod(typeof(PrisonerArenaL1), nameof(SyncedSetFighter));
            MpCompat.harmony.Patch(AccessTools.Method(utilType, "OpenActor1SelectMenu"), prefix: new HarmonyMethod(typeof(PrisonerArenaL1), nameof(PreActor1)));
            MpCompat.harmony.Patch(AccessTools.Method(utilType, "OpenActor2SelectMenu"), prefix: new HarmonyMethod(typeof(PrisonerArenaL1), nameof(PreActor2)));
            Log.Message("[MPCompat - FULL1127 L1] PrisonerArena: brawl buttons, radius/audience/circle gizmos, rules menus, fighter picks registered");
        }

        static bool PreActor1(Thing bell) => PreActor(bell, 1);
        static bool PreActor2(Thing bell) => PreActor(bell, 2);

        static bool PreActor(Thing bell, int which)
        {
            if (!MP.IsInMultiplayer) return true;
            var other = (Pawn)fighterP.GetValue((which == 1 ? f2 : f1).GetValue(bell));
            var all = (List<Pawn>)validPawns.Invoke(null, new object[] { bell });
            var list = all.Where(p => p != other).ToList();
            if (list.Count == 0)
            {
                Messages.Message("PA.NoFighters".Translate(), MessageTypeDefOf.RejectInput);
                return false;
            }
            var opts = new List<FloatMenuOption>();
            foreach (var p in list)
            {
                var pawn = p;
                string text = pawn.LabelShort;
                if (pawn.AnimalOrWildMan() || pawn.IsColonyMech) text += " (" + pawn.def.race.AnyPawnKind.label + ")";
                opts.Add(new FloatMenuOption(text, () => SyncedSetFighter(bell, which, pawn)));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
            return false;
        }

        static void SyncedSetFighter(Thing bell, int which, Pawn pawn)
        {
            if (bell == null) return;
            fighterP.SetValue((which == 1 ? f1 : f2).GetValue(bell), pawn);
        }
    }
}
