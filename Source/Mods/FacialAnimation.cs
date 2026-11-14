using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>[NL] Facial Animation - WIP by Nals</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197"/>
    [MpCompatFor("Nals.FacialAnimation")]
    public class FacialAnimation
    {
        private static Type controllerType;
        public FacialAnimation(ModContentPack mod)
        {
            // RNG
            // The first CompTick of a pawn builds its face animation dictionary: a lazy list that
            // makes a new FaceAnimation (Rand.Range in Reset) per animation def, enumerated once per JobDef.
            // ~29k Rand calls on a client that draws the pawn, none on one that does not (headless arbiter).
            // Face animation timing is cosmetic, so keep it out of the simulation's Rand stream.
            PatchingUtilities.PatchPushPopRand("FacialAnimation.FacialAnimationControllerComp:InitializeIfNeed");

            // FIXES row 111 (FA 1.6 decompile, 14 Nov 2026). Each face controller comp (head, brow, eyeball, lid, mouth, skin)
            // rolls its face type and colour in InitializeIfNeed, which only the render tree calls (DrawFaceGraphicsComp.CreateSlaveNode).
            // The values are saved (<Comp>_faceType, _color, _secondColor). A PC that draws the pawn has them; the headless arbiter
            // never draws, so its save lacks them, and two drawing PCs roll different faces from their own frame-time Rand.
            // Fix: when a pawn is generated, every PC calls InitializeIfNeed on the controllers once, under a Rand state seeded from
            // the pawn's thingIDNumber. All PCs get the same face, the render path then finds faceType set and skips, and the
            // push/pop means the synced Rand stream is never drawn from. Only in a multiplayer game.
            controllerType = AccessTools.TypeByName("FacialAnimation.IFacialAnimationController");
            if (controllerType == null)
            {
                Log.Error("[MPCompat] Facial Animation: IFacialAnimationController not found, face rolls stay per PC");
                return;
            }
            MpCompat.harmony.Patch(
                AccessTools.DeclaredMethod(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) }),
                postfix: new HarmonyMethod(typeof(FacialAnimation), nameof(GeneratePawnPostfix)));
        }

        private static void GeneratePawnPostfix(Pawn __result)
        {
            if (!MP.IsInMultiplayer || __result?.AllComps == null) return;

            Rand.PushState(Gen.HashCombineInt(__result.thingIDNumber, 1114111));
            try
            {
                foreach (var comp in __result.AllComps)
                {
                    if (!controllerType.IsInstanceOfType(comp)) continue;
                    try { controllerType.GetMethod("InitializeIfNeed")?.Invoke(comp, null); }
                    catch (Exception e) { Log.Warning($"[MPCompat] Facial Animation: InitializeIfNeed failed for {__result}: {e.InnerException ?? e}"); }
                }
            }
            finally
            {
                Rand.PopState();
            }
        }
    }
}
