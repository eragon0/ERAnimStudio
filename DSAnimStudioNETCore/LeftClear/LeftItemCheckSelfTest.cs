#if DUPLICATECROP_SELFTEST
// The in-app test of the left item check and Play Mirrored, compiled only into a build made with -p:DuplicateCropSelfTest=true and run
// only with the LEFTCLEAR_SELFTEST environment variable set to a spec file. Meant for a copy of the program and of a project, never a
// real one. It opens the spec's anibnd, wears the spec's armor, and for each case selects the entry (marked mirrored or not) and puts
// each item in the left hand in turn: the check's rows for that item (every armor mesh counted, as the pack's left_clear.py does, and
// once with the draw mask as the editor uses it), and at the case's frame the bones the editor shows against the decoded file's (mirrored
// when marked) and the item where the editor draws it against where the check hangs it. Each case's rows go to <out>\<hkx>[.mirrored]
// .measure.json in the console check's format. Saves nothing. Spec:
//   {"anibnd": <path>, "log": <path>, "out": <folder>, "exit": true, "armor": ["BD_M_1501", "AM_M_1500", "LG_M_1500", "HD_M_1500"],
//    "items": [{"name": "Heater Shield", "model": "WP_A_2020"}, ...],
//    "cases": [{"entry": "a926_030010", "mirror": false, "frame": 20}, ...]}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DSAnimStudio.HkxEdit;
using DSAnimStudio.TaeEditor;
using Microsoft.Xna.Framework;

namespace DSAnimStudio.LeftClear
{
    public static class LeftItemCheckSelfTest
    {
        static readonly string SpecPath = Environment.GetEnvironmentVariable("LEFTCLEAR_SELFTEST");
        public static bool Active => !string.IsNullOrEmpty(SpecPath);
        static JsonElement spec;
        static string logPath;
        static int stage, frames, waitUntil, caseIndex, itemIndex, phase;
        static DateTime started, phaseStarted;
        static List<Dictionary<string, Dictionary<string, double>>> rows;
        static string rowsName;

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

