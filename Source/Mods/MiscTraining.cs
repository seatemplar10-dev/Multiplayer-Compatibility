using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Misc. Training by HaploX1</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=717575199"/>
    [MpCompatFor("Haplo.Miscellaneous.Training")]
    class MiscTraining
    {
        public MiscTraining(ModContentPack mod)
        {
            // 1.7 lab, FIXES row 145: gizmo clicks on the training buildings. The buildings are Things, so MP finds the target
            // by id (same shape as MP's own Building_Trap auto-rearm toggle, GetGizmos 1). Each registration has its own
            // try/catch so one missing lambda cannot skip the others or the Rand patch below.
            // Building_ShootingRange.GetGizmos (iterator <GetGizmos>d__16): 0 = repair toggle action ("repairingDisabled =
            // !repairingDisabled" and writes the building's cell into the Home area: both saved), 1 = Command_Toggle.isActive
            // (read only, not registered), 2 = Command_Toggle.toggleAction ("autoRearm = !autoRearm;", saved).
            TryRegister("shooting range repair toggle and auto-rearm toggle",
                () => MpCompat.RegisterLambdaMethod("TrainingFacility.Building_ShootingRange", "GetGizmos", 0, 2));
            // Building_MartialArtsTarget.GetGizmos (iterator <GetGizmos>d__8): 0 = the repair toggle action, same body as the
            // shooting range's lambda 0 (flips repairingDisabled, writes the Home area cell). The only lambda in the method.
            TryRegister("martial arts target repair toggle",
                () => MpCompat.RegisterLambdaMethod("TrainingFacility.Building_MartialArtsTarget", "GetGizmos", 0));

            PatchingUtilities.PatchPushPopRand("TrainingFacility.JobDriver_Archery:ShootArrow");
        }

        private static void TryRegister(string what, System.Action register)
        {
            try
            {
                register();
            }
            catch (System.Exception e)
            {
                Log.Error($"MPCompat :: Misc. Training: could not sync {what} (FIXES row 145): {e}");
            }
        }
    }
}