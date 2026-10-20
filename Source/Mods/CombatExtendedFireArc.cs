using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - turret arc of fire changes are synced</summary>
    /// <remarks>
    /// FIXES row 136 (20 Oct 2026; row 58 = the lead for row 40, lab mp51). CE syncs its own turret gizmos
    /// (<c>Building_TurretGunCE.OrderAttack</c>, <c>ToggleHoldFire</c>, <c>SyncedResetForcedTarget</c> carry
    /// <c>[Compatibility.Multiplayer.SyncMethod]</c>), but not <c>CompFireArc</c>. Three paths changed the arc on the
    /// clicking PC only:
    /// <list type="bullet">
    /// <item>the overlay left-click (<c>CompFireArc.PostDrawExtraSelectionOverlays</c>, CompFireArc.cs:134-144): sets
    /// <c>CurrentCenterAngle</c> / <c>CurrentSpan</c> and calls <c>Building_TurretGunCE.PostAdjustFireArc</c>, which clears
    /// <c>currentTargetInt</c> / <c>forcedTarget</c> and calls <c>TryFindNewTarget</c>;</item>
    /// <item>the arc gizmo (<c>CompGetGizmosExtra</c> lambda 0, :218-238): with the eyedropper key down it toggles
    /// <c>turnedOn</c> or resets the arc, else it starts editing and sets <c>turnedOn = true</c>;</item>
    /// <item><c>FireArcCopyPaster.PasteTo</c> (:369-373), from the paste gizmo.</item>
    /// </list>
    /// <c>Building_TurretGunCE.Tick</c> resets a forced / current target outside the arc (Building_TurretGunCE.cs:376-378) and
    /// <c>WithinFireArc</c> gates target search, so the arc decides targets on every PC. Live only while CE's "arc of fire"
    /// setting (<c>enableArcOfFire</c>, default off) is on.
    /// Fix: ONE synced setter (<see cref="SyncedSetFireArc"/>: center angle, span, turnedOn, + whether to run
    /// <c>PostAdjustFireArc</c>) that all three paths call in MP; <c>PostAdjustFireArc</c> runs inside the synced call on
    /// every PC. The editing state (<c>Editing</c>, <c>NewSpan</c>, the mouse) stays local, as in CE. Single player is
    /// unchanged (every helper falls through to CE's own code).
    /// Also (found while building this, same class): the simulation's arc test <c>CompFireArc.WithinFireArc</c> (:99-107)
    /// reads <c>CenterAngle</c> / <c>Span</c>, which are <c>NewCenterAngle</c> / <c>NewSpan</c> (the mouse) while THIS PC is
    /// editing, and the <c>supplementarySpan</c> cache that only the <c>SupplementarySpan</c> getter fills while editing
    /// (:74-91) - so between the gizmo press and the click, and after it, the arc test differs per PC. In MP, outside the
    /// interface, <see cref="WithinSyncedArc"/> answers it from the synced state only (current angle / span, turnedOn), with
    /// the supplementary range (an arc across +-180 degrees) computed the way the getter does; the interface (the eyedropper
    /// preview while editing) keeps CE's own code.
    /// Not covered: CE does not save <c>turnedOn</c> (<c>PostExposeData</c> scribes only the angle and the span), so a PC
    /// that loads a save made while an arc was switched off starts with it on.
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedFireArc
    {
        private const string LogPrefix = "[MPCompat - CE fire arc] ";

        private static Type compType;
        private static Type turretType;
        private static FieldInfo pasterCenterAngle;
        private static FieldInfo pasterSpan;
        private static MethodInfo propsGetter;
        private static MethodInfo canTurnOffGetter;
        private static MethodInfo functionEnabledGetter;
        private static MethodInfo postAdjustFireArc;
        private static AccessTools.FieldRef<object, float> currentCenterAngle;
        private static AccessTools.FieldRef<object, float> currentSpan;
        private static AccessTools.FieldRef<object, float> newSpan;
        private static AccessTools.FieldRef<object, float> newCenterAngle;
        private static AccessTools.FieldRef<object, bool> turnedOn;
        private static AccessTools.FieldRef<object, bool> editing;
        private static AccessTools.FieldRef<object, FloatRange> spanRange;
        private static AccessTools.FieldRef<object, float> maxSpanDeviation;
        // No per-PC static state here on purpose: lab full-state checks walk every mod assembly's statics.

        public CombatExtendedFireArc(ModContentPack mod)
        {
            Init();
            MP.RegisterSyncMethod(typeof(CombatExtendedFireArc), nameof(SyncedSetFireArc)).CancelIfAnyArgNull();
            Patch(MpCompat.harmony);
        }

        // Types and members by name (MP-Compat has no compile-time reference to CE). Throws when one is missing.
        internal static void Init()
        {
            compType = AccessTools.TypeByName("CombatExtended.CompFireArc")
                       ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.CompFireArc not found");
            turretType = AccessTools.TypeByName("CombatExtended.Building_TurretGunCE")
                         ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.Building_TurretGunCE not found");
            var paster = AccessTools.TypeByName("CombatExtended.FireArcCopyPaster")
                         ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.FireArcCopyPaster not found");
            var propsType = AccessTools.TypeByName("CombatExtended.CompProperties_FireArc")
                            ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.CompProperties_FireArc not found");
            pasterCenterAngle = AccessTools.DeclaredField(paster, "CenterAngle") ?? throw new MissingFieldException(paster.FullName, "CenterAngle");
            pasterSpan = AccessTools.DeclaredField(paster, "Span") ?? throw new MissingFieldException(paster.FullName, "Span");
            propsGetter = AccessTools.DeclaredPropertyGetter(compType, "Props") ?? throw new MissingMethodException(compType.FullName, "get_Props");
            canTurnOffGetter = AccessTools.DeclaredPropertyGetter(compType, "canTurnOff") ?? throw new MissingMethodException(compType.FullName, "get_canTurnOff");
            functionEnabledGetter = AccessTools.DeclaredPropertyGetter(compType, "functionEnabled") ?? throw new MissingMethodException(compType.FullName, "get_functionEnabled");
            postAdjustFireArc = AccessTools.DeclaredMethod(turretType, "PostAdjustFireArc", Type.EmptyTypes)
                                ?? throw new MissingMethodException(turretType.FullName, "PostAdjustFireArc");
            currentCenterAngle = AccessTools.FieldRefAccess<float>(compType, "CurrentCenterAngle");
            currentSpan = AccessTools.FieldRefAccess<float>(compType, "CurrentSpan");
            newSpan = AccessTools.FieldRefAccess<float>(compType, "NewSpan");
            newCenterAngle = AccessTools.FieldRefAccess<float>(compType, "NewCenterAngle");
            turnedOn = AccessTools.FieldRefAccess<bool>(compType, "turnedOn");
            editing = AccessTools.FieldRefAccess<bool>(compType, "Editing");
            spanRange = AccessTools.FieldRefAccess<FloatRange>(propsType, "spanRange");
            maxSpanDeviation = AccessTools.FieldRefAccess<float>(propsType, "maxSpanDeviation");
        }

        // The three click paths (separate from the constructor so an offline check can patch without Multiplayer loaded).
        internal static void Patch(Harmony harmony)
        {
            var overlay = AccessTools.DeclaredMethod(compType, "PostDrawExtraSelectionOverlays", Type.EmptyTypes)
                          ?? throw new MissingMethodException(compType.FullName, "PostDrawExtraSelectionOverlays");
            harmony.Patch(overlay, transpiler: new HarmonyMethod(typeof(CombatExtendedFireArc), nameof(RouteOverlayClick)));

            // CompGetGizmosExtra lambda 0 = the "adjust arc of fire" gizmo action (lab CE 1.7: CompFireArc.<CompGetGizmosExtra>b__50_0)
            var arcGizmo = MpMethodUtil.GetLambda(compType, "CompGetGizmosExtra", lambdaOrdinal: 0);
            harmony.Patch(arcGizmo, prefix: new HarmonyMethod(typeof(CombatExtendedFireArc), nameof(PreArcGizmo)));

            var pasteTo = AccessTools.DeclaredMethod(pasterCenterAngle.DeclaringType, "PasteTo")
                          ?? throw new MissingMethodException(pasterCenterAngle.DeclaringType?.FullName, "PasteTo");
            harmony.Patch(pasteTo, prefix: new HarmonyMethod(typeof(CombatExtendedFireArc), nameof(PrePasteTo)));

            // the simulation's arc test (164 bytes of IL: not inlined into its Building_TurretGunCE callers)
            var within = AccessTools.DeclaredMethod(compType, "WithinFireArc", new[] { typeof(LocalTargetInfo) })
                         ?? throw new MissingMethodException(compType.FullName, "WithinFireArc");
            harmony.Patch(within, prefix: new HarmonyMethod(typeof(CombatExtendedFireArc), nameof(PreWithinFireArc)));
        }

        // Outside the interface in MP (ticks, synced commands): the arc test from the synced state only.
        private static bool PreWithinFireArc(ThingComp __instance, LocalTargetInfo tgt, ref bool __result)
        {
            if (!MP.IsInMultiplayer || MP.InInterface)
                return true;
            __result = WithinSyncedArc(__instance, tgt);
            return false;
        }

        // CE's WithinFireArc (CompFireArc.cs:99-107) with CurrentCenterAngle / CurrentSpan in place of CenterAngle / Span and the
        // supplementary range computed like the SupplementarySpan getter (:80-87) instead of read from its per-PC cache.
        internal static bool WithinSyncedArc(ThingComp comp, LocalTargetInfo tgt)
        {
            if (!(bool)functionEnabledGetter.Invoke(comp, null))
                return true;
            float maxDev = maxSpanDeviation(propsGetter.Invoke(comp, null));
            float center = currentCenterAngle(comp), span = currentSpan(comp);
            float left = Mathf.Max(center - span / 2f, -maxDev);
            float right = Mathf.Min(center + span / 2f, maxDev);
            Thing parent = comp.parent;
            float angle = Vector3.SignedAngle(Vector3.forward.RotatedBy(parent.Rotation.AsAngle), (tgt.CenterVector3 - parent.DrawPos).Yto0(), Vector3.up);
            if (angle >= left && angle <= right)
                return true;
            if (left < -180f)
                return angle >= left + 360f && angle <= 180f;
            if (right > 180f)
                return angle >= -180f && angle <= right - 360f;
            return false;
        }

        // THE synced setter: every PC applies the same arc in the same command, and the turret drops its targets and looks
        // for a new one (PostAdjustFireArc) inside it when the change came from the overlay click.
        // NoInlining: Multiplayer patches this method (sync prefix) - its callers here must call it, never inline its body.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SyncedSetFireArc(ThingComp comp, float centerAngle, float span, bool on, bool postAdjust)
        {
            if (comp == null || !compType.IsInstanceOfType(comp))
                return;
            currentCenterAngle(comp) = centerAngle;
            currentSpan(comp) = span;
            turnedOn(comp) = on;
            if (postAdjust && turretType.IsInstanceOfType(comp.parent))
                postAdjustFireArc.Invoke(comp.parent, null);
        }

        // In MP, an arc change made in the interface goes through the synced setter instead of CE's direct writes.
        private static bool RouteThroughSync => MP.IsInMultiplayer && !MP.IsExecutingSyncCommand;

        // CE's click: "CurrentCenterAngle = NewCenterAngle; CurrentSpan = NewSpan;" (:136-137). In MP the center store is dropped and
        // the span store sends both: the center is the comp's NewCenterAngle, which the dropped store would have copied.
        private static void OverlayStoreCenter(ThingComp comp, float value)
        {
            if (!RouteThroughSync)
                currentCenterAngle(comp) = value;
        }

        private static void OverlayStoreSpan(ThingComp comp, float value)
        {
            if (RouteThroughSync)
                SyncedSetFireArc(comp, newCenterAngle(comp), value, true, true);   // editing set turnedOn = true (CE :236)
            else
                currentSpan(comp) = value;
        }

        private static void OverlayPostAdjust(Thing turret)
        {
            if (RouteThroughSync)
                return;   // runs inside SyncedSetFireArc on every PC
            postAdjustFireArc.Invoke(turret, null);
        }

        // PostDrawExtraSelectionOverlays: the left-click block's two stores and its PostAdjustFireArc call -> the helpers above.
        private static IEnumerable<CodeInstruction> RouteOverlayClick(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            int centers = 0, spans = 0, adjusts = 0;
            foreach (var ci in instr)
            {
                if (ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f && f.DeclaringType == compType)
                {
                    if (f.Name == "CurrentCenterAngle")
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = AccessTools.DeclaredMethod(typeof(CombatExtendedFireArc), nameof(OverlayStoreCenter));
                        centers++;
                    }
                    else if (f.Name == "CurrentSpan")
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = AccessTools.DeclaredMethod(typeof(CombatExtendedFireArc), nameof(OverlayStoreSpan));
                        spans++;
                    }
                }
                else if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m &&
                         m.Name == "PostAdjustFireArc" && m.DeclaringType == turretType)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.DeclaredMethod(typeof(CombatExtendedFireArc), nameof(OverlayPostAdjust));
                    adjusts++;
                }

                yield return ci;
            }

            if (centers != 1 || spans != 1 || adjusts != 1)
                Log.Error($"{LogPrefix}patched {centers} CurrentCenterAngle / {spans} CurrentSpan stores and {adjusts} PostAdjustFireArc calls in " +
                          $"{baseMethod.DeclaringType?.FullName}:{baseMethod.Name}, expected 1 / 1 / 1 - turret arcs of fire may desync");
        }

        // The arc gizmo action, both branches (CE CompFireArc.cs:220-237), with the writes to the synced state replaced.
        private static bool PreArcGizmo(ThingComp __instance)
        {
            if (!RouteThroughSync)
                return true;
            if (KeyBindingDefOf.ShowEyedropper.IsDown)
            {
                if ((bool)canTurnOffGetter.Invoke(__instance, null))
                    SyncedSetFireArc(__instance, currentCenterAngle(__instance), currentSpan(__instance), !turnedOn(__instance), false);
                else
                    SyncedSetFireArc(__instance, 0f, spanRange(propsGetter.Invoke(__instance, null)).max, turnedOn(__instance), false);
            }
            else
            {
                editing(__instance) = true;                       // local: this PC starts editing (overlay + mouse)
                newSpan(__instance) = currentSpan(__instance);
                SyncedSetFireArc(__instance, currentCenterAngle(__instance), currentSpan(__instance), true, false);
            }
            return false;
        }

        // FireArcCopyPaster.PasteTo: the copied values live on the clicking PC only (static clipboard), so they travel in the command.
        private static bool PrePasteTo(ThingComp comp)
        {
            if (!RouteThroughSync || comp == null)
                return true;
            object props = propsGetter.Invoke(comp, null);
            FloatRange range = spanRange(props);
            float maxDev = maxSpanDeviation(props);
            float span = Mathf.Clamp((float)pasterSpan.GetValue(null), range.min, range.max);
            float center = Mathf.Clamp((float)pasterCenterAngle.GetValue(null), -maxDev, maxDev);
            SyncedSetFireArc(comp, center, span, turnedOn(comp), false);
            return false;
        }
    }
}
