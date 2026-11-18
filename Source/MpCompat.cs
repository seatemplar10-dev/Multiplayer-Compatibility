using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    public class MpCompat : Mod
    {
        public static readonly Harmony harmony = new Harmony("rimworld.multiplayer.compat");

        public MpCompat(ModContentPack content) : base(content)
        {
            DebugActions.content = content;
            LabOff.Touch();

            if (!MP.enabled) {
                Log.Warning("MPCompat :: Multiplayer is disabled.");
                return;
            }

            MpCompatLoader.Load(content);
            harmony.PatchAll();
            // FIXES row 305: a message from another player's tick or command is not drawn or played here (ForeignFactionMessages.cs)
            // lab switch row 305 (wave 2, 17 Nov): off = old behaviour
            if (!LabOff.Row(305)) ForeignFactionMessages.Patch(harmony);
            // FIXES row 180: the dev-mode-only ConfigErrors side effects on every PC (DeterministicConfigErrors.cs)
            LongEventHandler.ExecuteWhenFinished(DeterministicConfigErrors.Run);
            // FIXES row 262 root cause: races that get a training tracker but have no trainability (RaceTrainabilityFix.cs)
            // lab switch row 262 (wave 2, 17 Nov): off = old behaviour
            if (!LabOff.Row(262)) LongEventHandler.ExecuteWhenFinished(RaceTrainabilityFix.Run);
            // FIXES row 262: NullReferenceException in Pawn_TrainingTracker.CanAssignToTrain during every reload (TrainingTrackerLoadGuard.cs)
            try
            {
                // lab switch row 262 (wave 2, 17 Nov): off = old behaviour
                if (!LabOff.Row(262)) TrainingTrackerLoadGuard.Install(harmony);
            }
            catch (Exception e)
            {
                Log.Warning("[MPCompat] row 262: training tracker load guard not installed: " + e.Message);
            }
            // FIXES row 106: the letter archive cull and the ransom incident no longer read the per-PC letter stack (LetterArchiveDeterminism.cs)
            try
            {
                // lab switch row 106 (wave 2, 17 Nov): off = old behaviour
                if (!LabOff.Row(106)) LetterArchiveDeterminism.Install(harmony);
            }
            catch (Exception e)
            {
                Log.Warning("[MPCompat] row 106: letter archive guard not installed: " + e.Message);
            }
            // FIXES row 373: the vanilla Manage Areas Copy button is one synced action, not an empty new area (VanillaAreaCopy.cs)
            try
            {
                if (!LabOff.Row(373)) VanillaAreaCopy.Install(harmony);
            }
            catch (Exception e)
            {
                Log.Warning("[MPCompat] row 373: Manage Areas copy fix not installed: " + e.Message);
            }
            // FIXES row 424: the vanilla location generator's tick schedule uses the layer id, not an identity hash (LocationGeneratorStableSchedule.cs)
            try
            {
                if (!LabOff.Row(424)) LocationGeneratorStableSchedule.Install(harmony);
            }
            catch (Exception e)
            {
                Log.Warning("[MPCompat] row 424: location generator schedule fix not installed: " + e.Message);
            }
        }

        static IEnumerable<ISyncMethod> RegisterLambdaMethod_Impl(Type parentType, string parentMethod, MethodType methodType, params int[] lambdaOrdinals)
        {
            foreach (int ord in lambdaOrdinals)
            {
                var method = MpMethodUtil.GetLambda(parentType, parentMethod, methodType, null, ord);
                yield return MP.RegisterSyncMethod(method);
            }
        }

        public static ISyncMethod[] RegisterLambdaMethod(Type parentType, string parentMethod, MethodType methodType, params int[] lambdaOrdinals)
        {
            return RegisterLambdaMethod_Impl(parentType, parentMethod, methodType, lambdaOrdinals).ToArray();
        }

        public static ISyncMethod[] RegisterLambdaMethod(Type parentType, string parentMethod, params int[] lambdaOrdinals)
        {
            return RegisterLambdaMethod_Impl(parentType, parentMethod, MethodType.Normal, lambdaOrdinals).ToArray();
        }

        public static ISyncMethod[] RegisterLambdaMethod(string parentType, string parentMethod, MethodType methodType = MethodType.Normal, params int[] lambdaOrdinals)
        {
            return RegisterLambdaMethod_Impl(AccessTools.TypeByName(parentType), parentMethod, methodType, lambdaOrdinals).ToArray();
        }

        public static ISyncMethod[] RegisterLambdaMethod(string parentType, string parentMethod, params int[] lambdaOrdinals)
        {
            return RegisterLambdaMethod_Impl(AccessTools.TypeByName(parentType), parentMethod, MethodType.Normal, lambdaOrdinals).ToArray();
        }

        static IEnumerable<ISyncDelegate> RegisterLambdaDelegate_Impl(Type parentType, string parentMethod, MethodType methodType, string[] fields, params int[] lambdaOrdinals)
        {
            foreach (int ord in lambdaOrdinals)
            {
                var method = MpMethodUtil.GetLambda(parentType, parentMethod, methodType, null, ord);
                yield return MP.RegisterSyncDelegate(parentType, method.DeclaringType.Name, method.Name, fields);
            }
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(Type parentType, string parentMethod, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(parentType, parentMethod, MethodType.Normal, null, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(Type parentType, string parentMethod, string[] fields, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(parentType, parentMethod, MethodType.Normal, fields, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(Type parentType, string parentMethod, MethodType methodType, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(parentType, parentMethod, methodType, null, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(Type parentType, string parentMethod, MethodType methodType, string[] fields, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(parentType, parentMethod, methodType, fields, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(string parentType, string parentMethod, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(AccessTools.TypeByName(parentType), parentMethod, MethodType.Normal, null, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(string parentType, string parentMethod, string[] fields, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(AccessTools.TypeByName(parentType), parentMethod, MethodType.Normal, fields, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(string parentType, string parentMethod, MethodType methodType, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(AccessTools.TypeByName(parentType), parentMethod, methodType, null, lambdaOrdinals).ToArray();
        }

        public static ISyncDelegate[] RegisterLambdaDelegate(string parentType, string parentMethod, MethodType methodType, string[] fields, params int[] lambdaOrdinals)
        {
            return RegisterLambdaDelegate_Impl(AccessTools.TypeByName(parentType), parentMethod, methodType, fields, lambdaOrdinals).ToArray();
        }
    }
}