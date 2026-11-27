using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using System;
using System.Collections;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Chester Choose by Ferny. The card deck is a per-client view (UI Rand, PushState isolated); the player's pick is the synced thing.
    /// Design choice: the pick is replayed on every PC as (IncidentDef, points, target map) with default parms, so all PCs fire the same incident.</summary>
    [MpCompatFor("ferny.ChesterChoose")]
    internal class ChesterChooseL1
    {
        static Type stateType;
        static FieldInfo executing;

        public ChesterChooseL1(ModContentPack mod)
        {
            stateType = AccessTools.TypeByName("ChesterChoose.State");
            var dialog = AccessTools.TypeByName("ChesterChoose.Dialog_ChesterCards");
            executing = AccessTools.Field(stateType, "executingEvents");
            if (executing == null) throw new Exception("ChesterChoose: State.executingEvents not found");

            // deck generation + dialog header use Rand (and mutate then restore storyteller state) from UI code
            PatchingUtilities.PatchPushPopRand(new MethodBase[]
            {
                AccessTools.Method(stateType, "GenerateRedrawCards"),
                AccessTools.Method(stateType, "GenerateBlindCards"),
                AccessTools.Constructor(dialog, new[] { typeof(System.Collections.Generic.List<FiringIncident>), typeof(bool), typeof(int), typeof(int), AccessTools.TypeByName("ChesterChoose.CardPool") }),
            });

            MP.RegisterSyncMethod(typeof(ChesterChooseL1), nameof(SyncedFire));
            MpCompat.harmony.Patch(AccessTools.Method(typeof(Storyteller), nameof(Storyteller.TryFire)),
                prefix: new HarmonyMethod(typeof(ChesterChooseL1), nameof(PreTryFire)) { priority = Priority.First });
            Log.Message("[MPCompat - FULL1127 L1] ChesterChoose: card pick fire + deck Rand isolation registered");
        }

        static bool PreTryFire(FiringIncident fi, ref bool __result)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var set = (IEnumerable)executing.GetValue(null);
            bool fromCard = false;
            foreach (var x in set) if (ReferenceEquals(x, fi)) { fromCard = true; break; }
            if (!fromCard) return true;
            executing.FieldType.GetMethod("Remove").Invoke(set, new object[] { fi });
            SyncedFire(fi.def, fi.parms.points, fi.parms.target as Map);
            __result = true;
            return false;
        }

        static void SyncedFire(IncidentDef def, float points, Map map)
        {
            if (def == null) return;
            IIncidentTarget target = (IIncidentTarget)map ?? Find.World;
            var parms = StorytellerUtility.DefaultParmsNow(def.category, target);
            parms.points = points;
            var fi = new FiringIncident(def, null, parms);
            var set = executing.GetValue(null);
            executing.FieldType.GetMethod("Add").Invoke(set, new object[] { fi });
            Find.Storyteller.TryFire(fi);
        }
    }
}
