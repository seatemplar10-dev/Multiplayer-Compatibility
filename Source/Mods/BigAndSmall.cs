using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Big and Small - Framework by RedMattis</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2925432336"/>
    /// <see href="https://github.com/RedMattis/BigSmall_Framework"/>
    /// <remarks>
    /// Written against BigAndSmall.dll from 1.6/Base (the folder 1.7 falls back to), sha256 c774509634b8...
    /// Why the mod desyncs in Multiplayer as shipped (the join-point reload is the trigger: host and existing
    /// clients reload their own save, a joiner loads the host's save in a fresh process):
    /// 1. BS.internalTick (+Tick10/Tick100) is a process-lifetime static, advanced once per
    ///    BigAndSmallCache.GameComponentTick, never saved and never reset on load. BigAndSmallCache runs
    ///    SlowUpdate when it is a multiple of 100 (refresh queue every 5th, gene override pass every 4th,
    ///    jesus mode every 10th), so host and joiner run them at different game ticks after a join.
    ///    Fix: the counter is written into the MP save and restored on load (0 when the key is absent).
    /// 2. BigAndSmallCache.globalRandNum (set every SlowUpdate) decides raid infiltrators in
    ///    GeneratePawns_Patch.TrySetInfiltrator and whether Rand.Range is called there. Not saved either.
    ///    Fix: saved as raw float bits with the counter, default 1 (its initializer) when absent.
    /// 3. refreshQueue and frequentUpdateGenes are statics that survive the reload, so the host keeps
    ///    pre-reload Pawn/Gene objects (refresh / Notify_GenesChanged on stale objects) while a joiner
    ///    starts empty; regenerationAttempted (one-shot cache wipe on an exception) likewise.
    ///    Fix: reset in a Game.LoadGame prefix, before anything of the new game is loaded.
    /// 4. The refresh queue is filled from DictCache.Cache.Values, a ConcurrentDictionary whose order depends
    ///    on its bucket growth, which depends on its lock count = Environment.ProcessorCount by default.
    ///    Two PCs with different core counts refresh pawns in a different order (DelayedUpdate adds/removes
    ///    hediffs, drops apparel, repairs apparel by elapsed time). Fix: refill sorted by thingIDNumber.
    /// 5. HumanoidPawnScaler.GetInvalidateLater (called from simulation on every hediff add/remove, equip,
    ///    gene change and SlowUpdate) picks "refresh in N ticks" or "refresh now" from TickManager.Paused
    ///    (MP patches it to the VIEWED map's speed, vanilla when the world view is open) and
    ///    TickManager.NotPlaying (this player's escape menu is open). Fix: in MP, paused = the synced
    ///    CurTimeSpeed of the executing context and NotPlaying = false.
    /// 6. DictCache&lt;Pawn, BSCache&gt;.JunkCache is a static that is never cleared (BigAndSmallCache.ExposeData
    ///    clears DictCache.Cache only), and Pawn equality is def + thingIDNumber, so after a reload the host's
    ///    lookups for a reloaded pawn hit the cache object of the PRE-reload Pawn. GetCacheInner returns it
    ///    without "new entry", GetCache then skips CheckForScribedEntry (always for canRegenerate, and for a
    ///    junk entry that was regenerated since), so the refresh that gets scheduled is for the stale Pawn:
    ///    the host's DelayedUpdate (forced hediffs, apparel, genes, traits) runs on a ghost object, the joiner's
    ///    on the real pawn. Seen in lab run mp18: the VQE inventor (Genetron_GameComponent.inventor, gene
    ///    guy762_statgene_PsyHarmonize) got guy762_Hediff_PsyHarmonize on the arbiter 101 ticks after the join,
    ///    never on the host. Metamorphosis.pawnsQueuedForMorphing (HashSet of Pawn) has the same shape.
    ///    Fix: both cleared in the Game.LoadGame prefix.
    /// 7. Interface (render, UI) calls of HumanoidPawnScaler.GetCache create, register and refresh caches and
    ///    schedule simulation work (DelayedUpdate) on the machine that draws - a graphical host, never the
    ///    headless arbiter. Fix: in MP, while MP.InInterface, GetCache is read only (the existing cache or the
    ///    default one) and ShedueleForceRegenerate is skipped.
    /// 8. The Gene.ExposeData postfix registers every gene with a frequent-update extension into
    ///    frequentUpdateGenes with its current Active value, also when SAVING - only the host saves (join
    ///    points, autosaves). Fix: in MP, a save leaves frequentUpdateGenes as it was.
    /// 9. FIXES row 192 (26 Oct 2026): ScribedCache kept entries of pawns that exist on one PC only or no longer at all:
    ///    the multifaction starting-pawn page's pawns (generated on the joiner only, under ProgramState.Entry, so
    ///    MP.InInterface is false; GeneratePawns_Patch registers all 8) and a SendPawn pawn's junk entry made while its
    ///    command rebuilt it ("Human-4" on the host: Multiplayer renumbers the pawn only at the end of cross-ref
    ///    resolution). Lab round 1: ScribedCache 433 (host) vs 438 (joiner) at the first checkpoint after a Settle.
    ///    The page keeps B&S's normal caches (they shape the starting pawns: size, gear limits, race); in MP every save
    ///    first drops entries whose pawn has a negative thing id or no longer carries the entry's id, and entries
    ///    without a pawn whose id ends in a negative number. The set is rebuilt in its own order, so the PCs that keep
    ///    the same entries write them in the same order.
    /// Everything is MP only: outside a session nothing is written to saves and nothing changes.
    /// Not handled here: unsynced UI actions (drafted-pawn toggles, gene picker dialog, resource
    /// sliders, recolor window), and stale pre-reload Pawns that other mods' statics hand to Big and Small.
    /// </remarks>
    [MpCompatFor("RedMattis.BetterPrerequisites")]
    public class BigAndSmall
    {
        private const string LogPrefix = "[MPCompat - Big and Small] ";
        private const string TickKey = "MpCompat_BS_internalTick";
        private const string RandBitsKey = "MpCompat_BS_globalRandNumBits";
        // BigAndSmallCache.globalRandNum's field initializer.
        private const float DefaultGlobalRandNum = 1f;
        private static readonly int DefaultRandBits = BitConverter.SingleToInt32Bits(DefaultGlobalRandNum);

        // BigAndSmall.BS
        private static FieldInfo internalTickField;
        private static FieldInfo internalTick10Field;
        private static FieldInfo internalTick100Field;

        // BigAndSmall.BigAndSmallCache
        private static FieldInfo globalRandNumField;
        private static FieldInfo regenerationAttemptedField;
        private static FieldInfo refreshQueueField;
        private static FieldInfo frequentUpdateGenesField;

        // BigAndSmall.DictCache<Pawn, BSCache> and BigAndSmall.Metamorphosis
        private static FieldInfo junkCacheField;
        private static FieldInfo pawnsQueuedForMorphingField;

        // Interface read-only GetCache
        private static Func<object> pawnCacheGetter;
        private static FieldInfo defaultCacheField;
        private static ConstructorInfo bsCacheCtor;

        // Row 192 save-time purge (point 9)
        private static PropertyInfo scribedCacheProp;
        private static FieldInfo bsCachePawnField, bsCacheIdField;
        private static int purgeLogged;

        public BigAndSmall(ModContentPack mod)
        {
            PatchTickCounter();
            PatchRefreshOrder();
            PatchInvalidateLater();
            PatchStaleCaches();
            PatchInterfaceReadOnly();
            PatchGeneSaveHook();
        }

        #region Tick counter, globalRandNum, stale statics

        private static void PatchTickCounter()
        {
            var bsType = FindType("BigAndSmall.BS");
            var cacheType = FindType("BigAndSmall.BigAndSmallCache");

            internalTickField = GetStaticField(bsType, "internalTick", typeof(int));
            internalTick10Field = GetStaticField(bsType, "internalTick10", typeof(int));
            internalTick100Field = GetStaticField(bsType, "internalTick100", typeof(int));
            globalRandNumField = GetStaticField(cacheType, "globalRandNum", typeof(float));
            regenerationAttemptedField = GetStaticField(cacheType, "regenerationAttempted", typeof(bool));
            refreshQueueField = GetStaticField(cacheType, "refreshQueue", typeof(Queue<Pawn>));
            frequentUpdateGenesField = GetStaticField(cacheType, "frequentUpdateGenes", null);
            if (!typeof(IDictionary).IsAssignableFrom(frequentUpdateGenesField.FieldType))
                throw new MissingFieldException($"{LogPrefix}BigAndSmallCache.frequentUpdateGenes is {frequentUpdateGenesField.FieldType}, expected a dictionary");

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
                prefix: new HarmonyMethod(typeof(BigAndSmall), nameof(PreLoadGame)));
            MpCompat.harmony.Patch(GetDeclaredMethod(cacheType, nameof(GameComponent.ExposeData)),
                prefix: new HarmonyMethod(typeof(BigAndSmall), nameof(PreCacheExposeData)),
                postfix: new HarmonyMethod(typeof(BigAndSmall), nameof(PostCacheExposeData)));

            var bsCacheType = FindType("BigAndSmall.BSCache");
            scribedCacheProp = AccessTools.Property(cacheType, "ScribedCache")
                               ?? throw new MissingMemberException(cacheType.FullName, "ScribedCache");
            bsCachePawnField = AccessTools.Field(bsCacheType, "pawn");
            bsCacheIdField = AccessTools.Field(bsCacheType, "id");
            if (bsCachePawnField?.FieldType != typeof(Pawn) || bsCacheIdField?.FieldType != typeof(string))
                throw new MissingFieldException($"{LogPrefix}BSCache.pawn (Pawn) / BSCache.id (string) not found");
        }

        // Point 9: before the cache component writes ScribedCache, drop the entries of pawns that are not (or no longer) the entry's pawn.
        private static void PreCacheExposeData()
        {
            if (!MP.IsInMultiplayer || Scribe.mode != LoadSaveMode.Saving)
                return;

            if (scribedCacheProp.GetValue(null) is not IEnumerable set)
                return;

            var keep = new List<object>();
            var dropped = new List<string>();
            foreach (var entry in set)
            {
                if (entry == null) continue;
                var pawn = (Pawn)bsCachePawnField.GetValue(entry);
                var id = (string)bsCacheIdField.GetValue(entry);
                bool ghost = pawn != null
                    ? pawn.thingIDNumber < 0 || id != pawn.ThingID
                    : id != null && System.Text.RegularExpressions.Regex.IsMatch(id, @"-\d+$");
                if (ghost) dropped.Add(id); else keep.Add(entry);
            }

            if (dropped.Count == 0)
                return;

            // Rebuilt in enumeration order: Clear resets the set's slots, so the kept entries are written in the order they had.
            var setType = set.GetType();
            setType.GetMethod("Clear")!.Invoke(set, null);
            var add = setType.GetMethod("Add")!;
            foreach (var entry in keep)
                add.Invoke(set, new[] { entry });

            if (purgeLogged < 20)
            {
                purgeLogged++;
                Log.Message($"{LogPrefix}row 192: {dropped.Count} ScribedCache entr(ies) of page / pre-renumber pawns not saved: {string.Join(", ", dropped)}");
            }
        }

        // Runs before Game.ExposeSmallComponents, so everything the new game registers while loading is kept.
        private static void PreLoadGame()
        {
            if (!MP.IsInMultiplayer)
                return;

            SetCounter(0);
            globalRandNumField.SetValue(null, DefaultGlobalRandNum);
            regenerationAttemptedField.SetValue(null, false);
            RefreshQueue.Clear();
            ((IDictionary)frequentUpdateGenesField.GetValue(null))?.Clear();
            // Point 6: no cache object (or queued morph) of a pre-reload Pawn may answer for a reloaded one.
            ((IDictionary)junkCacheField?.GetValue(null))?.Clear();
            ClearPawnSet(pawnsQueuedForMorphingField?.GetValue(null));
        }

        private static void PostCacheExposeData()
        {
            if (!MP.IsInMultiplayer)
                return;

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var tick = (int)internalTickField.GetValue(null);
                var randBits = BitConverter.SingleToInt32Bits((float)globalRandNumField.GetValue(null));
                Scribe_Values.Look(ref tick, TickKey, 0);
                Scribe_Values.Look(ref randBits, RandBitsKey, DefaultRandBits);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                var tick = 0;
                var randBits = DefaultRandBits;
                Scribe_Values.Look(ref tick, TickKey, 0);
                Scribe_Values.Look(ref randBits, RandBitsKey, DefaultRandBits);

                SetCounter(tick);
                var rand = BitConverter.Int32BitsToSingle(randBits);
                globalRandNumField.SetValue(null, rand);
                Log.Message($"{LogPrefix}load: BS tick = {tick}, globalRandNum bits = {randBits:X8} ({rand:R})");
            }
        }

        // Same derived values BS.IncrementTick writes.
        private static void SetCounter(int tick)
        {
            internalTickField.SetValue(null, tick);
            internalTick10Field.SetValue(null, tick / 10);
            internalTick100Field.SetValue(null, tick / 100);
        }

        #endregion

        #region Stale pawn caches (point 6)

        private static void PatchStaleCaches()
        {
            var bsCacheType = FindType("BigAndSmall.BSCache");
            var dictCacheType = FindType("BigAndSmall.DictCache`2").MakeGenericType(typeof(Pawn), bsCacheType);
            junkCacheField = GetStaticField(dictCacheType, "JunkCache", null);
            if (!typeof(IDictionary).IsAssignableFrom(junkCacheField.FieldType))
                throw new MissingFieldException($"{LogPrefix}DictCache<Pawn, BSCache>.JunkCache is {junkCacheField.FieldType}, expected a dictionary");

            pawnsQueuedForMorphingField = GetStaticField(FindType("BigAndSmall.Metamorphosis"), "pawnsQueuedForMorphing", typeof(HashSet<Pawn>));
        }

        private static void ClearPawnSet(object set)
        {
            if (set is ICollection<Pawn> pawns)
                pawns.Clear();
        }

        #endregion

        #region Interface calls are read only (point 7)

        private static void PatchInterfaceReadOnly()
        {
            var scalerType = FindType("BigAndSmall.HumanoidPawnScaler");
            var bsCacheType = FindType("BigAndSmall.BSCache");
            var dictCacheType = FindType("BigAndSmall.DictCache`2").MakeGenericType(typeof(Pawn), bsCacheType);

            var cacheGetter = AccessTools.PropertyGetter(dictCacheType, "Cache")
                              ?? throw new MissingMethodException(dictCacheType.FullName, "get_Cache");
            if (!typeof(IDictionary).IsAssignableFrom(cacheGetter.ReturnType))
                throw new MissingMethodException($"{LogPrefix}DictCache<Pawn, BSCache>.Cache is {cacheGetter.ReturnType}, expected a dictionary");
            pawnCacheGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), cacheGetter);
            defaultCacheField = GetStaticField(bsCacheType, "defaultCache", bsCacheType);
            // An uncached pawn gets an UNREGISTERED temp cache bound to it (what GetCacheInner builds on a
            // canRegenerate:false miss, minus JunkCache/ScribedCache inserts and scheduling). Never defaultCache:
            // its pawn is null and B&S render helpers dereference cache.pawn (round-4 review must-fix).
            // GetConstructor, not AccessTools.Constructor: the latter walks base types to object..ctor on a miss.
            bsCacheCtor = bsCacheType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                              null, new[] { typeof(Pawn) }, null)
                          ?? throw new MissingMethodException(bsCacheType.FullName, ".ctor(Pawn)");
            var getCache = AccessTools.DeclaredMethod(scalerType, "GetCache",
                               new[] { typeof(Pawn), typeof(bool), typeof(bool), typeof(int), typeof(bool) })
                           ?? throw new MissingMethodException(scalerType.FullName, "GetCache(Pawn, bool, bool, int, bool)");
            if (getCache.ReturnType != bsCacheType)
                throw new MissingMethodException($"{LogPrefix}HumanoidPawnScaler.GetCache returns {getCache.ReturnType}, expected BSCache");
            var schedule = AccessTools.DeclaredMethod(scalerType, "ShedueleForceRegenerate", new[] { bsCacheType, typeof(int) })
                           ?? throw new MissingMethodException(scalerType.FullName, "ShedueleForceRegenerate(BSCache, int)");

            MpCompat.harmony.Patch(getCache, prefix: new HarmonyMethod(typeof(BigAndSmall), nameof(PreGetCache)));
            MpCompat.harmony.Patch(schedule, prefix: new HarmonyMethod(typeof(BigAndSmall), nameof(PreShedueleForceRegenerate)));
        }

        // Render threads and UI of the drawing machine only. Ticks, synced commands, loading and reloading are
        // not MP.InInterface and keep the mod's own behaviour on every machine.
        private static bool PreGetCache(Pawn pawn, ref object __result)
        {
            if (pawn == null || !MP.IsInMultiplayer || !MP.InInterface)
                return true;

            __result = ((IDictionary)pawnCacheGetter())[pawn] ?? bsCacheCtor.Invoke(new object[] { pawn });
            return false;
        }

        private static bool PreShedueleForceRegenerate() => !MP.IsInMultiplayer || !MP.InInterface;

        #endregion

        #region Gene.ExposeData while saving (point 8)

        private static void PatchGeneSaveHook()
        {
            frequentUpdateGenesField ??= GetStaticField(FindType("BigAndSmall.BigAndSmallCache"), "frequentUpdateGenes", null);
            var exposeData = AccessTools.DeclaredMethod(typeof(Gene), nameof(Gene.ExposeData))
                             ?? throw new MissingMethodException(typeof(Gene).FullName, nameof(Gene.ExposeData));
            // The prefix runs before, the postfix after every other patch (Big and Small's postfix included).
            MpCompat.harmony.Patch(exposeData,
                prefix: new HarmonyMethod(typeof(BigAndSmall), nameof(PreGeneExposeData)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(BigAndSmall), nameof(PostGeneExposeData)) { priority = Priority.Last });
        }

        // __state: null = nothing to restore; otherwise the entry as it was before the save (Present, Value).
        private static void PreGeneExposeData(Gene __instance, out object __state)
        {
            __state = null;
            if (!MP.IsInMultiplayer || Scribe.mode != LoadSaveMode.Saving || __instance == null)
                return;

            var genes = (IDictionary)frequentUpdateGenesField.GetValue(null);
            __state = genes.Contains(__instance) ? new SavedGeneEntry(true, genes[__instance]) : new SavedGeneEntry(false, null);
        }

        private static void PostGeneExposeData(Gene __instance, object __state)
        {
            if (__state is not SavedGeneEntry saved)
                return;

            var genes = (IDictionary)frequentUpdateGenesField.GetValue(null);
            if (saved.Present)
                genes[__instance] = saved.Value;
            else
                genes.Remove(__instance);
        }

        private sealed record SavedGeneEntry(bool Present, object Value);

        #endregion

        #region Refresh queue order

        private static void PatchRefreshOrder()
        {
            var cacheType = FindType("BigAndSmall.BigAndSmallCache");
            refreshQueueField ??= GetStaticField(cacheType, "refreshQueue", typeof(Queue<Pawn>));

            MpCompat.harmony.Patch(GetDeclaredMethod(cacheType, "SlowUpdate"),
                prefix: new HarmonyMethod(typeof(BigAndSmall), nameof(PreSlowUpdate)),
                postfix: new HarmonyMethod(typeof(BigAndSmall), nameof(PostSlowUpdate)));
        }

        // SlowUpdate fills the queue only when it is empty, and does nothing else with it in that call.
        private static void PreSlowUpdate(out bool __state) => __state = MP.IsInMultiplayer && RefreshQueue.Count == 0;

        private static void PostSlowUpdate(bool __state)
        {
            if (!__state)
                return;

            var queue = RefreshQueue;
            if (queue.Count < 2)
                return;

            // Stable sort: the same pawn twice keeps its relative order (identical work either way).
            var ordered = queue.OrderBy(p => p == null ? int.MinValue : p.thingIDNumber).ToList();
            queue.Clear();
            foreach (var pawn in ordered)
                queue.Enqueue(pawn);
        }

        #endregion

        #region Pause checks in GetInvalidateLater

        private static void PatchInvalidateLater()
        {
            var type = FindType("BigAndSmall.HumanoidPawnScaler");
            MpCompat.harmony.Patch(GetDeclaredMethod(type, "GetInvalidateLater"),
                transpiler: new HarmonyMethod(typeof(BigAndSmall), nameof(ReplacePauseChecks)));
        }

        private static IEnumerable<CodeInstruction> ReplacePauseChecks(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var paused = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.Paused));
            var notPlaying = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.NotPlaying));
            var syncedPaused = AccessTools.DeclaredMethod(typeof(BigAndSmall), nameof(SyncedPaused));
            var syncedNotPlaying = AccessTools.DeclaredMethod(typeof(BigAndSmall), nameof(SyncedNotPlaying));
            var replacedPaused = 0;
            var replacedNotPlaying = 0;

            var list = instr.ToList();
            foreach (var ci in list)
            {
                if ((ci.opcode != System.Reflection.Emit.OpCodes.Call && ci.opcode != System.Reflection.Emit.OpCodes.Callvirt) || ci.operand is not MethodInfo method)
                    continue;

                if (method == paused)
                {
                    ci.opcode = System.Reflection.Emit.OpCodes.Call;
                    ci.operand = syncedPaused;
                    replacedPaused++;
                }
                else if (method == notPlaying)
                {
                    ci.opcode = System.Reflection.Emit.OpCodes.Call;
                    ci.operand = syncedNotPlaying;
                    replacedNotPlaying++;
                }
            }

            // The decompiled method reads each property once; anything else means the mod changed.
            if (replacedPaused != 1 || replacedNotPlaying != 1)
                throw new Exception($"{LogPrefix}{baseMethod.DeclaringType?.FullName}.{baseMethod.Name}: replaced Paused x{replacedPaused}, NotPlaying x{replacedNotPlaying}, expected 1 and 1");

            return list;
        }

        // MP's PreContext sets CurTimeSpeed to the synced speed of the map/world being ticked or executing a command.
        private static bool SyncedPaused(TickManager tickManager)
            => MP.IsInMultiplayer ? tickManager.CurTimeSpeed == TimeSpeed.Paused : tickManager.Paused;

        // The escape menu does not pause Multiplayer; it is local UI state.
        private static bool SyncedNotPlaying(TickManager tickManager)
            => !MP.IsInMultiplayer && tickManager.NotPlaying;

        #endregion

        #region Helpers

        private static Queue<Pawn> RefreshQueue => (Queue<Pawn>)refreshQueueField.GetValue(null);

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name)
            => AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

        private static FieldInfo GetStaticField(Type type, string name, Type expectedType)
        {
            var field = AccessTools.DeclaredField(type, name);
            if (field == null || !field.IsStatic)
                throw new MissingFieldException(type.FullName, name);
            if (expectedType != null && field.FieldType != expectedType)
                throw new MissingFieldException($"{LogPrefix}{type.FullName}.{name} is {field.FieldType}, expected {expectedType}");
            return field;
        }

        #endregion
    }
}
