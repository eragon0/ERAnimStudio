// ERAnimStudio: 'Duplicate Animation (Crop)...' in the Edit menu, after 'Duplicate Animation' (Ctrl+Insert): the current animation
// duplicated to a new ID together with its hkx, both cropped to a frame range when one is given.
//   - Frames are the editor's, 30 a second, as the timeline shows them. No first and no last frame: no crop, a copy of the entry and
//     of its hkx at the new ID. A first and / or a last frame (a missing one is the source's first / last): the hkx is cut to those
//     frames and the events with it.
//   - Elden Ring projects only: the hkx it writes are Havok 2018.1, Elden Ring's.
//   - The hkx: aTTT_AAAAAA.hkx for the new ID, written at once into the split animation binder picked in the dialog (the character's
//     cNNNN_*.anibnd.dcx in the chr folder of the tab's mod folder; the last one picked is offered first). A whole copy is the source's bytes; a crop or a mirror is decoded, edited, written uncompressed and
//     spline-compressed by Havok Content Tools 2018.1 (HkxEdit), read back and compared. Without Havok Content Tools a crop or a
//     mirror refuses to start. The name already in any binder the editor loaded is an error: nothing is done.
//   - The TAE entry: made as 'Duplicate Animation' makes it (ImmediateImportAnim: the source's events and tracks, the reference chain
//     followed, unnamed), then set to play its own hkx (no import), the source's loop and delay-load flags kept. The ID already in
//     the TAE: the editor's own question ('Anim ID Already Exists ... Continue anyways?'), and yes adds a second entry with that ID.
//   - 'Mirror the hkx': left and right swapped after the crop (HkxEditOps.Mirror); the events stay as they are.
//   - With more than one tab open, the dialog asks which tab the copy goes into: the entry into that tab's project, the hkx into
//     that tab's mod folder's binder (a tab without one is listed with the reason it can't take the copy).
//   - 'Move the original there', next to the new ID: the original entry goes to the new ID and the copy takes its place; the
//     original's own hkx in the binder is renamed with it and the copy is written under its old name, and entries importing the
//     original follow it. Within the source's own tab only, to a free ID.
//   - The new entry goes into the category's list in ID order.
//   - Events of a crop: an event wholly before the first frame or starting after the last one is dropped; one running over the
//     first frame starts at frame 0; one running past the last frame keeps its part after the end; all move back by the first frame.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using DSAnimStudio.HkxEdit;
using DSAnimStudio.ImguiOSD;
using ImGuiNET;
using SoulsAssetPipeline.Animation;

namespace DSAnimStudio.TaeEditor
{
    // what the operation tells and asks the user: the editor's dialogs, or the self-test's recorder
    public interface IDuplicateCropUi
    {
        void Error(string title, string text);
        void AskYesNo(string title, string question, Action<bool> onChoose);
        void Done(string text);
    }

    sealed class DuplicateCropUi : IDuplicateCropUi
    {
        public void Error(string title, string text) => DialogManager.DialogOK(title, text);

        public void AskYesNo(string title, string question, Action<bool> onChoose) =>
            DialogManager.AskYesNo(title, question, onChoose, true,
                Dialog.InputFlag.EnterKeyToAccept | Dialog.InputFlag.EscapeKeyToCancel | Dialog.InputFlag.TitleBarXToCancel);

        public void Done(string text) => zzz_NotificationManagerIns.PushNotification(text, 6f, 0.5f);
    }

    public static class DuplicateCrop
    {
        public const string MenuText = "Duplicate Animation (Crop)...";
        public const string Shortcut = "Ctrl+Insert";
        const double Fps = HkxEditPipeline.EditorFps;
        const double Eps = 0.0005;          // seconds: well under a frame
        static volatile bool busy;
        public static bool Busy => busy;

        public sealed class Request
        {
            public TaeEditorScreen Tae;     // the tab of the source entry
            public TaeEditorScreen Dest;    // the tab the copy goes into, Tae unless another one was chosen
            public bool OtherTab => Dest != Tae;
            public DSAProj.Animation Source;
            public SplitAnimID SourceId;
            public SplitAnimID Target;
            public int? First, Last;        // editor frames; both null: a whole copy
            public string Binder;           // the binder the hkx goes into
            public SplitAnimID SourceHkx;
            public byte[] SourceBytes;
            public byte[] SourceCompendium; // the source's types when they are in its binder's compendium
            public HkxSkeleton Skeleton;
            public HctCompressor.Found Hct;
            public NewAnimationContainer Container;
            public bool Mirror;             // left and right swapped; events as they are
            public bool Whole => First == null && Last == null;
            public bool Edits => !Whole || Mirror;   // decoded and rewritten (needs Havok Content Tools)
            public bool MoveOriginal;       // the original entry goes to the new ID and the copy takes its place
            public bool OwnHkx;             // the source plays its own ID's hkx, so that file can swap too
        }

        public static bool Enabled(TaeEditorScreen tae) =>
            tae != null && tae.IsFileOpen && tae.SelectedAnim != null && !tae.SelectedAnim.IS_DUMMY_ANIM && !busy && IsEldenRing(tae);

        public static void ShowDialog(TaeEditorScreen tae)
        {
            if (tae?.FileContainer == null || tae.SelectedAnimCategory == null)
                return;
            var anim = tae.SelectedAnim;
            if (anim == null || anim.IS_DUMMY_ANIM)
                return;
            DialogManager.AddDialog(new DuplicateCropDialog(tae, anim));
        }

