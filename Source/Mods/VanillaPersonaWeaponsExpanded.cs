using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Vanilla Persona Weapons Expanded by Oskar Potocki, Taranchuk</summary>
/// <see href="https://github.com/Vanilla-Expanded/VanillaPersonaWeaponsExpanded"/>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2826922787"/>
[MpCompatFor("VanillaExpanded.VPersonaWeaponsE")]
public class VanillaPersonaWeaponsExpanded
{
    #region Fields

    // CompGraphicCustomization
    private static Type compGraphicCustomizationType;
    // Dialog_ChoosePersonaWeapon
    private static Type personaWeaponDialogType;
    private static AccessTools.FieldRef<Window, Thing> weaponCustomizationCurrentWeaponField;
    private static AccessTools.FieldRef<Window, WeaponTraitDef> weaponCustomizationCurrentWeaponTraitField;
    private static AccessTools.FieldRef<Window, Def> weaponCustomizationCurrentPsycastField;
    private static AccessTools.FieldRef<Window, ChoiceLetter> weaponCustomizationChoiceLetterField;
    // ChoiceLetter_ChoosePersonaWeapon.<>c__DisplayClass10_0
    private static AccessTools.FieldRef<object, ChoiceLetter> innerChoiceLetterThisField;

    #endregion

    #region Main Patch

    public VanillaPersonaWeaponsExpanded(ModContentPack mod)
    {
        LongEventHandler.ExecuteWhenFinished(LatePatch);

        // VEF 1.6 moved the comp into the VEF.Graphics namespace; the old name is kept as a fallback for older VEF builds.
        compGraphicCustomizationType = AccessTools.TypeByName("VEF.Graphics.CompGraphicCustomization")
                                       ?? AccessTools.TypeByName("GraphicCustomization.CompGraphicCustomization");

        var type = AccessTools.TypeByName("VanillaPersonaWeaponsExpanded.ChoiceLetter_ChoosePersonaWeapon");
        // Reject offer and close letter
        MP.RegisterSyncMethod(type, "RemoveAndResolveLetter");
        // Send a non-customizable weapon
        var method = MpMethodUtil.GetLambda(type, "OpenChooseDialog", 0);
        MP.RegisterSyncDelegate(type, method.DeclaringType!.Name, method.Name);
        innerChoiceLetterThisField = AccessTools.FieldRefAccess<ChoiceLetter>(method.DeclaringType, "<>4__this");
    }

    private static void LatePatch()
    {
        MpCompatPatchLoader.LoadPatch<VanillaPersonaWeaponsExpanded>();

        var type = personaWeaponDialogType = AccessTools.TypeByName("VanillaPersonaWeaponsExpanded.Dialog_ChoosePersonaWeapon");
        weaponCustomizationCurrentWeaponField = AccessTools.FieldRefAccess<Thing>(type, "currentWeapon");
        weaponCustomizationCurrentWeaponTraitField = AccessTools.FieldRefAccess<WeaponTraitDef>(type, "currentWeaponTrait");
        weaponCustomizationCurrentPsycastField = AccessTools.FieldRefAccess<Def>(type, "currentPsycast");
        weaponCustomizationChoiceLetterField = AccessTools.FieldRefAccess<ChoiceLetter>(type, "choiceLetter");
        // Accept customization
        MpCompat.RegisterLambdaMethod(type, nameof(Window.DoWindowContents), 3);
    }

    #endregion

    #region Sync Workers

    [MpCompatSyncWorker("VanillaPersonaWeaponsExpanded.Dialog_ChoosePersonaWeapon")]
    private static void SyncPersonaWeaponCustomizationDialog(SyncWorker sync, ref Window dialog)
    {
        ThingComp comp = null;

        if (sync.isWriting)
        {
            sync.Write(weaponCustomizationChoiceLetterField(dialog));

            // The weapon was created on client-side, and this
            // has client-side IDs. Send the def, so we'll re-make
            // the weapon after syncing it. Let's just hope that
            // creating the weapons client-side interface won't cause issues.
            sync.Write(weaponCustomizationCurrentWeaponField(dialog).def);
            sync.Write(weaponCustomizationCurrentWeaponTraitField(dialog));
            sync.Write(weaponCustomizationCurrentPsycastField(dialog));
        }
        else
        {
            // Skip constructor since it has a bunch of initialization we don't care about.
            dialog = (Window)FormatterServices.GetUninitializedObject(personaWeaponDialogType);

            var letter = sync.Read<ChoiceLetter>();
            weaponCustomizationChoiceLetterField(dialog) = letter;

            var def = sync.Read<ThingDef>();

            // Don't bother with making the weapon if the letter was archived,
            // at this point the dialog doesn't matter at all.
            if (letter is { ArchivedOnly: false })
            {
                var weapon = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));

                weaponCustomizationCurrentWeaponField(dialog) = weapon;
                if (weapon is ThingWithComps twc)
                {
                    foreach (var c in twc.AllComps)
                    {
                        if (compGraphicCustomizationType != null && compGraphicCustomizationType.IsInstanceOfType(c))
                            comp = VanillaExpandedFramework.graphicCustomizationCompField(dialog) = c;
                    }
                }
            }

