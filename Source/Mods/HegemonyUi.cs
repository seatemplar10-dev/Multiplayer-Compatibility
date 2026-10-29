using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Hegemony - Faction Territories and Vassalage 2 by Jaeger972: player UI off in multiplayer</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3825320105"/>
    [MpCompatFor("jaeger972.factionterritories2")]
    public class HegemonyUi
    {
        // Stage 1 "AI world" (heg_stage1_targets.md Part 2). No Hegemony player action is synced, so in multiplayer every
        // player-facing provider is skipped and the AI simulation keeps running. Single player is unchanged (OWNER-29-4):
        // every hook checks MP.IsInMultiplayer first, nothing is unpatched and no def is changed.
        // Hegemony patches in its Mod constructor and the module order against it is not fixed, so patch after loading.
        // Gap: other Hegemony windows (DiplomacyLedgerWindow, LogisticsHub, ...) opened from elsewhere are not guarded.

        private const string Ns = "FactionTerritories2.";
        private const string HegemonyHarmonyId = "jaeger972.factionterritories2";
        private const string VassalageButton = "FT2_Vassalage";

        private static Assembly hegemonyAssembly;
        private static Assembly frameworkAssembly;
        private static int rebuiltMethods;

        public HegemonyUi(ModContentPack mod)
        {
            // In game this only queues LatePatch. The catch is for runs outside the game (no Prefs), where it throws.
            try
            {
                LongEventHandler.ExecuteWhenFinished(LatePatch);
            }
            catch (Exception e)
            {
                Log.Error($"[MP Compat] Hegemony UI: could not queue the late patch, Hegemony UI stays on in multiplayer. {e}");
            }
        }

        private static void LatePatch()
        {
            hegemonyAssembly = AccessTools.TypeByName(Ns + "ModEntry")?.Assembly;
            frameworkAssembly = AccessTools.TypeByName("JaegerUtilityFramework.WorkQueue")?.Assembly;

            // G1-G5, G8-G10, F1, F2, F4, U2: Hegemony void postfixes. Skipped, they leave the vanilla __result and ref args as they are.
            Prefix(nameof(SkipInMultiplayer),
                "CaravanIncitementCommands:Postfix", // G1 Settlement.GetCaravanGizmos
                "InvasionJoinCommand:Postfix",       // G2 Settlement.GetCaravanGizmos
                "CaravanTilePreview:Postfix",        // G3 Caravan.GetGizmos
                "PlayerFoundingGizmo:Postfix",       // G4 Caravan.GetGizmos
                "EnvoyOfferCommands:Postfix",        // G5 Pawn.GetGizmos
                "OutpostFacilityGizmos:Postfix",     // G8 WorldObject.GetGizmos
                "OutpostLinkGizmos:Postfix",         // G9 WorldObject.GetGizmos
                "OrbitalSiteFeatureCommand:Postfix", // G10 MapParent.GetGizmos
                "CaravanIncitementOptions:Postfix",  // F1 Settlement.GetFloatMenuOptions
                "SupplyCaravanOptions:Postfix",      // F2 Settlement.GetFloatMenuOptions
                "SupplyPodOptions:Postfix",          // F4 Settlement.GetTransportersFloatMenuOptions
                "TilePreviewInspectButton:Postfix"); // U2 WorldInspectPane.DoInspectPaneButtons

            // G6, G7, G11: passthrough iterator postfixes (rule B). A plain skip would return null and drop the vanilla
            // gizmos, so the prefix returns the incoming enumerable unchanged.
            Prefix(nameof(PassThroughGizmos),
                "FealtyMapCommands:Postfix",    // G6 Pawn.GetGizmos
                "FealtyColonyCommands:Postfix", // G7 WorldObject.GetGizmos
                "TerritorialTaxGizmo:Postfix"); // G11 Settlement.GetGizmos

            // F3, U1, T7: Hegemony bool prefixes (rule C). Skipped with __result = true, so vanilla runs as without Hegemony.
            // T7 opens a confirm dialog from the synced caravan arrival; its button would attack on one PC only.
            Prefix(nameof(LetVanillaRun),
                "VassalTravelOptions:Prefix",      // F3 Settlement.GetFloatMenuOptions
                "FieldArmyWorldRightClick:Prefix", // U1 WorldSelector.HandleWorldClicks
                "DiplomaticAttackWarning:Prefix"); // T7 SettlementUtility.Attack

            // C1, W1-W4: Hegemony's own overrides. Keep the vanilla base gizmos and options (dev, reform caravan, abandon,
            // map gizmos), remove only the items Hegemony or its framework made.
            Postfix(nameof(FilterGizmos),
                "SettlementComp:GetGizmos",      // C1
                "ArmyMarker:GetGizmos",          // W1
                "ArmyMarker:GetCaravanGizmos",   // W1
                "SkirmishSite:GetGizmos",        // W2
                "SkirmishSite:GetCaravanGizmos", // W2
                "HegemonyOutpost:GetGizmos",     // W3
                "LandmarkHolding:GetGizmos");    // W4
            Postfix(nameof(FilterFloatMenuOptions),
                "ArmyMarker:GetFloatMenuOptions",    // W1
                "SkirmishSite:GetFloatMenuOptions"); // W2

            // T1, T2 (Factions tab buttons) and the other entry points to these two windows: close them in MP.
            // The row patches T1-T5 stay, so the Factions tab layout does not change.
            Prefix(nameof(CloseWindow),
                "StrategicDiplomacyWindow:DoWindowContents", // T2 "Manage agreements"
                "VassalageTab:DoWindowContents");            // T1 "Vassalage", M1

            // Letters (2e, 9k): one choke point for all 8 ChoiceLetter classes and every Hegemony message.
            Patch(AccessTools.DeclaredPropertyGetter(Ns + "HegemonyNotifications:Enabled"), "HegemonyNotifications.Enabled",
                prefix: nameof(NoNotifications));

            // M1: main button FT2_Vassalage. MainButtonsRoot draws a button only if Worker.Visible. The def uses the default
            // MainButtonWorker_ToggleTab, which keeps the base getter, so a postfix there hides it in MP only.
            Patch(AccessTools.DeclaredPropertyGetter(typeof(MainButtonWorker), nameof(MainButtonWorker.Visible)), "MainButtonWorker.Visible",
                postfix: nameof(HideVassalageButton));

            // Rebuild: Hegemony's replacements of vanilla methods were JIT-compiled when Hegemony patched, and the JIT may
            // have inlined its small patch methods (and what they call) into them. A prefix on such a method is then
            // bypassed. Harmony marks a patched method no-inline, so one empty postfix on every method Hegemony patches
            // makes Harmony rebuild that replacement, which then calls through the detours of this module and of
            // Hegemony.cs (LatePatch runs after every mod constructor).
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                if (Harmony.GetPatchInfo(original)?.Owners.Contains(HegemonyHarmonyId) != true)
                    continue;

                try
                {
                    MpCompat.harmony.Patch(original, postfix: new HarmonyMethod(typeof(HegemonyUi), nameof(NoOpRebuild)));
                    rebuiltMethods++;
                }
                catch (Exception e)
                {
                    Log.Error($"[MP Compat] Hegemony UI: rebuilding {original.FullDescription()} failed. {e}");
                }
            }

            Log.Message($"[MP Compat] Hegemony UI: rebuilt {rebuiltMethods} methods patched by Hegemony.");
        }

        private static void Prefix(string patch, params string[] targets)
        {
            foreach (var target in targets)
                Patch(AccessTools.DeclaredMethod(Ns + target), target, prefix: patch);
        }

        private static void Postfix(string patch, params string[] targets)
        {
            foreach (var target in targets)
                Patch(AccessTools.DeclaredMethod(Ns + target), target, postfix: patch);
        }

        private static void Patch(MethodBase method, string target, string prefix = null, string postfix = null)
        {
            if (method == null)
            {
                Log.Error($"[MP Compat] Hegemony UI: {target} not found, its UI stays on in multiplayer.");
                return;
            }

            try
            {
                MpCompat.harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(HegemonyUi), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(HegemonyUi), postfix));
            }
            catch (Exception e)
            {
                Log.Error($"[MP Compat] Hegemony UI: patching {target} failed, its UI stays on in multiplayer. {e}");
            }
        }

        private static void NoOpRebuild()
        {
        }

        private static bool SkipInMultiplayer() => !MP.IsInMultiplayer;

        // Hegemony: IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, X __instance). __args[0] is the vanilla result.
        private static bool PassThroughGizmos(object[] __args, ref IEnumerable<Gizmo> __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = (IEnumerable<Gizmo>)__args[0];
            return false;
        }

        private static bool LetVanillaRun(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = true;
            return false;
        }

        private static void FilterGizmos(ref IEnumerable<Gizmo> __result)
        {
            if (MP.IsInMultiplayer && __result != null)
                __result = __result.Where(gizmo => !FromHegemony(gizmo));
        }

        private static void FilterFloatMenuOptions(ref IEnumerable<FloatMenuOption> __result)
        {
            if (MP.IsInMultiplayer && __result != null)
                __result = __result.Where(option => !FromHegemony(option));
        }

        // Made by Hegemony or its framework: the type, or the click delegate (lambdas live in compiler-generated nested types).
        private static bool FromHegemony(object item) => item switch
        {
            null => false,
            _ when FromHegemony(item.GetType()) => true,
            Command_Action action => FromHegemony(action.action),
            Command_Toggle toggle => FromHegemony(toggle.toggleAction) || FromHegemony(toggle.isActive),
            FloatMenuOption option => FromHegemony(option.action),
            _ => false,
        };

        private static bool FromHegemony(Delegate del) => del != null && FromHegemony(del.Method.DeclaringType);

        private static bool FromHegemony(Type type)
        {
            var assembly = type?.Assembly;
            return assembly != null && (assembly == hegemonyAssembly || assembly == frameworkAssembly);
        }

        private static bool CloseWindow(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            ((Window)__instance).Close(false);
            return false;
        }

        private static bool NoNotifications(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = false;
            return false;
        }

        private static void HideVassalageButton(MainButtonWorker __instance, ref bool __result)
        {
            if (__result && MP.IsInMultiplayer && __instance.def?.defName == VassalageButton)
                __result = false;
        }
    }
}
