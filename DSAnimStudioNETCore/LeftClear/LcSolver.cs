// ERAnimStudio: the left arm fix's solve (Edit > Left Arm Clearance), a port of the pack's left_clear.py. Per frame, the smallest
// change of the left arm that keeps the items in the left hand out of the armor:
//   - the item held the game's way first (optional): L_Weapon keyed as every vanilla one-handed attack keys it (Hold);
//   - the hand turned about the forearm until the palm faces the torso's line (pelvis to neck), with the game's neutral wrist: a shield
//     then faces away from the body and a blade lies across the line to it, as the game's one-handed attacks hold the left hand;
//   - from there the search departs only as far as the items need: the wrist moved in the chest's frame (the arm by two-bone IK, at most
//     the move limit), the roll offset from the palm-in turn, the two wrist bends, the elbow's swing about the shoulder-wrist line, each
//     within its limit; each change costs by its size (Sigma), each cm of crossing squared by the item's weight (must clear 4, best
//     effort 0.3);
//   - all frames together, each against its neighbours' current answers (first and second differences cost), in sweeps forward and
//     back (Powell's method per frame), so the change eases in and out by itself;
//   - then runs of frames still crossing get the wrist moved and turned out together (the block step: one frame can't move alone
//     against its neighbours), and refining sweeps;
//   - then the final pose settled over 2 frames, a frame falling back toward the solve where that makes a must-clear item cross.
// Pinned frames (a strike with the left hand) keep the source's arm, the palm-in turn fading out over 8 frames. A mirrored clip is
// solved on the pose as the game plays it. Every frame of the animation counts.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSAnimStudio.HkxEdit;

namespace DSAnimStudio.LeftClear
{
    /// <summary>The fix's limits: the user's (defaults: the pack tool's).</summary>
    public sealed class LcLimits
    {
        public double MoveCm = 12;      // the wrist's move
        public double Roll = 30;        // the hand's roll offset from the palm-in turn, degrees
        public double Bend1 = 60;       // the wrist's two bends
        public double Bend2 = 60;
        public double Swing = 60;       // the elbow's swing about the shoulder-wrist line

