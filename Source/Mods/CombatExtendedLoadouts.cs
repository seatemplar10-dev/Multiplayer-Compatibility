using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - looking at a pawn's gear no longer assigns it a loadout on one PC</summary>
    /// <remarks>
    /// FIXES row 164 (21 Oct 2026, live save-state checksum run SS-A5 at MP 10,000): <c>Utility_Loadouts.GetLoadout(pawn)</c>
    /// (Loadouts/Utility_Loadouts.cs:160-172) adds <c>LoadoutManager.AssignedLoadouts[pawn] = DefaultLoadout</c> when the pawn has
    /// none, and the manager saves that dictionary. The gear tab (ITab_Inventory.cs:178), the loadouts main tab and the loadout
    /// column call it while drawing, so the PC that looked at a pawn saved an assignment the others did not have (SS-A5: raider
    /// Human31796 "Loadout_Nothing_1" on the host only). The simulation's own calls (JobGiver_UpdateLoadout, CheckReload,
    /// TakeAndEquip, CE_Utility) still assign on every PC. In MP, while in interface code, a pawn without an assignment gets the
    /// default loadout returned and nothing is stored. Same class as rows 160 / 162 / 163 (a UI read writes saved state).
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedLoadouts
    {
        private static System.Reflection.MethodInfo assignedLoadoutsGetter;
        private static FastInvokeHandler defaultLoadoutGetter;

        public CombatExtendedLoadouts(ModContentPack mod)
        {
            var manager = AccessTools.TypeByName("CombatExtended.LoadoutManager")
                          ?? throw new MissingMemberException("CombatExtended.LoadoutManager");
            assignedLoadoutsGetter = AccessTools.PropertyGetter(manager, "AssignedLoadouts")
                                     ?? throw new MissingMemberException(manager.FullName, "AssignedLoadouts");
            var defaultGetter = AccessTools.PropertyGetter(manager, "DefaultLoadout")
                                ?? throw new MissingMemberException(manager.FullName, "DefaultLoadout");
            defaultLoadoutGetter = MethodInvoker.GetHandler(defaultGetter);

            var getLoadout = AccessTools.Method("CombatExtended.Utility_Loadouts:GetLoadout", [typeof(Pawn)])
                             ?? throw new MissingMethodException("CombatExtended.Utility_Loadouts", "GetLoadout(Pawn)");
            MpCompat.harmony.Patch(getLoadout, prefix: new HarmonyMethod(typeof(CombatExtendedLoadouts), nameof(ReadOnlyInInterface)));
        }

        private static bool ReadOnlyInInterface(Pawn pawn, ref object __result)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface || pawn == null)
                return true;
            if (assignedLoadoutsGetter.Invoke(null, null) is System.Collections.IDictionary assigned && assigned.Contains(pawn))
                return true;
            __result = defaultLoadoutGetter(null);
            return false;
        }
    }
}
