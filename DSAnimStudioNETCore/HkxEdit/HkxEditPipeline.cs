using System;
using System.IO;
using System.Linq;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>
    /// The hkx of New Animation from Current: a crop to a frame range and / or a mirror, written as Elden Ring's files are (the frames
    /// decoded, cut or mirrored, written uncompressed through Havoc, spline-compressed by Havok Content Tools 2018.1, read back and
    /// compared). Frames are the editor's, as its timeline counts them (30 or 60 a second), converted to the file's own. A range
    /// on the file's frames is cut from them; one starting or ending between two of them (an odd 60 fps frame on a 30 fps file)
    /// is sampled from the file's own curves at the editor's rate, so the new file plays at that rate.
    /// </summary>
    public static class HkxEditPipeline
    {
        public const double DefaultEditorFps = 30.0;
        public const double MaxReadBackError = 0.01;   // metres: a crop read back further off than this is not written

        public sealed class Report
        {
            public int First, Last;            // the editor's frames kept
            public double EditorFps;           // the editor's frame rate they are counted in
            public double FileFirst, FileLast; // the source file's own frames (fractional when sampled between them)
            public double Fps;                 // the source file's rate
            public double OutFps;              // the new file's: the source's, or the editor's when sampled between frames
            public bool Resampled;             // sampled between the source's frames
            public int SourceFrames, Frames;
            public bool Mirrored;
            public double ReadBackError;       // metres, the largest bone position difference after compression
            public bool Eased;                 // the body started on the character (EaseRootOntoCharacter)
            public double EasedOffset;         // metres moved into the root motion
            public double EasedTurn;           // the root bone's turn at the first frame, degrees (left with the root bone)
            public int Bytes;
            public string HctVersion;
        }

        /// <summary>The source's last frame in the editor's frames.</summary>
        public static int LastEditorFrame(HkxAnimation a, double editorFps = DefaultEditorFps) => (int)Math.Round((a.Frames - 1) * editorFps / a.Fps);

        /// <summary>
        /// The cropped and / or mirrored hkx. first / last: the editor's frames at editorFps, null for the source's first / last.
        /// Needs Havok Content Tools 2018.1: <see cref="HctCompressor.Find"/>.
        /// </summary>
        public static byte[] Make(byte[] source, byte[] compendium, HkxSkeleton skeleton, int? first, int? last, bool mirror,
                                  HctCompressor.Found hct, out Report report, bool easeRoot = true, double editorFps = DefaultEditorFps)
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
            double fps = a.Fps;
            int lastEditor = LastEditorFrame(a, editorFps);
            int f0 = first ?? 0, f1 = last ?? lastEditor;
            if (!(0 <= f0 && f0 < f1 && f1 <= lastEditor))
                throw new HkxEditRefused($"frames {f0}-{f1}: the source has frames 0-{lastEditor}");
            double ff = f0 * fps / editorFps, fl = Math.Min(f1 * fps / editorFps, a.Frames - 1);
            static bool OnFrame(double x) => Math.Abs(x - Math.Round(x)) < 0.01;
            bool resampled = !(OnFrame(ff) && OnFrame(fl));
            HkxAnimation b2;
            double outFps;
            if (!resampled)
            {
                int i0 = (int)Math.Round(ff), i1 = (int)Math.Round(fl);
                if (i0 >= i1)
                    throw new HkxEditRefused($"frames {f0}-{f1} are one frame of the source's own ({fps:0.#} fps)");
                b2 = (i0 == 0 && i1 == a.Frames - 1) ? a : HkxAnimationOps.Crop(a, i0, i1);
                ff = i0;
                fl = i1;
                outFps = fps;
            }
            else
            {
                // a range starting or ending between the file's frames: every editor frame sampled from the file's curves
                var at = new double[f1 - f0 + 1];
                for (int k = 0; k < at.Length; k++)
                    at[k] = (f0 + k) * fps / editorFps;
                b2 = HkxAnimationOps.Crop(file.SampleAt(at, editorFps), 0, at.Length - 1);
                outFps = editorFps;
            }
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
                First = f0, Last = f1, EditorFps = editorFps, FileFirst = ff, FileLast = fl, Fps = fps, OutFps = outFps, Resampled = resampled,
                SourceFrames = a.Frames, Frames = b2.Frames,
                Mirrored = mirror, ReadBackError = err, Bytes = compressed.Length, HctVersion = hct.Version,
                Eased = easeRoot && easedOffset > 0, EasedOffset = easedOffset, EasedTurn = easedTurn,
            };
            return compressed;
        }
    }
}
