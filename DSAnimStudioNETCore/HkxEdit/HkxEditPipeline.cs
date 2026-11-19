using System;
using System.IO;
using System.Linq;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>
    /// The hkx of New Animation from Current: a crop to a frame range and / or a mirror, written as Elden Ring's files are (the frames
    /// decoded, cut or mirrored, written uncompressed through Havoc, spline-compressed by Havok Content Tools 2018.1, read back and
    /// compared). Frames are the file's own, at its own rate (30 or 60 a second, or a raw export's), as the editor's 'Frame' counter
    /// counts them while the file plays.
    /// </summary>
    public static class HkxEditPipeline
    {
        public const double MaxReadBackError = 0.01;   // metres: a crop read back further off than this is not written

        public sealed class Report
        {
            public int First, Last;            // the file's frames kept
            public double Fps;                 // the file's rate, which the copy keeps
            public int SourceFrames, Frames;
            public bool Mirrored;
            public double ReadBackError;       // metres, the largest bone position difference after compression
            public bool Eased;                 // the body started on the character (EaseRootOntoCharacter)
            public double EasedOffset;         // metres moved into the root motion
            public double EasedTurn;           // the root bone's turn at the first frame, degrees (left with the root bone)
            public int Bytes;
            public string HctVersion;
        }

        /// <summary>
        /// The cropped and / or mirrored hkx. first / last: the file's own frames, null for the source's first / last.
        /// Needs Havok Content Tools 2018.1: <see cref="HctCompressor.Find"/>.
        /// </summary>
        public static byte[] Make(byte[] source, byte[] compendium, HkxSkeleton skeleton, int? first, int? last, bool mirror,
                                  HctCompressor.Found hct, out Report report, bool easeRoot = true)
        {
            if (hct == null)
                throw new HkxEditRefused("a crop or a mirror needs Havok Content Tools " + HctCompressor.RequiredVersion);
            HkxAnimationFile file;
            try
            {
                file = HkxAnimationFile.Read(source, compendium);
            }
            catch (InvalidDataException e)
            {
                throw new HkxEditRefused("the source can't be decoded: " + e.Message);
            }
            var a = file.Decoded;
            foreach (var b in a.Bones)
                if (b < 0 || b >= skeleton.Count)
                    throw new HkxEditRefused($"a track animates bone {b}, past the skeleton's {skeleton.Count} bones: a crop can't name it (a whole copy works)");
            int lastFrame = a.Frames - 1;
            int f0 = first ?? 0, f1 = last ?? lastFrame;
            if (!(0 <= f0 && f0 < f1 && f1 <= lastFrame))
                throw new HkxEditRefused($"frames {f0}-{f1}: the source has frames 0-{lastFrame} ({a.Fps:0.#} fps)");
            var b2 = (f0 == 0 && f1 == lastFrame) ? a : HkxAnimationOps.Crop(a, f0, f1);
            if (mirror)
                b2 = HkxAnimationOps.Mirror(skeleton, b2);
            // the body started on the character: the root bone's offset at the first frame moved into the root motion
            double easedOffset = 0, easedTurn = 0;
            if (easeRoot)
                b2 = HkxAnimationOps.EaseRootOntoCharacter(skeleton, b2, out easedOffset, out easedTurn);
            var names = b2.Bones.Select(k => skeleton.Names[k]).ToList();
            byte[] uncompressed = HkxInterleavedWriter.Write(file, b2, names);
            string work = HctCompressor.ScratchFolder();
            byte[] compressed;
            try
            {
                compressed = HctCompressor.Compress(hct, uncompressed, work);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
            HkxAnimation back;
            try
            {
                back = HkxAnimationFile.Read(compressed).Decoded;
            }
            catch (InvalidDataException e)
            {
                throw new HkxEditRefused("Havok Content Tools' file can't be read back: " + e.Message);
            }
            if (back.Frames != b2.Frames || back.Tracks != b2.Tracks)
                throw new HkxEditRefused($"Havok Content Tools' file has {back.Frames} frames and {back.Tracks} tracks, {b2.Frames} and {b2.Tracks} were written");
            double err = skeleton.MaxPositionError(b2, back);
            if (b2.Root != null && back.Root != null)
                for (int f = 0; f < b2.Frames; f++)
                    err = Math.Max(err, (new System.Numerics.Vector3(b2.Root[f].X, b2.Root[f].Y, b2.Root[f].Z)
                                         - new System.Numerics.Vector3(back.Root[f].X, back.Root[f].Y, back.Root[f].Z)).Length());
            else if ((b2.Root == null) != (back.Root == null))
                throw new HkxEditRefused("the root motion was lost in Havok Content Tools' file");
            if (err > MaxReadBackError)
                throw new HkxEditRefused($"the new file read back differs from the edit by {err * 100:0.0} cm (over {MaxReadBackError * 100:0} cm): not written");
            report = new Report
            {
                First = f0, Last = f1, Fps = a.Fps, SourceFrames = a.Frames, Frames = b2.Frames,
                Mirrored = mirror, ReadBackError = err, Bytes = compressed.Length, HctVersion = hct.Version,
                Eased = easeRoot && easedOffset > 0, EasedOffset = easedOffset, EasedTurn = easedTurn,
            };
            return compressed;
        }
    }
}
