// ERAnimStudio: 'Left Arm Clearance...' in the Edit menu, after 'New Animation from Current...'. The selected entry's left arm changed
// so the items in the left hand stay off the body (LcSolver: the solve, a port of the pack's left_clear.py), written as a copy of the
// hkx the entry plays, and the entry pointed at the copy.
//   - Items: the equipped left weapon or shield and a list of weapons by ID kept in the config, each 'must clear' or best effort; each
//     hangs on the dummy its WepAbsorpPosParam gives the left hand. Armor: what the character wears (the meshes the draw mask hides
//     left out).
//   - The limits are the user's (kept in the config): the wrist's move (cm), the roll offset from the palm-in turn, the two wrist bends,
//     the elbow's swing (degrees). Defaults: the pack tool's 12 cm, 30, 60, 60, 60.
//   - Pins: frames (the timeline's, 30 a second) where the arm keeps the source's pose, a strike thrown with the left hand.
//   - An entry marked 'Play Mirrored' (Animation menu) is solved on the pose as the game plays it and the copy mirrored back, so the change
//     sits on the file's right arm, which the game plays as the left.
//   - The solve runs in the background (progress, cancel); then a preview: the entry plays the solved frames (Look at it), the left item
//     check measures them, and the dialog (opened again from the menu) writes the copy or discards it.
//   - Write: aTTT_AAAAAA.hkx with the next free number after the hkx the entry plays, written through Havok Content Tools 2018.1 into the
//     split binder picked (at once, not with the project's save), and the entry imports it (its own events stay). An entry importing
//     another entry's events (type 1) is refused.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DSAnimStudio.HkxEdit;
using DSAnimStudio.ImguiOSD;
using DSAnimStudio.TaeEditor;
using ImGuiNET;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DSAnimStudio.LeftClear
{
    public static class LeftClearFix
    {
        public const string MenuText = "Left Arm Clearance...";

        /// <summary>One solve: its inputs, its state, its result.</summary>
        public sealed class Job
        {
            public TaeEditorScreen Tae;
            public DSAProj Proj;
            public DSAProj.Animation Entry;
            public SplitAnimID EntryId, Hkx;
            public byte[] Source, Compendium;
            public HkxSkeleton Skeleton;
            public bool Mirror, Hold;
            public LcLimits Limits;
            public List<(int first, int last)> Pins;
            public List<LcItem> Items;
            public string ArmorText, PinsText;
            public CancellationTokenSource Cts = new CancellationTokenSource();
            public volatile bool Running;
            public double Progress;
            public string Stage = "";
            public LcSolveResult Result;
            public string Error;
            public bool Previewing;
            public int Version;
        }

        static Job job;
        static int versions;

        public static Job Current => job;

        public static bool Enabled(TaeEditorScreen tae) =>
            tae != null && tae.IsFileOpen && tae.SelectedAnim != null && !tae.SelectedAnim.IS_DUMMY_ANIM && DuplicateCrop.IsEldenRing(tae)
            && tae.Graph?.ViewportInteractor?.CurrentModel?.ChrAsm != null;

        public static string MenuLabel(TaeEditorScreen tae) =>
            job?.Result != null && job.Tae == tae ? "Left Arm Clearance (a solve is ready)..." : job?.Running == true ? "Left Arm Clearance (solving)..." : MenuText;

        public static void ShowDialog(TaeEditorScreen tae)
        {
            if (Enabled(tae))
                DialogManager.AddDialog(new LeftClearFixDialog(tae));
        }

        // ---- the preview: the entry plays the solved frames, as the copy will be written

        /// <summary>The solved copy (the file's side) when the entry is being previewed, else null.</summary>
        public static HkxAnimation PreviewOf(DSAProj proj, SplitAnimID entry, out int version)
        {
            var j = job;
            version = 0;
            if (j?.Result == null || !j.Previewing || j.Proj != proj || !entry.IsValid || entry.GetFullID(proj, null) != j.EntryId.GetFullID(proj, null))
                return null;
            version = j.Version;
            return j.Result.Copy;
        }

        /// <summary>The playback hook (NewAnimationContainer): the entry plays the preview, or what it plays.</summary>
        public static NewHavokAnimation PreviewPlayback(DSAProj proj, SplitAnimID entry, NewHavokAnimation anim)
        {
            var copy = PreviewOf(proj, entry, out _);
            if (copy == null || anim?.Data?.hkaSkeleton == null || anim.Data is HavokAnimationData_Preview)
                return anim;
            if (copy.Frames != anim.Data.FrameCount)
                return anim;
            return new NewHavokAnimation(new HavokAnimationData_Preview(anim.Data, copy), anim.FileSize, anim.RootMotion?.Data)
            {
                TaeAnimation = anim.TaeAnimation,
                IsUpperBody = anim.IsUpperBody,
                EnableLooping = anim.EnableLooping,
            };
        }

        public static void SetPreview(bool on)
        {
            var j = job;
            if (j?.Result == null)
                return;
            j.Previewing = on;
            j.Version = ++versions;
            Replay(j);
        }

        static void Replay(Job j)
        {
            if (j.Tae?.SelectedAnim == j.Entry)
                j.Tae.Graph?.ViewportInteractor?.OnNewAnimSelected(0, false, j.Entry);
        }

        public static void Discard()
        {
            var j = job;
            if (j == null)
                return;
            j.Cts.Cancel();
            bool wasPreviewing = j.Previewing;
            job = null;
            if (wasPreviewing)
                Replay(j);
        }

        // ---- the inputs

        /// <summary>The config's item list: (weapon ID, must clear).</summary>
        public static List<(int id, bool must)> ConfigItems()
        {
            var o = new List<(int, bool)>();
            foreach (var part in (Main.Config.LeftClearItems ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (int.TryParse(kv[0], out int id) && !o.Any(x => x.Item1 == id))
                    o.Add((id, kv.Length < 2 || kv[1] != "0"));
            }
            return o;
        }

        public static void SaveConfigItems(List<(int id, bool must)> items) =>
            Main.Config.LeftClearItems = string.Join(";", items.Select(x => $"{x.id}:{(x.must ? 1 : 0)}"));

        public static bool TryParsePins(string text, double fps, out List<(int, int)> pins, out string error)
        {
            pins = new List<(int, int)>();
            error = null;
            foreach (var raw in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var ab = raw.Split('-');
                if (ab.Length > 2 || !int.TryParse(ab[0], out int a) || !int.TryParse(ab[^1], out int b) || a < 0 || b < a)
                {
                    error = $"'{raw}' is not a frame or a range (3-19)";
                    return false;
                }
                // the timeline's frames (30 a second) to the file's own
                pins.Add(((int)Math.Round(a * fps / 30.0), (int)Math.Round(b * fps / 30.0)));
            }
            return true;
        }

        /// <summary>The left hand's dummy of a weapon held one-handed (its WepAbsorpPosParam), or -1.</summary>
        public static int LeftDummy(ParamData.EquipParamWeapon w)
        {
            var abs = w?.GetAbsorpPosParam();
            if (abs == null || abs.IsSkeletonBind)
                return -1;
            return abs.NewAbsorpPos.TryGetValue(ParamData.WepAbsorpPosParam.NewWepAbsorpPosType.Left, out var c) ? c.Model0DummyPoly : -1;
        }

        /// <summary>Starts a solve of the selected entry; null when it started, else why not.</summary>
        public static string Start(TaeEditorScreen tae, List<(int id, bool must)> itemList, LcLimits limits, string pinsText, bool hold)
        {
            if (job?.Running == true)
                return "a solve is still running";
            var model = tae?.Graph?.ViewportInteractor?.CurrentModel;
            var asm = model?.ChrAsm;
            var c = model?.AnimContainer;
            var entry = tae?.SelectedAnim;
            var proj = tae?.Proj;
            var doc = tae?.ParentDocument;
            if (model == null || asm == null || c == null || entry == null || proj == null || doc == null)
                return "no character animation selected";
            if (entry.SAFE_GetHeaderClone() is SoulsAssetPipeline.Animation.TAE.Animation.AnimFileHeader.ImportOtherAnim)
                return "the entry plays another entry with its events (type 1): its own events and hkx can't change";
            var hkx = entry.GetHkxID(proj);
            var bytes = hkx.IsValid ? c.FindAnimationBytes(hkx) : null;
            if (bytes?.HkxBytes == null)
                return "the entry's hkx is not loaded";
            var skH = c.Skeleton?.OriginalHavokSkeleton;
            if (skH == null)
                return "no skeleton is loaded";
            if (itemList.Count == 0)
                return "no items: equip something in the left hand or add one to the list";
            var armor = LeftItemCheck.ArmorWorn(asm);
            if (armor.Count == 0)
                return "no armor worn to keep the items off";
            HkxAnimation src;
            try
            {
                src = HkxAnimationFile.Read(bytes.HkxBytes, bytes.CompendiumBytes).Decoded;
            }
            catch (Exception e)
            {
                return "the entry's hkx can't be decoded: " + e.Message;
            }
            if (!TryParsePins(pinsText, src.Fps, out var pins, out string perr))
                return "Pins: " + perr;
            var skel = HkxSkeleton.FromHavok(skH);
            bool mirror = MirrorPlayback.IsMirrored(proj, entry.SplitID);
            if (mirror && MirrorPlayback.CantMirror(skH) != null)
                return "the entry is marked Play Mirrored, but " + MirrorPlayback.CantMirror(skH);
            var mask = (bool[])model.DrawMask.Clone();
            // the geometry is loaded on the editor's thread (the binders' cache), the solve in the background
            LcSkeleton sk;
            LcBody body;
            var items = new List<LcItem>();
            try
            {
                sk = new LcSkeleton(skel);
                body = LeftItemCheck.BodyOf(sk, armor, mask);
                var chr = LeftItemCheck.MainFlver(doc.GameData.ReadBinder($"/chr/{model.Name}.chrbnd.dcx"), model.Name);
                foreach (var (id, must) in itemList)
                {
                    if (!doc.ParamManager.EquipParamWeapon.TryGetValue(id, out var w))
                        return $"no weapon {id} in the params";
                    string name = LeftItemCheck.WeaponName(doc, id, w);
                    int dummy = LeftDummy(w);
                    if (dummy < 0 || dummy >= 1000)
                        return $"{name}: no dummy of the character's model holds it in the left hand (WepAbsorpPosParam)";
                    var it = LeftItemCheck.ItemOf(sk, chr, LeftItemCheck.MainFlver(w.GetPartBnd(NewChrAsm.EquipSlotTypes.LeftWeapon, false), name), dummy, name);
                    it.Required = must;
                    items.Add(it);
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
            Discard();
            var j = new Job
            {
                Tae = tae, Proj = proj, Entry = entry, EntryId = entry.SplitID, Hkx = hkx, Source = bytes.HkxBytes, Compendium = bytes.CompendiumBytes,
                Skeleton = skel, Mirror = mirror, Hold = hold, Limits = limits, Pins = pins, Items = items, PinsText = pinsText,
                ArmorText = string.Join(", ", armor.Select(a => a.name)), Running = true, Stage = "starting",
            };
            job = j;
            var input = new LcSolveInput { Sk = sk, Body = body, Items = items, Source = src, Mirror = mirror, Hold = hold, Pins = pins, Limits = limits };
            var token = j.Cts.Token;
            Task.Run(() =>
            {
                try
                {
                    var r = new LcSolver(input).Run((p, label) =>
                    {
                        j.Progress = p;
                        j.Stage = label;
                    }, token);
                    j.Result = r;
                }
                catch (OperationCanceledException)
                {
                    j.Error = "cancelled";
                }
                catch (Exception e)
                {
                    j.Error = e.Message;
                }
                finally
                {
                    j.Running = false;
                }
            });
            return null;
        }

        // ---- writing the copy

        /// <summary>The copy's name: the number after the played hkx's, the first one no binder the editor loaded has.</summary>
        public static SplitAnimID? CopyId(Job j)
        {
            var c = DuplicateCrop.Container(j.Tae);
            for (int k = j.Hkx.SubID + 1; k <= Math.Min(999999, j.Hkx.SubID + 99); k++)
                if (SplitAnimID.TryParse(j.Proj, $"a{j.Hkx.CategoryID:D3}_{k:D6}", out var id, out _) && c != null && !c.AnimExists(id))
                    return id;
            return null;
        }

        /// <summary>Writes the copy into the binder and points the entry at it; returns what was done (throws with why not).</summary>
        public static string Write(Job j, string binder)
        {
            if (j?.Result == null)
                throw new InvalidOperationException("nothing solved");
            var hct = HctCompressor.Find(out string why) ?? throw new HkxEditRefused(why + ". Writing the copy needs Havok Content Tools " + HctCompressor.RequiredVersion);
            var id = CopyId(j) ?? throw new HkxEditRefused($"no free name after {DuplicateCrop.HkxName(j.Hkx)}");
            string name = DuplicateCrop.HkxName(id);
            byte[] bytes = LcBake.Write(j.Source, j.Compendium, j.Skeleton, j.Result.Copy, j.Mirror ? "R_UpperArm" : "L_UpperArm", hct, out var rep);
            var w = HkxBinderWriter.Add(binder, bytes, name);
            var c = DuplicateCrop.Container(j.Tae);
            c.AddNewHKXToLoad(id, name + ".hkx", bytes, w.CompendiumName);
            var h = j.Entry.SAFE_GetHeaderClone() as SoulsAssetPipeline.Animation.TAE.Animation.AnimFileHeader.Standard
                    ?? throw new InvalidOperationException("the entry is not a standard entry");
            h.ImportsHKX = true;
            h.ImportHKXSourceAnimID = id.GetFullID(j.Proj, null);
            j.Entry.SAFE_SetHeader(h);
            j.Entry.SAFE_SetIsModified(true);
            string done = $"{name}.hkx written into {Path.GetFileName(binder)} ({rep.Bytes} B, read back within {rep.ReadBack * 1000:0.0} mm, the rest of the body "
                          + $"within {rep.RestOfBody * 1000:0.0} mm of the source); {j.EntryId.GetFormattedIDString(j.Proj, null)} now imports it "
                          + $"(it played {DuplicateCrop.HkxName(j.Hkx)}). Save the project to keep the entry's change.";
            job = null;
            if (j.Tae.SelectedAnim == j.Entry)
                j.Tae.SelectNewAnimRef(j.Entry.ParentCategory, j.Entry, false, false, -1f, false, false);
            return done;
        }

        // ---- the report

        public static List<string> ReportLines(Job j)
        {
            var r = j.Result;
            var o = new List<string>();
            if (r == null)
                return o;
            o.Add($"{j.EntryId.GetFormattedIDString(j.Proj, null)} (plays {DuplicateCrop.HkxName(j.Hkx)}), {r.Frames} frames, solved in {r.Seconds:0} s"
                  + (j.Mirror ? ", as a mirrored clip plays it (the change on the file's right arm)" : ""));
            o.Add($"Armor: {j.ArmorText}. Limits: the wrist {j.Limits.MoveCm:0.#} cm, roll {j.Limits.Roll:0}, bends {j.Limits.Bend1:0} / {j.Limits.Bend2:0}, swing {j.Limits.Swing:0}"
                  + (j.Pins.Count > 0 ? $"; pinned {j.PinsText}" : "") + (j.Hold ? "; held the game's way" : ""));
            o.Add("Frames over 1 cm (deepest), before -> with the hold -> after:");
            for (int i = 0; i < j.Items.Count; i++)
            {
                string Fig(Dictionary<string, double>[][] rows) =>
                    $"{rows.Count(x => x[i].Values.Any(v => v > 0.01))} ({rows.Max(x => x[i].Count == 0 ? 0 : x[i].Values.Max()) * 100:0.0} cm)";
                o.Add($"  {j.Items[i].Name} ({(j.Items[i].Required ? "must clear" : "best effort")}): {Fig(r.Before)} -> {Fig(r.Held)} -> {Fig(r.After)}");
                var left = Enumerable.Range(0, r.Frames).Where(f => r.After[f][i].Values.Any(v => v > 0.01)).ToList();
                if (left.Count > 0 && j.Items[i].Required)
                    o.Add($"      still over 1 cm at {Frames(left, r.Fps)}");
            }
            o.Add($"The change: the wrist moved up to {r.MoveMax * 100:0.0} cm (median {r.MoveMedian * 100:0.0}); the hand's turn about the forearm "
                  + $"median {r.RollMedian:0}, up to {r.RollMax:0} degrees; bent up to {r.BendMax:0}; the elbow swung up to {r.SwingMax:0}.");
            o.Add($"The palm to the torso's line: {r.PalmBefore:0} degrees on median before, {r.PalmAfter:0} after ({r.PalmWithin45 * 100:0}% of frames within 45; "
                  + "the game's one-handed attacks: 30-41).");
            return o;
        }

        // the file's frames as the timeline's, in ranges
        static string Frames(List<int> f, double fps)
        {
            var t = f.Select(x => (int)Math.Round(x * 30.0 / fps)).Distinct().ToList();
            var parts = new List<string>();
            for (int i = 0; i < t.Count;)
            {
                int k = i;
                while (k + 1 < t.Count && t[k + 1] == t[k] + 1)
                    k++;
                parts.Add(k > i ? $"{t[i]}-{t[k]}" : $"{t[i]}");
                i = k + 1;
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>The preview's frames served as the editor plays animations: the solved copy's tracks, frames blended as the editor blends.</summary>
    public sealed class HavokAnimationData_Preview : HavokAnimationData
    {
        readonly HkxAnimation copy;

        public HavokAnimationData_Preview(HavokAnimationData source, HkxAnimation copy)
            : base(source.ID, source.Name)
        {
            this.copy = copy;
            hkaSkeleton = source.hkaSkeleton;
            Duration = source.Duration;
            FrameDuration = source.FrameDuration;
            FrameCount = source.FrameCount;
            BlendHint = source.BlendHint;
            int n = (int)source.hkaSkeleton.Bones.Size;
            HkxBoneIndexToTransformTrackMap = Enumerable.Repeat(-1, n).ToArray();
            TransformTrackIndexToHkxBoneMap = copy.Bones.Select(b => (int)b).ToArray();
            for (int k = 0; k < copy.Tracks; k++)
                if (copy.Bones[k] >= 0 && copy.Bones[k] < n)
                    HkxBoneIndexToTransformTrackMap[copy.Bones[k]] = k;
        }

        public override NewBlendableTransform GetTransformOnFrame(int track, float frame, bool enableLooping)
        {
            int count = copy.Frames;
            if (enableLooping && count > 1)
                frame %= count - 1;
            int a = Math.Clamp((int)Math.Floor(frame), 0, count - 1), b = Math.Clamp((int)Math.Ceiling(frame), 0, count - 1);
            var ta = new NewBlendableTransform(copy.T[a, track], copy.S[a, track], copy.Q[a, track]);
            var tb = new NewBlendableTransform(copy.T[b, track], copy.S[b, track], copy.Q[b, track]);
            return NewBlendableTransform.Lerp(ta, tb, frame - (float)Math.Floor(frame));
        }
    }

    // the dialog: the inputs, then the solve's progress, then its report with Look at it / Write / Discard
    sealed class LeftClearFixDialog : Dialog
    {
        readonly TaeEditorScreen tae;
        readonly List<(int id, bool must)> list;
        (int id, bool must)? equipped;
        bool equippedInList;            // the equipped item is also in the saved list (kept there)
        string addId = "", pins = "", error, done;
        double[] lim;
        bool hold;
        readonly List<string> binders;
        readonly string[] binderNames;
        readonly string binderError;
        int binderPick;

        static readonly Vector4 Green = new Vector4(0.45f, 0.9f, 0.45f, 1f);
        static readonly Vector4 Amber = new Vector4(1f, 0.75f, 0.3f, 1f);
        static readonly Vector4 Red = new Vector4(1f, 0.4f, 0.4f, 1f);

        public LeftClearFixDialog(TaeEditorScreen tae) : base("Left Arm Clearance")
        {
            this.tae = tae;
            CancelHandledByInheritor = true;
            AcceptHandledByInheritor = true;
            AllowedResultTypes = ResultTypes.Accept | ResultTypes.Cancel;
            InputFlags = InputFlag.EscapeKeyToCancel | InputFlag.TitleBarXToCancel;
            list = LeftClearFix.ConfigItems();
            var left = tae.Graph?.ViewportInteractor?.CurrentModel?.ChrAsm?.WeaponSlots.FirstOrDefault(s => s.EquipSlotType == NewChrAsm.EquipSlotTypes.LeftWeapon);
            if (left?.EquipParam != null)
            {
                var have = list.FindIndex(x => x.id == left.EquipID);
                equipped = (left.EquipID, have >= 0 ? list[have].must : true);
                equippedInList = have >= 0;
                if (have >= 0)
                    list.RemoveAt(have);
            }
            var l = LcLimits.Parse(Main.Config.LeftClearLimits);
            lim = new[] { l.MoveCm, l.Roll, l.Bend1, l.Bend2, l.Swing };
            hold = Main.Config.LeftClearHold;
            var j = LeftClearFix.Current;
            if (j != null && j.Tae == tae && j.Entry == tae.SelectedAnim)
                pins = j.PinsText ?? "";
            binders = DuplicateCrop.DestBinders(tae, out _, out binderError);
            binderNames = binders.Select(Path.GetFileName).ToArray();
            binderPick = DuplicateCrop.DefaultBinder(tae, binders);
        }

        static void Help(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        static void Label(string text, float column)
        {
            ImGui.Text(text);
            ImGui.SameLine();
            ImGui.SetCursorPosX(column);
        }

        string Name(int id)
        {
            var doc = tae.ParentDocument;
            return doc.ParamManager.EquipParamWeapon.TryGetValue(id, out var w) ? LeftItemCheck.WeaponName(doc, id, w) : $"weapon {id} (not in the params)";
        }

        void SaveSettings()
        {
            var all = new List<(int id, bool must)>(list);
            if (equipped != null && equippedInList)
                all.Insert(0, equipped.Value);
            LeftClearFix.SaveConfigItems(all);
            Main.Config.LeftClearLimits = new LcLimits { MoveCm = lim[0], Roll = lim[1], Bend1 = lim[2], Bend2 = lim[3], Swing = lim[4] }.ToString();
            Main.Config.LeftClearHold = hold;
        }

        protected override void BuildInsideOfWindow()
        {
            float column = 110 * Main.DPI;
            var j = LeftClearFix.Current;
            bool mine = j != null && j.Tae == tae;
            var entry = tae.SelectedAnim;
            var proj = tae.Proj;

            if (IsTitleBarXRequested || IsEscapeKeyRequested)
            {
                Dismiss();
                return;
            }

            // ---- a solve running
            if (mine && j.Running)
            {
                ImGui.Text($"Solving {j.EntryId.GetFormattedIDString(j.Proj, null)} ...");
                ImGui.ProgressBar((float)j.Progress, new Vector2(420 * Main.DPI, 0), $"{j.Progress * 100:0}%  {j.Stage}");
                ImGui.Text(" ");
                if (Tools.SimpleClickButton("Cancel the solve"))
                    LeftClearFix.Discard();
                ImGui.SameLine();
                if (Tools.SimpleClickButton("Close (keeps solving)"))
                    Dismiss();
                return;
            }

            // ---- a solve done: its report, the preview, writing
            if (mine && j.Result != null)
            {
                foreach (var line in LeftClearFix.ReportLines(j))
                    ImGui.TextWrapped(line);
                ImGui.Separator();
                bool wrongEntry = tae.SelectedAnim != j.Entry;
                if (wrongEntry)
                    ImGui.TextColored(Amber, $"Select {j.EntryId.GetFormattedIDString(j.Proj, null)} to look at the preview.");
                bool pv = j.Previewing;
                if (ImGui.Checkbox("Preview: the entry plays the solved frames", ref pv))
                    LeftClearFix.SetPreview(pv);
                Help("The entry plays the solved frames as the copy will be written (the Left Item Check measures them too).\n"
                     + "Nothing is written until 'Write the copy'.");
                var id = LeftClearFix.CopyId(j);
                Label("Copy", column);
                ImGui.Text(id != null ? DuplicateCrop.HkxName(id.Value) + ".hkx" : "no free name after " + DuplicateCrop.HkxName(j.Hkx));
                Label("Binder", column);
                if (binders.Count > 0)
                {
                    ImGui.PushItemWidth(260 * Main.DPI);
                    ImGui.Combo("##LeftClear.Binder", ref binderPick, binderNames, binderNames.Length);
                    ImGui.PopItemWidth();
                }
                else
                    ImGui.TextColored(Amber, binderError);
                if (error != null)
                    ImGui.TextColored(Red, "Error: " + error);
                ImGui.Text(" ");
                if (Tools.SimpleClickButton("Look at it"))
                {
                    LeftClearFix.SetPreview(true);
                    Dismiss();
                    return;
                }
                ImGui.SameLine();
                if (Tools.SimpleClickButton("Write the copy", binders.Count > 0 && id != null))
                {
                    try
                    {
                        string text = LeftClearFix.Write(j, binders[binderPick]);
                        DuplicateCrop.LastBinderName = binderNames[binderPick];
                        zzz_NotificationManagerIns.PushNotification(text, 8f, 0.5f);
                        Dismiss();
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                    return;
                }
                ImGui.SameLine();
                if (Tools.SimpleClickButton("Discard"))
                {
                    LeftClearFix.Discard();
                    error = null;
                    return;
                }
                ImGui.SameLine();
                if (Tools.SimpleClickButton("Close"))
                    Dismiss();
                return;
            }

            // ---- the inputs
            if (mine && j.Error != null)
                ImGui.TextColored(j.Error == "cancelled" ? Amber : Red, "The last solve: " + j.Error);
            Label("Entry", column);
            var hkx = entry?.GetHkxID(proj) ?? SplitAnimID.Invalid;
            ImGui.Text($"{entry?.SplitID.GetFormattedIDString(proj, null)}, plays {(hkx.IsValid ? DuplicateCrop.HkxName(hkx) : "no hkx")}");
            bool mirrored = entry != null && MirrorPlayback.IsMirrored(proj, entry.SplitID);
            Label("", column);
            ImGui.TextDisabled(mirrored ? "Play Mirrored is on: solved as the game plays it, the change on the file's right arm"
                                        : "Play Mirrored is off: solved as the file plays");
            Help("A clip with the mirror flag plays its animation mirrored in game, so the arm holding the left item is the file's right arm.\n"
                 + "Mark such an entry with Animation > Play Mirrored first.");
            ImGui.Separator();

            Label("Items", column);
            ImGui.Text("must clear / name");
            Help("What the left hand holds: the equipped left item and a list of weapons by ID (kept in the settings).\n"
                 + "Must clear: the solve pays heavily for each cm of crossing; the others count less (as far as the limits allow).\n"
                 + "Each hangs where the game hangs it in the left hand (its WepAbsorpPosParam dummy); a shield counts by its face.");
            if (equipped != null)
            {
                var e = equipped.Value;
                Label("", column);
                bool m = e.must;
                if (ImGui.Checkbox($"##LeftClear.Equipped", ref m))
                    equipped = (e.id, m);
                ImGui.SameLine();
                ImGui.Text($"{Name(e.id)}  (equipped)");
            }
            for (int i = 0; i < list.Count; i++)
            {
                Label("", column);
                bool m = list[i].must;
                if (ImGui.Checkbox($"##LeftClear.Must{i}", ref m))
                    list[i] = (list[i].id, m);
                ImGui.SameLine();
                ImGui.Text(Name(list[i].id));
                ImGui.SameLine();
                if (Tools.SimpleClickButton($"Remove##LeftClear.Remove{i}"))
                {
                    list.RemoveAt(i);
                    break;
                }
            }
            Label("", column);
            ImGui.PushItemWidth(110 * Main.DPI);
            ImGui.InputTextWithHint("##LeftClear.Add", "weapon ID", ref addId, 12);
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (Tools.SimpleClickButton("Add", int.TryParse(addId.Trim(), out int newId)))
            {
                if (!tae.ParentDocument.ParamManager.EquipParamWeapon.ContainsKey(newId))
                    error = $"no weapon {newId} in the params";
                else if (!list.Any(x => x.id == newId) && equipped?.id != newId)
                {
                    list.Add((newId, true));
                    addId = "";
                    error = null;
                }
            }
            ImGui.Separator();

            Label("Limits", column);
            ImGui.Text("wrist move (cm)  roll  bend 1  bend 2  elbow swing (degrees)");
            Help("How far the solve may change the arm, frame by frame:\n"
                 + "the wrist's move (the arm follows by two-bone IK); the hand's roll away from the palm-in turn (the palm turned toward\n"
                 + "the torso); the wrist's two bends; the elbow's swing about the shoulder-wrist line. Larger limits clear more and move\n"
                 + "the arm further. Defaults: 12 cm, 30, 60, 60, 60.");
            Label("", column);
            ImGui.PushItemWidth(70 * Main.DPI);
            for (int i = 0; i < 5; i++)
            {
                double v = lim[i];
                ImGui.InputDouble($"##LeftClear.Lim{i}", ref v, 0, 0, "%.0f");
                lim[i] = Math.Clamp(v, 1, i == 0 ? 50 : 90);
                if (i < 4)
                    ImGui.SameLine();
            }
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (Tools.SimpleClickButton("Defaults"))
                lim = new double[] { 12, 30, 60, 60, 60 };

            Label("Pins", column);
            ImGui.PushItemWidth(200 * Main.DPI);
            ImGui.InputTextWithHint("##LeftClear.Pins", "e.g. 3-19, 27", ref pins, 128);
            ImGui.PopItemWidth();
            Help("Frames (the timeline's, 30 a second) where the arm keeps the animation's own pose: a strike with the left hand,\n"
                 + "a few frames before the hit to its end. The item may touch the body there.");
            Label("Hold", column);
            ImGui.Checkbox("Hold the item the game's way##LeftClear.Hold", ref hold);
            Help("Keys L_Weapon as every vanilla one-handed attack keys it (animations exported from 3ds Max often leave it at the\n"
                 + "skeleton's reference, 20 degrees off the game's grip).");
            ImGui.Separator();

            var items = new List<(int, bool)>();
            if (equipped != null)
                items.Add(equipped.Value);
            items.AddRange(list);
            if (error != null)
                ImGui.TextColored(Red, "Error: " + error);
            ImGui.TextDisabled("The solve runs in the background (a minute or a few); then a preview, and the copy is written only on 'Write the copy'.");
            ImGui.Text(" ");
            if (Tools.SimpleClickButton("Cancel"))
            {
                SaveSettings();
                Dismiss();
                return;
            }
            ImGui.SameLine();
            if (Tools.SimpleClickButton("Solve", items.Count > 0))
            {
                SaveSettings();
                error = LeftClearFix.Start(tae, items, new LcLimits { MoveCm = lim[0], Roll = lim[1], Bend1 = lim[2], Bend2 = lim[3], Swing = lim[4] }, pins, hold);
            }
        }
    }
}
