using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Adeptus Mechanicus: Armoury by Ogliss - the composite apparel customization window</summary>
    /// <remarks>
    /// FIXES row 309. AdeptusMechanicus.Dialog_CustomizeApparelComposite is opened by a click on the Gear tab (a transpiler on
    /// ITab_Pawn_Gear.DrawThingRow adds the button), so it exists only on the clicker's PC; that stays. The window has no Accept / Cancel:
    /// every control writes SAVED state of the apparel at once (live, permanent), from UI code, on the clicker's PC only:
    ///   main texture   ApparelComposite.activeAltKey / activeAltInt (setter ActiveAltGraphic, menu in DrawMainTexVariantButton_GenerateMenu)
    ///   colours        CompColorable.color / active, CompColorableTwo.colorTwo / activeTwo, CompColorableTwoFaction.factioncolor(Two) /
    ///                  factionactive(Two) (AdeptusWidgets.ColorSelector writes them through a ref, the Ideo / favourite colour buttons write fields)
    ///   faction preset CompColorableTwoFaction.faction / activeFaction (setter FactionDef)
    ///   pauldrons      ShoulderPadEntry.activeOption / faction of every CompPauldronDrawer.activeEntries entry (variant and faction menus)
    /// Graphic caches (ShoulderPadEntry.graphic, Thing.graphicInt, Graphic.path) are visual only and stay local.
    /// Those writes are spread over menu lambdas (compiler generated closures) and a ref parameter, so the module does not patch each one.
    /// It watches the saved fields instead: while this PC has such a window open (multiplayer, interface code), every frame of the window
    /// and every call of AdeptusApparelUtility.UpdateApparelGraphicsFor (every writer ends with it) compares the fields with the last
    /// known state. A difference is taken back at once on this PC and sent as ONE synced command (SyncApply: the apparel Thing and the
    /// changed keys only), which sets the same fields on every PC, so the saved state never differs between PCs. The command carries
    /// only the keys that changed (not the whole state), so two commands from one click (ColorSelector returns, THEN the caller sets the
    /// Active flag) do not undo each other. A command from another player on the same apparel updates the known state too.
    /// A difference counts as a window edit only when it appears INSIDE a window action: the window's FillMainRect (colour swatches
    /// and buttons; the state is re-read at its start, so a simulation write between frames, e.g. the vanilla recolor job, is never
    /// taken for an edit) or FloatMenuOption.Chosen (the menus; same re-read at its start). The UpdateApparelGraphicsFor prefix checks
    /// only inside those two scopes. Nothing ticks inside OnGUI, so no simulation write can fall inside a scope.
    /// Getters of the mod that fill saved fields lazily while the window draws (CompColorableTwoFaction.FactionDef, ShoulderPadEntry.Props
    /// through Options) are called once when the state is read in interface code, BEFORE the baseline is taken, so their writes are part
    /// of the baseline and never an edit. A send identical to one that is still in flight is not repeated.
    /// Not synced on purpose: graphic caches and portraits (rebuilt locally after each command), window scroll / selection, the static
    /// colour list, the dev log. The Graphic.path edit in DrawFactionButton_GenerateMenu (appends the variant to the shared graphic
    /// path) stays on the clicker's PC; it is a mod bug that is not saved and no simulation code reads it.
    /// Supported build: Adeptus Mechanicus: Armoury 1541438614, LoadFolders v1.6 (no v1.7 folder, so that folder loads in 1.7),
    /// 1.6/Assemblies/AdeptusMechanicusArmoury.dll. Every member is looked up before anything is patched or registered; if one is
    /// missing the module patches nothing (the window then works as without the module).
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("Ogliss.AdMech.Armoury")]
    internal class AdMechApparelComposite
    {
        private const string LogPrefix = "[MPCompat - AdMech Apparel] ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // mod types, found by name
        private static Type compositeType, dialogType, utilType, colorTwoType, colorFactionType;

        // ApparelComposite
        private static FieldInfo altKeyField, altIntField, altGraphicField, altCacheField, altFailedField;
        private static MethodInfo colorableGetter, pauldronsGetter;

        // comps
        private static FieldInfo colorField, colorActiveField, colorTwoField, colorTwoActiveField;
        private static FieldInfo factionField, factionActiveField, factionColorField, factionColorTwoField, factionActiveColorField, factionActiveColorTwoField;
        private static FieldInfo activeEntriesField;

        // ShoulderPadEntry / PauldronTextureOption
        private static FieldInfo entryOptionField, entryFactionField, optionTexPathField;
        private static MethodInfo entryOptionsGetter, entryDefaultGetter, entryGraphicSetter;

        // window
        private static FieldInfo dialogThingField;

        // True only when everything was found, registered and patched; while false nothing is compared or sent.
        private static bool active;
        private static bool checking;
        private static bool warnedUnsendable;

        // inside the window's FillMainRect / inside FloatMenuOption.Chosen on this PC (the only places an edit can be made)
        private static bool inFill, inChosen;
        private static MethodInfo factionDefGetter;

        private sealed class Tracked
        {
            public Thing apparel;
            public Dictionary<string, string> last;
            // the delta sent and not yet executed here (cleared by SyncApply when this PC's own command runs)
            public string pending;
        }

        // apparel thingIDNumber -> last known saved state, for the apparel shown in a window of this PC
        private static readonly Dictionary<int, Tracked> tracked = new();

        public AdMechApparelComposite(ModContentPack mod)
        {
            var problems = new List<string>();
            try
            {
                Resolve(problems);
            }
            catch (Exception e)
            {
                problems.Add($"{e.GetType().Name}: {e.Message}");
            }

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                    Log.Error($"{LogPrefix}missing: {problem}");
                Log.Error($"{LogPrefix}this Adeptus Mechanicus: Armoury build does not have what the module syncs: nothing patched, the customization window behaves as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported Adeptus Mechanicus: Armoury build");
            }

            MP.RegisterSyncMethod(AccessTools.DeclaredMethod(typeof(AdMechApparelComposite), nameof(SyncApply)));
            Patch(AccessTools.DeclaredMethod(dialogType, "FillMainRect"), prefix: nameof(PreFill), postfix: nameof(PostFill), finalizer: nameof(FinalizeFill));
            Patch(AccessTools.DeclaredMethod(dialogType, "PostClose"), postfix: nameof(PostDialogClose));
            Patch(AccessTools.DeclaredMethod(utilType, "UpdateApparelGraphicsFor"), prefix: nameof(PreUpdateApparelGraphics));
            Patch(AccessTools.DeclaredMethod(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen), new[] { typeof(bool), typeof(FloatMenu) }),
                prefix: nameof(PreChosen), postfix: nameof(PostChosen), finalizer: nameof(FinalizeChosen));
            Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)), postfix: nameof(PostGameFinalizeInit));
            active = true;
            Log.Message($"{LogPrefix}1 sync method registered, 5 patches installed, active=True.");
        }

        private static void Patch(MethodBase target, string prefix = null, string postfix = null, string finalizer = null)
        {
            if (target == null)
                throw new MissingMethodException("patch target not found");
            MpCompat.harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(AdMechApparelComposite), prefix) { priority = Priority.First },
                postfix: postfix == null ? null : new HarmonyMethod(typeof(AdMechApparelComposite), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(AdMechApparelComposite), finalizer));
        }

        #region Lookup

        private static T Need<T>(T value, string what, List<string> problems) where T : class
        {
            if (value == null)
                problems.Add(what);
            return value;
        }

        private static void Resolve(List<string> problems)
        {
            compositeType = Need(AccessTools.TypeByName("AdeptusMechanicus.ApparelComposite"), "type ApparelComposite", problems);
            dialogType = Need(AccessTools.TypeByName("AdeptusMechanicus.Dialog_CustomizeApparelComposite"), "type Dialog_CustomizeApparelComposite", problems);
            utilType = Need(AccessTools.TypeByName("AdeptusMechanicus.AdeptusApparelUtility"), "type AdeptusApparelUtility", problems);
            colorTwoType = Need(AccessTools.TypeByName("AdeptusMechanicus.CompColorableTwo"), "type CompColorableTwo", problems);
            colorFactionType = Need(AccessTools.TypeByName("AdeptusMechanicus.CompColorableTwoFaction"), "type CompColorableTwoFaction", problems);
            var pauldronCompType = Need(AccessTools.TypeByName("AdeptusMechanicus.CompPauldronDrawer"), "type CompPauldronDrawer", problems);
            var entryType = Need(AccessTools.TypeByName("AdeptusMechanicus.ShoulderPadEntry"), "type ShoulderPadEntry", problems);
            var optionType = Need(AccessTools.TypeByName("AdeptusMechanicus.PauldronTextureOption"), "type PauldronTextureOption", problems);
            if (problems.Count > 0)
                return;

            altKeyField = Need(AccessTools.DeclaredField(compositeType, "activeAltKey"), "ApparelComposite.activeAltKey", problems);
            altIntField = Need(AccessTools.DeclaredField(compositeType, "activeAltInt"), "ApparelComposite.activeAltInt", problems);
            altGraphicField = Need(AccessTools.DeclaredField(compositeType, "activeAltGraphic"), "ApparelComposite.activeAltGraphic", problems);
            altCacheField = Need(AccessTools.DeclaredField(compositeType, "_graphic"), "ApparelComposite._graphic", problems);
            altFailedField = Need(AccessTools.DeclaredField(compositeType, "failedgraphic"), "ApparelComposite.failedgraphic", problems);
            colorableGetter = Need(AccessTools.DeclaredPropertyGetter(compositeType, "Colorable"), "ApparelComposite.Colorable", problems);
            pauldronsGetter = Need(AccessTools.DeclaredPropertyGetter(compositeType, "Pauldrons"), "ApparelComposite.Pauldrons", problems);

            colorField = Need(AccessTools.DeclaredField(typeof(CompColorable), "color"), "Verse.CompColorable.color", problems);
            colorActiveField = Need(AccessTools.DeclaredField(typeof(CompColorable), "active"), "Verse.CompColorable.active", problems);
            colorTwoField = Need(AccessTools.DeclaredField(colorTwoType, "colorTwo"), "CompColorableTwo.colorTwo", problems);
            colorTwoActiveField = Need(AccessTools.DeclaredField(colorTwoType, "activeTwo"), "CompColorableTwo.activeTwo", problems);
            factionField = Need(AccessTools.DeclaredField(colorFactionType, "faction"), "CompColorableTwoFaction.faction", problems);
            factionActiveField = Need(AccessTools.DeclaredField(colorFactionType, "activeFaction"), "CompColorableTwoFaction.activeFaction", problems);
            factionColorField = Need(AccessTools.DeclaredField(colorFactionType, "factioncolor"), "CompColorableTwoFaction.factioncolor", problems);
            factionColorTwoField = Need(AccessTools.DeclaredField(colorFactionType, "factioncolorTwo"), "CompColorableTwoFaction.factioncolorTwo", problems);
            factionActiveColorField = Need(AccessTools.DeclaredField(colorFactionType, "factionactive"), "CompColorableTwoFaction.factionactive", problems);
            factionActiveColorTwoField = Need(AccessTools.DeclaredField(colorFactionType, "factionactiveTwo"), "CompColorableTwoFaction.factionactiveTwo", problems);
            factionDefGetter = Need(AccessTools.DeclaredPropertyGetter(colorFactionType, "FactionDef"), "CompColorableTwoFaction.FactionDef getter", problems);
            Need(AccessTools.DeclaredMethod(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen), new[] { typeof(bool), typeof(FloatMenu) }), "Verse.FloatMenuOption.Chosen(bool, FloatMenu)", problems);
            activeEntriesField = Need(AccessTools.DeclaredField(pauldronCompType, "activeEntries"), "CompPauldronDrawer.activeEntries", problems);

            entryOptionField = Need(AccessTools.DeclaredField(entryType, "activeOption"), "ShoulderPadEntry.activeOption", problems);
            entryFactionField = Need(AccessTools.DeclaredField(entryType, "faction"), "ShoulderPadEntry.faction", problems);
            entryOptionsGetter = Need(AccessTools.DeclaredPropertyGetter(entryType, "Options"), "ShoulderPadEntry.Options", problems);
            entryDefaultGetter = Need(AccessTools.DeclaredPropertyGetter(entryType, "DefaultOption"), "ShoulderPadEntry.DefaultOption", problems);
            entryGraphicSetter = Need(AccessTools.DeclaredPropertySetter(entryType, "Graphic"), "ShoulderPadEntry.Graphic setter", problems);
            optionTexPathField = Need(AccessTools.DeclaredField(optionType, "texPath"), "PauldronTextureOption.texPath", problems);

            dialogThingField = Need(AccessTools.DeclaredField(dialogType, "thing"), "Dialog_CustomizeApparelComposite.thing", problems);
            Need(AccessTools.DeclaredMethod(dialogType, "FillMainRect"), "Dialog_CustomizeApparelComposite.FillMainRect", problems);
            Need(AccessTools.DeclaredMethod(dialogType, "PostClose"), "Dialog_CustomizeApparelComposite.PostClose", problems);
            Need(AccessTools.DeclaredMethod(utilType, "UpdateApparelGraphicsFor", new[] { typeof(Pawn) }), "AdeptusApparelUtility.UpdateApparelGraphicsFor(Pawn)", problems);
        }

        #endregion

        #region Value codecs

        private static string Enc(Color c)
            => string.Join(",", c.r.ToString("R", Inv), c.g.ToString("R", Inv), c.b.ToString("R", Inv), c.a.ToString("R", Inv));

        private static bool TryDec(string s, out Color c)
        {
            c = default;
            var parts = s?.Split(',');
            if (parts == null || parts.Length != 4)
                return false;
            var f = new float[4];
            for (int i = 0; i < 4; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out f[i]))
                    return false;
            c = new Color(f[0], f[1], f[2], f[3]);
            return true;
        }

        private static string Enc(bool b) => b ? "1" : "0";

        // null and "" differ for activeAltKey (the getter tests NullOrEmpty, but the field is saved as is)
        private static string EncStr(string s) => s == null ? "n" : "s:" + s;

        private static bool TryDecStr(string s, out string value)
        {
            value = null;
            if (s == "n")
                return true;
            if (s != null && s.StartsWith("s:", StringComparison.Ordinal))
            {
                value = s.Substring(2);
                return true;
            }

            return false;
        }

        #endregion

        #region State

        // The saved state the window can write, as key -> text. Fields are read directly (no property getters: FactionDef and
        // ActiveAltGraphic fill their caches lazily). Keys of members a comp type does not have are simply absent.
        private static Dictionary<string, string> Capture(Thing apparel)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            // In interface code the mod's lazy getters are called first, so what they fill in is part of the state read here (the window
            // calls them every frame anyway). Never in a synced command: that must not write anything the command does not name.
            bool prime = MP.IsInMultiplayer && MP.InInterface;
            d["alt.key"] = EncStr(altKeyField.GetValue(apparel) as string);
            d["alt.int"] = ((int)altIntField.GetValue(apparel)).ToString(Inv);

            if (colorableGetter.Invoke(apparel, null) is CompColorable comp)
            {
                d["c1"] = Enc((Color)colorField.GetValue(comp));
                d["c1a"] = Enc((bool)colorActiveField.GetValue(comp));
                if (colorTwoType.IsInstanceOfType(comp))
                {
                    d["c2"] = Enc((Color)colorTwoField.GetValue(comp));
                    d["c2a"] = Enc((bool)colorTwoActiveField.GetValue(comp));
                }

                if (colorFactionType.IsInstanceOfType(comp))
                {
                    if (prime)
                    {
                        try
                        {
                            factionDefGetter.Invoke(comp, null);
                        }
                        catch (Exception)
                        {
                            // the getter is only called for its side effect
                        }
                    }

                    d["f.def"] = (factionField.GetValue(comp) as FactionDef)?.defName ?? "";
                    d["f.act"] = Enc((bool)factionActiveField.GetValue(comp));
                    d["f.c1"] = Enc((Color)factionColorField.GetValue(comp));
                    d["f.c2"] = Enc((Color)factionColorTwoField.GetValue(comp));
                    d["f.a1"] = Enc((bool)factionActiveColorField.GetValue(comp));
                    d["f.a2"] = Enc((bool)factionActiveColorTwoField.GetValue(comp));
                }
            }

            if (pauldronsGetter.Invoke(apparel, null) is IList pauldrons)
            {
                for (int i = 0; i < pauldrons.Count; i++)
                {
                    if (activeEntriesField.GetValue(pauldrons[i]) is not IList entries)
                        continue;
                    for (int j = 0; j < entries.Count; j++)
                    {
                        var entry = entries[j];
                        if (entry == null)
                            continue;
                        d[$"p{i}.{j}.o"] = OptionToken(entry);
                        d[$"p{i}.{j}.f"] = (entryFactionField.GetValue(entry) as FactionDef)?.defName ?? "";
                    }
                }
            }

            return d;
        }

        // The chosen pauldron texture as a token that survives a save / load: the saved activeOption is a deserialised copy (not the
        // object in Options), so it is matched to Options by index, else by texPath. n = none, d = DefaultOption, o<i> = Options[i].
        // u:<texPath> = not matched: compared, but never applied.
        private static string OptionToken(object entry)
        {
            try
            {
                // Options runs ShoulderPadEntry.Props, which on its first call sets a missing activeOption to the default option:
                // call it BEFORE reading activeOption so that first draw after a load is not seen as an edit
                var options = entryOptionsGetter.Invoke(entry, null) as IList;
                var active = entryOptionField.GetValue(entry);
                if (active == null)
                    return "n";
                if (options != null)
                    for (int i = 0; i < options.Count; i++)
                        if (ReferenceEquals(options[i], active))
                            return "o" + i.ToString(Inv);
                var def = entryDefaultGetter.Invoke(entry, null);
                if (ReferenceEquals(def, active))
                    return "d";
                var path = optionTexPathField.GetValue(active) as string;
                if (options != null)
                    for (int i = 0; i < options.Count; i++)
                        if (options[i] != null && (optionTexPathField.GetValue(options[i]) as string) == path)
                            return "o" + i.ToString(Inv);
                if (def != null && (optionTexPathField.GetValue(def) as string) == path)
                    return "d";
                return "u:" + path;
            }
            catch (Exception)
            {
                return "x";
            }
        }

        // Sets the fields named in kv; keys it does not know are ignored. Returns whether anything changed. Does no graphics work
        // except dropping the caches that hold stale data; RefreshVisuals does the rest.
        private static bool ApplyDelta(Thing apparel, Dictionary<string, string> kv)
        {
            bool changed = false;

            bool alt = false;
            if (kv.TryGetValue("alt.key", out var altKeyText) && TryDecStr(altKeyText, out var altKey) && !string.Equals(altKeyField.GetValue(apparel) as string, altKey, StringComparison.Ordinal))
            {
                altKeyField.SetValue(apparel, altKey);
                alt = true;
            }

            if (kv.TryGetValue("alt.int", out var altIntText) && int.TryParse(altIntText, NumberStyles.Integer, Inv, out var altInt) && (int)altIntField.GetValue(apparel) != altInt)
            {
                altIntField.SetValue(apparel, altInt);
                alt = true;
            }

            if (alt)
            {
                // what the ActiveAltGraphic setter resets; the getter then finds the graphic again from key / index
                altGraphicField.SetValue(apparel, null);
                altCacheField.SetValue(apparel, null);
                altFailedField.SetValue(apparel, false);
                changed = true;
            }

            bool colours = false;
            if (colorableGetter.Invoke(apparel, null) is CompColorable comp)
            {
                colours |= SetColor(comp, colorField, kv, "c1");
                colours |= SetBool(comp, colorActiveField, kv, "c1a");
                if (colorTwoType.IsInstanceOfType(comp))
                {
                    colours |= SetColor(comp, colorTwoField, kv, "c2");
                    colours |= SetBool(comp, colorTwoActiveField, kv, "c2a");
                }

                if (colorFactionType.IsInstanceOfType(comp))
                {
                    if (kv.TryGetValue("f.def", out var factionName))
                    {
                        var def = factionName.NullOrEmpty() ? null : DefDatabase<FactionDef>.GetNamedSilentFail(factionName);
                        if ((factionName.NullOrEmpty() || def != null) && !ReferenceEquals(factionField.GetValue(comp), def))
                        {
                            factionField.SetValue(comp, def);
                            colours = true;
                        }
                    }

                    colours |= SetBool(comp, factionActiveField, kv, "f.act");
                    colours |= SetColor(comp, factionColorField, kv, "f.c1");
                    colours |= SetColor(comp, factionColorTwoField, kv, "f.c2");
                    colours |= SetBool(comp, factionActiveColorField, kv, "f.a1");
                    colours |= SetBool(comp, factionActiveColorTwoField, kv, "f.a2");
                }
            }

            bool pads = false;
            var pauldrons = pauldronsGetter.Invoke(apparel, null) as IList;
            if (pauldrons != null)
            {
                foreach (var pair in kv)
                {
                    var parts = pair.Key.Split('.');
                    if (parts.Length != 3 || parts[0].Length < 2 || parts[0][0] != 'p'
                        || !int.TryParse(parts[0].Substring(1), NumberStyles.Integer, Inv, out var i)
                        || !int.TryParse(parts[1], NumberStyles.Integer, Inv, out var j))
                        continue;
                    if (i < 0 || i >= pauldrons.Count || activeEntriesField.GetValue(pauldrons[i]) is not IList entries || j < 0 || j >= entries.Count || entries[j] == null)
                        continue;
                    var entry = entries[j];
                    if (parts[2] == "f")
                    {
                        var def = pair.Value.NullOrEmpty() ? null : DefDatabase<FactionDef>.GetNamedSilentFail(pair.Value);
                        if ((pair.Value.NullOrEmpty() || def != null) && !ReferenceEquals(entryFactionField.GetValue(entry), def))
                        {
                            entryFactionField.SetValue(entry, def);
                            pads = true;
                        }
                    }
                    else if (parts[2] == "o" && ApplyOption(entry, pair.Value))
                        pads = true;
                }
            }

            if (colours)
                apparel.Notify_ColorChanged(); // what the colour setters of the comps call: drops the apparel's cached graphic

            // a colour or faction change also changes the pauldron colours: let every entry rebuild its graphic when it is drawn next
            if ((pads || colours) && pauldrons != null)
            {
                foreach (var pauldron in pauldrons)
                    if (activeEntriesField.GetValue(pauldron) is IList entries)
                        foreach (var entry in entries)
                            if (entry != null)
                                entryGraphicSetter.Invoke(entry, new object[] { null });
            }

            return changed || colours || pads;
        }

        private static bool ApplyOption(object entry, string token)
        {
            if (string.IsNullOrEmpty(token) || token[0] == 'u' || token[0] == 'x')
                return false;
            object wanted;
            if (token == "n")
                wanted = null;
            else
            {
                if (entryOptionsGetter.Invoke(entry, null) is not IList options)
                    return false;
                if (token == "d")
                    wanted = entryDefaultGetter.Invoke(entry, null);
                else if (token[0] == 'o' && int.TryParse(token.Substring(1), NumberStyles.Integer, Inv, out var index) && index >= 0 && index < options.Count)
                    wanted = options[index];
                else
                    return false;
            }

            if (ReferenceEquals(entryOptionField.GetValue(entry), wanted))
                return false;
            entryOptionField.SetValue(entry, wanted);
            return true;
        }

        private static bool SetColor(object target, FieldInfo field, Dictionary<string, string> kv, string key)
        {
            if (!kv.TryGetValue(key, out var text) || !TryDec(text, out var value) || (Color)field.GetValue(target) == value)
                return false;
            field.SetValue(target, value);
            return true;
        }

        private static bool SetBool(object target, FieldInfo field, Dictionary<string, string> kv, string key)
        {
            if (!kv.TryGetValue(key, out var text) || (text != "0" && text != "1") || (bool)field.GetValue(target) == (text == "1"))
                return false;
            field.SetValue(target, text == "1");
            return true;
        }

        private static void RefreshVisuals(Thing apparel)
        {
            if (apparel is Apparel { Wearer: { } pawn })
            {
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);
            }
        }

        private static string Serialize(Dictionary<string, string> kv)
        {
            var sb = new StringBuilder();
            foreach (var pair in kv.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
            return sb.ToString();
        }

        private static Dictionary<string, string> Parse(string text)
        {
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            if (text == null)
                return kv;
            foreach (var line in text.Split('\n'))
            {
                int at = line.IndexOf('=');
                if (at > 0)
                    kv[line.Substring(0, at)] = line.Substring(at + 1);
            }

            return kv;
        }

        // FNV-1a (string.GetHashCode differs between processes): lab evidence, the same number on every PC means the same saved state
        private static uint Hash(string text)
        {
            uint h = 2166136261;
            foreach (char c in text)
                h = (h ^ c) * 16777619;
            return h;
        }

        #endregion

        #region The synced command

        // Runs on every PC from the same arguments. Sets the named keys of the apparel's saved state, drops graphic caches, and brings
        // this PC's window bookkeeping up to date. No RNG, no Faction.OfPlayer, no Find.CurrentMap.
        private static void SyncApply(Thing thing, string delta)
        {
            if (!active || thing == null || !compositeType.IsInstanceOfType(thing))
                return;
            var kv = Parse(delta);
            ApplyDelta(thing, kv);
            RefreshVisuals(thing);
            var state = Capture(thing);
            if (tracked.TryGetValue(thing.thingIDNumber, out var t))
            {
                t.last = state;
                if (MP.IsExecutingSyncCommandIssuedBySelf)
                    t.pending = null; // this PC's own command has come back
            }

            Log.Message($"{LogPrefix}{thing.ThingID} keys=[{string.Join(",", kv.Keys.OrderBy(k => k, StringComparer.Ordinal))}] state-hash={Hash(Serialize(state)):x8} " +
                        $"tick={Find.TickManager?.TicksGame} bySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
        }

        #endregion

        #region Watching the window

        private static bool Live => active && MP.IsInMultiplayer && MP.InInterface;

        private static bool CanSend(Thing apparel)
        {
            // MP writes a Thing as id + holder: the map, or the spawned thing / world object that holds it (the wearer pawn for worn apparel)
            if (apparel.Spawned || ThingOwnerUtility.GetFirstSpawnedParentThing(apparel) != null)
                return true;
            for (var holder = apparel.ParentHolder; holder != null; holder = holder.ParentHolder)
                if (holder is WorldObject)
                    return true;
            return false;
        }

        // Compares the apparel with its last known state. A difference is the window's own edit: undo it here and send it.
        private static void Check(Thing apparel)
        {
            if (checking || !tracked.TryGetValue(apparel.thingIDNumber, out var t))
                return;
            checking = true;
            try
            {
                var current = Capture(apparel);
                Dictionary<string, string> sent = null, undo = null;
                foreach (var pair in current)
                {
                    // keys that are new since the last look (an entry that was set up meanwhile) are adopted, not sent
                    if (!t.last.TryGetValue(pair.Key, out var before) || before == pair.Value)
                        continue;
                    (sent ??= new Dictionary<string, string>())[pair.Key] = pair.Value;
                    (undo ??= new Dictionary<string, string>())[pair.Key] = before;
                }

                if (sent == null)
                {
                    t.last = current;
                    return;
                }

                // take the edit back on this PC (the synced command sets it everywhere, this PC included), then remember what is there now
                ApplyDelta(apparel, undo);
                RefreshVisuals(apparel);
                t.last = Capture(apparel);

                // the same edit again while its command has not come back (a getter of the mod that writes every frame): undone, not resent
                var text = Serialize(sent);
                if (text == t.pending)
                    return;

                if (!CanSend(apparel))
                {
                    if (!warnedUnsendable)
                        Log.Warning($"{LogPrefix}{apparel.ThingID} is not held by anything Multiplayer can address: the customization was undone and not sent.");
                    warnedUnsendable = true;
                    return;
                }

                t.pending = text;
                SyncApply(apparel, text);
            }
            finally
            {
                checking = false;
            }
        }

        private static Thing DialogApparel(object dialog)
        {
            var thing = dialogThingField.GetValue(dialog) as Thing;
            return thing != null && compositeType.IsInstanceOfType(thing) ? thing : null;
        }

        // Reads the apparel's state as the new baseline (no undo, no send): whatever changed since the last look was not made by the
        // window (the simulation ticks between frames: vanilla recolor job, another player's command, a lazy getter).
        private static Tracked Rebaseline(Thing apparel)
        {
            if (tracked.TryGetValue(apparel.thingIDNumber, out var t) && ReferenceEquals(t.apparel, apparel))
                t.last = Capture(apparel);
            else
                tracked[apparel.thingIDNumber] = t = new Tracked { apparel = apparel, last = Capture(apparel) };
            return t;
        }

        // FillMainRect: the start only re-reads the state (an edit from a menu is caught in Chosen, below); the end compares, which catches
        // the colour swatches and buttons clicked in this very pass. Nothing ticks inside one OnGUI pass.
        private static void PreFill(object __instance)
        {
            if (!Live)
                return;
            try
            {
                var apparel = DialogApparel(__instance);
                if (apparel == null)
                    return;
                inFill = true;
                Rebaseline(apparel);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"{LogPrefix}watching the window failed: {e}", 0x3a0d9c01);
            }
        }

        private static void PostFill(object __instance)
        {
            if (!Live || !inFill)
                return;
            try
            {
                var apparel = DialogApparel(__instance);
                if (apparel != null)
                    Check(apparel);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"{LogPrefix}watching the window failed: {e}", 0x3a0d9c02);
            }
        }

        private static void FinalizeFill()
        {
            inFill = false;
        }

        // The window's menus (Widgets.Dropdown) run their lambdas from FloatMenuOption.Chosen, which is synchronous. Re-read the tracked
        // apparel first (a simulation write since the last frame is not the menu's), then compare once the lambda has run.
        private static void PreChosen()
        {
            if (tracked.Count == 0 || !Live)
                return;
            try
            {
                inChosen = true;
                foreach (var t in tracked.Values.ToList())
                    Rebaseline(t.apparel);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"{LogPrefix}reading the state before a menu choice failed: {e}", 0x3a0d9c04);
            }
        }

        private static void PostChosen()
        {
            if (!inChosen || !Live)
                return;
            try
            {
                foreach (var t in tracked.Values.ToList())
                    Check(t.apparel);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"{LogPrefix}checking after a menu choice failed: {e}", 0x3a0d9c05);
            }
        }

        private static void FinalizeChosen()
        {
            inChosen = false;
        }

        // Every writer of the window ends with UpdateApparelGraphicsFor(wearer), so the check runs right after the write, but only inside
        // the window's FillMainRect or a menu choice (outside them the state may have been written by the simulation).
        private static void PreUpdateApparelGraphics(Pawn pawn)
        {
            if (pawn == null || tracked.Count == 0 || !(inFill || inChosen) || !Live)
                return;
            try
            {
                foreach (var t in tracked.Values.ToList())
                    if (t.apparel is Apparel { Wearer: { } wearer } && wearer == pawn)
                        Check(t.apparel);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"{LogPrefix}checking after a graphics update failed: {e}", 0x3a0d9c03);
            }
        }

        private static void PostDialogClose()
        {
            tracked.Clear();
        }

        private static void PostGameFinalizeInit()
        {
            tracked.Clear();
            warnedUnsendable = false;
        }

        #endregion
    }
}
