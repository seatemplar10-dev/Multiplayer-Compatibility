using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Asimov (Neronix17)</summary>
    /// <remarks>
    /// FIXES row 477 (26 Nov 2026): the "begin processing early" gizmo of Building_ChargepackCharger sets processManualStart in a
    /// click lambda on the clicking PC only; the field is saved and read by State_AwaitingInput from the tick. The lambda captures
    /// only "this", so it is a plain instance method of the building (found by the field it writes) and is synced directly.
    /// </remarks>
    [MpCompatFor("Neronix17.Asimov")]
    public class AsimovCompat
    {
        public AsimovCompat(ModContentPack mod)
        {
            if (LabOff.Row(477))
            {
                Log.Message("[MPCompat - Asimov] row 477 switched off by the lab, begin processing early button not synced");
                return;
            }
            PatchRow477();
        }

        private static void PatchRow477()
        {
            var type = AccessTools.TypeByName("Asimov.Building_ChargepackCharger");
            var method = type == null ? null : AccessTools.DeclaredMethod(type, "<GetGizmos>b__39_0");
            if (method == null || method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0
                || !WritesField(method, "processManualStart"))
            {
                Log.Error("[MPCompat - Asimov] row 477: Building_ChargepackCharger.<GetGizmos>b__39_0 (writes processManualStart) not found, the button can desync");
                return;
            }
            MP.RegisterSyncMethod(method);
            Log.Message("[MPCompat - Asimov] row 477: begin processing early button lambda synced");
        }

        // true when the method's IL has a stfld for the named field
        private static bool WritesField(System.Reflection.MethodInfo method, string member)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            var module = method.Module;
            for (int i = 0; i < il.Length - 4; i++)
            {
                if (il[i] != 0x7D) continue; // stfld
                try
                {
                    if (module.ResolveField(System.BitConverter.ToInt32(il, i + 1), method.DeclaringType.GetGenericArguments(), null).Name == member)
                        return true;
                }
                catch { }
            }
            return false;
        }
    }
}
