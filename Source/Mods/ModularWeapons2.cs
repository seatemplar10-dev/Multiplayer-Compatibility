using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>ModularWeapons 2 by kaitorisenkou</summary>
/// <remarks>
/// FIXES row 56 (17 Oct 2026, REA static sweep). <c>CompModularWeapon.RandomizePartsForPawn</c> picks a non-player
/// pawn's optional gun parts with <c>UnityEngine.Random.Range</c> inside a lambda
/// (<c>&lt;&gt;c__DisplayClass44_1.&lt;RandomizePartsForPawn&gt;b__1</c> in the lab's ModularWeapons2.dll); it runs from
/// Notify_Equipped on every PC -> draw from the synced Verse.Rand (sim code, no push/pop).
/// <para>
/// FIXES row 490 (27 Nov 2026). <c>JobDriver_UseGunsmithStation</c>'s last toil adds a <c>Dialog_Gunsmith</c> (sim code, so the
/// dialog opens on every PC). Each part click in <c>DoPartsDescContents</c> calls <c>CompModularWeapon.SetPart</c> on the live
/// weapon on the clicking PC only, and <c>PostClose</c> either keeps the new parts (BufferCurrent + TryTakeOrderedJob of the
/// ConsumeIngredientsForGunsmith job, whose last toil runs SetPartsWithBuffer) or reverts them (RevertToBuffer), on that PC only.
/// So the weapon parts differ per PC. In Multiplayer the job-path dialog is keyed by the opening job's loadID (recorded in a
/// constructor postfix, inside the toil, the same on every PC and saved with the game): a part click while the dialog draws sends
/// <see cref="SyncedSetPart"/> and writes nothing; closing it (Accept / Cancel) sends <see cref="SyncedClose"/>; the command sets
/// the part / runs the original PostClose once and closes the dialog on every PC. The first close wins, later commands do nothing.
/// Not covered: Load preset (Dialog_Gunsmith_Load / UnpackExposer), paint and decals, rename, the debug-action dialog (no station).
/// </para>
/// </remarks>
[MpCompatFor("kaitorisenkou.modularweapons2")]
public class ModularWeapons2
{
    private const string LogPrefix = "[MPCompat - ModularWeapons 2] ";

    public ModularWeapons2(ModContentPack mod)
    {
        var type = AccessTools.TypeByName("ModularWeapons2.CompModularWeapon")
                   ?? throw new System.TypeLoadException("[MPCompat - ModularWeapons 2] type ModularWeapons2.CompModularWeapon not found");
        // lab switch row 56 (package G, 17 Nov): off = old behaviour
        if (!LabOff.Row(56))
            PatchingUtilities.PatchUnityRand(MpMethodUtil.GetLambda(type, "RandomizePartsForPawn", lambdaOrdinal: 1), false);
        PatchGunsmithDialog();
    }

    #region Row 490: gunsmith dialog

    private sealed class GunsmithEntry
    {
        public int id;
        public Window dialog;
        public ThingComp comp;
        public bool resolved;
    }

    private static readonly Dictionary<int, GunsmithEntry> gunsmithByJob = new Dictionary<int, GunsmithEntry>();
    private static readonly ConditionalWeakTable<Window, GunsmithEntry> gunsmithByDialog = new ConditionalWeakTable<Window, GunsmithEntry>();
    private static Type partsDefType, mountDefType;
    private static FieldInfo gsCanceled, helperPart, helperMount;
    private static ConstructorInfo helperCtor;
    private static MethodInfo compSetPart, gsOnPartsChanged;
    private static Window drawingGunsmith;
    private static bool closingFromCommand;

    internal static void PatchGunsmithDialog()
    {
        if (LabOff.Row(490))
        {
            Log.Message(LogPrefix + "row 490 switched off by the lab, the gunsmith dialog part choice is not synced");
            return;
        }
        var dialogType = AccessTools.TypeByName("ModularWeapons2.Dialog_Gunsmith");
        var compType = AccessTools.TypeByName("ModularWeapons2.CompModularWeapon");
        var helperType = AccessTools.TypeByName("ModularWeapons2.PartsAttachHelper");
        partsDefType = AccessTools.TypeByName("ModularWeapons2.ModularPartsDef");
        mountDefType = AccessTools.TypeByName("ModularWeapons2.ModularPartsMountDef");
        if (dialogType == null || compType == null || helperType == null || partsDefType == null || mountDefType == null)
        {
            Log.Error(LogPrefix + "row 490: Dialog_Gunsmith / CompModularWeapon / PartsAttachHelper / part defs not found, the gunsmith dialog can desync");
            return;
        }
        gsCanceled = AccessTools.Field(dialogType, "canceled");
        helperPart = AccessTools.Field(helperType, "partsDef");
        helperMount = AccessTools.Field(helperType, "attachMountDef");
        helperCtor = AccessTools.Constructor(helperType, new[] { partsDefType, mountDefType });
        compSetPart = AccessTools.Method(compType, "SetPart", new[] { helperType });
        gsOnPartsChanged = AccessTools.Method(dialogType, "OnPartsChanged");
        var ctor = AccessTools.Constructor(dialogType, new[] { compType, typeof(Thing), typeof(Pawn) });
        var draw = AccessTools.DeclaredMethod(dialogType, "DoWindowContents");
        var postClose = AccessTools.DeclaredMethod(dialogType, "PostClose");
        if (gsCanceled == null || helperPart == null || helperMount == null || helperCtor == null || compSetPart == null || gsOnPartsChanged == null
            || ctor == null || draw == null || postClose == null)
        {
            Log.Error(LogPrefix + "row 490: a Dialog_Gunsmith / CompModularWeapon member was not found, the gunsmith dialog can desync");
            return;
        }
        MP.RegisterSyncMethod(typeof(ModularWeapons2), nameof(SyncedSetPart));
        MP.RegisterSyncMethod(typeof(ModularWeapons2), nameof(SyncedClose));
        var t = typeof(ModularWeapons2);
        MpCompat.harmony.Patch(ctor, postfix: new HarmonyMethod(t, nameof(PostGunsmithCtor)));
        MpCompat.harmony.Patch(draw, prefix: new HarmonyMethod(t, nameof(PreGunsmithDraw)), finalizer: new HarmonyMethod(t, nameof(FinGunsmithDraw)));
        MpCompat.harmony.Patch(compSetPart, prefix: new HarmonyMethod(t, nameof(PreSetPart)));
        MpCompat.harmony.Patch(postClose, prefix: new HarmonyMethod(t, nameof(PreGunsmithClose)));
        Log.Message(LogPrefix + "row 490: gunsmith dialog part choice and accept / cancel synced");
    }

