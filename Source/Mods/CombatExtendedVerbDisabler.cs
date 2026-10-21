using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - the per-verb "Hold fire" toggles of CIWS turrets are synced</summary>
    /// <remarks>
    /// FIXES row 168 (21 Oct 2026, live save-state checksum run SS-F2 at MP 12,500): <c>CompVerbDisabler.CompGetGizmosExtra</c>
    /// (Comps/CompVerbDisabler.cs:31-38) gives every <c>IVerbDisableable</c> verb (VerbCIWS: "Hold fire - skyfallers" / projectiles)
    /// a <c>Command_Toggle</c> whose <c>toggleAction</c> flips <c>HoldFire</c> directly, with no sync point; <c>VerbCIWS.Active</c>
    /// (Verbs/VerbCIWS.cs:55) reads it in the simulation and it is saved. The lab fuzzer's press on Turret_KPV25421 changed it on the
    /// host only. In MP each such toggle calls <see cref="SyncedSetHoldFire"/> (the comp, the verb's index in its CompEquippable, the
    /// new value) instead, so every PC flips the same verb at the same tick. Single player is unchanged.
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedVerbDisabler
    {
        private static PropertyInfo holdFireProp;

        public CombatExtendedVerbDisabler(ModContentPack mod)
        {
            var compType = AccessTools.TypeByName("CombatExtended.CompVerbDisabler")
                           ?? throw new MissingMemberException("CombatExtended.CompVerbDisabler");
            var iface = AccessTools.TypeByName("CombatExtended.IVerbDisableable")
                        ?? throw new MissingMemberException("CombatExtended.IVerbDisableable");
            holdFireProp = AccessTools.Property(iface, "HoldFire")
                           ?? throw new MissingMemberException(iface.FullName, "HoldFire");
            MpCompat.harmony.Patch(AccessTools.Method(compType, "CompGetGizmosExtra"),
                postfix: new HarmonyMethod(typeof(CombatExtendedVerbDisabler), nameof(GizmosPostfix)));
            MP.RegisterSyncMethod(typeof(CombatExtendedVerbDisabler), nameof(SyncedSetHoldFire));
        }

        private static IEnumerable<Gizmo> GizmosPostfix(IEnumerable<Gizmo> __result, ThingComp __instance)
        {
            foreach (var gizmo in __result)
            {
                if (MP.IsInMultiplayer && gizmo is Command_Toggle toggle && toggle.toggleAction?.Target is { } closure)
                {
                    var verbField = AccessTools.Field(closure.GetType(), "disableableVerb");
                    if (verbField?.GetValue(closure) is Verb verb)
                    {
                        var verbs = __instance.parent.GetComp<CompEquippable>()?.AllVerbs;
                        int index = verbs?.IndexOf(verb) ?? -1;
                        if (index >= 0)
                        {
                            var comp = __instance;
                            toggle.toggleAction = () => SyncedSetHoldFire(comp, index, !(bool)holdFireProp.GetValue(verb));
                        }
                    }
                }
                yield return gizmo;
            }
        }

        private static void SyncedSetHoldFire(ThingComp comp, int verbIndex, bool value)
        {
            var verbs = comp?.parent?.GetComp<CompEquippable>()?.AllVerbs;
            if (verbs == null || verbIndex < 0 || verbIndex >= verbs.Count) return;
            var verb = verbs[verbIndex];
            if (holdFireProp.DeclaringType.IsInstanceOfType(verb))
                holdFireProp.SetValue(verb, value);
        }
    }
}
