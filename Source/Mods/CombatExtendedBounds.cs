using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - every PC uses the host's hit bounds</summary>
    /// <remarks>
    /// FIXES row 64 (17 Oct 2026). CE measures the hit bounds of non-humanlike pawns and plants from their texture pixels at
    /// startup (<c>BoundsInjector.Inject</c>) and caches them per graphic path in <c>BoundsInjector.boundMap</c>. The pixels
    /// are not the same on every PC: lab mp64 hashed the textures on both sides - the windowed host holds them BC7-compressed
    /// (lossy), the headless arbiter uncompressed, and GPU vendors compress differently (row 19). Lab mp62/mp63: 216 of 2,055
    /// bounds differ, mostly by one pixel row, so a bullet grazing an edge hits on one PC only (mp61 desync). No measuring
    /// method can fix different input pixels, so in MP the host's table is written into the save (Game.ExposeData) and every
    /// PC that loads it (joiners, the arbiter, the host at each join point) replaces its own entries with the host's.
    /// Values are stored as float bits, so they are copied exactly. Single player is unchanged.
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedBounds
    {
        private const string LogPrefix = "[MPCompat - CE bounds] ";
        private static FieldInfo boundMapField;

        public CombatExtendedBounds(ModContentPack mod)
        {
            var injector = AccessTools.TypeByName("CombatExtended.BoundsInjector")
                           ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.BoundsInjector not found");
            boundMapField = AccessTools.DeclaredField(injector, "boundMap")
                            ?? throw new MissingFieldException(injector.FullName, "boundMap");
            if (!typeof(IDictionary).IsAssignableFrom(boundMapField.FieldType))
                throw new MissingFieldException($"{LogPrefix}BoundsInjector.boundMap is {boundMapField.FieldType}, expected a dictionary");

            // ExposeSmallComponents, not ExposeData: saving goes ExposeData -> ExposeSmallComponents, but loading goes
            // Game.LoadGame -> ExposeSmallComponents (decomp17 Game.cs:401, :571), so an ExposeData postfix never loads
            // (lab mp65: host table saved, never read back).
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), "ExposeSmallComponents")
                                   ?? throw new MissingMethodException(typeof(Game).FullName, "ExposeSmallComponents"),
                postfix: new HarmonyMethod(typeof(CombatExtendedBounds), nameof(ExposeHostBounds)));
        }

        // One string per entry: "<x float bits hex> <y float bits hex> <graphic path>" (the path may hold spaces).
        private static void ExposeHostBounds()
        {
            if (!MP.IsInMultiplayer)
                return;

            var map = (IDictionary)boundMapField.GetValue(null);
            List<string> entries = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                entries = new List<string>(map.Count);
                foreach (DictionaryEntry kv in map)
                {
                    var v = (Vector2)kv.Value;
                    entries.Add(BitConverter.SingleToInt32Bits(v.x).ToString("X8") + " " +
                                BitConverter.SingleToInt32Bits(v.y).ToString("X8") + " " + kv.Key);
                }
                entries.Sort(StringComparer.Ordinal);
            }

            Scribe_Collections.Look(ref entries, "mpCompatCeHitBounds", LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars || entries == null)
                return;

            int replaced = 0, bad = 0;
            foreach (string e in entries)
            {
                string[] p = e?.Split(new[] { ' ' }, 3);
                if (p == null || p.Length != 3 ||
                    !int.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out int xb) ||
                    !int.TryParse(p[1], System.Globalization.NumberStyles.HexNumber, null, out int yb))
                {
                    bad++;
                    continue;
                }
                map[p[2]] = new Vector2(BitConverter.Int32BitsToSingle(xb), BitConverter.Int32BitsToSingle(yb));
                replaced++;
            }
            Log.Message($"{LogPrefix}hit bounds taken from the save: {replaced} entries" + (bad > 0 ? $", {bad} unreadable" : ""));
        }
    }
}
