using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimJobWorld by Ed86</summary>
    /// <remarks>
    /// Determinism only (births). Written against RJW.dll from <c>S:/Rimworld MP shiz/lab/game/Mods/rjw-master/1.6/Assemblies/RJW.dll</c>.
    /// Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entry 13, token 0x0600050C): <c>rjw.Hediff_BasePregnancy::GenerateBabies(DnaGivingParent)</c>
    /// is <c>[SyncMethod(SyncContext.None)]</c> (it runs on every client) but starts with <c>Random random = new Random();</c> and uses
    /// <c>random.Next(0, list2.Count)</c> / <c>random.Next(0, list3.Count)</c> to pick the baby's inherited traits (four sites). SyncContext.None
    /// only means "no map/world seed is forced", it does not touch System.Random, so each client drew its own traits for the same baby.
    /// Fix: the <c>newobj System.Random::.ctor</c> becomes <see cref="PatchingUtilities.RandRedirector"/> (no push/pop: the method runs
    /// from the synced hediff tick, where Verse.Rand is the synced state; the other randomness in the method already uses Rand / RandomElement).
    /// The only other System.Random in RJW.dll that the sweep found is this one. Status: [unverified] - compiled only, not run in game yet.
    ///
    /// Permission flags (FIXES row 95). <c>rjw.PawnData</c> saves seven derived booleans (<c>CanChangeDesignationColonist</c>, <c>CanChangeDesignationPrisoner</c>,
    /// <c>CanDesignateService/Comfort/BreedingAnimal/Breeding/Hero</c>, <c>forceSave: true</c> in <c>PawnData.ExposeData</c>). They are a cache of a function of the
    /// pawn, written only by the <c>PawnDesignations_*.UpdateCan*</c> methods, and those run from interface code (<c>PawnSelect</c> = prefix on
    /// <c>Selector.Select</c>, the RJW main-tab pawn tables' sort functions, <c>PawnColumnCheckbox.DoCell</c> every 60 ticks, the <c>Toggle*</c> gizmo handlers),
    /// so each PC stores what its own UI last asked for (lab: host True / headless arbiter False on two wild animals in both join-point saves). Every reader found in
    /// RJW.dll is interface code (gizmos, float menu, main tab, <c>RJWdesignations</c> icons, <c>SexGizmo</c>) or the <c>Update*</c> chain itself, and no other loaded dll
    /// names these members, so the stored value is never needed by the simulation - only its divergence is visible (saves, join point).
    /// Fix, in MP only: the seven <c>Update*</c> writers leave the stored flag as it was (prefix remembers it, postfix puts it back), and the seven getters answer by
    /// running the same <c>Update*</c> logic and putting the stored flag back. Not "store only in synced code": <c>SyncHero</c> runs <c>UpdatePermissions</c> inside
    /// the synced command and <c>IsHeroOwner</c> compares with <c>MP.PlayerName</c>, so even a synced store holds a per-PC value for a hero's pawn.
    /// The Hero getter cannot call its writer, because <c>UpdateCanDesignateHero</c> also issues the synced <c>SetHeroIronman</c> / <c>UnDesignateHero</c>
    /// (a getter polled every frame would flood the command queue): it gets a side-effect-free copy of the same test; the writer keeps those calls (owner's UI only, as upstream).
    /// <c>CanDesignateMilking</c> is not touched: its writer only ever stores false and nothing calls it. Outside MP every patch runs the original untouched.
    /// Status: [unverified] - compiled, and the patch mechanics ran against a stand-in copy of the RJW bodies on Harmony 3.0 / .NET 10 (waylith/_lanes/d95_rjw/test, 21,019 checks
    /// incl. a 3,000-state equivalence sweep against the unpatched code); not run in game yet.
    /// </remarks>
    [MpCompatFor("rim.job.world")]
    public class RimJobWorld
    {
        private const string LogPrefix = "[MPCompat - RimJobWorld] ";

        // One saved permission flag: the PawnData field and the PawnDesignations_*.UpdateCan* writer that fills it from the pawn.
        private sealed class PermissionFlag
        {
            private readonly AccessTools.FieldRef<object, bool> field;
            private readonly Func<Pawn, bool> update;
            private readonly Func<Pawn, object, bool> pureOverride;

            public PermissionFlag(AccessTools.FieldRef<object, bool> field, Func<Pawn, bool> update, Func<Pawn, object, bool> pureOverride)
            {
                this.field = field;
                this.update = update;
                this.pureOverride = pureOverride;
            }

            public bool Get(object data) => field(data);

            public void Set(object data, bool value) => field(data) = value;

            // The value the writer would produce for this pawn right now, with the stored flag left as it was.
            public bool Compute(Pawn pawn, object data)
            {
                if (pureOverride != null) return pureOverride(pawn, data);
                var old = field(data);
                try { return update(pawn); }
                finally { field(data) = old; }
            }
        }

        // MetadataToken of every patched getter / writer -> its flag (tokens are unique inside RJW.dll).
        private static readonly Dictionary<int, PermissionFlag> permissionByMethodToken = new();
        private static Func<Pawn, object> getPawnData;

        // Inputs of the side-effect-free copy of UpdateCanDesignateHero.
        private static FieldInfo rpgHeroControl;
        private static Func<Pawn, bool> isHuman, isSlave, isDesignatedHero, isHeroOwner;
        private static AccessTools.FieldRef<object, bool> heroIronman;
        private static List<Pawn> heroPawns;

        public RimJobWorld(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("rjw.Hediff_BasePregnancy")
                ?? throw new TypeLoadException($"{LogPrefix}type rjw.Hediff_BasePregnancy not found");
            var method = AccessTools.DeclaredMethod(type, "GenerateBabies")
                ?? throw new MissingMethodException(type.FullName, "GenerateBabies");

            PatchingUtilities.PatchSystemRand(method, false);

            PatchPermissionFlags();
        }

        private static void PatchPermissionFlags()
        {
            var pawnDataType = AccessTools.TypeByName("rjw.PawnData")
                ?? throw new TypeLoadException($"{LogPrefix}type rjw.PawnData not found");
            var getData = AccessTools.DeclaredMethod(AccessTools.TypeByName("rjw.PawnExtensions") ?? throw new TypeLoadException($"{LogPrefix}type rjw.PawnExtensions not found"),
                "GetRJWPawnData", [typeof(Pawn)]) ?? throw new MissingMethodException("rjw.PawnExtensions", "GetRJWPawnData");
            getPawnData = AccessTools.MethodDelegate<Func<Pawn, object>>(getData);

            SetupHeroInputs(pawnDataType);

            // (static class that holds the getter and the writer, flag name after "Can"/"UpdateCan", is the Hero flag)
            var flags = new (string type, string name, bool hero)[]
            {
                ("rjw.PawnDesignations_Utility", "ChangeDesignationColonist", false),
                ("rjw.PawnDesignations_Utility", "ChangeDesignationPrisoner", false),
                ("rjw.PawnDesignations_Service", "DesignateService", false),
                ("rjw.PawnDesignations_Comfort", "DesignateComfort", false),
                ("rjw.PawnDesignations_Breeder", "DesignateBreedingAnimal", false),
                ("rjw.PawnDesignations_Breedee", "DesignateBreeding", false),
                ("rjw.PawnDesignations_Hero", "DesignateHero", true),
            };

            var harmony = MpCompat.harmony;
            foreach (var (typeName, name, hero) in flags)
            {
                var type = AccessTools.TypeByName(typeName) ?? throw new TypeLoadException($"{LogPrefix}type {typeName} not found");
                var getter = AccessTools.DeclaredMethod(type, "Can" + name, [typeof(Pawn)]) ?? throw new MissingMethodException(typeName, "Can" + name);
                var writer = AccessTools.DeclaredMethod(type, "UpdateCan" + name, [typeof(Pawn)]) ?? throw new MissingMethodException(typeName, "UpdateCan" + name);
                var field = AccessTools.DeclaredField(pawnDataType, "Can" + name) ?? throw new MissingFieldException(pawnDataType.FullName, "Can" + name);

                var flag = new PermissionFlag(AccessTools.FieldRefAccess<object, bool>(field), AccessTools.MethodDelegate<Func<Pawn, bool>>(writer),
                    hero ? ComputeCanDesignateHero : null);
                permissionByMethodToken[getter.MetadataToken] = flag;
                permissionByMethodToken[writer.MetadataToken] = flag;

                harmony.Patch(getter, prefix: new HarmonyMethod(typeof(RimJobWorld), nameof(PreGetPermission)));
                harmony.Patch(writer,
                    prefix: new HarmonyMethod(typeof(RimJobWorld), nameof(PreUpdatePermission)),
                    postfix: new HarmonyMethod(typeof(RimJobWorld), nameof(PostUpdatePermission)));
            }
        }

        private static void SetupHeroInputs(Type pawnDataType)
        {
            const string settingsName = "rjw.RJWSettings", xxxName = "rjw.xxx", heroName = "rjw.PawnDesignations_Hero", dataName = "rjw.DesignatorsData";
            var settingsType = AccessTools.TypeByName(settingsName) ?? throw new TypeLoadException($"{LogPrefix}type {settingsName} not found");
            var xxxType = AccessTools.TypeByName(xxxName) ?? throw new TypeLoadException($"{LogPrefix}type {xxxName} not found");
            var heroType = AccessTools.TypeByName(heroName) ?? throw new TypeLoadException($"{LogPrefix}type {heroName} not found");
            var dataType = AccessTools.TypeByName(dataName) ?? throw new TypeLoadException($"{LogPrefix}type {dataName} not found");

            rpgHeroControl = AccessTools.DeclaredField(settingsType, "RPG_hero_control") ?? throw new MissingFieldException(settingsName, "RPG_hero_control");
            isHuman = PawnPredicate(xxxType, "is_human");
            isSlave = PawnPredicate(xxxType, "is_slave");
            isDesignatedHero = PawnPredicate(heroType, "IsDesignatedHero");
            isHeroOwner = PawnPredicate(heroType, "IsHeroOwner");
            heroIronman = AccessTools.FieldRefAccess<object, bool>(AccessTools.DeclaredField(pawnDataType, "Ironman") ?? throw new MissingFieldException(pawnDataType.FullName, "Ironman"));
            // static readonly: the same list object for the whole session (DesignatorsData.ClearAll clears it, never replaces it)
            heroPawns = AccessTools.DeclaredField(dataType, "rjwHero")?.GetValue(null) as List<Pawn> ?? throw new MissingFieldException(dataName, "rjwHero");
        }

        private static Func<Pawn, bool> PawnPredicate(Type type, string name)
            => AccessTools.MethodDelegate<Func<Pawn, bool>>(AccessTools.DeclaredMethod(type, name, [typeof(Pawn)]) ?? throw new MissingMethodException(type.FullName, name));

        // Same test as PawnDesignations_Hero.UpdateCanDesignateHero minus its two synced calls (SetHeroIronman, UnDesignateHero of a dead hero):
        // the loop there continues after UnDesignateHero, so a dead hero of this player only blocks when the candidate's Ironman flag is set.
        private static bool ComputeCanDesignateHero(Pawn pawn, object data)
        {
            if (!(bool)rpgHeroControl.GetValue(null) || !isHuman(pawn) || !pawn.IsColonist || isSlave(pawn) || pawn.IsPrisoner || isDesignatedHero(pawn))
                return false;

            var ironman = heroIronman(data);
            for (var i = 0; i < heroPawns.Count; i++)
            {
                var hero = heroPawns[i];
                if (isHeroOwner(hero) && (!hero.Dead || ironman))
                    return false;
            }
            return true;
        }

        // In MP the getter answers from the pawn's current state and never from the saved field (which only the interface fills).
        private static bool PreGetPermission(Pawn pawn, MethodBase __originalMethod, ref bool __result)
        {
            if (pawn == null || !MP.IsInMultiplayer || !permissionByMethodToken.TryGetValue(__originalMethod.MetadataToken, out var flag))
                return true;

            var data = getPawnData(pawn);
            // no PawnData: the original getter says false without logging, so does this
            __result = data != null && flag.Compute(pawn, data);
            return false;
        }

        // In MP a writer still runs (UpdatePermissions and the Toggle* handlers rely on its return value and, for Hero, its synced calls) but the saved flag is put back.
        private static void PreUpdatePermission(Pawn pawn, MethodBase __originalMethod, out (PermissionFlag flag, object data, bool value) __state)
        {
            __state = default;
            if (pawn == null || !MP.IsInMultiplayer || !permissionByMethodToken.TryGetValue(__originalMethod.MetadataToken, out var flag))
                return;

            var data = getPawnData(pawn);
            if (data != null)
                __state = (flag, data, flag.Get(data));
        }

        private static void PostUpdatePermission((PermissionFlag flag, object data, bool value) __state)
        {
            __state.flag?.Set(__state.data, __state.value);
        }
    }
}
