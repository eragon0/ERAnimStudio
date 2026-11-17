using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>What New Animation from Current does to the frames: a crop to a range and a left / right mirror.</summary>
    public static class HkxAnimationOps
    {
        /// <summary>
        /// The frames first to last (the file's own frames) as a new animation. The root motion is re-based on the first frame kept:
        /// moved and turned so the copy starts at the origin facing forward.
        /// </summary>
        public static HkxAnimation Crop(HkxAnimation a, int first, int last)
        {
            if (first < 0 || last >= a.Frames || first >= last)
                throw new ArgumentOutOfRangeException(nameof(first), $"frames {first}-{last}: the animation has 0-{a.Frames - 1}");
            int n = last - first + 1;
            var b = new HkxAnimation(a.Bones.ToArray(), n)
            {
                Duration = (float)((n - 1) / a.Fps),
                SkeletonName = a.SkeletonName,
                BlendHint = a.BlendHint,
            };
            for (int f = 0; f < n; f++)
            {
                for (int k = 0; k < a.Tracks; k++)
                {
                    b.T[f, k] = a.T[first + f, k];
                    b.Q[f, k] = a.Q[first + f, k];
                    b.S[f, k] = a.S[first + f, k];
                }
            }
            if (a.Root != null)
            {
                var r0 = a.Root[first];
                double c = Math.Cos(-r0.W), s = Math.Sin(-r0.W);
                b.Root = new Vector4[n];
                for (int f = 0; f < n; f++)
                {
                    var r = a.Root[first + f];
                    double dx = r.X - r0.X, dy = r.Y - r0.Y, dz = r.Z - r0.Z;
                    // a rotation about up by -yaw: x' = x cos + z sin, z' = -x sin + z cos
                    b.Root[f] = new Vector4((float)(dx * c + dz * s), (float)dy, (float)(-dx * s + dz * c), r.W - r0.W);
                }
            }
            return b;
        }

        /// <summary>Each bone's left / right twin by name (L_ and R_), itself when it has none.</summary>
        public static int[] Twins(HkxSkeleton sk)
        {
            var p = Enumerable.Range(0, sk.Count).ToArray();
            for (int i = 0; i < sk.Count; i++)
            {
                string n = sk.Names[i];
                if (n.StartsWith("L_") && sk.Index.TryGetValue("R_" + n.Substring(2), out int r))
                    p[i] = r;
                else if (n.StartsWith("R_") && sk.Index.TryGetValue("L_" + n.Substring(2), out int l))
                    p[i] = l;
            }
            return p;
        }

        // the reflection across the plane x = 0 (the skeleton's left / right axis: every L_ bone sits at +x, its R_ twin at -x)
        static Vector3 ReflectT(Vector3 t) => new Vector3(-t.X, t.Y, t.Z);

        // the rotation S R S, S the reflection: (-S v, w)
        static Quaternion ReflectQ(Quaternion q) => new Quaternion(q.X, -q.Y, -q.Z, q.W);

        /// <summary>How far the mirrored reference pose lands from the reference pose (metres): a symmetric skeleton gives ~0.</summary>
        public static double ReferenceAsymmetry(HkxSkeleton sk)
        {
            var p = Twins(sk);
            var (rt, _) = sk.WorldReference();
            double max = 0;
            for (int b = 0; b < sk.Count; b++)
                max = Math.Max(max, (ReflectT(rt[p[b]]) - rt[b]).Length());
            return max;
        }

        /// <summary>
        /// Left and right swapped: in armature space, across x = 0, each bone takes its twin's pose (pairs by name, L_ / R_), with a
        /// fixed correction per bone so the mirrored reference pose is the reference pose. Root motion: x and the turn change sign.
        /// Bones the source doesn't animate stay unanimated, unless their twin is animated.
        /// </summary>
        public static HkxAnimation Mirror(HkxSkeleton sk, HkxAnimation a)
        {
            foreach (var b in a.Bones)
                if (b < 0 || b >= sk.Count)
                    throw new InvalidOperationException($"a track animates bone {b}, past the skeleton's {sk.Count} bones");
            var p = Twins(sk);
            var (rt, rq) = sk.WorldReference();
            var corr = new Quaternion[sk.Count];
            for (int b = 0; b < sk.Count; b++)
                corr[b] = Quaternion.Conjugate(ReflectQ(rq[p[b]])) * rq[b];
            var (lt, lq) = sk.FullPose(a);
            var (wt, wq) = sk.ToWorld(lt, lq);
            int frames = a.Frames;
            var mt = new Vector3[frames, sk.Count];
            var mq = new Quaternion[frames, sk.Count];
            for (int f = 0; f < frames; f++)
            {
                for (int b = 0; b < sk.Count; b++)
                {
                    mt[f, b] = ReflectT(wt[f, p[b]]);
                    mq[f, b] = Quaternion.Normalize(ReflectQ(wq[f, p[b]]) * corr[b]);
                }
            }
            var (nt, nq) = sk.ToLocal(mt, mq);
            var tracked = new HashSet<int>(a.Bones.Select(b => (int)b));
            var bones = tracked.Union(tracked.Select(b => p[b])).OrderBy(b => b).ToArray();
            var scale = new Vector3[frames, sk.Count];
            for (int f = 0; f < frames; f++)
            {
                for (int b = 0; b < sk.Count; b++)
                    scale[f, b] = Vector3.One;
                for (int k = 0; k < a.Tracks; k++)
                    scale[f, a.Bones[k]] = a.S[f, k];
            }
            var m = new HkxAnimation(bones.Select(b => (short)b).ToArray(), frames)
            {
                Duration = a.Duration,
                SkeletonName = a.SkeletonName,
                BlendHint = a.BlendHint,
            };
            for (int f = 0; f < frames; f++)
            {
                for (int k = 0; k < bones.Length; k++)
                {
                    int b = bones[k];
                    m.T[f, k] = nt[f, b];
                    m.Q[f, k] = nq[f, b];
                    m.S[f, k] = scale[f, p[b]];
                }
            }
            if (a.Root != null)
                m.Root = a.Root.Select(r => new Vector4(-r.X, r.Y, r.Z, -r.W)).ToArray();
            m.MakeQuaternionsContinuous();
            return m;
        }
    }
}
