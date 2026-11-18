using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Star Wars : The Force Standalone by Lee</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3557220601"/>
    /// <remarks>
    /// FIXES row 48. Written against the decompiled 1.6 "Star Wars The Force - Standalone.dll" that the 1.7 co-op loads.
    /// Telekinesis holder (<c>HediffComp_ThingHolder</c> / <c>HediffComp_ProjectileHolder</c>): every gizmo changes the
    /// simulation straight from the clicking PC. The throw targeter callback calls <c>LaunchProjectileAt</c>, which spawns and
    /// launches a projectile on that PC only, from <c>Pawn.Drawer.DrawPos</c> plus <c>Mathf.Sin(Time.realtimeSinceStartup)</c>.
    /// "Launch all" rolls <c>Rand</c> target cells in the interface, "launch next" moves <c>currentLaunchIndex</c>, and
    /// "explode"/"release" act directly. Fix: these gizmo lambdas are synced commands, and inside <c>LaunchProjectileAt</c> the
    /// spawn origin uses the pawn's cell instead of the tweened draw position and drops the real-time sine (MP only).
    /// Apprentice ability: <c>CompAbilityEffect_MasterApprentice.Apply</c> (ability cast, sim) calls
    /// <c>ChangeBackstoryBasedOnAlignment</c>, which picks Sith or Jedi on a tie with <c>UnityEngine.Random.value</c>.
    /// Fix: that call reads <c>Rand.Value</c> instead. Not run in game yet.
    /// </remarks>
    [MpCompatFor("lee.theforce.standalone")]
    public class StarWarsTheForceStandalone
    {
        public StarWarsTheForceStandalone(ModContentPack mod)
        {
            // lab switch row 256 (1.7 lab, 18 Nov): off = old behaviour
            if (!LabOff.Row(256)) PatchFlyer();
            // lab switch row 48 (wave 3, 17 Nov): off = old behaviour
            if (LabOff.Row(48)) return;
            const string ns = "TheForce_Standalone.HediffComps.";
            var thingHolderType = AccessTools.TypeByName(ns + "HediffComp_ThingHolder");
            var projectileHolderType = AccessTools.TypeByName(ns + "HediffComp_ProjectileHolder");

            // ThingHolder.CompGetGizmos: 1 = throw targeter callback (gets the chosen LocalTargetInfo, calls the virtual
            // LaunchProjectileAt), 5 = ExplodeHeldPawn, 6 = release held thing.
            foreach (var ordinal in new[] { 1, 5, 6 })
                MP.RegisterSyncMethodLambda(thingHolderType, nameof(HediffComp.CompGetGizmos), ordinal);
            // ProjectileHolder.CompGetGizmos: 0 = "launch next" (currentLaunchIndex), 1 = LaunchAllProjectiles (Rand cells).
            foreach (var ordinal in new[] { 0, 1 })
                MP.RegisterSyncMethodLambda(projectileHolderType, nameof(HediffComp.CompGetGizmos), ordinal);

            var launch = AccessTools.DeclaredMethod(thingHolderType, "LaunchProjectileAt", new[] { typeof(IntVec3), typeof(LocalTargetInfo) });
            MpCompat.harmony.Patch(launch,
                transpiler: new HarmonyMethod(typeof(StarWarsTheForceStandalone), nameof(DeterministicLaunchOrigin)));

            PatchingUtilities.PatchUnityRand(
                "TheForce_Standalone.Apprenticeship.CompAbilityEffect_MasterApprentice:ChangeBackstoryBasedOnAlignment", false);
        }

        // FIXES row 256: TelekinesisPawnFlyer (subclass of vanilla PawnFlyer, no Tick override) has a private RecomputePosition that
        // sets the flyer's Position (saved) and runs only from its DynamicDrawPhaseAt, so only a PC that draws the map moves it
        // (row 255 is the vanilla case). In MP the vanilla Tick (postfix, filtered to this type) and SpawnSetup recompute on every PC.
        private static FastInvokeHandler flyerRecompute;
        private static Type flyerType;

        private static void PatchFlyer()
        {
            flyerType = AccessTools.TypeByName("TheForce_Standalone.Telekinesis.TelekinesisPawnFlyer");
            var recompute = flyerType == null ? null : AccessTools.DeclaredMethod(flyerType, "RecomputePosition", []);
            var spawn = flyerType == null ? null : AccessTools.DeclaredMethod(flyerType, nameof(Thing.SpawnSetup));
            var tick = AccessTools.DeclaredMethod(typeof(PawnFlyer), nameof(Thing.Tick));
            if (recompute == null || spawn == null || tick == null)
            {
                Log.Warning("MPCompat: TelekinesisPawnFlyer RecomputePosition/SpawnSetup/PawnFlyer.Tick not found - Telekinesis flight is not guarded (FIXES row 256)");
                return;
            }
            flyerRecompute = MethodInvoker.GetHandler(recompute);
            var post = new HarmonyMethod(typeof(StarWarsTheForceStandalone), nameof(PostFlyerRecompute));
            MpCompat.harmony.Patch(tick, postfix: post);
            MpCompat.harmony.Patch(spawn, postfix: post);
        }

        private static void PostFlyerRecompute(Thing __instance)
        {
            if (!MP.IsInMultiplayer || __instance.GetType() != flyerType || __instance.Destroyed || !__instance.Spawned) return;
            if (__instance is not IThingHolder holder || holder.GetDirectlyHeldThings().Count == 0) return;   // DestinationPos reads it
            flyerRecompute(__instance);
        }

        private static IEnumerable<CodeInstruction> DeterministicLaunchOrigin(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
            => instr
                .ReplaceMethod(AccessTools.DeclaredPropertyGetter(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos)),
                    AccessTools.DeclaredMethod(typeof(StarWarsTheForceStandalone), nameof(LaunchDrawPos)), baseMethod, expectedReplacements: 1)
                .ReplaceMethod(AccessTools.DeclaredPropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)),
                    AccessTools.DeclaredMethod(typeof(StarWarsTheForceStandalone), nameof(LaunchRealtime)), baseMethod, expectedReplacements: 1);

        // Replaces Pawn.Drawer.DrawPos (tweened per frame, differs per PC). In MP: the pawn's cell centre, the same on every PC.
        private static Vector3 LaunchDrawPos(Pawn_DrawTracker drawer)
            => MP.IsInMultiplayer ? drawer.pawn.Position.ToVector3Shifted() : drawer.DrawPos;

        // Replaces Time.realtimeSinceStartup (wall clock of each PC). In MP: 0, so the Sin offset is 0.
        private static float LaunchRealtime() => MP.IsInMultiplayer ? 0f : Time.realtimeSinceStartup;
    }
}
