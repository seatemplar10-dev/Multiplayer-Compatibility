using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FULL1127 cu25 (FIXES 569, second cause): Gun Recoil (local.gunrecoil, workshop 3833016652).
    /// Its postfix on Verb_LaunchProjectile.TryCastShot ejects shell casings only when caster.Map == Find.CurrentMap, and
    /// CasingMapComponent.Eject draws Verse.Rand (velocity, spin). Its Muzzle Flash bridge prefix (MuzzleFollow.SpawnPrefix, TurretSpawn)
    /// draws Verse.Rand and decides by Time.frameCount and draw-time samples whether Muzzle Flash's own SpawnMuzzleFlash runs.
    /// All of that is per PC, so it moved the map Rand on one PC only.
    /// Fix: each of these runs inside Rand.PushState / PopState, so whether it runs or not, the sim Rand is the same on every PC.
    /// SpawnMuzzleFlash gets its own push prefix at Priority.First, so it runs before any prefix that can return false.
    /// </summary>
    [MpCompatFor("local.gunrecoil")]
    class FullGunRecoil
    {
        public const string Tag = "[MPCompat - FULL1127 GR] ";

        static readonly string[] Methods =
        {
            "GunRecoil.CasingMapComponent:Eject",
            "GunRecoil.MuzzleFollow:TurretSpawn",
            "GunRecoil.MuzzleFollow:SpawnPrefix",
        };

        public FullGunRecoil(ModContentPack mod)
        {
            int n = 0;
            foreach (var spec in Methods)
            {
                try
                {
                    var m = AccessTools.DeclaredMethod(spec);
                    if (m == null) { Log.Error(Tag + "Gun Recoil: " + spec + " not found"); continue; }
                    PatchingUtilities.PatchPushPopRand(m);
                    n++;
                }
                catch (Exception e) { Log.Error(Tag + "Gun Recoil: " + spec + " patch failed: " + e); }
            }
            // Muzzle Flash (issaczhuang.muzzleflash): Gun Recoil's SpawnPrefix may skip this on one PC only.
            try
            {
                var t = AccessTools.TypeByName("MuzzleFlash.MuzzleFlashUtility");
                if (t != null)
                {
                    var pre = new HarmonyMethod(typeof(FullGunRecoil), nameof(PushPre)) { priority = Priority.First };
                    var post = new HarmonyMethod(typeof(FullGunRecoil), nameof(PopPost)) { priority = Priority.Last };
                    foreach (var m in AccessTools.GetDeclaredMethods(t).Where(x => x.Name == "SpawnMuzzleFlash"))
                    {
                        MpCompat.harmony.Patch(m, prefix: pre, postfix: post);
                        n++;
                    }
                }
            }
            catch (Exception e) { Log.Error(Tag + "Muzzle Flash SpawnMuzzleFlash patch failed: " + e); }
            Log.Message(Tag + "Gun Recoil: " + n + " casing / muzzle flash methods run in their own Rand state");
        }

        static void PushPre() => Rand.PushState();
        static void PopPost() => Rand.PopState();
    }
}
