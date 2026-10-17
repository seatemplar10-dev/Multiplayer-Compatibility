using System;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Blood Animations by Fuu</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3228047321"/>
    [MpCompatFor("Fuu.BloodAnimations")]
    public class BloodAnimations
    {
        // Lab row 77 (1.7 lab mp74): the mod's blood flecks are visual, but (a) its fleck makers and patches draw the map Rand
        // (FleckMaker.CreateFleckBlood140: host 4 draws, headless arbiter 0) and (b) a landing blood fleck, its blood sprayer and
        // its bullet casings make real filth THINGS (FleckThrownBlood.TimeInterval -> FilthMaker.TryMakeFilth), so one PC hands out
        // one more thing id and every later id shifts. In MP: push/pop Rand around all of it, and no filth from those visual paths.
        [ThreadStatic] private static int visualDepth;

        public BloodAnimations(ModContentPack mod)
        {
            PatchingUtilities.PatchPushPopRand(new[]
            {
                "BloodAnimations.FleckMaker:CreateFleckBlood140",
                "BloodAnimations.FleckMaker:CreateFleckBloodDirectional",
                "BloodAnimations.FleckMaker:CreateFleckDeathBlood",
                "BloodAnimations.FleckMaker:CreateFleckHeavy360",
                "BloodAnimations.FleckMaker:CreateFleckBleed",
                "BloodAnimations.FleckMaker:CreateFleckBleedPuddle",
                "BloodAnimations.FleckMaker:CreateFleckRicochet",
                "BloodAnimations.FleckMaker:CreateMoteImpactFlyingPiece",
                "BloodAnimations.FleckMaker:CreateMoteGroundImpactFlyingPiece",
                "BloodAnimations.FleckThrownBlood:TimeInterval",
                "BloodAnimations.DamageWorker_AddInjury_ApplyToPawn:ApplyToPawn",
                "BloodAnimations.Pawn_HealthTracker_HealthTick:HealthTick",
                "BloodAnimations.SubEffecter_SprayerTriggeredBlood:SubTrigger",
                "BloodAnimations.Verb_TryCastNextBurstShot:TryCastNextBurstShot",
            });

            var enter = new HarmonyMethod(typeof(BloodAnimations), nameof(EnterVisual));
            var leave = new HarmonyMethod(typeof(BloodAnimations), nameof(LeaveVisual));
            foreach (var name in new[]
                     {
                         "BloodAnimations.FleckThrownBlood:TimeInterval",
                         "BloodAnimations.SubEffecter_SprayerTriggeredBlood:SubTrigger",
                         "BloodAnimations.Verb_TryCastNextBurstShot:TryCastNextBurstShot",
                     })
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(name), prefix: enter, finalizer: leave);

            MpCompat.harmony.Patch(AccessTools.Method(typeof(FilthMaker), nameof(FilthMaker.TryMakeFilth),
                    new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef), typeof(int), typeof(FilthSourceFlags), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(BloodAnimations), nameof(PreTryMakeFilth)));
        }

        private static void EnterVisual() => visualDepth++;

        private static Exception LeaveVisual(Exception __exception)
        {
            if (visualDepth > 0) visualDepth--;
            return __exception;
        }

        private static bool PreTryMakeFilth() => visualDepth == 0 || !MP.IsInMultiplayer;
    }
}
