using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Milira Race by Ancot</summary>
[MpCompatFor("Ancot.MiliraRace")]
public class MiliraRace
{
    // 1.7 lab, FIXES row 126 (class C17): Milira_MilianPawnGenerator_Patch.Postfix (a postfix on PawnGenerator.GeneratePawn) adds
    // the apparel it rolls for each apparel tag to the PawnKindDef's OWN apparelRequired list on every Milian it generates, makes
    // one item per entry of that list, and leaves weaponStuffOverride changed. Defs are not saved, so the list length is the
    // number of Milians this process has generated: a PC that joined after a Milira raid (or restarted) gives the next raid's
    // Milians less gear than the others (lab mp91: an extra MakeThing on the host only). In MP the kind def is put back after
    // every call, so each Milian starts from the def as loaded.
    public MiliraRace(ModContentPack mod)
    {
        // lab switch row 256 (wave 2, 17 Nov): off = old behaviour
        if (!LabOff.Row(256)) PatchFlyer();
        if (LabOff.Row(471))
            Log.Message("[MPCompat - Milira] row 471 switched off by the lab, Milian work tab manual priorities checkbox not synced");
        else
            PatchManualPriorities();
        var postfix = AccessTools.DeclaredMethod("Milira.Milira_MilianPawnGenerator_Patch:Postfix");
        if (postfix == null)
        {
            Log.Warning("MPCompat: Milira_MilianPawnGenerator_Patch.Postfix not found - Milira raid gear is not guarded (FIXES row 126)");
            return;
        }
        MpCompat.harmony.Patch(postfix,
            prefix: new HarmonyMethod(typeof(MiliraRace), nameof(PreMilianGear)),
            finalizer: new HarmonyMethod(typeof(MiliraRace), nameof(PostMilianGear)));
    }

