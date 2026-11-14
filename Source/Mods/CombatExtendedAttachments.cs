using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - the weapon attachments editor's Apply button is synced</summary>
    /// <remarks>
    /// FIXES row 58 (b) (14 Nov 2026, static finding, no lab run). <c>Window_AttachmentsEditor.Apply</c> (lab CE decompile
    /// Window_AttachmentsEditor.cs:87-112) runs on the clicking PC only: it sets <c>weapon.TargetConfig</c> (saved field
    /// <c>_targetConfig</c>), calls <c>UpdateConfiguration</c>, starts the wielder's <c>WorkGiver_ModifyWeapon</c> job with
    /// <c>StartJob(job, JobCondition.InterruptOptional)</c>, and with dev god mode adds the attachments at once. CE's own MP
    /// support (<c>[SyncMethod]</c> members, ModPatches/Multiplayer) does not cover it. Openers with a weapon:
    /// <c>ITab_AttachmentView</c> and <c>ITab_Inventory</c>. Fix: in MP the button sends ONE synced command (weapon, attachment
    /// defNames, god-mode flag of the clicking PC) and every PC runs CE's weapon branch. The loadout editor (weapon null,
    /// <c>applyAction</c> only, Dialog_ManageLoadouts) is left to CE's code. Row 58 (a), the fire-arc click, was already synced by
    /// <see cref="CombatExtendedFireArc"/> (row 136).
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedAttachments
    {
        private const string LogPrefix = "[MPCompat - CE attachments] ";

        private static Type platformType;
        private static Type attachmentDefType;
        private static FieldInfo editorWeapon;          // Window_AttachmentsEditor.weapon (WeaponPlatform)
        private static FieldInfo editorFragment;        // Window_AttachmentsEditor.editor (Fragment_AttachmentEditor)
        private static FieldInfo editorApplyAction;     // Window_AttachmentsEditor.applyAction
        private static MethodInfo curConfigGetter;      // Fragment_AttachmentEditor.CurConfig
        private static FieldInfo linkAttachment;        // AttachmentLink.attachment
        private static FieldInfo platformAttachments;   // WeaponPlatform.attachments
        private static FieldInfo defAttachmentLinks;    // WeaponPlatformDef.attachmentLinks
        private static MethodInfo platformDefGetter;    // WeaponPlatform.Platform
        private static MethodInfo targetConfigSetter;   // WeaponPlatform.TargetConfig
        private static MethodInfo updateConfiguration;  // WeaponPlatform.UpdateConfiguration
        private static MethodInfo wielderGetter;        // WeaponPlatform.Wielder
        private static MethodInfo tryGetModifyJob;      // WorkGiver_ModifyWeapon.TryGetModifyWeaponJob(Pawn, WeaponPlatform)

        public CombatExtendedAttachments(ModContentPack mod)
        {
            var window = Req(AccessTools.TypeByName("CombatExtended.Window_AttachmentsEditor"), "Window_AttachmentsEditor");
            platformType = Req(AccessTools.TypeByName("CombatExtended.WeaponPlatform"), "WeaponPlatform");
            var fragment = Req(AccessTools.TypeByName("CombatExtended.Fragment_AttachmentEditor"), "Fragment_AttachmentEditor");
            var link = Req(AccessTools.TypeByName("CombatExtended.AttachmentLink"), "AttachmentLink");
            var platformDef = Req(AccessTools.TypeByName("CombatExtended.WeaponPlatformDef"), "WeaponPlatformDef");
            var workGiver = Req(AccessTools.TypeByName("CombatExtended.WorkGiver_ModifyWeapon"), "WorkGiver_ModifyWeapon");
            attachmentDefType = Req(AccessTools.TypeByName("CombatExtended.AttachmentDef"), "AttachmentDef");

            editorWeapon = Req(AccessTools.DeclaredField(window, "weapon"), "Window_AttachmentsEditor.weapon");
            editorFragment = Req(AccessTools.DeclaredField(window, "editor"), "Window_AttachmentsEditor.editor");
            editorApplyAction = Req(AccessTools.DeclaredField(window, "applyAction"), "Window_AttachmentsEditor.applyAction");
            curConfigGetter = Req(AccessTools.DeclaredPropertyGetter(fragment, "CurConfig"), "Fragment_AttachmentEditor.CurConfig");
            linkAttachment = Req(AccessTools.DeclaredField(link, "attachment"), "AttachmentLink.attachment");
            platformAttachments = Req(AccessTools.DeclaredField(platformType, "attachments"), "WeaponPlatform.attachments");
            defAttachmentLinks = Req(AccessTools.DeclaredField(platformDef, "attachmentLinks"), "WeaponPlatformDef.attachmentLinks");
            platformDefGetter = Req(AccessTools.DeclaredPropertyGetter(platformType, "Platform"), "WeaponPlatform.Platform");
            targetConfigSetter = Req(AccessTools.DeclaredPropertySetter(platformType, "TargetConfig"), "WeaponPlatform.TargetConfig");
            updateConfiguration = Req(AccessTools.DeclaredMethod(platformType, "UpdateConfiguration", Type.EmptyTypes), "WeaponPlatform.UpdateConfiguration");
            wielderGetter = Req(AccessTools.DeclaredPropertyGetter(platformType, "Wielder"), "WeaponPlatform.Wielder");
            tryGetModifyJob = Req(AccessTools.DeclaredMethod(workGiver, "TryGetModifyWeaponJob", new[] { typeof(Pawn), platformType }),
                                  "WorkGiver_ModifyWeapon.TryGetModifyWeaponJob(Pawn, WeaponPlatform)");

            MP.RegisterSyncMethod(typeof(CombatExtendedAttachments), nameof(SyncedApply)).CancelIfAnyArgNull();
            var apply = Req(AccessTools.DeclaredMethod(window, "Apply", Type.EmptyTypes), "Window_AttachmentsEditor.Apply");
            MpCompat.harmony.Patch(apply, prefix: new HarmonyMethod(typeof(CombatExtendedAttachments), nameof(PreApply)));
        }

        private static T Req<T>(T member, string name) where T : class
            => member ?? throw new MissingMemberException(LogPrefix + "CombatExtended." + name + " not found");

        // The Apply button in MP: the weapon branch goes out as one synced command; the loadout editor (no weapon) runs CE's code.
        private static bool PreApply(Window __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;
            if (editorWeapon.GetValue(__instance) is not Thing weapon)
                return true;
            var curConfig = curConfigGetter.Invoke(editorFragment.GetValue(__instance), null);
            var defNames = new List<string>();
            foreach (var link in (IEnumerable)curConfig)
                if (linkAttachment.GetValue(link) is Def def)
                    defNames.Add(def.defName);
            SyncedApply(weapon, defNames, Prefs.DevMode && DebugSettings.godMode);
            // applyAction is null for the weapon constructor; kept local as in CE if a caller ever sets both
            (editorApplyAction.GetValue(__instance) as Delegate)?.DynamicInvoke(curConfig);
            return false;
        }

        // CE's Apply weapon branch (Window_AttachmentsEditor.cs:90-108) on every PC. The defNames resolve against the weapon's own
        // attachment links (where CE's editor takes them from), so the same link objects are used as in CE.
        // NoInlining: Multiplayer patches this method (sync prefix) - PreApply must call it, never inline its body.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SyncedApply(Thing weapon, List<string> defNames, bool instaApply)
        {
            if (weapon == null || defNames == null || !platformType.IsInstanceOfType(weapon) || weapon.Destroyed)
                return;
            var platformLinks = defAttachmentLinks.GetValue(platformDefGetter.Invoke(weapon, null)) as IList;
            var selected = new List<object>();
            var targetConfig = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(attachmentDefType));
            foreach (var name in defNames)
            {
                object found = null;
                if (platformLinks != null)
                    foreach (var link in platformLinks)
                        if (linkAttachment.GetValue(link) is Def def && def.defName == name)
                        {
                            found = link;
                            break;
                        }
                if (found == null)
                {
                    Log.Warning($"{LogPrefix}attachment {name} is not on {weapon}, skipped");
                    continue;
                }
                selected.Add(found);
                targetConfig.Add(linkAttachment.GetValue(found));
            }

            targetConfigSetter.Invoke(weapon, new object[] { targetConfig });
            updateConfiguration.Invoke(weapon, null);
            if (wielderGetter.Invoke(weapon, null) is Pawn wielder && wielder.jobs != null)
            {
                if (tryGetModifyJob.Invoke(null, new object[] { wielder, weapon }) is Job job)
                    wielder.jobs.StartJob(job, JobCondition.InterruptOptional);
            }
            // CE: "if god mode is on insta apply everything" - the clicking PC's god mode travels in the command
            if (instaApply && platformAttachments.GetValue(weapon) is IList attachments)
            {
                attachments.Clear();
                foreach (var link in selected)
                    attachments.Add(link);
                updateConfiguration.Invoke(weapon, null);
            }
        }
    }
}
