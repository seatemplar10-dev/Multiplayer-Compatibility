using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
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
        // Stage 2: with the private co-op fork (FactionTerritories2.RealmSync, slice S1a) the synced screens and gizmos stay on.
        // Letters are on when the fork has slice S1b (RealmSync.LettersActive).

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
            BindCoreUi();

            // G1, G2, G5, G9, F1, F2, F4: Hegemony void postfixes. Skipped, they leave the vanilla __result and ref args as they are.
            Prefix(nameof(SkipInMultiplayer),
                "CaravanIncitementCommands:Postfix", // G1 Settlement.GetCaravanGizmos
                "InvasionJoinCommand:Postfix",       // G2 Settlement.GetCaravanGizmos
                "EnvoyOfferCommands:Postfix",        // G5 Pawn.GetGizmos
                "OutpostLinkGizmos:Postfix",         // G9 WorldObject.GetGizmos
                "CaravanIncitementOptions:Postfix",  // F1 Settlement.GetFloatMenuOptions
                "SupplyCaravanOptions:Postfix",      // F2 Settlement.GetFloatMenuOptions
                "SupplyPodOptions:Postfix");         // F4 Settlement.GetTransportersFloatMenuOptions

            // G6, G7, G11: passthrough iterator postfixes (rule B). A plain skip would return null and drop the vanilla
            // gizmos, so the prefix returns the incoming enumerable unchanged.
            Prefix(nameof(PassThroughGizmos),
                "TerritorialTaxGizmo:Postfix"); // G11 Settlement.GetGizmos

            // Stage 2 (co-op fork S1a): these are synced. Shown when RealmSync.CoreUiActive, else hidden as in Stage 1.
            Prefix(nameof(PassThroughUnlessCoreUi),
                "FealtyMapCommands:Postfix",    // G6 Pawn.GetGizmos
                "FealtyColonyCommands:Postfix"); // G7 WorldObject.GetGizmos
            // G4 adds Found town (synced) and the other founding gizmos. It runs, and a last postfix on Caravan.GetGizmos keeps
            // only the marked S1a items of it. Without the fork the old skip stays.
            Prefix(nameof(SkipUnlessCoreUi), "PlayerFoundingGizmo:Postfix"); // G4 Caravan.GetGizmos
            // Stage 2 (co-op fork S3): both add only synced items (outpost site building, site feature upgrade).
            Prefix(nameof(SkipUnlessCoreUi),
                "OutpostFacilityGizmos:Postfix",      // G8 WorldObject.GetGizmos
                "OrbitalSiteFeatureCommand:Postfix"); // G10 MapParent.GetGizmos
            // Stage 2 (co-op fork S8): the tile preview gizmo is a synced item, and the inspect button opens the read-only
            // preview whose Send settlers button is synced (the fork hides its landmark editor in MP).
            Prefix(nameof(SkipUnlessCoreUi),
                "CaravanTilePreview:Postfix",        // G3 Caravan.GetGizmos
                "TilePreviewInspectButton:Postfix"); // U2 WorldInspectPane.DoInspectPaneButtons
            Patch(AccessTools.DeclaredMethod(typeof(Caravan), nameof(Caravan.GetGizmos)), "Caravan.GetGizmos", postfix: nameof(FilterCaravanGizmos), lastPostfix: true);

            // F3, U1, T7: Hegemony bool prefixes (rule C). Skipped with __result = true, so vanilla runs as without Hegemony.
            // T7 opens a confirm dialog from the synced caravan arrival; its button would attack on one PC only.
            Prefix(nameof(LetVanillaRun),
                "VassalTravelOptions:Prefix",      // F3 Settlement.GetFloatMenuOptions
                "DiplomaticAttackWarning:Prefix"); // T7 SettlementUtility.Attack
            // Stage 2 (co-op fork S6): the field army right-click menu sends only synced orders, and only for own armies.
            Prefix(nameof(LetVanillaRunUnlessCoreUi),
                "FieldArmyWorldRightClick:Prefix"); // U1 WorldSelector.HandleWorldClicks

            // C1, W1-W4: Hegemony's own overrides. Keep the vanilla base gizmos and options (dev, reform caravan, abandon,
            // map gizmos), remove only the items Hegemony or its framework made.
            Postfix(nameof(FilterGizmosKeepCoreUi),
                "SettlementComp:GetGizmos",      // C1 (Stage 2: keeps buy vassal, rename and the Vassalage button of S1a)
                "HegemonyOutpost:GetGizmos",     // W3 (Stage 2: keeps the S3 outpost site building gizmo)
                "LandmarkHolding:GetGizmos",     // W4 (Stage 2: keeps the S3 site feature upgrade gizmo)
                "ArmyMarker:GetGizmos",          // W1 (Stage 2: keeps the S6 field army gizmos)
                "SkirmishSite:GetGizmos");       // W2 (Stage 2: keeps the S6 Watch battle gizmo)
            Postfix(nameof(FilterGizmos),
                "ArmyMarker:GetCaravanGizmos",   // W1
                "SkirmishSite:GetCaravanGizmos"); // W2
            Postfix(nameof(FilterFloatMenuOptions),
                "ArmyMarker:GetFloatMenuOptions",    // W1
                "SkirmishSite:GetFloatMenuOptions"); // W2

            // T1, T2 (Factions tab buttons) and the other entry points to these two windows: close them in MP.
            // The row patches T1-T5 stay, so the Factions tab layout does not change.
            // Stage 2: the fork draws only its synced S1a parts of both windows in MP, so they stay open when CoreUiActive.
            Prefix(nameof(CloseWindowUnlessCoreUi),
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

        private static void Patch(MethodBase method, string target, string prefix = null, string postfix = null, bool lastPostfix = false)
        {
            if (method == null)
            {
                Log.Error($"[MP Compat] Hegemony UI: {target} not found, its UI stays on in multiplayer.");
                return;
            }

            try
            {
                var postfixMethod = postfix == null ? null : new HarmonyMethod(typeof(HegemonyUi), postfix);
                if (postfixMethod != null && lastPostfix)
                    postfixMethod.priority = Priority.Last;
                MpCompat.harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(HegemonyUi), prefix),
                    postfix: postfixMethod);
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

        // Stage 2: the co-op fork's FactionTerritories2.RealmSync (S1a). Absent in the Workshop DLL: then CoreUi is false.
        private static Func<bool> coreUiActive;
        private static Func<object, bool> isCoreUi;
        private static Func<bool> lettersActive;

        private static void BindCoreUi()
        {
            var realmSync = AccessTools.TypeByName(Ns + "RealmSync");
            if (realmSync == null)
                return;

            var active = AccessTools.PropertyGetter(realmSync, "CoreUiActive");
            var marked = AccessTools.DeclaredMethod(realmSync, "IsCoreUi", [typeof(object)]);
            if (active == null || marked == null)
            {
                Log.Error("[MP Compat] Hegemony UI: RealmSync has no CoreUiActive/IsCoreUi, the co-op fork UI stays hidden in multiplayer.");
                return;
            }

            coreUiActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active);
            isCoreUi = (Func<object, bool>)Delegate.CreateDelegate(typeof(Func<object, bool>), marked);

            // Slice S1b: the fork routes each letter to its realm and makes unsynced letter choices info-only in MP.
            var letters = AccessTools.PropertyGetter(realmSync, "LettersActive");
            if (letters != null)
                lettersActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), letters);
            Log.Message("[MP Compat] Hegemony UI: co-op fork found, its synced S1a screens and gizmos stay on in multiplayer.");
        }

        private static bool CoreUi => coreUiActive != null && coreUiActive();

        private static bool IsCoreUi(object item) => isCoreUi != null && isCoreUi(item);

        private static bool SkipUnlessCoreUi() => !MP.IsInMultiplayer || CoreUi;

        private static bool PassThroughUnlessCoreUi(object[] __args, ref IEnumerable<Gizmo> __result)
            => CoreUi || PassThroughGizmos(__args, ref __result);

        private static bool CloseWindowUnlessCoreUi(object __instance) => CoreUi || CloseWindow(__instance);

        // Lazy filters: the fork marks an item while its iterator yields it, so the mark is set before the check.
        private static void FilterGizmosKeepCoreUi(ref IEnumerable<Gizmo> __result)
        {
            if (MP.IsInMultiplayer && __result != null)
            {
                var keepCore = CoreUi;
                __result = __result.Where(gizmo => !FromHegemony(gizmo) || (keepCore && IsCoreUi(gizmo)));
            }
        }

        private static void FilterCaravanGizmos(ref IEnumerable<Gizmo> __result)
        {
            if (CoreUi && __result != null)
                __result = __result.Where(gizmo => !FromHegemony(gizmo) || IsCoreUi(gizmo));
        }

        // Hegemony: IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, X __instance). __args[0] is the vanilla result.
        private static bool PassThroughGizmos(object[] __args, ref IEnumerable<Gizmo> __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = (IEnumerable<Gizmo>)__args[0];
            return false;
        }

        private static bool LetVanillaRunUnlessCoreUi(ref bool __result) => CoreUi || LetVanillaRun(ref __result);

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
            if (!MP.IsInMultiplayer || (lettersActive != null && lettersActive()))
                return true;

            __result = false;
            return false;
        }

        private static void HideVassalageButton(MainButtonWorker __instance, ref bool __result)
        {
            if (__result && MP.IsInMultiplayer && !CoreUi && __instance.def?.defName == VassalageButton)
                __result = false;
        }
    }
}
