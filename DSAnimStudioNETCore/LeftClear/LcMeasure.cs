// ERAnimStudio: the left-hand clearance measure, per frame (the pack's left_clear.py measure, ported).
//   - A shield (a plate): per shell of the armor (one piece's vertices on one strongest bone and body part), inside the plate's
//     outline, how far the shell crosses the plate: the smaller of how far its vertices stand in front of the face and how far they
//     reach behind it (a shell all on one side: 0). Reported per body part, the deepest shell. Every part counts, the shield's own
//     arm too.
//   - Any other item (its points): how deep its points go inside the armor. A bone's height field says a point may be inside, and
//     the nearest armor vertex's normal has to face away from it (its distance is the depth); in the head's height field the field
//     alone decides (a helmet's dome, whose liner faces in). The own hand and forearm are left out: they hold it.
//   - The weapon in the right hand (LcBody.RightWeapon, 'right weapon'), mesh against mesh both ways (Crossing): an edge of one
//     going from one side of the other's plate to the other, at a point inside the plate's outline, crosses it. How deep: the least of
//     how far that point lies from the outline's edge (how far the item would slide to clear it) and how far the crossing mesh stands
//     on either side. Two blades crossing seldom leave a point of either inside the other, so the inside test can't see them.
// The armor that doesn't move with the left arm is posed once per frame (Frame); the left arm's (from L_UpperArm down) is posed from
// the pose being measured, so the left arm fix can measure many poses of its arm against one frame. 'fast' (the fix's search): every
// second vertex of the arm rigid on its strongest bone, every second static vertex near a plate, every third point of an item.
using System;
using System.Collections.Generic;
using System.Linq;

namespace DSAnimStudio.LeftClear
{
    /// <summary>A 3D tree for the nearest of a set of points, and the points within a radius.</summary>
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

        /// <summary>The points within r of q (in the tree's order).</summary>
        public void Within(D3 q, double r, List<int> into)
        {
            Range(0, pts.Length, q, r, r * r, into);
        }

