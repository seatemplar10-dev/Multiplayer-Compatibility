using System;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FULL1127 wave P2B: Roos Faun Xenotype. Three Verb_CastAbility.ValidateTarget overrides and
    /// RBSF_CompAbilityEffect_ChokevineGrasp.OnGizmoUpdate write the SHARED verbProps.range (the ability def's
    /// VerbProperties) from the caster stat. OnGizmoUpdate runs per frame for the pawn the local player selects,
    /// ValidateTarget for the targeter hover, so one PC's UI value could be seen by sim readers (Verb.EffectiveRange).
    /// In MP: OnGizmoUpdate is skipped, ValidateTarget restores the shared range when it returns, and
    /// Verb.EffectiveRange of these verbs is computed from this verb's caster. Every PC computes the same value.
    /// ChezhouLib (Register creates PawnKindDefs) is reachable only from DebugActions / level editor (dev), no code.
    /// Better Research Menu: FinalizeInit runs with the same Faction.OfPlayer on every PC (MP SaveLoad.cs), no code.
    /// </summary>
    [MpCompatFor("v.rooboid.faun")]
    class FullP2BRoosFaun
    {
        public const string Tag = "[MPCompat - FULL1127 P2B] ";
        const string Ns = "Roos_Faun_Xenotype.";
        static Type chokeVerb, treeVerb, sporeVerb;
        static StatDef natureConnection;

        public FullP2BRoosFaun(ModContentPack mod)
        {
            chokeVerb = AccessTools.TypeByName(Ns + "RBSF_Verb_CastAbilityChokevineGrasp");
            treeVerb = AccessTools.TypeByName(Ns + "RBSF_Verb_CastAbilityTreewalk");
            sporeVerb = AccessTools.TypeByName(Ns + "RBSF_Verb_CastAbilitySporeCloud");
            var gizmo = AccessTools.Method(Ns + "RBSF_CompAbilityEffect_ChokevineGrasp:OnGizmoUpdate");
            if (chokeVerb == null || treeVerb == null || sporeVerb == null || gizmo == null)
            {
                Log.Error(Tag + "Roos Faun Xenotype: type or method not found");
                return;
            }
            var harmony = MpCompat.harmony;
            harmony.Patch(gizmo, prefix: new HarmonyMethod(typeof(FullP2BRoosFaun), nameof(SkipInMp)));
            foreach (var t in new[] { chokeVerb, treeVerb, sporeVerb })
            {
                var m = AccessTools.DeclaredMethod(t, "ValidateTarget", new[] { typeof(LocalTargetInfo), typeof(bool) });
                if (m == null)
                {
                    Log.Error(Tag + "Roos Faun Xenotype: " + t.Name + ".ValidateTarget not found");
                    continue;
                }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(FullP2BRoosFaun), nameof(SaveRange)),
                    finalizer: new HarmonyMethod(typeof(FullP2BRoosFaun), nameof(RestoreRange)));
            }
            harmony.Patch(AccessTools.PropertyGetter(typeof(Verb), nameof(Verb.EffectiveRange)),
                postfix: new HarmonyMethod(typeof(FullP2BRoosFaun), nameof(PerCasterRange)));
            Log.Message(Tag + "Roos Faun Xenotype: per-caster ability range registered");
        }

        public static bool SkipInMp() => !MP.IsInMultiplayer;

        public static void SaveRange(Verb __instance, out float __state)
        {
            __state = MP.IsInMultiplayer ? __instance.verbProps.range : float.NaN;
        }

        public static Exception RestoreRange(Verb __instance, float __state, Exception __exception)
        {
            if (!float.IsNaN(__state)) __instance.verbProps.range = __state;
            return __exception;
        }

        // Same formulas as the mod's ValidateTarget / OnGizmoUpdate, from this verb's caster.
        static bool TryRange(Verb v, out float range)
        {
            range = 0f;
            var t = v.GetType();
            float a, b;
            if (t == chokeVerb) { a = 5f; b = 8f; }
            else if (t == sporeVerb) { a = 4f; b = 4f; }
            else if (t == treeVerb) { a = 0f; b = 32f; }
            else return false;
            natureConnection ??= DefDatabase<StatDef>.GetNamedSilentFail("RBSF_NatureConnection");
            var caster = v.caster;
            if (natureConnection == null || caster == null) return false;
            range = a + b * caster.GetStatValue(natureConnection);
            return true;
        }

        public static void PerCasterRange(Verb __instance, ref float __result)
        {
            if (!MP.IsInMultiplayer || __instance is not Verb_CastAbility) return;
            if (!TryRange(__instance, out var range)) return;
            var props = __instance.verbProps;
            var old = props.range;
            props.range = range;
            try { __result = props.AdjustedRange(__instance, __instance.Caster); }
            finally { props.range = old; }
        }
    }
}
