using Multiplayer.API;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Adeptus Mechanicus Armoury by Ogliss - fire mode switch</summary>
    /// <remarks>
    /// FIXES row 506. CompToggleFireMode.MakeModeMenu's FloatMenuOption delegate calls SwitchFireMode(int), which sets fireMode and
    /// a Stance_Cooldown on the clicking PC only. In Multiplayer the method is a synced method on the comp (MP syncs a ThingComp as
    /// the target: Thing holder lookup reaches the equipped weapon through its spawned parent pawn).
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("Ogliss.AdMech.Armoury")]
    public class AdMechArmouryFireMode
    {
        public AdMechArmouryFireMode(ModContentPack mod)
        {
            if (LabOff.Row(506))
            {
                Log.Message("[MpCompat] AdMech Armoury fire mode: row 506 off");
                return;
            }

            var type = AccessTools.TypeByName("AdeptusMechanicus.CompToggleFireMode");
            var method = type == null ? null : AccessTools.DeclaredMethod(type, "SwitchFireMode", new[] { typeof(int) });
            if (method == null)
            {
                Log.Error("AdMech Armoury fire mode: CompToggleFireMode.SwitchFireMode not found, the mode switch can desync");
                return;
            }
            MP.RegisterSyncMethod(method);
            Log.Message("[MpCompat] row 506: AdMech fire mode switch synced (1 methods)");
        }
    }
}