        void Range(int lo, int hi, D3 q, double r, double r2, List<int> into)
        {
            if (hi <= lo)
                return;
            int mid = (lo + hi) / 2;
            var p = pts[idx[mid]];
            if ((p - q).LengthSquared() <= r2)
                into.Add(idx[mid]);
            if (hi - lo == 1)
                return;
            int ax = axis[mid];
            double diff = q[ax] - p[ax];
            if (diff - r <= 0)
                Range(lo, mid, q, r, r2, into);
            if (diff + r >= 0)
                Range(mid + 1, hi, q, r, r2, into);
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
        readonly int[] fastMoving;                  // every second vertex of the arm (the search's)

        public LcMeasure(LcBody body)
        {
            Body = body;
            fastMoving = body.MovingV.Where((_, i) => i % 2 == 0).ToArray();
        }

        /// <summary>One frame's armor that doesn't move with the left arm, shared by the items and by every pose of the arm.</summary>
        public sealed class Frame
        {
            public int F;
            public D3[] Wt;                 // the frame's pose (armature space), the one the static armor was posed on
            public DQ[] Wq;
            public D3[] SV;                 // the static skinned vertices (Body.StaticV's order)
            public D3[] RightV;             // the right weapon's mesh, posed (Body.RightWeapon.MeshV's order)
            public LcKdTree STree;          // null: found by going through them
            public D3[] P, N;               // rigid vertices of the static bones, their normals
            public LcKdTree Tree;
        }

        /// <summary>One frame of a posed animation [frame, bone] (the check's every frame, with trees; one frame shown, without).</summary>
        public Frame Prepare(D3[,] wt, DQ[,] wq, int f, bool plates, bool points, bool trees = true)
        {
            int n = wt.GetLength(1);
            var t = new D3[n];
            var q = new DQ[n];
            for (int b = 0; b < n; b++)
            {
                t[b] = wt[f, b];
                q[b] = wq[f, b];
            }
            var fr = PrepareStatic(t, q, plates, points, trees);
            fr.F = f;
            return fr;
        }

        public Frame PrepareStatic(D3[] wt, DQ[] wq, bool plates, bool points, bool trees = true)
        {
            var fr = new Frame { Wt = wt, Wq = wq };
            var rw = Body.RightWeapon;
            if (rw != null)
            {
                var (o, r) = rw.World(wt, wq);
                fr.RightV = rw.MeshV.Select(v => o + r * v).ToArray();
            }
            if (plates)
            {
                fr.SV = new D3[Body.StaticV.Length];
                for (int k = 0; k < Body.StaticV.Length; k++)
                    fr.SV[k] = Skinned(Body.StaticV[k], wt, wq);
                if (trees)
                    fr.STree = new LcKdTree(fr.SV);
            }
            if (points)
            {
                (fr.P, fr.N) = Rigid(Body.StaticBones, wt, wq);
                if (trees)
                    fr.Tree = new LcKdTree(fr.P);
            }
            return fr;
        }

        D3 Skinned(int i, D3[] wt, DQ[] wq)
        {
            var p = D3.Zero;
            for (int k = 0; k < 4; k++)
            {
                double ww = Body.W[i * 4 + k];
                if (ww == 0)
                    continue;
                int b = Body.Bi[i * 4 + k];
                p = p + ww * (wt[b] + wq[b].Rotate(Body.L[i * 4 + k]));
            }
            return p;
        }

        (D3[] p, D3[] n) Rigid(int[] bones, D3[] wt, DQ[] wq)
        {
            int count = bones.Sum(b => Body.Rigid[b].v.Length);
            var p = new D3[count];
            var n = new D3[count];
            int k = 0;
            foreach (int b in bones)
            {
                var (v, nn) = Body.Rigid[b];
                var q = wq[b];
                var t = wt[b];
                for (int i = 0; i < v.Length; i++, k++)
                {
                    p[k] = t + q.Rotate(v[i]);
                    n[k] = q.Rotate(nn[i]);
                }
            }
            return (p, n);
        }

        // the arm's armor, posed on the pose being measured
        sealed class Moving
        {
            public int[] V;                 // vertex ids
            public D3[] VP;
            public D3[] P, N;               // rigid vertices of the moving bones
        }

        Moving PoseMoving(D3[] wt, DQ[] wq, bool plates, bool points, bool fast)
        {
            var m = new Moving();
            if (plates)
            {
                m.V = fast ? fastMoving : Body.MovingV;
                m.VP = new D3[m.V.Length];
                for (int k = 0; k < m.V.Length; k++)
                {
                    int i = m.V[k];
                    if (fast)
                    {
                        int b = Body.Top[i];
                        m.VP[k] = wt[b] + wq[b].Rotate(Body.L[i * 4 + Body.TopK[i]]);
                    }
                    else
                        m.VP[k] = Skinned(i, wt, wq);
                }
            }
            if (points && Body.MovingBones.Length > 0)
                (m.P, m.N) = Rigid(Body.MovingBones, wt, wq);
            return m;
        }

        /// <summary>The item's crossings at frame fr.F of a posed animation: {body part: depth in m}; hits gets the points to draw.</summary>
        public Dictionary<string, double> Depths(Frame fr, LcItem item, D3[,] wt, DQ[,] wq, List<LcHit> hits = null) =>
            DepthsAll(fr, fr.Wt, fr.Wq, new[] { item }, false, hits)[0];

        /// <summary>
        /// The items' crossings on a pose (armature space, every bone) whose bones off the left arm are the frame's: per item {body part:
        /// depth in m}. fast: the search's thinned measure.
        /// </summary>
        public Dictionary<string, double>[] DepthsAll(Frame fr, D3[] wt, DQ[] wq, IList<LcItem> items, bool fast, List<LcHit> hits = null)
        {
            bool plates = items.Any(i => i.Kind == LcItemKind.Plate), points = items.Any(i => i.Kind == LcItemKind.Points);
            var mv = PoseMoving(wt, wq, plates, points, fast);
            var res = new Dictionary<string, double>[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                res[i] = items[i].Kind == LcItemKind.Plate ? Plate(fr, mv, items[i], wt, wq, fast, hits) : Points(fr, mv, items[i], wt, wq, fast, hits);
                if (Body.RightWeapon != null && fr.RightV != null)
                    RightCross(fr, items[i], wt, wq, res[i], hits);
            }
            return res;
        }

        Dictionary<string, double> Plate(Frame fr, Moving mv, LcItem it, D3[] wt, DQ[] wq, bool fast, List<LcHit> hits)
        {
            var (o, r) = it.World(wt, wq);
            var mid = o + r * it.Half;
            int groups = Body.GrpLab.Length;
            var front = new double[groups];
            var back = new double[groups];
            var lookedAt = hits != null ? new List<(D3 p, int v, double dz)>() : null;
            void Look(D3 pos, int v)
            {
                var q = r.TransposeTimes(pos - o);
                int i = (int)Math.Floor((q.X - it.X0) / it.BinSize), j = (int)Math.Floor((q.Y - it.Y0) / it.BinSize);
                if (i < 0 || i >= it.Nx || j < 0 || j >= it.Ny || Math.Abs(q.Z) >= PlateDepthLimit || !it.Mask[i, j])
                    return;
                double dz = it.Zf[i, j] - q.Z;          // > 0: in front of the face
                int g = Body.Grp[v];
                if (dz > front[g])
                    front[g] = dz;
                if (-dz > back[g])
                    back[g] = -dz;
                lookedAt?.Add((pos, v, dz));
            }
            // the static armor near the plate (the measure's ball query), then the arm's
            if (fr.STree != null)
            {
                var near = new List<int>();
                fr.STree.Within(mid, it.Radius, near);
                for (int k = 0; k < near.Count; k += fast ? 2 : 1)
                    Look(fr.SV[near[k]], Body.StaticV[near[k]]);
            }
            else
            {
                double rad2 = it.Radius * it.Radius;
                for (int k = 0; k < fr.SV.Length; k++)
                    if ((fr.SV[k] - mid).LengthSquared() <= rad2)
                        Look(fr.SV[k], Body.StaticV[k]);
            }
            for (int k = 0; k < mv.V.Length; k++)
                Look(mv.VP[k], mv.V[k]);
            var res = new Dictionary<string, double>();
            for (int g = 0; g < groups; g++)
            {
                double cross = Math.Min(front[g], back[g]);
                if (cross <= 0)
                    continue;
                string p = Body.Labels[Body.GrpLab[g]];
                res[p] = Math.Max(res.TryGetValue(p, out double d) ? d : 0, cross);
            }
            if (lookedAt != null)
                foreach (var (pos, v, dz) in lookedAt)
                {
                    int g = Body.Grp[v];
                    if (dz > 0 && Math.Min(front[g], back[g]) > 0)
                        hits.Add(new LcHit { P = pos, Depth = dz, Part = Body.Labels[Body.GrpLab[g]], Index = v });
                }
            return res;
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

        Dictionary<string, double> Points(Frame fr, Moving mv, LcItem it, D3[] wt, DQ[] wq, bool fast, List<LcHit> hits)
        {
            var (o, r) = it.World(wt, wq);
            var src = fast ? it.FastPoints : it.Points;
            int n = src.Length;
            var pts = new D3[n];
            for (int i = 0; i < n; i++)
                pts[i] = o + r * src[i];
            var may = new double[n];
            var who = Enumerable.Repeat(-1, n).ToArray();
            for (int k = 0; k < Body.SolidBones.Count; k++)
            {
                int b = Body.SolidBones[k];
                var c = wt[b] + wq[b].Rotate(Body.SolidCentre[k]);
                double reach = Body.SolidReach[k];
                var inv = wq[b].Conj();
                var t = wt[b];
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
                if (mv.P != null && mv.P.Length > 0)
                {
                    var (k2, d2) = Nearest(null, mv.P, pts[i]);
                    if (d2 < dist)
                    {
                        dist = d2;
                        near = mv.P[k2];
                        nn = mv.N[k2];
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

        /// <summary>
        /// The item against the right weapon: the item's mesh through the right weapon's plate, and the right weapon's mesh through the
        /// item's plate (a shield's own, a weapon's Thin); the deeper counts, as 'right weapon'.
        /// </summary>
        void RightCross(Frame fr, LcItem it, D3[] wt, DQ[] wq, Dictionary<string, double> res, List<LcHit> hits)
        {
            var rw = Body.RightWeapon;
            double dep = 0;
            if (it.MeshV != null)
            {
                var (o, r) = it.World(wt, wq);
                // only the item's vertices near the right weapon's plate are posed
                var (ro, rr) = rw.World(wt, wq);
                var c = r.TransposeTimes(ro + rr * rw.Half - o);
                double rad2 = rw.Radius * rw.Radius;
                var v = new D3[it.MeshV.Length];
                var near = new bool[v.Length];
                for (int i = 0; i < v.Length; i++)
                    if ((it.MeshV[i] - c).LengthSquared() <= rad2)
                    {
                        near[i] = true;
                        v[i] = o + r * it.MeshV[i];
                    }
                dep = Crossing(rw, v, near, it.MeshE, wt, wq, hits);
            }
            var plate = it.Kind == LcItemKind.Plate ? it : it.Thin;
            if (plate != null)
                dep = Math.Max(dep, Crossing(plate, fr.RightV, null, rw.MeshE, wt, wq, hits));
            if (dep > 0)
                res[LcBody.RightWeaponPart] = Math.Max(res.TryGetValue(LcBody.RightWeaponPart, out double was) ? was : 0, dep);
        }

        /// <summary>
        /// A mesh (posed vertices, its edges; use: the vertices to look at, null for all) through a plate: each edge whose ends lie on
        /// either side of the plate's mid surface crosses it where it meets that surface, when that point is inside the outline. Its
        /// depth: the least of the point's distance to the outline's edge and how far the mesh stands in front of and behind the plate
        /// inside the outline. The deepest crossing, 0 for none; hits gets the crossing points.
        /// </summary>
        static double Crossing(LcItem pl, D3[] v, bool[] use, int[] edges, D3[] wt, DQ[] wq, List<LcHit> hits)
        {
            if (v == null || edges == null)
                return 0;
            var (o, r) = pl.World(wt, wq);
            var mid = o + r * pl.Half;
            double rad2 = pl.Radius * pl.Radius, bin = pl.BinSize;
            int n = v.Length;
            var q = new D3[n];
            var d = new double[n];
            var ok = new bool[n];
            double front = 0, back = 0;
            bool Bin(double x, double y, out int a, out int b)
            {
                a = (int)Math.Floor((x - pl.X0) / bin);
                b = (int)Math.Floor((y - pl.Y0) / bin);
                return a >= 0 && a < pl.Nx && b >= 0 && b < pl.Ny && pl.Mask[a, b];
            }
            for (int i = 0; i < n; i++)
            {
                if (use != null && !use[i] || (v[i] - mid).LengthSquared() > rad2)
                    continue;
                q[i] = r.TransposeTimes(v[i] - o);
                bool inside = Bin(q[i].X, q[i].Y, out int a, out int b);
                d[i] = q[i].Z - (inside ? (pl.Zf[a, b] + pl.Zb[a, b]) / 2 : pl.MidMean);
                if (Math.Abs(d[i]) >= PlateDepthLimit)
                    continue;
                ok[i] = true;
                if (!inside)
                    continue;
                if (d[i] < 0)
                    front = Math.Max(front, -d[i]);
                else
                    back = Math.Max(back, d[i]);
            }
            double reach = Math.Min(front, back), deepest = 0;
            if (reach <= 0)
                return 0;
            for (int k = 0; k + 1 < edges.Length; k += 2)
            {
                int i = edges[k], j = edges[k + 1];
                if (!ok[i] || !ok[j] || (d[i] < 0) == (d[j] < 0))
                    continue;
                double t = d[i] / (d[i] - d[j]);
                var c = q[i] + (q[j] - q[i]) * t;
                if (!Bin(c.X, c.Y, out int a, out int b))
                    continue;
                double dep = Math.Min(pl.EdgeDist[a, b], reach);
                if (dep <= 0)
                    continue;
                deepest = Math.Max(deepest, dep);
                hits?.Add(new LcHit { P = o + r * c, Depth = dep, Part = LcBody.RightWeaponPart, Index = i });
            }
            return deepest;
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
                rows[f] = DepthsAll(fr, fr.Wt, fr.Wq, items, false);
                progress?.Invoke((f + 1.0) / frames);
            }
            return rows;
        }
    }
}
