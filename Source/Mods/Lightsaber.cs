using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Star Wars : The Force - Lightsaber by Lee</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3466124712"/>
    /// <remarks>
    /// Determinism only. Written against the decompiled Lightsaber.StatPart_EquippedStatOffsetIncrease (mod 3466124712,
    /// the 1.6 Lightsaber.dll the 1.7 lab loads). Root cause of the world desync seen in lab run mp20:
    /// StatPart_EquippedStatOffsetIncrease keeps two process-lifetime statics, <c>statCacheDict</c>
    /// (Dictionary&lt;Pawn, Dictionary&lt;StatDef, EquipmentStatCache&gt;&gt;) and <c>compCache</c> (CompCache, Thing -> comp).
    /// Neither is cleared on load. The nested <c>EquipmentStatCache</c> constructor calls <c>Reset()</c>, which draws
    /// <c>recacheTick = TicksGame + 250 + Rand.Range(-50, 50)</c> from the WORLD Rand. After the join-point reload the
    /// host (same process) still holds its pre-reload entries, so it creates fewer entries than a fresh arbiter or
    /// joiner, which starts with empty statics and creates one per (pawn, stat) the first time that stat is read. The
    /// peers therefore make a different number of world Rand calls (mp20 ledger_diff: extra arbiter Rand.Int in
    /// EquipmentStatCache.Reset; the host drew once, the arbiter twice), and the world desyncs 34 ticks after the join.
    /// Even with equal counts the entries are created lazily from stat reads, which are not a function of the game
    /// state alone (UI and render reads happen on the machine that draws), so the draw can never be left on the world Rand.
    /// Fix 1: in MP, a Game.LoadGame / Game.InitNewGame prefix clears <c>statCacheDict</c> (the mod's own static
    /// <c>ClearAllCaches()</c>) and <c>compCache</c> (CompCache.ClearAllCaches()), so every peer starts the new game
    /// with empty caches and no entry of a pre-reload Pawn survives for the reloaded one.
    /// Fix 2: in MP, <c>EquipmentStatCache.Reset</c> runs inside <c>Rand.PushState(seed)</c> / <c>Rand.PopState()</c>,
    /// seed = hash of the cache's own Pawn.thingIDNumber, StatDef.defName (StableStringHash) and TicksGame, so the
    /// recache jitter is a pure function of (pawn, stat, tick) and no longer touches the world Rand at all, however
    /// many entries a peer creates. Each cache holds a Pawn and a StatDef, so no order-dependent counter is needed.
    /// A null Pawn/StatDef (the mod builds <c>new EquipmentStatCache(null, null, null)</c> for a null request) hashes as 0.
    /// Status: mp20 ledger_diff: extra arbiter Rand.Int in EquipmentStatCache.Reset; not run in game yet.
    /// Everything is MP only: outside a session nothing changes.
    /// </remarks>
    [MpCompatFor("lee.theforce.lightsaber")]
    public class Lightsaber
    {
        private const string LogPrefix = "[MPCompat - Lightsaber] ";
        private const string SeedKey = "MpCompat.Lightsaber.EquipmentStatCache.Reset";

        // Lightsaber.StatPart_EquippedStatOffsetIncrease
        private static MethodInfo clearAllCachesMethod;
        private static FieldInfo compCacheField;

        // Lightsaber.CompCache.ClearAllCaches() (instance)
        private static MethodInfo compCacheClearMethod;

        // Lightsaber.StatPart_EquippedStatOffsetIncrease.EquipmentStatCache (private nested class)
        private static FieldInfo cachePawnField;
        private static FieldInfo cacheStatDefField;

        public Lightsaber(ModContentPack mod)
        {
            var statPartType = FindType("Lightsaber.StatPart_EquippedStatOffsetIncrease");
            var compCacheType = FindType("Lightsaber.CompCache");
            var cacheType = AccessTools.Inner(statPartType, "EquipmentStatCache")
                            ?? throw new TypeLoadException($"{LogPrefix}type {statPartType.FullName}+EquipmentStatCache not found");

            clearAllCachesMethod = GetDeclaredMethod(statPartType, "ClearAllCaches");
            if (!clearAllCachesMethod.IsStatic || clearAllCachesMethod.GetParameters().Length != 0)
                throw new MissingMethodException($"{LogPrefix}{statPartType.FullName}.ClearAllCaches is not a static parameterless method");

            // The static dictionary itself is cleared by ClearAllCaches(); check it is still there so a rename is loud.
            var statCacheDictField = GetStaticField(statPartType, "statCacheDict");
            if (!typeof(System.Collections.IDictionary).IsAssignableFrom(statCacheDictField.FieldType))
                throw new MissingFieldException($"{LogPrefix}{statPartType.FullName}.statCacheDict is {statCacheDictField.FieldType}, expected a dictionary");

            compCacheField = GetStaticField(statPartType, "compCache");
            if (compCacheField.FieldType != compCacheType)
                throw new MissingFieldException($"{LogPrefix}{statPartType.FullName}.compCache is {compCacheField.FieldType}, expected {compCacheType.FullName}");
            compCacheClearMethod = GetDeclaredMethod(compCacheType, "ClearAllCaches");
            if (compCacheClearMethod.IsStatic || compCacheClearMethod.GetParameters().Length != 0)
                throw new MissingMethodException($"{LogPrefix}{compCacheType.FullName}.ClearAllCaches is not an instance parameterless method");

            cachePawnField = GetInstanceField(cacheType, "Pawn", typeof(Pawn));
            cacheStatDefField = GetInstanceField(cacheType, "StatDef", typeof(StatDef));

            // Fix 1: empty statics before anything of the new game is loaded or created.
            MpCompat.harmony.Patch(GetDeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
                prefix: new HarmonyMethod(typeof(Lightsaber), nameof(PreGameStart)));
            MpCompat.harmony.Patch(GetDeclaredMethod(typeof(Game), nameof(Game.InitNewGame)),
                prefix: new HarmonyMethod(typeof(Lightsaber), nameof(PreGameStart)));

            // Fix 2: the recache jitter draws from a seeded state, never from the world Rand.
            MpCompat.harmony.Patch(GetDeclaredMethod(cacheType, "Reset"),
                prefix: new HarmonyMethod(typeof(Lightsaber), nameof(PreReset)),
                finalizer: new HarmonyMethod(typeof(Lightsaber), nameof(FinalizeReset)));
        }

        private static void PreGameStart()
        {
            if (!MP.IsInMultiplayer)
                return;

            clearAllCachesMethod.Invoke(null, null);

            // compCache is initialised inline, but it is read at call time in case the mod ever replaces it.
            var compCache = compCacheField.GetValue(null);
            if (compCache != null)
                compCacheClearMethod.Invoke(compCache, null);
        }

        private static void PreReset(object __instance, ref bool __state)
        {
            if (!MP.IsInMultiplayer)
                return;

            var pawn = (Pawn)cachePawnField.GetValue(__instance);
            var statDef = (StatDef)cacheStatDefField.GetValue(__instance);
            var ticks = Current.Game?.tickManager?.TicksGame ?? 0;

            // Same on every peer: the cache's pawn id, the stat's defName and the saved game tick.
            var seed = Gen.HashCombineInt(GenText.StableStringHash(SeedKey), pawn?.thingIDNumber ?? 0);
            seed = Gen.HashCombineInt(seed, statDef != null ? GenText.StableStringHash(statDef.defName) : 0);
            seed = Gen.HashCombineInt(seed, ticks);

            Rand.PushState(seed);
            __state = true;
        }

        private static void FinalizeReset(bool __state)
        {
            if (__state)
                Rand.PopState();
        }

        #region Helpers

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name)
            => AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

        private static FieldInfo GetStaticField(Type type, string name)
        {
            var field = AccessTools.DeclaredField(type, name);
            if (field == null || !field.IsStatic)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static FieldInfo GetInstanceField(Type type, string name, Type expectedType)
        {
            var field = AccessTools.DeclaredField(type, name);
            if (field == null || field.IsStatic)
                throw new MissingFieldException(type.FullName, name);
            if (field.FieldType != expectedType)
                throw new MissingFieldException($"{LogPrefix}{type.FullName}.{name} is {field.FieldType}, expected {expectedType}");
            return field;
        }

        #endregion
    }
}
