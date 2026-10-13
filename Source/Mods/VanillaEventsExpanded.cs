using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Events Expanded by Oskar Potocki, Helixien, Kikohi</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1938420742"/>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaEventsExpanded"/>
    /// Contribution to Multiplayer Compatibility by Sokyran and Reshiram
    [MpCompatFor("VanillaExpanded.VEE")]
    class VEE
    {
        public VEE(ModContentPack mod)
        {
            MpSyncWorkers.Requires<GameCondition>();

            PatchingUtilities.PatchSystemRand("VEE.RegularEvents.MeteoriteShower:TryExecuteWorker", false);

            // VEE 2.0 (11 Jul 2026) added IncomingSmoker and the two choice letters and moved the incidents below to
            // Verse.Rand. Older VEE (the frozen 1.6 copy the 1.7 lab runs, Mods\1938420742\1.6\Assemblies\VEE.dll,
            // 75,264 bytes) has no IncomingSmoker and still calls new System.Random() / UnityEngine.Random.Range and
            // Find.CurrentMap there, so it keeps the patches upstream dropped in fb4751e (11 Jul) and a5bb8ad (25 Jul).
            if (AccessTools.TypeByName("VEE.IncomingSmoker") != null)
                PatchingUtilities.PatchPushPopRand("VEE.IncomingSmoker:ThrowBlackSmoke");
            else
            {
                PatchingUtilities.PatchSystemRand(new[]
                {
                    "VEE.RegularEvents.ApparelPod:TryExecuteWorker",
                    "VEE.RegularEvents.CaravanAnimalWI:GenerateGroup",
                    "VEE.RegularEvents.WeaponPod:TryExecuteWorker",
                    "VEE.RegularEvents.EarthQuake:DamageInRadius",
                }, false);

                // Unity RNG
                PatchingUtilities.PatchUnityRand("VEE.Shuttle:Tick", false);

                // Current map usage, picks between rain and snow based on current map temperature, instead of using map it affects
                PatchingUtilities.ReplaceCurrentMapUsage("VEE.PurpleEvents.PsychicRain:ForcedWeather");

                LongEventHandler.ExecuteWhenFinished(() =>
                    PatchingUtilities.PatchSystemRand("VEE.RegularEvents.SpaceBattle:GameConditionTick", false));
            }

            // Reset game conditions - technically does not require debug mode,
            // but lets you end (almost?) any game condition at any time
            // so I'd consider it close enough to justify `SetDebugOnly` on it.
            MpCompat.RegisterLambdaDelegate("VEE.Settings.VEESettings", "ResetWorldCondButton", 0).SetDebugOnly();
            MpCompat.RegisterLambdaDelegate("VEE.Settings.VEESettings", "ResetMapCondButton", 0).SetDebugOnly();

            RegisterChoiceLetterLambdas("VEE.ChoiceLetter_AcceptCrashlanders");
            RegisterChoiceLetterLambdas("VEE.ChoiceLetter_WhiteoutRefugees");

            MpCompat.RegisterLambdaMethod("VEE.HediffComp_Traitor", "CompGetGizmos", 0).SetDebugOnly();

            PatchPurpleTargetTags();
        }

        #region Purple incident target tags (Long Night, Global Warming / Ice Age)

        // The purple raid / manhunter / insanity incidents ship with targetTags = Map_Dummy only, so they can fire on a
        // home map only while VEE has added Map_PlayerHome to them: LongNight.Init/End (frozen pre-2.0 VEE) and
        // PlanetEvent.Init/GameConditionTick/End (GlobalWarming, IceAge; all VEE versions) add and remove it at runtime.
        // Defs are process state and are not saved. A player whose game LOADED while such a condition was active (a
        // joining or rejoining client, the arbiter) never ran Init, so his storyteller has a different candidate set
        // than a player whose process ran Init -> different incident rolls -> desync. PlanetEvent also re-adds the tag
        // on its first tick after every load (unsaved boolNoSave), so the number of copies differs per process and End
        // can leave it behind in one process and not in another.
        // Fix: after each of those methods and after every game load (Game.FinalizeInit), set the tag from game state
        // alone - present iff the XML def has it, or an active condition wants it. Same result on every machine.
        private static Type longNightType;
        private static Type planetEventType;
        private static IncidentDef[] longNightDefs;
        private static IncidentDef planetEventDef;
        private static HashSet<IncidentDef> xmlHasHomeTag;

        private static void PatchPurpleTargetTags()
        {
            // Only versions whose Init AND End really edit IncidentDef.targetTags count (VEE 2.11's LongNight is empty).
            longNightType = TypeIfEditsTargetTags("VEE.PurpleEvents.LongNight");
            planetEventType = TypeIfEditsTargetTags("VEE.PurpleEvents.PlanetEvent");
            if (longNightType == null && planetEventType == null)
                return;

            var postfix = new HarmonyMethod(typeof(VEE), nameof(NormalizePurpleTargetTags));
            if (longNightType != null)
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(longNightType, "Init"), postfix: postfix);
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(longNightType, "End"), postfix: postfix);
            }
            if (planetEventType != null)
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(planetEventType, "Init"), postfix: postfix);
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(planetEventType, "End"), postfix: postfix);
                var tick = AccessTools.DeclaredMethod(planetEventType, "GameConditionTick");
                if (tick != null)
                    MpCompat.harmony.Patch(tick, postfix: postfix);
            }
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)), postfix: postfix);
            // A map condition disappears with its map without End(); recompute so no process keeps a stale tag.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.DeinitAndRemoveMap)), postfix: postfix);

            // Defs and DefOfs are ready only after the startup load; read the XML state before any game exists.
            LongEventHandler.ExecuteWhenFinished(CaptureXmlTargetTags);
        }

        private static Type TypeIfEditsTargetTags(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
                return null;
            var targetTags = AccessTools.DeclaredField(typeof(IncidentDef), nameof(IncidentDef.targetTags));
            foreach (var name in new[] { "Init", "End" })
            {
                var method = AccessTools.DeclaredMethod(type, name);
                if (method == null || !PatchProcessor.GetOriginalInstructions(method).Any(ci => ci.operand is FieldInfo f && f == targetTags))
                    return null;
            }
            return type;
        }

        private static void CaptureXmlTargetTags()
        {
            IncidentDef PurpleDef(string name) => AccessTools.Field(AccessTools.TypeByName("VEE.VEE_DefOf"), name)?.GetValue(null) as IncidentDef
                                              ?? DefDatabase<IncidentDef>.GetNamedSilentFail(name);

            longNightDefs = new[] { PurpleDef("RaidEnemyPurple"), PurpleDef("ManhunterPackPurple"), PurpleDef("AnimalInsanityMassPurple") }
                .Where(d => d != null).ToArray();
            planetEventDef = PurpleDef("RaidEnemyPurple");
            xmlHasHomeTag = longNightDefs.Where(d => d.targetTags != null && d.targetTags.Contains(IncidentTargetTagDefOf.Map_PlayerHome)).ToHashSet();
        }

        private static void NormalizePurpleTargetTags()
        {
            if (xmlHasHomeTag == null || Current.Game == null)
                return;

            bool longNight = false, planetEvent = false;
            void Scan(GameConditionManager manager)
            {
                if (manager?.ActiveConditions == null)
                    return;
                foreach (var condition in manager.ActiveConditions)
                {
                    longNight |= longNightType != null && longNightType.IsInstanceOfType(condition);
                    planetEvent |= planetEventType != null && planetEventType.IsInstanceOfType(condition);
                }
            }

            Scan(Current.Game.World?.gameConditionManager);
            if (Current.Game.Maps != null)
                foreach (var map in Current.Game.Maps)
                    Scan(map.gameConditionManager);

            foreach (var def in longNightDefs)
                SetHomeTag(def, xmlHasHomeTag.Contains(def) || longNight || (planetEvent && def == planetEventDef));
        }

        private static void SetHomeTag(IncidentDef def, bool wanted)
        {
            var tag = IncidentTargetTagDefOf.Map_PlayerHome;
            def.targetTags ??= new List<IncidentTargetTagDef>();
            var count = def.targetTags.Count(t => t == tag);
            if (count == (wanted ? 1 : 0))
                return;
            def.targetTags.RemoveAll(t => t == tag);
            if (wanted)
                def.targetTags.Add(tag);
        }

        #endregion

        private static void RegisterChoiceLetterLambdas(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            // Both choice letters exist only in VEE 2.0+ (the frozen pre-2.0 VEE.dll has no ChoiceLetter_* types).
            if (type == null)
                return;
            foreach (var method in MpMethodUtil.GetLambda(type, "Choices", MethodType.Getter, null, 0, 1))
                MP.RegisterSyncMethod(method);
        }
    }
}
