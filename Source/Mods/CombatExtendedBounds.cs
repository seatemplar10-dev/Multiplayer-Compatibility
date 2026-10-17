using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - hit bounds measured the same way on every PC</summary>
    /// <remarks>
    /// FIXES row 64 (17 Oct 2026). CE measures the hit bounds of non-humanlike pawns and plants from their texture pixels at
    /// startup (<c>BoundsInjector.Inject</c>): a windowed PC scales each texture on the GPU and reads it back, a headless one
    /// (the MP arbiter) cannot. Lab mp62 dumped the resulting cache on host and arbiter: 216 of 2,055 entries differ, mostly
    /// by one pixel row, so a bullet grazing an edge hits on one PC only (mp61 desync). GPU vendors can differ the same way
    /// (row 19). This module replaces Inject with the same steps done on the CPU on every PC: the texture's mip level is
    /// scaled with exact integer maths (<see cref="CpuBlit"/>, the rules of Waylith's blit), so every PC gets the same bytes.
    /// It is self-contained (no dependency on our CE build's own CPU fallback), so it works with any CE build.
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedBounds
    {
        private const string LogPrefix = "[MPCompat - CE bounds] ";

        private static MethodInfo collectTextures, injectPawnKinds, injectPlants, getScaledSize;
        private static FieldInfo textureCache;

        public CombatExtendedBounds(ModContentPack mod)
        {
            var injector = AccessTools.TypeByName("CombatExtended.BoundsInjector")
                           ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.BoundsInjector not found");
            var ceUtility = AccessTools.TypeByName("CombatExtended.CE_Utility")
                            ?? throw new TypeLoadException(LogPrefix + "type CombatExtended.CE_Utility not found");
            collectTextures = Need(AccessTools.DeclaredMethod(injector, "CollectTextures"), "BoundsInjector.CollectTextures");
            injectPawnKinds = Need(AccessTools.DeclaredMethod(injector, "InjectPawnKinds"), "BoundsInjector.InjectPawnKinds");
            injectPlants = Need(AccessTools.DeclaredMethod(injector, "InjectPlants"), "BoundsInjector.InjectPlants");
            getScaledSize = Need(AccessTools.DeclaredMethod(ceUtility, "GetScaledSize", [typeof(Texture2D)]), "CE_Utility.GetScaledSize");
            textureCache = AccessTools.DeclaredField(injector, "_textureCache")
                           ?? throw new MissingFieldException(injector.FullName, "_textureCache");

            MpCompat.harmony.Patch(Need(AccessTools.DeclaredMethod(injector, "Inject"), "BoundsInjector.Inject"),
                prefix: new HarmonyMethod(typeof(CombatExtendedBounds), nameof(InjectOnCpu)));
        }

        private static MethodInfo Need(MethodInfo m, string what) => m ?? throw new MissingMethodException(LogPrefix + what + " not found");

        // Same steps as BoundsInjector.Inject, with the GPU blit + readback replaced by CpuBlit. Runs in CE's startup long event
        // (main thread), so Texture2D.GetPixels32 is allowed.
        private static bool InjectOnCpu()
        {
            var pawnKinds = DefDatabase<PawnKindDef>.AllDefs.Where(k => !k.RaceProps.Humanlike).ToList();
            var plants = DefDatabase<ThingDef>.AllDefs.Where(d => d.plant != null).ToList();
            var textures = (IEnumerable)collectTextures.Invoke(null, [pawnKinds, plants]);
            var cache = (IDictionary)textureCache.GetValue(null);

            int done = 0, failed = 0;
            foreach (Texture2D tex in textures)
            {
                var (w, h) = ((int, int))getScaledSize.Invoke(null, [tex]);
                if (TryBlitToColorsCpu(tex, w, h, out Color[] colors, out string error))
                {
                    cache[tex] = (colors, w, h);
                    done++;
                }
                else
                {
                    failed++;
                    if (failed <= 5)
                        Log.Error($"{LogPrefix}texture '{tex?.name}' could not be read on the CPU: {error}");
                }
            }

            var pawnArgs = new object[1];
            foreach (var kind in pawnKinds)
            {
                pawnArgs[0] = kind;
                injectPawnKinds.Invoke(null, pawnArgs);
            }
            var plantArgs = new object[1];
            foreach (var plant in plants)
            {
                plantArgs[0] = plant;
                injectPlants.Invoke(null, plantArgs);
            }

            if (failed == 0)
                Log.Message($"{LogPrefix}bounds pre-generated on the CPU for {done} textures (same on every PC)");
            else
                Log.Error($"{LogPrefix}{failed} of {done + failed} textures could not be read on the CPU - their bounds are missing");
            return false;
        }

        // Same output contract as CE's GPU path: w*h colours, rows bottom-up, 8-bit values / 255f.
        private static bool TryBlitToColorsCpu(Texture2D tex, int w, int h, out Color[] colors, out string error)
        {
            colors = null;
            error = null;
            try
            {
                int mip = CpuBlit.SourceMip(tex.mipmapCount, tex.width, tex.height, w, h);
                int sw = Math.Max(1, tex.width >> mip);
                int sh = Math.Max(1, tex.height >> mip);
                Color32[] src = tex.GetPixels32(mip);
                if (src == null || src.Length != sw * sh)
                {
                    error = $"mip {mip} has {src?.Length ?? 0} pixels, expected {sw * sh}";
                    return false;
                }
                var raw = new byte[src.Length * 4];
                for (int i = 0; i < src.Length; i++)
                {
                    raw[i * 4] = src[i].r;
                    raw[i * 4 + 1] = src[i].g;
                    raw[i * 4 + 2] = src[i].b;
                    raw[i * 4 + 3] = src[i].a;
                }
                // Waylith's blit takes its filter from the SOURCE texture.
                byte[] scaled = CpuBlit.Scale(raw, sw, sh, w, h, tex.filterMode == FilterMode.Point);
                var result = new Color[w * h];
                for (int i = 0; i < result.Length; i++)
                    result[i] = new Color(scaled[i * 4] / 255f, scaled[i * 4 + 1] / 255f, scaled[i * 4 + 2] / 255f, scaled[i * 4 + 3] / 255f);
                colors = result;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// CPU twin of Waylith's "Graphics.Blit(tex, rt)": one source mip level (round-half-even log2 of the downscale ratio),
        /// texel centres mapped as (x + 0.5) * src / dst, Nearest = floor, Linear = 2-tap lerp around (that - 0.5) with
        /// clamp-to-edge, rounded to the nearest 8-bit value; rows bottom-up. Exact integer weights, so the result is the same
        /// on every machine. Copied from our CE 1.7 lab build (CpuBlit.cs, written for it on 16 Oct 2026).
        /// </summary>
        private static class CpuBlit
        {
            internal static int SourceMip(int mipCount, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
            {
                float ratio = MathF.Max((float)srcWidth / Math.Max(1, dstWidth), (float)srcHeight / Math.Max(1, dstHeight));
                if (ratio <= 1f)
                    return 0;
                return Math.Clamp((int)MathF.Round(MathF.Log2(ratio)), 0, Math.Max(1, mipCount) - 1);
            }

            internal static byte[] Scale(byte[] srcRgbaBottomUp, int srcWidth, int srcHeight, int dstWidth, int dstHeight, bool nearest)
            {
                if (srcRgbaBottomUp == null || srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0
                    || srcRgbaBottomUp.Length != checked(srcWidth * srcHeight * 4))
                    throw new ArgumentException("CpuBlit.Scale: bad dimensions or pixel array length.");

                var x0 = new int[dstWidth];
                var x1 = new int[dstWidth];
                var fx = new long[dstWidth];
                var y0 = new int[dstHeight];
                var y1 = new int[dstHeight];
                var fy = new long[dstHeight];
                Axis(srcWidth, dstWidth, nearest, x0, x1, fx);
                Axis(srcHeight, dstHeight, nearest, y0, y1, fy);

                long denX = 2L * dstWidth;
                long denY = 2L * dstHeight;
                long total = denX * denY;
                var result = new byte[dstWidth * dstHeight * 4];
                for (int yTop = 0; yTop < dstHeight; yTop++)
                {
                    int outRow = dstHeight - 1 - yTop;
                    int srcRowA = (srcHeight - 1 - y0[yTop]) * srcWidth;
                    int srcRowB = (srcHeight - 1 - y1[yTop]) * srcWidth;
                    long wy = fy[yTop];
                    for (int x = 0; x < dstWidth; x++)
                    {
                        int a = (srcRowA + x0[x]) * 4;
                        int b = (srcRowA + x1[x]) * 4;
                        int c = (srcRowB + x0[x]) * 4;
                        int d = (srcRowB + x1[x]) * 4;
                        long wx = fx[x];
                        int o = (outRow * dstWidth + x) * 4;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            long top = srcRgbaBottomUp[a + ch] * (denX - wx) + srcRgbaBottomUp[b + ch] * wx;
                            long bottom = srcRgbaBottomUp[c + ch] * (denX - wx) + srcRgbaBottomUp[d + ch] * wx;
                            long v = top * (denY - wy) + bottom * wy;
                            result[o + ch] = (byte)Math.Clamp((2 * v + total) / (2 * total), 0L, 255L);
                        }
                    }
                }
                return result;
            }

            private static void Axis(int srcSize, int dstSize, bool nearest, int[] i0, int[] i1, long[] weightNumerator)
            {
                long den = 2L * dstSize;
                for (int i = 0; i < dstSize; i++)
                {
                    if (nearest)
                    {
                        long s = (2L * i + 1) * srcSize / den;
                        i0[i] = i1[i] = (int)Math.Min(s, srcSize - 1);
                        weightNumerator[i] = 0;
                        continue;
                    }
                    long num = (2L * i + 1) * srcSize - dstSize;
                    long floor = num >= 0 ? num / den : -((-num + den - 1) / den);
                    weightNumerator[i] = num - floor * den;
                    i0[i] = (int)Math.Clamp(floor, 0L, (long)srcSize - 1);
                    i1[i] = (int)Math.Clamp(floor + 1, 0L, (long)srcSize - 1);
                }
            }
        }
    }
}
