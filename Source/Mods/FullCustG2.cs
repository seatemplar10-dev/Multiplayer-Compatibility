using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FULL1127 class G wave G2: per-PC cache phases and per-PC toggles in sim code (rows judged REAL).
    /// Perspective Shift State.Avatar and the Surveillance Shift avatar / walkie / scope view are NOT synced here:
    /// they are one player's camera pawn (see the wave report).
    /// </summary>
    internal static class FullG2
    {
        public const string Tag = "[MPCompat - FULL1127 G2] ";

        public static bool Patch(string spec, string prefix, string what)
        {
            var m = AccessTools.Method(spec);
            if (m == null)
            {
                Log.Error(Tag + what + ": method " + spec + " not found");
                return false;
            }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullG2), prefix));
            return true;
        }

        // gravshipfleet: 60-tick platform cache on absolute buckets
        public static void Platforms() =>
            FullG1.Align("g2platforms", "GravshipFleet.FleetVge2Target:platformTick", 60, FullG1.Tick, 0);

        // semirandompfork: SOS2 uplink negative cache on absolute buckets
        public static void Uplink() =>
            FullG1.Align("g2uplink", "CM_Semi_Random_Research.Compatibility:sos2UplinkCheckedTick", 60, FullG1.Tick, 0);

        // VRE Android: do not let the per-PC memory cleanup clear the stand registry in MP
        public static bool KeepStands() => !MP.IsInMultiplayer;

        // Perspective Shift: the seek-at-will toggle gizmo writes a static id set on the clicking PC only
        static FieldInfo seekInstance;
        static FieldInfo seekSet;
        public static bool SeekToggle(object __instance)
        {
            if (!MP.IsInMultiplayer) return true;
            seekInstance = seekInstance ?? AccessTools.Field(__instance.GetType(), "__instance");
            seekSet = seekSet ?? AccessTools.Field("PerspectiveShift.State:seekAtWillPawns");
            var pawn = seekInstance?.GetValue(__instance) as Pawn;
            var set = seekSet?.GetValue(null) as System.Collections.Generic.HashSet<int>;
            if (pawn == null || set == null) return true;
            SetSeekAtWill(pawn, !set.Contains(pawn.thingIDNumber));
            return false;
        }

        public static void SetSeekAtWill(Pawn pawn, bool on)
        {
            if (pawn == null) return;
            seekSet = seekSet ?? AccessTools.Field("PerspectiveShift.State:seekAtWillPawns");
            var set = seekSet?.GetValue(null) as System.Collections.Generic.HashSet<int>;
            if (set == null)
            {
                set = new System.Collections.Generic.HashSet<int>();
                seekSet?.SetValue(null, set);
            }
            if (on) set.Add(pawn.thingIDNumber); else set.Remove(pawn.thingIDNumber);
        }
    }

    [MpCompatFor("asunib.gravshipfleet")]
    class FullG2GravshipFleet
    {
        public FullG2GravshipFleet(ModContentPack mod)
        {
            bool ok = FullG2.Patch("GravshipFleet.FleetVge2Target:ActivePlatforms", nameof(FullG2.Platforms), "GravshipFleet");
            Log.Message(FullG2.Tag + "gravshipfleet: platform cache bucket " + (ok ? "registered" : "FAILED"));
        }
    }

    [MpCompatFor("cruesoe.semirandompfork.continued")]
    class FullG2SemiRandom
    {
        public FullG2SemiRandom(ModContentPack mod)
        {
            bool ok = FullG2.Patch("CM_Semi_Random_Research.Compatibility:SaveOurShip2ArchotechUplinkUnlocked", nameof(FullG2.Uplink), "SemiRandom");
            Log.Message(FullG2.Tag + "semirandompfork: SOS2 uplink cache bucket " + (ok ? "registered" : "FAILED"));
        }
    }

    [MpCompatFor("vanillaracesexpanded.android")]
    class FullG2AndroidStands
    {
        public FullG2AndroidStands(ModContentPack mod)
        {
            bool ok = FullG2.Patch("VREAndroids.MemoryUtility_UnloadUnusedUnityAssets_Patch:Postfix", nameof(FullG2.KeepStands), "VREAndroids");
            Log.Message(FullG2.Tag + "vreandroids: stand registry kept on memory cleanup in multiplayer " + (ok ? "registered" : "FAILED"));
        }
    }

    [MpCompatFor("ferny.perspectiveshift")]
    class FullG2PerspectiveShift
    {
        public FullG2PerspectiveShift(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("PerspectiveShift.Pawn_GetGizmos_Patch+<>c__DisplayClass0_0");
            var lam = t == null ? null : AccessTools.Method(t, "<Postfix>b__3");
            if (lam == null) { Log.Error(FullG2.Tag + "perspectiveshift: seek-at-will toggle lambda not found"); return; }
            MpCompat.harmony.Patch(lam, prefix: new HarmonyMethod(typeof(FullG2), nameof(FullG2.SeekToggle)));
            MP.RegisterSyncMethod(typeof(FullG2), nameof(FullG2.SetSeekAtWill));
            Log.Message(FullG2.Tag + "perspectiveshift: seek-at-will toggle sync method registered");
        }
    }
}
