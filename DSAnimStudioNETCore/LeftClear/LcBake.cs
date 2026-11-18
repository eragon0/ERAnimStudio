// ERAnimStudio: the left arm fix's copy written as Elden Ring's files are: the solved frames written uncompressed through Havoc on the
// source's own types (HkxInterleavedWriter), spline-compressed by Havok Content Tools 2018.1, read back and compared: against what was
// written (every bone), and the bones off the changed arm against the source.
using System;
using System.IO;
using System.Linq;
using DSAnimStudio.HkxEdit;

namespace DSAnimStudio.LeftClear
{
    public static class LcBake
    {
        public const double MaxReadBackError = 0.01;    // m

        public sealed class Report
        {
            public double ReadBack;      // m: the copy read back against what was written, every bone
            public double RestOfBody;    // m: the bones off the changed arm against the source
            public string RootMotion;
            public int Bytes;
            public string HctVersion;
        }

        /// <summary>The compressed copy. armRoot: the arm the change sits on in the file (L_UpperArm, or R_UpperArm for a mirrored clip).</summary>
        public static byte[] Write(byte[] source, byte[] compendium, HkxSkeleton skeleton, HkxAnimation copy, string armRoot,
                                   HctCompressor.Found hct, out Report report)
        {
            if (hct == null)
                throw new HkxEditRefused("writing the copy needs Havok Content Tools " + HctCompressor.RequiredVersion);
            var file = HkxAnimationFile.Read(source, compendium);
            var src = file.Decoded;
            if (src.Frames != copy.Frames || src.Tracks != copy.Tracks || !src.Bones.SequenceEqual(copy.Bones))
                throw new HkxEditRefused("the solved copy doesn't have the source's frames and tracks");
            var names = copy.Bones.Select(k => skeleton.Names[k]).ToList();
            byte[] uncompressed = HkxInterleavedWriter.Write(file, copy, names);
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
            if (back.Frames != copy.Frames || back.Tracks != copy.Tracks)
                throw new HkxEditRefused($"Havok Content Tools' file has {back.Frames} frames and {back.Tracks} tracks, {copy.Frames} and {copy.Tracks} were written");
            double err = skeleton.MaxPositionError(copy, back);
            if (err > MaxReadBackError)
                throw new HkxEditRefused($"the copy read back differs from what was written by {err * 100:0.0} cm (over {MaxReadBackError * 100:0} cm): not written");
            string rm;
            if (src.Root == null && back.Root == null)
                rm = "none on either side";
            else if (src.Root != null && back.Root != null)
                rm = $"within {Enumerable.Range(0, src.Frames).Max(f => (src.Root[f] - back.Root[f]).Length()) * 1000:0.00} mm of the source's";
            else
                throw new HkxEditRefused("the root motion was lost in Havok Content Tools' file");
            // the rest of the body against the source
            var arm = skeleton.Index.TryGetValue(armRoot, out int ar) ? new LcSkeleton(skeleton).Subtree(ar).ToHashSet() : new System.Collections.Generic.HashSet<int>();
            var wa = skeleton.ToWorld(skeleton.FullPose(src).t, skeleton.FullPose(src).q).t;
            var wb = skeleton.ToWorld(skeleton.FullPose(back).t, skeleton.FullPose(back).q).t;
            double rest = 0;
            for (int f = 0; f < src.Frames; f++)
                for (int b = 0; b < skeleton.Count; b++)
                    if (!arm.Contains(b))
                        rest = Math.Max(rest, (wa[f, b] - wb[f, b]).Length());
            report = new Report { ReadBack = err, RestOfBody = rest, RootMotion = rm, Bytes = compressed.Length, HctVersion = hct.Version };
            return compressed;
        }
    }
}
