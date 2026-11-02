using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES row 106: the letter archive cull and the ransom incident read the letter stack, which is per PC.</summary>
    /// <remarks>
    /// The letter stack differs per PC: MP removes another faction's letters from it on receive (LetterStackReceiveOnlyMyFaction,
    /// MultifactionPatches.cs), and each player closes letters locally. Two places in the simulation read it (decomp 1.7):
    /// 1. Archive cull. Letter.CanCullArchivedNow is "not in the letter stack" (Verse/Letter.cs:51). Archive.CheckCullArchivables
    ///    removes the oldest cullable entries past 200 (RimWorld/Archive.cs:81). MP's Letter sync worker finds a letter by id in
    ///    the archive (SyncDictRimWorld.cs, "Letters"). So on the PC that does not hold a choice letter, the letter can be culled,
    ///    and the owner's later choice (accept joiner, ransom, growth moment, baby name...) finds no letter there: the choice runs
    ///    on one PC only. The archive count also differs per PC (historical messages dedupe by real time, row 108; pins are local),
    ///    so no rule that counts can make the cull itself the same on every PC.
    ///    Fix: in MP a letter is cullable only when no synced choice can still reach it, by a rule that reads game state only:
    ///    a letter with a timeout stays until a day after its timeout; a plain letter (StandardLetter, DeathLetter,
    ///    NewQuestLetter, exact types: their buttons are interface only) can go; any other letter type stays.
    ///    The other entries (messages, plain letters) can still differ per PC. No sync reads them: the History tab only.
    /// 2. IncidentWorker_RansomDemand.RandomKidnappedColonist (RimWorld/IncidentWorker_RansomDemand.cs:64) leaves out pawns with
    ///    a ransom letter in the stack. Another faction's ransom letter is not in this PC's stack, so the pick could differ.
    ///    Fix: in MP it reads the archive's ransom letters whose timeout has not passed. Rule 1 keeps those on every PC.
    /// 3. Mods (their modules): VAspirE grow old (VanillaAspirationsExpanded.cs) and Vanilla Persona Weapons Expanded
    ///    (VanillaPersonaWeaponsExpanded.cs) read the stack in a tick or a synced call.
    /// Single player is unchanged (every patch returns at once when not in MP).
    /// </remarks>
    internal static class LetterArchiveDeterminism
    {
        // A day after a letter's timeout: a choice sent in the last tick before the timeout has landed by then.
        private const int MarginTicks = GenDate.TicksPerDay;

        // Letter types whose buttons are interface only (jump, close, view quest). Exact types: a subclass may add a synced choice.
        private static readonly HashSet<Type> PlainTypes = new HashSet<Type>
        {
            typeof(StandardLetter), typeof(DeathLetter), typeof(NewQuestLetter),
        };

        private static readonly List<Letter> openRansomLetters = new List<Letter>();

        internal static void Install(Harmony harmony)
        {
            var map = typeof(Letter).GetInterfaceMap(typeof(IArchivable));
            MethodInfo canCull = null;
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == "get_" + nameof(IArchivable.CanCullArchivedNow))
                    canCull = map.TargetMethods[i];
            }
            if (canCull == null)
                Log.Warning("[MPCompat] row 106: Letter.CanCullArchivedNow not found, the letter archive cull stays per PC");
            else
                harmony.Patch(canCull, postfix: new HarmonyMethod(typeof(LetterArchiveDeterminism), nameof(CanCullPostfix)));

            var ransom = AccessTools.Method(typeof(IncidentWorker_RansomDemand), "RandomKidnappedColonist");
            if (ransom == null)
                Log.Warning("[MPCompat] row 106: IncidentWorker_RansomDemand.RandomKidnappedColonist not found, ransom picks stay per PC");
            else
                harmony.Patch(ransom, transpiler: new HarmonyMethod(typeof(LetterArchiveDeterminism), nameof(RansomTranspiler)));
        }

        private static void CanCullPostfix(Letter __instance, ref bool __result)
        {
            if (MP.IsInMultiplayer)
                __result = !KeepInArchive(__instance);
        }

        internal static bool KeepInArchive(Letter letter)
        {
            if (letter is LetterWithTimeout timed && timed.TimeoutActive)
                return Find.TickManager.TicksGame < timed.disappearAtTick + MarginTicks;
            return !PlainTypes.Contains(letter.GetType());
        }

        private static IEnumerable<CodeInstruction> RansomTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            => ReplaceStackRead(instructions, original, AccessTools.PropertyGetter(typeof(LetterStack), nameof(LetterStack.LettersListForReading)),
                AccessTools.Method(typeof(LetterArchiveDeterminism), nameof(LettersForRansom)));

        /// <summary>Replaces the one read of the letter stack (LettersListForReading or the letters field) with replacement(LetterStack).</summary>
        internal static IEnumerable<CodeInstruction> ReplaceStackRead(IEnumerable<CodeInstruction> instructions, MethodBase original,
            MemberInfo stackRead, MethodInfo replacement)
        {
            var list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            foreach (var ci in list)
            {
                bool hit = stackRead is FieldInfo
                    ? ci.opcode == OpCodes.Ldfld && Equals(ci.operand, stackRead)
                    : (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && Equals(ci.operand, stackRead);
                if (hit)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    replaced++;
                }
            }
            if (replaced != 1)
                Log.Warning($"[MPCompat] row 106: {original?.DeclaringType?.Name}.{original?.Name} reads the letter stack {replaced} times, expected 1; it may still decide per PC");
            return list;
        }

        // Replaces LetterStack.letters where a mod looks for a letter it sent in the simulation (VAspirE grow old).
        // ReceiveLetter archives the letter on every PC in the same tick, before MP removes another faction's letter from the stack.
        private static readonly List<Letter> archivedLetters = new List<Letter>();

        internal static List<Letter> ArchivedLetters(LetterStack stack)
        {
            if (!MP.IsInMultiplayer)
                return stack.letters;

            archivedLetters.Clear();
            foreach (var archivable in Find.Archive.ArchivablesListForReading)
            {
                if (archivable is Letter letter)
                    archivedLetters.Add(letter);
            }
            return archivedLetters;
        }

        // Replaces LetterStack.LettersListForReading in RandomKidnappedColonist. It only looks for ChoiceLetter_RansomDemand there.
        private static List<Letter> LettersForRansom(LetterStack stack)
        {
            if (!MP.IsInMultiplayer)
                return stack.LettersListForReading;

            openRansomLetters.Clear();
            foreach (var archivable in Find.Archive.ArchivablesListForReading)
            {
                if (archivable is ChoiceLetter_RansomDemand letter && !letter.TimeoutPassed)
                    openRansomLetters.Add(letter);
            }
            return openRansomLetters;
        }
    }
}
