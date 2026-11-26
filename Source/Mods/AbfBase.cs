using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>ABF: Artificial Beings Framework by Killathon</summary>
    [MpCompatFor("Killathon.ArtificialBeings")]
    public class AbfBase
    {
        private static readonly FieldInfo medCareField = AccessTools.Field(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.medCare));
        private static readonly FieldInfo settingsPawnField = AccessTools.Field(typeof(Pawn_PlayerSettings), "pawn");

        // FIXES row 470 (26 Nov 2026): MedicalCareUtility_MedicalCareSelectButton_Patch (a prefix that replaces the vanilla medical care
        // dropdown) builds options whose click lambda runs `p.playerSettings.medCare = category`. Multiplayer syncs medCare only in
        // the vanilla button, so the pick changed the clicking PC only. The store in that lambda is rewritten to WriteMedCare,
        // which sends one SyncedSetMedCare(pawn, category) command.
        public AbfBase(ModContentPack mod)
        {
            if (LabOff.Row(470))
                Log.Message("[MPCompat - ABF] row 470 switched off by the lab, medical care dropdown not synced");
            else
                PatchMedCare();
        }

        internal static void PatchMedCare()
        {
            var outer = AccessTools.TypeByName("MedicalCareUtility_Patch+MedicalCareUtility_MedicalCareSelectButton_Patch");
            MethodInfo lambda = null;
            if (outer != null && medCareField != null && settingsPawnField != null)
                foreach (var nested in outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var m in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (m.ReturnType == typeof(void) && m.GetParameters().Length == 0 && WritesMedCare(m))
                            lambda = m;
            if (lambda == null)
            {
                Log.Error("[MPCompat - ABF] row 470: the medical care dropdown lambda (writes playerSettings.medCare) not found, the dropdown can desync");
                return;
            }
            MP.RegisterSyncMethod(typeof(AbfBase), nameof(SyncedSetMedCare));
            MpCompat.harmony.Patch(lambda, transpiler: new HarmonyMethod(typeof(AbfBase), nameof(ReplaceStore)));
            Log.Message("[MPCompat - ABF] row 470: medical care dropdown synced");
        }

        private static bool WritesMedCare(MethodInfo method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            for (int i = 0; i < il.Length - 4; i++)
            {
                if (il[i] != 0x7D) continue; // stfld
                try
                {
                    if (method.Module.ResolveField(BitConverter.ToInt32(il, i + 1), method.DeclaringType.GetGenericArguments(), null) == medCareField)
                        return true;
                }
                catch { }
            }
            return false;
        }

        internal static IEnumerable<CodeInstruction> ReplaceStore(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Stfld && ins.operand is FieldInfo f && f == medCareField)
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AbfBase), nameof(WriteMedCare)));
                else
                    yield return ins;
            }
        }

        // the stack at the old store is (playerSettings, category)
        public static void WriteMedCare(Pawn_PlayerSettings settings, MedicalCareCategory category)
        {
            var pawn = settings == null ? null : (Pawn)settingsPawnField.GetValue(settings);
            if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand && pawn != null)
                SyncedSetMedCare(pawn, category);
            else
                settings.medCare = category;
        }

        internal static void SyncedSetMedCare(Pawn pawn, MedicalCareCategory category)
        {
            if (pawn?.playerSettings != null) pawn.playerSettings.medCare = category;
        }
    }
}
