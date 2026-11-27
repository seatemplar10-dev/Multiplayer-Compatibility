using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 CU3 shared helpers: push a player faction (MP internal PushFaction / PopFaction by reflection) around a body.</summary>
    internal static class FullCust3
    {
        public const string Log3 = "[MPCompat - FULL1127 CU] ";
        static MethodInfo pushM, popM;

        public static void Push(Map map, Faction f)
        {
            if (!MP.IsInMultiplayer || f == null) return;
            if (pushM == null)
            {
                var t = AccessTools.TypeByName("Multiplayer.Client.Factions.FactionExtensions");
                pushM = AccessTools.Method(t, "PushFaction", new[] { typeof(Map), typeof(Faction), typeof(bool) });
                popM = AccessTools.Method(t, "PopFaction", new[] { typeof(Map) });
            }
            pushM.Invoke(null, new object[] { map, f, true });
        }

        public static void Pop(Map map)
        {
            if (!MP.IsInMultiplayer) return;
            popM?.Invoke(null, new object[] { map });
        }

        /// <summary>The existing RightPlayerFaction.Resolve(pawnFaction) as a reflection-free call for the transpilers.</summary>
        public static readonly MethodInfo ResolveM = AccessTools.Method(typeof(RightPlayerFaction), nameof(RightPlayerFaction.Resolve));
        public static readonly MethodInfo OfPlayerGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        public static readonly MethodInfo ThingFactionGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Faction));
    }

    /// <summary>RimToll (forti.rimtoll), extra rows. ReleaseWorker's pair read is done by FullRimToll in FullQ12Helpers.cs.
    /// 1) ReleaseWorker second read (faction.HostileTo(OfPlayer)) becomes Resolve(worker pawn's faction).
    /// 2) ProcessPendingOpens (tick): HostileTo(OfPlayer) uses the lord's map owner; Open() runs with that faction pushed (letters, goodwill).
    /// 3) The UI answers (DemandToll, RefusePassage, AcceptRecruit [recruit.SetFaction(OfPlayer)], AccusePass, LetPass) were run on one PC only:
    ///    they are sent as one synced static call (string name, two ints) and run under the issuing player's faction.</summary>
    internal static class FullRimTollCu3
    {
        static Type stateType;
        static FieldInfo pendingOpensF;
        static MethodInfo openM, boothForM;
        static PropertyInfo instanceP;
        static FieldInfo workerPawnF;
        static readonly string[] ui = { "DemandToll", "RefusePassage", "AcceptRecruit", "AccusePass", "LetPass" };
        public static int SecondReadSwapped = -1;

        public static void Init()
        {
            stateType = AccessTools.TypeByName("RimToll.GameComponent_TollState");
            pendingOpensF = AccessTools.Field(stateType, "pendingOpens");
            openM = AccessTools.Method(stateType, "Open");
            boothForM = AccessTools.Method("RimToll.TollUtility:BoothFor");
            instanceP = AccessTools.Property(stateType, "Instance");
            workerPawnF = AccessTools.Field(AccessTools.TypeByName("RimToll.TollWorker"), "pawn");
            var h = MpCompat.harmony;

            h.Patch(AccessTools.Method(stateType, "ReleaseWorker"), transpiler: new HarmonyMethod(typeof(FullRimTollCu3), nameof(ReleaseTranspiler)));
            h.Patch(AccessTools.Method(stateType, "ProcessPendingOpens"), prefix: new HarmonyMethod(typeof(FullRimTollCu3), nameof(PendingOpensPrefix)) { priority = Priority.First });
            foreach (var n in ui)
                h.Patch(AccessTools.Method(stateType, n), prefix: new HarmonyMethod(typeof(FullRimTollCu3), nameof(UiPrefix)) { priority = Priority.First });
            MP.RegisterSyncMethod(typeof(FullRimTollCu3), nameof(SyncedUi));
            Log.Message(FullCust3.Log3 + "RimToll: ReleaseWorker second read, ProcessPendingOpens faction push and 5 UI answers registered");
        }

        // ReleaseWorker: the OfPlayer read that is not after Thing.get_Faction (the first pair is done by FullQ12) -> Resolve(w.pawn.Faction).
        static IEnumerable<CodeInstruction> ReleaseTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var list = instr.ToList();
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Calls(FullCust3.OfPlayerGetter) || (i > 0 && list[i - 1].Calls(FullCust3.ThingFactionGetter))) continue;
                var labels = list[i].labels;
                var load = new CodeInstruction(OpCodes.Ldarg_1) { labels = new List<Label>(labels) };
                labels.Clear();
                list[i] = new CodeInstruction(OpCodes.Call, FullCust3.ResolveM);
                list.InsertRange(i, new[]
                {
                    load,
                    new CodeInstruction(OpCodes.Ldfld, workerPawnF),
                    new CodeInstruction(OpCodes.Callvirt, FullCust3.ThingFactionGetter),
                });
                i += 3;
                n++;
            }
            SecondReadSwapped = n;
            return list;
        }

        static bool PendingOpensPrefix(object __instance)
        {
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return true;
            var pending = (IList)pendingOpensF.GetValue(__instance);
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var lord = (Lord)pending[i];
                pending.RemoveAt(i);
                if (lord == null || lord.Map == null || lord.ownedPawns.Count == 0 || lord.faction == null || lord.faction.IsPlayer) continue;
                var player = PlayerFactionForTick.ForMap(lord.Map);
                if (lord.faction.HostileTo(player) || lord.LordJob?.GetType().Name == "LordJob_TollVisit") continue;
                var booth = (Building)boothForM.Invoke(null, new object[] { lord });
                if (booth == null) continue;
                bool pushed = player != Faction.OfPlayer;
                if (pushed) FullCust3.Push(lord.Map, player);
                try { openM.Invoke(__instance, new object[] { lord, booth }); }
                finally { if (pushed) FullCust3.Pop(lord.Map); }
            }
            return false;
        }

        static bool UiPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            SyncedUi(__originalMethod.Name, (int)__args[0], __args.Length > 1 ? (int)__args[1] : 0);
            return false;
        }

        [SyncMethod]
        static void SyncedUi(string name, int id, int level)
        {
            var inst = instanceP.GetValue(null);
            var m = AccessTools.Method(stateType, name);
            m.Invoke(inst, name == "DemandToll" ? new object[] { id, level } : new object[] { id });
        }
    }
}
