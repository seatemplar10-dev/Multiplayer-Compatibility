using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Simple Sidearms by PeteTimesSix</summary>
    /// <see href="https://github.com/PeteTimesSix/SimpleSidearms"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=927155256"/>
    /// <remarks>This would be so simple as a PR for SimpleSidearms</remarks>
    [MpCompatFor("PeteTimesSix.SimpleSidearms")]
    public class SimpleSidearmsCompat
    {
        // TODO: Suggest the author to encapsulate this, would simplify things so much
        [MpCompatSyncField("SimpleSidearms.rimworld.CompSidearmMemory", "primaryWeaponMode")]
        protected static ISyncField primaryWeaponModeSyncField;

        public SimpleSidearmsCompat(ModContentPack mod)
        {
            MpCompatPatchLoader.LoadPatch(this);

            Type type;

            // Gizmo interactions
            {
                type = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.Utilities.WeaponAssingment");

                var methods = new[] {
                    "equipSpecificWeapon", // Called by equipSpecificWeaponFromInventory and equipSpecificWeaponTypeFromInventory
                    "DropSidearm",
                    // Tacticowl dual-wield support
                    "EquipSpecificWeaponFromInventoryAsOffhand",
                    "UnequipOffhand",
                };
                foreach (string method in methods) {
                    MP.RegisterSyncMethod(AccessTools.Method(type, method));
                }
            }

            {
                type = AccessTools.TypeByName("SimpleSidearms.rimworld.CompSidearmMemory");

                var methods = new[] {
                    "SetWeaponAsForced",
                    "SetRangedWeaponTypeAsDefault",
                    "SetMeleeWeaponTypeAsPreferred",
                    "SetUnarmedAsForced",
                    "SetUnarmedAsPreferredMelee",
                    "UnsetForcedWeapon",
                    "UnsetRangedWeaponDefault",
                    "UnsetMeleeWeaponPreference",
                    "UnsetUnarmedAsForced",
                    "ForgetSidearmMemory",
                    // Tacticowl dual wield support
                    "InformOfAddedSidearm", // As opposed to InformOfAddedPrimary, this one can be called from a place we need to sync
                };
                foreach (string method in methods) {
                    MP.RegisterSyncMethod(AccessTools.Method(type, method));
                }
            }

            // Patched sync methods
            {
                // When undrafted, the pawns will remove their temporary forced weapon.
                // Could cause issues when drafting pawns, as they'll be considered undrafted when the postfix runs.
                PatchingUtilities.PatchCancelInInterface("PeteTimesSix.SimpleSidearms.Intercepts.Pawn_DraftController_Drafted_Setter_Postfix:DraftedSetter");
                // When dropping a weapon, it'll cause the pawn to about preferences towards them.
                PatchingUtilities.PatchCancelInInterface("PeteTimesSix.SimpleSidearms.Intercepts.ITab_Pawn_Gear_InterfaceDrop_Prefix:InterfaceDrop");
            }
        }

        #region ThingDefStuffDefPair

        // Used often in the Set* methods for CompSidearmMemory
        [MpCompatSyncWorker("SimpleSidearms.rimworld.ThingDefStuffDefPair")]
        private static void SyncWorkerForThingDefStuffDefPair(SyncWorker sync, ref object obj)
        {
            var traverse = Traverse.Create(obj);

            var thingField = traverse.Field("thing");
            var stuffField = traverse.Field("stuff");

            if (sync.isWriting) {
                sync.Write(thingField.GetValue<ThingDef>());
                sync.Write(stuffField.GetValue<ThingDef>());
            } else {
                thingField.SetValue(sync.Read<ThingDef>());
                stuffField.SetValue(sync.Read<ThingDef>());
            }
        }

        #endregion

        #region primaryWeaponMode field watch

        // Required for primaryWeaponMode
        [MpCompatPrefix("SimpleSidearms.rimworld.Gizmo_SidearmsList", "handleInteraction")]
        private static void HandleInteractionPrefix(ThingComp ___pawnMemory)
        {
            if (MP.IsInMultiplayer) {
                MP.WatchBegin();
                primaryWeaponModeSyncField.Watch(___pawnMemory);
            }
        }

        [MpCompatPostfix("SimpleSidearms.rimworld.Gizmo_SidearmsList", "handleInteraction")]
        private static void HandleInteractionPostfix()
        {
            if (MP.IsInMultiplayer)
                MP.WatchEnd();
        }

        #endregion

        #region Stop verb init in interface

        [MpCompatPrefix("PeteTimesSix.SimpleSidearms.Utilities.GettersFilters", "isManualUse")]
        [MpCompatPrefix("PeteTimesSix.SimpleSidearms.Utilities.GettersFilters", "isDangerousWeapon")]
        [MpCompatPrefix("PeteTimesSix.SimpleSidearms.Utilities.GettersFilters", "isEMPWeapon")]
        [MpCompatPrefix("PeteTimesSix.SimpleSidearms.Utilities.GettersFilters", "findBestRangedWeapon", 8)]
        private static bool PrePrimaryVerbMethodCall(ThingWithComps __0, ref bool __result)
        {
            if (!MP.InInterface)
                return true;

            var comp = __0.GetComp<CompEquippable>();
            // Let the mod handle non-existent CompEquippable
            if (comp == null)
                return true;

            // If verbs are initialized, let the mod handle it as it wants
            if (comp.verbTracker.verbs != null)
                return true;

            // If verbs are null, assume false (is EMP, is dangerous, etc.)
            __result = false;
            // Initialize the verb (FIXES row 315: at most one command per weapon per throttle window)
            TrySendInitVerbs(comp);
            // Prevent the method from running
            return false;
        }

        // FIXES row 315 (29 Oct co-op, desync A): the gizmo calls the 3 getters for every verbs-null weapon on every frame.
        // When the synced comp read back as null, the command threw an NRE, verbs stayed null and the gizmo sent again:
        // 101 failing commands in 3 seconds. Now: one send per weapon, then a doubling wait (1 s up to 60 s).
        // Interface-only state (never read by synced code), so it may differ between PCs.
        private const long InitVerbsFirstWaitMs = 1000;
        private const long InitVerbsMaxWaitMs = 60000;
        private static readonly Dictionary<int, (long nextMs, long waitMs)> initVerbsThrottle = new();
        private static readonly Stopwatch initVerbsClock = Stopwatch.StartNew();
        private static int initVerbsThrottleGame; // identity hash of Current.Game, so no old Game stays referenced

        // Lab only: -mplabssverbslog logs every SyncInitVerbsForComp read that finds no comp (thing id, holder, ThingsById hits)
        private static readonly bool LabSsVerbsLog = GenCommandLine.CommandLineArgPassed("mplabssverbslog");

        private static void TrySendInitVerbs(CompEquippable comp)
        {
            var weapon = comp.parent;
            if (weapon == null)
                return;

            var game = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Current.Game);
            if (initVerbsThrottleGame != game)
            {
                initVerbsThrottle.Clear();
                initVerbsThrottleGame = game;
            }

            var now = initVerbsClock.ElapsedMilliseconds;
            var id = weapon.thingIDNumber;
            var wait = InitVerbsFirstWaitMs;
            if (initVerbsThrottle.TryGetValue(id, out var entry))
            {
                if (now < entry.nextMs)
                    return;
                wait = Math.Min(entry.waitMs * 2, InitVerbsMaxWaitMs);
            }
            initVerbsThrottle[id] = (now + wait, wait);

            var holder = ThingOwnerUtility.GetFirstSpawnedParentThing(weapon);
            SyncInitVerbsForComp(id, holder?.thingIDNumber ?? -1, HolderPath(weapon), comp);
        }

        // Debug text only: the ParentHolder chain of the weapon on the sending PC, e.g. "Pawn_InventoryTracker>Pawn"
        private static string HolderPath(Thing thing)
        {
            var sb = new StringBuilder();
            IThingHolder holder = thing.ParentHolder;
            for (var i = 0; holder != null && i < 6; i++)
            {
                if (sb.Length > 0)
                    sb.Append('>');
                sb.Append(holder.GetType().Name);
                if (holder is Thing { Spawned: true })
                    break;
                holder = holder.ParentHolder;
            }
            return sb.ToString();
        }

        // A null comp no longer throws: the method looks the weapon up again (same game state on every PC). Every read
        // goes to LogInitVerbsRead (always on), or under -mplabssverbslog every null read is logged in full instead.
        // comp MUST stay the LAST param: MP's ThingComp reader returns early on a null parent without reading its comp
        // index (SyncDictRimWorld.cs ThingComp worker), so any arg after comp would read shifted bytes.
        [MpCompatSyncMethod]
        private static void SyncInitVerbsForComp(int thingId, int holderId, string holderPath, CompEquippable comp)
        {
            var nullRead = comp == null;
            var thingHit = false;
            var foundAgain = false;
            if (nullRead)
            {
                Thing holder = null;
                var holderHit = holderId >= 0 && MP.TryGetThingById(holderId, out holder);
                thingHit = MP.TryGetThingById(thingId, out var byId);
                Thing found = null;
                if (holderHit && holder.thingIDNumber == thingId)
                    found = holder; // spawned weapon: GetFirstSpawnedParentThing returned the weapon itself
                else if (holderHit && holder is IThingHolder thingHolder)
                    found = ThingOwnerUtility.GetAllThingsRecursively(thingHolder).Find(t => t.thingIDNumber == thingId);
                found ??= byId;
                foundAgain = found != null;
                comp = (found as ThingWithComps)?.GetComp<CompEquippable>();

                if (LabSsVerbsLog)
                    Log.Warning($"[MPCompat] row 315: SyncInitVerbsForComp read a null comp: thing {thingId} (ThingsById hit {thingHit}), " +
                                $"holder {holderId} (ThingsById hit {holderHit}, {holder?.GetType().Name ?? "none"}, spawned {holder?.Spawned}), " +
                                $"sender path {holderPath}, found again {foundAgain}, comp {comp != null}, " +
                                $"tick {Find.TickManager?.TicksGame}, bySelf {MP.IsExecutingSyncCommandIssuedBySelf}");
            }

            if (!LabSsVerbsLog)
                LogInitVerbsRead(thingId, holderId, holderPath, nullRead, thingHit, foundAgain, comp != null);

            if (comp == null)
                return;

            if (comp.verbTracker != null && comp.verbTracker.verbs == null)
                comp.verbTracker.InitVerbsFromZero();
        }

        // FIXES row 315 desync A diagnostic, always on. Per weapon: one line for its first command (null comp or not),
        // then one more each time the read result changes, at most 5 lines per weapon per game.
        // Reading rule for the two players' logs: match on thing + tick and compare only the result part (before ';').
        // Same result on both PCs = the null read happens on every PC (the sender's lag race, not the desync).
        // A different result = the PCs already differ there. If a line has no partner at that tick, compare it with the
        // other PC's latest earlier line for that thing (a PC logs only result changes). "log since" is this PC's first
        // command after its last game load: a reloaded PC starts with an empty history and logs its first read again.
        // bySelf is True only on the sending PC, and False on every PC while catching up, so it is never compared.
        // Log-only state, never read by synced code.
        private const int InitVerbsLogMaxPerWeapon = 5;
        private static readonly Dictionary<int, (int lines, string result)> initVerbsLogged = new();
        private static int initVerbsLoggedGame; // identity hash of Current.Game, own copy: a receiving PC never runs TrySendInitVerbs
        private static int initVerbsLoggedSince;

        private static void LogInitVerbsRead(int thingId, int holderId, string holderPath, bool nullRead, bool thingHit, bool foundAgain, bool compAfter)
        {
            try
            {
                var tick = Find.TickManager?.TicksGame ?? -1;
                var game = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Current.Game);
                if (initVerbsLoggedGame != game)
                {
                    initVerbsLogged.Clear();
                    initVerbsLoggedGame = game;
                    initVerbsLoggedSince = tick;
                }

                var result = nullRead
                    ? $"comp null True, ThingsById hit {thingHit}, found again {foundAgain}, comp after {compAfter}"
                    : "comp null False";
                var lines = 0;
                if (initVerbsLogged.TryGetValue(thingId, out var last))
                {
                    if (last.result == result || last.lines >= InitVerbsLogMaxPerWeapon)
                        return;
                    lines = last.lines;
                }
                initVerbsLogged[thingId] = (lines + 1, result);

                Log.Warning($"[MPCompat] row 315: SyncInitVerbsForComp thing {thingId} tick {tick}: {result}; holder {holderId}, " +
                            $"sender path {holderPath}, bySelf {MP.IsExecutingSyncCommandIssuedBySelf}, log since tick {initVerbsLoggedSince}");
            }
            catch
            {
                // Diagnostic only: logging must never change what the synced command does on this PC
            }
        }

        #endregion
    }
}