        public static string HkxName(SplitAnimID id) => $"a{id.CategoryID:D3}_{id.SubID:D6}";

        public static NewAnimationContainer Container(TaeEditorScreen tae) =>
            tae?.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer ?? tae?.ParentDocument?.Scene?.MainModel?.AnimContainer;

        /// <summary>Crops, mirrors and the hkx they write are Elden Ring's (Havok 2018.1): the feature works on Elden Ring projects only.</summary>
        public static bool IsEldenRing(TaeEditorScreen tae) =>
            tae?.ParentDocument?.GameRoot?.GameType == SoulsAssetPipeline.SoulsGames.ER;

        // the character the tab's anibnd belongs to (c0000 for c0000.anibnd.dcx), null for another file
        static string Character(TaeEditorScreen tae)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(tae?.NewFileContainerName ?? ""), @"^(c\d{4})\.anibnd\.dcx$",
                                                               System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>
        /// The split animation binders the tab's new hkx can go into: the character's cNNNN_*.anibnd.dcx in the chr folder of the tab's
        /// mod folder (the ModEngine directory of its workspace).
        /// </summary>
        public static List<string> DestBinders(TaeEditorScreen tae, out string folder, out string error)
        {
            error = null;
            folder = null;
            if (!IsEldenRing(tae))
            {
                error = "Elden Ring projects only (the hkx this writes are Elden Ring's)";
                return new List<string>();
            }
            var data = tae.ParentDocument?.GameData;
            string chr = Character(tae);
            if (chr == null)
            {
                error = $"only a character's anibnd (cNNNN.anibnd.dcx): its hkx go into the character's split binders (this file: {Path.GetFileName(tae.NewFileContainerName ?? "none")})";
                return new List<string>();
            }
            string mod = data?.ModengineRootPath;
            if (string.IsNullOrWhiteSpace(mod))
            {
                error = "the project has no mod folder (ModEngine directory), so the new hkx has no binder to go into";
                return new List<string>();
            }
            folder = Path.GetFullPath(Path.Combine(mod, "chr"));
            var list = Directory.Exists(folder)
                ? Directory.GetFiles(folder, chr + "_*.anibnd.dcx").Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (list.Count == 0)
                error = $"no split animation binder ({chr}_*.anibnd.dcx) in {folder}";
            return list;
        }

        // the binder picked last, offered first the next time (kept in the config); the mirror and the tab, for the session
        public static string LastBinderName;
        public static bool LastMirror;
        public static zzz_DocumentIns LastTabDocument;

        /// <summary>
        /// The binder offered first: the one picked last, else one the game doesn't ship (not in the game folder's chr folder),
        /// the largest of them (a mod's own binder holds its animations), else the first.
        /// </summary>
        public static int DefaultBinder(TaeEditorScreen tab, List<string> binders)
        {
            if (binders.Count == 0)
                return 0;
            int last = binders.FindIndex(b => Path.GetFileName(b).Equals(LastBinderName, StringComparison.OrdinalIgnoreCase));
            if (last >= 0)
                return last;
            string gameChr = tab?.ParentDocument?.GameData?.InterrootPath is string g && !string.IsNullOrWhiteSpace(g) ? Path.Combine(g, "chr") : null;
            var own = binders.Select((b, i) => (b, i))
                .Where(x => gameChr == null || !File.Exists(Path.Combine(gameChr, Path.GetFileName(x.b))))
                .OrderByDescending(x => new FileInfo(x.b).Length).ToList();
            return own.Count > 0 ? own[0].i : 0;
        }

        /// <summary>What the dialog shows as the user types: the new ID's state, what Accept will do, or why it can't yet.</summary>
        public sealed class LookResult
        {
            public string IdStatus;
            public int IdLevel;         // 0 fine, 1 a question will follow, 2 refused
            public string Summary;
            public string Problem;      // null: Accept can go ahead (the full check still runs)
            public bool ProblemIsHct;
        }