        public static void Frame()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Note($"SELF-TEST ERROR at stage {stage}, case {caseIndex}, item {itemIndex}, phase {phase}, frame {frames}: {e}");
                if (stage == 0 && frames < 600)
                    return;
                Finish(4);
            }
        }

        static void Wait(int n) => waitUntil = frames + n;

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
                Note($"left item check self-test {SpecPath} at {started:yyyy-MM-dd HH:mm:ss}");
                zzz_DocumentManager.CurrentDocument.GameRoot.SuppressNextInterrootBrowsePrompt = true;
                Main.TAE_EDITOR.SuppressNextModelOverridePrompt = true;
                zzz_DocumentManager.RequestFileOpenRecent_SelectedFile = new DSAProj.TaeContainerInfo.ContainerAnibnd(Str(spec, "anibnd"), null);
                zzz_DocumentManager.RequestFileOpenRecent = true;
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
            if ((DateTime.Now - started).TotalMinutes > 25)
            {
                Note("TIMEOUT at stage " + stage);
                Finish(3);
                return;
            }
            var tabs = zzz_DocumentManager.GetOpenFileDocuments();
            bool idle = tabs.Count > 0 && !zzz_DocumentManager.AnyDocumentWithANYLoadingTasks()
                        && tabs[0].EditorScreen.FileContainer != null && DuplicateCrop.Container(tabs[0].EditorScreen) != null;
            if (frames < waitUntil || !idle)
                return;
            var tae = tabs[0].EditorScreen;
            var model = tae.Graph?.ViewportInteractor?.CurrentModel;
            var asm = model?.ChrAsm;
            var doc = tabs[0];
            LeftItemCheck.Update(tae, now: true);
            switch (stage)
            {
                case 1:   // loaded: the armor, one-handed, the check on (every armor mesh counted)
                {
                    if (asm == null)
                    {
                        Note("the model has no equipment (no c0000?)");
                        Finish(5);
                        return;
                    }
                    Note($"opened {tae.NewFileContainerName}; mod folder {doc.GameData.ModengineRootPath}; game folder {doc.GameData.InterrootPath}");
                    asm.IsFemale = false;
                    asm.WeaponStyle = NewChrAsm.WeaponStyleType.OneHand;
                    var want = spec.GetProperty("armor").EnumerateArray().Select(x => x.GetString().ToUpperInvariant()).ToList();
                    foreach (var (type, flag) in new[] { (NewChrAsm.EquipSlotTypes.Head, "HD"), (NewChrAsm.EquipSlotTypes.Body, "BD"),
                                                         (NewChrAsm.EquipSlotTypes.Arms, "AM"), (NewChrAsm.EquipSlotTypes.Legs, "LG") })
                    {
                        string part = want.FirstOrDefault(w => w.StartsWith(flag));
                        var slot = asm.ArmorSlots_ByEquipSlot[type];
                        var row = part == null ? null : doc.ParamManager.EquipParamProtector
                            .Where(kv => (type == NewChrAsm.EquipSlotTypes.Head ? kv.Value.HeadEquip : type == NewChrAsm.EquipSlotTypes.Body ? kv.Value.BodyEquip
                                          : type == NewChrAsm.EquipSlotTypes.Arms ? kv.Value.ArmEquip : kv.Value.LegEquip)
                                         && (kv.Value.GetPartsbndName(false, ParamData.PartSuffixType.None) ?? "").ToUpperInvariant().StartsWith(part + "."))
                            .OrderBy(kv => kv.Key).Select(kv => (long?)kv.Key).FirstOrDefault();
                        slot.EquipID = row != null ? (int)row.Value : -1;
                        Note($"  {type}: {part ?? "none"} -> EquipParamProtector {slot.EquipID}");
                    }
                    asm.UpdateModels(isAsync: false, onCompleteAction: null, forceReloadUnchanged: false, disableCache: false);
                    LeftItemCheck.Enabled = true;
                    LeftItemCheck.MaskOff = true;
                    stage = 2;
                    caseIndex = 0;
                    itemIndex = 0;
                    phase = 0;
                    Wait(60);
                    break;
                }
                case 2:   // the cases: per item, select, wait for the check, record; once per case the draw mask and the live checks
                {
                    var cs = spec.GetProperty("cases");
                    if (caseIndex >= cs.GetArrayLength())
                    {
                        LeftItemCheck.MaskOff = false;
                        Finish(0);
                        return;
                    }
                    var c = cs[caseIndex];
                    var items = spec.GetProperty("items");
                    var item = items[itemIndex];
                    var proj = tae.Proj;
                    bool mirror = c.TryGetProperty("mirror", out var mv) && mv.GetBoolean();
                    if (phase == 0)   // the item into the left hand, the entry selected with its mark
                    {
                        string model0 = Str(item, "model").ToUpperInvariant();
                        var w = doc.ParamManager.EquipParamWeapon
                            .Where(kv => kv.Value.GetPartBndName_Short(NewChrAsm.EquipSlotTypes.LeftWeapon).ToUpperInvariant() == model0)
                            .OrderBy(kv => kv.Key).Select(kv => (long?)kv.Key).FirstOrDefault();
                        if (w == null)
                        {
                            Note($"  no weapon row with the model {model0}");
                            Finish(6);
                            return;
                        }
                        asm.SetWeaponID(NewChrAsm.EquipSlotTypes.LeftWeapon, (int)w.Value);
                        asm.UpdateModels(isAsync: false, onCompleteAction: null, forceReloadUnchanged: false, disableCache: false);
                        if (!SplitAnimID.TryParse(proj, Str(c, "entry"), out var id, out string err))
                            throw new InvalidOperationException("entry not parsed: " + err);
                        var entry = proj.SAFE_GetFirstAnimationFromFullID(id) ?? throw new InvalidOperationException("no entry " + Str(c, "entry"));
                        MirrorPlayback.SetMirrored(proj, id, mirror);
                        if (itemIndex == 0)
                        {
                            rows = null;
                            rowsName = null;
                            Note($"case {caseIndex + 1}: {Str(c, "entry")}{(mirror ? " mirrored" : "")}");
                        }
                        tae.SelectNewAnimRef(entry.ParentCategory, entry, false, false, -1f, false, false);
                        Note($"  {Str(item, "name")}: EquipParamWeapon {w.Value} ({model0})");
                        phase = 1;
                        phaseStarted = DateTime.Now;
                        Wait(30);
                        return;
                    }
                    if (phase == 1 || phase == 3)   // the check's result: every armor mesh counted (1), with the draw mask (3)
                    {
                        var r = LeftItemCheck.CurrentResult(out string state);
                        if (r == null)
                        {
                            if ((DateTime.Now - phaseStarted).TotalMinutes > 3)
                                throw new InvalidOperationException("no result after 3 minutes: " + state);
                            return;
                        }
                        int over = r.Worst.Count(x => x > 0.01);
                        if (phase == 3)
                        {
                            Note($"    with the draw mask as the editor uses it: {over} frames over 1 cm; armor {r.Scene.ArmorText}");
                            Summary(r);
                            LeftItemCheck.MaskOff = true;
                            NextItem(items.GetArrayLength(), mirror);
                            return;
                        }
                        Note($"    {r.AnimName}: {r.Scene.ItemText}; {r.Frames} frames at {r.Fps:0.##} fps in {r.Seconds:0.0} s; {over} frames over 1 cm"
                             + " (every armor mesh counted)");
                        rows ??= Enumerable.Range(0, r.Frames).Select(_ => new Dictionary<string, Dictionary<string, double>>()).ToList();
                        rowsName ??= r.AnimName.Replace(" (mirrored)", "");
                        for (int f = 0; f < r.Frames && f < rows.Count; f++)
                            rows[f][Str(item, "name")] = r.Rows[f];
                        // the live checks at the case's frame
                        int frame = c.TryGetProperty("frame", out var fv) ? fv.GetInt32() : 0;
                        tae.PlaybackCursor.IsPlaying = false;
                        tae.PlaybackCursor.CurrentTime = frame / r.Fps;
                        tae.Graph?.ViewportInteractor?.NewScrub(absolute: true, time: (float)(frame / r.Fps));
                        phase = 2;
                        phaseStarted = DateTime.Now;
                        Wait(20);
                        return;
                    }
                    if (phase == 2)   // what the editor shows against the decoded file; the first item also with the draw mask
                    {
                        var r = LeftItemCheck.CurrentResult(out _);
                        if (r != null)
                            Live(tae, model, r, mirror);
                        if (itemIndex == 0)
                        {
                            LeftItemCheck.MaskOff = false;
                            phase = 3;
                            phaseStarted = DateTime.Now;
                            Wait(10);
                            return;
                        }
                        NextItem(items.GetArrayLength(), mirror);
                    }
                    break;
                }
            }
        }

        static void NextItem(int count, bool mirror)
        {
            itemIndex++;
            if (itemIndex >= count)
            {
                WriteRows(mirror);
                itemIndex = 0;
                caseIndex++;
            }
            phase = 0;
            Wait(10);
        }

        static void Summary(LeftItemCheck.Result r)
        {
            var per = new Dictionary<string, (int n, double deep, int at)>();
            for (int f = 0; f < r.Frames; f++)
                foreach (var (p, v) in r.Rows[f])
                    if (v > 0.01)
                    {
                        var (n, deep, at) = per.TryGetValue(p, out var x) ? x : (0, 0.0, -1);
                        per[p] = (n + 1, Math.Max(deep, v), v > deep ? f : at);
                    }
            Note("      " + (per.Count == 0 ? "clear" : string.Join("; ", per.OrderByDescending(kv => kv.Value.deep).Select(kv => $"{kv.Key} {kv.Value.n} frames, {kv.Value.deep * 100:0.0} cm (frame {kv.Value.at})"))));
        }

        // the bones the editor shows against the decoded file's at that frame, and the item's place
        static void Live(TaeEditorScreen tae, Model model, LeftItemCheck.Result r, bool mirror)
        {
            var c = model.AnimContainer;
            double time = c.CurrentAnimTime;
            double at = time * r.Fps;
            int f = (int)Math.Round(at);
            var proj = tae.Proj;
            var hkxId = tae.SelectedAnim.GetHkxID(proj);
            var bytes = c.FindAnimationBytes(hkxId);
            var a = HkxAnimationFile.Read(bytes.HkxBytes, bytes.CompendiumBytes).Decoded;
            var sk = r.Scene.Sk;
            if (mirror)
                a = HkxAnimationOps.Mirror(sk.Hkx, a);
            var (wt, wq) = sk.Pose(a);
            var bones = c.Skeleton.Bones;
            double maxT = 0, maxQ = 0;
            string worstBone = "";
            for (int b = 0; b < sk.Count; b++)
            {
                bones[b].FKMatrix.Decompose(out _, out var q, out var t);
                double d = (new D3(t.X, t.Y, t.Z) - wt[f, b]).Length();
                double ang = DQ.AngleBetween(new DQ(q.X, q.Y, q.Z, q.W), wq[f, b]);
                if (d > maxT)
                {
                    maxT = d;
                    worstBone = sk.Names[b];
                }
                maxQ = Math.Max(maxQ, ang);
            }
            Note($"    at the editor's time {time:0.####} s (file frame {at:0.###}): bones within {maxT * 1000:0.00} mm ({worstBone}) and {maxQ:0.00} degrees of the decoded file's{(mirror ? ", mirrored" : "")}");
            // the item where the editor draws it (its model 0) against the check's hanging
            var left = model.ChrAsm.WeaponSlots.First(s => s.EquipSlotType == NewChrAsm.EquipSlotTypes.LeftWeapon);
            Matrix? wm = null;
            left.AccessModel(0, m => wm = m.CurrentTransform.WorldMatrix);
            if (wm != null)
            {
                var rel = wm.Value * Matrix.Invert(model.CurrentTransform.WorldMatrix);
                var (o, rr) = r.Scene.Item.World(wt, wq, f);
                double worst = 0;
                foreach (var p in new[] { Vector3.Zero, Vector3.UnitX * 0.5f, Vector3.UnitY * 0.5f, Vector3.UnitZ * 0.5f })
                {
                    var app = Vector3.Transform(p, rel);
                    var mine = o + rr * new D3(p.X, p.Y, p.Z);
                    worst = Math.Max(worst, (new D3(app.X, app.Y, app.Z) - mine).Length());
                }
                Note($"    the item as drawn against the check's {r.Scene.Item.Hanging}: within {worst * 1000:0.00} mm over its axes (0.5 m long)");
            }
            // the live measure at that frame against the file's row
            var live = new List<LcHit>();
            var wtl = new D3[1, sk.Count];
            var wql = new DQ[1, sk.Count];
            for (int b = 0; b < sk.Count; b++)
            {
                bones[b].FKMatrix.Decompose(out _, out var q, out var t);
                wtl[0, b] = new D3(t.X, t.Y, t.Z);
                wql[0, b] = new DQ(q.X, q.Y, q.Z, q.W).Normalized();
            }
            var it = r.Scene.Item;
            var fr = r.Scene.Measure.Prepare(wtl, wql, 0, it.Kind == LcItemKind.Plate, it.Kind == LcItemKind.Points, trees: false);
            var parts = r.Scene.Measure.Depths(fr, it, wtl, wql, live);
            Note($"    measured on the pose shown: {LeftItemCheck.Describe(parts, 0)}; the file's frame {f}: {LeftItemCheck.Describe(r.Rows[f], 0)}; {live.Count} points drawn");
        }

        static void WriteRows(bool mirror)
        {
            if (rows == null)
                return;
            string outDir = Str(spec, "out");
            Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir, rowsName + (mirror ? ".mirrored" : "") + ".measure.json");
            var sb = new StringBuilder();
            sb.Append("{\"name\": \"").Append(rowsName).Append("\", \"frames\": ").Append(rows.Count).Append(", \"mirrored\": ").Append(mirror ? "true" : "false").Append(", \"rows\": [\n");
            for (int f = 0; f < rows.Count; f++)
            {
                sb.Append('{');
                sb.Append(string.Join(", ", rows[f].Select(kv => $"\"{kv.Key}\": {{" + string.Join(", ", kv.Value.Select(p => $"\"{p.Key}\": {Math.Round(p.Value * 100, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)}")) + "}")));
                sb.Append(f + 1 < rows.Count ? "},\n" : "}\n");
            }
            sb.Append("]}\n");
            File.WriteAllText(path, sb.ToString());
            Note($"  rows written: {path}");
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
    }
}
#endif
