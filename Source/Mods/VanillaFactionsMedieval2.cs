using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Factions Expanded - Medieval 2 by Oskar Potocki, Taranchuk</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaFactionsExpanded-Medieval2"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3444347874"/>
    [MpCompatFor("OskarPotocki.VFE.Medieval2")]
    public class VanillaFactionsMedieval2
    {
        // Barter dialog
        private static Type barterDealType;
        private static MethodInfo tryExecuteMethod;
        private static AccessTools.FieldRef<object, List<Tradeable>> barterDealTradeablesPlayerField;
        private static AccessTools.FieldRef<object, List<Tradeable>> barterDealTradeablesTraderField;
        private static Type dialogBarterType;

        // Stock generation guard
        private static bool inStockGeneration;
        private static AccessTools.FieldRef<CompForbiddable, bool> forbiddenIntField;

        public VanillaFactionsMedieval2(ModContentPack mod)
        {
            // Archery target uses RNG for its arrow fleck, from the sim tick (WatchTickAction). ThrowObjectAt in the 1.6 dll has no
            // camera or view check, so the draws do not depend on the player looking at it. The push/pop isolation is a harmless guard.
            PatchingUtilities.PatchPushPopRand("VFEMedieval.JobDriver_PlayArchery:ThrowObjectAt");

            // Rally ability gizmo - adds hediff to wearer
            MP.RegisterSyncMethod(AccessTools.DeclaredMethod("VFEMedieval.CompRallyAbility:ActivateAbility"));

            // Apiary dev gizmos
            MpCompat.RegisterLambdaMethod("VFEMedieval.Building_Apiary", nameof(Thing.GetGizmos), 0, 1).SetDebugOnly();

            LongEventHandler.ExecuteWhenFinished(LatePatchBarter);

            // FIXES row 406: heraldry editor writes saved tag state from the UI
            PatchHeraldry();
        }

        private static void LatePatchBarter()
        {
            // Barter dialog sync
            barterDealType = AccessTools.TypeByName("VFEMedieval.BarterDeal");
            tryExecuteMethod = AccessTools.DeclaredMethod(barterDealType, "TryExecute");
            barterDealTradeablesPlayerField = AccessTools.FieldRefAccess<List<Tradeable>>(barterDealType, "tradeablesPlayer");
            barterDealTradeablesTraderField = AccessTools.FieldRefAccess<List<Tradeable>>(barterDealType, "tradeablesTrader");
            dialogBarterType = AccessTools.TypeByName("VFEMedieval.Dialog_Barter");

            // Stock generation guard - MerchantGuild.RegenerateStock creates unspawned things.
            // MP tries to sync CompForbiddable.Forbidden and StorageSettings.CopyFrom on these
            // things, which fails because they're not on any map. We bypass the synced setters
            // entirely during stock generation since trader stock doesn't need sync.
            forbiddenIntField = AccessTools.FieldRefAccess<CompForbiddable, bool>("forbiddenInt");

            MpCompat.harmony.Patch(
                AccessTools.DeclaredMethod("VFEMedieval.MerchantGuild:RegenerateStock"),
                prefix: new HarmonyMethod(typeof(VanillaFactionsMedieval2), nameof(PreRegenerateStock)),
                finalizer: new HarmonyMethod(typeof(VanillaFactionsMedieval2), nameof(PostRegenerateStock)));

            // Bypass CompForbiddable.Forbidden setter during stock gen (skip MP sync transpiler)
            MpCompat.harmony.Patch(
                AccessTools.DeclaredPropertySetter(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden)),
                prefix: new HarmonyMethod(typeof(VanillaFactionsMedieval2), nameof(PreSetForbidden)));

            // Bypass StorageSettings.CopyFrom during stock gen (skip MP sync transpiler)
            MpCompat.harmony.Patch(
                typeof(StorageSettings).GetMethod(nameof(StorageSettings.CopyFrom),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(StorageSettings) }, null),
                prefix: new HarmonyMethod(typeof(VanillaFactionsMedieval2), nameof(PreCopyFrom)));

            // Intercept trade execution to sync it
            MpCompat.harmony.Patch(tryExecuteMethod,
                prefix: new HarmonyMethod(typeof(VanillaFactionsMedieval2), nameof(PreTryExecute)));

            // Synced trade execution
            MP.RegisterSyncMethod(typeof(VanillaFactionsMedieval2), nameof(SyncedBarterExecute));
        }

        #region Stock generation guard

        private static void PreRegenerateStock()
        {
            if (MP.IsInMultiplayer)
                inStockGeneration = true;
        }

        private static void PostRegenerateStock(Exception __exception)
        {
            inStockGeneration = false;
        }

        // Skip the Forbidden setter entirely during stock gen.
        // MP's sync transpiler is in the method body — returning false from prefix skips it.
        // Trader stock forbidden state doesn't matter.
        private static bool PreSetForbidden(CompForbiddable __instance, bool value)
        {
            if (!inStockGeneration)
                return true;

            forbiddenIntField(__instance) = value;
            return false;
        }

        // Skip CopyFrom sync during stock gen. Copy filter manually.
        // Storage settings on trader stock items don't need syncing.
        private static bool PreCopyFrom(StorageSettings __instance, StorageSettings other)
        {
            if (!inStockGeneration)
                return true;

            __instance.filter.CopyAllowancesFrom(other.filter);
            return false;
        }

        #endregion

        #region Barter sync

        // Intercept BarterDeal.TryExecute: in MP, sync the trade instead of executing locally
        private static bool PreTryExecute(object __instance, ref bool actuallyTraded)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            // Collect trade counts from current dialog state
            var playerTradeables = barterDealTradeablesPlayerField(__instance);
            var traderTradeables = barterDealTradeablesTraderField(__instance);

            var playerCounts = new int[playerTradeables.Count];
            for (var i = 0; i < playerTradeables.Count; i++)
                playerCounts[i] = playerTradeables[i].CountToTransfer;

            var traderCounts = new int[traderTradeables.Count];
            for (var i = 0; i < traderTradeables.Count; i++)
                traderCounts[i] = traderTradeables[i].CountToTransfer;

            // Sync the trade to all clients
            SyncedBarterExecute(
                TradeSession.playerNegotiator,
                (WorldObject)(object)TradeSession.trader,
                playerCounts, traderCounts);

            // Close dialog on the initiating player (synced method closes on all clients)
            actuallyTraded = true;
            return false;
        }

        // Synced method: recreates the trade session and executes with the specified counts
        private static void SyncedBarterExecute(Pawn negotiator, WorldObject traderObj,
            int[] playerCounts, int[] traderCounts)
        {
            if (negotiator == null || traderObj == null)
                return;

            // Setup trade session on all clients
            TradeSession.SetupWith((ITrader)traderObj, negotiator, false);

            // Create new BarterDeal (deterministic: same items, same seeded prices)
            var deal = Activator.CreateInstance(barterDealType);

            // Apply the trade counts from the initiating player
            var playerTradeables = barterDealTradeablesPlayerField(deal);
            for (var i = 0; i < playerCounts.Length && i < playerTradeables.Count; i++)
                playerTradeables[i].AdjustTo(playerCounts[i]);

            var traderTradeables = barterDealTradeablesTraderField(deal);
            for (var i = 0; i < traderCounts.Length && i < traderTradeables.Count; i++)
                traderTradeables[i].AdjustTo(traderCounts[i]);

            // Execute the trade (calls through Harmony-patched method, prefix allows it via IsExecutingSyncCommand)
            var args = new object[] { false };
            tryExecuteMethod.Invoke(deal, args);
            var actuallyTraded = (bool)args[0];

            if (actuallyTraded)
            {
                SoundDefOf.ExecuteTrade.PlayOneShotOnCamera();
                negotiator.GetCaravan()?.RecacheInventory();
            }

            // Close barter dialog on all clients
            Find.WindowStack.TryRemove(dialogBarterType);
        }

        #endregion

        #region Heraldry editor sync (FIXES row 406)

        // VFEMedieval.Dialog_Heraldic (opened by the CompEditHeraldic gizmo) has no Accept step: each colour click, each pattern or
        // symbol pick and the Reset button write VEF TaggedPropManager state (a saved GameComponent) at once, from UI code, on the
        // clicking PC only. A transpiler redirects each of those write calls to a stub. In MP the stub sends ONE synced command with the
        // final tag values (SyncedHeraldryApply): it sets the tags, then refreshes open heraldry windows and graphics, on every PC.
        // Outside MP the stub runs the original code. Every patched method is 236 to 1133 IL bytes, over the JIT inline limit (checked at load).

        private const string HeraldryLog = "[MPCompat] Medieval2 heraldry";
        private const int HeraldryMinIlSize = 100;
        private const long HeraldryPendingMs = 5000;

        private sealed class HeraldryRedirect
        {
            public readonly string Name;
            public readonly MethodBase From;
            public readonly MethodInfo To;
            public readonly int Expected;
            public readonly string OnlyIn;
            public int Found;

            public HeraldryRedirect(string name, MethodBase from, MethodInfo to, int expected, string onlyIn = null)
            {
                Name = name;
                From = from;
                To = to;
                Expected = expected;
                OnlyIn = onlyIn;
            }
        }

        private static HeraldryRedirect[] heraldryRedirects;
        private static readonly Dictionary<string, int> heraldryExpectedByMethod = new Dictionary<string, int>();
        private static readonly string[] NoStrings = new string[0];
        private static readonly float[] NoFloats = new float[0];
        // Edits sent but not yet applied (key = target id + tag): the window shows the old tag value until the command runs.
        private static readonly Dictionary<string, KeyValuePair<string, long>> heraldryPending = new Dictionary<string, KeyValuePair<string, long>>();
        // The Reset button's five removals are collected here, then sent as one command.
        private static readonly List<string> heraldryResetColorTags = new List<string>();
        private static readonly List<string> heraldryResetTextTags = new List<string>();
        private static ILoadReferenceable heraldryResetTarget;

        private static Type heraldryDialogType;
        private static FastInvokeHandler origSetColorTagCall, origSetHeraldicMaskCall, origSaveTagsToCall, settingsRefreshCall, getPathIndicesCall;
        private static MethodInfo dialogActiveTargetGetter;
        private static Action<ILoadReferenceable, string, UnityEngine.Color> vefSetColorTag;
        private static Action<ILoadReferenceable, string, string> vefSetStringTag;
        private static Action<ILoadReferenceable, string> vefRemoveColorTag, vefRemoveStringTag;
        private static Action dialogRefreshAllGraphics;
        private static AccessTools.FieldRef<object, object> dialogIndividualRef, dialogSharedRef;
        private static AccessTools.FieldRef<object, int> dialogPatternIndexRef, dialogSymbolIndexRef;
        private static AccessTools.FieldRef<object, object> settingsMaskColorARef, settingsMaskColorBRef, settingsMaskPathRef, settingsSymbolColorRef, settingsSymbolPathRef;
        // TaggedColor and TaggedText have different generic bases (TaggedItem<Color>, TaggedItem<string>), so each has its own FieldInfo
        private static FieldInfo taggedColorTagField, taggedTextTagField, taggedColorValueField, taggedTextValueField;

        private static bool HeraldryShouldSync() => MP.IsInMultiplayer && MP.InInterface;

        private static string HeraldryMethodKey(MethodBase m) => $"{m.Module.ScopeName}:{m.MetadataToken}";

        private static bool SameMethod(MethodBase a, MethodBase b) =>
            a == b || (a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module);

        private static void PatchHeraldry()
        {
            var patched = new List<MethodBase>();
            try
            {
                // 1. look everything up first; patch nothing unless all of it is there
                var dialogType = AccessTools.TypeByName("VFEMedieval.Dialog_Heraldic");
                var settingsType = AccessTools.TypeByName("VFEMedieval.HeraldicSettings");
                var helperType = AccessTools.TypeByName("VEF.Graphics.TaggedItemHelper");
                var colorItemType = AccessTools.TypeByName("VEF.Graphics.TaggedColor");
                var textItemType = AccessTools.TypeByName("VEF.Graphics.TaggedText");
                if (dialogType == null || settingsType == null || helperType == null || colorItemType == null || textItemType == null)
                    throw new InvalidOperationException("VFEMedieval.Dialog_Heraldic, VFEMedieval.HeraldicSettings or VEF.Graphics.TaggedItemHelper / TaggedColor / TaggedText not found");
                heraldryDialogType = dialogType;

                var refArg = typeof(ILoadReferenceable);
                var setColor = AccessTools.DeclaredMethod(helperType, "SetColorTag", new[] { refArg, typeof(string), typeof(UnityEngine.Color) });
                var setString = AccessTools.DeclaredMethod(helperType, "SetStringTag", new[] { refArg, typeof(string), typeof(string) });
                var removeColor = AccessTools.DeclaredMethod(helperType, "RemoveColorTag", new[] { refArg, typeof(string) });
                var removeString = AccessTools.DeclaredMethod(helperType, "RemoveStringTag", new[] { refArg, typeof(string) });
                var dialogSetColor = AccessTools.DeclaredMethod(dialogType, "SetColorTag", new[] { refArg, typeof(string), colorItemType, typeof(UnityEngine.Color) });
                var dialogSetMask = AccessTools.DeclaredMethod(dialogType, "SetHeraldicMask", new[] { refArg, typeof(string), textItemType, typeof(int), typeof(string) });
                var dialogGetPathIndices = AccessTools.DeclaredMethod(dialogType, "GetPathIndices", new[] { refArg });
                var dialogRefreshAll = AccessTools.DeclaredMethod(dialogType, "RefreshAllGraphics", Type.EmptyTypes);
                dialogActiveTargetGetter = AccessTools.DeclaredPropertyGetter(dialogType, "ActiveTarget");
                var settingsRefresh = AccessTools.DeclaredMethod(settingsType, "Refresh", Type.EmptyTypes);
                var settingsSaveTags = AccessTools.DeclaredMethod(settingsType, "SaveTagsTo", new[] { refArg });
                if (setColor == null || setString == null || removeColor == null || removeString == null || dialogSetColor == null || dialogSetMask == null
                    || dialogGetPathIndices == null || dialogRefreshAll == null || dialogActiveTargetGetter == null || settingsRefresh == null || settingsSaveTags == null)
                    throw new InvalidOperationException("a TaggedItemHelper, Dialog_Heraldic or HeraldicSettings method was not found");
                if (!setColor.IsStatic || !setString.IsStatic || !removeColor.IsStatic || !removeString.IsStatic || !dialogRefreshAll.IsStatic)
                    throw new InvalidOperationException("a TaggedItemHelper or RefreshAllGraphics method is not static");

                taggedColorTagField = AccessTools.Field(colorItemType, "tag");
                taggedTextTagField = AccessTools.Field(textItemType, "tag");
                taggedColorValueField = AccessTools.Field(colorItemType, "value");
                taggedTextValueField = AccessTools.Field(textItemType, "value");
                if (taggedColorTagField == null || taggedTextTagField == null || taggedColorValueField == null || taggedTextValueField == null)
                    throw new InvalidOperationException("TaggedColor.tag / value or TaggedText.tag / value not found");

                // FieldRefAccess throws when a field is missing, which ends in the clean skip below
                dialogIndividualRef = AccessTools.FieldRefAccess<object>(dialogType, "individualHeraldry");
                dialogSharedRef = AccessTools.FieldRefAccess<object>(dialogType, "sharedHeraldry");
                dialogPatternIndexRef = AccessTools.FieldRefAccess<int>(dialogType, "currentPatternIndex");
                dialogSymbolIndexRef = AccessTools.FieldRefAccess<int>(dialogType, "currentSymbolIndex");
                settingsMaskColorARef = AccessTools.FieldRefAccess<object>(settingsType, "maskColorA");
                settingsMaskColorBRef = AccessTools.FieldRefAccess<object>(settingsType, "maskColorB");
                settingsMaskPathRef = AccessTools.FieldRefAccess<object>(settingsType, "maskPath");
                settingsSymbolColorRef = AccessTools.FieldRefAccess<object>(settingsType, "symbolColor");
                settingsSymbolPathRef = AccessTools.FieldRefAccess<object>(settingsType, "symbolPath");

                vefSetColorTag = (Action<ILoadReferenceable, string, UnityEngine.Color>)Delegate.CreateDelegate(typeof(Action<ILoadReferenceable, string, UnityEngine.Color>), setColor);
                vefSetStringTag = (Action<ILoadReferenceable, string, string>)Delegate.CreateDelegate(typeof(Action<ILoadReferenceable, string, string>), setString);
                vefRemoveColorTag = (Action<ILoadReferenceable, string>)Delegate.CreateDelegate(typeof(Action<ILoadReferenceable, string>), removeColor);
                vefRemoveStringTag = (Action<ILoadReferenceable, string>)Delegate.CreateDelegate(typeof(Action<ILoadReferenceable, string>), removeString);
                dialogRefreshAllGraphics = (Action)Delegate.CreateDelegate(typeof(Action), dialogRefreshAll);
                // the original (unpatched) small methods, for outside MP
                origSetColorTagCall = MethodInvoker.GetHandler(dialogSetColor);
                origSetHeraldicMaskCall = MethodInvoker.GetHandler(dialogSetMask);
                origSaveTagsToCall = MethodInvoker.GetHandler(settingsSaveTags);
                settingsRefreshCall = MethodInvoker.GetHandler(settingsRefresh);
                getPathIndicesCall = MethodInvoker.GetHandler(dialogGetPathIndices);

                // 2. count the redirected calls on the original IL (exactly these, else the mod build differs); the small
                //    Dialog_Heraldic.SetColorTag / SetHeraldicMask are not patched, the stubs call them outside MP
                var self = typeof(VanillaFactionsMedieval2);
                heraldryRedirects = new[]
                {
                    new HeraldryRedirect("Dialog_Heraldic.SetColorTag", dialogSetColor, AccessTools.DeclaredMethod(self, nameof(RedirectSetColorTag)), 1),
                    new HeraldryRedirect("Dialog_Heraldic.SetHeraldicMask", dialogSetMask, AccessTools.DeclaredMethod(self, nameof(RedirectSetHeraldicMask)), 2),
                    new HeraldryRedirect("TaggedItemHelper.RemoveColorTag", removeColor, AccessTools.DeclaredMethod(self, nameof(RedirectRemoveColorTag)), 3),
                    new HeraldryRedirect("TaggedItemHelper.RemoveStringTag", removeString, AccessTools.DeclaredMethod(self, nameof(RedirectRemoveStringTag)), 2),
                    new HeraldryRedirect("Dialog_Heraldic.RefreshAllGraphics in DrawBottomButtons", dialogRefreshAll, AccessTools.DeclaredMethod(self, nameof(RedirectResetRefresh)), 1, "DrawBottomButtons"),
                    new HeraldryRedirect("HeraldicSettings.SaveTagsTo", settingsSaveTags, AccessTools.DeclaredMethod(self, nameof(RedirectSaveTagsTo)), 2),
                };
                foreach (var r in heraldryRedirects)
                    if (r.To == null)
                        throw new InvalidOperationException($"the stub for {r.Name} is missing");
                heraldryExpectedByMethod.Clear();
                var targets = new List<MethodBase>();
                foreach (var m in HeraldryCandidates(dialogType, settingsType))
                {
                    var found = CountHeraldryCalls(m);
                    if (found == 0)
                        continue;
                    var ilSize = m.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
                    if (ilSize <= HeraldryMinIlSize || (m.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0)
                        throw new InvalidOperationException($"{m.DeclaringType?.Name}:{m.Name} has {ilSize} IL bytes or AggressiveInlining, the JIT could inline it past the patch");
                    heraldryExpectedByMethod[HeraldryMethodKey(m)] = found;
                    targets.Add(m);
                }
                foreach (var r in heraldryRedirects)
                    if (r.Found != r.Expected)
                        throw new InvalidOperationException($"expected {r.Expected} call(s) of {r.Name}, found {r.Found}");

                // 3. register, then patch (all of it or none)
                MP.RegisterSyncMethod(self, nameof(SyncedHeraldryApply));
                var transpiler = new HarmonyMethod(self, nameof(HeraldryRedirectTranspiler));
                var summary = new List<string>();
                foreach (var m in targets)
                {
                    MpCompat.harmony.Patch(m, transpiler: transpiler);
                    patched.Add(m);
                    summary.Add($"{m.DeclaringType?.Name}:{m.Name} ({heraldryExpectedByMethod[HeraldryMethodKey(m)]} call(s), IL {m.GetMethodBody().GetILAsByteArray().Length} bytes)");
                }
                Log.Message($"{HeraldryLog}: sync method registered, {patched.Count} methods patched: {string.Join("; ", summary)}");
            }
            catch (Exception e)
            {
                foreach (var m in patched)
                    MpCompat.harmony.Unpatch(m, HarmonyPatchType.Transpiler, MpCompat.harmony.Id);
                Log.Warning($"{HeraldryLog}: not installed, the heraldry window keeps changing saved state on the clicking PC only: {e.Message}");
            }
        }

        // Every method and constructor of the types and of their nested types (the lambdas of the dialog are in nested types).
        private static IEnumerable<MethodBase> HeraldryCandidates(params Type[] roots)
        {
            var seen = new HashSet<Type>();
            var pending = new Stack<Type>(roots);
            while (pending.Count > 0)
            {
                var type = pending.Pop();
                if (type == null || !seen.Add(type))
                    continue;
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                    yield return method;
                foreach (var ctor in AccessTools.GetDeclaredConstructors(type))
                    yield return ctor;
                foreach (var nested in type.GetNestedTypes(AccessTools.all))
                    pending.Push(nested);
            }
        }

        private static bool IsRedirectCall(CodeInstruction ci, HeraldryRedirect r, MethodBase inMethod) =>
            (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase called && SameMethod(called, r.From)
            && (r.OnlyIn == null || r.OnlyIn == inMethod.Name);

        private static int CountHeraldryCalls(MethodBase method)
        {
            List<CodeInstruction> instr;
            try
            {
                instr = PatchProcessor.GetOriginalInstructions(method);
            }
            catch (Exception)
            {
                return 0; // no body to read (abstract or extern); a call missed here shows up as a wrong total
            }

            var count = 0;
            foreach (var ci in instr)
                foreach (var r in heraldryRedirects)
                    if (IsRedirectCall(ci, r, method))
                    {
                        r.Found++;
                        count++;
                    }
            return count;
        }

        // Each redirected call keeps its stack shape: an instance call (this first) becomes a static call taking the same values.
        // Opcode and operand change in place, so labels stay on the instruction.
        private static IEnumerable<CodeInstruction> HeraldryRedirectTranspiler(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var replacedCount = 0;
            foreach (var ci in instr)
            {
                foreach (var r in heraldryRedirects)
                {
                    if (!IsRedirectCall(ci, r, baseMethod))
                        continue;
                    ci.opcode = OpCodes.Call;
                    ci.operand = r.To;
                    replacedCount++;
                    break;
                }

                yield return ci;
            }

            heraldryExpectedByMethod.TryGetValue(HeraldryMethodKey(baseMethod), out var expected);
            if (replacedCount != expected)
            {
                var name = (baseMethod.DeclaringType?.Namespace).NullOrEmpty() ? baseMethod.Name : $"{baseMethod.DeclaringType!.Name}:{baseMethod.Name}";
                Log.Warning($"Patched incorrect number of heraldry tag calls (patched {replacedCount}, expected {expected}) for method {name}");
            }
        }

        // ---- stubs (UI time): these replace the write calls inside the window code ----

        // Replaces Dialog_Heraldic.SetColorTag(activeTarget, type, tColor, color): a click on a colour.
        private static void RedirectSetColorTag(object dialog, ILoadReferenceable activeTarget, string type, object tColor, UnityEngine.Color color)
        {
            if (!HeraldryShouldSync())
            {
                origSetColorTagCall(dialog, new object[] { activeTarget, type, tColor, color });
                return;
            }

            if (activeTarget == null)
                return; // the original writes nothing for a null target either
            var tag = (string)taggedColorTagField.GetValue(tColor);
            if (HeraldryIsPending(activeTarget, tag, $"{color.r}|{color.g}|{color.b}|{color.a}"))
                return;
            SendHeraldry(activeTarget, new[] { tag }, new[] { color.r, color.g, color.b, color.a }, NoStrings, NoStrings, NoStrings, NoStrings);
        }

        // Replaces Dialog_Heraldic.SetHeraldicMask(activeTarget, type, tText, currentIdx, path): a pattern or symbol pick. The window calls it
        // again on every frame until its cached tag value changes, so a send that is still pending is not repeated.
        private static void RedirectSetHeraldicMask(object dialog, ILoadReferenceable activeTarget, string type, object tText, int currentIdx, string path)
        {
            if (!HeraldryShouldSync())
            {
                origSetHeraldicMaskCall(dialog, new object[] { activeTarget, type, tText, currentIdx, path });
                return;
            }

            // the window's own list position (not saved state), as in the first lines of the original
            if (type == "HeraldryPattern")
                dialogPatternIndexRef(dialog) = currentIdx;
            else
                dialogSymbolIndexRef(dialog) = currentIdx;
            if (activeTarget == null)
                return;
            var tag = (string)taggedTextTagField.GetValue(tText);
            path ??= "";
            if (HeraldryIsPending(activeTarget, tag, path))
                return;
            SendHeraldry(activeTarget, NoStrings, NoFloats, new[] { tag }, new[] { path }, NoStrings, NoStrings);
        }

        // Replace the five TaggedItemHelper.Remove*Tag calls of the Reset button: in MP they are collected, then RedirectResetRefresh sends them.
        private static void RedirectRemoveColorTag(ILoadReferenceable target, string tag)
        {
            if (!HeraldryShouldSync())
            {
                vefRemoveColorTag(target, tag);
                return;
            }

            heraldryResetTarget = target;
            heraldryResetColorTags.Add(tag);
        }

        private static void RedirectRemoveStringTag(ILoadReferenceable target, string tag)
        {
            if (!HeraldryShouldSync())
            {
                vefRemoveStringTag(target, tag);
                return;
            }

            heraldryResetTarget = target;
            heraldryResetTextTags.Add(tag);
        }

        // Replaces the Dialog_Heraldic.RefreshAllGraphics() call that follows the five removals (in DrawBottomButtons only).
        private static void RedirectResetRefresh()
        {
            if (!HeraldryShouldSync())
            {
                dialogRefreshAllGraphics();
                return;
            }

            var target = heraldryResetTarget;
            var colors = heraldryResetColorTags.ToArray();
            var texts = heraldryResetTextTags.ToArray();
            heraldryResetTarget = null;
            heraldryResetColorTags.Clear();
            heraldryResetTextTags.Clear();
            if (target != null && colors.Length + texts.Length > 0)
                SendHeraldry(target, NoStrings, NoFloats, NoStrings, NoStrings, colors, texts);
        }

        // Replaces HeraldicSettings.SaveTagsTo(target): the first-time fill of the tags of a faction that has none (the faction constructor of
        // HeraldicSettings, random pattern and colours) or of a thing without faction. The values are generated on the clicking PC, so
        // they are sent as they are, not generated again on the other PCs.
        private static void RedirectSaveTagsTo(object settings, ILoadReferenceable target)
        {
            if (HeraldryShouldSync() && target != null)
            {
                var colorA = settingsMaskColorARef(settings);
                var colorB = settingsMaskColorBRef(settings);
                var pattern = settingsMaskPathRef(settings);
                var symbolColor = settingsSymbolColorRef(settings);
                var symbol = settingsSymbolPathRef(settings);
                if (colorA != null && colorB != null && pattern != null && symbolColor != null && symbol != null)
                {
                    var a = (UnityEngine.Color)taggedColorValueField.GetValue(colorA);
                    var b = (UnityEngine.Color)taggedColorValueField.GetValue(colorB);
                    var s = (UnityEngine.Color)taggedColorValueField.GetValue(symbolColor);
                    SendHeraldry(target,
                        new[] { "HeraldryColorA", "HeraldryColorB", "HeraldrySymbolColor" },
                        new[] { a.r, a.g, a.b, a.a, b.r, b.g, b.b, b.a, s.r, s.g, s.b, s.a },
                        new[] { "HeraldryPattern", "HeraldrySymbol" },
                        new[] { (string)taggedTextValueField.GetValue(pattern) ?? "", (string)taggedTextValueField.GetValue(symbol) ?? "" },
                        NoStrings, NoStrings);
                    return;
                }
            }

            origSaveTagsToCall(settings, new object[] { target });
        }

        private static string HeraldryPendingKey(ILoadReferenceable target, string tag) => target.GetUniqueLoadID() + "|" + tag;

        // True when the same value was sent for this tag less than HeraldryPendingMs ago and has not been applied yet.
        private static bool HeraldryIsPending(ILoadReferenceable target, string tag, string valueText)
        {
            var key = HeraldryPendingKey(target, tag);
            var now = Environment.TickCount64;
            if (heraldryPending.TryGetValue(key, out var sent) && sent.Key == valueText && now < sent.Value)
                return true;
            heraldryPending[key] = new KeyValuePair<string, long>(valueText, now + HeraldryPendingMs);
            return false;
        }

        private static void SendHeraldry(ILoadReferenceable target, string[] colorTags, float[] colorValues, string[] textTags, string[] textValues,
            string[] removeColorTags, string[] removeTextTags)
        {
            var thing = target as Thing;
            var faction = target as Faction;
            if (thing == null && faction == null)
            {
                Log.Warning($"{HeraldryLog}: target {target} is neither a Thing nor a Faction, edit not sent");
                return;
            }

            SyncedHeraldryApply(thing, faction, colorTags, colorValues, textTags, textValues, removeColorTags, removeTextTags);
        }

        // ---- synced (runs on every PC from the same arguments) ----

        // The final edit of one user action: set colour tags (4 floats each: r, g, b, a), set text tags, remove tags. Exactly one of
        // thing and faction is the target (the dialog edits a Thing, or the Faction of that Thing). NoInlining because MP patches this method.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedHeraldryApply(Thing thing, Faction faction, string[] colorTags, float[] colorValues, string[] textTags,
            string[] textValues, string[] removeColorTags, string[] removeTextTags)
        {
            ILoadReferenceable target = (ILoadReferenceable)thing ?? faction;
            colorTags ??= NoStrings;
            colorValues ??= NoFloats;
            textTags ??= NoStrings;
            textValues ??= NoStrings;
            removeColorTags ??= NoStrings;
            removeTextTags ??= NoStrings;
            if (target == null || colorValues.Length != colorTags.Length * 4 || textValues.Length != textTags.Length)
            {
                Log.Warning($"{HeraldryLog}: command dropped (target={target?.GetUniqueLoadID() ?? "null"}, color tags {colorTags.Length} with {colorValues.Length} values, text tags {textTags.Length} with {textValues.Length} values)");
                return;
            }

            for (var i = 0; i < colorTags.Length; i++)
            {
                vefSetColorTag(target, colorTags[i], new UnityEngine.Color(colorValues[i * 4], colorValues[i * 4 + 1], colorValues[i * 4 + 2], colorValues[i * 4 + 3]));
                heraldryPending.Remove(HeraldryPendingKey(target, colorTags[i]));
            }

            for (var i = 0; i < textTags.Length; i++)
            {
                vefSetStringTag(target, textTags[i], textValues[i]);
                heraldryPending.Remove(HeraldryPendingKey(target, textTags[i]));
            }

            foreach (var tag in removeColorTags)
                vefRemoveColorTag(target, tag);
            foreach (var tag in removeTextTags)
                vefRemoveStringTag(target, tag);

            Log.Message($"[MPCompat] Medieval2 heraldry set: target={target.GetUniqueLoadID()} colors={colorTags.Length} texts={textTags.Length} removed={removeColorTags.Length + removeTextTags.Length} issuedBySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
            RefreshHeraldryViews();
        }

        // After the tags changed, on every PC: open heraldry windows read their cached tag values again (and move their list positions to
        // the saved value), then the graphics of all things are refreshed, what the dialog did itself on the clicking PC. Not saved state,
        // so a failure here is logged and does not undo the edit.
        private static void RefreshHeraldryViews()
        {
            try
            {
                var windows = Find.WindowStack?.Windows;
                for (var i = 0; windows != null && i < windows.Count; i++)
                {
                    var window = windows[i];
                    if (window == null || !heraldryDialogType.IsInstanceOfType(window))
                        continue;
                    if (dialogIndividualRef(window) is { } individual)
                        settingsRefreshCall(individual, Array.Empty<object>());
                    if (dialogSharedRef(window) is { } shared)
                        settingsRefreshCall(shared, Array.Empty<object>());
                    getPathIndicesCall(window, new[] { dialogActiveTargetGetter.Invoke(window, null) });
                }

                dialogRefreshAllGraphics();
            }
            catch (Exception e)
            {
                Log.Warning($"{HeraldryLog}: refreshing the heraldry windows or graphics failed: {e}");
            }
        }

        #endregion
    }
}
