using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>A refusal: nothing was written, and the message says why.</summary>
    public sealed class HkxEditRefused : Exception
    {
        public HkxEditRefused(string message) : base(message) { }
    }

    /// <summary>
    /// Adds an animation to a split animation binder (c0000_aNx.anibnd.dcx): aTTT_AAAAAA.hkx with id 1,000,000,000 + TTT x 1,000,000 +
    /// AAAAAA, in the folder of the binder's other hkx, the binder kept in its own DCX compression. Refused, nothing written: a name or
    /// id the binder has already, an hkx whose types come from a compendium the binder lacks, a binder that changes while the file is
    /// added (another program writing it). With moveTo, the original's own file (byte for byte the source given) is renamed to
    /// moveTo and the new file takes its name; an original the binder doesn't have is left alone and the new file is written as
    /// moveTo.
    /// </summary>
    public static class HkxBinderWriter
    {
        static readonly Regex AnimName = new Regex(@"^a(\d{3})_(\d{6})$");

        public sealed class Result
        {
            public string Name;          // the name the new file was written under
            public int Id;
            public string PathInBinder;
            public bool Swapped;         // moveTo: the original was renamed and the new file took its name
            public string CompendiumName;   // the binder's compendium when the new file takes its types from it
            public int FileCount;
        }

        public static int FileId(string name)
        {
            var m = AnimName.Match(name);
            if (!m.Success)
                throw new HkxEditRefused($"{name}: not an animation name aTTT_AAAAAA");
            return 1000000000 + int.Parse(m.Groups[1].Value) * 1000000 + int.Parse(m.Groups[2].Value);
        }

        static string BaseName(string path) => path.Replace('/', '\\').Split('\\').Last();

        public static Result Add(string binderPath, byte[] hkx, string name, string moveTo = null, byte[] original = null)
        {
            if (!File.Exists(binderPath))
                throw new HkxEditRefused($"no binder at {binderPath}");
            if (!Regex.IsMatch(Path.GetFileName(binderPath), @"^c\d{4}_\w+\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                throw new HkxEditRefused($"{Path.GetFileName(binderPath)}: not a split animation binder (c0000_aNx.anibnd.dcx)");
            int id = FileId(name);
            int moveId = moveTo != null ? FileId(moveTo) : 0;
            var before = new FileInfo(binderPath);
            long size = before.Length;
            DateTime time = before.LastWriteTimeUtc;
            var bnd = BND4.Read(File.ReadAllBytes(binderPath));
            var have = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in bnd.Files)
                have.TryAdd(BaseName(f.Name), f);
            string bn = Path.GetFileName(binderPath);

            BinderFile moved = null;
            if (moveTo != null)
            {
                if (have.ContainsKey(moveTo + ".hkx") || bnd.Files.Any(f => f.ID == moveId))
                    throw new HkxEditRefused($"{moveTo}.hkx (id {moveId}) is already in {bn}");
                if (have.TryGetValue(name + ".hkx", out moved))
                {
                    if (moved.ID != id)
                        throw new HkxEditRefused($"{bn} has {name}.hkx under id {moved.ID}, not {id}: check it");
                    if (original == null || !moved.Bytes.AsSpan().SequenceEqual(original))
                        throw new HkxEditRefused($"{name}.hkx in {bn} is not the file the program plays (saved since it was loaded?): reload the project");
                }
                else
                {
                    name = moveTo;
                    id = moveId;
                }
            }
            if (moved == null)
            {
                if (have.ContainsKey(name + ".hkx"))
                    throw new HkxEditRefused($"{name}.hkx is already in {bn}");
                if (bnd.Files.Any(f => f.ID == id))
                    throw new HkxEditRefused($"id {id} ({name}) is taken in {bn}");
            }

            var comp = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase));
            var offered = comp != null ? CompendiumIds(comp.Bytes) : new HashSet<ulong>();
            var refs = TypeSetReferences(hkx);
            if (!refs.IsSubsetOf(offered))
                throw new HkxEditRefused($"the hkx takes its types from a compendium {bn} lacks: it would not load in game");
            var folderOf = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)) ?? comp
                ?? throw new HkxEditRefused($"{bn} holds no hkx or compendium to take the folder from");
            string folder = folderOf.Name.Substring(0, folderOf.Name.Replace('/', '\\').LastIndexOf('\\') + 1);
            string path = folder + name + ".hkx";
            if (moved != null)
            {
                string movedFolder = moved.Name.Substring(0, moved.Name.Replace('/', '\\').LastIndexOf('\\') + 1);
                moved.ID = moveId;
                moved.Name = movedFolder + moveTo + ".hkx";
            }
            bnd.Files.Add(new BinderFile(folderOf.Flags, id, path, hkx));   // the flags the binder's other files have
            bnd.Files.Sort((a, b) => a.ID.CompareTo(b.ID));
            byte[] written = bnd.Write();

            var now = new FileInfo(binderPath);
            if (now.Length != size || now.LastWriteTimeUtc != time)
                throw new HkxEditRefused($"{bn} changed while the copy was made (another program writing it?): nothing written");
            string tmp = binderPath + ".hkxedit.tmp";
            File.WriteAllBytes(tmp, written);
            now = new FileInfo(binderPath);
            if (now.Length != size || now.LastWriteTimeUtc != time)
            {
                File.Delete(tmp);
                throw new HkxEditRefused($"{bn} changed while it was written: nothing written");
            }
            File.Move(tmp, binderPath, overwrite: true);

            // read back
            var back = BND4.Read(File.ReadAllBytes(binderPath));
            var got = back.Files.Where(f => f.ID == id).ToList();
            if (back.Files.Count != bnd.Files.Count || got.Count != 1 || got[0].Name != path || !got[0].Bytes.AsSpan().SequenceEqual(hkx))
                throw new HkxEditRefused($"{bn} read back without the new file as written: check it");
            if (moved != null)
            {
                var mg = back.Files.Where(f => f.ID == moveId).ToList();
                if (mg.Count != 1 || !mg[0].Bytes.AsSpan().SequenceEqual(original))
                    throw new HkxEditRefused($"{bn} read back without {moveTo}.hkx as moved: check it");
            }
            return new Result
            {
                Name = name, Id = id, PathInBinder = path, Swapped = moved != null,
                CompendiumName = refs.Count > 0 ? comp?.Name : null, FileCount = back.Files.Count,
            };
        }

        // ---- tagfile chunks: u32 big-endian size (flag bits on top), then a four-character tag

        static IEnumerable<(string tag, int start, int length)> Chunks(byte[] d, int i, int end)
        {
            end = Math.Min(end, d.Length);
            while (i + 8 <= end)
            {
                int size = (int)(((uint)d[i] << 24 | (uint)d[i + 1] << 16 | (uint)d[i + 2] << 8 | d[i + 3]) & 0x3FFFFFFF);
                yield return (System.Text.Encoding.ASCII.GetString(d, i + 4, 4), i + 8, Math.Max(0, Math.Min(size, end - i) - 8));
                if (size < 8)
                    break;
                i += size;
            }
        }

        /// <summary>The compendium type sets a tagfile takes its types from (its TCRF chunk); empty when it carries its own.</summary>
        public static HashSet<ulong> TypeSetReferences(byte[] hkx)
        {
            var ids = new HashSet<ulong>();
            if (hkx.Length < 8 || System.Text.Encoding.ASCII.GetString(hkx, 4, 4) != "TAG0")
                return ids;
            int end = (int)(((uint)hkx[0] << 24 | (uint)hkx[1] << 16 | (uint)hkx[2] << 8 | hkx[3]) & 0x3FFFFFFF);
            foreach (var (tag, start, length) in Chunks(hkx, 8, end))
                if (tag == "TCRF" && length >= 8)
                    ids.Add(BitConverter.ToUInt64(hkx, start));
            return ids;
        }

        /// <summary>The type sets a .compendium offers (its TCID chunks, 8 bytes each).</summary>
        public static HashSet<ulong> CompendiumIds(byte[] compendium)
        {
            var ids = new HashSet<ulong>();
            foreach (var (tag, start, length) in Chunks(compendium, 8, compendium.Length))
                if (tag == "TCID")
                    for (int k = 0; k + 8 <= length; k += 8)
                        ids.Add(BitConverter.ToUInt64(compendium, start + k));
            return ids;
        }
    }
}