            weaponCustomizationCurrentWeaponTraitField(dialog) = sync.Read<WeaponTraitDef>();
            weaponCustomizationCurrentPsycastField(dialog) = sync.Read<Def>();
        }

        VanillaExpandedFramework.SyncGraphicCustomizationDialog(sync, ref dialog, comp);
    }

    #endregion

    #region Dialog archived letter handling

    // Close the dialog if the letter is archived.
    [MpCompatPrefix("VanillaPersonaWeaponsExpanded.Dialog_ChoosePersonaWeapon", nameof(Window.DoWindowContents))]
    private static void CloseDialogIfLetterArchived(Window __instance, ChoiceLetter ___choiceLetter)
    {
        if (MP.IsInMultiplayer && (___choiceLetter == null || ___choiceLetter.ArchivedOnly))
            __instance.Close();
    }

    // Cancel if letter is archived or there's no map to deliver to.
    [MpCompatPrefix("VanillaPersonaWeaponsExpanded.Dialog_ChoosePersonaWeapon", nameof(Window.DoWindowContents), 3)]
    private static bool PreSyncAcceptPersonaWeapon(ChoiceLetter ___choiceLetter) => CanAcceptPersonaWeapon(___choiceLetter);

    [MpCompatPrefix("VanillaPersonaWeaponsExpanded.ChoiceLetter_ChoosePersonaWeapon", "OpenChooseDialog", 0)]
    private static bool PreSyncAcceptNonCustomizablePersonaWeapon(object __instance) => CanAcceptPersonaWeapon(innerChoiceLetterThisField(__instance));

    private static bool CanAcceptPersonaWeapon(ChoiceLetter choiceLetter)
    {
        // If the letter is resolved, cancel execution - weapon was accepted or rejected.
        // FIXES row 106: this ran ArchivedOnly, which reads this PC's letter stack. MP removes another faction's letter from the
        // stack on receive, so the synced accept was cancelled on every PC but the owner's (the weapon spawned on one PC only).
        // GameComponent_PersonaWeapons.unresolvedLetters is saved and changes only in synced code and the tick:
        // RemoveAndResolveLetter, which accept and reject both call, takes the letter out.
        return !MP.IsInMultiplayer || choiceLetter != null && UnresolvedLetters()?.Contains(choiceLetter) == true;
    }

    // GameComponent_PersonaWeapons.unresolvedLetters (List<ChoiceLetter_ChoosePersonaWeapon>)
    private static IList UnresolvedLetters()
    {
        personaComponentType ??= AccessTools.TypeByName("VanillaPersonaWeaponsExpanded.GameComponent_PersonaWeapons");
        var component = Current.Game?.components?.Find(c => c.GetType() == personaComponentType);
        return component == null ? null : AccessTools.Field(personaComponentType, "unresolvedLetters").GetValue(component) as IList;
    }

    private static Type personaComponentType;
    private static readonly List<Letter> unresolvedAsLetters = new List<Letter>();

    // FIXES row 106: GameComponent_PersonaWeapons.GameComponentTick re-sends, or for a non-colonist drops, an unresolved letter
    // that is not in this PC's letter stack 7 days after tickWhenOpened (set when a player opens the letter, per PC). In MP every
    // unresolved letter counts as in the stack, so neither happens and unresolvedLetters stays the same on every PC.
    // MP already keeps the letter in the stack on postpone (DontRemoveLetterOnPostpone).
    [MpCompatTranspiler("VanillaPersonaWeaponsExpanded.GameComponent_PersonaWeapons", "GameComponentTick")]
    private static IEnumerable<CodeInstruction> TickReadsUnresolved(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        => LetterArchiveDeterminism.ReplaceStackRead(instr, baseMethod,
            AccessTools.PropertyGetter(typeof(LetterStack), nameof(LetterStack.LettersListForReading)),
            AccessTools.Method(typeof(VanillaPersonaWeaponsExpanded), nameof(LettersForTick)));

    private static List<Letter> LettersForTick(LetterStack stack)
    {
        if (!MP.IsInMultiplayer)
            return stack.LettersListForReading;

        unresolvedAsLetters.Clear();
        var unresolved = UnresolvedLetters();
        if (unresolved != null)
        {
            foreach (var letter in unresolved)
            {
                if (letter is Letter l)
                    unresolvedAsLetters.Add(l);
            }
        }
        return unresolvedAsLetters;
    }

    #endregion

    #region Letter removing patches

    // The postpone button removes the dialog for 7 days. This is a bit disruptive
    // in MP if one of the players closes the letter while another one has the
    // customization dialog open. This will cause postpone to just hide the letter.
    [MpCompatPrefix("VanillaPersonaWeaponsExpanded.ChoiceLetter_ChoosePersonaWeapon", "Choices", 0, MethodType.Getter)]
    private static bool DontRemoveLetterOnPostpone() => !MP.IsInMultiplayer;

    #endregion
}