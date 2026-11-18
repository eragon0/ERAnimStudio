// ERAnimStudio: 'Play Mirrored' in the Animation menu. In game a behavior clip can play its animation mirrored (the clip's mirror
// flag): left and right swapped. The editor reads no behavior, so it plays every animation as the file has it; an entry marked here
// plays as such a clip does: each bone takes its left / right twin's pose (L_ / R_ by name) reflected across x = 0, with a fixed
// correction per bone so the reference pose stays the reference pose (the same mirror as 'New Animation from Current', HkxEdit's
// HkxAnimationOps.Mirror), and the root motion's x and turn change sign. The marks are kept per tab for the session.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DSAnimStudio.HkxEdit;
using SoulsAssetPipeline.Animation;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace DSAnimStudio.LeftClear
{
    public static class MirrorPlayback
    {
        static readonly ConditionalWeakTable<DSAProj, HashSet<int>> marks = new ConditionalWeakTable<DSAProj, HashSet<int>>();
        static readonly ConditionalWeakTable<HKX.HKASkeleton, MirrorTable> tables = new ConditionalWeakTable<HKX.HKASkeleton, MirrorTable>();

        /// <summary>Changed whenever a mark changes: what depends on the marks (the left item check) looks at it.</summary>
        public static int Version { get; private set; }

        static HashSet<int> Marks(DSAProj proj) => marks.GetValue(proj, _ => new HashSet<int>());

        public static bool IsMirrored(DSAProj proj, SplitAnimID entry) =>
            proj != null && entry.IsValid && Marks(proj).Contains(entry.GetFullID(proj, null));

        public static void SetMirrored(DSAProj proj, SplitAnimID entry, bool on)
        {
            if (proj == null || !entry.IsValid)
                return;
            var m = Marks(proj);
            int id = entry.GetFullID(proj, null);
            if (on ? m.Add(id) : m.Remove(id))
                Version++;
        }

        /// <summary>Why this skeleton can't be mirrored (its reference pose isn't left / right symmetric), or null.</summary>
        public static string CantMirror(HKX.HKASkeleton skeleton)
        {
            if (skeleton == null)
                return "no skeleton is loaded";
            return Table(skeleton).Problem;
        }

        static MirrorTable Table(HKX.HKASkeleton skeleton) => tables.GetValue(skeleton, s => new MirrorTable(HkxSkeleton.FromHavok(s)));

        /// <summary>The animation as a mirrored clip plays it (a new instance; the source is left as it is), or the source when it can't be.</summary>
        public static NewHavokAnimation Wrap(NewHavokAnimation anim)
        {
            var sk = anim?.Data?.hkaSkeleton;
            if (sk == null || anim.Data is HavokAnimationData_Mirrored)
                return anim;
            var t = Table(sk);
            if (t.Problem != null)
                return anim;
            var root = anim.RootMotion?.Data;
            RootMotionData mirroredRoot = root == null ? null
                : new RootMotionData(root.Up, root.Forward, root.Duration, root.Frames.Select(r => new NVector4(-r.X, r.Y, r.Z, -r.W)).ToArray());
            var m = new NewHavokAnimation(new HavokAnimationData_Mirrored(anim.Data, t), anim.FileSize, mirroredRoot)
            {
                TaeAnimation = anim.TaeAnimation,
                IsUpperBody = anim.IsUpperBody,
                EnableLooping = anim.EnableLooping,
            };
            return m;
        }
    }

    /// <summary>Per bone of a skeleton: its twin and the mirror's correction, for mirroring one bone's local pose from its twin's.</summary>
    public sealed class MirrorTable
    {
        public readonly int[] Twin;
        public readonly int[] Parent;
        public readonly NQuaternion[] Corr;       // rotation correction per bone (HkxAnimationOps.Mirror's corr)
        public readonly NQuaternion[] CorrParentInv;
        public readonly string Problem;

        public MirrorTable(HkxSkeleton sk)
        {
            Twin = HkxAnimationOps.Twins(sk);
            Parent = sk.Parents;
            if (HkxAnimationOps.ReferenceAsymmetry(sk) > 0.001)
            {
                Problem = "the skeleton's reference pose isn't left / right symmetric";
                return;
            }
            // a bone's local pose comes from its twin's alone when the twin's parent is the parent's twin
            for (int b = 0; b < sk.Count; b++)
            {
                int pTwin = Parent[Twin[b]], twinP = Parent[b] >= 0 ? Twin[Parent[b]] : -1;
                if (pTwin != twinP)
                {
                    Problem = $"{sk.Names[b]}'s twin hangs on another bone than its parent's twin";
                    return;
                }
            }
            var (_, rq) = sk.WorldReference();
            Corr = new NQuaternion[sk.Count];
            for (int b = 0; b < sk.Count; b++)
                Corr[b] = NQuaternion.Normalize(NQuaternion.Conjugate(ReflectQ(rq[Twin[b]])) * rq[b]);
            CorrParentInv = new NQuaternion[sk.Count];
            for (int b = 0; b < sk.Count; b++)
                CorrParentInv[b] = Parent[b] >= 0 ? NQuaternion.Conjugate(Corr[Parent[b]]) : NQuaternion.Identity;
        }

        public static NVector3 ReflectT(NVector3 t) => new NVector3(-t.X, t.Y, t.Z);
        public static NQuaternion ReflectQ(NQuaternion q) => new NQuaternion(q.X, -q.Y, -q.Z, q.W);

        /// <summary>Bone b's mirrored local pose from its twin's local pose.</summary>
        public NewBlendableTransform Mirror(int b, NewBlendableTransform twin)
        {
            var ci = CorrParentInv[b];
            return new NewBlendableTransform(
                NVector3.Transform(ReflectT(twin.Translation), ci),
                twin.Scale,
                NQuaternion.Normalize(ci * ReflectQ(twin.Rotation) * Corr[b]));
        }
    }

    /// <summary>A decoded animation served mirrored, bone by bone (every bone is a 'track' of its own).</summary>
    public sealed class HavokAnimationData_Mirrored : HavokAnimationData
    {
        public readonly HavokAnimationData Source;
        readonly MirrorTable table;

        public HavokAnimationData_Mirrored(HavokAnimationData source, MirrorTable t)
            : base(source.ID, source.Name)
        {
            Source = source;
            table = t;
            hkaSkeleton = source.hkaSkeleton;
            Duration = source.Duration;
            FrameDuration = source.FrameDuration;
            FrameCount = source.FrameCount;
            BlendHint = source.BlendHint;
            int n = t.Twin.Length;
            HkxBoneIndexToTransformTrackMap = Enumerable.Range(0, n).ToArray();
            TransformTrackIndexToHkxBoneMap = Enumerable.Range(0, n).ToArray();
        }

        public override NewBlendableTransform GetTransformOnFrame(int bone, float frame, bool enableLooping) =>
            table.Mirror(bone, Source.GetTransformOnFrameByBone(table.Twin[bone], frame, enableLooping));
    }
}
