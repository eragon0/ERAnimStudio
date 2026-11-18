// ERAnimStudio: the left-hand clearance measure, per frame (the pack's left_clear.py measure, ported).
//   - A shield (a plate): per shell of the armor (one piece's vertices on one strongest bone and body part), inside the plate's
//     outline, how far the shell crosses the plate: the smaller of how far its vertices stand in front of the face and how far they
//     reach behind it (a shell all on one side: 0). Reported per body part, the deepest shell. Every part counts, the shield's own
//     arm too.
//   - Any other item (its points): how deep its points go inside the armor. A bone's height field says a point may be inside, and
//     the nearest armor vertex's normal has to face away from it (its distance is the depth); in the head's height field the field
//     alone decides (a helmet's dome, whose liner faces in). The own hand and forearm are left out: they hold it.
using System;
using System.Collections.Generic;
using System.Linq;

namespace DSAnimStudio.LeftClear
{
    /// <summary>A 3D tree for the nearest of a set of points.</summary>
    public sealed class LcKdTree
    {
        readonly D3[] pts;
        readonly int[] idx;     // the points' order in the tree: node k covers idx[lo..hi), split at the middle
        readonly byte[] axis;

        public LcKdTree(D3[] points)
        {
            pts = points;
            idx = Enumerable.Range(0, points.Length).ToArray();
            axis = new byte[points.Length];
            Build(0, points.Length);
        }

        void Build(int lo, int hi)
        {
            if (hi - lo <= 1)
                return;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = lo; i < hi; i++)
            {
                var p = pts[idx[i]];
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            double sx = maxX - minX, sy = maxY - minY, sz = maxZ - minZ;
            byte ax = (byte)(sx >= sy && sx >= sz ? 0 : sy >= sz ? 1 : 2);
            int mid = (lo + hi) / 2;
            Array.Sort(idx, lo, hi - lo, Comparer<int>.Create((a, b) => pts[a][ax].CompareTo(pts[b][ax])));
            axis[mid] = ax;
            Build(lo, mid);
            Build(mid + 1, hi);
        }

        public (int index, double dist) Nearest(D3 q)
        {
            int best = -1;
            double bestD2 = double.MaxValue;
            Search(0, pts.Length, q, ref best, ref bestD2);
            return (best, Math.Sqrt(bestD2));
        }