        public static LookResult Look(TaeEditorScreen tae, DSAProj.Animation source, string target, string first, string last, bool mirror,
                                      TaeEditorScreen dest, bool move, string binder, int? sourceLast, bool hctFound, string binderError)
        {
            var r = new LookResult();
            var proj = tae?.Proj;
            var destProj = dest?.Proj;
            if (proj == null || destProj == null || source == null)
            {
                r.Problem = "No animation selected.";
                return r;
            }
            SplitAnimID id = default;
            bool idOk = false;
            string t = (target ?? "").Trim();
            if (t.Length == 0)
                r.Problem = "Enter the new animation ID.";
            else if (!SplitAnimID.TryParse(destProj, t, out id, out string err))
            {
                r.IdStatus = err ?? "not an animation ID";
                r.IdLevel = 2;
            }
            else if (id.CategoryID < 0 || id.CategoryID > 999 || id.SubID < 0 || id.SubID > 999999)
            {
                r.IdStatus = "an ID aTTT_AAAAAA is needed for the hkx name";
                r.IdLevel = 2;
            }
            else
            {
                var dc = Container(dest);
                bool entry = destProj.SAFE_GetFirstAnimationFromFullID(id) != null;
                if (move && dest == tae && id == source.SplitID)
                    (r.IdStatus, r.IdLevel) = ("the original's own ID", 2);
                else if (dc != null && dc.AnimExists(id))
                    (r.IdStatus, r.IdLevel) = ($"{HkxName(id)}.hkx is already loaded: pick another ID", 2);
                else if (entry && move)
                    (r.IdStatus, r.IdLevel) = ("taken in the TAE: a move needs a free ID", 2);
                else if (entry)
                    (r.IdStatus, r.IdLevel, idOk) = ("an entry has this ID: you'll be asked whether to add a second one", 1, true);
                else
                    (r.IdStatus, r.IdLevel, idOk) = ("free", 0, true);
            }
            if (r.IdLevel == 2)
                r.Problem = "New ID: " + r.IdStatus + ".";
            int? f0 = null, f1 = null;
            if (r.Problem == null)
            {
                if (!ParseFrame(first, "first", out f0, out string ferr) || !ParseFrame(last, "last", out f1, out ferr))
                    r.Problem = "Frames: " + ferr + ".";
                else if (f0 != null && f1 != null && f0 >= f1)
                    r.Problem = "Frames: the first frame must come before the last one.";
                else if (sourceLast != null && (f0 > sourceLast || f1 > sourceLast))
                    r.Problem = $"Frames: the source has frames 0-{sourceLast}.";
            }
            if (r.Problem == null && move && dest != tae)
                r.Problem = "A move works within the source's own tab: pick this tab, or copy instead.";
            if (r.Problem == null && (f0 != null || f1 != null || mirror) && !hctFound)
            {
                r.Problem = "A crop or a mirror needs Havok Content Tools " + HkxEdit.HctCompressor.RequiredVersion
                            + ", which was not found: set its path in Toolbox > [Havok Content Tools], or make a whole copy.";
                r.ProblemIsHct = true;
            }
            if (r.Problem == null && binder == null)
                r.Problem = "No binder to write into: " + binderError + ".";
            if (idOk && binder != null)
            {
                string what = f0 == null && f1 == null ? "a whole copy" : $"frames {f0 ?? 0}-{f1?.ToString() ?? sourceLast?.ToString() ?? "end"}";
                if (mirror)
                    what += ", mirrored";
                string bn = Path.GetFileName(binder), idName = id.GetFormattedIDString(destProj, null), srcName = source.SplitID.GetFormattedIDString(proj, null);
                r.Summary = move
                    ? $"Accept moves {srcName} to {idName}, and the copy ({what}) takes {srcName}. The hkx is written into {bn} at once, not with the project's save."
                    : $"Accept writes {HkxName(id)}.hkx ({what}) into {bn} at once, not with the project's save, and adds the entry {idName}"
                      + (dest != tae ? $" to the tab {TabName(dest)}." : ".");
            }
            return r;
        }

        static bool ParseFrame(string text, string which, out int? frame, out string error)
        {
            frame = null;
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return true;
            if (!int.TryParse(text, out int f) || f < 0)
            {
                error = $"the {which} frame '{text}' is not a frame number (0 or more)";
                return false;
            }
            frame = f;
            return true;
        }