    // FIXES row 471 (26 Nov 2026): MainTabWindow_MilianWork.DoManualPrioritiesCheckbox draws the "Manual priorities" checkbox on
    // `ref Current.Game.playSettings.useWorkPriorities` and then calls Notify_UseWorkPrioritiesChanged on the clicking PC's pawns.
    // Multiplayer syncs that field only in the vanilla work tab. In MP the checkbox is redrawn here on a local copy and a change
    // is sent as one SyncedSetUseWorkPriorities(value), which writes the field and runs the notify loop on every PC.
    // The tab's priority cells are the AncotLibrary work type columns (not checked here).
    internal static void PatchManualPriorities()
    {
        var method = AccessTools.DeclaredMethod("Milira.MainTabWindow_MilianWork:DoManualPrioritiesCheckbox");
        if (method == null || AccessTools.Field(typeof(PlaySettings), nameof(PlaySettings.useWorkPriorities)) == null)
        {
            Log.Error("[MPCompat - Milira] row 471: MainTabWindow_MilianWork.DoManualPrioritiesCheckbox not found, the manual priorities checkbox can desync");
            return;
        }
        MP.RegisterSyncMethod(typeof(MiliraRace), nameof(SyncedSetUseWorkPriorities));
        MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(MiliraRace), nameof(PreManualPriorities)));
        Log.Message("[MPCompat - Milira] row 471: manual priorities checkbox synced");
    }

    internal static bool PreManualPriorities()
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        Text.Font = GameFont.Small;
        UnityEngine.GUI.color = UnityEngine.Color.white;
        Text.Anchor = UnityEngine.TextAnchor.UpperLeft;
        var box = new UnityEngine.Rect(5f, 5f, 140f, 30f);
        var old = Current.Game.playSettings.useWorkPriorities;
        var value = old;
        Widgets.CheckboxLabeled(box, "ManualPriorities".Translate(), ref value);
        if (value != old)
            SyncedSetUseWorkPriorities(value);
        if (Current.Game.playSettings.useWorkPriorities)
        {
            using (new TextBlock(new UnityEngine.Color(1f, 1f, 1f, 0.5f)))
                Widgets.Label(new UnityEngine.Rect(box.x, box.yMax - 6f, box.width, 60f), "PriorityOneDoneFirst".Translate());
        }
        else
            UIHighlighter.HighlightOpportunity(box, "ManualPriorities-Off");
        return false;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void SyncedSetUseWorkPriorities(bool value)
    {
        if (Current.Game?.playSettings == null || Current.Game.playSettings.useWorkPriorities == value) return;
        Current.Game.playSettings.useWorkPriorities = value;
        foreach (var pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
            if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null)
                pawn.workSettings.Notify_UseWorkPrioritiesChanged();
    }

    // 1.7 lab, FIXES row 256: MiliraPawnFlyer is a copy of vanilla PawnFlyer (not a subclass): its RecomputePosition sets the flyer's
    // Position (saved) and runs only from DrawPos and DynamicDrawPhaseAt, so only a PC that draws the map moves it (row 255 is the
    // vanilla case). In MP its Tick and SpawnSetup recompute on every PC; the draw then finds this tick's position already computed.
    private static FastInvokeHandler miliraRecompute;

    private static void PatchFlyer()
    {
        var type = AccessTools.TypeByName("Milira.MiliraPawnFlyer");
        var recompute = type == null ? null : AccessTools.DeclaredMethod(type, "RecomputePosition", []);
        var tick = type == null ? null : AccessTools.DeclaredMethod(type, "Tick", []);
        var spawn = type == null ? null : AccessTools.DeclaredMethod(type, nameof(Thing.SpawnSetup));
        if (recompute == null || tick == null || spawn == null)
        {
            Log.Warning("MPCompat: Milira.MiliraPawnFlyer RecomputePosition/Tick/SpawnSetup not found - Milira flight is not guarded (FIXES row 256)");
            return;
        }
        miliraRecompute = MethodInvoker.GetHandler(recompute);
        var post = new HarmonyMethod(typeof(MiliraRace), nameof(PostFlyerRecompute));
        MpCompat.harmony.Patch(tick, postfix: post);
        MpCompat.harmony.Patch(spawn, postfix: post);
    }

    private static void PostFlyerRecompute(Thing __instance)
    {
        if (!MP.IsInMultiplayer || __instance.Destroyed || !__instance.Spawned) return;
        if (__instance is not IThingHolder holder || holder.GetDirectlyHeldThings().Count == 0) return;   // DestinationPos reads it
        miliraRecompute(__instance);
    }

    // 1.7 lab, FIXES row 145: the Ancot Library gizmo syncs (Milira turrets and weapons) moved to AncotLibrary.cs, keyed on
    // Ancot.AncotLibrary (its own mod and packageId), so they are registered once and not tied to Milira Race being active.

    private sealed class KindState
    {
        public PawnKindDef kind;
        public List<ThingDef> apparelRequired;
        public List<ThingDef> apparelRequiredCopy;
        public ThingDef weaponStuffOverride;
        public QualityCategory? forceWeaponQuality;
        public bool forceNormalGearQuality;
    }

    private static void PreMilianGear(PawnGenerationRequest request, ref KindState __state)
    {
        if (!MP.IsInMultiplayer) return;
        var kind = request.KindDef;
        if (kind == null) return;
        __state = new KindState
        {
            kind = kind,
            apparelRequired = kind.apparelRequired,
            apparelRequiredCopy = kind.apparelRequired == null ? null : new List<ThingDef>(kind.apparelRequired),
            weaponStuffOverride = kind.weaponStuffOverride,
            forceWeaponQuality = kind.forceWeaponQuality,
            forceNormalGearQuality = kind.forceNormalGearQuality,
        };
    }

    private static void PostMilianGear(KindState __state)
    {
        if (__state == null) return;
        var kind = __state.kind;
        // the same list object (other code may hold it), with its contents as before the call
        if (__state.apparelRequired != null)
        {
            __state.apparelRequired.Clear();
            __state.apparelRequired.AddRange(__state.apparelRequiredCopy);
        }
        kind.apparelRequired = __state.apparelRequired;
        kind.weaponStuffOverride = __state.weaponStuffOverride;
        kind.forceWeaponQuality = __state.forceWeaponQuality;
        kind.forceNormalGearQuality = __state.forceNormalGearQuality;
    }
}
