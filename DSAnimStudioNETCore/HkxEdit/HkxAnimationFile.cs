using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Havoc.IO.Tagfile.Binary;
using Havoc.Objects;
using Havoc.Reflection;
using SoulsAssetPipeline.Animation;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>
    /// An Elden Ring animation hkx (a Havok 2018 tagfile) read through Havoc: its object tree, kept as the template the writer
    /// builds the new file from, and its frames decoded into an <see cref="HkxAnimation"/>.
    /// Reads spline-compressed and interleaved (uncompressed) animations, files whose types are in their binder's compendium,
    /// raw 3ds Max exports (only their animation container is used) and any reference frame with per-frame samples
    /// (hkaDefaultAnimatedReferenceFrame, hkaDirectionalReferenceFrame).
    /// </summary>
    public sealed class HkxAnimationFile
    {
        public IHkObject Root;
        public HkClass Container;
        public HkClass Animation;
        public HkClass Binding;
        public HkClass ReferenceFrame;
        public HkxAnimation Decoded;
        // a spline-compressed file's own curves, kept for sampling between its frames (SampleAt); null for an interleaved one
        List<SplineCompressedAnimation.TransformTrack[]> splineBlocks;
        int splinePerBlock;

        public static HkxAnimationFile Read(byte[] hkx, byte[] compendium = null)
        {
            IHkObject root;
            try
            {
                root = HkBinaryTagfileReader.Read(hkx, compendium);
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"the hkx can't be read as an Elden Ring tagfile ({e.Message})", e);
            }
            var f = new HkxAnimationFile { Root = root };
            if (root?.Type?.Name != "hkRootLevelContainer")
                throw new InvalidDataException($"the hkx's root is {root?.Type?.Name ?? "nothing"}, not hkRootLevelContainer");
            foreach (var variant in Items(Field(Class(root), "namedVariants")))
            {
                var c = Class(Field(Class(variant), "variant")?.Value as IHkObject);
                if (c?.Type.Name == "hkaAnimationContainer")
                {
                    f.Container = c;
                    break;
                }
            }
            if (f.Container == null)
                throw new InvalidDataException("the hkx has no animation container");
            var anims = Items(Field(f.Container, "animations")).Select(p => Class(p.Value as IHkObject)).Where(a => a != null).ToList();
            var bindings = Items(Field(f.Container, "bindings")).Select(p => Class(p.Value as IHkObject)).Where(a => a != null).ToList();
            if (anims.Count != 1 || bindings.Count != 1)
                throw new InvalidDataException($"the hkx has {anims.Count} animations and {bindings.Count} bindings, one of each expected");
            f.Animation = anims[0];
            f.Binding = bindings[0];
            f.ReferenceFrame = Class(Field(f.Animation, "extractedMotion")?.Value as IHkObject);
            f.Decoded = Decode(f);
            return f;
        }

        // ---- the tree

        public static HkClass Class(IHkObject o) => o as HkClass;

        public static IHkObject Field(HkClass c, string name)
        {
            if (c == null)
                return null;
            foreach (var kv in c.Value)
                if (kv.Key.Name == name)
                    return kv.Value;
            return null;
        }

        public static IReadOnlyList<IHkObject> Items(IHkObject array) =>
            (array as HkArray)?.Value ?? (IReadOnlyList<IHkObject>)Array.Empty<IHkObject>();

        public static float Float(IHkObject o) => o switch
        {
            HkSingle s => s.Value,
            HkDouble d => (float)d.Value,
            HkHalf h => (float)h.Value,
            null => 0f,
            _ => throw new InvalidDataException($"{o.Type?.Name} is not a float"),
        };

        public static long Int(IHkObject o) => o == null ? 0 : Convert.ToInt64(o.Value);

        /// <summary>A hkVector4 / hkQuaternion: a fixed array of four floats, or a class around one.</summary>
        public static Vector4 Vec4(IHkObject o)
        {
            while (o is HkClass c && c.Value.Count == 1)
                o = c.Value.First().Value;
            var items = Items(o);
            if (items.Count < 4)
                throw new InvalidDataException($"{o?.Type?.Name} is not a vector of four floats");
            return new Vector4(Float(items[0]), Float(items[1]), Float(items[2]), Float(items[3]));
        }

        // ---- decoding

        static HkxAnimation Decode(HkxAnimationFile f)
        {
            var bones = Items(Field(f.Binding, "transformTrackToBoneIndices")).Select(x => (short)Int(x)).ToArray();
            int tracks = (int)Int(Field(f.Animation, "numberOfTransformTracks"));
            if (tracks != bones.Length)
                throw new InvalidDataException($"the animation has {tracks} tracks and its binding {bones.Length}");
            float duration = Float(Field(f.Animation, "duration"));
            HkxAnimation a;
            string type = f.Animation.Type.Name;
            if (type == "hkaSplineCompressedAnimation")
                a = DecodeSpline(f, bones, duration);
            else if (type == "hkaInterleavedUncompressedAnimation")
                a = DecodeInterleaved(f.Animation, bones, duration);
            else
                throw new InvalidDataException($"{type} animations can't be decoded (spline-compressed and interleaved only)");
            a.SkeletonName = (Field(f.Binding, "originalSkeletonName") as HkString)?.Value;
            a.BlendHint = (int)Int(Field(f.Binding, "blendHint"));
            if (f.ReferenceFrame != null)
            {
                var samples = Items(Field(f.ReferenceFrame, "referenceFrameSamples")).Select(Vec4).ToArray();
                if (samples.Length > 0)
                {
                    if (samples.Length != a.Frames)
                        throw new InvalidDataException($"the root motion has {samples.Length} samples for {a.Frames} frames");
                    a.Root = samples;
                }
            }
            a.MakeQuaternionsContinuous();
            return a;
        }

        static HkxAnimation DecodeInterleaved(HkClass anim, short[] bones, float duration)
        {
            var tr = Items(Field(anim, "transforms"));
            int tracks = bones.Length;
            if (tracks == 0 || tr.Count % tracks != 0)
                throw new InvalidDataException($"{tr.Count} transforms for {tracks} tracks");
            int frames = tr.Count / tracks;
            var a = new HkxAnimation(bones, frames) { Duration = duration };
            for (int f = 0; f < frames; f++)
            {
                for (int k = 0; k < tracks; k++)
                {
                    var c = (HkClass)tr[f * tracks + k];
                    var t = Vec4(Field(c, "translation"));
                    var q = Vec4(Field(c, "rotation"));
                    var s = Vec4(Field(c, "scale"));
                    a.T[f, k] = new Vector3(t.X, t.Y, t.Z);
                    a.Q[f, k] = new Quaternion(q.X, q.Y, q.Z, q.W);
                    a.S[f, k] = new Vector3(s.X, s.Y, s.Z);
                }
            }
            return a;
        }

        static HkxAnimation DecodeSpline(HkxAnimationFile file, short[] bones, float duration)
        {
            var anim = file.Animation;
            int frames = (int)Int(Field(anim, "numFrames"));
            int blocks = (int)Int(Field(anim, "numBlocks"));
            int perBlock = (int)Int(Field(anim, "maxFramesPerBlock"));
            byte[] data = Items(Field(anim, "data")).Select(x => (byte)Int(x)).ToArray();
            if (frames < 1 || blocks < 1 || perBlock < 2)
                throw new InvalidDataException($"a spline animation with {frames} frames, {blocks} blocks of {perBlock}");
            var decoded = SplineCompressedAnimation.ReadSplineCompressedAnimByteBlock(false, data, bones.Length, blocks);
            file.splineBlocks = decoded;
            file.splinePerBlock = perBlock;
            var a = new HkxAnimation(bones, frames) { Duration = duration };
            for (int f = 0; f < frames; f++)
            {
                // blocks overlap by one frame: block b holds frames b * (perBlock - 1) to (b + 1) * (perBlock - 1)
                int block = Math.Min(f / (perBlock - 1), blocks - 1);
                float local = f - block * (perBlock - 1);
                for (int k = 0; k < bones.Length; k++)
                {
                    var (t, q, s) = Sample(decoded[block][k], local, frames);
                    a.T[f, k] = t;
                    a.Q[f, k] = q;
                    a.S[f, k] = s;
                }
            }
            return a;
        }

        /// <summary>
        /// The animation at the given frames of its own, as the frames of a new animation playing at fps. A frame between two of the
        /// file's is taken from a spline-compressed file's own curves there (what the game plays at that time), or blended between an
        /// interleaved file's two frames; the root motion is blended between its two samples. The root motion is not re-based
        /// (<see cref="HkxAnimationOps.Crop"/> does that).
        /// </summary>
        public HkxAnimation SampleAt(double[] at, double fps)
        {
            var a = Decoded;
            int n = at.Length;
            var b = new HkxAnimation(a.Bones.ToArray(), n)
            {
                Duration = (float)((n - 1) / fps),
                SkeletonName = a.SkeletonName,
                BlendHint = a.BlendHint,
            };
            if (a.Root != null)
                b.Root = new Vector4[n];
            for (int i = 0; i < n; i++)
            {
                double x = Math.Clamp(at[i], 0, a.Frames - 1);
                int f0 = Math.Min((int)Math.Floor(x), a.Frames - 1), f1 = Math.Min(f0 + 1, a.Frames - 1);
                float u = (float)(x - f0);
                if (splineBlocks != null)
                {
                    int block = Math.Min((int)(x / (splinePerBlock - 1)), splineBlocks.Count - 1);
                    float local = (float)(x - block * (splinePerBlock - 1));
                    for (int k = 0; k < a.Tracks; k++)
                    {
                        var (t, q, s) = Sample(splineBlocks[block][k], local, a.Frames);
                        b.T[i, k] = t;
                        b.Q[i, k] = q;
                        b.S[i, k] = s;
                    }
                }
                else
                {
                    for (int k = 0; k < a.Tracks; k++)
                    {
                        b.T[i, k] = Vector3.Lerp(a.T[f0, k], a.T[f1, k], u);
                        b.Q[i, k] = Quaternion.Normalize(Quaternion.Slerp(a.Q[f0, k], a.Q[f1, k], u));
                        b.S[i, k] = Vector3.Lerp(a.S[f0, k], a.S[f1, k], u);
                    }
                }
                if (a.Root != null)
                    b.Root[i] = Vector4.Lerp(a.Root[f0], a.Root[f1], u);
            }
            b.MakeQuaternionsContinuous();
            return b;
        }

        // one track at one frame of its block, as Havok defines the channels: a spline, a static value, or the identity
        static (Vector3, Quaternion, Vector3) Sample(SplineCompressedAnimation.TransformTrack track, float frame, int frameCount)
        {
            Vector3 t, s;
            Quaternion q;
            if (track.SplinePosition != null)
                t = new Vector3(track.SplinePosition.GetValueX(frame, frameCount) ?? 0,
                                track.SplinePosition.GetValueY(frame, frameCount) ?? 0,
                                track.SplinePosition.GetValueZ(frame, frameCount) ?? 0);
            else
                t = new Vector3(
                    track.Mask.PositionTypes.Contains(SplineCompressedAnimation.FlagOffset.StaticX) ? track.StaticPosition.X : 0,
                    track.Mask.PositionTypes.Contains(SplineCompressedAnimation.FlagOffset.StaticY) ? track.StaticPosition.Y : 0,
                    track.Mask.PositionTypes.Contains(SplineCompressedAnimation.FlagOffset.StaticZ) ? track.StaticPosition.Z : 0);
            if (track.SplineRotation != null)
                q = track.SplineRotation.GetValue(frame, frameCount);
            else if (track.HasStaticRotation)
                q = track.StaticRotation;
            else
                q = Quaternion.Identity;
            if (track.SplineScale != null)
                s = new Vector3(track.SplineScale.GetValueX(frame, frameCount) ?? 1,
                                track.SplineScale.GetValueY(frame, frameCount) ?? 1,
                                track.SplineScale.GetValueZ(frame, frameCount) ?? 1);
            else
                s = new Vector3(
                    track.Mask.ScaleTypes.Contains(SplineCompressedAnimation.FlagOffset.StaticX) ? track.StaticScale.X : 1,
                    track.Mask.ScaleTypes.Contains(SplineCompressedAnimation.FlagOffset.StaticY) ? track.StaticScale.Y : 1,
                    track.Mask.ScaleTypes.Contains(SplineCompressedAnimation.FlagOffset.StaticZ) ? track.StaticScale.Z : 1);
            return (t, Quaternion.Normalize(q), s);
        }
    }
}
