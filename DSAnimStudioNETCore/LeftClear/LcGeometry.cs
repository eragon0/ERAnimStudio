// ERAnimStudio: the geometry of the left-hand clearance check (Left Item Check in the Animation menu).
//   - The skeleton in double precision, posed from a decoded hkx (HkxEdit).
//   - The armor worn, read from its FLVERs: for a shield's plate, every vertex blend-skinned on up to 4 bones (a cloth bone rides on
//     its nearest ancestor the skeleton has), in shells (one piece's vertices on one strongest bone and body part); for a weapon's
//     points, each vertex rigid on its strongest bone (cloth left out) with its normal, and per bone a radial height field around
//     the principal axis of its vertices (LcSolid).
//   - The item in the left hand: a shield as a plate (its face and back as height fields over the model's x / y, 1 cm bins), any
//     other item as its model's points; hung on a dummy of the character's model, +z on the dummy's forward and +y on its up, or on a
//     bone by a fixed turn.
//   - The weapon in the right hand, an obstacle: a plate across its thinnest extent (its principal axes, 0.5 cm bins), hung on its
//     dummy. Every item and the right weapon keep their whole mesh (vertices and edges) for the crossing test between them, and a
//     weapon in the left hand its own thin plate.
using System;
using System.Collections.Generic;
using System.Linq;
using DSAnimStudio.HkxEdit;
using SoulsFormats;

namespace DSAnimStudio.LeftClear
{
    /// <summary>Body parts as the check names them, for an item in the hand of the given side ('own' is that side).</summary>
    public static class LcParts
    {
        static readonly string[] Leg = { "Thigh", "Calf", "Knee", "Foot", "Toe", "Hip" };

        public static string PartOf(string n, char side)
        {
            if (n.StartsWith("R_") || n.StartsWith("L_"))
            {
                char s = n[0];
                string rest = n.Substring(2);
                if (Leg.Any(rest.Contains))
                    return "leg";
                string own = s == side ? "own" : "other";
                if (rest.Contains("Finger") || rest.Contains("Hand"))
                    return own + " hand";
                if (rest.Contains("Forearm") || rest.Contains("ForeArm") || rest.Contains("Elbow"))
                    return own + " forearm";
                if (rest.Contains("UpperArm") || rest.Contains("UpArm") || rest.Contains("Shoulder") || rest.Contains("Clavicle"))
                    return own + " shoulder/upper arm";
            }
            return n.Contains("Head") ? "head" : n.Contains("Neck") ? "neck" : "torso";
        }

        /// <summary>A part's name for the user, the item in the left hand.</summary>
        public static string Display(string part) => part switch
        {
            "own hand" => "left hand",
            "own forearm" => "left forearm",
            "own shoulder/upper arm" => "left upper arm",
            "other hand" => "right hand",
            "other forearm" => "right forearm",
            "other shoulder/upper arm" => "right upper arm",
            "gauntlet" => "left gauntlet",
            _ => part,
        };
    }

    /// <summary>The skeleton in double precision: reference pose, armature space, and a decoded animation's frames posed on it.</summary>
    public sealed class LcSkeleton
    {
        public readonly HkxSkeleton Hkx;
        public readonly int Count;
        public readonly string[] Names;
        public readonly int[] Parents;
        public readonly int[] Order;
        public readonly Dictionary<string, int> Index;
        public readonly D3[] RefT;     // local reference pose
        public readonly DQ[] RefQ;
        public readonly D3[] RestT;    // the reference pose in armature space
        public readonly DQ[] RestQ;

        public LcSkeleton(HkxSkeleton s)
        {
            Hkx = s;
            Count = s.Count;
            Names = s.Names;
            Parents = s.Parents;
            Order = s.Order;
            Index = s.Index;
            RefT = s.T.Select(D3.From).ToArray();
            RefQ = s.Q.Select(DQ.From).ToArray();
            RestT = new D3[Count];
            RestQ = new DQ[Count];
            foreach (int b in Order)
            {
                int p = Parents[b];
                if (p < 0)
                {
                    RestT[b] = RefT[b];
                    RestQ[b] = RefQ[b];
                }
                else
                {
                    RestQ[b] = (RestQ[p] * RefQ[b]).Normalized();
                    RestT[b] = RestT[p] + RestQ[p].Rotate(RefT[b]);
                }
            }
        }

        public int Bone(string name) => Index.TryGetValue(name, out int i) ? i : -1;

        /// <summary>The bone and every bone under it.</summary>
        public int[] Subtree(int root)
        {
            var kids = new List<int>[Count];
            for (int i = 0; i < Count; i++)
                if (Parents[i] >= 0)
                    (kids[Parents[i]] ??= new List<int>()).Add(i);
            var outList = new List<int>();
            var todo = new Stack<int>();
            todo.Push(root);
            while (todo.Count > 0)
            {
                int b = todo.Pop();
                outList.Add(b);
                if (kids[b] != null)
                    foreach (int k in kids[b])
                        todo.Push(k);
            }
            return outList.ToArray();
        }

