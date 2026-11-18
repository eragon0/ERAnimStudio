// LeftClearCheck: the editor's left-hand clearance measure run on files, for checking it outside the editor.
//   LeftClearCheck measure <folder> <hkx name> [--mirror] [--dummies] [--json <out.json>]
//     <folder> holds Skeleton.hkx, c0000.flver (the character's dummies), the armor's FLVERs (bd_*, am_*, lg_*, hd_*), the items'
//     FLVERs (wp_a_*.flver) and <hkx name>.hkx (with <hkx name>.compendium or dual.compendium when its types are in a compendium).
//     Items: the shields hang on dummy 23, the weapons on L_Weapon (the pack's left_clear.py); --dummies: each on the dummy the editor
//     hangs it on (the Distinguished Greatshield on 98, the weapons on 21); --mirror measures the animation as a mirrored clip plays it.
//     Prints each item's frames over 1 cm per body part (as the pack's left_clear.py measure) and writes the per-frame rows (cm).
//   LeftClearCheck info <folder>     the skeleton, the armor's vertices and shells, the items' shapes and where they hang
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DSAnimStudio.HkxEdit;
using DSAnimStudio.LeftClear;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

static string Opt(List<string> a, string name)
{
    int i = a.IndexOf(name);
    if (i < 0) return null;
    string v = a[i + 1];
    a.RemoveRange(i, 2);
    return v;
}

static bool Flag(List<string> a, string name) => a.Remove(name);

static HkxSkeleton Skeleton(string path)
{
    var hkx = HKX.GenFakeFromTagFile(File.ReadAllBytes(path));
    var sk = hkx.DataSection.Objects.OfType<HKX.HKASkeleton>().FirstOrDefault()
             ?? throw new InvalidDataException($"{path} holds no skeleton");
    return HkxSkeleton.FromHavok(sk);
}

// the pack's reference set (left_clear.py ITEMS and ARMOR), in its order
var items = new (string name, string kind, string model)[]
{
    ("Heater Shield", "shield", "wp_a_2020"), ("Kite Shield", "shield", "wp_a_2033"), ("Distinguished Greatshield", "shield", "wp_a_2010"),
    ("Longsword", "blade", "wp_a_0200"), ("Claymore", "blade", "wp_a_0601"),
};
var armor = new[] { "bd_m_1501", "am_m_1500", "lg_m_1500", "hd_m_1500" };

// --dummies: each item hung on the dummy the game's WepAbsorpPosParam gives it (the editor's placement: the shields on 23, the
// Distinguished Greatshield on 98, the weapons on 21) instead of left_clear.py's (every shield on 23, the weapons on L_Weapon by a fixed turn)
bool useDummies = args.Contains("--dummies");
var dummyOf = new Dictionary<string, int> { ["wp_a_2020"] = 23, ["wp_a_2033"] = 23, ["wp_a_2010"] = 98, ["wp_a_0200"] = 21, ["wp_a_0601"] = 21 };

(LcSkeleton sk, LcBody body, List<LcItem> its) Load(string folder)
{
    var sk = new LcSkeleton(Skeleton(Path.Combine(folder, "Skeleton.hkx")));
    var pieces = armor.Select(a => new LcArmorPiece { Name = a, Piece = a.Substring(0, 2), Flver = FLVER2.Read(Path.Combine(folder, a + ".flver")) }).ToList();
    var body = new LcBody(sk, pieces);
    var chr = FLVER2.Read(Path.Combine(folder, "c0000.flver"));
    var list = new List<LcItem>();
    foreach (var (name, kind, model) in items)
    {
        var f = FLVER2.Read(Path.Combine(folder, model + ".flver"));
        LcItem it;
        if (kind == "shield")
        {
            it = LcItem.Plate(name, model, f);
            it.HangOnDummy(sk, chr, useDummies ? dummyOf[model] : 23);
        }
        else
        {
            it = LcItem.PointsOf(name, model, f);
            if (useDummies)
                it.HangOnDummy(sk, chr, dummyOf[model]);
            else
                it.HangOnBone(sk, "L_Weapon", LcItem.LeftWeaponAxes);
        }
        list.Add(it);
    }
    return (sk, body, list);
}