        // the source's hkx length in editor frames, when the editor has it decoded (the selected animation plays it)
        public static int? SourceLastFrame(NewAnimationContainer c, SplitAnimID hkx)
        {
            try
            {
                var cur = c?.CurrentAnimationID;
                if (cur == null || cur.Value.CategoryID != hkx.CategoryID || cur.Value.SubID != hkx.SubID)
                    return null;
                var a = c.CurrentAnimation;
                return a == null ? null : (int)Math.Round(a.Duration * Fps);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A line on Havok Content Tools for the dialog: found (where, which version), or what is missing.</summary>
        public static string HctStatus(out bool found)
        {
            var hct = HctCompressor.Find(out string why);
            found = hct != null;
            return found ? $"Havok Content Tools {hct.Version} ({hct.Source}): crops and mirrors are available."
                         : "Crops and mirrors need Havok Content Tools " + HctCompressor.RequiredVersion + ", which was not found (whole copies don't).";
        }

        // the checks before anything is written; null when the request can go ahead
        public static string Check(TaeEditorScreen tae, DSAProj.Animation source, string target, string first, string last, out Request req,
                                   bool mirror = false, TaeEditorScreen dest = null, bool move = false, string binder = null)
        {
            req = null;
            dest ??= tae;
            var proj = tae?.Proj;
            var destProj = dest?.Proj;
            if (proj == null || source == null)
                return "no animation selected";
            if (!IsEldenRing(tae) || !IsEldenRing(dest))
                return "Elden Ring projects only (the hkx this writes are Elden Ring's)";
            if (destProj == null || !dest.IsFileOpen)
                return "the tab to copy into has no file open";
            if (busy)
                return "a duplicate is still being written";
            if (!SplitAnimID.TryParse(destProj, (target ?? "").Trim(), out var id, out string err))
                return err ?? "not an animation ID";
            if (id.CategoryID < 0 || id.CategoryID > 999 || id.SubID < 0 || id.SubID > 999999)
                return $"{target}: an ID aTTT_AAAAAA is needed for the hkx name";
            if (!ParseFrame(first, "first", out var f0, out err) || !ParseFrame(last, "last", out var f1, out err))
                return err;
            if (f0 != null && f1 != null && f0 >= f1)
                return "the first frame must come before the last one";
            HctCompressor.Found hct = null;
            if (f0 != null || f1 != null || mirror)
            {
                // a crop or a mirror is written by Havok Content Tools: refused before anything runs when it isn't there
                hct = HctCompressor.Find(out string why);
                if (hct == null)
                    return why + ". Install Havok Content Tools " + HctCompressor.RequiredVersion + ", or set the "
                           + HctCompressor.EnvironmentVariable + " environment variable to its " + HctCompressor.ExeName + " (a whole copy works without it).";
            }
            var c = Container(tae);
            if (c == null)
                return "no model is loaded, so there are no animations to copy";
            var hkx = source.GetHkxID(proj);
            if (!hkx.IsValid)
                return "the source entry plays no hkx";
            var bytes = c.FindAnimationBytes(hkx);
            if (bytes?.HkxBytes == null)
                return $"the source's hkx {HkxName(hkx)} is not loaded";
            int? lastFrame = SourceLastFrame(c, hkx);
            if (lastFrame != null && (f0 > lastFrame || f1 > lastFrame))
                return $"the source's hkx has frames 0-{lastFrame}";
            HkxSkeleton skeleton = null;
            if (hct != null)
            {
                var sk = c.Skeleton?.OriginalHavokSkeleton;
                if (sk == null)
                    return "the model has no skeleton loaded to crop or mirror on";
                skeleton = HkxSkeleton.FromHavok(sk);
                if (mirror && HkxAnimationOps.ReferenceAsymmetry(skeleton) > 0.001)
                    return "the skeleton's reference pose isn't left / right symmetric, so it can't be mirrored";
            }
            var dc = Container(dest);
            if (dc == null)
                return "the tab to copy into has no model loaded";
            if (dc.AnimExists(id))
                return $"{HkxName(id)}.hkx is already in a binder the editor loaded: pick another ID";
            var binders = DestBinders(dest, out _, out err);
            if (binders.Count == 0)
                return err;
            binder ??= binders[0];
            if (!binders.Contains(binder, StringComparer.OrdinalIgnoreCase))
                return $"{binder} is not one of the tab's split animation binders";
            if (move)
            {
                if (dest != tae)
                    return "moving the original works only within its own tab: pick this tab, or untick 'Move the original there'";
                if (id == source.SplitID)
                    return "the new ID is the original's own";
                if (proj.SAFE_GetFirstAnimationFromFullID(id) != null)
                    return $"{id.GetFormattedIDString(proj, null)} is already in the TAE: moving the original needs a free ID";
            }
            req = new Request
            {
                Tae = tae, Dest = dest, Source = source, SourceId = source.SplitID, Target = id, First = f0, Last = f1, Binder = binder,
                SourceHkx = hkx, SourceBytes = bytes.HkxBytes, SourceCompendium = bytes.CompendiumBytes, Skeleton = skeleton, Hct = hct,
                Container = dc, Mirror = mirror, MoveOriginal = move,
                OwnHkx = hkx.CategoryID == source.SplitID.CategoryID && hkx.SubID == source.SplitID.SubID,
            };
            return null;
        }

        // after Check: the editor's question when the ID is taken, then the work. finished(true) once the entry is in, finished(false)
        // when it was refused, failed or declined
        public static void Start(Request r, IDuplicateCropUi ui, Action<bool> finished = null)
        {
            finished ??= _ => { };
            var proj = r.Dest.Proj;
            var existing = proj.SAFE_GetFirstAnimationFromFullID(r.Target);
            if (existing == null || r.MoveOriginal)   // a move's free ID was checked
            {
                Run(r, ui, null, finished);
                return;
            }
            ui.AskYesNo("Anim ID Already Exists",
                "An animation already exists with ID " + r.Target.GetFormattedIDString(proj, null) + ". Duplicating to this ID will cause an ID conflict. Continue anyways?",
                yes =>
                {
                    if (yes)
                        Run(r, ui, existing, finished);
                    else
                        finished(false);
                });
        }

        sealed class Written
        {
            public byte[] Hkx;
            public HkxBinderWriter.Result Binder;
            public HkxEditPipeline.Report Edit;   // null for a whole copy
        }

        static void Run(Request r, IDuplicateCropUi ui, DSAProj.Animation existing, Action<bool> finished)
        {
            busy = true;
            // a move with the original's own hkx: the files swap (the original renamed to the new ID's name when the binder has it, the
            // copy under the original's name); any other move or copy: the copy's hkx is the new ID's
            bool swap = r.MoveOriginal && r.OwnHkx;
            string name = swap ? HkxName(r.SourceId) : HkxName(r.Target);
            string moveTo = swap ? HkxName(r.Target) : null;
            string what = (r.Whole ? "a whole copy" : $"frames {(r.First?.ToString() ?? "0")}-{(r.Last?.ToString() ?? "end")}") + (r.Mirror ? ", mirrored" : "");
            var doc = r.Tae.ParentDocument;
            string title = r.MoveOriginal ? $"Moving {HkxName(r.SourceId)} to {HkxName(r.Target)}, the copy in its place ({what}) ..." : $"Duplicating to {name} ({what}) ...";
            bool started = doc.LoadingTaskMan.DoLoadingTask("DuplicateCrop", title, prog =>
            {
                try
                {
                    var w = new Written();
                    w.Hkx = r.Edits
                        ? HkxEditPipeline.Make(r.SourceBytes, r.SourceCompendium, r.Skeleton, r.First, r.Last, r.Mirror, r.Hct, out w.Edit)
                        : r.SourceBytes;
                    w.Binder = HkxBinderWriter.Add(r.Binder, w.Hkx, name, moveTo, r.SourceBytes);
                    Main.WinForm.Invoke(new Action(() =>
                    {
                        try
                        {
                            string done = Apply(r, w, existing);
                            busy = false;
                            ui.Done(done);
                            finished(true);
                        }
                        catch (Exception e)
                        {
                            busy = false;
                            ui.Error("Duplication Failed", $"{w.Binder.Name}.hkx is in {Path.GetFileName(r.Binder)}, but the TAE entry failed: {e.Message}");
                            finished(false);
                        }
                    }));
                }
                catch (HkxEditRefused e)
                {
                    busy = false;
                    ui.Error("Duplication Failed", e.Message);
                    finished(false);
                }
                catch (Exception e)
                {
                    busy = false;
                    ui.Error("Duplication Failed", $"{e.GetType().Name}: {e.Message}");
                    finished(false);
                }
            }, 0, false, 16, false, zzz_LoadingTaskManIns.TaskFlags.Critical);
            if (!started)
            {
                busy = false;
                ui.Error("Duplication Failed", "the previous duplicate is still running");
                finished(false);
            }
        }

        // on the editor's thread: the hkx registered, the entry made as Duplicate Animation makes it, then its own hkx and the crop
        static string Apply(Request r, Written w, DSAProj.Animation existing)
        {
            if (r.MoveOriginal)
                return ApplyMove(r, w);
            var tae = r.Dest;
            var proj = tae.Proj;
            string name = HkxName(r.Target);
            r.Container.AddNewHKXToLoad(r.Target, name + ".hkx", w.Hkx, w.Binder.CompendiumName);
            var category = existing != null ? existing.ParentCategory : proj.SAFE_RegistCategory(r.Target.CategoryID);
            var anim = new DSAProj.Animation(proj, category);
            if (r.OtherTab)
                CopyEvents(r.Source, r.Tae.Proj, anim);
            else
                r.Tae.ImmediateImportAnim(anim, r.SourceId);
            anim.SplitID = r.Target;
            anim.SAFE_SetHeader(OwnHkxHeader(r.Source, r.Tae.Proj));
            int dropped = 0, kept = 0;
            string range = "";
            if (!r.Whole)
            {
                (dropped, kept) = CropEvents(anim, w.Edit.First / Fps, w.Edit.Last / Fps);
                range = $" frames {w.Edit.First}-{w.Edit.Last},";
            }
            anim.SAFE_SetIsModified(true);
            category.SAFE_AddAnimation(anim, -1);
            category.SAFE_ResortAnimIDs();
            tae.RecreateAnimList();
            tae.SelectNewAnimRef(category, anim, false, false, -1f, false, false);
            if (r.Mirror)
                range += " mirrored,";
            string text = $"Duplicated to {r.Target.GetFormattedIDString(proj, null)}{(r.OtherTab ? " in the tab " + TabName(tae) : "")}:{range} {name}.hkx written into {r.Binder}";
            if (!r.Whole)
                text += $"; events: {kept} kept, {dropped} dropped";
            return text;
        }

        // 'Move the original there': the original entry goes to the new ID T and the copy takes its ID S.
        //   - hkx: when the original plays its own aTTT_S.hkx and the binder has it, that file was renamed to aTTT_T.hkx and the copy
        //     written as aTTT_S.hkx, so each entry plays its own file; otherwise (a file of another binder, or an original importing
        //     another hkx) nothing was renamed, the copy's file is aTTT_T.hkx and the copy imports it, and an original that played its
        //     own hkx imports aTTT_S.hkx from its new ID.
        //   - Other entries follow the original: a type-1 entry importing S now imports T; when the files swapped, an entry importing
        //     the hkx aTTT_S imports aTTT_T, so every one plays what it played before.
        static string ApplyMove(Request r, Written w)
        {
            var tae = r.Tae;
            var proj = tae.Proj;
            var original = r.Source;
            SplitAnimID s = r.SourceId, t = r.Target;
            int fullS = s.GetFullID(proj, null), fullT = t.GetFullID(proj, null);
            bool swapped = w.Binder.Swapped;
            if (swapped)
                r.Container.SwapLoadedHkx(s, t, HkxName(s) + ".hkx", HkxName(t) + ".hkx", w.Hkx, w.Binder.CompendiumName);
            else
                r.Container.AddNewHKXToLoad(t, HkxName(t) + ".hkx", w.Hkx, w.Binder.CompendiumName);
            var originalHeader = original.SAFE_GetHeaderClone();
            var category = original.ParentCategory;
            // the copy, made from the original before it moves
            var anim = new DSAProj.Animation(proj, category);
            tae.ImmediateImportAnim(anim, s);
            anim.SplitID = s;
            var own = OwnHkxHeader(original, proj);
            if (!swapped)
            {
                own.ImportsHKX = true;
                own.ImportHKXSourceAnimID = fullT;
            }
            anim.SAFE_SetHeader(own);
            int dropped = 0, kept = 0;
            string range = "";
            if (!r.Whole)
            {
                (dropped, kept) = CropEvents(anim, w.Edit.First / Fps, w.Edit.Last / Fps);
                range = $" frames {w.Edit.First}-{w.Edit.Last},";
            }
            if (r.Mirror)
                range += " mirrored,";
            // the original to T (the editor's own ID change: out of its category, into T's, sorted)
            proj.SAFE_RegistAnimationIDChange(original, s, t);
            if (originalHeader is TAE.Animation.AnimFileHeader.Standard oh)
            {
                if (swapped && oh.ImportsHKX && oh.ImportHKXSourceAnimID == fullS)
                    oh.ImportHKXSourceAnimID = fullT;
                else if (!swapped && r.OwnHkx && !oh.ImportsHKX)
                {
                    oh.ImportsHKX = true;
                    oh.ImportHKXSourceAnimID = fullS;
                }
                original.SAFE_SetHeader(oh);
            }
            original.SAFE_SetIsModified(true);
            // the other entries follow the original
            var followed = new List<string>();
            foreach (var cat in proj.SAFE_GetAllAnimCategories())
            {
                foreach (var e in cat.SAFE_GetAnimations().ToList())
                {
                    if (e == original)
                        continue;
                    var h = e.SAFE_GetHeaderClone();
                    bool changed = false;
                    if (h is TAE.Animation.AnimFileHeader.ImportOtherAnim io && io.ImportFromAnimID == fullS)
                    {
                        io.ImportFromAnimID = fullT;
                        changed = true;
                    }
                    else if (swapped && h is TAE.Animation.AnimFileHeader.Standard st && st.ImportsHKX && st.ImportHKXSourceAnimID == fullS)
                    {
                        st.ImportHKXSourceAnimID = fullT;
                        changed = true;
                    }
                    if (changed)
                    {
                        e.SAFE_SetHeader(h);
                        e.SAFE_SetIsModified(true);
                        followed.Add(e.SplitID.GetFormattedIDString(proj, null));
                    }
                }
            }
            anim.SAFE_SetIsModified(true);
            category.SAFE_AddAnimation(anim, -1);
            category.SAFE_ResortAnimIDs();
            tae.RecreateAnimList();
            tae.SelectNewAnimRef(category, anim, false, false, -1f, false, false);
            string sName = s.GetFormattedIDString(proj, null), tName = t.GetFormattedIDString(proj, null);
            string text = $"Moved {sName} to {tName}" + (swapped ? $" with its hkx (now {HkxName(t)}.hkx)" : r.OwnHkx ? $", importing {HkxName(s)}.hkx" : "")
                          + $"; the copy took {sName}:{range} {(swapped ? HkxName(s) : HkxName(t))}.hkx written into {r.Binder}";
            if (!r.Whole)
                text += $"; events: {kept} kept, {dropped} dropped";
            if (followed.Count > 0)
                text += $"; {followed.Count} importing entr{(followed.Count == 1 ? "y" : "ies")} now on {tName}: {string.Join(", ", followed)}";
            return text;
        }

        // into another tab's project: what ImmediateImportAnim copies within one: the events of the entry the source resolves to (a
        // type-1 entry's chain followed), cloned from its template, and its event groups
        static void CopyEvents(DSAProj.Animation source, DSAProj sourceProj, DSAProj.Animation anim)
        {
            var from = source;
            if (source.SAFE_GetHeaderClone() is TAE.Animation.AnimFileHeader.ImportOtherAnim)
                from = sourceProj.SAFE_SolveAnimRefChain(source.SplitID) ?? source;
            foreach (var a in from.SAFE_GetActions().ToList())
                anim.SAFE_AddAction(a.GetClone(true));
            foreach (var t in from.SAFE_GetActionTracks().ToList())
                anim.SAFE_AddActionTrack(t.GetClone());
        }

        // the tab's own name says only the file ('[ER]c0000*' in every c0000 tab), so the folder the file sits in goes with it: the
        // anibnd's chr folder's parent, the mod folder (the '*' is the unsaved mark, left out)
        public static string TabName(TaeEditorScreen tae)
        {
            string n = tae?.ParentDocument?.ImguiTabDisplayName?.TrimEnd('*');
            string file = tae?.NewFileContainerName;
            if (string.IsNullOrWhiteSpace(n))
                n = Path.GetFileName(file ?? "?");
            string dir = string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file);
            if (dir != null && Path.GetFileName(dir).Equals("chr", StringComparison.OrdinalIgnoreCase))
                dir = Path.GetDirectoryName(dir);
            return dir != null ? $"{n} in {dir}" : n;
        }

        // a standard entry playing its own hkx, with the loop and delay-load flags of the entry the source resolves to
        public static TAE.Animation.AnimFileHeader.Standard OwnHkxHeader(DSAProj.Animation source, DSAProj proj)
        {
            var h = source.SAFE_GetHeaderClone();
            if (h is TAE.Animation.AnimFileHeader.ImportOtherAnim)
            {
                var end = proj.SAFE_SolveAnimRefChain(source.SplitID);
                if (end != null)
                    h = end.SAFE_GetHeaderClone();
            }
            var std = new TAE.Animation.AnimFileHeader.Standard { ImportsHKX = false, ImportHKXSourceAnimID = -1 };
            if (h is TAE.Animation.AnimFileHeader.Standard s)
            {
                std.IsLoopByDefault = s.IsLoopByDefault;
                std.AllowDelayLoad = s.AllowDelayLoad;
            }
            return std;
        }

        // wholly before t0 or starting after t1: dropped; over t0: starts at 0; past t1: keeps its end
        public static (int dropped, int kept) CropEvents(DSAProj.Animation anim, double t0, double t1)
        {
            int dropped = 0, kept = 0;
            foreach (var a in anim.SAFE_GetActions().ToList())
            {
                var (keep, s, e) = CropTimes(a.StartTime, a.EndTime, t0, t1);
                if (!keep)
                {
                    anim.SAFE_RemoveAction(a);
                    dropped++;
                    continue;
                }
                a.StartTime = s;
                a.EndTime = e;
                kept++;
            }
            return (dropped, kept);
        }

        public static (bool keep, float start, float end) CropTimes(double start, double end, double t0, double t1)
        {
            if (end <= t0 + Eps || start > t1 + Eps)
                return (false, 0, 0);
            return (true, Snap(Math.Max(start - t0, 0)), Snap(end - t0));
        }

        // back on the 30 fps grid when the time was on it (float subtraction leaves 0.20000002)
        static float Snap(double t)
        {
            double f = t * Fps;
            return Math.Abs(f - Math.Round(f)) < 0.01 ? (float)(Math.Round(f) / Fps) : (float)t;
        }
    }

