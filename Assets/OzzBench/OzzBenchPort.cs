// Unity Mono datapoint for the ozz-animation C# rewrite evaluation.
// Pure scalar port (netstandard 2.1 compatible, no System.Runtime.Intrinsics),
// same algorithm as ~/ECS/OzzBench/managed/ScalarRuntime.cs.
// Run from UniCli:
//   eval 'OzzBenchPort.BenchRunner.Run("/Users/ai/ECS/OzzBench/managed/anim.bin", 1000, 100)'
using System;
using System.Diagnostics;
using System.IO;

namespace OzzBenchPort
{
    static class OzzMathS
    {
        public const float Sqrt2 = 1.4142135623730950488016887242097f;
        public const float Sqrt2_2 = 0.70710678118654752440084436210485f;

        public static float HalfToFloat(ushort h)
        {
            uint sign = (uint)(h & 0x8000);
            int expMant = (h & 0x7fff) << 13;
            float magic = BitConverter.Int32BitsToSingle((254 - 15) << 23);
            float infnan = BitConverter.Int32BitsToSingle((127 + 16) << 23);
            float adjustF = BitConverter.Int32BitsToSingle(expMant) * magic;
            uint adjustU = (uint)BitConverter.SingleToInt32Bits(adjustF);
            uint result = (adjustF >= infnan ? (adjustU | (255u << 23)) : adjustU) | (sign << 16);
            return BitConverter.Int32BitsToSingle(unchecked((int)result));
        }

        public static void UnpackQuat(ushort v0, ushort v1, ushort v2,
                                      out int biggest, out int sign,
                                      out int c0, out int c1, out int c2)
        {
            uint packed = (uint)v0 >> 3 | (uint)v1 << 13 | (uint)v2 << 29;
            biggest = v0 & 0x3;
            sign = (v0 >> 2) & 0x1;
            c0 = (int)(packed & 0x7fff);
            c1 = (int)((packed >> 15) & 0x7fff);
            c2 = v2 >> 1;
        }
    }

    sealed class KeyCtrl
    {
        public int NumKeys;
        public byte[] Ratios;
        public ushort[] Previouses;
        public ushort[] Values;
    }

    sealed class AnimData
    {
        public int NumTracks, NumSoaTracks, NumAlignedTracks, NumJoints;
        public float Duration;
        public float[] Timepoints;
        public KeyCtrl T, R, S;
        public short[] Parents;

        public static AnimData Load(string path)
        {
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                var a = new AnimData();
                a.NumTracks = br.ReadInt32();
                a.Duration = br.ReadSingle();
                int numTimepoints = br.ReadInt32();
                a.Timepoints = new float[numTimepoints];
                for (int i = 0; i < numTimepoints; ++i) a.Timepoints[i] = br.ReadSingle();
                a.NumSoaTracks = (a.NumTracks + 3) / 4;
                a.NumAlignedTracks = a.NumSoaTracks * 4;
                a.T = LoadCtrl(br);
                a.R = LoadCtrl(br);
                a.S = LoadCtrl(br);
                a.NumJoints = br.ReadInt32();
                a.Parents = new short[a.NumJoints];
                for (int i = 0; i < a.NumJoints; ++i) a.Parents[i] = br.ReadInt16();
                return a;
            }
        }

