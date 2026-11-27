using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Adeptus Mechanicus Armoury by Ogliss - reserves monitor "Deploy Now" button</summary>
    /// <remarks>
    /// FIXES row 492. MapComponent_Reserves.MapComponentTick opens AdeptusMechanicus.Window_Reserves (onlyDrawInDevMode, so a PC with
    /// dev mode on sees it) while a deep strike / infiltrator ReserveForce waits on the current map. Its DoWavePredictionUI button
    /// "Deploy Now" wrote ReservesTracker.nextReserve.delay = 1 on the clicking PC only; ReserveForce.Tick deploys the reserve when
    /// delay reaches 0, so that PC deployed the raid early. Fix: a transpiler replaces that one stfld ReserveForce::delay by a call to
    /// DeployNowClicked. In MP it writes nothing and sends SyncedDeployNow(map, faction loadID, first member thingIDNumber, value);
    /// the command finds that reserve on that map and sets delay = value only while it is not deployed, not defeated and its delay is
    /// still above value, so the first command wins and a later one does nothing. Single player unchanged (the field is written).
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("Ogliss.AdMech.Armoury")]
    public class AdMechArmouryReserves
    {
        private const string LogPrefix = "[MPCompat - AdMech Armoury] ";

        public AdMechArmouryReserves(ModContentPack mod) => PatchDeployNow();

        private static Type componentType;
        private static FieldInfo trackerField;
        private static FieldInfo delayField;
        private static FieldInfo factionField;
        private static FieldInfo deployedField;
        private static FieldInfo defeatedField;
        private static FieldInfo membersField;
        private static FieldInfo reservesField;
        private static int replaced;

        internal static void PatchDeployNow()
        {
            if (LabOff.Row(492))
            {
                Log.Message(LogPrefix + "row 492 switched off by the lab, the reserves monitor Deploy Now button is not synced");
                return;
            }
            var windowType = AccessTools.TypeByName("AdeptusMechanicus.Window_Reserves");
            var reserveType = AccessTools.TypeByName("AdeptusMechanicus.ReserveForce");
            componentType = AccessTools.TypeByName("AdeptusMechanicus.MapComponent_Reserves");
            trackerField = windowType == null ? null : AccessTools.DeclaredField(windowType, "ReservesTracker");
            delayField = reserveType == null ? null : AccessTools.DeclaredField(reserveType, "delay");
            factionField = reserveType == null ? null : AccessTools.DeclaredField(reserveType, "faction");
            deployedField = reserveType == null ? null : AccessTools.DeclaredField(reserveType, "deployed");
            defeatedField = reserveType == null ? null : AccessTools.DeclaredField(reserveType, "defeated");
            membersField = reserveType == null ? null : AccessTools.DeclaredField(reserveType, "members");
            reservesField = componentType == null ? null : AccessTools.DeclaredField(componentType, "reserves");
            var method = windowType == null ? null : AccessTools.DeclaredMethod(windowType, "DoWavePredictionUI");
            if (method == null || trackerField == null || delayField == null || factionField == null || deployedField == null
                || defeatedField == null || membersField == null || reservesField == null || delayField.FieldType != typeof(int))
            {
                Log.Error(LogPrefix + "row 492: Window_Reserves.DoWavePredictionUI, ReserveForce or MapComponent_Reserves members not found, the Deploy Now button can desync");
                return;
            }
            replaced = 0;
            MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(AdMechArmouryReserves), nameof(Transpile)));
            if (replaced != 1)
            {
                Log.Error(LogPrefix + $"row 492: expected one ReserveForce.delay write in DoWavePredictionUI, found {replaced}, the Deploy Now button can desync");
                return;
            }
            Multiplayer.API.MP.RegisterSyncMethod(typeof(AdMechArmouryReserves), nameof(SyncedDeployNow));
            Log.Message(LogPrefix + "row 492: reserves monitor Deploy Now button synced");
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instr)
        {
            var list = instr.ToList();
            var hits = list.Where(ci => ci.opcode == OpCodes.Stfld && ci.operand as FieldInfo == delayField).ToList();
            replaced = hits.Count;
            if (hits.Count != 1) return list;
            var at = list.IndexOf(hits[0]);
            // stack before the stfld: reserve, value -> push this (the window), call DeployNowClicked(reserve, value, window)
            var self = new CodeInstruction(OpCodes.Ldarg_0);
            self.labels.AddRange(hits[0].labels);
            self.blocks.AddRange(hits[0].blocks);
            var call = new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AdMechArmouryReserves), nameof(DeployNowClicked)));
            list[at] = self;
            list.Insert(at + 1, call);
            return list;
        }

        internal static void DeployNowClicked(object reserve, int value, object window)
        {
            if (!Multiplayer.API.MP.IsInMultiplayer || Multiplayer.API.MP.IsExecutingSyncCommand)
            {
                if (reserve == null) throw new NullReferenceException();
                delayField.SetValue(reserve, value);
                return;
            }
            var map = (trackerField.GetValue(window) as MapComponent)?.map;
            if (reserve == null || map == null) return;
            SyncedDeployNow(map, FactionId(reserve), FirstMemberId(reserve), value);
        }

        private static int FactionId(object reserve) => (factionField.GetValue(reserve) as RimWorld.Faction)?.loadID ?? -1;

        private static int FirstMemberId(object reserve) => (membersField.GetValue(reserve) as List<Pawn>)?.FirstOrDefault()?.thingIDNumber ?? -1;

        internal static void SyncedDeployNow(Map map, int factionId, int firstMemberId, int value)
        {
            var comp = map?.components?.FirstOrDefault(c => componentType.IsInstanceOfType(c));
            if (comp == null || reservesField.GetValue(comp) is not IList reserves) return;
            foreach (var reserve in reserves)
            {
                if (reserve == null || FactionId(reserve) != factionId || FirstMemberId(reserve) != firstMemberId) continue;
                if ((bool)deployedField.GetValue(reserve) || (bool)defeatedField.GetValue(reserve)) continue;
                if ((int)delayField.GetValue(reserve) > value) delayField.SetValue(reserve, value);
                return;
            }
        }
    }
}