        public static LcLimits Parse(string s)
        {
            var l = new LcLimits();
            var v = (s ?? "").Split(',').Select(x => double.TryParse(x.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToArray();
            if (v.Length == 5 && v.All(x => !double.IsNaN(x) && x > 0))
                (l.MoveCm, l.Roll, l.Bend1, l.Bend2, l.Swing) = (v[0], v[1], v[2], v[3], v[4]);
            return l;
        }

        public override string ToString() => string.Join(",", new[] { MoveCm, Roll, Bend1, Bend2, Swing }
            .Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public double[] Limit => new[] { MoveCm / 100, MoveCm / 100, MoveCm / 100, Roll, Bend1, Bend2, Swing };
    }

    public sealed class LcSolveInput
    {
        public LcSkeleton Sk;
        public LcBody Body;
        public List<LcItem> Items;          // Required: must clear
        public HkxAnimation Source;         // the file as it is
        public bool Mirror;                 // solved as a mirrored clip plays it
        public bool Hold = true;
        public List<(int first, int last)> Pins = new List<(int, int)>();   // the file's frames
        public LcLimits Limits = new LcLimits();
    }

    public sealed class LcSolveResult
    {
        public HkxAnimation Copy;           // the file's side (mirrored back when solved mirrored), the source's tracks
        public HkxAnimation Played;         // as the game plays it (mirrored when solved mirrored)
        public Dictionary<string, double>[][] Before, Held, After;   // [frame][item]
        public int Frames;
        public double Fps;
        public double MoveMax, MoveMedian, RollMedian, RollMax, BendMax, SwingMax;
        public double PalmBefore, PalmAfter, PalmWithin45;
        public int SettledFully;
        public double Seconds;
        public List<string> Log = new List<string>();
    }

    /// <summary>One frame's left arm and how a change poses it (left_clear.Arm).</summary>
    sealed class LcArm
    {
        public const double Reach = 0.995;
        public const double PalmAllow = 0.5;

        readonly LcSkeleton sk;
        readonly D3[] lt;
        readonly DQ[] lq;
        public readonly D3[] Wt;
        public readonly DQ[] Wq;
        public readonly int U, F, H, C;
        public readonly D3 S, E, W;
        readonly double a, b;
        readonly DQ chest;
        readonly D3 ax, e1, e2;
        readonly (int anchor, int[] m, D3[] rt, DQ[] rq)[] groups;
        public readonly bool Bent;
        public readonly double[] Center;    // the hand held the game's way: no move, the neutral wrist's bend (Sigma's units)
        readonly D3 pelvis, neck;

        public LcArm(LcSkeleton sk, D3[] lt, DQ[] lq, D3[] wt, DQ[] wq, int[] subH, int[] subF, int[] subU)
        {
            this.sk = sk;
            this.lt = lt;
            this.lq = lq;
            Wt = wt;
            Wq = wq;
            U = sk.Bone("L_UpperArm");
            F = sk.Bone("L_Forearm");
            H = sk.Bone("L_Hand");
            C = sk.Parents[U];
            S = wt[U];
            E = wt[F];
            W = wt[H];
            a = (E - S).Length();
            b = (W - E).Length();
            chest = wq[sk.Bone("Spine2")];
            ax = lt[H].Unit();
            e1 = D3.Cross(ax, Math.Abs(ax.Z) < 0.9 ? new D3(0, 0, 1) : new D3(0, 1, 0)).Unit();
            e2 = D3.Cross(ax, e1);
            var hSet = new HashSet<int>(subH);
            var fSet = new HashSet<int>(subF);
            groups = new[] { (H, subH), (F, subF.Where(x => !hSet.Contains(x)).ToArray()), (U, subU.Where(x => !fSet.Contains(x)).ToArray()) }
                .Select(g =>
                {
                    var inv = wq[g.Item1].Conj();
                    var m = g.Item2.OrderBy(x => x).ToArray();
                    return (g.Item1, m, m.Select(x => inv.Rotate(wt[x] - wt[g.Item1])).ToArray(), m.Select(x => inv * wq[x]).ToArray());
                }).ToArray();
            var u0 = (W - S).Unit();
            var n0 = (E - S) - D3.Dot(E - S, u0) * u0;
            Bent = n0.Length() > 0.01 * a;
            // the change that takes the hand to the skeleton's reference on its forearm: roll about the forearm, then the bend left
            var D = (sk.RefQ[H] * lq[H].Conj()).Normalized();
            if (D.W < 0)
                D = D.Negate();
            var dv = new D3(D.X, D.Y, D.Z);
            double roll = 2 * Math.Atan2(D3.Dot(dv, ax), D.W) * 180 / Math.PI;
            var tw = new DQ(D3.Dot(dv, ax) * ax.X, D3.Dot(dv, ax) * ax.Y, D3.Dot(dv, ax) * ax.Z, D.W).Normalized();
            var sw = tw.Conj() * D;
            if (sw.W < 0)
                sw = sw.Negate();
            double ang = 2 * Math.Acos(Math.Clamp(sw.W, -1, 1)) * 180 / Math.PI;
            var v = ang > 1e-6 ? new D3(sw.X, sw.Y, sw.Z).Unit() * ang : D3.Zero;
            var sig = LcSolver.Sigma;
            Center = new[] { 0, 0, 0, 0, D3.Dot(v, e1) / sig[4], D3.Dot(v, e2) / sig[5], 0 };
            _ = roll;
            pelvis = wt[sk.Bone("Pelvis")];
            neck = wt[sk.Bone("Neck")];
        }

        public D3 BendPlaneOrNull()
        {
            var u0 = (W - S).Unit();
            var x = (E - S) - D3.Dot(E - S, u0) * u0;
            return Bent ? x.Unit() : new D3(double.NaN, double.NaN, double.NaN);
        }

        static DQ Swing(D3 a, D3 b)
        {
            a = a.Unit();
            b = b.Unit();
            double w = 1 + D3.Dot(a, b);
            var c = D3.Cross(a, b);
            return new DQ(c.X, c.Y, c.Z, Math.Max(w, 1e-9)).Normalized();
        }

        /// <summary>The palm's angle to the torso's line (degrees), the turn about the forearm that would face it there, and the share of
        /// the direction to the torso across the forearm.</summary>
        public (double angle, double turn, double allow) Palm(D3[] wt, DQ[] wq)
        {
            var hand = wt[H];
            var ab = neck - pelvis;
            double s = Math.Clamp(D3.Dot(hand - pelvis, ab) / Math.Max(ab.LengthSquared(), 1e-9), 0, 1);
            var d = (pelvis + s * ab - hand).Unit();
            var palm = wq[H].Rotate(new D3(0, 0, 1));
            double angle = Math.Acos(Math.Clamp(D3.Dot(palm, d), -1, 1)) * 180 / Math.PI;
            var fa = wq[F].Rotate(ax).Unit();
            var pp = palm - D3.Dot(palm, fa) * fa;
            var dp = d - D3.Dot(d, fa) * fa;
            double turn = Math.Atan2(D3.Dot(D3.Cross(pp, dp), fa), D3.Dot(pp, dp)) * 180 / Math.PI;
            return (angle, turn, dp.Length());
        }

        /// <summary>Pose with the roll taken as an offset from the palm-in turn (by weight and by how far the forearm allows it).</summary>
        public (D3[] wt, DQ[] wq, DQ[] loc, double roll) PosePalm(double[] p, D3 plane, double weight)
        {
            var q = (double[])p.Clone();
            q[3] = 0;
            var (wt, wq, _) = Pose(q, plane);
            var (_, turn, allow) = Palm(wt, wq);
            q[3] = weight * Math.Min(1.0, allow / PalmAllow) * turn + p[3];
            var r = Pose(q, plane);
            return (r.wt, r.wq, r.loc, q[3]);
        }

        /// <summary>p: the change (m and degrees: the wrist's move 3, roll, bend 2, swing) -> world pose, the new local turns of the upper
        /// arm, forearm and hand.</summary>
        public (D3[] wt, DQ[] wq, DQ[] loc) Pose(double[] p, D3 plane)
        {
            var G = W + chest.Rotate(new D3(p[0], p[1], p[2]));
            double reach = Math.Max(Reach * (a + b), (W - S).Length());
            if ((G - S).Length() > reach)
                G = S + (G - S).Unit() * reach;
            var u0 = (W - S).Unit();
            var n0 = (E - S) - D3.Dot(E - S, u0) * u0;
            n0 = Bent || double.IsNaN(plane.X) ? n0.Unit() : plane;
            var u = (G - S).Unit();
            var n1 = Swing(u0, u).Rotate(n0);
            n1 = (n1 - D3.Dot(n1, u) * u).Unit();
            double dd = Math.Clamp((G - S).Length(), Math.Abs(a - b) + 1e-4, a + b - 1e-4);
            double x = (a * a - b * b + dd * dd) / (2 * dd);
            var E2 = S + x * u + Math.Sqrt(Math.Max(a * a - x * x, 0)) * n1;
            var W2 = S + dd * u;
            var s1 = Swing(E - S, E2 - S);
            var s2 = Swing(s1.Rotate(W - E), W2 - E2);
            var Qs = DQ.AxisAngle(u, p[6]);
            var qU = (Qs * (s1 * Wq[U])).Normalized();
            var qF = (Qs * (s2 * (s1 * Wq[F]))).Normalized();
            var lqH = (DQ.AxisAngle(ax, p[3]) * ((DQ.AxisAngle(e1, p[4]) * DQ.AxisAngle(e2, p[5])) * lq[H])).Normalized();
            var qH = (qF * lqH).Normalized();
            var tF = S + qU.Rotate(lt[F]);
            var tH = tF + qF.Rotate(lt[H]);
            var wt = (D3[])Wt.Clone();
            var wq = (DQ[])Wq.Clone();
            var place = new[] { (tH, qH), (tF, qF), (S, qU) };
            for (int g = 0; g < 3; g++)
            {
                var (_, m, rt, rq) = groups[g];
                var (t, q) = place[g];
                for (int k = 0; k < m.Length; k++)
                {
                    wt[m[k]] = t + q.Rotate(rt[k]);
                    wq[m[k]] = q * rq[k];
                }
            }
            var lqU = Wq[C].Conj() * qU;
            var lqF = qU.Conj() * qF;
            return (wt, wq, new[] { lqU, lqF, lqH });
        }
    }

    /// <summary>Powell's direction-set method within bounds, each line searched by Brent's bounded method (scipy's Powell, as the pack
    /// tool calls it: the evaluation budget checked after each pass over the directions, so a pass always ends).</summary>
    static class LcPowell
    {
        public static (double[] x, double fx, int nfev) Minimize(Func<double[], double> f, double[] x0, double[] lo, double[] hi,
                                                                 int maxfev, double xtol = 0.01, double ftol = 1e-4)
        {
            int n = x0.Length;
            var x = x0.Select((v, i) => Math.Clamp(v, lo[i], hi[i])).ToArray();
            int nfev = 0;
            double F(double[] v)
            {
                nfev++;
                return f(v);
            }
            double fx = F(x);
            var dirs = new double[n][];
            for (int i = 0; i < n; i++)
            {
                dirs[i] = new double[n];
                dirs[i][i] = 1;
            }
            while (nfev < maxfev)
            {
                double fx0 = fx;
                var x1 = (double[])x.Clone();
                int big = 0;
                double delta = 0;
                for (int i = 0; i < n; i++)     // the budget is checked after a whole pass, as scipy's Powell does
                {
                    double before = fx;
                    (x, fx) = Line(F, x, fx, dirs[i], lo, hi, xtol, maxfev, () => nfev);
                    if (before - fx > delta)
                    {
                        delta = before - fx;
                        big = i;
                    }
                }
                if (2 * (fx0 - fx) <= ftol * (Math.Abs(fx0) + Math.Abs(fx)) + 1e-20 || nfev >= maxfev)
                    break;
                var d = x.Select((v, i) => v - x1[i]).ToArray();
                var x2 = x.Select((v, i) => Math.Clamp(v + d[i], lo[i], hi[i])).ToArray();
                double fx2 = F(x2);
                if (fx0 > fx2)
                {
                    double t = 2 * (fx0 + fx2 - 2 * fx) * Sq(fx0 - fx - delta) - delta * Sq(fx0 - fx2);
                    if (t < 0 && d.Any(v => Math.Abs(v) > 1e-12))
                    {
                        (x, fx) = Line(F, x, fx, d, lo, hi, xtol, maxfev, () => nfev);
                        dirs[big] = dirs[n - 1];
                        dirs[n - 1] = d;
                    }
                }
            }
            return (x, fx, nfev);
        }

        static double Sq(double v) => v * v;

        // the best point along x + t d within the bounds (Brent's bounded search over the t the bounds allow)
        static (double[] x, double fx) Line(Func<double[], double> F, double[] x, double fx, double[] d, double[] lo, double[] hi,
                                            double xtol, int maxfev, Func<int> nfev)
        {
            double tmin = double.NegativeInfinity, tmax = double.PositiveInfinity;
            for (int i = 0; i < x.Length; i++)
            {
                if (Math.Abs(d[i]) < 1e-15)
                    continue;
                double t1 = (lo[i] - x[i]) / d[i], t2 = (hi[i] - x[i]) / d[i];
                tmin = Math.Max(tmin, Math.Min(t1, t2));
                tmax = Math.Min(tmax, Math.Max(t1, t2));
            }
            if (!(tmax > tmin) || double.IsInfinity(tmin) || double.IsInfinity(tmax))
                return (x, fx);
            double At(double t) => F(x.Select((v, i) => Math.Clamp(v + t * d[i], lo[i], hi[i])).ToArray());
            var (tb, fb) = Brent(At, tmin, tmax, xtol, maxfev, nfev);
            if (fb < fx)
                return (x.Select((v, i) => Math.Clamp(v + tb * d[i], lo[i], hi[i])).ToArray(), fb);
            return (x, fx);
        }

        // scipy's _minimize_scalar_bounded (fminbound)
        static (double t, double ft) Brent(Func<double, double> f, double a, double b, double xatol, int maxfev, Func<int> nfev)
        {
            const double golden = 0.3819660112501051, sqrtEps = 1.4901161193847656e-8;
            double fulc = a + golden * (b - a), nfc = fulc, xf = fulc;
            double rat = 0, e = 0, x = xf, fx = f(x);
            double ffulc = fx, fnfc = fx;
            double xm = 0.5 * (a + b), tol1 = sqrtEps * Math.Abs(xf) + xatol / 3, tol2 = 2 * tol1;
            int iter = 0;
            while (Math.Abs(xf - xm) > tol2 - 0.5 * (b - a) && iter++ < 500)
            {
                bool golden_ = true;
                if (Math.Abs(e) > tol1)
                {
                    golden_ = false;
                    double r = (xf - nfc) * (fx - ffulc), q = (xf - fulc) * (fx - fnfc), p = (xf - fulc) * q - (xf - nfc) * r;
                    q = 2 * (q - r);
                    if (q > 0)
                        p = -p;
                    q = Math.Abs(q);
                    r = e;
                    e = rat;
                    if (Math.Abs(p) < Math.Abs(0.5 * q * r) && p > q * (a - xf) && p < q * (b - xf))
                    {
                        rat = p / q;
                        x = xf + rat;
                        if (x - a < tol2 || b - x < tol2)
                            rat = tol1 * (xm - xf >= 0 ? 1 : -1);
                    }
                    else
                        golden_ = true;
                }
                if (golden_)
                {
                    e = xf >= xm ? a - xf : b - xf;
                    rat = golden * e;
                }
                double si = rat >= 0 ? 1 : -1;
                x = xf + si * Math.Max(Math.Abs(rat), tol1);
                double fu = f(x);
                if (fu <= fx)
                {
                    if (x >= xf)
                        a = xf;
                    else
                        b = xf;
                    fulc = nfc;
                    ffulc = fnfc;
                    nfc = xf;
                    fnfc = fx;
                    xf = x;
                    fx = fu;
                }
                else
                {
                    if (x < xf)
                        a = x;
                    else
                        b = x;
                    if (fu <= fnfc || nfc == xf)
                    {
                        fulc = nfc;
                        ffulc = fnfc;
                        nfc = x;
                        fnfc = fu;
                    }
                    else if (fu <= ffulc || fulc == xf || fulc == nfc)
                    {
                        fulc = x;
                        ffulc = fu;
                    }
                }
                xm = 0.5 * (a + b);
                tol1 = sqrtEps * Math.Abs(xf) + xatol / 3;
                tol2 = 2 * tol1;
            }
            return (xf, fx);
        }
    }

    public sealed class LcSolver
    {
        // the pack tool's settings (left_clear.py), but the limits, which are the user's
        public static readonly double[] Sigma = { 0.06, 0.06, 0.06, 30, 20, 20, 25 };
        public static readonly D3 HoldT = new D3(0.0819, -0.0096, 0.0165);
        public static readonly DQ HoldQ = new DQ(0.0121, -0.0674, 0.1606, 0.9846).Normalized();
        const double WeightRequired = 4.0, WeightBest = 0.3;
        const double Smooth1 = 30, Smooth2 = 60;
        const int Sweeps = 6, RequiredSweeps = 4, MaxFev = 250, BlockRamp = 6, RefineSweeps = 2, BlockRounds = 2;
        const double BlockMoveStep = 0.04, BlockTurnStep = 15;
        const double SettleSigma = 2.0, SettleSlack = 0.003;
        const int PinFade = 8;

        static readonly D3[] Directions = BuildDirections();

        static D3[] BuildDirections()
        {
            var d = new List<D3> { new D3(1, 0, 0), new D3(-1, 0, 0), new D3(0, 1, 0), new D3(0, -1, 0), new D3(0, 0, 1), new D3(0, 0, -1) };
            foreach (int a in new[] { -1, 1 })
                foreach (int b in new[] { -1, 1 })
                    d.AddRange(new[] { new D3(a, b, 0), new D3(a, 0, b), new D3(0, a, b) });
            return d.Select(v => v.Unit()).ToArray();
        }

        readonly LcSolveInput input;
        readonly LcMeasure measure;
        readonly List<LcItem> items, required;
        readonly double[] limit, lim;      // in m / degrees; in Sigma's units
        readonly double cap;
        readonly double[] blockMoves, blockTurns;
        int Fn;
        LcArm[] arms;
        LcMeasure.Frame[] statics;
        D3[] planes;
        double[] palmW, rolls;
        bool[] pinned;
        HkxAnimation played;
        D3[,] wt0;
        DQ[,] wq0;
        Action<double, string> progress;
        CancellationToken token;

        public LcSolver(LcSolveInput input)
        {
            this.input = input;
            measure = new LcMeasure(input.Body);
            items = input.Items;
            required = items.Where(i => i.Required).ToList();
            limit = input.Limits.Limit;
            cap = input.Limits.MoveCm / 100;
            lim = limit.Select((v, i) => v / Sigma[i]).ToArray();
            int nm = Math.Max(1, (int)Math.Round(cap / BlockMoveStep));
            blockMoves = Enumerable.Range(1, nm).Select(k => cap * k / nm).ToArray();
            int nt = Math.Max(1, (int)Math.Round(input.Limits.Roll / BlockTurnStep));
            blockTurns = Enumerable.Range(1, nt).Select(k => input.Limits.Roll * k / nt).ToArray();
        }

        // ---- per frame

        double[] Capped(double[] x)
        {
            var p = x.Select((v, i) => v * Sigma[i]).ToArray();
            double d = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
            if (d > cap)
                for (int i = 0; i < 3; i++)
                    p[i] *= cap / d;
            for (int i = 3; i < 7; i++)
                p[i] = Math.Clamp(p[i], -limit[i], limit[i]);
            return p.Select((v, i) => v / Sigma[i]).ToArray();
        }

        static double[] Times(double[] x, double[] s) => x.Select((v, i) => v * s[i]).ToArray();

        // the items' rows, then the left arm's own on the right weapon (LcMeasure.DepthsAll), which must clear as a required item
        double Penalty(Dictionary<string, double>[] dep, IList<LcItem> names)
        {
            double c = 0;
            for (int i = 0; i < dep.Length; i++)
            {
                double w = i >= names.Count || names[i].Required ? WeightRequired : WeightBest;
                foreach (var v in dep[i].Values)
                    c += w * (v * 100) * (v * 100);
            }
            return c;
        }

        double Pen(int f, double[] x, IList<LcItem> names)
        {
            if (names.Count == 0 && input.Body.RightWeapon == null)
                return 0;
            var (wt, wq, _, _) = arms[f].PosePalm(Times(x, Sigma), planes[f], palmW[f]);
            return Penalty(measure.DepthsAll(statics[f], wt, wq, names, true), names);
        }

        // the smoothness terms of frame f's change against its neighbours: (weight, coefficient, rest)
        List<(double w, double a, double[] c)> SmoothTerms(double[][] z, int f)
        {
            var o = new List<(double, double, double[])>();
            foreach (int g in new[] { f - 1, f + 1 })
                if (g >= 0 && g < Fn)
                    o.Add((Smooth1, 1.0, z[g].Select(v => -v).ToArray()));
            foreach (int c in new[] { f - 1, f, f + 1 })
            {
                if (c < 1 || c > Fn - 2)
                    continue;
                var coef = new Dictionary<int, double> { [c - 1] = 1, [c] = -2, [c + 1] = 1 };
                var rest = new double[7];
                foreach (var (g, k) in coef)
                    if (g != f)
                        for (int i = 0; i < 7; i++)
                            rest[i] += k * z[g][i];
                o.Add((Smooth2, coef[f], rest));
            }
            return o;
        }

        static double FrameCost(double[] x, List<(double w, double a, double[] c)> terms, double[] center)
        {
            double s = 0;
            for (int i = 0; i < 7; i++)
                s += (x[i] - center[i]) * (x[i] - center[i]);
            foreach (var (w, a, c) in terms)
                for (int i = 0; i < 7; i++)
                {
                    double v = a * x[i] + c[i];
                    s += w * v * v;
                }
            return s;
        }

        static double[] QuietBest(List<(double w, double a, double[] c)> terms, double[] center)
        {
            var o = new double[7];
            for (int i = 0; i < 7; i++)
            {
                double num = center[i], den = 1;
                foreach (var (w, a, c) in terms)
                {
                    num -= w * a * c[i];
                    den += w * a * a;
                }
                o[i] = num / den;
            }
            return o;
        }

        (double[] x, int n) SolveFrame(int f, IList<LcItem> names, double[] x0, List<(double, double, double[])> terms, List<double[]> near)
        {
            var c0 = arms[f].Center;
            var q = Capped(QuietBest(terms, c0));
            double pq = Pen(f, q, names);
            if (pq == 0)
                return (q, 1);
            var raw = new List<double[]> { x0 };
            raw.AddRange(near);
            raw.Add(c0);
            raw.Add(x0.Select((v, i) => i == 4 || i == 5 ? c0[i] : v).ToArray());
            var starts = raw.Select(x => Capped(x.Select((v, i) => Math.Clamp(v, -lim[i], lim[i])).ToArray())).ToList();
            var cand = new List<double[]> { q };
            cand.AddRange(starts);
            var costs = new List<double> { pq + FrameCost(q, terms, c0) };
            costs.AddRange(starts.Select(x => Pen(f, x, names) + FrameCost(x, terms, c0)));
            int bi = costs.IndexOf(costs.Min());
            var best = cand[bi];
            var neg = lim.Select(v => -v).ToArray();
            var (rx, _, nfev) = LcPowell.Minimize(x => Pen(f, x, names) + FrameCost(x, terms, c0), best, neg, lim, MaxFev);
            var xr = Capped(rx);
            int n = nfev + costs.Count;
            return Pen(f, xr, names) + FrameCost(xr, terms, c0) <= costs.Min() ? (xr, n) : (best, n);
        }

        // ---- over the frames

        (D3[][] wt, DQ[][] wq, DQ[][] loc) Posed(double[][] z)
        {
            var wt = new D3[Fn][];
            var wq = new DQ[Fn][];
            var loc = new DQ[Fn][];
            for (int f = 0; f < Fn; f++)
            {
                var r = arms[f].PosePalm(Times(z[f], Sigma), planes[f], palmW[f]);
                (wt[f], wq[f], loc[f], rolls[f]) = r;
            }
            return (wt, wq, loc);
        }

        (D3[][] wt, DQ[][] wq, DQ[][] loc) PosedAbs(double[][] Q)
        {
            var wt = new D3[Fn][];
            var wq = new DQ[Fn][];
            var loc = new DQ[Fn][];
            Parallel.For(0, Fn, f => (wt[f], wq[f], loc[f]) = arms[f].Pose(Q[f], planes[f]));
            return (wt, wq, loc);
        }

        Dictionary<string, double>[][] Measure(D3[][] wt, DQ[][] wq)
        {
            var rows = new Dictionary<string, double>[Fn][];
            Parallel.For(0, Fn, f => rows[f] = measure.DepthsAll(statics[f], wt[f], wq[f], items, false));
            return rows;
        }

        double WorstRequired(Dictionary<string, double>[] row)
        {
            double m = 0;
            for (int i = 0; i < row.Length; i++)
                if (i >= items.Count || items[i].Required)
                    foreach (var v in row[i].Values)
                        m = Math.Max(m, v);
            return m;
        }

        bool[] Crossing(Dictionary<string, double>[][] rows, double floor) => rows.Select(r => WorstRequired(r) > floor).ToArray();

        double RunCost(double[][] z, int lo, int hi)
        {
            double c = 0;
            for (int f = lo; f <= hi; f++)
            {
                c += Pen(f, z[f], items);
                var ctr = arms[f].Center;
                for (int i = 0; i < 7; i++)
                    c += (z[f][i] - ctr[i]) * (z[f][i] - ctr[i]);
            }
            for (int f = Math.Max(1, lo); f < Math.Min(Fn, hi + 2); f++)
                for (int i = 0; i < 7; i++)
                {
                    double d = z[f][i] - z[f - 1][i];
                    c += Smooth1 * d * d;
                }
            for (int f = Math.Max(1, lo); f < Math.Min(Fn - 1, hi + 2); f++)
                for (int i = 0; i < 7; i++)
                {
                    double d = z[f + 1][i] - 2 * z[f][i] + z[f - 1][i];
                    c += Smooth2 * d * d;
                }
            return c;
        }

        static double[][] Copy(double[][] z) => z.Select(r => (double[])r.Clone()).ToArray();

        int BlockMoves(double[][] z, Dictionary<string, double>[][] rows)
        {
            var bad = Crossing(rows, 0.01).Select((b, f) => b && !pinned[f]).ToArray();
            var runs = new List<(int a, int b)>();
            for (int f = 0; f < Fn;)
            {
                if (!bad[f])
                {
                    f++;
                    continue;
                }
                int g = f;
                while (g + 1 < Fn && (bad[g + 1] || (g + 2 < Fn && bad[g + 2]) || (g + 3 < Fn && bad[g + 3])))
                    g++;
                runs.Add((f, g));
                f = g + 1;
            }
            int moved = 0;
            foreach (var (a, b) in runs)
            {
                token.ThrowIfCancellationRequested();
                int lo = Math.Max(0, a - BlockRamp), hi = Math.Min(Fn - 1, b + BlockRamp);
                var w = new double[Fn];
                for (int f = a; f <= b; f++)
                    w[f] = 1;
                for (int k = 1; k <= BlockRamp; k++)
                {
                    double s = 0.5 - 0.5 * Math.Cos(Math.PI * (BlockRamp + 1 - k) / (BlockRamp + 1));
                    if (a - k >= 0)
                        w[a - k] = Math.Max(w[a - k], s);
                    if (b + k < Fn)
                        w[b + k] = Math.Max(w[b + k], s);
                }
                for (int f = 0; f < Fn; f++)
                    if (pinned[f])
                        w[f] = 0;
                double c0 = RunCost(z, lo, hi), bestC = c0;
                var baseZ = Copy(z);
                // the wrist moved out: every direction and size at once (each candidate on its own copy)
                var cands = Directions.SelectMany(d => blockMoves.Select(m => (d, m))).ToArray();
                var cc = new double[cands.Length];
                Parallel.For(0, cands.Length, i =>
                {
                    var zz = Copy(baseZ);
                    var (d, m) = cands[i];
                    for (int f = lo; f <= hi; f++)
                    {
                        zz[f][0] += w[f] * d.X * m / Sigma[0];
                        zz[f][1] += w[f] * d.Y * m / Sigma[1];
                        zz[f][2] += w[f] * d.Z * m / Sigma[2];
                        zz[f] = Capped(zz[f]);
                    }
                    cc[i] = RunCost(zz, lo, hi);
                });
                int bm = -1;
                for (int i = 0; i < cands.Length; i++)
                    if (cc[i] < bestC)
                    {
                        bestC = cc[i];
                        bm = i;
                    }
                var what = new List<string>();
                if (bm >= 0)
                {
                    var (d, m) = cands[bm];
                    for (int f = lo; f <= hi; f++)
                    {
                        z[f][0] += w[f] * d.X * m / Sigma[0];
                        z[f][1] += w[f] * d.Y * m / Sigma[1];
                        z[f][2] += w[f] * d.Z * m / Sigma[2];
                        z[f] = Capped(z[f]);
                    }
                    what.Add($"the wrist moved {m * 100:0} cm along ({d.X:0.##}, {d.Y:0.##}, {d.Z:0.##}) of the chest's frame");
                }
                // then the turns, greedily: the roll offset, the two bends, the swing, each way, each size
                string[] tn = { "roll", "bend 1", "bend 2", "swing" };
                for (int comp = 3; comp < 7; comp++)
                {
                    var b0 = Copy(z);
                    var tc = blockTurns.SelectMany(deg => new[] { deg, -deg }).ToArray();
                    var tz = new double[tc.Length][][];
                    var tcost = new double[tc.Length];
                    Parallel.For(0, tc.Length, i =>
                    {
                        var zz = Copy(b0);
                        for (int f = lo; f <= hi; f++)
                        {
                            zz[f][comp] += w[f] * tc[i] / Sigma[comp];
                            zz[f] = Capped(zz[f].Select((v, k) => Math.Clamp(v, -lim[k], lim[k])).ToArray());
                        }
                        tz[i] = zz;
                        tcost[i] = RunCost(zz, lo, hi);
                    });
                    int pick = -1;
                    for (int i = 0; i < tc.Length; i++)
                        if (tcost[i] < bestC)
                        {
                            bestC = tcost[i];
                            pick = i;
                        }
                    if (pick >= 0)
                    {
                        for (int f = 0; f < Fn; f++)
                            z[f] = tz[pick][f];
                        what.Add($"{tn[comp - 3]} {tc[pick]:+0;-0}");
                    }
                }
                if (what.Count > 0)
                    moved++;
                Log($"block {a}-{b}: " + (what.Count > 0 ? string.Join(", ", what) + $"; cost {c0:0} -> {bestC:0}" : $"no change lowers its cost ({c0:0})"));
            }
            return moved;
        }

        static double[][] Gauss(double[][] x, double sig)
        {
            int n = 3 * (int)Math.Ceiling(sig);
            var w = Enumerable.Range(-n, 2 * n + 1).Select(k => Math.Exp(-0.5 * (k / sig) * (k / sig))).ToArray();
            double sum = w.Sum();
            for (int i = 0; i < w.Length; i++)
                w[i] /= sum;
            int F = x.Length, C = x[0].Length;
            var o = new double[F][];
            for (int f = 0; f < F; f++)
            {
                o[f] = new double[C];
                for (int k = -n; k <= n; k++)
                {
                    var src = x[Math.Clamp(f + k, 0, F - 1)];
                    for (int c = 0; c < C; c++)
                        o[f][c] += w[k + n] * src[c];
                }
            }
            return o;
        }

        readonly List<string> log = new List<string>();
        void Log(string s) => log.Add(s);

        // ---- the whole solve

        public LcSolveResult Run(Action<double, string> progress, CancellationToken token)
        {
            this.progress = progress ?? ((_, _) => { });
            this.token = token;
            var t0 = DateTime.Now;
            var sk = input.Sk;
            var src = input.Source;
            played = input.Mirror ? HkxAnimationOps.Mirror(sk.Hkx, src) : src;
            foreach (var n in new[] { "L_UpperArm", "L_Forearm", "L_Hand", "L_Weapon", "Spine2", "Pelvis", "Neck" })
                if (sk.Bone(n) < 0)
                    throw new InvalidOperationException($"the skeleton has no bone {n}");
            var trackOf = new Dictionary<int, int>();
            for (int k = 0; k < played.Tracks; k++)
                trackOf[played.Bones[k]] = k;
            foreach (var n in new[] { "L_UpperArm", "L_Forearm", "L_Hand" }.Concat(input.Hold ? new[] { "L_Weapon" } : new string[0]))
                if (!trackOf.ContainsKey(sk.Bone(n)))
                    throw new InvalidOperationException($"{n} has no track in the animation{(input.Mirror ? " (as mirrored: the file's R_ twin)" : "")}");
            Fn = played.Frames;
            // the local pose, with the game's hold
            var lt = new D3[Fn][];
            var lq = new DQ[Fn][];
            int wpn = sk.Bone("L_Weapon");
            for (int f = 0; f < Fn; f++)
            {
                lt[f] = (D3[])sk.RefT.Clone();
                lq[f] = (DQ[])sk.RefQ.Clone();
                for (int k = 0; k < played.Tracks; k++)
                {
                    lt[f][played.Bones[k]] = D3.From(played.T[f, k]);
                    lq[f][played.Bones[k]] = DQ.From(played.Q[f, k]);
                }
            }
            (wt0, wq0) = sk.Pose(played);
            var before = new Dictionary<string, double>[Fn][];
            this.progress(0, "measuring the source");
            var allPlates = items.Any(i => i.Kind == LcItemKind.Plate);
            var allPoints = items.Any(i => i.Kind == LcItemKind.Points);
            Parallel.For(0, Fn, f =>
            {
                var fr = measure.Prepare(wt0, wq0, f, allPlates, allPoints);
                before[f] = measure.DepthsAll(fr, fr.Wt, fr.Wq, items, false);
            });
            if (input.Hold)
                for (int f = 0; f < Fn; f++)
                {
                    lt[f][wpn] = HoldT;
                    lq[f][wpn] = HoldQ;
                }
            var wt = new D3[Fn][];
            var wq = new DQ[Fn][];
            statics = new LcMeasure.Frame[Fn];
            arms = new LcArm[Fn];
            int U = sk.Bone("L_UpperArm"), F_ = sk.Bone("L_Forearm"), H = sk.Bone("L_Hand");
            var subH = sk.Subtree(H);
            var subF = sk.Subtree(F_);
            var subU = sk.Subtree(U);
            Parallel.For(0, Fn, f =>
            {
                (wt[f], wq[f]) = World(sk, lt[f], lq[f]);
                statics[f] = measure.PrepareStatic(wt[f], wq[f], allPlates, allPoints);
                arms[f] = new LcArm(sk, lt[f], lq[f], wt[f], wq[f], subH, subF, subU);
            });
            var held = Measure(wt, wq);
            // the bend plane where the arm is nearly straight: the nearest bent frames'
            planes = arms.Select(a => a.BendPlaneOrNull()).ToArray();
            var good = Enumerable.Range(0, Fn).Where(f => !double.IsNaN(planes[f].X)).ToArray();
            if (good.Length > 0 && good.Length < Fn)
                for (int f = 0; f < Fn; f++)
                    if (double.IsNaN(planes[f].X))
                    {
                        int lo = good.LastOrDefault(g => g < f, -1), hi = good.FirstOrDefault(g => g > f, -1);
                        D3 p = lo < 0 ? planes[hi] : hi < 0 ? planes[lo] : planes[lo] + (planes[hi] - planes[lo]) * ((f - lo) / (double)(hi - lo));
                        planes[f] = p.Unit();
                    }
            if (good.Length == 0)
                for (int f = 0; f < Fn; f++)
                    planes[f] = D3.Zero;
            // pins: the palm-in turn fades out over PinFade frames
            pinned = new bool[Fn];
            foreach (var (a, b) in input.Pins)
                for (int f = Math.Max(0, a); f <= Math.Min(Fn - 1, b); f++)
                    pinned[f] = true;
            palmW = pinned.Select(p => p ? 0.0 : 1.0).ToArray();
            if (pinned.Any(p => p))
            {
                var avg = Enumerable.Range(0, Fn).Select(f => Enumerable.Range(f - PinFade, 2 * PinFade + 1)
                    .Select(g => palmW[Math.Clamp(g, 0, Fn - 1)]).Average()).ToArray();
                for (int f = 0; f < Fn; f++)
                    palmW[f] = pinned[f] ? 0 : Math.Min(palmW[f], avg[f]);
            }
            rolls = new double[Fn];
            var active = Enumerable.Range(0, Fn).Where(f => !pinned[f]).ToArray();
            var z = Enumerable.Range(0, Fn).Select(_ => new double[7]).ToArray();
            foreach (int f in active)
                z[f] = (double[])arms[f].Center.Clone();
            var req = required.Count > 0 ? (IList<LcItem>)required : items;
            int steps = Sweeps + BlockRounds * (1 + RefineSweeps) + 1, step = 0;
            Dictionary<string, double>[][] rows = null;
            Dictionary<string, double>[][] Sweep(int k, IList<LcItem> names, string label)
            {
                var order = k % 2 == 0 ? active : active.Reverse().ToArray();
                int evals = 0, done = 0;
                var ts = DateTime.Now;
                foreach (int f in order)
                {
                    token.ThrowIfCancellationRequested();
                    var near = new[] { f - 1, f + 1 }.Where(g => g >= 0 && g < Fn && z[g].Any(v => Math.Abs(v) > 0)).Select(g => z[g]).ToList();
                    var (x, n) = SolveFrame(f, names, z[f], SmoothTerms(z, f), near);
                    z[f] = x;
                    evals += n;
                    done++;
                    this.progress((step + done / (double)order.Length) / steps, label);
                }
                var (pw, pq, _) = Posed(z);
                var r = Measure(pw, pq);
                int left = Crossing(r, 0.01).Count(b => b);
                Log($"{label} ({string.Join(", ", names.Select(i => i.Name))}): {order.Length} frames, {evals} evaluations, "
                    + $"{(DateTime.Now - ts).TotalSeconds:0.0} s; {left} frames with a must-clear item still over 1 cm");
                step++;
                return r;
            }
            for (int k = 0; k < Sweeps; k++)
                rows = Sweep(k, k < RequiredSweeps ? req : items, $"sweep {k + 1}");
            int k0 = Sweeps;
            for (int r = 0; r < BlockRounds; r++)
            {
                this.progress(step / (double)steps, $"block step {r + 1}");
                int moved = BlockMoves(z, rows);
                step++;
                if (moved == 0)
                    break;
                for (int k = 0; k < RefineSweeps; k++)
                    rows = Sweep(k0++, items, $"refine {r + 1}.{k + 1}");
            }
            // the settle
            this.progress((steps - 1.0) / steps, "settling");
            Posed(z);
            var baseQ = z.Select((r, f) => Times(r, Sigma)).ToArray();
            var unwrapped = Unwrap(rolls);
            for (int f = 0; f < Fn; f++)
                baseQ[f][3] = unwrapped[f];
            var (sw0, sq0, _) = Posed(z);
            var solved = Measure(sw0, sq0).Select(WorstRequired).ToArray();
            var smooth = Gauss(baseQ, SettleSigma);
            var sw = palmW.Select(v => v > 0 ? 1.0 : 0.0).ToArray();
            double[][] Q = null;
            (D3[][] wt, DQ[][] wq, DQ[][] loc) fin = default;
            Dictionary<string, double>[][] after = null;
            for (int it = 0; it < 6; it++)
            {
                token.ThrowIfCancellationRequested();
                Q = Enumerable.Range(0, Fn).Select(f => smooth[f].Select((v, i) => sw[f] * v + (1 - sw[f]) * baseQ[f][i]).ToArray()).ToArray();
                fin = PosedAbs(Q);
                after = Measure(fin.wt, fin.wq);
                var now = after.Select(WorstRequired).ToArray();
                var bad = Enumerable.Range(0, Fn).Where(f => now[f] > 0.01 && now[f] > solved[f] + SettleSlack && sw[f] > 0).ToList();
                if (bad.Count == 0)
                    break;
                foreach (int f in bad)
                    sw[f] = 0;
                var g = Gauss(sw.Select(v => new[] { v > 0 ? 1.0 : 0.0 }).ToArray(), 1.5);
                for (int f = 0; f < Fn; f++)
                    sw[f] = Math.Min(sw[f], g[f][0]);
            }
            int fully = sw.Count(v => v > 0.99);
            Log($"settled over {SettleSigma:0} frames: {fully} of {Fn} frames fully, {sw.Count(v => v > 0.01 && v <= 0.99)} partly, {sw.Count(v => v <= 0.01)} kept as solved");
            // the copy: the left upper arm, forearm and hand as solved, L_Weapon held the game's way
            var copy = new HkxAnimation(played.Bones.ToArray(), Fn)
            {
                Duration = played.Duration, SkeletonName = played.SkeletonName, BlendHint = played.BlendHint, Root = played.Root,
            };
            for (int f = 0; f < Fn; f++)
                for (int k = 0; k < played.Tracks; k++)
                {
                    copy.T[f, k] = played.T[f, k];
                    copy.Q[f, k] = played.Q[f, k];
                    copy.S[f, k] = played.S[f, k];
                }
            int[] armBones = { U, F_, H };
            for (int j = 0; j < 3; j++)
            {
                int k = trackOf[armBones[j]];
                for (int f = 0; f < Fn; f++)
                    copy.Q[f, k] = fin.loc[f][j].Normalized().ToFloat();
            }
            if (input.Hold)
            {
                int k = trackOf[wpn];
                for (int f = 0; f < Fn; f++)
                {
                    copy.T[f, k] = HoldT.ToFloat();
                    copy.Q[f, k] = HoldQ.ToFloat();
                }
            }
            copy.MakeQuaternionsContinuous();
            var res = new LcSolveResult { Played = copy, Before = before, Held = held, After = after, Frames = Fn, Fps = played.Fps };
            if (input.Mirror)
            {
                var back = HkxAnimationOps.Mirror(sk.Hkx, copy);
                var col = new Dictionary<int, int>();
                for (int k = 0; k < back.Tracks; k++)
                    col[back.Bones[k]] = k;
                var file = new HkxAnimation(src.Bones.ToArray(), Fn)
                {
                    Duration = src.Duration, SkeletonName = src.SkeletonName, BlendHint = src.BlendHint, Root = src.Root,
                };
                for (int k = 0; k < src.Tracks; k++)
                {
                    int c = col[src.Bones[k]];
                    for (int f = 0; f < Fn; f++)
                    {
                        file.T[f, k] = back.T[f, c];
                        file.Q[f, k] = back.Q[f, c];
                        file.S[f, k] = back.S[f, c];
                    }
                }
                file.MakeQuaternionsContinuous();
                res.Copy = file;
            }
            else
                res.Copy = copy;
            // the report's figures
            var mv = Enumerable.Range(0, Fn).Select(f => (fin.wt[f][H] - wt[f][H]).Length()).ToArray();
            res.MoveMax = mv.Max();
            var changed = mv.Where(v => v > 0.001).OrderBy(v => v).ToArray();
            res.MoveMedian = changed.Length > 0 ? changed[changed.Length / 2] : 0;
            var rollAbs = Q.Select(q => Math.Abs(q[3])).OrderBy(v => v).ToArray();
            res.RollMedian = rollAbs[rollAbs.Length / 2];
            res.RollMax = rollAbs.Last();
            res.BendMax = Q.Max(q => Math.Sqrt(q[4] * q[4] + q[5] * q[5]));
            res.SwingMax = Q.Max(q => Math.Abs(q[6]));
            var pb = Enumerable.Range(0, Fn).Select(f => arms[f].Palm(wt[f], wq[f]).angle).OrderBy(v => v).ToArray();
            var pa = Enumerable.Range(0, Fn).Select(f => arms[f].Palm(fin.wt[f], fin.wq[f]).angle).OrderBy(v => v).ToArray();
            res.PalmBefore = pb[Fn / 2];
            res.PalmAfter = pa[Fn / 2];
            res.PalmWithin45 = pa.Count(v => v < 45) / (double)Fn;
            res.SettledFully = fully;
            res.Seconds = (DateTime.Now - t0).TotalSeconds;
            res.Log.AddRange(log);
            this.progress(1, "done");
            return res;
        }

        static (D3[] wt, DQ[] wq) World(LcSkeleton sk, D3[] lt, DQ[] lq)
        {
            var wt = new D3[sk.Count];
            var wq = new DQ[sk.Count];
            foreach (int b in sk.Order)
            {
                int p = sk.Parents[b];
                if (p < 0)
                {
                    wt[b] = lt[b];
                    wq[b] = lq[b];
                }
                else
                {
                    wq[b] = wq[p] * lq[b];
                    wt[b] = wt[p] + wq[p].Rotate(lt[b]);
                }
            }
            for (int b = 0; b < sk.Count; b++)
                wq[b] = wq[b].Normalized();
            return (wt, wq);
        }

        static double[] Unwrap(double[] deg)
        {
            var o = new double[deg.Length];
            double off = 0;
            for (int i = 0; i < deg.Length; i++)
            {
                if (i > 0)
                {
                    double d = deg[i] + off - o[i - 1];
                    while (d > 180)
                    {
                        off -= 360;
                        d -= 360;
                    }
                    while (d < -180)
                    {
                        off += 360;
                        d += 360;
                    }
                }
                o[i] = deg[i] + off;
            }
            return o;
        }
    }
}