        void Search(int lo, int hi, D3 q, ref int best, ref double bestD2)
        {
            if (hi <= lo)
                return;
            int mid = (lo + hi) / 2;
            var p = pts[idx[mid]];
            double d2 = (p - q).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = idx[mid];
            }
            if (hi - lo == 1)
                return;
            int ax = axis[mid];
            double diff = q[ax] - p[ax];
            if (diff < 0)
            {
                Search(lo, mid, q, ref best, ref bestD2);
                if (diff * diff < bestD2)
                    Search(mid + 1, hi, q, ref best, ref bestD2);
            }
            else
            {
                Search(mid + 1, hi, q, ref best, ref bestD2);
                if (diff * diff < bestD2)
                    Search(lo, mid, q, ref best, ref bestD2);
            }
        }
    }

    /// <summary>A point of a crossing at one frame, for drawing it: where, how deep (m), which body part.</summary>
    public struct LcHit
    {
        public D3 P;
        public double Depth;
        public string Part;
        public int Index;       // the item's point, or the armor's vertex
    }

    public sealed class LcMeasure
    {
        public const double PlateDepthLimit = 0.6;   // m: armor further from the plate's face than this isn't looked at

        public readonly LcBody Body;

        public LcMeasure(LcBody body)
        {
            Body = body;
        }

        /// <summary>One frame's posed armor, shared by the items.</summary>
        public sealed class Frame
        {
            public int F;
            public D3[] V;                  // blend-skinned vertices
            public D3[] P, N;               // rigid vertices of the static bones, their normals
            public LcKdTree Tree;
            public D3[] PM, NM;             // and of the moving bones (the left arm)
            public LcKdTree TreeM;
        }

        /// <summary>trees: the nearest-vertex search through trees (every frame of an animation), else by going through the vertices
        /// (one frame, few points inside)</summary>
        public Frame Prepare(D3[,] wt, DQ[,] wq, int f, bool plates, bool points, bool trees = true)
        {
            var fr = new Frame { F = f };
            if (plates)
                fr.V = Body.Posed(wt, wq, f);
            if (points)
            {
                (fr.P, fr.N) = Rigid(Body.StaticBones, wt, wq, f);
                if (trees)
                    fr.Tree = new LcKdTree(fr.P);
                if (Body.MovingBones.Length > 0)
                {
                    (fr.PM, fr.NM) = Rigid(Body.MovingBones, wt, wq, f);
                    if (trees)
                        fr.TreeM = new LcKdTree(fr.PM);
                }
            }
            return fr;
        }

        static (int index, double dist) Nearest(LcKdTree tree, D3[] pts, D3 q)
        {
            if (tree != null)
                return tree.Nearest(q);
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < pts.Length; i++)
            {
                double d = (pts[i] - q).LengthSquared();
                if (d < bd)
                {
                    bd = d;
                    best = i;
                }
            }
            return (best, Math.Sqrt(bd));
        }

        (D3[] p, D3[] n) Rigid(int[] bones, D3[,] wt, DQ[,] wq, int f)
        {
            int count = bones.Sum(b => Body.Rigid[b].v.Length);
            var p = new D3[count];
            var n = new D3[count];
            int k = 0;
            foreach (int b in bones)
            {
                var (v, nn) = Body.Rigid[b];
                var q = wq[f, b];
                var t = wt[f, b];
                for (int i = 0; i < v.Length; i++, k++)
                {
                    p[k] = t + q.Rotate(v[i]);
                    n[k] = q.Rotate(nn[i]);
                }
            }
            return (p, n);
        }

        /// <summary>The item's crossings at one frame: {body part: depth in m}; hits (optional) gets the points to draw.</summary>
        public Dictionary<string, double> Depths(Frame fr, LcItem item, D3[,] wt, DQ[,] wq, List<LcHit> hits = null) =>
            item.Kind == LcItemKind.Plate ? Plate(fr, item, wt, wq, hits) : Points(fr, item, wt, wq, hits);

        Dictionary<string, double> Plate(Frame fr, LcItem it, D3[,] wt, DQ[,] wq, List<LcHit> hits)
        {
            var (o, r) = it.World(wt, wq, fr.F);
            var mid = o + r * it.Half;
            double rad2 = it.Radius * it.Radius;
            int groups = Body.GrpLab.Length;
            var front = new double[groups];
            var back = new double[groups];
            var lookedAt = new List<(int v, double dz)>();
            void Look(int v)
            {
                var q = r.TransposeTimes(fr.V[v] - o);
                int i = (int)Math.Floor((q.X - it.X0) / LcItem.Bin), j = (int)Math.Floor((q.Y - it.Y0) / LcItem.Bin);
                if (i < 0 || i >= it.Nx || j < 0 || j >= it.Ny || Math.Abs(q.Z) >= PlateDepthLimit || !it.Mask[i, j])
                    return;
                double dz = it.Zf[i, j] - q.Z;          // > 0: in front of the face
                int g = Body.Grp[v];
                if (dz > front[g])
                    front[g] = dz;
                if (-dz > back[g])
                    back[g] = -dz;
                if (hits != null)
                    lookedAt.Add((v, dz));
            }
            foreach (int v in Body.StaticV)              // the static armor near the plate (the measure's ball query)
                if ((fr.V[v] - mid).LengthSquared() <= rad2)
                    Look(v);
            foreach (int v in Body.MovingV)
                Look(v);
            var res = new Dictionary<string, double>();
            for (int g = 0; g < groups; g++)
            {
                double cross = Math.Min(front[g], back[g]);
                if (cross <= 0)
                    continue;
                string p = Body.Labels[Body.GrpLab[g]];
                res[p] = Math.Max(res.TryGetValue(p, out double d) ? d : 0, cross);
            }
            if (hits != null)
                foreach (var (v, dz) in lookedAt)
                {
                    int g = Body.Grp[v];
                    if (dz > 0 && Math.Min(front[g], back[g]) > 0)
                        hits.Add(new LcHit { P = fr.V[v], Depth = dz, Part = Body.Labels[Body.GrpLab[g]], Index = v });
                }
            return res;
        }

        Dictionary<string, double> Points(Frame fr, LcItem it, D3[,] wt, DQ[,] wq, List<LcHit> hits)
        {
            var (o, r) = it.World(wt, wq, fr.F);
            int n = it.Points.Length;
            var pts = new D3[n];
            for (int i = 0; i < n; i++)
                pts[i] = o + r * it.Points[i];
            var may = new double[n];
            var who = Enumerable.Repeat(-1, n).ToArray();
            for (int k = 0; k < Body.SolidBones.Count; k++)
            {
                int b = Body.SolidBones[k];
                var c = wt[fr.F, b] + wq[fr.F, b].Rotate(Body.SolidCentre[k]);
                double reach = Body.SolidReach[k];
                var inv = wq[fr.F, b].Conj();
                var t = wt[fr.F, b];
                for (int i = 0; i < n; i++)
                {
                    if ((pts[i] - c).Length() >= reach)
                        continue;
                    double dep = Body.Solids[k].Depth(inv.Rotate(pts[i] - t));
                    if (dep > may[i])
                    {
                        may[i] = dep;
                        who[i] = b;
                    }
                }
            }
            var res = new Dictionary<string, double>();
            for (int i = 0; i < n; i++)
            {
                if (may[i] <= 0)
                    continue;
                var (k, dist) = Nearest(fr.Tree, fr.P, pts[i]);
                var near = fr.P[k];
                var nn = fr.N[k];
                if (fr.PM != null && fr.PM.Length > 0)
                {
                    var (k2, d2) = Nearest(fr.TreeM, fr.PM, pts[i]);
                    if (d2 < dist)
                    {
                        dist = d2;
                        near = fr.PM[k2];
                        nn = fr.NM[k2];
                    }
                }
                bool agrees = D3.Dot(pts[i] - near, nn) < 0;
                double dep = Body.Hollow.Contains(who[i]) ? may[i] : agrees ? dist : 0;
                if (dep <= 0)
                    continue;
                string p = Body.Part[who[i]];
                res[p] = Math.Max(res.TryGetValue(p, out double d) ? d : 0, dep);
                hits?.Add(new LcHit { P = pts[i], Depth = dep, Part = p, Index = i });
            }
            return res;
        }

        /// <summary>Every frame of a posed animation: [frame][item] {body part: depth in m}. progress gets the share done.</summary>
        public Dictionary<string, double>[][] Run(D3[,] wt, DQ[,] wq, IList<LcItem> items, Action<double> progress = null,
                                                  Func<bool> cancelled = null)
        {
            int frames = wt.GetLength(0);
            bool plates = items.Any(i => i.Kind == LcItemKind.Plate), points = items.Any(i => i.Kind == LcItemKind.Points);
            var rows = new Dictionary<string, double>[frames][];
            for (int f = 0; f < frames; f++)
            {
                if (cancelled?.Invoke() == true)
                    return null;
                var fr = Prepare(wt, wq, f, plates, points);
                rows[f] = items.Select(it => Depths(fr, it, wt, wq)).ToArray();
                progress?.Invoke((f + 1.0) / frames);
            }
            return rows;
        }
    }
}