        /// <summary>
        /// Every frame's bones in armature space [frame, bone]: the animation's tracks, the reference pose for bones it doesn't animate
        /// (havok_anim.full_pose and fk: scale taken as 1).
        /// </summary>
        public (D3[,] t, DQ[,] q) Pose(HkxAnimation a)
        {
            int frames = a.Frames;
            var lt = new D3[Count];
            var lq = new DQ[Count];
            var wt = new D3[frames, Count];
            var wq = new DQ[frames, Count];
            var track = Enumerable.Repeat(-1, Count).ToArray();
            for (int k = 0; k < a.Tracks; k++)
                if (a.Bones[k] >= 0 && a.Bones[k] < Count)
                    track[a.Bones[k]] = k;
            for (int f = 0; f < frames; f++)
            {
                for (int b = 0; b < Count; b++)
                {
                    int k = track[b];
                    lt[b] = k >= 0 ? D3.From(a.T[f, k]) : RefT[b];
                    lq[b] = k >= 0 ? DQ.From(a.Q[f, k]) : RefQ[b];
                }
                foreach (int b in Order)
                {
                    int p = Parents[b];
                    if (p < 0)
                    {
                        wt[f, b] = lt[b];
                        wq[f, b] = lq[b];
                    }
                    else
                    {
                        wq[f, b] = wq[f, p] * lq[b];
                        wt[f, b] = wt[f, p] + wq[f, p].Rotate(lt[b]);
                    }
                }
                for (int b = 0; b < Count; b++)
                    wq[f, b] = wq[f, b].Normalized();
            }
            return (wt, wq);
        }
    }

    /// <summary>A FLVER bone's place in its model's bind pose (SoulsFormats' order: scale, X, Z, Y, translation), scale left out.</summary>
    public static class LcFlver
    {
        public static M3 EulerXZY(System.Numerics.Vector3 r)
        {
            // applied X first, then Z, then Y: R = Ry Rz Rx for column vectors
            double cx = Math.Cos(r.X), sx = Math.Sin(r.X), cy = Math.Cos(r.Y), sy = Math.Sin(r.Y), cz = Math.Cos(r.Z), sz = Math.Sin(r.Z);
            var rx = new M3(1, 0, 0, 0, cx, -sx, 0, sx, cx);
            var ry = new M3(cy, 0, sy, 0, 1, 0, -sy, 0, cy);
            var rz = new M3(cz, -sz, 0, sz, cz, 0, 0, 0, 1);
            return ry * (rz * rx);
        }

        public static (D3 t, M3 r) ArmatureSpace(FLVER2 f, int node)
        {
            var chain = new List<int>();
            for (int i = node; i >= 0; i = f.Nodes[i].ParentIndex)
                chain.Add(i);
            var t = D3.Zero;
            var r = M3.Identity;
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                var n = f.Nodes[chain[k]];
                t = t + r * D3.From(n.Translation);
                r = r * EulerXZY(n.Rotation);
            }
            return (t, r);
        }

        /// <summary>The model's main mesh points as the game hangs it: the root bone's turn and offset applied (fh_body_check.read_weapon).</summary>
        public static D3[] RootedPositions(FLVER2 f, out List<int[]> triangles)
        {
            M3 r = M3.Identity;
            D3 t = D3.Zero;
            if (f.Nodes.Count > 0)
            {
                r = EulerXZY(f.Nodes[0].Rotation);
                t = D3.From(f.Nodes[0].Translation);
            }
            var pts = new List<D3>();
            triangles = new List<int[]>();
            foreach (var m in f.Meshes)
            {
                if (!HasMember(f, m, FLVER.LayoutSemantic.Position, FLVER.LayoutType.Float3))
                    continue;
                int b = pts.Count;
                foreach (var v in m.Vertices)
                    pts.Add(r * D3.From(v.Position) + t);
                if (m.FaceSets.Count > 0)
                {
                    var idx = m.FaceSets[0].Triangulate(false, true);
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                        if (idx[i] < m.Vertices.Count && idx[i + 1] < m.Vertices.Count && idx[i + 2] < m.Vertices.Count)
                            triangles.Add(new[] { b + idx[i], b + idx[i + 1], b + idx[i + 2] });
                }
            }
            return pts.ToArray();
        }

        /// <summary>Whether one of the mesh's vertex buffers holds this member (no type: any type).</summary>
        public static bool HasMember(FLVER2 f, FLVER2.Mesh m, FLVER.LayoutSemantic sem, FLVER.LayoutType? type = null) =>
            m.VertexBuffers.Any(vb => f.BufferLayouts[vb.LayoutIndex].Any(x => x.Semantic == sem && (type == null || x.Type == type)));

