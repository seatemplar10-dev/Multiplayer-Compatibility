using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Rim-Effect: Core by Vanilla Expanded Team and Co., and Rim-Effect Renegade: Core by Team Rim-Effect</summary>
    /// <see href="https://github.com/AndroidQuazar/RimEffect-Core"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2479560240"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3473370247"/>
    /// <remarks>
    /// FIXES row 388 (2 Nov 2026, mod_sweep3 item 8 finding 1): the Renegade rework has packageId <c>RimEffectRenegade.Core</c>, so the old
    /// <c>RimEffect.Core</c> attribute never matched and this whole module was dead on 1.7. Checked in IL against the 1.6 folder 1.7 loads
    /// (<c>Rim-Effect Renegade Core/1.6/Assemblies/RimEffect.dll</c>, sha256 4429CDE8...): <c>RimEffect.AmmoBelt</c> lambda 1 of
    /// <c>GetWornGizmos</c> is <c>&lt;GetWornGizmos&gt;b__6_1</c> = the toggleAction, it stores <c>beltModeIsActive = !InUse</c> (saved, and read by the
    /// Launch / Impact / TakeDamage patches, so the toggle is simulation state). The autodoor class is no longer in RimEffect.dll: the Renegade
    /// def <c>RE_PrefabAutodoor</c> uses <c>VEF.Buildings.Building_AutoDoorLockable</c> (VEF.dll sha256 2F0C890C...), whose GetGizmos lambda 1 is the
    /// float-menu action with the closure field <c>doorState</c> (<c>KeyValuePair&lt;DoorAccess, string&gt;</c>). The old RimEffect.* name stays as a fallback.
    /// </remarks>
    [MpCompatFor("RimEffect.Core")]
    [MpCompatFor("RimEffectRenegade.Core")]
    public class RimEffectCore
    {
        private static ConstructorInfo doorValueCtor;
        private static FieldInfo doorValueKeyField;

        public RimEffectCore(ModContentPack mod)
        {
            // May need patching:
            // AddHumanlikeOrders_Patch - free hostages
            // RimEffect.CompSpawnOnInteract - open

            // FIXES row 388 (Opus review R388-4): each half has its own try/catch, so a mod update that breaks one half
            // is logged as an error and never hides the other half (before, a throw here made the loader log
            // "Exception loading" and skip everything after the failing line). An error, not a warning: an unsynced
            // half is a desync on the first click (beltModeIsActive and the door access are simulation state).
            try
            {
                MpCompat.RegisterLambdaMethod("RimEffect.AmmoBelt", "GetWornGizmos", 1);
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Rim-Effect Core: could not sync the AmmoBelt toggle, it stays unsynced (FIXES row 388): {e}");
            }

            try
            {
                RegisterAutoDoor();
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Rim-Effect Core: could not sync the autodoor access gizmo, it stays unsynced (FIXES row 388): {e}");
            }
        }

        private static void RegisterAutoDoor()
        {
            // FIXES row 388: Rim-Effect Renegade uses the VEF class; RimEffect.Building_AutoDoorLockable is the old mod's name.
            var autoDoorType = AccessTools.TypeByName("VEF.Buildings.Building_AutoDoorLockable")
                               ?? AccessTools.TypeByName("RimEffect.Building_AutoDoorLockable");
            if (autoDoorType == null)
            {
                Log.Warning("MPCompat :: Rim-Effect Core: Building_AutoDoorLockable not found, the autodoor access gizmo stays unsynced (FIXES row 388)");
                return;
            }

            var method = MpMethodUtil.GetLambda(autoDoorType, "GetGizmos", lambdaOrdinal: 1);
            var field = AccessTools.Field(method.DeclaringType, "doorState")
                        ?? throw new MissingFieldException(method.DeclaringType.FullName, "doorState");
            var doorValueType = field.FieldType;
            var genericTypes = doorValueType.GetGenericArguments();
            if (genericTypes.Length != 2)
                throw new InvalidOperationException($"{doorValueType} has {genericTypes.Length} generic arguments, expected 2 (KeyValuePair<DoorAccess, string>)");

            // Checked here, not at sync time: a null ctor or key field would only fail inside SyncDoorValue, mid-sync.
            var ctor = AccessTools.DeclaredConstructor(doorValueType, new[] { genericTypes[0], genericTypes[1] })
                       ?? throw new MissingMethodException(doorValueType.FullName, ".ctor");
            var keyField = AccessTools.Field(doorValueType, "key")
                           ?? throw new MissingFieldException(doorValueType.FullName, "key");

            doorValueCtor = ctor;
            doorValueKeyField = keyField;

            MP.RegisterSyncDelegate(autoDoorType, method.DeclaringType.Name, method.Name);
            MP.RegisterSyncWorker<object>(SyncDoorValue, doorValueType);
        }

        private static void SyncDoorValue(SyncWorker sync, ref object doorValue)
        {
            if (sync.isWriting)
                sync.Write((int)doorValueKeyField.GetValue(doorValue));
            else
                doorValue = doorValueCtor.Invoke(new object[] { sync.Read<int>(), string.Empty });
        }
    }
}