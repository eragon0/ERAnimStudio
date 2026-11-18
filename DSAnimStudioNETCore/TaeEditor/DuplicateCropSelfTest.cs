#if DUPLICATECROP_SELFTEST
// The in-app test of New Animation from Current, compiled only into a build made with -p:DuplicateCropSelfTest=true and run only with
// the DUPLICATECROP_SELFTEST environment variable set to a spec file. Meant for a copy of the program and of a project, never a real
// one. It opens the spec's anibnd (and 'anibnd2' in a second tab), runs each case through the same code as the dialog's Accept
// (Check, then Start; the yes / no question answered by the case), records what happened and the state of every target (entries,
// headers, events, hkx loaded), saves every tab through the program's own save and exits. Spec:
//   {"anibnd": <path>, "anibnd2": <path>, "log": <path>, "save": true, "exit": true, "show": ["a642_032510"],
//    "cases": [{"name": .., "source": "a642_032500", "target": "a642_032597", "first": "10", "last": "23", "mirror": false,
//               "move": false, "src": 0, "dest": 1, "answer": true}, ...]}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DSAnimStudio.TaeEditor
{
    public static class DuplicateCropSelfTest
    {
        static readonly string SpecPath = Environment.GetEnvironmentVariable("DUPLICATECROP_SELFTEST");
        public static bool Active => !string.IsNullOrEmpty(SpecPath);
        static JsonElement spec;
        static string logPath;
        static int stage, frames, caseIndex, waitUntil;
        static bool caseDone, requested2;
        static int savedCount, saveTarget;
        static DateTime started;

        sealed class RecorderUi : IDuplicateCropUi
        {
            readonly bool answer;
            public RecorderUi(bool answer) => this.answer = answer;
            public void Error(string title, string text) => Note($"  error dialog: {title}: {text}");

            public void AskYesNo(string title, string question, Action<bool> onChoose)
            {
                Note($"  question: {title}: {question} -> {(answer ? "yes" : "no")}");
                onChoose(answer);
            }

            public void Done(string text) => Note($"  done: {text}");
        }

        static void Note(string line)
        {
            try
            {
                File.AppendAllText(logPath ?? SpecPath + ".log", line + Environment.NewLine);
            }
            catch
            {
            }
        }

        static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static void Finish(int code)
        {
            Note($"self-test finished ({code}) after {(DateTime.Now - started).TotalSeconds:F0} s");
            stage = -1;
            if (spec.ValueKind != JsonValueKind.Object || !spec.TryGetProperty("exit", out var x) || x.GetBoolean())
                Environment.Exit(code);
        }

        static void Open(string anibnd)
        {
            // as the program's own dsas://open?suppress_prompts=1: the workspace json next to the anibnd taken as it is
            zzz_DocumentManager.CurrentDocument.GameRoot.SuppressNextInterrootBrowsePrompt = true;
            Main.TAE_EDITOR.SuppressNextModelOverridePrompt = true;
            zzz_DocumentManager.RequestFileOpenRecent_SelectedFile = new DSAProj.TaeContainerInfo.ContainerAnibnd(anibnd, null);
            zzz_DocumentManager.RequestFileOpenRecent = true;
        }

        public static void Frame()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Note($"SELF-TEST ERROR at stage {stage}, frame {frames}: {e}");
                // before the file is open the program may not be ready yet (its document, its editor): the next frame tries again
                if (stage == 0 && frames < 600)
                    return;
                Finish(4);
            }
        }

        static void Step()
        {
            if (!Active || stage < 0)
                return;
            frames++;
            if (stage == 0)
            {
                spec = JsonDocument.Parse(File.ReadAllText(SpecPath)).RootElement.Clone();
                logPath = Str(spec, "log") ?? SpecPath + ".log";
                File.WriteAllText(logPath, "");
                started = DateTime.Now;
                Note($"self-test {SpecPath} at {started:yyyy-MM-dd HH:mm:ss}");
                Open(Str(spec, "anibnd"));
                // the file opens in a new document whose 'Setup Workspace' the flags above don't reach: its Apply button, as Enter
                // would, and the 'Choose Character Model' list that follows: OK (a WinForms timer runs in their modal loops)
                var timer = new System.Windows.Forms.Timer { Interval = 500 };
                timer.Tick += (_, _) =>
                {
                    if (stage >= 2 || (DateTime.Now - started).TotalMinutes > 14)
                    {
                        timer.Stop();
                        return;
                    }
                    foreach (var f in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().ToList())
                    {
                        string kind = f.GetType().Name;
                        string button = kind == "TaeGameDirPicker" ? "Apply"
                            : kind == "TaeLoadFromArchivesFilePicker" && f.Text.StartsWith("Choose Character Model") ? "OK" : null;
                        if (button == null || !f.Visible)
                            continue;
                        var b = Controls(f).OfType<System.Windows.Forms.Button>().FirstOrDefault(x => x.Text == button);
                        Note($"'{f.Text}' open: {(b != null ? button + " clicked" : "no " + button + " button")}");
                        b?.PerformClick();
                    }
                };
                timer.Start();
                stage = 1;
                return;
            }
            if ((DateTime.Now - started).TotalMinutes > 15)
            {
                Note("TIMEOUT at stage " + stage);
                Finish(3);
                return;
            }
            var tabs = zzz_DocumentManager.GetOpenFileDocuments();
            bool idle = tabs.Count > 0 && !zzz_DocumentManager.AnyDocumentWithANYLoadingTasks() && !DuplicateCrop.Busy
                        && tabs.All(t => t.EditorScreen.FileContainer != null && DuplicateCrop.Container(t.EditorScreen) != null);
            if (frames < waitUntil || !idle && stage != 4)
                return;
            var cs = spec.GetProperty("cases");
            var cc = caseIndex < cs.GetArrayLength() ? cs[caseIndex] : default;
            int Index(string key, int fallback) => cc.ValueKind == JsonValueKind.Object && cc.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
            int srcIx = Index("src", 0);
            var tae = tabs[Math.Min(srcIx, tabs.Count - 1)].EditorScreen;
            var dest = tabs[Math.Min(Index("dest", srcIx), tabs.Count - 1)].EditorScreen;
            switch (stage)
            {
                case 1:   // loaded: a second file in a tab of its own when the spec has one, settle, then the cases
                    if (Str(spec, "anibnd2") != null && tabs.Count < 2)
                    {
                        if (!requested2)
                        {
                            requested2 = true;
                            Note($"opened {tabs[0].EditorScreen.NewFileContainerName}; opening {Str(spec, "anibnd2")} in a second tab");
                            Open(Str(spec, "anibnd2"));
                            waitUntil = frames + 120;
                        }
                        return;
                    }
                    foreach (var t in tabs)
                        Note($"tab '{DuplicateCrop.TabName(t.EditorScreen)}': {t.EditorScreen.NewFileContainerName}; mod folder {t.GameData.ModengineRootPath}; game folder {t.GameData.InterrootPath}");
                    Note(DuplicateCrop.HctStatus(out _));
                    stage = 2;
                    waitUntil = frames + 120;
                    break;
                case 2:   // select the case's source, let it load
                {
                    if (caseIndex >= cs.GetArrayLength())
                    {
                        foreach (var t in tabs)
                            Dump(t.EditorScreen);
                        if (spec.TryGetProperty("save", out var sv) && sv.GetBoolean())
                        {
                            Note("saving every tab through the program's own save");
                            saveTarget = tabs.Count;
                            foreach (var t in tabs)
                                t.EditorScreen.SaveCurrentFile(() => savedCount++, "New Animation from Current self-test save");
                            stage = 4;
                        }
                        else
                            Finish(0);
                        return;
                    }
                    var c = cs[caseIndex];
                    Note($"case {caseIndex + 1}: {Str(c, "name")}: {Str(c, "source")} -> {Str(c, "target")}, first '{Str(c, "first")}', last '{Str(c, "last")}'");
                    var proj = tae.Proj;
                    if (!SplitAnimID.TryParse(proj, Str(c, "source"), out var sid, out string err))
                    {
                        Note("  source not parsed: " + err);
                        caseIndex++;
                        return;
                    }
                    var src = proj.SAFE_GetFirstAnimationFromFullID(sid);
                    if (src == null)
                    {
                        Note("  no source entry " + Str(c, "source"));
                        caseIndex++;
                        return;
                    }
                    Note("  source events: " + Events(src));
                    tae.SelectNewAnimRef(src.ParentCategory, src, false, false, -1f, false, false);
                    stage = 3;
                    waitUntil = frames + 60;
                    break;
                }
                case 3:   // the case itself, as the dialog's Accept runs it
                {
                    var c = cs[caseIndex];
                    if (!caseDone)
                    {
                        var sel = tae.SelectedAnim;
                        Note($"  selected {sel?.SplitID.GetFormattedIDString(tae.Proj, null)}; playing hkx {DuplicateCrop.Container(tae)?.CurrentAnimationID.GetFormattedIDString(tae.Proj, null)}");
                        if (dest != tae)
                            Note($"  into the tab '{DuplicateCrop.TabName(dest)}'");
                        // the case's binder ('binder', a file name; the spec's, else the first the dialog would list)
                        string binderName = Str(c, "binder") ?? Str(spec, "binder");
                        var binders = DuplicateCrop.DestBinders(dest, out _, out _);
                        string binder = binderName == null ? null
                            : binders.FirstOrDefault(b => Path.GetFileName(b).Equals(binderName, StringComparison.OrdinalIgnoreCase)) ?? binderName;
                        Note($"  binders offered: {string.Join(", ", binders.Select(Path.GetFileName))}; writing into {Path.GetFileName(binder ?? "(the first)")}");
                        // what the dialog would show for these inputs
                        var look = DuplicateCrop.Look(tae, sel, Str(c, "target"), Str(c, "first"), Str(c, "last"),
                            c.TryGetProperty("mirror", out var lm) && lm.GetBoolean(), dest, c.TryGetProperty("move", out var lmo) && lmo.GetBoolean(),
                            binders.Contains(binder ?? "", StringComparer.OrdinalIgnoreCase) ? binder : (binders.Count > 0 && binder == null ? binders[0] : null),
                            DuplicateCrop.SourceLastFrame(DuplicateCrop.Container(tae), sel.GetHkxID(tae.Proj)), DuplicateCrop.HctStatus(out bool hf) != null && hf,
                            "none in the list");
                        Note($"  dialog: new ID '{look.IdStatus}' (level {look.IdLevel}); {(look.Problem != null ? "Accept off: " + look.Problem : "Accept on")}; summary: {look.Summary}");
                        Note($"  dialog: binder offered first {Path.GetFileName(binders.Count > 0 ? binders[DuplicateCrop.DefaultBinder(dest, binders)] : "none")}");
                        string err = DuplicateCrop.Check(tae, sel, Str(c, "target"), Str(c, "first"), Str(c, "last"), out var req,
                                                         c.TryGetProperty("mirror", out var mv) && mv.GetBoolean(), dest,
                                                         c.TryGetProperty("move", out var mo) && mo.GetBoolean(), binder,
                                                         !c.TryGetProperty("ease", out var ev) || ev.GetBoolean());
                        if (err != null)
                        {
                            Note("  refused by the check: " + err);
                            Next();
                            return;
                        }
                        bool answer = !c.TryGetProperty("answer", out var a) || a.GetBoolean();
                        caseDone = true;
                        var t0 = DateTime.Now;
                        DuplicateCrop.Start(req, new RecorderUi(answer), ok =>
                        {
                            Note($"  finished: {(ok ? "entry added" : "nothing added")} ({(DateTime.Now - t0).TotalSeconds:0.0} s)");
                            caseDone = false;
                            Next();
                        });
                    }
                    break;
                }
                case 4:   // saving
                    if (savedCount >= saveTarget && idle)
                    {
                        Note("saved");
                        Finish(0);
                    }
                    break;
            }
        }

        static IEnumerable<System.Windows.Forms.Control> Controls(System.Windows.Forms.Control c)
        {
            foreach (System.Windows.Forms.Control x in c.Controls)
            {
                yield return x;
                foreach (var y in Controls(x))
                    yield return y;
            }
        }

        static void Next()
        {
            caseIndex++;
            stage = 2;
            waitUntil = frames + 30;
        }

        static string Events(DSAProj.Animation a)
        {
            var sb = new StringBuilder();
            foreach (var e in a.SAFE_GetActions().OrderBy(e => e.StartTime).ThenBy(e => e.Type))
                sb.Append($"[{e.Type} {e.StartTime * 30:0.##}-{e.EndTime * 30:0.##}] ");
            return $"{a.SAFE_GetActionCount()} events: {sb}";
        }

        // every target's entries as the program holds them now
        static void Dump(TaeEditorScreen tae)
        {
            var proj = tae.Proj;
            var c = DuplicateCrop.Container(tae);
            Note($"state after the cases, tab '{DuplicateCrop.TabName(tae)}':");
            var show = spec.GetProperty("cases").EnumerateArray().SelectMany(cs => new[] { Str(cs, "target"), Str(cs, "source") }).ToList();
            if (spec.TryGetProperty("show", out var sv))
                show.AddRange(sv.EnumerateArray().Select(x => x.GetString()));
            var cats = new HashSet<int>();
            foreach (string t in show.Where(x => x != null).Distinct())
            {
                if (!SplitAnimID.TryParse(proj, t, out var id, out _) || !proj.SAFE_CategoryExists(id.CategoryID))
                    continue;
                cats.Add(id.CategoryID);
                var cat = proj.SAFE_RegistCategory(id.CategoryID);
                var anims = cat.SAFE_GetAnimationsBySubID(id.SubID);
                Note($"  {t}: {anims.Count} entries; hkx loaded: {c.AnimExists(id)}{(c.Animations.TryGetValue(id, out var hb) ? $" ({hb.Length} bytes)" : "")}");
                foreach (var a in anims)
                {
                    var h = a.SAFE_GetHeaderClone();
                    string hs = h is SoulsAssetPipeline.Animation.TAE.Animation.AnimFileHeader.Standard s
                        ? $"Standard ImportsHKX={s.ImportsHKX} ImportHKXSourceAnimID={s.ImportHKXSourceAnimID} IsLoopByDefault={s.IsLoopByDefault} AllowDelayLoad={s.AllowDelayLoad}"
                        : h is SoulsAssetPipeline.Animation.TAE.Animation.AnimFileHeader.ImportOtherAnim io
                            ? $"ImportOtherAnim ImportFromAnimID={io.ImportFromAnimID}"
                            : h.GetType().Name;
                    Note($"    header {hs}, plays hkx {a.GetHkxID(proj).GetFormattedIDString(proj, null)}, modified {a.SAFE_GetIsModified()}");
                    Note("    " + Events(a));
                }
            }
            foreach (int k in cats)
            {
                var ids = proj.SAFE_RegistCategory(k).SAFE_GetAnimations().Select(a => a.SplitID.SubID).ToList();
                int bad = ids.Zip(ids.Skip(1), (x, y) => x > y ? 1 : 0).Sum();
                Note($"  category {k}: {ids.Count} entries, {(bad == 0 ? "in ID order" : bad + " out of ID order")}");
            }
        }
    }
}
#endif
