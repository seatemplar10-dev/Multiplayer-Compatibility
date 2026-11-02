using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Cleaning Area Relaunched by Cerule</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3586697885"/>
/// <remarks>
/// FIXES row 389. Status: [unverified] compiled only, not run in game. Separate from <see cref="CleaningArea"/>: that module covers
/// the older Cleaning Area mods (hatti.cleaningarea, s1.cleaningareatemp); this mod has its own package id and types.
/// <para>
/// (1) Which area is cleaned. The play-settings broom button opens AreaUtility.MakeAllowedAreaListFloatMenu and its click lambda
/// (Patch_PlaySettings_CleaningAreaButton.OpenFloatMenu) calls CleaningAreaRelaunched_Util.SetCleanArea(currentMap, area) on the
/// clicking PC only. That sets CleaningAreaRelaunched_MapComponent.cleanAreaSelected (saved with the map) and rebuilds the filth
/// lister, and WorkGiver_CleanFilth_CleaningAreaRelaunched reads it in the simulation. Fix: SetCleanArea is a synced method
/// (Map and Area are both sync-able arguments: the Area is written as map id + area id and found again in the area manager of the
/// faction that sent the command, SyncDictRimWorld.cs "#region Areas"). The two Designator_AreaClean* designators need nothing:
/// Multiplayer's generic designator sync builds them with Activator.CreateInstance, so the parameterless ctor sets the readonly mode.
/// </para>
/// <para>
/// (2) The per-PC mod setting useModCleanArea (CleaningAreaRelaunched_Settings). It decides in MapComponent.FinalizeInit whether
/// the map gets an internal Area_Allowed (a new area id from the shared counter, a new entry in the area list), ExposeData nulls
/// cleanAreaInternal when it is off, and Designator_AreaClean.CanDesignateCell refuses every cell when it is off. A different value
/// on two PCs, or a mid-game toggle on one PC (the setter calls CleaningAreaRelaunched_Util.ToggleInternalArea, which creates or
/// clears the internal area of every map at once), changes the simulation on that PC only. Multiplayer's join check compares the
/// mod's config file with the host's (SyncConfigs / JoinData.CompareToLocal) and offers "Fix and restart", but still lets the
/// joiner "Connect anyway". This module (a) refuses the mid-game change while a Multiplayer game runs and (b) logs the value, and the
/// ids of the internal and the selected area of every map plus the next area id, at every load so the lab can compare the host and
/// client logs. That detects a join-time mismatch after the fact; it cannot prevent one (a forced session-wide value would need a
/// channel between the PCs and would override a player's setting). Lab rule: after every join compare the two log lines
/// ("useModCleanArea=" and "areas:") of the host, the client and the arbiter, and never click "Connect anyway" on a config mismatch.
/// </para>
/// <para>
/// (3) The clean-area cache. CleaningAreaRelaunched_Util.GetCleanArea keeps one Area per Map in a static dictionary and fills it on
/// the first call. With no selected area it returns map.areaManager.Home, and in Multiplayer map.areaManager is the manager of the
/// faction whose context is active: the LOCAL faction in the UI, the sender or the pawn's faction in the simulation. A right click on
/// filth runs WorkGiver_CleanFilth.HasJobOnThing(forced) in the UI, which could fill the cache with this PC's Home only; the simulation
/// would then read different areas on different PCs. Fix: a prefix on GetCleanArea that, in the UI and on a cache miss, answers from
/// the map component without storing the answer. Every other call (simulation, single player, a cache hit) runs the mod's code.
/// </para>
/// <para>
/// (4) The gravship transfer names. Gravship.CopyAreas (launch) records the area names in two static, unsaved strings; the
/// MapComponent.FinalizeInit of the landing map reads them many ticks later (it halts while a name is recorded, otherwise it
/// creates an area, which takes a new id from the global counter). A joiner (a new process) that joins between the launch and the
/// landing has no names, while the PCs that were in the game keep theirs: a static survives the reload of a join point. Fix: a
/// prefix on Game.LoadGame clears both strings on every load in Multiplayer, so every PC that loads the join point snapshot starts
/// equal (every PC reloads at a join point: AsyncWorldTimeComp.CreateJoinPointAndSendIfHost, except the non-issuing clients of a
/// standalone server, which the co-op setup does not use; with one, this reset would split the names). Cost: a join point between a launch and the landing loses the area transfer (a
/// second "Clean" area may appear on the landing map), the same on every PC, as it would after a restart of the game in single player.
/// </para>
/// </remarks>
[MpCompatFor("Cerule.CleaningAreaRelaunched")]
public class CleaningAreaRelaunched
{
    private const string LogPrefix = "MPCompat :: Cleaning Area Relaunched ";