        static KeyCtrl LoadCtrl(BinaryReader br)
        {
            var c = new KeyCtrl();
            c.NumKeys = br.ReadInt32();
            byte stride = br.ReadByte();
            if (stride != 1) throw new NotSupportedException();
            c.Ratios = br.ReadBytes(c.NumKeys);
            c.Previouses = new ushort[c.NumKeys];
            for (int i = 0; i < c.NumKeys; ++i) c.Previouses[i] = br.ReadUInt16();
            c.Values = new ushort[c.NumKeys * 3];
            for (int i = 0; i < c.Values.Length; ++i) c.Values[i] = br.ReadUInt16();
            return c;
        }
    }

    static class CacheOps
    {
        static float KeyRatio(float[] timepoints, byte[] ratios, uint at) => timepoints[ratios[at]];

        static uint TrackForward(uint[] cache, ushort[] previouses, uint key, uint lastTrack, uint numTracks)
        {
            uint target = key - previouses[key];
            for (uint entry = lastTrack; entry < numTracks; ++entry)
                if (cache[entry] == target) return entry;
            for (uint entry = 0; ; ++entry)
                if (cache[entry] == target) return entry;
        }

        static uint TrackBackward(uint[] cache, uint target, uint lastTrack, uint numTracks)
        {
            for (uint entry = lastTrack; ; --entry)
            {
                if (cache[entry] == target) return entry;
                if (entry == 0) break;
            }
            for (uint entry = numTracks - 1; ; --entry)
                if (cache[entry] == target) return entry;
        }

        static void OutdateAll(byte[] outdated, int numSoaTracks)
        {
            int numFlags = (numSoaTracks + 7) / 8;
            int i = 0;
            for (; i < numFlags - 1; ++i) outdated[i] = 0xff;
            outdated[i] = (byte)(0xff >> (numFlags * 8 - numSoaTracks));
        }

        public static void UpdateCache(float ratio, float previousRatio, int numSoaTracks,
                                       float[] timepoints, KeyCtrl c,
                                       uint[] entries, byte[] outdated, ref uint next)
        {
            uint numTracks = (uint)(numSoaTracks * 4);
            uint numKeys = (uint)c.NumKeys;
            var previouses = c.Previouses;
            var ratios = c.Ratios;

            float delta = ratio - previousRatio;
            if (next == 0 || delta < 0f)
            {
                for (uint i = 0; i < numTracks; ++i) entries[i] = i + numTracks;
                next = numTracks * 2;
                OutdateAll(outdated, numSoaTracks);
            }

            uint track = 0;
            for (; next < numKeys && KeyRatio(timepoints, ratios, next - previouses[next]) <= ratio; ++next)
            {
                track = TrackForward(entries, previouses, next, track, numTracks);
                outdated[track / 32] |= (byte)(1 << (int)((track & 0x1f) / 4));
                entries[track] = next;
            }
            for (; KeyRatio(timepoints, ratios, (next - 1) - previouses[next - 1]) > ratio; --next)
            {
                track = TrackBackward(entries, next - 1, track, numTracks);
                outdated[track / 32] |= (byte)(1 << (int)((track & 0x1f) / 4));
                entries[track] -= previouses[entries[track]];
            }
        }
    }

    sealed class ScalarContext
    {
        public uint[] TEntries, REntries, SEntries;
        public byte[] TOutdated, ROutdated, SOutdated;
        public uint TNext, RNext, SNext;
        public float Ratio;
        public float[] THot, RHot, SHot;

        public ScalarContext(AnimData a)
        {
            int tracks = a.NumAlignedTracks, flags = (a.NumSoaTracks + 7) / 8;
            TEntries = new uint[tracks]; REntries = new uint[tracks]; SEntries = new uint[tracks];
            TOutdated = new byte[flags]; ROutdated = new byte[flags]; SOutdated = new byte[flags];
            THot = new float[tracks * 8]; RHot = new float[tracks * 10]; SHot = new float[tracks * 8];
        }
    }

    static class ScalarRuntime
    {
        static readonly byte[] CpntMapping = { 0, 0, 1, 2, 0, 0, 1, 2, 0, 1, 0, 2, 0, 1, 2, 0 };

        public static void Sample(AnimData a, ScalarContext ctx, float ratio, float[] locals)
        {
            ratio = Math.Max(0f, Math.Min(ratio, 1f));
            float previous = ctx.Ratio;
            ctx.Ratio = ratio;

            CacheOps.UpdateCache(ratio, previous, a.NumSoaTracks, a.Timepoints, a.T, ctx.TEntries, ctx.TOutdated, ref ctx.TNext);
            DecompressFloat3(a, a.T, ctx.TEntries, ctx.TOutdated, ctx.THot);
            CacheOps.UpdateCache(ratio, previous, a.NumSoaTracks, a.Timepoints, a.R, ctx.REntries, ctx.ROutdated, ref ctx.RNext);
            DecompressQuaternion(a, a.R, ctx.REntries, ctx.ROutdated, ctx.RHot);
            CacheOps.UpdateCache(ratio, previous, a.NumSoaTracks, a.Timepoints, a.S, ctx.SEntries, ctx.SOutdated, ref ctx.SNext);
            DecompressFloat3(a, a.S, ctx.SEntries, ctx.SOutdated, ctx.SHot);

            Interpolate(a, ctx, ratio, locals);
        }

        static void DecompressFloat3(AnimData a, KeyCtrl c, uint[] entries, byte[] outdated, float[] hot)
        {
            for (int j = 0; j < outdated.Length; ++j)
            {
                byte o = outdated[j];
                outdated[j] = 0;
                for (int i = j * 8; o != 0; ++i, o >>= 1)
                {
                    if ((o & 1) == 0) continue;
                    for (int lane = 0; lane < 4; ++lane)
                    {
                        int track = i * 4 + lane;
                        uint right = entries[track];
                        uint left = right - c.Previouses[right];
                        int b = track * 8;
                        hot[b] = a.Timepoints[c.Ratios[left]];
                        hot[b + 1] = a.Timepoints[c.Ratios[right]];
                        int lv = (int)left * 3, rv = (int)right * 3;
                        hot[b + 2] = OzzMathS.HalfToFloat(c.Values[lv]);
                        hot[b + 3] = OzzMathS.HalfToFloat(c.Values[lv + 1]);
                        hot[b + 4] = OzzMathS.HalfToFloat(c.Values[lv + 2]);
                        hot[b + 5] = OzzMathS.HalfToFloat(c.Values[rv]);
                        hot[b + 6] = OzzMathS.HalfToFloat(c.Values[rv + 1]);
                        hot[b + 7] = OzzMathS.HalfToFloat(c.Values[rv + 2]);
                    }
                }
            }
        }

        static void DecompressQuaternion(AnimData a, KeyCtrl c, uint[] entries, byte[] outdated, float[] hot)
        {
            for (int j = 0; j < outdated.Length; ++j)
            {
                byte o = outdated[j];
                outdated[j] = 0;
                for (int i = j * 8; o != 0; ++i, o >>= 1)
                {
                    if ((o & 1) == 0) continue;
                    for (int lane = 0; lane < 4; ++lane)
                    {
                        int track = i * 4 + lane;
                        uint right = entries[track];
                        uint left = right - c.Previouses[right];
                        int b = track * 10;
                        hot[b] = a.Timepoints[c.Ratios[left]];
                        hot[b + 1] = a.Timepoints[c.Ratios[right]];
                        DecompressQuat(c.Values, left, hot, b + 2);
                        DecompressQuat(c.Values, right, hot, b + 6);
                    }
                }
            }
        }

        static readonly float[] TmpStored = new float[3];
        static readonly float[] TmpQuat = new float[4];

        static void DecompressQuat(ushort[] values, uint key, float[] outQuat, int at)
        {
            int v = (int)key * 3;
            OzzMathS.UnpackQuat(values[v], values[v + 1], values[v + 2],
                                out int biggest, out int sign, out int c0, out int c1, out int c2);
            const float scale = OzzMathS.Sqrt2 / 32767f;
            const float offset = -OzzMathS.Sqrt2_2;
            TmpStored[0] = c0 * scale + offset;
            TmpStored[1] = c1 * scale + offset;
            TmpStored[2] = c2 * scale + offset;
            int m = biggest * 4;
            for (int comp = 0; comp < 4; ++comp) TmpQuat[comp] = TmpStored[CpntMapping[m + comp]];
            float dot = 0f;
            for (int comp = 0; comp < 4; ++comp) if (comp != biggest) dot += TmpQuat[comp] * TmpQuat[comp];
            float w = (float)Math.Sqrt(Math.Max(0f, 1f - dot));
            TmpQuat[biggest] = sign != 0 ? -w : w;
            outQuat[at] = TmpQuat[0]; outQuat[at + 1] = TmpQuat[1]; outQuat[at + 2] = TmpQuat[2]; outQuat[at + 3] = TmpQuat[3];
        }

        static void Interpolate(AnimData a, ScalarContext ctx, float ratio, float[] locals)
        {
            int numTracks = a.NumAlignedTracks;
            float[] t = ctx.THot, r = ctx.RHot, s = ctx.SHot;
            for (int i = 0; i < numTracks; ++i)
            {
                int tb = i * 8, rb = i * 10, sb = i * 8, ob = i * 10;
                float tr = (ratio - t[tb]) / (t[tb + 1] - t[tb]);
                locals[ob] = t[tb + 2] + (t[tb + 5] - t[tb + 2]) * tr;
                locals[ob + 1] = t[tb + 3] + (t[tb + 6] - t[tb + 3]) * tr;
                locals[ob + 2] = t[tb + 4] + (t[tb + 7] - t[tb + 4]) * tr;

                float rr = (ratio - r[rb]) / (r[rb + 1] - r[rb]);
                float qx = r[rb + 2] + (r[rb + 6] - r[rb + 2]) * rr;
                float qy = r[rb + 3] + (r[rb + 7] - r[rb + 3]) * rr;
                float qz = r[rb + 4] + (r[rb + 8] - r[rb + 4]) * rr;
                float qw = r[rb + 5] + (r[rb + 9] - r[rb + 5]) * rr;
                float invLen = 1f / (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                locals[ob + 3] = qx * invLen;
                locals[ob + 4] = qy * invLen;
                locals[ob + 5] = qz * invLen;
                locals[ob + 6] = qw * invLen;

                float sr = (ratio - s[sb]) / (s[sb + 1] - s[sb]);
                locals[ob + 7] = s[sb + 2] + (s[sb + 5] - s[sb + 2]) * sr;
                locals[ob + 8] = s[sb + 3] + (s[sb + 6] - s[sb + 3]) * sr;
                locals[ob + 9] = s[sb + 4] + (s[sb + 7] - s[sb + 4]) * sr;
            }
        }

        static readonly float[] TmpLocal = new float[16];

        public static void LocalToModel(AnimData a, float[] locals, float[] models)
        {
            var local = TmpLocal;
            for (int i = 0; i < a.NumJoints; ++i)
            {
                int b = i * 10;
                float tx = locals[b], ty = locals[b + 1], tz = locals[b + 2];
                float qx = locals[b + 3], qy = locals[b + 4], qz = locals[b + 5], qw = locals[b + 6];
                float sx = locals[b + 7], sy = locals[b + 8], sz = locals[b + 9];

                float xx = qx * qx, xy = qx * qy, xz = qx * qz, xw = qx * qw;
                float yy = qy * qy, yz = qy * qz, yw = qy * qw, zz = qz * qz, zw = qz * qw;

                local[0] = sx * (1f - 2f * (yy + zz)); local[1] = sx * 2f * (xy + zw); local[2] = sx * 2f * (xz - yw); local[3] = 0f;
                local[4] = sy * 2f * (xy - zw); local[5] = sy * (1f - 2f * (xx + zz)); local[6] = sy * 2f * (yz + xw); local[7] = 0f;
                local[8] = sz * 2f * (xz + yw); local[9] = sz * 2f * (yz - xw); local[10] = sz * (1f - 2f * (xx + yy)); local[11] = 0f;
                local[12] = tx; local[13] = ty; local[14] = tz; local[15] = 1f;

                int parent = a.Parents[i];
                int ob2 = i * 16;
                if (parent < 0)
                {
                    for (int k = 0; k < 16; ++k) models[ob2 + k] = local[k];
                }
                else
                {
                    int pb = parent * 16;
                    for (int col = 0; col < 4; ++col)
                    {
                        float d0 = local[col * 4], d1 = local[col * 4 + 1], d2 = local[col * 4 + 2], d3 = local[col * 4 + 3];
                        models[ob2 + col * 4] = models[pb] * d0 + models[pb + 4] * d1 + models[pb + 8] * d2 + models[pb + 12] * d3;
                        models[ob2 + col * 4 + 1] = models[pb + 1] * d0 + models[pb + 5] * d1 + models[pb + 9] * d2 + models[pb + 13] * d3;
                        models[ob2 + col * 4 + 2] = models[pb + 2] * d0 + models[pb + 6] * d1 + models[pb + 10] * d2 + models[pb + 14] * d3;
                        models[ob2 + col * 4 + 3] = models[pb + 3] * d0 + models[pb + 7] * d1 + models[pb + 11] * d2 + models[pb + 15] * d3;
                    }
                }
            }
        }
    }

    public static class BenchRunner
    {
        public static string Run(string animPath, int instances, int frames)
        {
            var anim = AnimData.Load(animPath);
            var rng = new System.Random(42);
            var ctxs = new ScalarContext[instances];
            var locals = new float[instances][];
            var models = new float[instances][];
            var times = new float[instances];
            var speeds = new float[instances];
            for (int i = 0; i < instances; ++i)
            {
                ctxs[i] = new ScalarContext(anim);
                locals[i] = new float[anim.NumAlignedTracks * 10];
                models[i] = new float[anim.NumJoints * 16];
                times[i] = (float)rng.NextDouble() * anim.Duration;
                speeds[i] = 0.5f + (float)rng.NextDouble();
            }
            const float dt = 1f / 60f;
            float duration = anim.Duration;

            void SampleFrame()
            {
                for (int i = 0; i < instances; ++i)
                {
                    times[i] += dt * speeds[i];
                    ScalarRuntime.Sample(anim, ctxs[i], (times[i] % duration) / duration, locals[i]);
                }
            }
            void L2MFrame()
            {
                for (int i = 0; i < instances; ++i)
                    ScalarRuntime.LocalToModel(anim, locals[i], models[i]);
            }

            for (int f = 0; f < 20; ++f) { SampleFrame(); L2MFrame(); }
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; ++f) SampleFrame();
            sw.Stop();
            double sampleMs = sw.Elapsed.TotalMilliseconds / frames;
            sw.Restart();
            for (int f = 0; f < frames; ++f) L2MFrame();
            sw.Stop();
            double l2mMs = sw.Elapsed.TotalMilliseconds / frames;

            float check = models[0][12];
            return $"unity_mono_scalar_sample_ms={sampleMs:f3},unity_mono_scalar_l2m_ms={l2mMs:f3},check={check:f4}";
        }
    }
}
