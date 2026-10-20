using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Doors Expanded by Jecrell, lbmaian, jebjordan</summary>
    /// <remarks>
    /// 1.7 lab, FIXES row 145 (audit d143 colony rows 11, 12, 65): the three gizmos of a remote door
    /// (<c>Building_DoorRemote</c>) that write saved state ran on the clicking PC only.
    /// <list type="bullet">
    /// <item>Row 11, "Secured remotely" toggle: <c>SecuredRemotely = !SecuredRemotely</c> (the scribed <c>securedRemotely</c>,
    /// which drives HoldOpen/ForcedClosed/PawnCanOpen, then <c>ForbidUtility.SetForbidden</c>).</item>
    /// <item>Row 12, "Disconnect from button": <c>Button = null; SecuredRemotely = false;</c> (scribed <c>button</c>, plus
    /// <c>Notify_Unlinked</c> on the old button).</item>
    /// <item>Row 65, "Connect to button": a targeter. Its callback lambda sets <c>Button</c> and runs
    /// <c>UpdateOpenStateFromButtonEvent</c> (DoorOpen/DoorTryClose); the chosen button arrives as the
    /// <see cref="LocalTargetInfo"/> argument, which MP syncs as a thing id.</item>
    /// </list>
    /// Nothing in these bodies reads per-PC state (selection, UI, current map, settings).
    /// </remarks>
    [MpCompatFor("jecrell.doorsexpanded")]
    internal class DoorsExpandedCompat
    {
        public DoorsExpandedCompat(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("DoorsExpanded.Building_DoorRemote");

            // 1.7 lab, FIXES row 145: 'Secured remotely' toggle (Command_Toggle.toggleAction of GetGizmos, <GetGizmos>b__29_1)
            MpCompat.RegisterLambdaMethod(type, "GetGizmos", 1);

            // 1.7 lab, FIXES row 145: 'Disconnect from button' (Command_Action.action = ButtonDisconnect)
            MP.RegisterSyncMethod(type, "ButtonDisconnect");

            // 1.7 lab, FIXES row 145: 'Connect to button' - the targeter callback <ButtonConnect>b__32_0(LocalTargetInfo t):
            // synced with the clicked button as the argument. The gizmo's own ButtonConnect only opens the targeter (UI), left unsynced.
            MpCompat.RegisterLambdaMethod(type, "ButtonConnect", 0);

            // 1.7 lab, FIXES row 145 (lead from the d143 audit, not a listed row): Building_DoorRemoteButton 'Use button or lever'
            // = Command_Toggle.toggleAction <GetGizmos>b__22_1, 'NeedsToBeSwitched = !NeedsToBeSwitched'. needsToBeSwitched is scribed
            // and read by WorkGiver_UseRemoteButton (which pawn is sent to push the button), so a click on one PC split the job queue.
            var buttonType = AccessTools.TypeByName("DoorsExpanded.Building_DoorRemoteButton");
            MpCompat.RegisterLambdaMethod(buttonType, "GetGizmos", 1);
        }
    }
}