    // the dialog, in sections: the source; the mode and the new ID (checked as it is typed); the frames (with the playhead) and the
    // mirror; where it goes (tab and binder); a summary of what Accept will do, written at once and not with the project's save
    sealed class DuplicateCropDialog : Dialog
    {
        readonly TaeEditorScreen tae;
        readonly DSAProj.Animation source;
        string target = "", first = "", last = "", error;
        bool mirror;
        int mode;   // 0: copy to a new ID; 1: move the original to the new ID, the copy stays at its ID
        readonly string sourceText;
        readonly int? sourceLast;
        readonly bool hctFound;
        readonly string hctWhy;
        readonly List<TaeEditorScreen> tabs = new();
        readonly string[] tabNames;
        // per tab: its split binders (full paths, and names for the list), their folder, or why it has none
        readonly List<string>[] binders;
        readonly string[][] binderNames;
        readonly string[] binderFolders, binderErrors;
        readonly int[] binderPick;
        int tab;

        static readonly Vector4 Green = new Vector4(0.45f, 0.9f, 0.45f, 1f);
        static readonly Vector4 Amber = new Vector4(1f, 0.75f, 0.3f, 1f);
        static readonly Vector4 Red = new Vector4(1f, 0.4f, 0.4f, 1f);

        public DuplicateCropDialog(TaeEditorScreen tae, DSAProj.Animation source) : base("Duplicate Animation (Crop)")
        {
            this.tae = tae;
            this.source = source;
            CancelHandledByInheritor = true;
            AcceptHandledByInheritor = true;
            AllowedResultTypes = ResultTypes.Accept | ResultTypes.Cancel;
            InputFlags = InputFlag.EnterKeyToAccept | InputFlag.EscapeKeyToCancel | InputFlag.TitleBarXToCancel;
            var proj = tae.Proj;
            var hkx = source.GetHkxID(proj);
            var c = DuplicateCrop.Container(tae);
            sourceLast = DuplicateCrop.SourceLastFrame(c, hkx);
            sourceText = $"{source.SplitID.GetFormattedIDString(proj, null)}, plays {(hkx.IsValid ? DuplicateCrop.HkxName(hkx) : "no hkx")}"
                         + (sourceLast != null ? $", frames 0-{sourceLast}" : "");
            hctFound = HkxEdit.HctCompressor.Find(out hctWhy) != null;
            mirror = DuplicateCrop.LastMirror;
            tabs.Add(tae);
            tabs.AddRange(zzz_DocumentManager.GetOpenFileDocuments().Select(d => d.EditorScreen).Where(s => s != tae));
            tabNames = tabs.Select(s => DuplicateCrop.TabName(s) + (s == tae ? " (this tab)" : "")).ToArray();
            tab = Math.Max(0, tabs.FindIndex(s => s.ParentDocument == DuplicateCrop.LastTabDocument));
            binders = new List<string>[tabs.Count];
            binderNames = new string[tabs.Count][];
            binderFolders = new string[tabs.Count];
            binderErrors = new string[tabs.Count];
            binderPick = new int[tabs.Count];
            for (int i = 0; i < tabs.Count; i++)
            {
                binders[i] = DuplicateCrop.DestBinders(tabs[i], out binderFolders[i], out binderErrors[i]);
                binderNames[i] = binders[i].Select(Path.GetFileName).ToArray();
                binderPick[i] = DuplicateCrop.DefaultBinder(tabs[i], binders[i]);
            }
        }

