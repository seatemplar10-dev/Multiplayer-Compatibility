using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>ABF: Synstructs Core by Killathon (killathon.artificialbeings.syncore): the "paint synstruct" repaint window</summary>
    /// <remarks>
    /// FIXES row 309 leftover (extra windows, set A, finding 1). The surgery recipe <c>ABF_Recipe_Synstruct_Paint</c> (worker
    /// <c>ArtificialBeings.Recipe_PaintUnit</c>) has one job: <c>ApplyOnPawn</c> opens <c>Dialog_Repaint</c>. A surgery bill finishes in a job
    /// toil, so <c>ApplyOnPawn</c> runs in the simulation on EVERY PC and the window opened on every PC. Its controls then write SAVED pawn
    /// state live from window code, on the PC of whoever clicks:
    /// <list type="bullet">
    /// <item>the swatch grid (DrawSkinColors, every frame the picked colour differs from the pawn's "skin" channel), the "favourite colour"
    /// button (DrawBottomButtons) and the Reset button (Reset) each call <c>AlienComp.OverwriteColorChannel("skin", colour, null)</c> (the
    /// comp's <c>colorChannels</c> dictionary is Scribed) and set <c>Pawn_StoryTracker.SkinColorBase</c> (a runtime field of the 1.7 story
    /// tracker, not in its ExposeData; Humanoid Alien Races sets it from the saved "skin" channel on load), then redraw;</item>
    /// <item>OverwriteColorChannel also calls RegenerateColorChannelLink, which draws colours for any channel linked to "skin" with Rand,
    /// so an unsynced write also leaves different linked channels on each PC;</item>
    /// <item>a situational mood thought (<c>ThoughtWorker_PaintedColor_Favorite</c>) compares SkinColorBase with the favourite colour in the
    /// simulation, so the two PCs disagree on the mood.</item>
    /// </list>
    /// Accept only closes the window. The window has no Cancel and no PreClose / PostClose override: every live write stays when it closes,
    /// so there is nothing to revert and nothing "previewed" that is left unsynced.
    /// This module (the mod is reached by name only, no reference to its dll):
    /// A. Window gate. A prefix on <c>Recipe_PaintUnit.ApplyOnPawn</c>, in multiplayer only, lets the original (which only adds the window)
    ///    run on the one PC whose own player faction (<c>MP.RealPlayerFaction</c>) owns the pawn. The owner is the pawn's faction, or the
    ///    surgeon's faction when the pawn belongs to no player faction (a prisoner). Every other PC skips the window but still reads
    ///    <c>AlienComp.ColorChannels</c>, as the window's constructor does, so the lazy channel set-up (a Rand draw) happens on every PC as
    ///    before. This gate is PC-local on purpose: it only decides where a window opens.
    /// B. Edits. A transpiler on the three methods that contain the write sites (DrawSkinColors, DrawBottomButtons, Reset) points each
    ///    <c>AlienComp.OverwriteColorChannel</c> call at RouteOverwrite and each <c>SkinColorBase</c> setter call at RouteSkinBase. Nothing
    ///    else is hooked, so the simulation, other windows, genes, pawn generation and other mods keep the original writers. At interface
    ///    time in a multiplayer game RouteOverwrite sends ONE synced method, SyncPaint(Pawn, Color), and RouteSkinBase writes nothing. The
    ///    swatch grid calls the writer again every frame until the command has run, so a send of the same pawn and colour is not repeated
    ///    for two seconds. SyncPaint, on every PC: overwrites the "skin" channel (same reflection call), sets SkinColorBase, marks the pawn's
    ///    graphics and portrait dirty. The live preview stays; it shows after the command runs, not in the clicking frame. In single player
    ///    (or while this module is not active) both routes call the original writers.
    /// SyncPaint checks on every PC that the pawn is not null, destroyed, discarded or dead, that it has a story tracker and that it still
    /// has the comp, before it writes anything. It reads no Faction.OfPlayer and no Find.CurrentMap.
    /// Left local on purpose: the window itself, its colour grid and scroll, the portrait draw (DrawPawn), the target colour and the Colors
    /// list, the click sound, the two redraw calls the window makes itself.
    /// Supported build: ABF Synstructs Core 1.6 folder, Synstructs Core.dll (138,240 bytes), with Humanoid Alien Races 1.6 (AlienRace.dll).
    /// Every member is looked up and the IL of the three patched methods is counted (one OverwriteColorChannel call and one SkinColorBase
    /// setter call each) before anything is patched. On a mismatch nothing is patched and the window behaves as without the module.
    /// <c>active</c> is true only when every lookup, registration and patch worked.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("killathon.artificialbeings.syncore")]
    internal class AbfSynstructsRepaint
    {
        private const string LogPrefix = "[MPCompat - ABF Synstructs Repaint] ";
        private const string SkinChannel = "skin";

        // The swatch grid calls the writer every frame until the synced command has run: do not send the same pawn and colour again before this.
        private const float ResendSeconds = 2f;

        // mod types and members, found by name
        private static Type alienCompType;
        private static MethodInfo overwriteMethod, colorChannelsGetter;

        // True only when everything was found, registered and patched. While false every hook below passes straight through.
        private static bool active;

        private static int failures, syncMethods, patches;

        // Interface-time resend guard (main thread, clicker's PC only)
        private static int lastSentPawnId = -1;
        private static Color lastSentColor;
        private static float lastSentAt = float.NegativeInfinity;

        private static bool warnedTouch;

        public AbfSynstructsRepaint(ModContentPack mod)
        {
            MethodBase applyOnPawn = null;
            MethodBase[] writers = null;
            Try("look up the Synstructs Core and Humanoid Alien Races members", () =>
            {
                var recipe = TypeNamed("ArtificialBeings.Recipe_PaintUnit");
                var dialog = TypeNamed("ArtificialBeings.Dialog_Repaint");
                alienCompType = TypeNamed("AlienRace.AlienPartGenerator+AlienComp");

                if (!typeof(RecipeWorker).IsAssignableFrom(recipe))
                    throw new InvalidOperationException("Recipe_PaintUnit is not a RecipeWorker");
                if (!typeof(Window).IsAssignableFrom(dialog))
                    throw new InvalidOperationException("Dialog_Repaint is not a Window");
                if (!typeof(ThingComp).IsAssignableFrom(alienCompType))
                    throw new InvalidOperationException("AlienComp is not a ThingComp");

                applyOnPawn = Required(AccessTools.DeclaredMethod(recipe, "ApplyOnPawn",
                    new[] { typeof(Pawn), typeof(BodyPartRecord), typeof(Pawn), typeof(List<Thing>), typeof(Bill) }),
                    "Recipe_PaintUnit.ApplyOnPawn(Pawn, BodyPartRecord, Pawn, List<Thing>, Bill)");
                var names = applyOnPawn.GetParameters().Select(p => p.Name).ToArray();
                if (applyOnPawn.IsStatic || names[0] != "pawn" || names[2] != "billDoer")
                    throw new MissingMethodException("Recipe_PaintUnit.ApplyOnPawn must be an instance method with parameters named pawn and billDoer");

                var drawSkinColors = Required(AccessTools.DeclaredMethod(dialog, "DrawSkinColors", new[] { typeof(Rect) }), "Dialog_Repaint.DrawSkinColors(Rect)");
                var drawBottomButtons = Required(AccessTools.DeclaredMethod(dialog, "DrawBottomButtons", new[] { typeof(Rect) }), "Dialog_Repaint.DrawBottomButtons(Rect)");
                var reset = Required(AccessTools.DeclaredMethod(dialog, "Reset", Type.EmptyTypes), "Dialog_Repaint.Reset()");
                writers = new MethodBase[] { drawSkinColors, drawBottomButtons, reset };

                overwriteMethod = Required(AccessTools.DeclaredMethod(alienCompType, "OverwriteColorChannel",
                    new[] { typeof(string), typeof(Color?), typeof(Color?) }), "AlienComp.OverwriteColorChannel(string, Color?, Color?)");
                if (overwriteMethod.IsStatic)
                    throw new MissingMethodException("AlienComp.OverwriteColorChannel must be an instance method");
                colorChannelsGetter = Required(AccessTools.PropertyGetter(alienCompType, "ColorChannels"), "AlienComp.ColorChannels getter");
                if (colorChannelsGetter.IsStatic)
                    throw new MissingMethodException("AlienComp.ColorChannels must be an instance property");
                Required(AccessTools.PropertySetter(typeof(Pawn_StoryTracker), "SkinColorBase"), "Pawn_StoryTracker.SkinColorBase setter");

                // The window's writes are exactly one OverwriteColorChannel call and one SkinColorBase setter call per method: patch nothing unless that is still true
                foreach (var writer in writers)
                {
                    var instructions = PatchProcessor.GetOriginalInstructions(writer);
                    var overwrites = instructions.Count(ci => IsCallTo(ci, alienCompType, "OverwriteColorChannel"));
                    var setters = instructions.Count(ci => IsCallTo(ci, typeof(Pawn_StoryTracker), "set_SkinColorBase"));
                    if (overwrites != 1 || setters != 1)
                        throw new InvalidOperationException($"Dialog_Repaint.{writer.Name} has {overwrites} OverwriteColorChannel call(s) and {setters} SkinColorBase setter call(s), expected 1 and 1");
                }
            });

            if (failures > 0)
            {
                Log.Error($"{LogPrefix}this Synstructs Core build does not have what the module syncs ({failures} problem(s) above): nothing patched, the repaint window behaves as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported Synstructs Core build");
            }

            Try("register the synced method", () =>
            {
                MP.RegisterSyncMethod(typeof(AbfSynstructsRepaint), nameof(SyncPaint)).CancelIfAnyArgNull();
                syncMethods++;
            });

            if (failures == 0)
            {
                Try("patch Recipe_PaintUnit.ApplyOnPawn", () =>
                {
                    MpCompat.harmony.Patch(applyOnPawn, prefix: new HarmonyMethod(typeof(AbfSynstructsRepaint), nameof(PreApplyOnPawn)));
                    patches++;
                });
                foreach (var writer in writers)
                {
                    var target = writer;
                    Try($"patch Dialog_Repaint.{target.Name}", () =>
                    {
                        MpCompat.harmony.Patch(target, transpiler: new HarmonyMethod(typeof(AbfSynstructsRepaint), nameof(TranspileRoutes)));
                        patches++;
                    });
                }
            }

            if (failures == 0)
                active = true;
            else
                Log.Error($"{LogPrefix}module NOT active because a step failed: the repaint window's edits stay local and it opens on every PC, as without the module.");

            Log.Message($"{LogPrefix}{syncMethods} sync method(s) registered, {patches} patch(es) installed, {failures} failure(s), active={active}.");
            if (failures > 0)
                throw new InvalidOperationException($"{LogPrefix}{failures} registration step(s) failed, see the errors above.");
        }

        #region Lookup helpers

        private static void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failures++;
                Log.Error($"{LogPrefix}could not {what}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static Type TypeNamed(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static MethodInfo Required(MethodInfo method, string what)
            => method ?? throw new MissingMethodException($"{what} not found");

        private static bool IsCallTo(CodeInstruction ci, Type declaringType, string methodName)
            => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
               && ci.operand is MethodInfo method && method.DeclaringType == declaringType && method.Name == methodName;

        private static ThingComp FindAlienComp(Pawn pawn)
            => pawn?.AllComps?.FirstOrDefault(c => alienCompType.IsInstanceOfType(c));

        private static void LogCommand(string what, Pawn pawn, string detail)
        {
            if (Prefs.DevMode)
                Log.Message($"{LogPrefix}{what} pawn={pawn?.ThingID} {detail} tick={Find.TickManager?.TicksGame} bySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
        }

        #endregion

        #region Window gate (A)

        // Simulation time, every PC. The original only adds the window. Which PC shows it is decided by this PC's own faction, on purpose.
        private static bool PreApplyOnPawn(Pawn pawn, Pawn billDoer)
        {
            if (!active || !MP.IsInMultiplayer || pawn == null)
                return true;

            if (OwnsPawn(pawn, billDoer))
                return true;

            LogCommand("repaint window not opened on this PC", pawn, $"faction={pawn.Faction?.Name} mine={MP.RealPlayerFaction?.Name}");
            // The window's constructor reads AlienComp.ColorChannels, whose getter sets the channels up with Rand when they are empty.
            // Do the same read here so every PC makes the same Rand draws and saved writes as before.
            TouchColorChannels(pawn);
            return false;
        }

        // The PC whose real player faction is the pawn's faction. A pawn in no player faction (a prisoner) goes to the surgeon's faction.
        private static bool OwnsPawn(Pawn pawn, Pawn billDoer)
        {
            var mine = MP.RealPlayerFaction;
            if (mine == null)
                return false;

            var owner = pawn.Faction;
            if (owner == null || !owner.IsPlayer)
                owner = billDoer?.Faction;
            return owner != null && owner == mine;
        }

        private static void TouchColorChannels(Pawn pawn)
        {
            try
            {
                var comp = FindAlienComp(pawn);
                if (comp != null)
                    colorChannelsGetter.Invoke(comp, null);
            }
            catch (Exception e)
            {
                if (!warnedTouch)
                {
                    warnedTouch = true;
                    Log.Warning($"{LogPrefix}reading the colour channels of {pawn.ThingID} failed (the window's constructor would have thrown the same): {e.GetType().Name}: {e.Message}");
                }
            }
        }

        #endregion

        #region Edits (B)

        // Dialog_Repaint.DrawSkinColors / DrawBottomButtons / Reset: replace the two writer calls with the routes below. Nothing else changes.
        private static IEnumerable<CodeInstruction> TranspileRoutes(IEnumerable<CodeInstruction> instructions)
        {
            var routeOverwrite = AccessTools.DeclaredMethod(typeof(AbfSynstructsRepaint), nameof(RouteOverwrite));
            var routeSkinBase = AccessTools.DeclaredMethod(typeof(AbfSynstructsRepaint), nameof(RouteSkinBase));
            var overwrites = 0;
            var setters = 0;
            foreach (var ci in instructions)
            {
                if (IsCallTo(ci, alienCompType, "OverwriteColorChannel"))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = routeOverwrite;
                    overwrites++;
                }
                else if (IsCallTo(ci, typeof(Pawn_StoryTracker), "set_SkinColorBase"))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = routeSkinBase;
                    setters++;
                }

                yield return ci;
            }

            if (overwrites != 1 || setters != 1)
                throw new InvalidOperationException($"{LogPrefix}repaint transpiler replaced {overwrites} OverwriteColorChannel and {setters} SkinColorBase setter calls, expected 1 and 1");
        }

        // Replaces comp.OverwriteColorChannel(channel, first, second) in the three dialog methods (same stack shape, comp is an AlienComp).
        // Window code only runs at interface time. In multiplayer it never writes here: SyncPaint does, on every PC.
        private static void RouteOverwrite(ThingComp comp, string channel, Color? first, Color? second)
        {
            if (active && MP.IsInMultiplayer)
            {
                if (MP.InInterface && channel == SkinChannel && first.HasValue && !second.HasValue && comp?.parent is Pawn pawn)
                    SendPaint(pawn, first.Value);
                // Anything else (not interface time, another shape of call) writes nothing: an unsynced write would split the PCs.
                return;
            }

            overwriteMethod.Invoke(comp, new object[] { channel, first, second });
        }

        // Replaces pawn.story.SkinColorBase = colour in the three dialog methods. SyncPaint sets it, together with the "skin" channel.
        private static void RouteSkinBase(Pawn_StoryTracker story, Color value)
        {
            if (active && MP.IsInMultiplayer)
                return;

            story.SkinColorBase = value;
        }

        private static void SendPaint(Pawn pawn, Color color)
        {
            var now = Time.realtimeSinceStartup;
            if (pawn.thingIDNumber == lastSentPawnId && color.Equals(lastSentColor) && now - lastSentAt < ResendSeconds)
                return;

            lastSentPawnId = pawn.thingIDNumber;
            lastSentColor = color;
            lastSentAt = now;
            SyncPaint(pawn, color);
        }

        // Runs on every PC from the same arguments (or directly when not in a multiplayer game). 2 parameters.
        // The same four steps the window did: OverwriteColorChannel("skin", colour, null), SkinColorBase, then the two redraws.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncPaint(Pawn pawn, Color color)
        {
            if (pawn == null || pawn.Destroyed || pawn.Discarded || pawn.Dead || pawn.story == null)
                return;

            var comp = FindAlienComp(pawn);
            if (comp == null)
                return;

            LogCommand("Paint", pawn, $"color={color}");
            overwriteMethod.Invoke(comp, new object[] { SkinChannel, color, null });
            pawn.story.SkinColorBase = color;
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
            PortraitsCache.SetDirty(pawn);
        }

        #endregion
    }
}