    // CleaningAreaRelaunched_Mod.Settings (static field) and CleaningAreaRelaunched_Settings.useModCleanArea
    private static FieldInfo modSettingsField;
    private static MethodInfo useModCleanAreaSetterMethod;
    private static FastInvokeHandler useModCleanAreaGetter;
    private static FastInvokeHandler useModCleanAreaSetter;

    // (3) CleaningAreaRelaunched_Util.mapToAreaCache (private static readonly), Util.EnsureMapComponent(Map) and
    // CleaningAreaRelaunched_MapComponent.GetCleanArea()
    private static Dictionary<Map, Area> areaCache;
    private static FastInvokeHandler ensureMapComponent;
    private static FastInvokeHandler componentGetCleanArea;

    // (4) CleaningAreaRelaunched_Util.transferredInternalAreaName and transferredSelectedAreaName (private static string, not saved)
    private static FieldInfo transferredInternalAreaNameField;
    private static FieldInfo transferredSelectedAreaNameField;

    // The "areas:" load log line: the mod's MapComponent type and its private fields, and UniqueIDsManager.nextAreaID. Any one null = no line.
    private static Type areaStateComponentType;
    private static FieldInfo internalAreaField;
    private static FieldInfo selectedAreaField;
    private static FieldInfo nextAreaIdField;

    public CleaningAreaRelaunched(ModContentPack mod)
    {
        // (1) The only call that picks the cleaned area. The lambda that calls it is a closure method in the mod's dll (compiled
        // with optimizations off), SetCleanArea itself is a big method, and Multiplayer marks every sync method no-inlining.
        var utilType = AccessTools.TypeByName("CleaningAreaRelaunched.CleaningAreaRelaunched_Util");
        MP.RegisterSyncMethod(utilType, "SetCleanArea").CancelIfAnyArgNull();

        // One try per patch: a failed lookup in one of them must not skip the others.
        try
        {
            PatchUseModCleanArea();
        }
        catch (Exception e)
        {
            Log.Error($"{LogPrefix}useModCleanArea guard (no mid-game change in Multiplayer) and its load log failed, not applied: {e}");
        }

        try
        {
            PatchCleanAreaCache(utilType);
        }
        catch (Exception e)
        {
            Log.Error($"{LogPrefix}clean area cache guard (no cache fill from the UI in Multiplayer) failed, not applied: {e}");
        }

        try
        {
            PatchGravshipTransferNames(utilType);
        }
        catch (Exception e)
        {
            Log.Error($"{LogPrefix}gravship transfer names reset (on every load in Multiplayer) failed, not applied: {e}");
        }
    }

    // (3) Fills nothing from the UI. GetCleanArea(Map map) is called only from methods of the mod's dll, which is compiled with
    // DebuggableAttribute DisableOptimizations (decompile header, assembly attributes), so those callers are not optimized and a
    // tier-1 inline of this method into them is not expected; the prefix is on the method itself, not on a caller.
    private static void PatchCleanAreaCache(Type utilType)
    {
        var componentType = AccessTools.TypeByName("CleaningAreaRelaunched.CleaningAreaRelaunched_MapComponent");

        var cacheField = AccessTools.DeclaredField(utilType, "mapToAreaCache")
                         ?? throw new MissingFieldException(utilType.FullName, "mapToAreaCache");
        var cache = cacheField.GetValue(null) as Dictionary<Map, Area>
                    ?? throw new InvalidCastException($"{cacheField.DeclaringType}.{cacheField.Name} is {cacheField.FieldType}, expected Dictionary<Map, Area>");
        var ensure = AccessTools.DeclaredMethod(utilType, "EnsureMapComponent", new[] { typeof(Map) })
                     ?? throw new MissingMethodException(utilType.FullName, "EnsureMapComponent");
        var componentGetter = AccessTools.DeclaredMethod(componentType, "GetCleanArea", Type.EmptyTypes)
                              ?? throw new MissingMethodException(componentType.FullName, "GetCleanArea");
        var target = AccessTools.DeclaredMethod(utilType, "GetCleanArea", new[] { typeof(Map) })
                     ?? throw new MissingMethodException(utilType.FullName, "GetCleanArea");

        // Fields first, the patch last: a prefix is only live when everything it reads is set.
        areaCache = cache;
        ensureMapComponent = MethodInvoker.GetHandler(ensure);
        componentGetCleanArea = MethodInvoker.GetHandler(componentGetter);

        MpCompat.harmony.Patch(target, prefix: new HarmonyMethod(typeof(CleaningAreaRelaunched), nameof(GetCleanAreaWithoutUiCacheWrite)));
    }

