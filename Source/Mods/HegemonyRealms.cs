using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Hegemony - Faction Territories and Vassalage 2 by Jaeger972: one realm per player faction (private co-op fork only)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3825320105"/>
    [MpCompatFor("jaeger972.factionterritories2")]
    public class HegemonyRealms
    {
        // FIXES row 299, Hegemony Stage 2 P1. The private fork (hegemony_fork, BRIEF-two-realms.md) has static hooks on
        // FactionTerritories2.Realm: AllHook, DefaultHook, PushHook, PopHook. This module sets them. The Workshop DLL has no
        // Realm type: then this module logs one message and does nothing, and the Stage 1 modules (Hegemony, HegemonyUi) keep working.
        //
        // Every hook checks at CALL time "MP.IsInMultiplayer && multifaction". When that is false it behaves like single player:
        // AllHook and DefaultHook return null (Realm falls back to { Faction.OfPlayer }), Push and Pop do nothing.
        // Main thread only: MP's FactionContext.stack is a plain static Stack.

        private const string RealmType = "FactionTerritories2.Realm";

        private static Func<object> worldCompGetter;     // Multiplayer.Client.Multiplayer.WorldComp
        private static Func<object> gameCompGetter;      // Multiplayer.Client.Multiplayer.GameComp
        private static FieldInfo spectatorField;         // MultiplayerWorldComp.spectatorFaction
        private static FieldInfo multifactionField;      // MultiplayerGameComp.multifaction
        private static Action<Map, Faction, bool> pushFaction; // FactionExtensions.PushFaction(Map, Faction, bool)
        private static Func<Faction> popFaction;         // FactionExtensions.PopFaction()
        private static ICollection mpFactionStack;       // FactionContext.stack, only to see whether a push really happened (may be null)
        private static bool bound;
        private static bool installed;

        // One entry per Realm.Push, in order: true = this push really pushed in MP, so its Pop must pop. A push that did nothing
        // (flag off, refused faction) still gets an entry, so Push and Pop stay balanced even if the flag changes between them.
        private static readonly Stack<bool> pushes = new();

        private static readonly List<Faction> scratch = new();
        private static List<Faction> cached = new();
        private static readonly Comparison<Faction> ByLoadId = (a, b) => a.loadID.CompareTo(b.loadID);

        public HegemonyRealms(ModContentPack mod)
        {
            try
            {
                var realm = AccessTools.TypeByName(RealmType);
                if (realm == null)
                {
                    Log.Message("[MP Compat] Hegemony realms: FactionTerritories2.Realm not found (Workshop Hegemony, not the co-op fork). Hegemony stays in its Stage 1 mode.");
                    return;
                }

                Install(realm);
            }
            catch (Exception e)
            {
                Log.Error($"[MP Compat] Hegemony realms: could not install the realm hooks, Hegemony keeps one realm. {e}");
            }
        }

        private static void Install(Type realm)
        {
            if (installed)
                return;

            var allHook = AccessTools.Field(realm, "AllHook");
            var defaultHook = AccessTools.Field(realm, "DefaultHook");
            var pushHook = AccessTools.Field(realm, "PushHook");
            var popHook = AccessTools.Field(realm, "PopHook");
            if (allHook == null || defaultHook == null || pushHook == null || popHook == null)
            {
                Log.Error("[MP Compat] Hegemony realms: Realm has no AllHook/DefaultHook/PushHook/PopHook, Hegemony keeps one realm.");
                return;
            }

            // Bound before the hooks are set. If it fails, every hook stays inert (single player behaviour).
            bound = ResolveMultiplayerInternals();
            if (!bound)
                Log.Error("[MP Compat] Hegemony realms: could not bind Multiplayer internals, Hegemony keeps one realm in multiplayer.");

            var of = AccessTools.DeclaredMethod(realm, "Of", [typeof(Faction)]);
            if (of != null)
                realmOf = (Func<Faction, Faction>)Delegate.CreateDelegate(typeof(Func<Faction, Faction>), of);
            else
                Log.Error("[MP Compat] Hegemony realms: Realm.Of(Faction) not found, realm-vs-realm wars are not detected.");

            allHook.SetValue(null, (Func<List<Faction>>)AllRealms);
            defaultHook.SetValue(null, (Func<Faction>)DefaultRealm);
            pushHook.SetValue(null, (Action<Faction>)PushRealm);
            popHook.SetValue(null, (Action)PopRealm);
            installed = true;

            try
            {
                PatchMpRelationCommand();
            }
            catch (Exception e)
            {
                Log.Error($"[MP Compat] Hegemony realms: could not patch MP's relation command, it is not blocked between realms. {e}");
            }
        }

        // MP's multifaction "Change faction relation" pawn gizmo (MultifactionPatches.cs, PawnChangeRelationGizmo) sets the
        // relation directly. Between two realms that skips Hegemony's synced war and peace (S2: war by declaration, peace only
        // by an accepted offer). So: no gizmo between realms, and the synced SetRelation does nothing between realms.
        private static void PatchMpRelationCommand()
        {
            var setRelation = AccessTools.DeclaredMethod("Multiplayer.Client.Patches.PawnChangeRelationGizmo:SetRelation");
            if (setRelation == null)
            {
                Log.Error("[MP Compat] Hegemony realms: MP PawnChangeRelationGizmo.SetRelation not found, MP's relation command is not blocked between realms.");
                return;
            }

            MpCompat.harmony.Patch(setRelation, prefix: new HarmonyMethod(typeof(HegemonyRealms), nameof(PreMpSetRelation)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.GetGizmos)),
                postfix: new HarmonyMethod(typeof(HegemonyRealms), nameof(PostPawnGizmos)) { priority = Priority.Last });
        }

        private const string MpRelationLabel = "Change faction relation"; // MP hard-codes this label (not translated)

        private static bool BetweenRealms(Faction other) => RealmsActive && other != Faction.OfPlayer && IsRealmFaction(Faction.OfPlayer) && IsRealmFaction(other);

        // Runs as the issuer on every PC (synced command), so the check gives the same answer everywhere.
        private static bool PreMpSetRelation(Faction other) => !BetweenRealms(other);

        private static IEnumerable<Gizmo> PostPawnGizmos(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            var block = BetweenRealms(__instance.Faction);
            foreach (var gizmo in gizmos)
            {
                if (block && gizmo is Command_Action { defaultLabel: MpRelationLabel })
                    continue;
                yield return gizmo;
            }
        }

        private static bool ResolveMultiplayerInternals()
        {
            try
            {
                var extensions = AccessTools.TypeByName("Multiplayer.Client.Factions.FactionExtensions");
                var push = extensions == null ? null : AccessTools.DeclaredMethod(extensions, "PushFaction", [typeof(Map), typeof(Faction), typeof(bool)]);
                var pop = extensions == null ? null : AccessTools.DeclaredMethod(extensions, "PopFaction", Type.EmptyTypes);
                var worldComp = AccessTools.PropertyGetter("Multiplayer.Client.Multiplayer:WorldComp");
                var gameComp = AccessTools.PropertyGetter("Multiplayer.Client.Multiplayer:GameComp");
                var spectator = AccessTools.Field("Multiplayer.Client.MultiplayerWorldComp:spectatorFaction");
                var multifaction = AccessTools.Field("Multiplayer.Client.Comp.MultiplayerGameComp:multifaction");
                if (push == null || pop == null || worldComp == null || gameComp == null || spectator == null || multifaction == null)
                    return false;

                pushFaction = (Action<Map, Faction, bool>)Delegate.CreateDelegate(typeof(Action<Map, Faction, bool>), push);
                popFaction = (Func<Faction>)Delegate.CreateDelegate(typeof(Func<Faction>), pop);
                worldCompGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), worldComp);
                gameCompGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), gameComp);
                spectatorField = spectator;
                multifactionField = multifaction;

                // Optional: lets PushRealm tell whether MP's push really happened. Without it PushRealm trusts "no exception".
                mpFactionStack = AccessTools.Field("Multiplayer.Client.FactionContext:stack")?.GetValue(null) as ICollection;
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"[MP Compat] Hegemony realms: could not bind Multiplayer internals: {e}");
                return false;
            }
        }

        // Stage 2: the Stage 1 modules (Hegemony, HegemonyUi) lift a block only while this is true.
        internal static bool RealmsActive => installed && Active();

        // A player faction that owns a realm (not MP's Spectator). False when realms are not active.
        internal static bool IsRealmFaction(Faction faction) => faction != null && AllRealms()?.Contains(faction) == true;

        // The fork's Realm.Of(Faction): a realm -> itself, a vassal domain faction -> its owner realm, else null.
        private static Func<Faction, Faction> realmOf;

        internal static Faction RealmOf(Faction faction) => faction == null || realmOf == null || !RealmsActive ? null : realmOf(faction);

        // Checked at call time: the flag lives in the save and a game can be reloaded in process. Review #12: a true is kept
        // per MultiplayerGameComp object (a reload makes a new one). A false is read again each call, in case hosting sets
        // the flag after it creates the comp.
        private static object multifactionGameComp;

        private static bool Active()
        {
            if (!bound || !MP.IsInMultiplayer)
                return false;

            try
            {
                var gameComp = gameCompGetter();
                if (gameComp == null)
                    return false;
                if (ReferenceEquals(gameComp, multifactionGameComp))
                    return true;
                if (!(bool)multifactionField.GetValue(gameComp))
                    return false;
                multifactionGameComp = gameComp;
                return true;
            }
            catch (Exception)
            {
                // No MP game yet (loading, leaving): behave like single player.
                return false;
            }
        }

        private static Faction Spectator()
        {
            try
            {
                var worldComp = worldCompGetter();
                return worldComp == null ? null : spectatorField.GetValue(worldComp) as Faction;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The player factions that own a realm: IsPlayer, not MP's Spectator, sorted by loadID.
        // Same on every PC: the faction set is part of the save/join snapshot and of synced commands, loadID is saved
        // and never reused, the Spectator is a saved field of the world comp, and the sort key is unique, so the order does not depend on
        // the order of FactionManager.AllFactionsListForReading or on the PC.
        // Cheap: the scan and sort of a few factions run into a reused scratch list. The returned list is replaced only when its content
        // changes, so callers may keep and iterate it, and must not modify it. Null = no player faction found.
        // Review #12: the scan runs once per game tick and faction count. A faction added in a tick (a join command, also
        // while paused) changes the count, so the list is rebuilt. Factions are never removed and never change IsPlayer.
        private static object listGame;
        private static int listTick = -1;
        private static int listFactionCount = -1;

        private static List<Faction> RealmList()
        {
            var factions = Find.FactionManager?.AllFactionsListForReading;
            if (factions == null)
                return null;

            var game = Current.Game;
            var tick = Find.TickManager?.TicksGame ?? -1;
            if (ReferenceEquals(game, listGame) && tick == listTick && factions.Count == listFactionCount)
                return cached.Count == 0 ? null : cached;

            var spectator = Spectator();
            // No Spectator known (world comp not loaded, or reflection failed): fail closed, no realms. The list could hold
            // the Spectator as a realm (review 29 Oct, finding 5). Not kept, so the next call reads the Spectator again.
            if (spectator == null)
            {
                listGame = null;
                return null;
            }
            listGame = game;
            listTick = tick;
            listFactionCount = factions.Count;

            scratch.Clear();
            for (var i = 0; i < factions.Count; i++)
            {
                var faction = factions[i];
                if (faction.IsPlayer && faction != spectator)
                    scratch.Add(faction);
            }
            scratch.Sort(ByLoadId);

            if (!SameFactions(cached, scratch))
                cached = new List<Faction>(scratch);
            scratch.Clear();
            return cached.Count == 0 ? null : cached;
        }

        private static bool SameFactions(List<Faction> a, List<Faction> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static List<Faction> AllRealms() => Active() ? RealmList() : null;

        // The host's faction: the lowest-loadID player faction that is not the Spectator. That is the save's original colony faction.
        // Deterministic on all PCs because it is derived only from saved state: faction loadIDs come from one counter (UniqueIDsManager),
        // the colony faction exists from world generation, so it has a lower loadID than any faction MP made later (the Spectator at
        // hosting, a joiner's faction at join). No PC-local value is used (not MP.RealPlayerFaction, not Faction.OfPlayer, not the player list).
        private static Faction DefaultRealm() => Active() ? RealmList()?[0] : null;

        private static void PushRealm(Faction faction)
        {
            if (!Active())
            {
                pushes.Push(false);
                return;
            }

            if (faction == null || !faction.IsPlayer)
            {
                Log.ErrorOnce($"[MP Compat] Hegemony realms: refused to push {(faction == null ? "null" : faction.GetUniqueLoadID())}, it is not a player faction.", 299_201);
                pushes.Push(false);
                return;
            }

            var before = mpFactionStack?.Count ?? -1;
            try
            {
                pushFaction(null, faction, true);
                pushes.Push(true);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[MP Compat] Hegemony realms: pushing {faction.GetUniqueLoadID()} failed: {e}", 299_202);
                // MP pushes its stack before it can throw in most places. If we can see its stack, pop only when it grew.
                pushes.Push(mpFactionStack != null && mpFactionStack.Count > before);
            }
        }

        private static void PopRealm()
        {
            if (pushes.Count == 0)
            {
                Log.ErrorOnce("[MP Compat] Hegemony realms: Realm.Pop without a Realm.Push, ignored.", 299_203);
                return;
            }

            // Whether to pop was decided at push time, not by the flag now.
            if (!pushes.Pop())
                return;

            try
            {
                popFaction();
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[MP Compat] Hegemony realms: popping failed: {e}", 299_204);
            }
        }
    }
}
