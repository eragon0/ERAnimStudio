// HkxEditCheck: the editor's hkx editing run on files, for checking it outside the editor.
//   HkxEditCheck edit <source.hkx> <Skeleton.hkx> <out.hkx> [--first N] [--last N] [--mirror] [--compendium <file>] [--uncompressed <file>]
//     frames are the editor's (30 fps); the output is the spline-compressed file Havok Content Tools 2018.1 writes
//   HkxEditCheck decode <hkx> [--compendium <file>]    prints frames, tracks, duration, root motion and the animation's type
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DSAnimStudio.HkxEdit;
using SoulsAssetPipeline.Animation;

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

var args2 = args.ToList();
try
{
    string cmd = args2.Count > 0 ? args2[0] : "";
    args2.RemoveAt(0);
    string comp = Opt(args2, "--compendium");
    byte[] compBytes = comp != null ? File.ReadAllBytes(comp) : null;
    if (cmd == "decode")
    {
        var f = HkxAnimationFile.Read(File.ReadAllBytes(args2[0]), compBytes);
        var a = f.Decoded;
        Console.WriteLine($"{f.Animation.Type.Name}: {a.Frames} frames, {a.Tracks} tracks, {a.Duration:0.####} s ({a.Fps:0.###} fps), skeleton {a.SkeletonName}, "
                          + (a.Root == null ? "no root motion" : $"root motion ends at {a.Root[^1]} ({f.ReferenceFrame.Type.Name})"));
        var t = f.Animation.Type;
        Console.WriteLine($"type {t.Name}: flags {t.Flags}, format 0x{t.FormatInfo:X}, version {t.Version}, {t.ByteSize} bytes, align {t.Alignment}; parent {t.ParentType?.Name} {t.ParentType?.ByteSize} bytes");
        foreach (var fl in t.AllFields)
            Console.WriteLine($"  {fl.Name} @{fl.ByteOffset} flags {(int)fl.Flags} {fl.Type?.Name}");
        return 0;
    }
    if (cmd == "ease")   // a crop with and without the body eased onto the character, compared in world space
    {
        var sk = Skeleton(args2[1]);
        var hct = HctCompressor.Find(out string why) ?? throw new Exception(why);
        int? f0 = int.Parse(args2[2]), f1 = args2.Count > 3 ? int.Parse(args2[3]) : null;
        bool mir = Flag(args2, "--mirror");
        byte[] src = File.ReadAllBytes(args2[0]);
        var plain = HkxAnimationFile.Read(HkxEditPipeline.Make(src, compBytes, sk, f0, f1, mir, hct, out _, easeRoot: false)).Decoded;
        var eased = HkxAnimationFile.Read(HkxEditPipeline.Make(src, compBytes, sk, f0, f1, mir, hct, out var rep, easeRoot: true)).Decoded;
        // world positions of every bone: the root motion (a move, then a turn about up) applied to the armature-space pose
        System.Numerics.Vector3[,] World(HkxAnimation a)
        {
            var (wt, _) = sk.ToWorld(sk.FullPose(a).t, sk.FullPose(a).q);
            var w = new System.Numerics.Vector3[a.Frames, sk.Count];
            for (int f = 0; f < a.Frames; f++)
            {
                var r = a.Root?[f] ?? System.Numerics.Vector4.Zero;
                var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, r.W);
                for (int b = 0; b < sk.Count; b++)
                    w[f, b] = new System.Numerics.Vector3(r.X, r.Y, r.Z) + System.Numerics.Vector3.Transform(wt[f, b], q);
            }
            return w;
        }
        var wp = World(plain);
        var we = World(eased);
        int pelvis = sk.Index["Pelvis"], master = Array.FindIndex(sk.Parents, p => p < 0);
        // rigid motion: every distance from the first frame's pelvis is the same in both
        double rigid = 0;
        for (int f = 0; f < plain.Frames; f++)
            for (int b = 0; b < sk.Count; b++)
                rigid = Math.Max(rigid, Math.Abs((wp[f, b] - wp[0, pelvis]).Length() - (we[f, b] - we[0, pelvis]).Length()));
        int km = Array.IndexOf(eased.Bones, (short)master), kp = Array.IndexOf(plain.Bones, (short)master);
        var start = eased.T[0, km];
        var endE = eased.T[eased.Frames - 1, km];
        var endP = plain.T[plain.Frames - 1, kp];
        Console.WriteLine($"moved into the root motion: {rep.EasedOffset * 100:0.0} cm (the root bone's turn of {rep.EasedTurn:0.0} deg left with it)");
        Console.WriteLine($"root bone at the first frame: plain {new System.Numerics.Vector2(plain.T[0, kp].X, plain.T[0, kp].Z).Length() * 100:0.0} cm off, eased {new System.Numerics.Vector2(start.X, start.Z).Length() * 100:0.0} cm off");
        Console.WriteLine($"root bone at the last frame, eased against plain: {(endE - endP).Length() * 100:0.00} cm apart");
        Console.WriteLine($"root motion at the first frame, eased: {eased.Root[0]}; at the last frame: plain {plain.Root[^1]}, eased {eased.Root[^1]}");
        Console.WriteLine($"body motion rigid between the two (largest change of a distance): {rigid * 1000:0.0} mm");
        return 0;
    }
    if (cmd == "objects")   // the root's variants and the classes reachable from it
    {
        var root = Havoc.IO.Tagfile.Binary.HkBinaryTagfileReader.Read(File.ReadAllBytes(args2[0]), compBytes);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var counts = new Dictionary<string, int>();
        void Walk(Havoc.Objects.IHkObject o, string path)
        {
            if (o == null || !seen.Add(o)) return;
            if (o is Havoc.Objects.HkClass c)
            {
                if (c.Type.Name.StartsWith("hka") && c.Type.Name.EndsWith("Animation"))
                    Console.WriteLine($"  {c.Type.Name} at {path}");
                counts[c.Type.Name] = counts.GetValueOrDefault(c.Type.Name) + 1;
                foreach (var kv in c.Value) Walk(kv.Value, path + "." + kv.Key.Name);
            }
            else if (o is Havoc.Objects.HkPtr p) Walk(p.Value, path);
            else if (o is Havoc.Objects.HkArray a) { int i = 0; foreach (var x in HkxAnimationFile.Items(a)) Walk(x, path + "[" + i++ + "]"); }
        }
        Walk(root, "root");
        foreach (var v in HkxAnimationFile.Items(HkxAnimationFile.Field((Havoc.Objects.HkClass)root, "namedVariants")))
            Console.WriteLine("variant " + string.Join(", ", ((Havoc.Objects.HkClass)v).Value.Select(kv => kv.Key.Name + "=" + (kv.Value is Havoc.Objects.HkString s ? s.Value : (kv.Value as Havoc.Objects.HkPtr)?.Value?.Type?.Name))));
        foreach (var kv in counts.Where(kv => !kv.Key.StartsWith("hkQsTransform") && kv.Value < 1000 || kv.Key.Contains("Animation")).OrderBy(kv => kv.Key))
            Console.WriteLine($"  {kv.Key}: {kv.Value}");
        Console.WriteLine($"  (hkQsTransform: {counts.GetValueOrDefault("hkQsTransform")})");
        return 0;
    }
    if (cmd == "rewrite")   // read and write back unchanged through Havoc
    {
        var root = Havoc.IO.Tagfile.Binary.HkBinaryTagfileReader.Read(File.ReadAllBytes(args2[0]), compBytes);
        using var ms = new MemoryStream();
        Havoc.IO.Tagfile.Binary.HkBinaryTagfileWriter.Write(ms, true, root, Havoc.HkSdkVersion.V20180100);
        File.WriteAllBytes(args2[1], ms.ToArray());
        Console.WriteLine($"rewritten {args2[1]}: {ms.Length} bytes");
        return 0;
    }
    if (cmd == "hct")   // Havok Content Tools' compression alone
    {
        var hct = HctCompressor.Find(out string why) ?? throw new Exception(why);
        string work = HctCompressor.ScratchFolder();
        File.WriteAllBytes(args2[1], HctCompressor.Compress(hct, File.ReadAllBytes(args2[0]), work));
        Console.WriteLine($"compressed {args2[1]}");
        return 0;
    }
    if (cmd == "edit")
    {
        string fs = Opt(args2, "--first"), ls = Opt(args2, "--last"), unc = Opt(args2, "--uncompressed");
        bool mirror = Flag(args2, "--mirror");
        var sk = Skeleton(args2[1]);
        var hct = HctCompressor.Find(out string why) ?? throw new Exception(why);
        if (unc != null)
        {
            var f = HkxAnimationFile.Read(File.ReadAllBytes(args2[0]), compBytes);
            var a = f.Decoded;
            File.WriteAllBytes(unc, HkxInterleavedWriter.Write(f, a, a.Bones.Select(b => sk.Names[b]).ToList()));
        }
        var t0 = DateTime.Now;
        byte[] outBytes = HkxEditPipeline.Make(File.ReadAllBytes(args2[0]), compBytes, sk, fs == null ? null : int.Parse(fs),
                                               ls == null ? null : int.Parse(ls), mirror, hct, out var rep);
        File.WriteAllBytes(args2[2], outBytes);
        Console.WriteLine($"written {args2[2]}: frames {rep.First}-{rep.Last} (file {rep.FileFirst}-{rep.FileLast} at {rep.Fps:0.###} fps), "
                          + $"{rep.SourceFrames} -> {rep.Frames} frames{(rep.Mirrored ? ", mirrored" : "")}, read back within {rep.ReadBackError * 1000:0.0} mm, "
                          + $"{rep.Bytes} bytes, HCT {rep.HctVersion} ({hct.Source}), {(DateTime.Now - t0).TotalSeconds:0.0} s");
        return 0;
    }
    Console.WriteLine("HkxEditCheck edit <source.hkx> <Skeleton.hkx> <out.hkx> [--first N] [--last N] [--mirror] [--compendium <file>] [--uncompressed <file>]");
    Console.WriteLine("HkxEditCheck decode <hkx> [--compendium <file>]");
    return 1;
}
catch (Exception e)
{
    Console.WriteLine($"FAILED: {e.GetType().Name}: {e.Message}");
    Console.WriteLine(e.StackTrace);
    return 2;
}