        static void Help(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        void Label(string text, float column)
        {
            ImGui.Text(text);
            ImGui.SameLine();
            ImGui.SetCursorPosX(column);
        }

        int? Playhead()
        {
            var cursor = tae.Graph?.PlaybackCursor;
            return cursor == null ? null : (int)Math.Round(cursor.GUICurrentTimeMod * 30.0);
        }

        protected override void BuildInsideOfWindow()
        {
            float column = 80 * Main.DPI;
            bool move = mode == 1;
            string binder = binders[tab].Count > 0 ? binders[tab][binderPick[tab]] : null;

            Label("Source", column);
            ImGui.Text(sourceText);
            ImGui.Separator();

            Label("Mode", column);
            ImGui.RadioButton("Copy to a new ID", ref mode, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Move the original to the new ID", ref mode, 1);
            Help("Copy: the copy gets the new ID and its own hkx; the original stays as it is.\n\n"
                 + "Move: the original goes to the new ID and the copy takes its place. When the original plays its own hkx and the binder\n"
                 + "has it, that file is renamed with it and the copy's hkx gets the original's name. Entries importing the original follow it.\n"
                 + "Within the source's own tab only, to a free ID.");
            move = mode == 1;

            var look = DuplicateCrop.Look(tae, source, target, first, last, mirror, tabs[tab], move, binder, sourceLast, hctFound,
                                          binder == null ? binderErrors[tab] : null);

            Label("New ID", column);
            if (IsFirstFrameBuildingWindow)
                ImGui.SetKeyboardFocusHere();
            ImGui.PushItemWidth(160 * Main.DPI);
            ImGui.InputTextWithHint("##DuplicateCrop.Target", "aTTT_AAAAAA", ref target, 64);
            ImGui.PopItemWidth();
            if (!string.IsNullOrEmpty(look.IdStatus))
            {
                ImGui.SameLine();
                ImGui.TextColored(look.IdLevel == 0 ? Green : look.IdLevel == 1 ? Amber : Red, look.IdStatus);
            }
            ImGui.Separator();

            Label("Frames", column);
            ImGui.Text("First");
            ImGui.SameLine();
            ImGui.PushItemWidth(60 * Main.DPI);
            ImGui.InputTextWithHint("##DuplicateCrop.First", "0", ref first, 8);
            ImGui.SameLine();
            if (Tools.SimpleClickButton("Playhead##DuplicateCrop.First", Playhead() != null))
                first = Playhead()?.ToString() ?? first;
            ImGui.SameLine();
            ImGui.Text("   Last");
            ImGui.SameLine();
            ImGui.InputTextWithHint("##DuplicateCrop.Last", sourceLast?.ToString() ?? "end", ref last, 8);
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (Tools.SimpleClickButton("Playhead##DuplicateCrop.Last", Playhead() != null))
                last = Playhead()?.ToString() ?? last;
            Help("Frames at 30 fps, as the timeline shows them; 'Playhead' takes the timeline's current frame.\n"
                 + "Both empty: a whole copy, no crop. One empty: the source's first or last frame.\n\n"
                 + "The events are cropped with the frames: an event wholly outside the range is dropped, one running over the\n"
                 + "first frame starts at 0, one running past the last frame keeps its end, and all move back by the first frame.");

            Label("Mirror", column);
            ImGui.Checkbox("Swap left and right##DuplicateCrop.Mirror", ref mirror);
            Help("The hkx mirrored after the crop: each bone takes its left / right twin's pose, the root motion's sideways\n"
                 + "drift and turn flip. The events stay as they are (an effect on the right hand stays on the right hand).");
            ImGui.Separator();

            if (tabs.Count > 1)
            {
                Label("Tab", column);
                ImGui.PushItemWidth(420 * Main.DPI);
                ImGui.Combo("##DuplicateCrop.Tab", ref tab, tabNames, tabNames.Length);
                ImGui.PopItemWidth();
                Help("The open tab the copy goes into: its TAE gets the entry, its mod folder's binder the hkx.");
            }
            Label("Binder", column);
            if (binders[tab].Count > 0)
            {
                ImGui.PushItemWidth(260 * Main.DPI);
                ImGui.Combo("##DuplicateCrop.Binder", ref binderPick[tab], binderNames[tab], binderNames[tab].Length);
                ImGui.PopItemWidth();
                Help($"The split animation binder the new hkx is written into, in\n{binderFolders[tab]}");
            }
            else
            {
                ImGui.TextColored(Amber, binderErrors[tab]);
            }
            ImGui.Separator();

            // what Accept will do, and why it can't yet
            if (look.Summary != null)
                ImGui.TextWrapped(look.Summary);
            if (look.Problem != null)
                ImGui.TextColored(look.ProblemIsHct ? Amber : Red, look.Problem);
            if (error != null)
                ImGui.TextColored(Red, "Error: " + error);
            ImGui.Text(" ");

            if (Tools.SimpleClickButton("Cancel") || IsTitleBarXRequested || IsEscapeKeyRequested)
            {
                Dismiss();
                return;
            }
            ImGui.SameLine();
            bool ready = look.Problem == null;
            if (Tools.SimpleClickButton("Accept", ready) || (ready && IsEnterKeyRequested))
            {
                error = DuplicateCrop.Check(tae, source, target, first, last, out var req, mirror, tabs[tab], move, binder);
                if (error == null)
                {
                    DuplicateCrop.LastBinderName = Path.GetFileName(binder);
                    DuplicateCrop.LastMirror = mirror;
                    DuplicateCrop.LastTabDocument = tabs[tab].ParentDocument;
                    Dismiss();
                    DuplicateCrop.Start(req, new DuplicateCropUi());
                }
            }
        }
    }
}
