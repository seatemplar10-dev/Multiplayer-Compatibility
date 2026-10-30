using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Capture Expansion by ferny</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3787736781"/>
    [MpCompatFor("ferny.CaptureExpansion")]
    public class CaptureExpansion
    {
        // Mod check (30 Oct). Lambda ordinals read from the shipped DLL (sha256 dae6414a), not from the c54d67d source.
        // 1. Guillotine and electric chair gizmos write the plain field Activated: lambda 0 = cancel, lambda 2 = the
        //    confirm-dialog callback (lambda 1 only opens the dialog). Sync only "<>4__this": "<>9__2" is a cached Action.
        // 2. For humans the mod's prefix replaces vanilla StudyUtility.TargetHoldingPlatformForEntity, so MP's synced vanilla
        //    lambda never runs. Lambda 0 is the targeter action (writes targetHolder, orders the carrier). carrier and
        //    sourcePlatform are null for the Transfer gizmo, so no CancelIfAnyFieldNull.
        // 3. The mod's gizmo postfix hides MP's synced vanilla "Cancel transfer" for human prisoners: lambda 0 is its own cancel.
        //    Lambda 1 only opens the targeter (UI), its callback is item 2.
        // 4. WorkGiver_Warden_TakeToBed_JobOnThing_Patch.Prefix clears targetHolder when the prisoner is in its target bed.
        //    The right-click menu runs JobOnThing in the interface, so that write happened on one PC. Skip it in the interface:
        //    the next warden scan in the tick clears it on every PC.
        // 5. FeedPatientUtility.ShouldBeFed is true for restrained prisoners the mod already makes warden-fed. DBH water (thirst
        //    on) and Alpha Genes mineral delivery use this predicate, and nobody served those prisoners. Only this faction's
        //    prisoners (multifaction: IsPrisonerOfColony is true for both player factions), only spawned and in bed.
        // 6. Cage top graphic cycle gizmo (CompCageTopGraphicVariation lambda 0, saved field graphicIndex).
        // Each part is wrapped on its own, so a mod update that breaks one part does not drop the others.
        private static Func<Pawn, bool> isRestrained;

        public CaptureExpansion(ModContentPack mod)
        {
            Part("guillotine and electric chair gizmos", () =>
            {
                MpCompat.RegisterLambdaDelegate("CaptureExpansion.CompGuillotine", "CompGetGizmosExtra", ["<>4__this"], 0, 2);
                MpCompat.RegisterLambdaDelegate("CaptureExpansion.CompElectricChair", "CompGetGizmosExtra", ["<>4__this"], 0, 2);
            });
            Part("capture/transfer targeter", () =>
                MpCompat.RegisterLambdaDelegate("CaptureExpansion.StudyUtility_TargetHoldingPlatformForEntity_Patch", "Prefix",
                    ["carrier", "pawn", "sourcePlatform"], 0)[0].CancelIfFieldsNull("pawn"));
            Part("cancel transfer gizmo", () =>
                MpCompat.RegisterLambdaDelegate("CaptureExpansion.CompHoldingPlatformTarget_CompGetGizmosExtra_Patch", "Postfix",
                    ["__instance"], 0)[0].CancelIfAnyFieldNull());
            Part("cage top graphic gizmo", () =>
                MpCompat.RegisterLambdaMethod("CaptureExpansion.CompCageTopGraphicVariation", "CompGetGizmosExtra", 0));
            Part("right-click targetHolder clear", () =>
                MpCompat.harmony.Patch(Required(AccessTools.DeclaredMethod("CaptureExpansion.WorkGiver_Warden_TakeToBed_JobOnThing_Patch:Prefix")),
                    transpiler: new HarmonyMethod(typeof(CaptureExpansion), nameof(SkipTargetClearInInterface))));
            Part("restrained prisoner feed predicate", () =>
            {
                isRestrained = AccessTools.MethodDelegate<Func<Pawn, bool>>(Required(AccessTools.DeclaredMethod("CaptureExpansion.State:IsRestrained")));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(FeedPatientUtility), nameof(FeedPatientUtility.ShouldBeFed)),
                    postfix: new HarmonyMethod(typeof(CaptureExpansion), nameof(PostShouldBeFed)));
            });
        }

        private static void Part(string name, Action register)
        {
            try
            {
                register();
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Capture Expansion: part '{name}' not loaded (mod changed?): {e}");
            }
        }

        private static MethodInfo Required(MethodInfo method)
            => method ?? throw new MissingMethodException("method not found");

        private static IEnumerable<CodeInstruction> SkipTargetClearInInterface(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var field = AccessTools.DeclaredField(typeof(CompHoldingPlatformTarget), nameof(CompHoldingPlatformTarget.targetHolder));
            var replacement = AccessTools.DeclaredMethod(typeof(CaptureExpansion), nameof(SetTargetHolder));
            var replaced = 0;

            foreach (var ci in instr)
            {
                if (ci.StoresField(field))
                {
                    ci.opcode = System.Reflection.Emit.OpCodes.Call;
                    ci.operand = replacement;
                    replaced++;
                }

                yield return ci;
            }

            const int expected = 1;
            if (replaced != expected)
                Log.Error($"MPCompat :: Capture Expansion: replaced {replaced} targetHolder writes in {baseMethod.DeclaringType?.Name}:{baseMethod.Name}, expected {expected}. Mod changed?");
        }

        private static void SetTargetHolder(CompHoldingPlatformTarget comp, Thing value)
        {
            if (MP.IsInMultiplayer && MP.InInterface)
                return;
            comp.targetHolder = value;
        }

        private static void PostShouldBeFed(Pawn p, ref bool __result)
        {
            if (!__result && p.HostFaction != null && p.HostFaction == Faction.OfPlayer && p.Spawned && p.InBed() &&
                p.needs?.food != null && isRestrained(p) && WardenFeedUtility.ShouldBeFed(p))
                __result = true;
        }
    }
}
