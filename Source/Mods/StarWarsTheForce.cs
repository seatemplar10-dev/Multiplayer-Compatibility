using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Star Wars - The Force (Continued) by Mlie (assembly ProjectJedi, built on JecsTools AbilityUser)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3539405590"/>
    /// <remarks>
    /// FIXES row 287 (28 Oct 2026, live co-op: "the force awakened and force choke immediately desynced"; desync 11, opinion
    /// tick 620571, trace 1). Written against the decompiled 1.6 ProjectJedi.dll that the 1.7 co-op loads.
    /// "The Force Awakens" is <c>CompForceUser.LevelUp</c> (sim, every PC): it gives one ability point. The point is spent in the
    /// Force tab (<c>ITab_Pawn_Force</c> -> <c>ForceCardUtility.DrawForceCard</c>), and those buttons change the simulation
    /// directly from OnGUI on the clicking PC only: <c>powersGUIHandler</c> calls <c>compForce.LevelUpPower(power)</c> (which adds
    /// the <c>ForceAbility</c> to <c>AbilityData</c>) and subtracts the cost from <c>ForceData.AbilityPoints</c>; <c>skillsPane</c>
    /// does <c>AbilityPoints--; skill.level++</c>; the god-mode buttons call <c>LevelUp</c>, <c>ResetPowers</c> and the
    /// <c>AlignmentValue</c> setter. Nothing synced them, so Sea learned Force Choke on the host only. Casting is synced
    /// (JecsTools.cs: <c>PawnAbility.UseAbility</c>, ability written as comp index + index in <c>AbilityData.AllPowers</c>), so the
    /// first cast ran the cast job on the host and found no such ability on the other PC: the very first trace differed.
    /// Fix: in MP, while the Force card is drawn in the interface, <c>LevelUpPower</c>, <c>LevelUp</c>, <c>ResetPowers</c> and
    /// <c>AlignmentValue</c> are sent as synced commands instead of running locally, and the card's direct writes to
    /// <c>AbilityPoints</c> and <c>ForceSkill.level</c> are reverted after the pane draws and sent as synced commands. Each
    /// command re-checks the button's own conditions on every PC. Outside a session nothing changes.
    /// Not fixed here: the cast job's <c>verbToUse</c> (a <c>Verb_UseAbility</c> with no loadID, never deep-saved) does not
    /// survive a save, so a join point taken mid-cast loads the job with a null verb (JobDriver_CastAbilityVerb throws).
    /// </remarks>
    [MpCompatFor("mlie.starwarstheforce")]
    public class StarWarsTheForce
    {
        private const string LogPrefix = "[MPCompat - Star Wars The Force] ";
        private const int MaxSkillLevel = 5; // skillsPane: skill.level < 5
        private const int MaxPowerLevel = 3; // powersGUIHandler: power.level >= 3 is not clickable

        // >0 while ForceCardUtility.DrawForceCard runs (interface only).
        private static int cardDepth;

        private static Type compForceUserType, forceAbilityDefType;

        // CompForceUser
        private static FastInvokeHandler forceDataGetter, levelUpPowerMethod, levelUpMethod, resetPowersMethod, alignmentSetter;

        // ForceData: Skills, PowersDark, PowersGray, PowersLight (lazy getters, same lists on every PC)
        private static FastInvokeHandler skillsGetter;
        private static FastInvokeHandler[] treeGetters;
        private static AccessTools.FieldRef<object, int> abilityPointsField;

        // ForceSkill / ForcePower / ForceAbilityDef
        private static AccessTools.FieldRef<object, int> skillLevelField, powerLevelField, defAbilityPointsField;
        private static FastInvokeHandler nextLevelAbilityDefGetter;

        public StarWarsTheForce(ModContentPack mod)
        {
            var cardType = NeedType("ProjectJedi.ForceCardUtility");
            compForceUserType = NeedType("ProjectJedi.CompForceUser");
            var forceDataType = NeedType("ProjectJedi.ForceData");
            var forcePowerType = NeedType("ProjectJedi.ForcePower");
            var forceSkillType = NeedType("ProjectJedi.ForceSkill");
            forceAbilityDefType = NeedType("ProjectJedi.ForceAbilityDef");

            forceDataGetter = MethodInvoker.GetHandler(Need(AccessTools.DeclaredPropertyGetter(compForceUserType, "ForceData"), "CompForceUser.ForceData"));
            var levelUpPower = Need(AccessTools.DeclaredMethod(compForceUserType, "LevelUpPower", [forcePowerType]), "CompForceUser.LevelUpPower");
            var levelUp = Need(AccessTools.DeclaredMethod(compForceUserType, "LevelUp", [typeof(bool)]), "CompForceUser.LevelUp");
            var resetPowers = Need(AccessTools.DeclaredMethod(compForceUserType, "ResetPowers", Type.EmptyTypes), "CompForceUser.ResetPowers");
            var setAlignment = Need(AccessTools.DeclaredPropertySetter(compForceUserType, "AlignmentValue"), "CompForceUser.AlignmentValue setter");
            levelUpPowerMethod = MethodInvoker.GetHandler(levelUpPower);
            levelUpMethod = MethodInvoker.GetHandler(levelUp);
            resetPowersMethod = MethodInvoker.GetHandler(resetPowers);
            alignmentSetter = MethodInvoker.GetHandler(setAlignment);

            skillsGetter = MethodInvoker.GetHandler(Need(AccessTools.DeclaredPropertyGetter(forceDataType, "Skills"), "ForceData.Skills"));
            treeGetters = new[] { "PowersDark", "PowersGray", "PowersLight" }
                .Select(n => MethodInvoker.GetHandler(Need(AccessTools.DeclaredPropertyGetter(forceDataType, n), "ForceData." + n)))
                .ToArray();
            abilityPointsField = AccessTools.FieldRefAccess<int>(forceDataType, "abilityPoints");
            skillLevelField = AccessTools.FieldRefAccess<int>(forceSkillType, "level");
            powerLevelField = AccessTools.FieldRefAccess<int>(forcePowerType, "level");
            defAbilityPointsField = AccessTools.FieldRefAccess<int>(forceAbilityDefType, "abilityPoints");
            nextLevelAbilityDefGetter = MethodInvoker.GetHandler(Need(AccessTools.DeclaredPropertyGetter(forcePowerType, "NextLevelAbilityDef"), "ForcePower.NextLevelAbilityDef"));

            var drawCard = Need(AccessTools.DeclaredMethod(cardType, "DrawForceCard"), "ForceCardUtility.DrawForceCard");
            var skillsPane = Need(AccessTools.DeclaredMethod(cardType, "skillsPane"), "ForceCardUtility.skillsPane");
            var powersPane = Need(AccessTools.DeclaredMethod(cardType, "powersGUIHandler"), "ForceCardUtility.powersGUIHandler");

            var self = typeof(StarWarsTheForce);
            MpCompat.harmony.Patch(drawCard,
                prefix: new HarmonyMethod(self, nameof(CardEnter)),
                finalizer: new HarmonyMethod(self, nameof(CardExit)));
            MpCompat.harmony.Patch(skillsPane,
                prefix: new HarmonyMethod(self, nameof(PreSkillsPane)),
                postfix: new HarmonyMethod(self, nameof(PostSkillsPane)));
            MpCompat.harmony.Patch(powersPane,
                prefix: new HarmonyMethod(self, nameof(PrePowersPane)),
                postfix: new HarmonyMethod(self, nameof(PostPowersPane)));
            MpCompat.harmony.Patch(levelUpPower, prefix: new HarmonyMethod(self, nameof(PreLevelUpPower)));
            MpCompat.harmony.Patch(levelUp, prefix: new HarmonyMethod(self, nameof(PreLevelUp)));
            MpCompat.harmony.Patch(resetPowers, prefix: new HarmonyMethod(self, nameof(PreResetPowers)));
            MpCompat.harmony.Patch(setAlignment, prefix: new HarmonyMethod(self, nameof(PreSetAlignment)));

            MP.RegisterSyncMethod(self, nameof(SyncedLearnPower)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(self, nameof(SyncedSkillUp)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(self, nameof(SyncedLevelUp)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(self, nameof(SyncedResetPowers)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(self, nameof(SyncedSetAlignment)).CancelIfAnyArgNull();
        }

        #region Interface (clicking PC only)

        private static bool Redirect => cardDepth > 0 && MP.IsInMultiplayer && MP.InInterface;

        private static void CardEnter() => cardDepth++;

        private static void CardExit() => cardDepth--;

        // skillsPane writes AbilityPoints-- and skill.level++ inline. Remember them, put them back after the pane, send the clicks.
        private static void PreSkillsPane(object compForce, out int[] __state)
        {
            __state = null;
            if (!Redirect || compForce == null)
                return;
            var data = forceDataGetter(compForce);
            if (data == null)
                return;
            var skills = (IList)skillsGetter(data);
            __state = new int[skills.Count + 1];
            __state[0] = abilityPointsField(data);
            for (var i = 0; i < skills.Count; i++)
                __state[i + 1] = skillLevelField(skills[i]);
        }

        private static void PostSkillsPane(object compForce, int[] __state)
        {
            if (__state == null)
                return;
            var data = forceDataGetter(compForce);
            var skills = (IList)skillsGetter(data);
            abilityPointsField(data) = __state[0];
            for (var i = 0; i < skills.Count && i + 1 < __state.Length; i++)
            {
                var now = skillLevelField(skills[i]);
                var before = __state[i + 1];
                if (now == before)
                    continue;
                skillLevelField(skills[i]) = before;
                for (var step = before; step < now; step++)
                    SyncedSkillUp(((ThingComp)compForce).parent as Pawn, i);
            }
        }

        // powersGUIHandler subtracts the cost from AbilityPoints inline after LevelUpPower (redirected below). Put it back.
        private static void PrePowersPane(object compForce, out int __state)
        {
            __state = int.MinValue;
            if (!Redirect || compForce == null)
                return;
            var data = forceDataGetter(compForce);
            if (data != null)
                __state = abilityPointsField(data);
        }

        private static void PostPowersPane(object compForce, int __state)
        {
            if (__state == int.MinValue)
                return;
            var data = forceDataGetter(compForce);
            if (data != null)
                abilityPointsField(data) = __state;
        }

        private static bool PreLevelUpPower(object __instance, object power)
        {
            if (!Redirect)
                return true;
            var data = forceDataGetter(__instance);
            for (var tree = 0; data != null && tree < treeGetters.Length; tree++)
            {
                var index = ((IList)treeGetters[tree](data)).IndexOf(power);
                if (index >= 0)
                {
                    SyncedLearnPower(((ThingComp)__instance).parent as Pawn, tree, index);
                    return false;
                }
            }
            Log.Warning($"{LogPrefix}LevelUpPower from the Force card: power not found in the pawn's trees, not sent.");
            return false;
        }

        // God-mode buttons on the Force card.
        private static bool PreLevelUp(object __instance, bool hideNotification)
        {
            if (!Redirect)
                return true;
            SyncedLevelUp(((ThingComp)__instance).parent as Pawn, hideNotification);
            return false;
        }

        private static bool PreResetPowers(object __instance)
        {
            if (!Redirect)
                return true;
            SyncedResetPowers(((ThingComp)__instance).parent as Pawn);
            return false;
        }

        private static bool PreSetAlignment(object __instance, float value)
        {
            if (!Redirect)
                return true;
            SyncedSetAlignment(((ThingComp)__instance).parent as Pawn, value);
            return false;
        }

        #endregion

        #region Synced commands (every PC)

        // powersGUIHandler click: same checks as the button, then LevelUpPower and the cost (NextLevelAbilityDef read before).
        private static void SyncedLearnPower(Pawn pawn, int tree, int index)
        {
            if (!TryGetData(pawn, out var comp, out var data) || tree < 0 || tree >= treeGetters.Length)
                return;
            var powers = (IList)treeGetters[tree](data);
            if (index < 0 || index >= powers.Count)
                return;
            var power = powers[index];
            var points = abilityPointsField(data);
            if (points == 0 || powerLevelField(power) >= MaxPowerLevel)
                return;
            var def = nextLevelAbilityDefGetter(power);
            var hasForceDef = def != null && forceAbilityDefType.IsInstanceOfType(def);
            var cost = hasForceDef ? defAbilityPointsField(def) : 0;
            if (hasForceDef && points < cost)
                return;
            levelUpPowerMethod(comp, power);
            if (hasForceDef)
                abilityPointsField(data) -= cost;
        }

        // skillsPane click: AbilityPoints-- and skill.level++.
        private static void SyncedSkillUp(Pawn pawn, int index)
        {
            if (!TryGetData(pawn, out _, out var data))
                return;
            var skills = (IList)skillsGetter(data);
            if (index < 0 || index >= skills.Count)
                return;
            if (abilityPointsField(data) <= 0 || skillLevelField(skills[index]) >= MaxSkillLevel)
                return;
            abilityPointsField(data)--;
            skillLevelField(skills[index])++;
        }

        private static void SyncedLevelUp(Pawn pawn, bool hideNotification)
        {
            var comp = FindComp(pawn);
            if (comp != null)
                levelUpMethod(comp, hideNotification);
        }

        private static void SyncedResetPowers(Pawn pawn)
        {
            var comp = FindComp(pawn);
            if (comp != null)
                resetPowersMethod(comp);
        }

        private static void SyncedSetAlignment(Pawn pawn, float value)
        {
            var comp = FindComp(pawn);
            if (comp != null)
                alignmentSetter(comp, value);
        }

        #endregion

        #region Helpers

        // Same lookup as the card's pawn.GetComp<CompForceUser>(). The comp is added at runtime with null props
        // (AbilityUserUtility.TransformPawn), so Multiplayer's ThingComp sync worker (props.compClass) cannot write it:
        // the commands take the pawn instead.
        private static ThingComp FindComp(Pawn pawn)
            => pawn?.AllComps?.FirstOrDefault(c => compForceUserType.IsInstanceOfType(c));

        private static bool TryGetData(Pawn pawn, out ThingComp comp, out object data)
        {
            comp = FindComp(pawn);
            data = comp == null ? null : forceDataGetter(comp);
            // The card's buttons only work for the player's own pawns (Faction == Faction.OfPlayer); in a synced command
            // Faction.OfPlayer is the issuing player's faction.
            return data != null && pawn.Faction == Faction.OfPlayer;
        }

        private static Type NeedType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo Need(MethodInfo method, string what)
            => method ?? throw new MissingMethodException($"{LogPrefix}{what} not found");

        #endregion
    }
}