    // Dialog_Gunsmith(CompModularWeapon weapon, Thing gunsmithStation, Pawn worker): the job path passes the station and the worker,
    // and runs inside the job's toil, so worker.CurJob is the UseGunsmithStation job on every PC.
    private static void PostGunsmithCtor(Window __instance, object[] __args)
    {
        if (!MP.IsInMultiplayer) return;
        if (__args[1] is not Thing || __args[2] is not Pawn worker || __args[0] is not ThingComp comp) return;  // debug-action dialog: local
        var job = worker.CurJob;
        if (job == null)
        {
            Log.Warning(LogPrefix + "row 490: gunsmith dialog opened with no current job, its part choice is not synced");
            return;
        }
        var entry = new GunsmithEntry { id = job.loadID, dialog = __instance, comp = comp };
        gunsmithByJob[entry.id] = entry;
        gunsmithByDialog.Remove(__instance);
        gunsmithByDialog.Add(__instance, entry);
    }

    private static void PreGunsmithDraw(Window __instance)
    {
        if (MP.IsInMultiplayer) drawingGunsmith = __instance;
    }

    private static Exception FinGunsmithDraw(Exception __exception)
    {
        drawingGunsmith = null;
        return __exception;
    }

    private static bool PreSetPart(ThingComp __instance, object[] __args)
    {
        var dialog = drawingGunsmith;
        if (dialog == null || !MP.IsInMultiplayer || MP.IsExecutingSyncCommand || closingFromCommand) return true;
        if (!gunsmithByDialog.TryGetValue(dialog, out var entry) || entry.comp != __instance) return true;
        if (entry.resolved) return false;
        var helper = __args[0];
        var part = helperPart.GetValue(helper) as Def;
        var mount = helperMount.GetValue(helper) as Def;
        SyncedSetPart(entry.id, part?.defName ?? "", mount?.defName ?? "");
        return false;
    }

    private static void SyncedSetPart(int jobId, string partDefName, string mountDefName)
    {
        if (!gunsmithByJob.TryGetValue(jobId, out var entry))
        {
            Log.Warning(LogPrefix + $"row 490: no gunsmith dialog for job {jobId} on this PC, part {partDefName} on {mountDefName} not applied");
            return;
        }
        if (entry.resolved) return;
        var part = string.IsNullOrEmpty(partDefName) ? null : GenDefDatabase.GetDefSilentFail(partsDefType, partDefName, false);
        var mount = string.IsNullOrEmpty(mountDefName) ? null : GenDefDatabase.GetDefSilentFail(mountDefType, mountDefName, false);
        if ((part == null && !string.IsNullOrEmpty(partDefName)) || (mount == null && !string.IsNullOrEmpty(mountDefName)))
        {
            Log.Warning(LogPrefix + $"row 490: gunsmith part {partDefName} or mount {mountDefName} not found, not applied");
            return;
        }
        compSetPart.Invoke(entry.comp, new[] { helperCtor.Invoke(new object[] { part, mount }) });
        gsOnPartsChanged.Invoke(entry.dialog, null);
    }

    // Window.Close removes the window from the stack before PostClose runs, so the clicking PC's dialog is already gone here.
    private static bool PreGunsmithClose(Window __instance)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || closingFromCommand) return true;
        if (!gunsmithByDialog.TryGetValue(__instance, out var entry)) return true;
        if (entry.resolved) return false;
        SyncedClose(entry.id, (bool)gsCanceled.GetValue(__instance));
        return false;
    }

    private static void SyncedClose(int jobId, bool canceled)
    {
        if (!gunsmithByJob.TryGetValue(jobId, out var entry))
        {
            Log.Warning(LogPrefix + $"row 490: no gunsmith dialog for job {jobId} on this PC, close (canceled {canceled}) not applied");
            return;
        }
        if (entry.resolved) return;
        entry.resolved = true;
        gsCanceled.SetValue(entry.dialog, canceled);
        closingFromCommand = true;
        try
        {
            var stack = Find.WindowStack;
            if (stack != null && stack.IsOpen(entry.dialog)) stack.TryRemove(entry.dialog, false);
            else entry.dialog.PostClose();
        }
        finally
        {
            closingFromCommand = false;
        }
    }

    #endregion
}