var args2 = args.ToList();
try
{
    string cmd = args2.Count > 0 ? args2[0] : "";
    if (args2.Count > 0)
        args2.RemoveAt(0);
    if (cmd == "info")
    {
        string folder = args2[0];
        var t0 = DateTime.Now;
        var (sk, body, its) = Load(folder);
        Console.WriteLine($"skeleton: {sk.Count} bones; loaded in {(DateTime.Now - t0).TotalSeconds:0.0} s");
        Console.WriteLine($"armor: {body.Count} skinned vertices ({body.MovingV.Length} on the left arm), {body.GrpLab.Length} shells, labels {string.Join(", ", body.Labels)}");
        Console.WriteLine($"rigid: {body.BodyBones.Count} bones, {body.Rigid.Values.Sum(x => x.v.Length)} vertices, {body.Solids.Count} height fields; static bones {body.StaticBones.Length}, moving {body.MovingBones.Length}");
        var chrInfo = FLVER2.Read(Path.Combine(folder, "c0000.flver"));
        foreach (int dm in new[] { 21, 23, 98 })
        {
            var probe = new LcItem();
            try
            {
                probe.HangOnDummy(sk, chrInfo, dm);
                Console.WriteLine($"dummy {dm}: on {probe.Hanging}, origin {probe.Origin}, axes x {probe.Axes.Column(0)} y {probe.Axes.Column(1)} z {probe.Axes.Column(2)}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"dummy {dm}: {e.Message}");
            }
        }
        if (Flag(args2, "--axes"))
            for (int k = 0; k < body.Solids.Count; k++)
            {
                var s = body.Solids[k];
                Console.WriteLine($"solid {sk.Names[body.SolidBones[k]]}: axes {s.A[0]} {s.A[1]} {s.A[2]} s0 {s.S0:0.0000} n {s.N}");
            }
        foreach (var it in its)
        {
            var f = FLVER2.Read(Path.Combine(folder, it.Model + ".flver"));
            var root = f.Nodes.Count > 0 ? f.Nodes[0] : null;
            string shape = it.Kind == LcItemKind.Plate ? $"plate {it.Nx} x {it.Ny} bins from ({it.X0:0.###}, {it.Y0:0.###}), radius {it.Radius:0.###}" : $"{it.Points.Length} points";
            Console.WriteLine($"{it.Name} ({it.Model}): {shape}; on {it.Hanging}, origin {it.Origin}, axes x {it.Axes.Column(0)} y {it.Axes.Column(1)} z {it.Axes.Column(2)}; root bone {root?.Name} t {root?.Translation} r {root?.Rotation}");
        }
        return 0;
    }
    if (cmd == "measure")
    {
        string json = Opt(args2, "--json");
        bool mirror = Flag(args2, "--mirror");
        Flag(args2, "--dummies");
        string folder = args2[0], name = args2[1];
        var t0 = DateTime.Now;
        var (sk, body, its) = Load(folder);
        double tLoad = (DateTime.Now - t0).TotalSeconds;
        string compPath = new[] { name + ".compendium", "dual.compendium" }.Select(c => Path.Combine(folder, c)).FirstOrDefault(File.Exists);
        byte[] hkx = File.ReadAllBytes(Path.Combine(folder, name + ".hkx"));
        HkxAnimation a;
        try
        {
            a = HkxAnimationFile.Read(hkx).Decoded;
        }
        catch (InvalidDataException) when (compPath != null)
        {
            a = HkxAnimationFile.Read(hkx, File.ReadAllBytes(compPath)).Decoded;
        }
        if (mirror)
            a = HkxAnimationOps.Mirror(sk.Hkx, a);
        var (wt, wq) = sk.Pose(a);
        var m = new LcMeasure(body);
        string onlyFrame = Opt(args2, "--hits");      // --hits <frame>:<item index>: that frame's crossing points
        if (onlyFrame != null)
        {
            int hf = int.Parse(onlyFrame.Split(':')[0]), hi = int.Parse(onlyFrame.Split(':')[1]);
            var it = its[hi];
            var fr = m.Prepare(wt, wq, hf, it.Kind == LcItemKind.Plate, it.Kind == LcItemKind.Points);
            var hits = new List<LcHit>();
            m.Depths(fr, it, wt, wq, hits);
            foreach (var h in hits.OrderBy(h => h.Index))
                Console.WriteLine($"point {h.Index}: {h.Depth * 100:0.00} cm, {h.Part}");
            return 0;
        }
        var t1 = DateTime.Now;
        var rows = m.Run(wt, wq, its);
        double tRun = (DateTime.Now - t1).TotalSeconds;
        Console.WriteLine($"== {name}: {rows.Length} frames" + (mirror ? ", mirrored as a mirrored clip plays it" : "") + $" (loaded in {tLoad:0.0} s, measured in {tRun:0.0} s)");
        for (int i = 0; i < its.Count; i++)
        {
            var parts = new Dictionary<string, (int n, double deep, int at)>();
            int frames = 0;
            for (int f = 0; f < rows.Length; f++)
            {
                var d = rows[f][i];
                if (d.Values.Any(v => v > 0.01))
                    frames++;
                foreach (var (p, v) in d)
                    if (v > 0.01)
                    {
                        var (n, deep, at) = parts.TryGetValue(p, out var x) ? x : (0, 0.0, -1);
                        parts[p] = (n + 1, Math.Max(deep, v), v > deep ? f : at);
                    }
            }
            string req = i == 2 || i == 4 ? "best effort" : "required";
            string text = string.Join("; ", parts.OrderByDescending(kv => kv.Value.deep).Select(kv => $"{kv.Key} {kv.Value.n} frames, {Math.Round(kv.Value.deep * 100, 1):0.0} cm (frame {kv.Value.at})"));
            Console.WriteLine($"   {its[i].Name} ({req}): {frames} frames over 1 cm: {(text.Length > 0 ? text : "clear")}");
        }
        if (json != null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\": \"").Append(name).Append("\", \"frames\": ").Append(rows.Length).Append(", \"mirrored\": ").Append(mirror ? "true" : "false").Append(", \"rows\": [\n");
            for (int f = 0; f < rows.Length; f++)
            {
                sb.Append('{');
                sb.Append(string.Join(", ", its.Select((it, i) => $"\"{it.Name}\": {{" + string.Join(", ", rows[f][i].Select(kv => $"\"{kv.Key}\": {Math.Round(kv.Value * 100, 2)}")) + "}")));
                sb.Append(f + 1 < rows.Length ? "},\n" : "}\n");
            }
            sb.Append("]}\n");
            File.WriteAllText(json, sb.ToString());
        }
        return 0;
    }
    Console.WriteLine("LeftClearCheck measure <folder> <hkx name> [--mirror] [--json <out.json>]");
    Console.WriteLine("LeftClearCheck info <folder>");
    return 1;
}
catch (Exception e)
{
    Console.WriteLine($"FAILED: {e.GetType().Name}: {e.Message}");
    Console.WriteLine(e.StackTrace);
    return 2;
}
