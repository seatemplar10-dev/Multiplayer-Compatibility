using System;
using System.Collections;
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
    /// <summary>Gravship Fleet (asunib.gravshipfleet). FleetLauncher.AdvanceAirborne (Postfix after the map draw, once per frame) does
    /// launch.timeLeft -= Time.deltaTime, so the cutscene timer that decides when Depart() runs (a sim change) follows each PC's frame
    /// rate. In multiplayer the per-frame subtraction is replaced by one fixed step per game tick (TickManager.DoSingleTick postfix).
    /// Depart() and SetStage() calls inside AdvanceAirborne are no-ops in multiplayer (frame path draws only); the same stage decision
    /// (no gravship -> Failed, resumed or timeLeft &lt;= 0 -> Depart) runs in the DoSingleTick postfix, so every PC launches at the same tick.</summary>
    [MpCompatFor("asunib.gravshipfleet")]
    public class GravshipFleetCust1
    {
        private const float Step = 1f / 60f;
        private static FieldInfo launchesField, stageField, resumedField, timeLeftField;
        private static MethodInfo liveGetter, departM, setStageM;
        private static FieldInfo gravshipField;
        private static object airborne, failed;
        public static int replaced;

        public GravshipFleetCust1(ModContentPack mod)
        {
            var launcher = AccessTools.TypeByName("GravshipFleet.FleetLauncher");
            var launch = AccessTools.TypeByName("GravshipFleet.FleetLaunch");
            var stage = AccessTools.TypeByName("GravshipFleet.LaunchStage");
            launchesField = AccessTools.DeclaredField(launcher, "launches");
            liveGetter = AccessTools.DeclaredPropertyGetter(launcher, "Live");
            stageField = AccessTools.DeclaredField(launch, "stage");
            resumedField = AccessTools.DeclaredField(launch, "resumed");
            timeLeftField = AccessTools.DeclaredField(launch, "timeLeft");
            gravshipField = AccessTools.DeclaredField(launch, "gravship");
            departM = AccessTools.DeclaredMethod(launcher, "Depart", new[] { launch });
            setStageM = AccessTools.DeclaredMethod(launcher, "SetStage", new[] { launch, stage });
            failed = Enum.Parse(stage, "Failed");
            airborne = Enum.Parse(stage, "Airborne");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(launcher, "AdvanceAirborne"),
                transpiler: new HarmonyMethod(typeof(GravshipFleetCust1), nameof(Transpile)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                postfix: new HarmonyMethod(typeof(GravshipFleetCust1), nameof(TickPostfix)));
            Log.Message("[MPCompat - FULL1127 CU] Gravship Fleet: cutscene timer per game tick registered (deltaTime x" + replaced + ")");
        }

        public static int wrapped;
        public static void FrameDepart(object launcher, object launch) { if (!MP.IsInMultiplayer) departM.Invoke(launcher, new[] { launch }); }
        public static void FrameSetStage(object launcher, object launch, int stage) { if (!MP.IsInMultiplayer) setStageM.Invoke(launcher, new[] { launch, Enum.ToObject(setStageM.GetParameters()[1].ParameterType, stage) }); }

        public static float FrameDelta() => MP.IsInMultiplayer ? 0f : Time.deltaTime;

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var dt = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            foreach (var ins in instructions)
            {
                if (ins.Calls(dt))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GravshipFleetCust1), nameof(FrameDelta))) { labels = ins.labels, blocks = ins.blocks };
                }
                else if (ins.Calls(departM))
                {
                    wrapped++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GravshipFleetCust1), nameof(FrameDepart))) { labels = ins.labels, blocks = ins.blocks };
                }
                else if (ins.Calls(setStageM))
                {
                    wrapped++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GravshipFleetCust1), nameof(FrameSetStage))) { labels = ins.labels, blocks = ins.blocks };
                }
                else yield return ins;
            }
        }

        private static void TickPostfix()
        {
            if (!MP.IsInMultiplayer) return;
            var live = liveGetter.Invoke(null, null);
            if (live == null) return;
            if (!(launchesField.GetValue(live) is IEnumerable list)) return;
            var snapshot = new List<object>();
            foreach (var l in list) snapshot.Add(l);
            foreach (var l in snapshot)
            {
                if (l == null || !Equals(stageField.GetValue(l), airborne)) continue;
                if (gravshipField.GetValue(l) == null) { setStageM.Invoke(live, new[] { l, failed }); continue; }
                var resumed = (bool)resumedField.GetValue(l);
                if (!resumed) timeLeftField.SetValue(l, (float)timeLeftField.GetValue(l) - Step);
                if (resumed || (float)timeLeftField.GetValue(l) <= 0f) departM.Invoke(live, new[] { l });
            }
        }
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Planning Extended (scherub.planningextended). BaseShapePlanDesignator.DesignateMultiCell reads UI.MouseMapPosition(),
    /// key-held flags, the picked colour and the selected shape/variant, all per PC, and the designators are not synced by MP (the
    /// override is not the Designator base method). In multiplayer the local call is turned into one synced call that carries the drag
    /// cells, the mouse cell, the colour, the shape, the variant and the key flags; every PC then forces that state on its own designator
    /// instance and replays the original DesignateMultiCell (mouse read replaced by the carried cell).</summary>
    [MpCompatFor("Scherub.PlanningExtended")]
    public class PlanningExtendedCust1
    {
        private static Type shapeDes, planDes, colorDes, shapeBase;
        private static FieldInfo fCenter, fSkip, fModifier, fPick, fColor, fShape;
        private static MethodInfo mSelectShape, mCellsSrc;
        [ThreadStatic] private static bool replaying;
        [ThreadStatic] private static IntVec3 replayMouse;
        public static int replaced;

        public PlanningExtendedCust1(ModContentPack mod)
        {
            shapeDes = AccessTools.TypeByName("PlanningExtended.Designators.BaseShapePlanDesignator");
            planDes = AccessTools.TypeByName("PlanningExtended.Designators.BasePlanDesignator");
            colorDes = AccessTools.TypeByName("PlanningExtended.Designators.BaseColorPlanDesignator");
            shapeBase = AccessTools.TypeByName("PlanningExtended.Shapes.BaseShape");
            fCenter = AccessTools.DeclaredField(planDes, "<IsCenterModeKeyPressed>k__BackingField");
            fSkip = AccessTools.DeclaredField(planDes, "<IsSkipExistingDesignationsKeyPressed>k__BackingField");
            fModifier = AccessTools.DeclaredField(planDes, "<IsModifierKeyPressed>k__BackingField");
            fPick = AccessTools.DeclaredField(colorDes, "<IsColorPickModeEnabled>k__BackingField");
            fColor = AccessTools.DeclaredField(colorDes, "colorDef");
            fShape = AccessTools.DeclaredField(shapeDes, "<SelectedShape>k__BackingField");
            mSelectShape = AccessTools.DeclaredMethod(shapeDes, "SelectShape");
            var dmc = AccessTools.DeclaredMethod(shapeDes, "DesignateMultiCell");
            MpCompat.harmony.Patch(dmc,
                prefix: new HarmonyMethod(typeof(PlanningExtendedCust1), nameof(Prefix)),
                transpiler: new HarmonyMethod(typeof(PlanningExtendedCust1), nameof(Transpile)));
            MP.RegisterSyncMethod(typeof(PlanningExtendedCust1), nameof(Replay));
            Log.Message("[MPCompat - FULL1127 CU] Planning Extended: designate cells + state synced registered (mouse reads x" + replaced + ")");
        }

        public static Vector3 MouseVec()
            => replaying ? new Vector3(replayMouse.x, 0f, replayMouse.z) : UI.MouseMapPosition();

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var mouse = AccessTools.Method(typeof(UI), nameof(UI.MouseMapPosition));
            foreach (var ins in instructions)
            {
                if (ins.Calls(mouse))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlanningExtendedCust1), nameof(MouseVec))) { labels = ins.labels, blocks = ins.blocks };
                }
                else yield return ins;
            }
        }

        private static bool Prefix(Designator __instance, IEnumerable<IntVec3> cells)
        {
            if (!MP.IsInMultiplayer || replaying) return true;
            var shape = fShape.GetValue(__instance);
            var shapeId = (int)shapeBase.GetProperty("Shape").GetValue(shape);
            var variantObj = shapeBase.GetProperty("SelectedShapeVariant").GetValue(shape);
            var variantId = (int)variantObj.GetType().GetProperty("ShapeVariant").GetValue(variantObj);
            var mouse = new IntVec3(UI.MouseMapPosition());
            Replay(__instance.GetType().FullName, new List<IntVec3>(cells), mouse,
                colorDes.IsInstanceOfType(__instance) ? (ColorDef)fColor.GetValue(__instance) : null,
                shapeId, variantId,
                ((bool)fCenter.GetValue(__instance) ? 1 : 0) | ((bool)fSkip.GetValue(__instance) ? 2 : 0) | ((bool)fModifier.GetValue(__instance) ? 4 : 0)
                | (colorDes.IsInstanceOfType(__instance) && (bool)fPick.GetValue(__instance) ? 8 : 0));
            return false;
        }

        // Runs on every PC (MP SyncMethod). All arguments come from the PC that issued the command.
        public static void Replay(string designatorType, List<IntVec3> cells, IntVec3 mouse, ColorDef color, int shapeId, int variantId, int flags)
        {
            // flags: 1 centre mode, 2 skip existing, 4 modifier key, 8 colour pick mode (fewer than 8 parameters: Harmony's MethodInvoker cannot emit more)
            bool center = (flags & 1) != 0, skip = (flags & 2) != 0, modifier = (flags & 4) != 0, pick = (flags & 8) != 0;
            Designator des = FindDesignator(designatorType);
            if (des == null) { Log.Warning("[MPCompat - FULL1127 CU] Planning Extended: designator " + designatorType + " not found"); return; }
            fCenter.SetValue(des, center); fSkip.SetValue(des, skip); fModifier.SetValue(des, modifier);
            if (colorDes.IsInstanceOfType(des)) { fColor.SetValue(des, color); fPick.SetValue(des, pick); }
            var shape = fShape.GetValue(des);
            if ((int)shapeBase.GetProperty("Shape").GetValue(shape) != shapeId)
            {
                mSelectShape.Invoke(des, new object[] { Enum.ToObject(AccessTools.TypeByName("PlanningExtended.Shape"), shapeId) });
                shape = fShape.GetValue(des);
            }
            shapeBase.GetMethod("SelectShapeVariant").Invoke(shape, new object[] { Enum.ToObject(AccessTools.TypeByName("PlanningExtended.ShapeVariant"), variantId) });
            replaying = true; replayMouse = mouse;
            try { des.DesignateMultiCell(cells); }
            finally { replaying = false; }
        }

        private static Designator FindDesignator(string typeName)
        {
            foreach (var cat in DefDatabase<DesignationCategoryDef>.AllDefsListForReading)
                foreach (var d in cat.ResolvedAllowedDesignators)
                    if (d.GetType().FullName == typeName) return d;
            return null;
        }
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Better Research Menu (ferny.noderesearch). ResearchQueueGroups.Sync rewrites the saved research queue (ResearchQueue.DefNames)
    /// that ResearchQueue.StartNext turns into the active project, so it reaches the sim and the save. It had a Time.realtimeSinceStartup
    /// 0.4 s throttle (skips at 3x speed and above, differs per PC) and a second caller in the research window (per PC UI). In multiplayer:
    /// the tick caller (QueueTicker.GameComponentTick, every 60 ticks) always runs, the window caller does nothing.</summary>
    [MpCompatFor("ferny.noderesearch")]
    public class BetterResearchMenuCust1
    {
        [ThreadStatic] private static bool inTick;
        public BetterResearchMenuCust1(ModContentPack mod)
        {
            var groups = AccessTools.TypeByName("BetterResearchMenu.ResearchQueueGroups");
            var ticker = AccessTools.TypeByName("BetterResearchMenu.QueueTicker");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(ticker, "GameComponentTick"),
                prefix: new HarmonyMethod(typeof(BetterResearchMenuCust1), nameof(TickPre)),
                finalizer: new HarmonyMethod(typeof(BetterResearchMenuCust1), nameof(TickFin)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(groups, "Sync"),
                prefix: new HarmonyMethod(typeof(BetterResearchMenuCust1), nameof(SyncPre)));
            Log.Message("[MPCompat - FULL1127 CU] Better Research Menu: queue group Sync per tick registered");
        }
        private static void TickPre() { inTick = true; }
        private static void TickFin() { inTick = false; }
        private static bool SyncPre(ref bool force)
        {
            if (!MP.IsInMultiplayer) return true;
            if (!inTick) return false;
            force = true;
            return true;
        }
    }
}
