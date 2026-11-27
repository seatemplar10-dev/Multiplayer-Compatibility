using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Use Bedrolls by Memegoddess</summary>
    /// <remarks>
    /// FIXES row 503: the 'pick back up bed' gizmo (UseBedrolls.PickBackUpBedGizmo.Postfix closure) calls pawn.jobs.StartJob(TakeBedroll)
    /// on the clicking PC only. In multiplayer the click sends one synced call (pawn + bed); on every PC it runs the original closure.
    /// </remarks>
    [MpCompatFor("Memegoddess.UseBedrolls")]
    class UseBedrolls
    {
        static FieldInfo bedField, pawnField;
        static MethodInfo lambda;
        static System.Type closureType;

        public UseBedrolls(ModContentPack mod)
        {
            if (LabOff.Row(503))
            {
                Log.Message("[MPCompat - Use Bedrolls] row 503 switched off by the lab, pick up bed gizmo not synced");
                return;
            }
            var type = AccessTools.TypeByName("UseBedrolls.PickBackUpBedGizmo");
            if (type != null)
                foreach (var nested in type.GetNestedTypes(AccessTools.all))
                {
                    var bf = nested.GetField("placedBed", AccessTools.all);
                    var pf = nested.GetField("__instance", AccessTools.all);
                    var m = nested.GetMethods(AccessTools.all).FirstOrDefault(x => x.Name.StartsWith("<Postfix>b__") && !x.IsStatic && x.GetParameters().Length == 0);
                    if (bf != null && pf != null && m != null) { bedField = bf; pawnField = pf; lambda = m; closureType = nested; break; }
                }
            if (lambda == null)
            {
                Log.Error("[MPCompat - Use Bedrolls] row 503: pick up bed gizmo closure not found, the button can desync");
                return;
            }
            MpCompat.harmony.Patch(lambda, prefix: new HarmonyMethod(typeof(UseBedrolls), nameof(Prefix)));
            MP.RegisterSyncMethod(typeof(UseBedrolls), nameof(SyncedPickUp)).CancelIfAnyArgNull();
            Log.Message("[MPCompat - Use Bedrolls] row 503: Use Bedrolls pick up bed synced");
        }

        static bool Prefix(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var pawn = pawnField.GetValue(__instance) as Pawn;
            var bed = bedField.GetValue(__instance) as Building_Bed;
            if (pawn != null && bed != null) SyncedPickUp(pawn, bed);
            return false;
        }

        // Runs on every PC: the original closure with the same pawn and bed.
        static void SyncedPickUp(Pawn pawn, Building_Bed bed)
        {
            var c = System.Activator.CreateInstance(closureType);
            pawnField.SetValue(c, pawn);
            bedField.SetValue(c, bed);
            lambda.Invoke(c, null);
        }
    }
}
