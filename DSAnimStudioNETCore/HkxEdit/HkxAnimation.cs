using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>
    /// An animation as plain per-frame arrays: what the crop and the mirror work on.
    /// Track k animates bone Bones[k]; T / Q / S are [frame, track] local transforms (Havok's x, y, z, w quaternions).
    /// Root is the reference frame's samples (x, y, z, rotation about up in radians), one per frame, or null for no root motion.
    /// </summary>
    public sealed class HkxAnimation
    {
        public short[] Bones;
        public Vector3[,] T;
        public Quaternion[,] Q;
        public Vector3[,] S;
        public float Duration;
        public Vector4[] Root;
        public string SkeletonName;
        public int BlendHint;

        public int Frames => T.GetLength(0);
        public int Tracks => T.GetLength(1);
        public double Fps => Duration > 0 && Frames > 1 ? (Frames - 1) / Duration : 30.0;

        public HkxAnimation(short[] bones, int frames)
        {
            Bones = bones;
            T = new Vector3[frames, bones.Length];
            Q = new Quaternion[frames, bones.Length];
            S = new Vector3[frames, bones.Length];
        }

        /// <summary>Normalizes the rotations and keeps consecutive frames of a track in the same hemisphere.</summary>
        public void MakeQuaternionsContinuous()
        {
            for (int k = 0; k < Tracks; k++)
            {
                for (int f = 0; f < Frames; f++)
                {
                    var q = Quaternion.Normalize(Q[f, k]);
                    if (f > 0 && Quaternion.Dot(q, Q[f - 1, k]) < 0)
                        q = Quaternion.Negate(q);
                    Q[f, k] = q;
                }
            }
        }
    }

    /// <summary>A skeleton as plain arrays: bone names, parents (-1 = root) and the local reference pose.</summary>
    public sealed class HkxSkeleton
    {
        public string[] Names;
        public int[] Parents;
        public Vector3[] T;
        public Quaternion[] Q;
        public Dictionary<string, int> Index;
        public int[] Order;

        public int Count => Names.Length;

        public HkxSkeleton(string[] names, int[] parents, Vector3[] t, Quaternion[] q)
        {
            Names = names;
            Parents = parents;
            T = t;
            Q = q.Select(Quaternion.Normalize).ToArray();
            Index = new Dictionary<string, int>();
            for (int i = 0; i < names.Length; i++)
                Index.TryAdd(names[i], i);
            // parents before children
            var order = new List<int>();
            var done = new bool[names.Length];
            while (order.Count < names.Length)
            {
                int before = order.Count;
                for (int b = 0; b < names.Length; b++)
                {
                    if (!done[b] && (parents[b] < 0 || done[parents[b]]))
                    {
                        order.Add(b);
                        done[b] = true;
                    }
                }
                if (order.Count == before)
                    throw new InvalidOperationException("the skeleton's parent indices form a loop");
            }
            Order = order.ToArray();
        }

        public static HkxSkeleton FromHavok(SoulsAssetPipeline.Animation.HKX.HKASkeleton sk)
        {
            var bones = sk.Bones.GetArrayData().Elements;
            var parents = sk.ParentIndices.GetArrayData().Elements;
            var tr = sk.Transforms.GetArrayData().Elements;
            int n = bones.Count;
            return new HkxSkeleton(
                bones.Select(b => b.Name.GetString()).ToArray(),
                Enumerable.Range(0, n).Select(i => (int)parents[i].data).ToArray(),
                Enumerable.Range(0, n).Select(i => new Vector3(tr[i].Position.Vector.X, tr[i].Position.Vector.Y, tr[i].Position.Vector.Z)).ToArray(),
                Enumerable.Range(0, n).Select(i => new Quaternion(tr[i].Rotation.Vector.X, tr[i].Rotation.Vector.Y, tr[i].Rotation.Vector.Z, tr[i].Rotation.Vector.W)).ToArray());
        }

        /// <summary>Local pose of every bone on each frame: the animation's track where there is one, else the reference pose.</summary>
        public (Vector3[,] t, Quaternion[,] q) FullPose(HkxAnimation a)
        {
            int frames = a.Frames;
            var lt = new Vector3[frames, Count];
            var lq = new Quaternion[frames, Count];
            for (int f = 0; f < frames; f++)
            {
                for (int b = 0; b < Count; b++)
                {
                    lt[f, b] = T[b];
                    lq[f, b] = Q[b];
                }
                for (int k = 0; k < a.Tracks; k++)
                {
                    int b = a.Bones[k];
                    if (b >= 0 && b < Count)
                    {
                        lt[f, b] = a.T[f, k];
                        lq[f, b] = a.Q[f, k];
                    }
                }
            }
            return (lt, lq);
        }

        /// <summary>Local -> armature space (scale taken as 1, as on FromSoftware's character skeletons).</summary>
        public (Vector3[,] t, Quaternion[,] q) ToWorld(Vector3[,] lt, Quaternion[,] lq)
        {
            int frames = lt.GetLength(0);
            var wt = new Vector3[frames, Count];
            var wq = new Quaternion[frames, Count];
            for (int f = 0; f < frames; f++)
            {
                foreach (int b in Order)
                {
                    int p = Parents[b];
                    if (p < 0)
                    {
                        wt[f, b] = lt[f, b];
                        wq[f, b] = lq[f, b];
                    }
                    else
                    {
                        wq[f, b] = Quaternion.Normalize(wq[f, p] * lq[f, b]);
                        wt[f, b] = wt[f, p] + Vector3.Transform(lt[f, b], wq[f, p]);
                    }
                }
            }
            return (wt, wq);
        }

        public (Vector3[,] t, Quaternion[,] q) ToLocal(Vector3[,] wt, Quaternion[,] wq)
        {
            int frames = wt.GetLength(0);
            var lt = new Vector3[frames, Count];
            var lq = new Quaternion[frames, Count];
            for (int f = 0; f < frames; f++)
            {
                foreach (int b in Order)
                {
                    int p = Parents[b];
                    if (p < 0)
                    {
                        lt[f, b] = wt[f, b];
                        lq[f, b] = wq[f, b];
                    }
                    else
                    {
                        var inv = Quaternion.Conjugate(wq[f, p]);
                        lq[f, b] = Quaternion.Normalize(inv * wq[f, b]);
                        lt[f, b] = Vector3.Transform(wt[f, b] - wt[f, p], inv);
                    }
                }
            }
            return (lt, lq);
        }

        /// <summary>The reference pose in armature space.</summary>
        public (Vector3[] t, Quaternion[] q) WorldReference()
        {
            var lt = new Vector3[1, Count];
            var lq = new Quaternion[1, Count];
            for (int b = 0; b < Count; b++)
            {
                lt[0, b] = T[b];
                lq[0, b] = Q[b];
            }
            var (wt, wq) = ToWorld(lt, lq);
            var rt = new Vector3[Count];
            var rq = new Quaternion[Count];
            for (int b = 0; b < Count; b++)
            {
                rt[b] = wt[0, b];
                rq[b] = wq[0, b];
            }
            return (rt, rq);
        }

        /// <summary>
        /// The largest distance between the same bone in two animations on the same skeleton, in armature space, over the frames both
        /// have (metres).
        /// </summary>
        public double MaxPositionError(HkxAnimation a, HkxAnimation b)
        {
            var wa = ToWorld(FullPose(a).t, FullPose(a).q).t;
            var wb = ToWorld(FullPose(b).t, FullPose(b).q).t;
            int frames = Math.Min(a.Frames, b.Frames);
            double max = 0;
            for (int f = 0; f < frames; f++)
                for (int k = 0; k < Count; k++)
                    max = Math.Max(max, (wa[f, k] - wb[f, k]).Length());
            return max;
        }
    }
}
