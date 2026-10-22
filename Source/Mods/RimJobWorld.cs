using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
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
    ///
    /// Right-click sex orders (FIXES row 209). RJW 6.2.1 registers its own <c>[SyncMethod] rjw.RMB.RMB_Menu.HaveSex(Pawn, JobDef, LocalTargetInfo, InteractionDef,
    /// SexInteractionResolved)</c>; the last argument was added after RJW's MP support was written and MP has no writer for it, so every right-click sex order threw
    /// "No writer for type ...SexInteractionResolved" on the clicking PC and was never sent (live 22 Oct). Fix: a sync worker for <c>SexInteractionResolved</c>
    /// (pawns, interaction def, score, the chosen parts as part hediffs or body parts, penetrations as indices into those lists). Every PC runs the clicker's
    /// choice; letting each PC resolve it again would read each PC's own RJW preference settings. A part that is gone when the command runs throws on every PC
    /// (order lost, no split). Status: [unverified] - compiled only, not run in game yet.
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

            SetupSexInteractionResolved();
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

        #region SexInteractionResolved (row 209)

        // rjw.Modules.Interactions.SexInteractionResolved and the part types it holds
        private static FieldInfo resolvedInitiator, resolvedRecipient, resolvedInteraction, resolvedInitiatorParts, resolvedRecipientParts, resolvedPenetrations, resolvedScore;
        private static FieldInfo[] scoreFields;
        private static MethodInfo interactionDefGetter, partFamilyGetter, partOwnerGetter, partBodyPartGetter, sexPartAsHediffGetter, penetratorGetter, orificeGetter;
        private static ConstructorInfo interactionCtor, rjwPartCtor, vanillaPartCtor, penetrationCtor;
        private static FieldInfo rjwPartSexPart;
        private static Type sexPartHediffType, rjwPartType, vanillaPartType, partFamilyType;

        // Part kinds on the wire
        private const byte NoPart = 0, RjwPart = 1, VanillaPart = 2;
        // Penetration ends on the wire: an index into one of the two part lists, or the part itself (Sex_Sixtynine picks mouths that are in neither list)
        private const byte NoRef = 0, InitiatorRef = 1, RecipientRef = 2, InlineRef = 3;

        private static void SetupSexInteractionResolved()
        {
            const string ns = "rjw.Modules.Interactions.";
            var resolvedType = TypeOrThrow(ns + "SexInteractionResolved");
            var interactionType = TypeOrThrow(ns + "SexInteraction");
            var partType = TypeOrThrow(ns + "ILewdablePart");
            rjwPartType = TypeOrThrow(ns + "RJWLewdablePart");
            vanillaPartType = TypeOrThrow(ns + "VanillaLewdablePart");
            partFamilyType = TypeOrThrow(ns + "LewdablePartFamily");
            sexPartHediffType = TypeOrThrow("rjw.ISexPartHediff");

            resolvedInitiator = FieldOrThrow(resolvedType, "Initiator");
            resolvedRecipient = FieldOrThrow(resolvedType, "Recipient");
            resolvedInteraction = FieldOrThrow(resolvedType, "Interaction");
            resolvedInitiatorParts = FieldOrThrow(resolvedType, "InitiatorParts");
            resolvedRecipientParts = FieldOrThrow(resolvedType, "RecipientParts");
            resolvedPenetrations = FieldOrThrow(resolvedType, "Penetrations");
            resolvedScore = FieldOrThrow(resolvedType, "Score");
            scoreFields =
            [
                FieldOrThrow(resolvedScore.FieldType, "initiatorScore"),
                FieldOrThrow(resolvedScore.FieldType, "recieverScore"),
                FieldOrThrow(resolvedScore.FieldType, "settingsScore"),
                FieldOrThrow(resolvedScore.FieldType, "totalScore"),
            ];

            interactionDefGetter = AccessTools.PropertyGetter(interactionType, "Def") ?? throw new MissingMethodException(interactionType.FullName, "get_Def");
            interactionCtor = AccessTools.DeclaredConstructor(interactionType, [typeof(InteractionDef)]) ?? throw new MissingMethodException(interactionType.FullName, ".ctor(InteractionDef)");
            partFamilyGetter = AccessTools.PropertyGetter(partType, "Family") ?? throw new MissingMethodException(partType.FullName, "get_Family");
            partOwnerGetter = AccessTools.PropertyGetter(partType, "Owner") ?? throw new MissingMethodException(partType.FullName, "get_Owner");
            partBodyPartGetter = AccessTools.PropertyGetter(partType, "BodyPart") ?? throw new MissingMethodException(partType.FullName, "get_BodyPart");
            sexPartAsHediffGetter = AccessTools.PropertyGetter(sexPartHediffType, "AsHediff") ?? throw new MissingMethodException(sexPartHediffType.FullName, "get_AsHediff");
            rjwPartSexPart = FieldOrThrow(rjwPartType, "SexPart");
            rjwPartCtor = AccessTools.DeclaredConstructor(rjwPartType, [sexPartHediffType, partFamilyType]) ?? throw new MissingMethodException(rjwPartType.FullName, ".ctor");
            vanillaPartCtor = AccessTools.DeclaredConstructor(vanillaPartType, [typeof(Pawn), typeof(BodyPartRecord), partFamilyType]) ?? throw new MissingMethodException(vanillaPartType.FullName, ".ctor");

            // List<Tuple<ILewdablePart, ILewdablePart>> = (penetrator, orifice)
            var penetrationType = resolvedPenetrations.FieldType.GetGenericArguments()[0];
            penetratorGetter = AccessTools.PropertyGetter(penetrationType, "Item1") ?? throw new MissingMethodException(penetrationType.FullName, "get_Item1");
            orificeGetter = AccessTools.PropertyGetter(penetrationType, "Item2") ?? throw new MissingMethodException(penetrationType.FullName, "get_Item2");
            penetrationCtor = AccessTools.DeclaredConstructor(penetrationType, [partType, partType]) ?? throw new MissingMethodException(penetrationType.FullName, ".ctor");

            MP.RegisterSyncWorker<object>(SyncSexInteractionResolved, resolvedType, shouldConstruct: true);
        }

        private static Type TypeOrThrow(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static FieldInfo FieldOrThrow(Type type, string name) => AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static void SyncSexInteractionResolved(SyncWorker sync, ref object resolved)
        {
            if (sync.isWriting)
            {
                // HaveSex's argument defaults to null; MP gives a registered class type no null marker of its own
                sync.Write(resolved != null);
                if (resolved == null)
                    return;

                sync.Write((Pawn)resolvedInitiator.GetValue(resolved));
                sync.Write((Pawn)resolvedRecipient.GetValue(resolved));
                var interaction = resolvedInteraction.GetValue(resolved);
                sync.Write(interaction == null ? null : (InteractionDef)interactionDefGetter.Invoke(interaction, []));

                var initiatorParts = (IList)resolvedInitiatorParts.GetValue(resolved);
                var recipientParts = (IList)resolvedRecipientParts.GetValue(resolved);
                WritePartList(sync, initiatorParts);
                WritePartList(sync, recipientParts);

                var penetrations = (IList)resolvedPenetrations.GetValue(resolved);
                sync.Write(penetrations?.Count ?? -1);
                if (penetrations != null)
                {
                    foreach (var penetration in penetrations)
                    {
                        WritePartRef(sync, penetratorGetter.Invoke(penetration, []), initiatorParts, recipientParts);
                        WritePartRef(sync, orificeGetter.Invoke(penetration, []), initiatorParts, recipientParts);
                    }
                }

                var score = resolvedScore.GetValue(resolved);
                sync.Write(score != null);
                if (score != null)
                {
                    foreach (var field in scoreFields)
                        sync.Write((float)field.GetValue(score));
                }
            }
            else
            {
                if (!sync.Read<bool>())
                {
                    resolved = null;
                    return;
                }

                resolvedInitiator.SetValue(resolved, sync.Read<Pawn>());
                resolvedRecipient.SetValue(resolved, sync.Read<Pawn>());
                var def = sync.Read<InteractionDef>();
                resolvedInteraction.SetValue(resolved, def == null ? null : interactionCtor.Invoke([def]));

                var initiatorParts = ReadPartList(sync, resolvedInitiatorParts.FieldType);
                var recipientParts = ReadPartList(sync, resolvedRecipientParts.FieldType);
                resolvedInitiatorParts.SetValue(resolved, initiatorParts);
                resolvedRecipientParts.SetValue(resolved, recipientParts);

                var count = sync.Read<int>();
                if (count >= 0)
                {
                    var penetrations = (IList)Activator.CreateInstance(resolvedPenetrations.FieldType);
                    for (var i = 0; i < count; i++)
                    {
                        var penetrator = ReadPartRef(sync, initiatorParts, recipientParts);
                        var orifice = ReadPartRef(sync, initiatorParts, recipientParts);
                        penetrations.Add(penetrationCtor.Invoke([penetrator, orifice]));
                    }
                    resolvedPenetrations.SetValue(resolved, penetrations);
                }

                if (sync.Read<bool>())
                {
                    var score = Activator.CreateInstance(resolvedScore.FieldType);
                    foreach (var field in scoreFields)
                        field.SetValue(score, sync.Read<float>());
                    resolvedScore.SetValue(resolved, score);
                }
            }
        }

        private static void WritePartList(SyncWorker sync, IList parts)
        {
            sync.Write(parts?.Count ?? -1);
            if (parts == null)
                return;
            foreach (var part in parts)
                WritePart(sync, part);
        }

        private static IList ReadPartList(SyncWorker sync, Type listType)
        {
            var count = sync.Read<int>();
            if (count < 0)
                return null;
            var parts = (IList)Activator.CreateInstance(listType);
            for (var i = 0; i < count; i++)
                parts.Add(ReadPart(sync));
            return parts;
        }

        private static void WritePart(SyncWorker sync, object part)
        {
            var kind = part == null ? NoPart : rjwPartType.IsInstanceOfType(part) ? RjwPart : VanillaPart;
            sync.Write(kind);
            if (kind == NoPart)
                return;

            sync.Write(Convert.ToInt32(partFamilyGetter.Invoke(part, [])));
            if (kind == RjwPart)
            {
                var sexPart = rjwPartSexPart.GetValue(part);
                sync.Write(sexPart == null ? null : (Hediff)sexPartAsHediffGetter.Invoke(sexPart, []));
            }
            else
            {
                sync.Write((Pawn)partOwnerGetter.Invoke(part, []));
                sync.Write((BodyPartRecord)partBodyPartGetter.Invoke(part, []));
            }
        }

        private static object ReadPart(SyncWorker sync)
        {
            var kind = sync.Read<byte>();
            if (kind == NoPart)
                return null;

            var family = Enum.ToObject(partFamilyType, sync.Read<int>());
            if (kind == RjwPart)
            {
                // the same on every PC: a part hediff gone by the time the command runs loses the order everywhere
                var hediff = sync.Read<Hediff>();
                if (!sexPartHediffType.IsInstanceOfType(hediff))
                    throw new InvalidOperationException($"{LogPrefix}sex part hediff {hediff?.ToStringSafe() ?? "null"} not found");
                return rjwPartCtor.Invoke([hediff, family]);
            }

            var owner = sync.Read<Pawn>();
            var bodyPart = sync.Read<BodyPartRecord>();
            if (owner == null || bodyPart == null)
                throw new InvalidOperationException($"{LogPrefix}body part {bodyPart?.ToStringSafe() ?? "null"} of {owner?.ToStringSafe() ?? "null"} not found");
            return vanillaPartCtor.Invoke([owner, bodyPart, family]);
        }

        // Reference equality: the resolver adds the same part object twice for parts tagged "Multiple"
        private static void WritePartRef(SyncWorker sync, object part, IList initiatorParts, IList recipientParts)
        {
            int index;
            if (part == null)
                sync.Write(NoRef);
            else if ((index = IndexOfReference(initiatorParts, part)) >= 0)
            {
                sync.Write(InitiatorRef);
                sync.Write(index);
            }
            else if ((index = IndexOfReference(recipientParts, part)) >= 0)
            {
                sync.Write(RecipientRef);
                sync.Write(index);
            }
            else
            {
                sync.Write(InlineRef);
                WritePart(sync, part);
            }
        }

        private static object ReadPartRef(SyncWorker sync, IList initiatorParts, IList recipientParts)
        {
            return sync.Read<byte>() switch
            {
                NoRef => null,
                InitiatorRef => initiatorParts[sync.Read<int>()],
                RecipientRef => recipientParts[sync.Read<int>()],
                _ => ReadPart(sync),
            };
        }

        private static int IndexOfReference(IList list, object item)
        {
            if (list == null)
                return -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item))
                    return i;
            }
            return -1;
        }

        #endregion
    }
}
