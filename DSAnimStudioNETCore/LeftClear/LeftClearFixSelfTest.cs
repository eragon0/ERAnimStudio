#if DUPLICATECROP_SELFTEST
// The in-app test of Edit > Left Arm Clearance, compiled only into a build made with -p:DuplicateCropSelfTest=true and run only with
// the LEFTCLEAR_FIX_SELFTEST environment variable set to a spec file. Meant for a copy of the program and of a project, never a real
// one. It opens the spec's anibnd, wears the spec's armor, puts the spec's weapon in the left hand and the spec's item list in the
// settings, and for each case selects the entry (marked mirrored or not), runs the solve as the dialog's Solve does (LeftClearFix.Start),
// turns the preview on and checks what the editor plays (the bones at the case's frame against the solved copy's, mirrored when marked)
// and what the left item check measures (its rows against the solve's for the equipped item), writes the copy into the spec's binder
// (LeftClearFix.Write) and checks the entry's header and the loaded hkx. Then saves through the program's own save and exits. Spec:
//   {"anibnd": <path>, "log": <path>, "exit": true, "save": true, "binder": "c0000_a8x.anibnd.dcx",
//    "armor": ["BD_M_1501", ...], "left": "WP_A_2020", "items": "31330000:1;31000000:1;32020000:0;2000000:1;3180000:0",
//    "cases": [{"entry": "a926_030010", "mirror": false, "frame": 20, "limits": "12,30,60,60,60", "pins": ""}, ...]}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DSAnimStudio.HkxEdit;
using DSAnimStudio.TaeEditor;

