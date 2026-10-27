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
    /// <summary>Vanilla Traits Expanded by Oskar Potocki, Taranchuk, Chowder</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaTraitsExpanded"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2296404655"/>
    [MpCompatFor("VanillaExpanded.VanillaTraitsExpanded")]
    public class VanillaTraitsExpanded
    {
        private static AccessTools.FieldRef<IDictionary> mapPawnsAnimalListField;

        // TraitsManager.Instance and its per-pawn lists (FIXES row 249)
        private static FieldInfo managerInstanceField;
        private static FieldInfo[] managerPawnFields = Array.Empty<FieldInfo>();
        private static int pageDropLogged;
        private static int compactLogged;

        public VanillaTraitsExpanded(ModContentPack mod)
        {
            LongEventHandler.ExecuteWhenFinished(LatePatch);

            // Clear cache
            {
                // List of animals on current map for menagerist trait
                var field = AccessTools.DeclaredField("VanillaTraitsExpanded.ThoughtWorker_AnimalsInColony:mapPawns");
                mapPawnsAnimalListField = AccessTools.StaticFieldRefAccess<IDictionary>(field);

                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(GameComponentUtility), nameof(GameComponentUtility.FinalizeInit)),
                    postfix: new HarmonyMethod(typeof(VanillaTraitsExpanded), nameof(ClearCache)));
            }

            // FIXES row 249 (27 Oct 2026): the multifaction Settle pages make pawns on the issuer only (negative local ids).
            // A mood read on one of them (ThoughtWorker_HaventExitedColonyForLongTime) puts a Wanderlust pawn into
            // TraitsManager.wanderLustersWithLastMapExitedTick, which is saved: lab run c5short_1 desynced on it at the first
            // checkpoint after a Settle. Only Pawn.Destroy clears these lists and page pawns are never destroyed, so the
            // pawn lists drop every page pawn when the pages are done (our Multiplayer build, rows 236/237).
            {
                var managerType = AccessTools.TypeByName("VanillaTraitsExpanded.TraitsManager");
                managerInstanceField = AccessTools.DeclaredField(managerType, "Instance");
                managerPawnFields = AccessTools.GetDeclaredFields(managerType)
                    .Where(f => !f.IsStatic && f.IsPublic && (IsPawnDictionary(f.FieldType) || typeof(ICollection<Pawn>).IsAssignableFrom(f.FieldType)))
                    .ToArray();
                var restore = AccessTools.Method("Multiplayer.Client.FactionSidebar:RestoreGameStateAfterPages");
                if (restore == null)
                    Log.Warning("[MPCompat - Vanilla Traits Expanded] FactionSidebar.RestoreGameStateAfterPages not found: page pawns stay in the trait lists (FIXES row 249)");
                else
                    MpCompat.harmony.Patch(restore, prefix: new HarmonyMethod(typeof(VanillaTraitsExpanded), nameof(PreRestoreGameStateAfterPages)));

                // A removal leaves a free slot that the next insert reuses, so the issuer's later inserts would enumerate (and save)
                // in another order than the other PCs' (lab rgC6h_1: wanderLustersWithLastMapExitedTick same entries, other order).
                // The Settle command rebuilds every list in place on all PCs, so all of them continue from the same compact layout.
                var createFaction = AccessTools.Method("Multiplayer.Client.Factions.FactionCreator:CreateFaction");
                if (createFaction == null)
                    Log.Warning("[MPCompat - Vanilla Traits Expanded] FactionCreator.CreateFaction not found: trait lists are not compacted on Settle (FIXES row 249)");
                else
                    MpCompat.harmony.Patch(createFaction, prefix: new HarmonyMethod(typeof(VanillaTraitsExpanded), nameof(PreCreateFaction)));
            }
        }

        private static void LatePatch()
        {
            // Stop doing stuff with absent minded/rebel/submissive pawns outside of synced context
            {
                // Basically, this method adds/removes slow work speed hediff to/from rebel/submissive pawns.
                // On top of that, it also adds the current job to the list of forced jobs for absent minded pawns.
                // This can cause issues when the mod operates and does stuff based on those, but they are in different state for all players.
                // The issue is that a sync method catches this call, but all prefixes/postfixes/etc. still run.
                PatchingUtilities.PatchCancelInInterface(AccessTools.DeclaredMethod("VanillaTraitsExpanded.TryTakeOrderedJob_Patch:Postfix"));
            }
        }

        private static void ClearCache() => mapPawnsAnimalListField().Clear();

        private static bool IsPawnDictionary(Type type)
            => typeof(IDictionary).IsAssignableFrom(type) && type.IsGenericType && type.GetGenericArguments()[0] == typeof(Pawn);

        private static bool IsPagePawn(Pawn pawn) => pawn != null && pawn.thingIDNumber < 0;

        private static void PreRestoreGameStateAfterPages()
        {
            if (!MP.IsInMultiplayer)
                return;
            var manager = managerInstanceField?.GetValue(null);
            if (manager == null)
                return;

            int dropped = 0;
            foreach (var field in managerPawnFields)
            {
                switch (field.GetValue(manager))
                {
                    case IDictionary dict:
                        var pageKeys = dict.Keys.OfType<Pawn>().Where(IsPagePawn).ToList();
                        foreach (var pawn in pageKeys)
                            dict.Remove(pawn);
                        if (pageKeys.Count > 0)
                            Compact(dict);
                        dropped += pageKeys.Count;
                        break;
                    case ICollection<Pawn> pawns:
                        int before = dropped;
                        foreach (var pawn in pawns.Where(IsPagePawn).ToList())
                            dropped += pawns.Remove(pawn) ? 1 : 0;
                        if (dropped > before)
                            Compact(pawns);
                        break;
                }
            }
            if (pageDropLogged++ < 3)
                Log.Message($"[MPCompat - Vanilla Traits Expanded] FIXES row 249: Settle pages done, {dropped} page pawn entries dropped from {managerPawnFields.Length} trait lists");
        }

        private static void PreCreateFaction()
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return;
            var manager = managerInstanceField?.GetValue(null);
            if (manager == null)
                return;

            foreach (var field in managerPawnFields)
                Compact(field.GetValue(manager));
            if (compactLogged++ < 3)
                Log.Message($"[MPCompat - Vanilla Traits Expanded] FIXES row 249: Settle command, {managerPawnFields.Length} trait lists compacted");
        }

        // Clear and re-add in enumeration order: same entries, same order, no free slots.
        private static void Compact(object collection)
        {
            switch (collection)
            {
                case IDictionary dict:
                    // foreach over IDictionary gives DictionaryEntry; Cast<DictionaryEntry>() goes through IEnumerable and gets
                    // KeyValuePair (lab rgC6j_1: InvalidCastException in the page drop, the Settle failed)
                    var entries = new List<DictionaryEntry>();
                    foreach (DictionaryEntry entry in dict)
                        entries.Add(entry);
                    dict.Clear();
                    foreach (var entry in entries)
                        dict.Add(entry.Key, entry.Value);
                    break;
                case ICollection<Pawn> pawns:
                    var items = pawns.ToList();
                    pawns.Clear();
                    foreach (var pawn in items)
                        pawns.Add(pawn);
                    break;
            }
        }
    }
}