        /// <summary>Whether one vertex buffer of the mesh holds all these members together (the Python tools read buffer by buffer).</summary>
        public static bool HasTogether(FLVER2 f, FLVER2.Mesh m, params FLVER.LayoutSemantic[] sems) =>
            m.VertexBuffers.Any(vb =>
            {
                var lay = f.BufferLayouts[vb.LayoutIndex];
                return sems.All(s => lay.Any(x => x.Semantic == s && (s != FLVER.LayoutSemantic.Position || x.Type == FLVER.LayoutType.Float3)));
            });
    }

    /// <summary>A bone's armor vertices as a radial height field around their principal axis, in the bone's rest frame (fh_body_check.Solid).</summary>
    public sealed class LcSolid
    {
        public const double DS = 0.02;      // m along the axis
        public const int Sectors = 16;      // around it
        public readonly D3 C;
        public readonly D3[] A;             // the axes, most spread first
        public readonly double S0;
        public readonly int N;
        public readonly double[,] R;

        public LcSolid(D3[] v)
        {
            var c = D3.Zero;
            foreach (var p in v)
                c = c + p;
            C = c / v.Length;
            A = LcLinear.PrincipalAxes(v, C);
            var x = v.Select(Local).ToArray();
            double min = x.Min(p => p.X), max = x.Max(p => p.X);
            S0 = min - DS / 2;
            N = (int)((max - S0) / DS) + 2;
            var r = new double[N, Sectors];
            foreach (var p in x)
            {
                int i = (int)((p.X - S0) / DS);
                int j = Sector(p);
                r[i, j] = Math.Max(r[i, j], Hypot(p.Y, p.Z));
            }
            var e = (double[,])r.Clone();
            for (int a = 0; a < N; a++)
            {
                for (int b = 0; b < Sectors; b++)
                {
                    if (r[a, b] != 0)
                        continue;
                    double best = double.MaxValue;
                    void Take(double y)
                    {
                        if (y > 0)
                            best = Math.Min(best, y);
                    }
                    Take(r[a, (b + Sectors - 1) % Sectors]);
                    Take(r[a, (b + 1) % Sectors]);
                    if (a > 0)
                        Take(r[a - 1, b]);
                    if (a + 1 < N)
                        Take(r[a + 1, b]);
                    if (best < double.MaxValue)
                        e[a, b] = best;
                }
            }
            R = e;
        }

        static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        D3 Local(D3 p)
        {
            var d = p - C;
            return new D3(D3.Dot(d, A[0]), D3.Dot(d, A[1]), D3.Dot(d, A[2]));
        }

        static int Sector(D3 x) => ((int)((Math.Atan2(x.Z, x.Y) + Math.PI) / (2 * Math.PI) * Sectors)) % Sectors;

        /// <summary>How far a point (the bone's rest frame) is inside the height field, 0 outside it.</summary>
        public double Depth(D3 p)
        {
            var x = Local(p);
            int i = (int)Math.Floor((x.X - S0) / DS);
            if (i < 0 || i >= N)
                return 0;
            return Math.Max(R[i, Sector(x)] - Hypot(x.Y, x.Z), 0);
        }
    }

    /// <summary>One armor piece as the check reads it: its main FLVER and which piece it is (bd, am, lg, hd).</summary>
    public sealed class LcArmorPiece
    {
        public string Name;
        public string Piece;
        public FLVER2 Flver;
    }

    /// <summary>The armor worn, on the skeleton.</summary>
    public sealed class LcBody
    {
        public const string MovingRoot = "L_UpperArm";
        public static readonly string[] BladeSkip = { "own hand", "own forearm" };
        public static readonly string[] HollowBones = { "Head" };

        public readonly LcSkeleton Sk;
        public readonly bool[] MovingBone;      // the left arm from L_UpperArm down

        // blend skinning
        public readonly int Count;
        public readonly int[] Bi;               // [vertex * 4 + k]
        public readonly double[] W;
        public readonly D3[] L;                 // the vertex in each influence's rest frame
        public readonly int[] Top;
        public readonly int[] TopK;             // per vertex: which of its 4 influences is the strongest (Top's)
        public readonly string[] Labels;        // sorted
        public readonly int[] Lab;              // per vertex
        public readonly int[] Grp;              // per vertex: its shell (strongest bone and label)
        public readonly int[] GrpLab;           // per shell
        public readonly int[] StaticV, MovingV;

        // the arm's own armor against the right weapon (LcMeasure.ArmCross): its mesh's edges, two indices into MovingV each, and its
        // vertices by strongest bone with a ball around them (in the bone's frame; 5 cm over the rigid reach for the skinning); only the
        // vertices whose strongest bone is from L_UpperArm down
        public readonly int[] ArmE;
        public readonly ArmGroup[] ArmGroups;
        public sealed class ArmGroup
        {
            public int Bone;
            public D3 Centre;
            public double Reach;
            public int[] K;                     // indices into MovingV
            public int[] E;                     // the arm's edges whose first end is one of K, two indices into MovingV each
        }
        public const string ArmPart = "left arm on right weapon";

        // rigid per bone, for the inside test of points
        public readonly List<int> BodyBones = new List<int>();
        public readonly Dictionary<int, (D3[] v, D3[] n)> Rigid = new Dictionary<int, (D3[] v, D3[] n)>();
        public readonly Dictionary<int, string> Part = new Dictionary<int, string>();
        public readonly List<int> SolidBones = new List<int>();
        public readonly List<LcSolid> Solids = new List<LcSolid>();
        public readonly List<D3> SolidCentre = new List<D3>();
        public readonly List<double> SolidReach = new List<double>();
        public readonly HashSet<int> Hollow = new HashSet<int>();
        public readonly int[] StaticBones, MovingBones;

        /// <summary>The model mask group of a material ('#NN#' in its name), -1 for none (as the viewer reads it).</summary>
        public static int MaskGroup(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return -1;
            int a = materialName.IndexOf('#');
            if (a < 0)
                return -1;
            int b = materialName.IndexOf('#', a + 1);
            if (b < 0)
                return -1;
            return int.TryParse(materialName.Substring(a + 1, b - a - 1), out int g) ? g : -1;
        }

        /// <summary>The weapon in the right hand, an obstacle like the armor (null: none): a thin plate (LcItem.ThinPlate) hung where it's held.</summary>
        public readonly LcItem RightWeapon;
        public const string RightWeaponPart = "right weapon";

        /// <summary>
        /// meshShown: which meshes count (piece, mesh index), every mesh by default; the viewer leaves out the ones its draw mask hides,
        /// as the game does. rightWeapon: the right hand's weapon as a thin plate, hung (LcMeasure.RightCross measures the items against
        /// it, mesh against mesh).
        /// </summary>
        public LcBody(LcSkeleton sk, IList<LcArmorPiece> pieces, Func<LcArmorPiece, int, bool> meshShown = null, LcItem rightWeapon = null)
        {
            Sk = sk;
            RightWeapon = rightWeapon;
            MovingBone = new bool[sk.Count];
            int mr = sk.Bone(MovingRoot);
            if (mr >= 0)
                foreach (int b in sk.Subtree(mr))
                    MovingBone[b] = true;

            // ---- blend skinning (fh_shield.Skins)
            var bi = new List<int>();
            var w = new List<double>();
            var l = new List<D3>();
            var top = new List<int>();
            var topK = new List<int>();
            var label = new List<string>();
            var tris = new List<int[]>();
            foreach (var piece in pieces)
            {
                var f = piece.Flver;
                var toSkel = new int[f.Nodes.Count];
                for (int k = 0; k < f.Nodes.Count; k++)
                {
                    int a = k;
                    while (a >= 0 && !sk.Index.ContainsKey(f.Nodes[a].Name))
                        a = f.Nodes[a].ParentIndex;
                    toSkel[k] = a >= 0 ? sk.Index[f.Nodes[a].Name] : 0;
                }
                for (int mi = 0; mi < f.Meshes.Count; mi++)
                {
                    var m = f.Meshes[mi];
                    if (meshShown != null && !meshShown(piece, mi))
                        continue;
                    if (!LcFlver.HasTogether(f, m, FLVER.LayoutSemantic.Position, FLVER.LayoutSemantic.BoneWeights, FLVER.LayoutSemantic.BoneIndices))
                        continue;
                    int baseV = top.Count;
                    if (m.FaceSets.Count > 0)
                    {
                        var idx = m.FaceSets[0].Triangulate(false, true);
                        for (int i = 0; i + 2 < idx.Count; i += 3)
                            if (idx[i] < m.Vertices.Count && idx[i + 1] < m.Vertices.Count && idx[i + 2] < m.Vertices.Count)
                                tris.Add(new[] { baseV + idx[i], baseV + idx[i + 1], baseV + idx[i + 2] });
                    }
                    foreach (var v in m.Vertices)
                    {
                        var p = D3.From(v.Position);
                        var ws = new double[4];
                        double sum = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            ws[k] = Math.Max(v.BoneWeights[k], 0f);
                            sum += ws[k];
                        }
                        if (sum > 0)
                            for (int k = 0; k < 4; k++)
                                ws[k] /= sum;
                        else
                            ws = new double[] { 1, 0, 0, 0 };
                        int best = 0;
                        for (int k = 1; k < 4; k++)
                            if (ws[k] > ws[best])
                                best = k;
                        int topBone = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int fb = Math.Clamp(v.BoneIndices[k], 0, f.Nodes.Count - 1);
                            int b = toSkel[fb];
                            bi.Add(b);
                            w.Add(ws[k]);
                            l.Add(sk.RestQ[b].Conj().Rotate(p - sk.RestT[b]));
                            if (k == best)
                                topBone = b;
                        }
                        top.Add(topBone);
                        topK.Add(best);
                        string lab = LcParts.PartOf(sk.Names[topBone], 'L');
                        if (piece.Piece == "am" && (lab == "own hand" || lab == "own forearm"))
                            lab = "gauntlet";
                        label.Add(lab);
                    }
                }
            }
            Count = top.Count;
            Bi = bi.ToArray();
            W = w.ToArray();
            L = l.ToArray();
            Top = top.ToArray();
            TopK = topK.ToArray();
            Labels = label.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var labIndex = Labels.Select((x, i) => (x, i)).ToDictionary(t => t.x, t => t.i);
            Lab = label.Select(x => labIndex[x]).ToArray();
            var keys = Enumerable.Range(0, Count).Select(i => (long)Top[i] * Labels.Length + Lab[i]).ToArray();
            var uniq = keys.Distinct().OrderBy(k => k).ToArray();
            var grpIndex = uniq.Select((k, i) => (k, i)).ToDictionary(t => t.k, t => t.i);
            Grp = keys.Select(k => grpIndex[k]).ToArray();
            GrpLab = uniq.Select(k => (int)(k % Labels.Length)).ToArray();
            var mv = new List<int>();
            var st = new List<int>();
            for (int i = 0; i < Count; i++)
            {
                bool moving = false;
                for (int k = 0; k < 4; k++)
                    if (MovingBone[Bi[i * 4 + k]] && W[i * 4 + k] > 0)
                        moving = true;
                (moving ? mv : st).Add(i);
            }
            MovingV = mv.ToArray();
            StaticV = st.ToArray();

            // ---- the arm's own mesh against the right weapon: its edges (both ends on the arm) and its vertices by strongest bone. Only
            // the vertices whose strongest bone is the arm's: the chest and shoulder armor some arm weight moves (Spine2, L_Clavicle,
            // L_Pectoral) is the torso, which the right blade crosses whatever the left arm does
            var at = Enumerable.Repeat(-1, Count).ToArray();
            for (int k = 0; k < MovingV.Length; k++)
                if (MovingBone[Top[MovingV[k]]])
                    at[MovingV[k]] = k;
            var seen = new HashSet<long>();
            var armE = new List<int>();
            foreach (var t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int a = at[t[k]], b = at[t[(k + 1) % 3]];
                    if (a < 0 || b < 0 || a == b)
                        continue;
                    long key = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                    if (seen.Add(key))
                    {
                        armE.Add(a);
                        armE.Add(b);
                    }
                }
            ArmE = armE.ToArray();
            ArmGroups = MovingV.Select((i, k) => (i, k)).Where(x => MovingBone[Top[x.i]]).GroupBy(x => Top[x.i]).Select(g =>
            {
                var ks = g.Select(x => x.k).ToArray();
                var ps = g.Select(x => L[x.i * 4 + TopK[x.i]]).ToArray();
                var c = D3.Zero;
                foreach (var p in ps)
                    c = c + p;
                c = c / ps.Length;
                return new ArmGroup { Bone = g.Key, Centre = c, Reach = ps.Max(p => (p - c).Length()) + 0.05, K = ks };
            }).ToArray();
            var groupOf = new int[MovingV.Length];
            for (int g = 0; g < ArmGroups.Length; g++)
                foreach (int k in ArmGroups[g].K)
                    groupOf[k] = g;
            var perGroup = ArmGroups.Select(_ => new List<int>()).ToArray();
            for (int e = 0; e + 1 < ArmE.Length; e += 2)
            {
                perGroup[groupOf[ArmE[e]]].Add(ArmE[e]);
                perGroup[groupOf[ArmE[e]]].Add(ArmE[e + 1]);
            }
            for (int g = 0; g < ArmGroups.Length; g++)
                ArmGroups[g].E = perGroup[g].ToArray();

            // ---- rigid per bone (fh_body_check.load_body): each vertex on its strongest bone, cloth left out
            var acc = new Dictionary<int, (List<D3> v, List<D3> n)>();
            foreach (var piece in pieces)
            {
                var f = piece.Flver;
                var byName = new SortedDictionary<string, (List<D3> v, List<D3> n)>(StringComparer.Ordinal);
                for (int mi = 0; mi < f.Meshes.Count; mi++)
                {
                    var m = f.Meshes[mi];
                    if (meshShown != null && !meshShown(piece, mi))
                        continue;
                    if (!LcFlver.HasTogether(f, m, FLVER.LayoutSemantic.Position, FLVER.LayoutSemantic.Normal,
                                             FLVER.LayoutSemantic.BoneWeights, FLVER.LayoutSemantic.BoneIndices))
                        continue;
                    foreach (var v in m.Vertices)
                    {
                        int best = 0;
                        for (int k = 1; k < 4; k++)
                            if (v.BoneWeights[k] > v.BoneWeights[best])
                                best = k;
                        int fb = v.BoneIndices[best];
                        string name = fb >= 0 && fb < f.Nodes.Count ? f.Nodes[fb].Name : "";
                        if (!byName.TryGetValue(name, out var lists))
                            byName[name] = lists = (new List<D3>(), new List<D3>());
                        lists.v.Add(D3.From(v.Position));
                        lists.n.Add(D3.From(v.Normal));
                    }
                }
                foreach (var kv in byName)
                {
                    if (!sk.Index.TryGetValue(kv.Key, out int b))
                        continue;      // cloth: it hangs
                    var inv = sk.RestQ[b].Conj();
                    if (!acc.TryGetValue(b, out var lists))
                    {
                        acc[b] = lists = (new List<D3>(), new List<D3>());
                        BodyBones.Add(b);
                    }
                    foreach (var p in kv.Value.v)
                        lists.v.Add(inv.Rotate(p - sk.RestT[b]));
                    foreach (var n in kv.Value.n)
                        lists.n.Add(inv.Rotate(n));
                }
            }
            foreach (int b in BodyBones)
            {
                Rigid[b] = (acc[b].v.ToArray(), acc[b].n.ToArray());
                Part[b] = LcParts.PartOf(sk.Names[b], 'L');
            }
            foreach (int b in BodyBones)
            {
                var v = Rigid[b].v;
                if (v.Length < 30 || BladeSkip.Contains(Part[b]))
                    continue;
                SolidBones.Add(b);
                Solids.Add(new LcSolid(v));
                var c = D3.Zero;
                foreach (var p in v)
                    c = c + p;
                c = c / v.Length;
                SolidCentre.Add(c);
                SolidReach.Add(v.Max(p => (p - c).Length()) + LcSolid.DS);
            }
            foreach (var n in HollowBones)
                if (sk.Index.TryGetValue(n, out int hb))
                    Hollow.Add(hb);
            StaticBones = BodyBones.Where(b => !MovingBone[b] && !BladeSkip.Contains(Part[b])).ToArray();
            MovingBones = BodyBones.Where(b => MovingBone[b] && !BladeSkip.Contains(Part[b])).ToArray();
        }

        /// <summary>The skinned vertices' places at one frame (every vertex).</summary>
        public D3[] Posed(D3[,] wt, DQ[,] wq, int f)
        {
            var outV = new D3[Count];
            for (int i = 0; i < Count; i++)
            {
                var p = D3.Zero;
                for (int k = 0; k < 4; k++)
                {
                    double ww = W[i * 4 + k];
                    if (ww == 0)
                        continue;
                    int b = Bi[i * 4 + k];
                    p = p + ww * (wt[f, b] + wq[f, b].Rotate(L[i * 4 + k]));
                }
                outV[i] = p;
            }
            return outV;
        }
    }

    public enum LcItemKind
    {
        Plate,      // a shield: what crosses its face
        Points,     // anything else: its model's points inside the armor
    }

    /// <summary>The item in the left hand: its shape and where it hangs.</summary>
    public sealed class LcItem
    {
        public const double Bin = 0.01;
        public const int PointCount = 600;

        public string Name;
        public string Model;
        public LcItemKind Kind;
        public int Bone;                 // the bone it hangs on
        public D3 Origin;                // the model's origin in the bone's frame
        public M3 Axes;                  // the item's x, y, z in the bone's frame (columns): the model's, turned by Shape
        public M3 Shape = M3.Identity;   // the item's frame in the model's (ThinPlate: its principal axes)
        public string Hanging;           // how it hangs, for the user

        // plate: the face (smallest z) and back (largest z) per bin over the model's x / y
        public double BinSize = Bin;     // a shield's 1 cm; a thin plate's ThinBin
        public double X0, Y0;
        public int Nx, Ny;
        public double[,] Zf, Zb;
        public bool[,] Mask;
        public double[,] EdgeDist;       // per bin of the outline: how far its centre is from the outline's edge, in the plate (m)
        public double MidMean;           // the mid surface's mean height (z), for a point off the outline
        public double MidMin, MidMax;    // its lowest and highest over the outline (LcMeasure.ArmCross's cull)
        public D3 Half;
        public double Radius;

        // points
        public D3[] Points;
        public D3[] FastPoints;          // every third, for the fix's search
        public bool Required = true;     // the fix: must clear (else as far as the limits allow)

        // the whole mesh, in the item's frame (Shape applied), for the crossing test against the right weapon (LcMeasure.Crossing)
        public D3[] MeshV;
        public int[] MeshE;              // its edges, two vertex indices each
        public LcItem Thin;              // a weapon in the left hand: its own thin plate (Shape relative to this item's frame), hung with it

        /// <summary>A plate's bins when it is a thin plate: blades are a few cm wide, so 0.5 cm.</summary>
        public const double ThinBin = 0.005;

        /// <summary>A shield's plate from its model (fh_shield.plate): every triangle sampled 0.25 cm apart along its longest edge.</summary>
        public static LcItem Plate(string name, string model, FLVER2 f)
        {
            var p = LcFlver.RootedPositions(f, out var tris);
            var it = PlateFrom(name, model, p, tris, Bin);
            it.SetMesh(p, tris);
            return it;
        }

        /// <summary>
        /// Any item as a plate across its thinnest extent: the model turned onto its principal axes (most spread first, so z is the
        /// thinnest), the plate built in that frame (Shape: the model's points = Shape * the plate's). A blade is a plate across its
        /// flat; the right hand's weapon is measured so, as an obstacle (LcBody.RightWeapon), and a weapon in the left hand gets one
        /// (Thin) for the right weapon's edges.
        /// </summary>
        public static LcItem ThinPlate(string name, string model, FLVER2 f)
        {
            var p = LcFlver.RootedPositions(f, out var tris);
            var it = ThinPlateFrom(name, model, p, tris);
            it.SetMesh(it.MeshV, tris);
            return it;
        }

        static LcItem ThinPlateFrom(string name, string model, D3[] p, List<int[]> tris)
        {
            if (p.Length < 3)
                throw new InvalidOperationException($"{model}: the model has too few points");
            var c = D3.Zero;
            foreach (var x in p)
                c = c + x;
            c = c / p.Length;
            var a = LcLinear.PrincipalAxes(p, c);
            var shape = M3.FromColumns(a[0], a[1], D3.Cross(a[0], a[1]).Unit());
            var q = p.Select(x => shape.TransposeTimes(x)).ToArray();
            var it = PlateFrom(name, model, q, tris, ThinBin);
            it.Shape = shape;
            it.MeshV = q;
            return it;
        }

        void SetMesh(D3[] v, List<int[]> tris)
        {
            MeshV = v;
            var seen = new HashSet<long>();
            var e = new List<int>();
            foreach (var t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int a = t[k], b = t[(k + 1) % 3];
                    if (a == b)
                        continue;
                    long key = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                    if (seen.Add(key))
                    {
                        e.Add(a);
                        e.Add(b);
                    }
                }
            MeshE = e.ToArray();
        }

        static LcItem PlateFrom(string name, string model, D3[] p, List<int[]> tris, double bin)
        {
            var it = new LcItem { Name = name, Model = model, Kind = LcItemKind.Plate, BinSize = bin };
                        var s = new List<D3>();
            foreach (var t in tris)
            {
                D3 a = p[t[0]], b = p[t[1]], c = p[t[2]];
                double edge = Math.Max(D3.Distance(a, b), Math.Max(D3.Distance(b, c), D3.Distance(c, a)));
                int k = Math.Max(2, (int)Math.Ceiling(edge / 0.0025));
                for (int i = 0; i <= k; i++)
                    for (int j = 0; i + j <= k; j++)
                    {
                        double A = (double)i / k, B = (double)j / k;
                        s.Add(a * (1 - A - B) + b * A + c * B);
                    }
            }
            if (s.Count == 0)
                throw new InvalidOperationException($"{model}: the model has no triangles to make a plate from");
            it.X0 = s.Min(q => q.X) - bin;
            it.Y0 = s.Min(q => q.Y) - bin;
            it.Nx = (int)((s.Max(q => q.X) - it.X0) / bin) + 2;
            it.Ny = (int)((s.Max(q => q.Y) - it.Y0) / bin) + 2;
            it.Zf = new double[it.Nx, it.Ny];
            it.Zb = new double[it.Nx, it.Ny];
            it.Mask = new bool[it.Nx, it.Ny];
            for (int i = 0; i < it.Nx; i++)
                for (int j = 0; j < it.Ny; j++)
                {
                    it.Zf[i, j] = double.PositiveInfinity;
                    it.Zb[i, j] = double.NegativeInfinity;
                }
            foreach (var q in s)
            {
                int i = (int)((q.X - it.X0) / bin), j = (int)((q.Y - it.Y0) / bin);
                it.Zf[i, j] = Math.Min(it.Zf[i, j], q.Z);
                it.Zb[i, j] = Math.Max(it.Zb[i, j], q.Z);
                it.Mask[i, j] = true;
            }
            it.Half = new D3(it.X0 + it.Nx * bin / 2, it.Y0 + it.Ny * bin / 2, 0);
            it.Radius = (it.Half - new D3(it.X0, it.Y0, 0)).Length() + 0.12;
            // the outline's in-plane distance to its edge (two-pass chamfer, 1 / sqrt 2 steps; off the grid is outside)
            var d = new double[it.Nx, it.Ny];
            double mids = 0;
            int count = 0;
            for (int i = 0; i < it.Nx; i++)
                for (int j = 0; j < it.Ny; j++)
                {
                    d[i, j] = it.Mask[i, j] ? double.MaxValue : 0;
                    if (it.Mask[i, j])
                    {
                        mids += (it.Zf[i, j] + it.Zb[i, j]) / 2;
                        count++;
                    }
                }
            double At(int i, int j) => i < 0 || j < 0 || i >= it.Nx || j >= it.Ny ? 0 : d[i, j];
            const double R2 = 1.4142135623730951;
            for (int i = 0; i < it.Nx; i++)
                for (int j = 0; j < it.Ny; j++)
                    if (it.Mask[i, j])
                        d[i, j] = Math.Min(d[i, j], Math.Min(Math.Min(At(i - 1, j) + 1, At(i, j - 1) + 1),
                                                             Math.Min(At(i - 1, j - 1) + R2, At(i - 1, j + 1) + R2)));
            for (int i = it.Nx - 1; i >= 0; i--)
                for (int j = it.Ny - 1; j >= 0; j--)
                    if (it.Mask[i, j])
                        d[i, j] = Math.Min(d[i, j], Math.Min(Math.Min(At(i + 1, j) + 1, At(i, j + 1) + 1),
                                                             Math.Min(At(i + 1, j + 1) + R2, At(i + 1, j - 1) + R2)));
            it.EdgeDist = new double[it.Nx, it.Ny];
            for (int i = 0; i < it.Nx; i++)
                for (int j = 0; j < it.Ny; j++)
                    it.EdgeDist[i, j] = it.Mask[i, j] ? (d[i, j] - 0.5) * bin : 0;
            it.MidMean = count > 0 ? mids / count : 0;
            it.MidMin = it.MidMax = it.MidMean;
            for (int i = 0; i < it.Nx; i++)
                for (int j = 0; j < it.Ny; j++)
                    if (it.Mask[i, j])
                    {
                        double m = (it.Zf[i, j] + it.Zb[i, j]) / 2;
                        it.MidMin = Math.Min(it.MidMin, m);
                        it.MidMax = Math.Max(it.MidMax, m);
                    }
            return it;
        }

        /// <summary>
        /// Any other item as its model's points, about PointCount of them (fh_body_check.read_weapon, left_clear's subsample); its whole
        /// mesh and its thin plate for the right weapon.
        /// </summary>
        public static LcItem PointsOf(string name, string model, FLVER2 f)
        {
            var p = LcFlver.RootedPositions(f, out var tris);
            int step = Math.Max(1, p.Length / PointCount);
            var pts = new List<D3>();
            for (int i = 0; i < p.Length; i += step)
                pts.Add(p[i]);
            var it = new LcItem { Name = name, Model = model, Kind = LcItemKind.Points, Points = pts.ToArray(),
                                  FastPoints = pts.Where((_, i) => i % 3 == 0).ToArray() };
            it.SetMesh(p, tris);
            if (p.Length >= 3 && tris.Count > 0)
                it.Thin = ThinPlateFrom(name, model, p, tris);
            return it;
        }

        /// <summary>Hung on a dummy of the character's model: +z on the dummy's forward, +y on its up (left_clear.shield_dummy).</summary>
        public void HangOnDummy(LcSkeleton sk, FLVER2 character, int dummyId)
        {
            var d = character.Dummies.FirstOrDefault(x => x.ReferenceID == dummyId)
                    ?? throw new InvalidOperationException($"the character's model has no dummy {dummyId}");
            if (d.AttachBoneIndex < 0 || d.AttachBoneIndex >= character.Nodes.Count)
                throw new InvalidOperationException($"dummy {dummyId} is attached to no bone");
            string boneName = character.Nodes[d.AttachBoneIndex].Name;
            Bone = sk.Bone(boneName);
            if (Bone < 0)
                throw new InvalidOperationException($"dummy {dummyId}'s bone {boneName} is not in the skeleton");
            var (t, r) = LcFlver.ArmatureSpace(character, d.AttachBoneIndex);
            var rt = r.Transpose();
            var fw = rt * D3.From(d.Forward).Unit();
            var up = rt * D3.From(d.Upward).Unit();
            up = (up - D3.Dot(up, fw) * fw).Unit();
            Origin = rt * (D3.From(d.Position) - t);
            Axes = M3.FromColumns(D3.Cross(up, fw), up, fw) * Shape;
            Hanging = $"dummy {dummyId} on {boneName}";
            HangThin();
        }

        // the thin plate hangs with its item
        void HangThin()
        {
            if (Thin == null)
                return;
            Thin.Bone = Bone;
            Thin.Origin = Origin;
            Thin.Axes = Axes * Thin.Shape;
            Thin.Hanging = Hanging;
        }

        /// <summary>Hung on a bone with a fixed turn: a left weapon on L_Weapon, the model turned 180 degrees about +X from R_Weapon's rule.</summary>
        public void HangOnBone(LcSkeleton sk, string boneName, M3 axes)
        {
            Bone = sk.Bone(boneName);
            if (Bone < 0)
                throw new InvalidOperationException($"the skeleton has no bone {boneName}");
            Origin = D3.Zero;
            Axes = axes * Shape;
            Hanging = boneName;
            HangThin();
        }

        /// <summary>The left weapon's rule on L_Weapon: the model's x, y, z on the bone's -X, +Y, -Z.</summary>
        public static readonly M3 LeftWeaponAxes = new M3(-1, 0, 0, 0, 1, 0, 0, 0, -1);

        /// <summary>The model's origin and axes in armature space at one frame.</summary>
        public (D3 o, M3 r) World(D3[,] wt, DQ[,] wq, int f)
        {
            var rb = wq[f, Bone].ToM3();
            return (wt[f, Bone] + rb * Origin, rb * Axes);
        }

        public (D3 o, M3 r) World(D3[] wt, DQ[] wq)
        {
            var rb = wq[Bone].ToM3();
            return (wt[Bone] + rb * Origin, rb * Axes);
        }
    }
}
