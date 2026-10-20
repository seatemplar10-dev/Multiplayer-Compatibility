using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Nice Bill Tab by Andromeda</summary>
    /// <remarks>
    /// FIXES row 143 (20 Oct 2026, lab R7): <c>StatMechanoidWorker.GetMechStats</c> builds a real mech pawn (with its gear,
    /// CE ammo and turret guns) per craftable mech kind to read its DPS. It runs once per process from
    /// <c>ModIntegration.LoadedGame</c> -> <c>StatRequestWorker.PostScan</c> (static <c>PostScanFinished</c>) and again from
    /// the bill UI for kinds not cached yet. The host ran it at its first load before hosting, a joiner after loading the join
    /// point: 334 thing ids (plus hediff and other ids) taken on the joiner only, so the next new thing got another id on
    /// each PC (R7: a wild MonitorLizard 31742 on the host, 32076 on the arbiter) and the map split 545 ticks after the join.
    /// The stat pawns are never spawned or saved, so every unique-id counter and the random state are put back after the
    /// call, on every PC and in single player too (the host's first load is not in a session yet).
    /// </remarks>
    [MpCompatFor("Andromeda.NiceBillTab")]
    internal class NiceBillTab
    {
        private static FieldInfo[] idFields;
        private static int depth;
        private static bool logged;

        public NiceBillTab(ModContentPack mod)
        {
            idFields = AccessTools.GetDeclaredFields(typeof(UniqueIDsManager))
                .Where(f => !f.IsStatic && f.FieldType == typeof(int))
                .ToArray();

            var getMechStats = AccessTools.DeclaredMethod("NiceBillTab.StatMechanoidWorker:GetMechStats")
                ?? throw new MissingMethodException("NiceBillTab.StatMechanoidWorker", "GetMechStats");
            MpCompat.harmony.Patch(getMechStats,
                prefix: new HarmonyMethod(typeof(NiceBillTab), nameof(SaveIds)),
                finalizer: new HarmonyMethod(typeof(NiceBillTab), nameof(RestoreIds)));
        }

        private static void SaveIds(out int[] __state)
        {
            __state = null;
            if (depth++ > 0) return;
            var ids = Current.Game?.uniqueIDsManager;
            if (ids == null) return;
            __state = idFields.Select(f => (int)f.GetValue(ids)).ToArray();
            Rand.PushState();
        }

        private static void RestoreIds(int[] __state)
        {
            depth--;
            if (__state == null) return;
            Rand.PopState();
            var ids = Current.Game?.uniqueIDsManager;
            if (ids == null) return;
            int thingIdsTaken = 0;
            for (int i = 0; i < idFields.Length; i++)
            {
                if (idFields[i].Name == "nextThingID")
                    thingIdsTaken = (int)idFields[i].GetValue(ids) - __state[i];
                idFields[i].SetValue(ids, __state[i]);
            }

            if (!logged && thingIdsTaken > 0)
            {
                logged = true;
                Log.Message($"MPCompat - Nice Bill Tab: a mech stat pawn took {thingIdsTaken} thing id(s); every unique-id counter and the random state were put back (FIXES row 143)");
            }
        }
    }
}