namespace DSAnimStudio.LeftClear
{
    public static class LeftClearFixSelfTest
    {
        static readonly string SpecPath = Environment.GetEnvironmentVariable("LEFTCLEAR_FIX_SELFTEST");
        public static bool Active => !string.IsNullOrEmpty(SpecPath);
        static JsonElement spec;
        static string logPath;
        static int stage, frames, waitUntil, caseIndex, phase, savedCount, saveTarget;
        static DateTime started, phaseStarted;

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
                Note($"SELF-TEST ERROR at stage {stage}, case {caseIndex}, phase {phase}, frame {frames}: {e}");
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
                Note($"left arm fix self-test {SpecPath} at {started:yyyy-MM-dd HH:mm:ss}");
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
            if ((DateTime.Now - started).TotalMinutes > 40)
            {
                Note("TIMEOUT at stage " + stage);
                Finish(3);
                return;
            }
            var tabs = zzz_DocumentManager.GetOpenFileDocuments();
            bool idle = tabs.Count > 0 && !zzz_DocumentManager.AnyDocumentWithANYLoadingTasks()
                        && tabs[0].EditorScreen.FileContainer != null && DuplicateCrop.Container(tabs[0].EditorScreen) != null;
            if (frames < waitUntil || (!idle && stage != 3))
                return;
            var doc = tabs[0];
            var tae = doc.EditorScreen;
            var model = tae.Graph?.ViewportInteractor?.CurrentModel;
            var asm = model?.ChrAsm;
            var proj = tae.Proj;
            switch (stage)
            {
                case 1:   // loaded: the armor, the left weapon, the item list, one-handed
                {
                    Note($"opened {tae.NewFileContainerName}; mod folder {doc.GameData.ModengineRootPath}; game folder {doc.GameData.InterrootPath}");
                    asm.IsFemale = false;
                    asm.WeaponStyle = NewChrAsm.WeaponStyleType.OneHand;
                    var want = spec.GetProperty("armor").EnumerateArray().Select(x => x.GetString().ToUpperInvariant()).ToList();
                    foreach (var (type, flag) in new[] { (NewChrAsm.EquipSlotTypes.Head, "HD"), (NewChrAsm.EquipSlotTypes.Body, "BD"),
                                                         (NewChrAsm.EquipSlotTypes.Arms, "AM"), (NewChrAsm.EquipSlotTypes.Legs, "LG") })
                    {
                        string part = want.FirstOrDefault(w => w.StartsWith(flag));
                        var row = part == null ? null : doc.ParamManager.EquipParamProtector
                            .Where(kv => (type == NewChrAsm.EquipSlotTypes.Head ? kv.Value.HeadEquip : type == NewChrAsm.EquipSlotTypes.Body ? kv.Value.BodyEquip
                                          : type == NewChrAsm.EquipSlotTypes.Arms ? kv.Value.ArmEquip : kv.Value.LegEquip)
                                         && (kv.Value.GetPartsbndName(false, ParamData.PartSuffixType.None) ?? "").ToUpperInvariant().StartsWith(part + "."))
                            .OrderBy(kv => kv.Key).Select(kv => (long?)kv.Key).FirstOrDefault();
                        asm.ArmorSlots_ByEquipSlot[type].EquipID = row != null ? (int)row.Value : -1;
                    }
                    string leftModel = Str(spec, "left").ToUpperInvariant();
                    var w0 = doc.ParamManager.EquipParamWeapon
                        .Where(kv => kv.Value.GetPartBndName_Short(NewChrAsm.EquipSlotTypes.LeftWeapon).ToUpperInvariant() == leftModel)
                        .OrderBy(kv => kv.Key).Select(kv => (long?)kv.Key).FirstOrDefault();
                    asm.SetWeaponID(NewChrAsm.EquipSlotTypes.LeftWeapon, (int)w0.Value);
                    asm.UpdateModels(isAsync: false, onCompleteAction: null, forceReloadUnchanged: false, disableCache: false);
                    Main.Config.LeftClearItems = Str(spec, "items");
                    Note($"left hand: EquipParamWeapon {w0} ({leftModel}); the item list: {Main.Config.LeftClearItems}");
                    LeftItemCheck.Enabled = true;
                    LeftItemCheck.MaskOff = false;      // the fix measures with the draw mask, as the check does
                    stage = 2;
                    Wait(60);
                    break;
                }
                case 2:   // a case: select the entry with its mark, start the solve
                {
                    var cs = spec.GetProperty("cases");
                    if (caseIndex >= cs.GetArrayLength())
                    {
                        if (spec.TryGetProperty("save", out var sv) && sv.GetBoolean())
                        {
                            Note("saving through the program's own save");
                            saveTarget = 1;
                            tae.SaveCurrentFile(() => savedCount++, "Left Arm Clearance self-test save");
                            stage = 4;
                        }
                        else
                            Finish(0);
                        return;
                    }
                    var c = cs[caseIndex];
                    bool mirror = c.TryGetProperty("mirror", out var mv) && mv.GetBoolean();
                    if (!SplitAnimID.TryParse(proj, Str(c, "entry"), out var id, out string err))
                        throw new InvalidOperationException("entry not parsed: " + err);
                    var entry = proj.SAFE_GetFirstAnimationFromFullID(id) ?? throw new InvalidOperationException("no entry " + Str(c, "entry"));
                    MirrorPlayback.SetMirrored(proj, id, mirror);
                    if (phase == 0)
                    {
                        Note($"case {caseIndex + 1}: {Str(c, "entry")}{(mirror ? " mirrored" : "")}, limits {Str(c, "limits")}, pins '{Str(c, "pins")}'");
                        tae.SelectNewAnimRef(entry.ParentCategory, entry, false, false, -1f, false, false);
                        phase = 1;
                        Wait(60);
                        return;
                    }
                    var items = LeftClearFix.ConfigItems();
                    var left = asm.WeaponSlots.First(s => s.EquipSlotType == NewChrAsm.EquipSlotTypes.LeftWeapon);
                    if (!items.Any(x => x.id == left.EquipID))
                        items.Insert(0, (left.EquipID, true));
                    else
                        items = items.OrderBy(x => x.id == left.EquipID ? 0 : 1).ToList();
                    string why = LeftClearFix.Start(tae, items, LcLimits.Parse(Str(c, "limits")), Str(c, "pins") ?? "", true);
                    Note($"  solve started: {why ?? "yes"}; {items.Count} items");
                    if (why != null)
                    {
                        Next();
                        return;
                    }
                    phaseStarted = DateTime.Now;
                    stage = 3;
                    break;
                }
                case 3:   // the solve: wait, then the preview, then write
                {
                    var j = LeftClearFix.Current;
                    var c = spec.GetProperty("cases")[caseIndex];
                    if (phase == 1)
                    {
                        if (j == null || j.Running)
                        {
                            if ((DateTime.Now - phaseStarted).TotalMinutes > 15)
                                throw new InvalidOperationException("the solve took over 15 minutes");
                            return;
                        }
                        if (j.Result == null)
                        {
                            Note("  the solve failed: " + j.Error);
                            Next();
                            return;
                        }
                        foreach (var line in LeftClearFix.ReportLines(j))
                            Note("  | " + line);
                        foreach (var line in j.Result.Log)
                            Note("  . " + line);
                        LeftClearFix.SetPreview(true);
                        int frame = c.TryGetProperty("frame", out var fv) ? fv.GetInt32() : 0;
                        tae.PlaybackCursor.IsPlaying = false;
                        tae.PlaybackCursor.CurrentTime = frame / j.Result.Fps;
                        tae.Graph?.ViewportInteractor?.NewScrub(absolute: true, time: (float)(frame / j.Result.Fps));
                        phase = 2;
                        phaseStarted = DateTime.Now;
                        Wait(30);
                        return;
                    }
                    if (phase == 2)
                    {
                        if (!idle)
                            return;
                        // what the editor plays against the solved copy (as played: mirrored when marked)
                        var cnt = model.AnimContainer;
                        double at = cnt.CurrentAnimTime * j.Result.Fps;
                        int f = (int)Math.Round(at);
                        var sk = new LcSkeleton(j.Skeleton);
                        var played = j.Mirror ? HkxAnimationOps.Mirror(j.Skeleton, j.Result.Copy) : j.Result.Copy;
                        var (wt, wq) = sk.Pose(played);
                        var bones = cnt.Skeleton.Bones;
                        double maxT = 0, maxQ = 0;
                        for (int b = 0; b < sk.Count; b++)
                        {
                            bones[b].FKMatrix.Decompose(out _, out var q, out var t);
                            maxT = Math.Max(maxT, (new D3(t.X, t.Y, t.Z) - wt[f, b]).Length());
                            maxQ = Math.Max(maxQ, DQ.AngleBetween(new DQ(q.X, q.Y, q.Z, q.W), wq[f, b]));
                        }
                        Note($"  preview at frame {at:0.###}: the editor's bones within {maxT * 1000:0.00} mm and {maxQ:0.00} degrees of the solved copy's"
                             + (j.Mirror ? " (mirrored back and played mirrored)" : ""));
                        // the left item check on the preview against the solve's own rows for the equipped item (index 0)
                        LeftItemCheck.Update(tae, now: true);
                        var r = LeftItemCheck.CurrentResult(out string state);
                        if (r == null)
                        {
                            if ((DateTime.Now - phaseStarted).TotalMinutes > 3)
                                throw new InvalidOperationException("no left item check result: " + state);
                            return;
                        }
                        double worst = 0;
                        for (int k = 0; k < Math.Min(r.Frames, j.Result.Frames); k++)
                        {
                            var x = r.Rows[k];
                            var y = j.Result.After[k][0];
                            foreach (var p in x.Keys.Union(y.Keys))
                                worst = Math.Max(worst, Math.Abs((x.TryGetValue(p, out var a) ? a : 0) - (y.TryGetValue(p, out var b2) ? b2 : 0)));
                        }
                        Note($"  the left item check on the preview ({r.AnimName}): {r.Worst.Count(v => v > 0.01)} frames over 1 cm; "
                             + $"its rows against the solve's for {j.Items[0].Name}: within {worst * 100:0.00} cm (the copy is written as float, the solve measured in double)");
                        // write
                        var binders = DuplicateCrop.DestBinders(tae, out _, out string berr);
                        string binder = binders.FirstOrDefault(b => Path.GetFileName(b).Equals(Str(spec, "binder"), StringComparison.OrdinalIgnoreCase));
                        if (binder == null)
                            throw new InvalidOperationException("no binder " + Str(spec, "binder") + ": " + berr);
                        var id = LeftClearFix.CopyId(j);
                        var entry = j.Entry;
                        string done = LeftClearFix.Write(j, binder);
                        Note("  written: " + done);
                        var h = entry.SAFE_GetHeaderClone() as SoulsAssetPipeline.Animation.TAE.Animation.AnimFileHeader.Standard;
                        Note($"  the entry's header: ImportsHKX={h?.ImportsHKX} ImportHKXSourceAnimID={h?.ImportHKXSourceAnimID} (the copy {DuplicateCrop.HkxName(id.Value)} = "
                             + $"{id.Value.GetFullID(proj, null)}); plays hkx {entry.GetHkxID(proj).GetFormattedIDString(proj, null)}; the copy loaded: {cnt.AnimExists(id.Value)}; "
                             + $"a preview left: {LeftClearFix.Current != null}");
                        Next();
                        stage = 2;
                        Wait(60);
                    }
                    break;
                }
                case 4:
                    if (savedCount >= saveTarget && idle)
                    {
                        Note("saved");
                        Finish(0);
                    }
                    break;
            }
        }

        static void Next()
        {
            caseIndex++;
            phase = 0;
            stage = 2;
            Wait(30);
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
