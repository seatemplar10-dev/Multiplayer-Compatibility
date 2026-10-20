using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>WeaponSwitch (WeaponSwitch.dll, shipped identically by Coventech core and GrimWorld Framework)</summary>
/// <remarks>
/// 1.7 lab, FIXES row 145 (gap 62 of the d143 colony audit). The weapon-mode buttons of a switchable weapon (for example
/// GW_TotE_CastellanAxe, through the mod's Pawn.GetGizmos postfix) are <c>CompSwitchWeapon.SwitchWeaponOptions</c> gizmos
/// whose action is lambda 0 of the iterator, a method on the compiler display class that captures the comp
/// (<c>&lt;&gt;4__this</c>) and the clicked <c>WeaponSwitchOption</c> (<c>weaponSwithOption</c>):
/// <c>if (option.ticksToSwitchWeapon &gt; 0) { curWeaponSwitchOption = option; pawn.jobs.TryTakeOrderedJob(WS_SwitchWeapon job) } else SwitchWeapon(option);</c>.
/// Unsynced, the field and the equipment swap happen on the clicking PC only, and the (synced) job's toil then runs
/// <c>SwitchWeapon(comp.curWeaponSwitchOption)</c> with a null option on the others. The whole lambda is registered as a sync
/// delegate. An option is an object from the comp's CompProperties (not a Def, no sync worker), so the field is sent as
/// (comp, index in <c>Props.weaponsToSwitch</c>) and looked up again on the receiving side.
/// Both mods carry the same dll (same sha256) and either may be the one that gets loaded, so the module is keyed on both
/// package ids and registers once.
/// </remarks>
[MpCompatFor("shunthewitch.cvnmechanoidwarfarecore")]
[MpCompatFor("Grimworld.Framework")]
public class WeaponSwitch
{
    private static bool registered;
    private static FieldInfo delegateThisField;
    private static FieldInfo optionsField;

    public WeaponSwitch(ModContentPack mod)
    {
        // Constructed once per matching package id; both can be active in one list.
        if (registered)
            return;
        registered = true;

        var compType = AccessTools.TypeByName("WeaponSwitch.CompSwitchWeapon")
                       ?? throw new TypeLoadException("WeaponSwitch.CompSwitchWeapon not found");
        var propsType = AccessTools.TypeByName("WeaponSwitch.CompProperties_SwitchWeapon")
                        ?? throw new TypeLoadException("WeaponSwitch.CompProperties_SwitchWeapon not found");
        optionsField = AccessTools.DeclaredField(propsType, "weaponsToSwitch")
                       ?? throw new MissingFieldException(propsType.FullName, "weaponsToSwitch");

        // Lambda 0 of SwitchWeaponOptions = the Command_Action.action (<>c__DisplayClass11_0.<SwitchWeaponOptions>b__0).
        var lambda = MpMethodUtil.GetLambda(compType, "SwitchWeaponOptions", MethodType.Normal, null, 0);
        delegateThisField = AccessTools.DeclaredField(lambda.DeclaringType, "<>4__this")
                            ?? throw new MissingFieldException(lambda.DeclaringType.FullName, "<>4__this");

        MpCompat.RegisterLambdaDelegate(compType, "SwitchWeaponOptions", 0)[0]
            .TransformField("weaponSwithOption",
                Serializer.New<object, (ThingComp comp, int index)>(WriteOption, ReadOption),
                skipTypeCheck: true)
            .CancelIfAnyFieldNull();
    }

    // writer: (live field value, delegate target = the display class instance, arguments)
    private static (ThingComp comp, int index) WriteOption(object option, object displayClass, object[] _)
    {
        if (option == null || delegateThisField.GetValue(displayClass) is not ThingComp comp)
            return (null, -1);
        return (comp, ((IList)optionsField.GetValue(comp.props)).IndexOf(option));
    }

    private static object ReadOption((ThingComp comp, int index) data)
    {
        if (data.comp?.props == null || data.index < 0)
            return null;
        var options = (IList)optionsField.GetValue(data.comp.props);
        return data.index < options.Count ? options[data.index] : null;
    }
}
