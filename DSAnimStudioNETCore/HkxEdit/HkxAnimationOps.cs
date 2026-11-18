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

        // a turn about up and a move: the kind of transform root motion samples describe (x, y, z, turn)
        readonly struct Planar
        {
            public readonly Vector3 T;
            public readonly double Yaw;
            public Planar(Vector3 t, double yaw) { T = t; Yaw = yaw; }
            public static Planar Identity => new Planar(Vector3.Zero, 0);
            public Quaternion Q => Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Yaw);
            public Planar Then(Planar b) => new Planar(T + Vector3.Transform(b.T, Q), Yaw + b.Yaw);   // this, then b in this one's frame
            public Planar Inverse() => new Planar(-Vector3.Transform(T, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)-Yaw)), -Yaw);
        }

        // the turn about up a rotation adds to a reference rotation (its twist about Y, measured in world space)
        static double YawFrom(Quaternion q, Quaternion reference)
        {
            var d = q * Quaternion.Conjugate(reference);
            double yaw = 2 * Math.Atan2(d.Y, d.W);
            // into -180..180 degrees (q and -q are the same rotation)
            while (yaw > Math.PI) yaw -= 2 * Math.PI;
            while (yaw <= -Math.PI) yaw += 2 * Math.PI;
            return yaw;
        }

        /// <summary>
        /// The body started on the character: the root bone's (Master's) sideways offset from the character at the first frame is moved
        /// into the root motion, and handed back to the root bone over the animation (smoothly, back to the source's own by the last
        /// frame). The body moves exactly as before, only placed so it starts on the character; the root motion absorbs the difference,
        /// so its travel changes by that offset. The root bone's turn is left as it is: in attacks it is the body twisting through the
        /// swing (often 30-50 degrees at a frame mid-swing), and moving it into the root motion would turn the whole attack's direction
        /// away from where the character faces. Needs root motion and the root bone among the tracks; returns the offset moved (metres,
        /// 0 when there was nothing to do) and the root bone's turn at the first frame (degrees, for the report, not moved).
        /// </summary>
        public static HkxAnimation EaseRootOntoCharacter(HkxSkeleton sk, HkxAnimation a, out double offset, out double turnDegrees)
        {
            offset = 0;
            turnDegrees = 0;
            int root = Array.FindIndex(sk.Parents, p => p < 0);
            int k = Array.IndexOf(a.Bones, (short)root);
            if (a.Root == null || root < 0 || k < 0 || a.Frames < 2)
                return a;
            var m0 = a.T[0, k];
            double yaw0 = YawFrom(a.Q[0, k], sk.Q[root]);
            offset = new Vector2(m0.X, m0.Z).Length();
            turnDegrees = Math.Abs(yaw0 * 180 / Math.PI);
            if (offset < 1e-4)
            {
                offset = 0;
                return a;
            }
            // C(t) carries the correction: C0 at the first frame (the sideways offset), easing to nothing by the last.
            // Root motion' = C0^-1 . RM(t) . C(t), root bone' = C(t)^-1 . root bone(t): at the first frame the root motion stays at the
            // origin and the root bone loses its offset; the body's world motion becomes C0^-1 . source, the source's moved as a whole
            yaw0 = 0;   // the turn stays with the root bone (see above)
            var c0 = new Planar(new Vector3(m0.X, 0, m0.Z), 0);
            var c0Inv = c0.Inverse();
            var b = new HkxAnimation(a.Bones.ToArray(), a.Frames)
            {
                Duration = a.Duration, SkeletonName = a.SkeletonName, BlendHint = a.BlendHint, Root = new Vector4[a.Frames],
            };
            Array.Copy(a.T, b.T, a.T.Length);
            Array.Copy(a.Q, b.Q, a.Q.Length);
            Array.Copy(a.S, b.S, a.S.Length);
            for (int f = 0; f < a.Frames; f++)
            {
                double s = (double)f / (a.Frames - 1);
                double keep = 1 - s * s * (3 - 2 * s);   // smoothstep: 1 at the first frame, 0 at the last
                var c = new Planar(new Vector3(m0.X, 0, m0.Z) * (float)keep, 0).Then(new Planar(Vector3.Zero, yaw0 * keep));
                var r = a.Root[f];
                var rm = c0Inv.Then(new Planar(new Vector3(r.X, r.Y, r.Z), r.W)).Then(c);
                b.Root[f] = new Vector4(rm.T, (float)rm.Yaw);
                var cInv = c.Inverse();
                b.T[f, k] = cInv.T + Vector3.Transform(a.T[f, k], cInv.Q);
                b.Q[f, k] = Quaternion.Normalize(cInv.Q * a.Q[f, k]);
            }
            b.MakeQuaternionsContinuous();
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
