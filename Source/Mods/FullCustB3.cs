using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 wave B3 (cu13): class B rows, UI writes without MP sync. Each class registers synced methods for one mod.</summary>
    internal static class FullCustB3
    {
        const string Tag = "[MPCompat - FULL1127 B3] ";

        /// <summary>RegisterSyncMethod on type.method by name (private allowed). Logs one line, never throws.</summary>
        public static bool Reg(string modName, string typeName, string method, string what, Type[] args = null)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var mi = type == null ? null : (args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args));
                if (mi == null) { Log.Error(Tag + modName + ": " + typeName + "." + method + " not found"); return false; }
                MP.RegisterSyncMethod(mi);
                Log.Message(Tag + modName + ": " + what + " registered");
                return true;
            }
            catch (Exception e) { Log.Error(Tag + modName + ": " + what + " failed " + e); return false; }
        }
    }

    [MpCompatFor("andromeda.helpmebuild")]
    class FullB3HelpMeBuild
    {
        public FullB3HelpMeBuild(ModContentPack mod) =>
            FullCustB3.Reg("Help Me Build", "HelpMeBuild.Utils", "TryAddBillToQueue", "Utils.TryAddBillToQueue sync method (bill + repeat mode/count)");
    }

    [MpCompatFor("brrainz.squadkit")]
    class FullB3SquadKit
    {
        public FullB3SquadKit(ModContentPack mod)
        {
            FullCustB3.Reg("SquadKit", "SquadKit.SquadKitHealthUtility", "SetMedicalCare", "SetMedicalCare sync method");
            FullCustB3.Reg("SquadKit", "SquadKit.SquadKitHealthUtility", "SetSelfTend", "SetSelfTend sync method");
        }
    }

    [MpCompatFor("ferny.perspectiveshift")]
    class FullB3PerspectiveShift
    {
        public FullB3PerspectiveShift(ModContentPack mod) =>
            FullCustB3.Reg("Perspective Shift", "PerspectiveShift.CompPlayerOnly", "SetModeOnSelected", "CompPlayerOnly.SetModeOnSelected sync method (mode + WideFilter)");
    }

    [MpCompatFor("forti.rimjoust")]
    class FullB3RimJoust
    {
        public FullB3RimJoust(ModContentPack mod) =>
            FullCustB3.Reg("RimJoust", "RimJoust.Tournament.GuestKnightUtility", "RestoreAnimalFollowing", "GuestKnightUtility.RestoreAnimalFollowing sync method");
    }

    [MpCompatFor("goji.thesimstraits")]
    class FullB3SimsTraits
    {
        public FullB3SimsTraits(ModContentPack mod)
        {
            try
            {
                var t = AccessTools.TypeByName("SimsTraits.HostilityResponseModeUtility_DrawResponseButton_GenerateMenu_Patch");
                MpCompat.RegisterLambdaDelegate(t, "Prefix", 0);
                Log.Message("[MPCompat - FULL1127 B3] Sims Traits: hostility response menu lambda registered");
            }
            catch (Exception e) { Log.Error("[MPCompat - FULL1127 B3] Sims Traits: lambda failed " + e); }
        }
    }

    [MpCompatFor("i.automaticforaging")]
    class FullB3AutomaticForaging
    {
        public FullB3AutomaticForaging(ModContentPack mod)
        {
            // Copy is synced too so the static clipboard is identical on every PC before PasteInto runs
            FullCustB3.Reg("Automatic Foraging", "AutomaticForaging.ForageSpotClipboard", "Copy", "ForageSpotClipboard.Copy sync method");
            FullCustB3.Reg("Automatic Foraging", "AutomaticForaging.ForageSpotClipboard", "PasteInto", "ForageSpotClipboard.PasteInto sync method");
        }
    }

    [MpCompatFor("mrbeverage.shiftchange")]
    class FullB3ShiftChange
    {
        public FullB3ShiftChange(ModContentPack mod) =>
            FullCustB3.Reg("Shift Change", "ShiftChange.CompShiftStand", "MakeServeArea", "CompShiftStand.MakeServeArea sync method");
    }

    [MpCompatFor("oels.vehiclemapframework")]
    class FullB3VehicleMapFramework
    {
        public FullB3VehicleMapFramework(ModContentPack mod) =>
            FullCustB3.Reg("Vehicle Map Framework", "VehicleMapFramework.CompLocalThruster", "GenerateGravshipVehicle", "CompLocalThruster.GenerateGravshipVehicle sync method");
    }

    [MpCompatFor("shunter.bettertradersguild")]
    class FullB3BetterTradersGuild
    {
        public FullB3BetterTradersGuild(ModContentPack mod) =>
            FullCustB3.Reg("Better Traders Guild", "BetterTradersGuild.Comps.CompRelockable", "AssignRelockJob", "CompRelockable.AssignRelockJob sync method (job + bed rest care)");
    }
}
