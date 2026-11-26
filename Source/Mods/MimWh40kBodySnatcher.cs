using System;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>MIM - WH40k Core: body snatcher draw prefixes (FIXES row 390)</summary>
    /// <remarks>
    /// EMWH_PawnBodySnatcher patches PawnRenderNode_Body/Head.GraphicFor and HumanlikeMeshPoolUtility.GetHumanlikeHairSetForPawn.
    /// The prefixes (ApplyAppearanceModifications, ApplyHeadModifications, hair scaling) write the saved story.bodyType, story.hairDef,
    /// story.headType and style.beardDef / FaceTattoo / BodyTattoo, so only a PC that draws the pawn changes the saved state; the
    /// arbiter and a PC that never looks at the pawn keep the old values, and Pawn_StyleTracker's Rand gate (HasAnyUnwantedStyleItem)
    /// then splits the Rand stream. Fix: drawing path only. A First-priority prefix snapshots the 6 fields and a Last-priority
    /// finalizer puts them back, so the mod still draws with its values (its postfix runs before the finalizer) but nothing it writes
    /// stays in the pawn. The saved state is then the same on every PC whichever PC draws.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("emitbreaker.MIM.WH40k.Core")]
    internal class MimWh40kBodySnatcher
    {
        private const string LogPrefix = "[MPCompat - MIM body snatcher] ";

        private class Snapshot
        {
            public BodyTypeDef bodyType;
            public HairDef hairDef;
            public HeadTypeDef headType;
            public BeardDef beardDef;
            public TattooDef faceTattoo;
            public TattooDef bodyTattoo;
        }

        // FIXES row 464: Utility_WolfenMutation.DoMutationConsideration rolls Wulfen / Long Fang / Space Wolves with a
        // time-seeded private static System.Random, so every PC rolls a different class. It is called from
        // Hediff_Mutation_WulfenNPC.Tick -> DoMutation (hediff tick, in the simulation). In Multiplayer the roll now comes from Verse.Rand
        // through PatchingUtilities.RandRedirector. The Rubric copy only rolls Next(1,100) against a limit it never passes: not patched.
        private static readonly System.Reflection.FieldInfo wolfenRand = AccessTools.Field(AccessTools.TypeByName("Utility_WolfenMutation"), "rand");

        private static void PatchWolfenRand()
        {
            if (LabOff.Row(464))
            {
                Log.Message(LogPrefix + "row 464 switched off by the lab, Wulfen roll still uses System.Random");
                return;
            }
            var method = AccessTools.Method("Utility_WolfenMutation:DoMutationConsideration");
            if (method == null || wolfenRand == null || wolfenRand.FieldType != typeof(Random))
            {
                Log.Error(LogPrefix + "row 464: Utility_WolfenMutation.DoMutationConsideration or its rand field not found, the Wulfen roll can desync");
                return;
            }
            MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MimWh40kBodySnatcher), nameof(UseRandRedirector)));
            Log.Message(LogPrefix + "row 464: Wulfen / Long Fang / Space Wolves roll uses Verse.Rand in Multiplayer");
        }

        internal static System.Collections.Generic.IEnumerable<CodeInstruction> UseRandRedirector(System.Collections.Generic.IEnumerable<CodeInstruction> instr)
        {
            var helper = AccessTools.Method(typeof(MimWh40kBodySnatcher), nameof(GetWolfenRand));
            var found = false;
            foreach (var ci in instr)
            {
                if (ci.opcode == System.Reflection.Emit.OpCodes.Ldsfld && ci.operand as System.Reflection.FieldInfo == wolfenRand)
                {
                    ci.opcode = System.Reflection.Emit.OpCodes.Call;
                    ci.operand = helper;
                    found = true;
                }
                yield return ci;
            }
            if (!found) Log.Error(LogPrefix + "row 464: no read of the rand field in DoMutationConsideration, the Wulfen roll can desync");
        }

        internal static Random GetWolfenRand() => MP.IsInMultiplayer ? PatchingUtilities.RandRedirector.Instance : (Random)wolfenRand.GetValue(null);

        public MimWh40kBodySnatcher(ModContentPack mod)
        {
            PatchWolfenRand();
            try
            {
                var targets = new[]
                {
                    AccessTools.DeclaredMethod(typeof(PawnRenderNode_Body), "GraphicFor", [typeof(Pawn)]),
                    AccessTools.DeclaredMethod(typeof(PawnRenderNode_Head), "GraphicFor", [typeof(Pawn)]),
                    AccessTools.DeclaredMethod(typeof(HumanlikeMeshPoolUtility), "GetHumanlikeHairSetForPawn", [typeof(Pawn), typeof(float), typeof(float)]),
                };
                foreach (var t in targets)
                    if (t == null)
                    {
                        Log.Warning(LogPrefix + "a draw method was not found, the guard is not installed.");
                        return;
                    }
                var pre = new HarmonyMethod(typeof(MimWh40kBodySnatcher), nameof(PreSnapshot)) { priority = Priority.First };
                var fin = new HarmonyMethod(typeof(MimWh40kBodySnatcher), nameof(FinRestore)) { priority = Priority.Last };
                foreach (var t in targets)
                    MpCompat.harmony.Patch(t, prefix: pre, finalizer: fin);
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "patching failed: " + e);
            }
        }

        private static void PreSnapshot(Pawn pawn, out Snapshot __state)
        {
            __state = null;
            if (LabOff.Row(390)) return; // lab switch row 390: off = old behaviour
            if (!MP.IsInMultiplayer || pawn?.story == null)
                return;
            var style = pawn.style;
            __state = new Snapshot
            {
                bodyType = pawn.story.bodyType,
                hairDef = pawn.story.hairDef,
                headType = pawn.story.headType,
                beardDef = style?.beardDef,
                faceTattoo = style?.FaceTattoo,
                bodyTattoo = style?.BodyTattoo,
            };
        }

        private static void FinRestore(Pawn pawn, Snapshot __state)
        {
            if (LabOff.Row(390)) return;
            if (__state == null || pawn?.story == null)
                return;
            var story = pawn.story;
            if (story.bodyType != __state.bodyType) story.bodyType = __state.bodyType;
            if (story.hairDef != __state.hairDef) story.hairDef = __state.hairDef;
            if (story.headType != __state.headType) story.headType = __state.headType;
            var style = pawn.style;
            if (style == null)
                return;
            if (style.beardDef != __state.beardDef) style.beardDef = __state.beardDef;
            if (style.FaceTattoo != __state.faceTattoo) style.FaceTattoo = __state.faceTattoo;
            if (style.BodyTattoo != __state.bodyTattoo) style.BodyTattoo = __state.bodyTattoo;
        }
    }
}
