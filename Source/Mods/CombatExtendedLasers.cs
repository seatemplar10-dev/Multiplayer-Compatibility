using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - laser beams. Lab-found desync source (FIXES row 39, lab run mp50, 17 Oct 2026).
    ///
    /// CombatExtended.Lasers.LaserBeamCE.SpawnBeam spawns the beam's graphic thing at a cell chosen from the camera:
    /// <c>bool inView = Find.CameraDriver.CurrentViewRect.Contains(Origin.ToIntVec3());</c> -> the origin cell when the
    /// shooter is on screen, else the beam end <c>b</c>. The thing is a real map thing (thing id, spawn, tick), so two
    /// players looking at different places spawn it at different cells. In mp50 the beam end was off the map (200,0,108):
    /// the headless arbiter's spawn failed ("ThingsListAt out of bounds") while the host spawned it at the origin -> desync.
    ///
    /// Fix: in multiplayer the camera rect read there is replaced by a rect that contains every cell, so the beam always
    /// spawns at the origin cell (the shooter's position: same on every instance and always on the map). Cosmetic cost:
    /// a beam whose shooter is off screen may not be drawn (things are culled by their own cell).</summary>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedLasers
    {
        private static readonly CellRect Everything = new CellRect(-1000000, -1000000, 2000000, 2000000);

        public CombatExtendedLasers(ModContentPack mod)
        {
            var spawnBeam = AccessTools.DeclaredMethod("CombatExtended.Lasers.LaserBeamCE:SpawnBeam");
            if (spawnBeam == null)
            {
                Log.Error("[MPCompat] CE lasers: CombatExtended.Lasers.LaserBeamCE.SpawnBeam not found - laser beams may desync");
                return;
            }

            MpCompat.harmony.Patch(spawnBeam,
                transpiler: new HarmonyMethod(typeof(CombatExtendedLasers), nameof(ReplaceViewRect)));
        }

        private static CellRect ViewRectForBeam(CameraDriver driver)
        {
            if (MP.IsInMultiplayer || driver == null)
                return Everything;
            return driver.CurrentViewRect;
        }

        private static IEnumerable<CodeInstruction> ReplaceViewRect(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var target = AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect));
            var replacement = AccessTools.DeclaredMethod(typeof(CombatExtendedLasers), nameof(ViewRectForBeam));
            var replaced = 0;

            foreach (var ci in instr)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m == target)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    replaced++;
                }

                yield return ci;
            }

            const int expected = 1;
            if (replaced != expected)
                Log.Error($"[MPCompat] CE lasers: patched {replaced} CameraDriver.CurrentViewRect calls in {baseMethod.DeclaringType?.FullName}:{baseMethod.Name}, expected {expected} - laser beams may desync");
        }
    }
}
