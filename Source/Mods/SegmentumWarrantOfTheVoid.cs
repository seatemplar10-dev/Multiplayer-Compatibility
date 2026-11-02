using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Segmentum - Warrant of the Void (WOTV.dll, namespace seg)</summary>
    /// <remarks>
    /// FIXES row 391 (mod sweep 2 Nov 2026, decompile of WOTV.dll 1.6). Three actions ran on the clicking PC only:
    /// <list type="bullet">
    /// <item>Gizmo "Assign Warrant Holder" (<c>SegCompWarrantWall.CompGetGizmosExtra</c>): the float menu option sets
    /// <c>comp.owner = p</c> and calls <c>Notify_OwnerAssigned</c> (warrant list, royal title of the holder).</item>
    /// <item>Gizmo "Assign Heir": sets <c>comp.heir = p</c>, <c>Notify_HeirAssigned</c> and
    /// <c>owner.royalty.SetHeir(heir, faction)</c>.</item>
    /// <item>"Request trader" (<c>WarrantOfTrade.GetFloatMenuOptions</c>, and the menu <c>JobDriver_WarrantOfTrade</c> opens from its
    /// tick on every PC): <c>WarrantOfTradeManager.TriggerTraderCall(Map, TraderKindDef)</c> sets the 900000 tick cooldown and runs
    /// the orbital trader incident.</item>
    /// </list>
    /// The two option lambdas are lambda delegates (<c>&lt;&gt;4__this</c> = the comp, <c>p</c> = the chosen pawn); the trader call is
    /// the method itself, so both call sites are covered. The job's float menu opens on every PC when the work ends, so a second
    /// click would call a second trader: a prefix drops <c>TriggerTraderCall</c> while the (saved, synced) cooldown runs. The mod only
    /// checks that cooldown when it builds the "Request trader" menu, never in the job path.
    /// Known and left: the job's menu still opens on every PC (UI only, no state). In the installed mod no def reaches either caller of
    /// <c>TriggerTraderCall</c> (the wall's <c>thingClass</c> is commented out, no JobDef names the job driver), so the trader part only
    /// matters for old saves or a later mod version.
    /// </remarks>
    [MpCompatFor("REDACTED.Segmentum.WarrantOfTheVoid")]
    class SegmentumWarrantOfTheVoid
    {
        private const string Namespace = "seg.";

        private static PropertyInfo isOnCooldownProperty;

        public SegmentumWarrantOfTheVoid(ModContentPack mod)
        {
            // Lambda 1 = "Assign Warrant Holder" option (<>c__DisplayClass2_0), lambda 3 = "Assign Heir" option (<>c__DisplayClass2_1).
            // Both capture the comp and the pawn that was clicked. One TryRegister each, so a log line names the one that failed.
            foreach (var ordinal in new[] { 1, 3 })
            {
                var lambda = ordinal;
                TryRegister($"{(lambda == 1 ? "owner" : "heir")} assignment (lambda {lambda})", () =>
                    MpCompat.RegisterLambdaDelegate(Namespace + "SegCompWarrantWall", nameof(ThingComp.CompGetGizmosExtra),
                        ["<>4__this", "p"], lambda)[0].CancelIfAnyFieldNull());
            }

            TryRegister("trader request", () =>
            {
                var managerType = AccessTools.TypeByName(Namespace + "WarrantOfTradeManager")
                    ?? throw new Exception("type " + Namespace + "WarrantOfTradeManager not found");
                var trigger = AccessTools.DeclaredMethod(managerType, "TriggerTraderCall")
                    ?? throw new Exception("method TriggerTraderCall not found");
                isOnCooldownProperty = AccessTools.DeclaredProperty(managerType, "IsOnCooldown")
                    ?? throw new Exception("property IsOnCooldown not found");
                MP.RegisterSyncMethod(trigger);
                MpCompat.harmony.Patch(trigger, prefix: new HarmonyMethod(typeof(SegmentumWarrantOfTheVoid), nameof(PreTriggerTraderCall)));
            });
        }

        private static void TryRegister(string what, Action register)
        {
            try
            {
                register();
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Segmentum Warrant of the Void: {what} stays unsynced: {e.InnerException ?? e}");
            }
        }

        // MP syncs with a transpiler, so this prefix also runs on the sending PC before the sync check, in UI time. Only the executing
        // side is guarded: it reads the cooldown in the command's map time, the same on every PC. The cooldown is saved and set only
        // inside this method, so a call that arrives during it is the second click of the job's shared menu.
        private static bool PreTriggerTraderCall(object __instance)
            => !MP.IsInMultiplayer || !MP.IsExecutingSyncCommand || !(bool)isOnCooldownProperty.GetValue(__instance);
    }
}