    // Prefix of CleaningAreaRelaunched_Util.GetCleanArea(Map map). Harmony binds "map" by the parameter's name.
    private static bool GetCleanAreaWithoutUiCacheWrite(Map map, ref Area __result)
    {
        // Not the UI (single player, tick, command, load, map generation), or a cache hit (a read; the entry was written in the
        // simulation): the mod's own code. MP.InInterface is false outside a Multiplayer game.
        if (map == null || !MP.InInterface || areaCache.ContainsKey(map))
            return true;

        // UI and a cache miss: the answer the mod would store, without storing it (see remarks (3)).
        var component = ensureMapComponent(null, new object[] { map });
        if (component == null)
            return true; // the mod's own NullReferenceException, as without this patch

        __result = (Area)componentGetCleanArea(component, Array.Empty<object>());
        return false;
    }

    // (4) Game.LoadGame is the entry of every load, Multiplayer's join point reloads included (Source/Client/Saving/LoadPatch.cs
    // calls Current.Game.LoadGame()), and it runs before any map's FinalizeInit (Map.FinalizeLoading, called from Game.LoadGame).
    private static void PatchGravshipTransferNames(Type utilType)
    {
        var internalName = AccessTools.DeclaredField(utilType, "transferredInternalAreaName")
                           ?? throw new MissingFieldException(utilType.FullName, "transferredInternalAreaName");
        var selectedName = AccessTools.DeclaredField(utilType, "transferredSelectedAreaName")
                           ?? throw new MissingFieldException(utilType.FullName, "transferredSelectedAreaName");

        transferredInternalAreaNameField = internalName;
        transferredSelectedAreaNameField = selectedName;

        MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
            prefix: new HarmonyMethod(typeof(CleaningAreaRelaunched), nameof(ClearGravshipTransferNames)));
    }

    private static void ClearGravshipTransferNames()
    {
        if (!MP.IsInMultiplayer)
            return;

        // Never throws into the load of a game
        try
        {
            transferredInternalAreaNameField.SetValue(null, null);
            transferredSelectedAreaNameField.SetValue(null, null);
        }
        catch (Exception e)
        {
            Log.Warning($"{LogPrefix}could not reset the gravship transfer names: {e}");
        }
    }

    private static void PatchUseModCleanArea()
    {
        var settingsType = AccessTools.TypeByName("CleaningAreaRelaunched.CleaningAreaRelaunched_Settings");
        var modType = AccessTools.TypeByName("CleaningAreaRelaunched.CleaningAreaRelaunched_Mod");

        modSettingsField = AccessTools.DeclaredField(modType, "Settings");
        useModCleanAreaSetterMethod = AccessTools.DeclaredPropertySetter(settingsType, "useModCleanArea");
        useModCleanAreaGetter = MethodInvoker.GetHandler(AccessTools.DeclaredPropertyGetter(settingsType, "useModCleanArea"));
        useModCleanAreaSetter = MethodInvoker.GetHandler(useModCleanAreaSetterMethod);

        // The mod's settings window is the only caller of the setter: DoSettingsWindowContents runs
        // `Settings.useModCleanArea = checkOn;` every frame, and the setter does nothing unless the value changed. A transpiler
        // on that big UI method, not a prefix on the tiny setter or getter: on 1.7 (CoreCLR) a tier-1 compiled caller can inline
        // a small method and skip its patch.
        MpCompat.harmony.Patch(AccessTools.DeclaredMethod(modType, "DoSettingsWindowContents"),
            transpiler: new HarmonyMethod(typeof(CleaningAreaRelaunched), nameof(ReplaceUseModCleanAreaSetter)));

        // One log line per game load, on every PC. Game.FinalizeInit runs once per load (host, joiner and arbiter alike), after the
        // maps' FinalizeInit, which is where the mod reads the value. Not a patch on the mod's MapComponent.FinalizeInit: that one
        // runs once per map (a line count that depends on the map count, a dedupe field to keep) and is a virtual method of the mod.
        // Game.FinalizeInit is a large vanilla method that is called once per load, so no caller of it is hot enough to inline it.
        MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)),
            postfix: new HarmonyMethod(typeof(CleaningAreaRelaunched), nameof(LogUseModCleanArea)));

        // Extra data for the same load log. A failed lookup here only drops the "areas:" line, never the patches above.
        try
        {
            var componentType = AccessTools.TypeByName("CleaningAreaRelaunched.CleaningAreaRelaunched_MapComponent");
            var internalField = AccessTools.DeclaredField(componentType, "cleanAreaInternal")
                                ?? throw new MissingFieldException(componentType.FullName, "cleanAreaInternal");
            var selectedField = AccessTools.DeclaredField(componentType, "cleanAreaSelected")
                                ?? throw new MissingFieldException(componentType.FullName, "cleanAreaSelected");
            var nextField = AccessTools.DeclaredField(typeof(UniqueIDsManager), "nextAreaID")
                            ?? throw new MissingFieldException(typeof(UniqueIDsManager).FullName, "nextAreaID");

            areaStateComponentType = componentType;
            internalAreaField = internalField;
            selectedAreaField = selectedField;
            nextAreaIdField = nextField;
        }
        catch (Exception e)
        {
            Log.Warning($"{LogPrefix}the area ids load log line is not available: {e}");
        }
    }

    private static IEnumerable<CodeInstruction> ReplaceUseModCleanAreaSetter(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
    {
        var replacement = AccessTools.DeclaredMethod(typeof(CleaningAreaRelaunched), nameof(SetUseModCleanArea));
        var replacedCount = 0;

        foreach (var ci in instr)
        {
            // callvirt Settings::set_useModCleanArea(bool) with [Settings, bool] on the stack becomes
            // call SetUseModCleanArea(object, bool): a Settings reference is an object, so the stack stays valid
            if (useModCleanAreaSetterMethod != null && ci.Calls(useModCleanAreaSetterMethod))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = replacement;
                replacedCount++;
            }

            yield return ci;
        }

        const int expected = 1;
        if (replacedCount != expected)
            Log.Warning($"Patched incorrect number of CleaningAreaRelaunched_Settings.useModCleanArea setter calls (patched {replacedCount}, expected {expected}) for method {baseMethod.Name}");
    }

    // Single player and the main menu: the mod's own setter, unchanged. In a Multiplayer game a changed value is ignored: the
    // checkbox keeps showing the old value on the next frame, because the window reads the setting back every frame.
    // One message per refused click (the window passes a changed value once per click, not once per frame).
    private static void SetUseModCleanArea(object settings, bool value)
    {
        if (MP.IsInMultiplayer && settings != null && (bool)useModCleanAreaGetter(settings) != value)
        {
            // historical must stay false: Multiplayer gives an unhistorical message an id outside the shared message counter,
            // and this runs in the UI of one PC only (a historical message would advance that counter on this PC alone).
            Messages.Message(
                "Cleaning Area Relaunched: this setting can be changed only outside a Multiplayer game, because it adds or removes an area on this PC only (it would desync).",
                MessageTypeDefOf.RejectInput, false);
            return;
        }

        useModCleanAreaSetter(settings, value);
    }

    private static void LogUseModCleanArea()
    {
        if (!MP.IsInMultiplayer)
            return;

        try
        {
            var settings = modSettingsField.GetValue(null);
            Log.Message($"{LogPrefix}useModCleanArea={(settings == null ? "null" : useModCleanAreaGetter(settings))}");
        }
        catch (Exception e)
        {
            Log.Warning($"{LogPrefix}could not read useModCleanArea: {e}");
        }

        LogAreaIds();
    }

    // One line per load: the next area id (global) and, per map, the id of the internal and of the selected area. Equal on every PC
    // that loaded the same snapshot; a PC whose setting or gravship state differs shows another id here (see remarks (2) and (4)).
    private static void LogAreaIds()
    {
        if (areaStateComponentType == null || internalAreaField == null || selectedAreaField == null || nextAreaIdField == null)
            return;

        try
        {
            var text = new StringBuilder(LogPrefix).Append("areas: nextAreaID=").Append(nextAreaIdField.GetValue(Find.UniqueIDsManager));
            foreach (var map in Find.Maps)
            {
                text.Append(" | map ").Append(map.uniqueID);
                var component = map.GetComponent(areaStateComponentType);
                if (component == null)
                    text.Append(" no component");
                else
                    text.Append(" internal=").Append(AreaIdText(internalAreaField.GetValue(component)))
                        .Append(" selected=").Append(AreaIdText(selectedAreaField.GetValue(component)));
            }

            Log.Message(text.ToString());
        }
        catch (Exception e)
        {
            Log.Warning($"{LogPrefix}could not read the area ids: {e}");
        }
    }

    private static string AreaIdText(object area) => area is Area a ? a.ID.ToString() : "none";
}
