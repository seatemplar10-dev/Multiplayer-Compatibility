using System;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>MediStation by Asiimoves</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3791910009"/>
    [MpCompatFor("Asiimoves.MediStation")]
    public class MediStation
    {
        // New-mods check (29 Oct): two gizmos change state outside a synced call. "Eject medicine" (CompMediStationFuel,
        // no subclass overrides it) and the "allow guests" toggle (CompMediStationSharing lambda 1). The refuel gizmos are
        // vanilla CompRefuelable lambdas (MP core syncs them), and the float menu only calls TryTakeOrderedJob (synced).
        //
        // Multifaction ownership (2 Nov): CompMediStationSharing.CanBeUsedBy lets any pawn with IsColonist use the station,
        // and IsColonist is true for every player faction. The station has no CompForbiddable and JobGiver_UseMediStation
        // searches the whole map, so the other player's wounded colonists drained your medicine. Inside a prison cell the
        // mod skips the sharing check and asks CompMediStationPrison.PrisonAllowsUseBy, which accepts any prisoner, so a
        // prisoner of the other player was treated too. Both are refused when station and pawn (or the prisoner's host)
        // belong to two different player factions, unless the owner switched "allow guests" on (the owner chose to share).
        // Saved sim data only (factions, allowGuests, the station's room), so every PC gets the same answer. A refused pawn just gets no job / a disabled float menu entry.
        // The Wakeup, Gojuice and Yayo variants inherit CanBeUsedBy and share CompMediStationPrison, so one patch covers them.
        private static AccessTools.FieldRef<object, bool> allowGuestsField;
        private static Type sharingType;

        public MediStation(ModContentPack mod)
        {
            MP.RegisterSyncMethod(AccessTools.TypeByName("MediStation.CompMediStationFuel"), "EjectMedicine");
            MpCompat.RegisterLambdaMethod("MediStation.CompMediStationSharing", "CompGetGizmosExtra", 1);

            try
            {
                sharingType = AccessTools.TypeByName("MediStation.CompMediStationSharing");
                allowGuestsField = AccessTools.FieldRefAccess<object, bool>(AccessTools.DeclaredField(sharingType, "allowGuests"));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(sharingType, "CanBeUsedBy"),
                    postfix: new HarmonyMethod(typeof(MediStation), nameof(PostCanBeUsedBy)));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod("MediStation.CompMediStationPrison:PrisonAllowsUseBy"),
                    postfix: new HarmonyMethod(typeof(MediStation), nameof(PostPrisonAllowsUseBy)));
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: MediStation: faction ownership patches not loaded (mod changed?): {e}");
            }
        }

        private static bool AllowsGuests(ThingComp anyCompOfStation)
        {
            foreach (var comp in anyCompOfStation.parent.AllComps)
            {
                if (sharingType.IsInstanceOfType(comp))
                    return allowGuestsField(comp);
            }

            return false;
        }

        private static void PostCanBeUsedBy(ThingComp __instance, Pawn pawn, ref bool __result)
        {
            if (!__result || !MP.IsInMultiplayer || pawn == null)
                return;

            var stationFaction = __instance.parent.Faction;
            var pawnFaction = pawn.Faction;
            if (stationFaction is { IsPlayer: true } && pawnFaction is { IsPlayer: true } && stationFaction != pawnFaction
                && !allowGuestsField(__instance))
                __result = false;
        }

        private static void PostPrisonAllowsUseBy(ThingComp __instance, Pawn pawn, ref bool __result)
        {
            if (!__result || !MP.IsInMultiplayer || pawn == null || !pawn.IsPrisoner)
                return;

            // PrisonAllowsUseBy also answers true for a station outside a prison cell (the caller ignores it then).
            // Same room test as CompMediStationPrison.IsInPrison, so only a station in a cell is restricted.
            var station = __instance.parent;
            if (!station.Spawned || !(station.GetRoom()?.IsPrisonCell ?? false))
                return;

            var stationFaction = station.Faction;
            var host = pawn.HostFaction;
            if (stationFaction is { IsPlayer: true } && host is { IsPlayer: true } && stationFaction != host
                && !AllowsGuests(__instance))
                __result = false;
        }
    }
}
