// ERAnimStudio: 'Left Item Check' in the Animation menu. Whether the item in the left hand goes into the body during the animation:
// the equipped left weapon or shield against the equipped armor, every frame of the animation the selected entry plays, measured on
// the models' own meshes (LcGeometry, LcMeasure: the clearance measure of the pack's left_clear.py, ported).
//   - The timeline gets a strip under its frame numbers: each frame of the hkx green (clear), yellow (under 1 cm), orange to red
//     (deeper, 5 cm and more red); hovering it names the body parts and how deep.
//   - The viewport draws where it crosses at the frame shown (the armor coming through a shield's face, or a weapon's points inside
//     the armor) and the status lists the parts.
//   - Measured as shown: an entry marked 'Play Mirrored' is measured mirrored; the armor's meshes the draw mask hides don't count.
//   - Runs in the background when the entry, its hkx, the mirror mark or the equipment change.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DSAnimStudio.HkxEdit;
using DSAnimStudio.TaeEditor;
using ImGuiNET;
using Microsoft.Xna.Framework;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DSAnimStudio.LeftClear
{
    public static class LeftItemCheck
    {
        public const string MenuText = "Left Item Check";
        public const string MirrorMenuText = "Play Mirrored";
        public const double Floor = 0.003;      // m: shallower than this counts as clear (the strip's green)
        public const double Deep = 0.05;        // m: the strip's full red

        public static bool Enabled
        {
            get => Main.Config.LeftItemCheck;
            set => Main.Config.LeftItemCheck = value;
        }

        /// <summary>The geometry for one skeleton, armor and item.</summary>
        public sealed class Scene
        {
            public string Key;
            public LcSkeleton Sk;
            public LcBody Body;
            public LcItem Item;
            public LcMeasure Measure;
            public string ItemText;
            public string ArmorText;
        }

        /// <summary>One animation measured.</summary>
        public sealed class Result
        {
            public string Key;
            public Scene Scene;
            public string AnimName;
            public bool Mirrored;
            public double Fps;
            public int Frames;
            public Dictionary<string, double>[] Rows;
            public double[] Worst;
            public double Seconds;

            public int FrameAt(double time) => Math.Clamp((int)Math.Round(time * Fps), 0, Frames - 1);
        }

        // what the editor shows now, and the work on it
        sealed class Request
        {
            public string Key;
            public string SceneKey;
            public string AnimName;
            public bool Mirrored;
            public byte[] Hkx, Compendium;
            public HkxAnimation Preview;     // a left arm fix being previewed: its solved copy instead of the file
            public HKX.HKASkeleton Skeleton;
            public zzz_DocumentIns Doc;
            public string ChrName;
            public List<(NewEquipSlot_Armor slot, string piece, string name)> Armor;
            public bool[] Mask;
            public ParamData.EquipParamWeapon Equip;
            public NewChrAsm.EquipSlotTypes SlotType;
            public int Dummy;
            public string ItemName;
        }

        /// <summary>The self-test's switch: every armor mesh counts, as in the pack's left_clear.py, whatever the draw mask hides.</summary>
        internal static bool MaskOff;

        /// <summary>The result for what the editor shows now, or null with why not (the self-test reads it).</summary>
        internal static Result CurrentResult(out string state) => Current(out state);

        static readonly object gate = new object();
        static Scene scene;
        static Result result;
        static string currentKey, runningKey, message;
        static double progress;
        static CancellationTokenSource cts;
        static DateTime lastLook;

        // ---- shared with the left arm fix (LeftClearFix)

        /// <summary>The armor the character wears, as the viewer loaded it (the gauntlets left out where a weapon hides them).</summary>
        internal static List<(NewEquipSlot_Armor slot, string piece, string name)> ArmorWorn(NewChrAsm asm)
        {
            bool hideArms = asm.WeaponSlots.Any(s => s.IsERHideGlovesModel);
            var armor = new List<(NewEquipSlot_Armor, string, string)>();
            foreach (var (type, piece) in new[] { (NewChrAsm.EquipSlotTypes.Body, "bd"), (NewChrAsm.EquipSlotTypes.Arms, "am"),
                                                 (NewChrAsm.EquipSlotTypes.Legs, "lg"), (NewChrAsm.EquipSlotTypes.Head, "hd") })
            {
                if (type == NewChrAsm.EquipSlotTypes.Arms && hideArms)
                    continue;
                if (!asm.ArmorSlots_ByEquipSlot.TryGetValue(type, out var slot) || !slot.CheckIfModelLoaded())
                    continue;
                string name = slot.GetPartsbndName(asm.IsFemale, asm.CurrentPartSuffixType);
                if (name != null)
                    armor.Add((slot, piece, Utils.GetShortIngameFileName(name)));
            }
            return armor;
        }

        /// <summary>The armor's geometry on the skeleton; meshes the draw mask hides don't count.</summary>
        internal static LcBody BodyOf(LcSkeleton sk, List<(NewEquipSlot_Armor slot, string piece, string name)> armor, bool[] mask)
        {
            var pieces = new List<LcArmorPiece>();
            foreach (var (slot, piece, name) in armor)
            {
                var bnd = slot.GetPartsbnd(slot.ASM.IsFemale, slot.ASM.CurrentPartSuffixType, disableCache: false);
                pieces.Add(new LcArmorPiece { Name = name, Piece = piece, Flver = MainFlver(bnd, name) });
            }
            return new LcBody(sk, pieces, (p, mi) =>
            {
                var m = p.Flver.Meshes[mi];
                string mat = m.MaterialIndex >= 0 && m.MaterialIndex < p.Flver.Materials.Count ? p.Flver.Materials[m.MaterialIndex].Name : null;
                int g = LcBody.MaskGroup(mat);
                return g < 0 || g >= mask.Length || mask[g];
            });
        }

        /// <summary>A left-hand item: its model hung on its dummy of the character's model; a shield (on a *Shield bone) as its plate.</summary>
        internal static LcItem ItemOf(LcSkeleton sk, FLVER2 chr, FLVER2 itemFlver, int dummy, string name)
        {
            if (dummy < 0 || dummy >= 1000 || !chr.Dummies.Any(d => d.ReferenceID == dummy))
                throw new InvalidOperationException($"{name} hangs on dummy {dummy}, which the character's model doesn't have");
            var d0 = chr.Dummies.First(d => d.ReferenceID == dummy);
            string bone = d0.AttachBoneIndex >= 0 && d0.AttachBoneIndex < chr.Nodes.Count ? chr.Nodes[d0.AttachBoneIndex].Name : "";
            bool shield = bone.Contains("Shield");      // a shield hangs on L_Shield / R_Shield (dummy 23 on the left)
            var item = shield ? LcItem.Plate(name, name, itemFlver) : LcItem.PointsOf(name, name, itemFlver);
            item.HangOnDummy(sk, chr, dummy);
            return item;
        }

        /// <summary>A weapon's name: its FMG name and model, else its ID.</summary>
        internal static string WeaponName(zzz_DocumentIns doc, int id, ParamData.EquipParamWeapon equip)
        {
            string model = equip?.GetPartBndName_Short(NewChrAsm.EquipSlotTypes.LeftWeapon) ?? "?";
            return doc?.FmgManager?.WeaponNamesMap_A != null && doc.FmgManager.WeaponNamesMap_A.TryGetValue(id, out var wn)
                   && !string.IsNullOrWhiteSpace(wn) && !wn.Contains("[ERROR]") ? $"{wn} ({model})" : $"weapon {id} ({model})";
        }

        // ---- the request, every frame on the editor's thread

        static Request Look(TaeEditorScreen tae, out string why)
        {
            why = null;
            var model = tae?.Graph?.ViewportInteractor?.CurrentModel;
            var asm = model?.ChrAsm;
            var c = model?.AnimContainer;
            var entry = tae?.SelectedAnim;
            var proj = tae?.Proj;
            if (model == null || c == null || entry == null || proj == null)
            {
                why = "no animation selected";
                return null;
            }
            if (asm == null)
            {
                why = "the check needs a character with equipment (c0000)";
                return null;
            }
            var hkxId = entry.GetHkxID(proj);
            var bytes = hkxId.IsValid ? c.FindAnimationBytes(hkxId) : null;
            if (bytes?.HkxBytes == null)
            {
                why = "the entry's hkx is not loaded";
                return null;
            }
            var sk = c.Skeleton?.OriginalHavokSkeleton;
            if (sk == null)
            {
                why = "no skeleton is loaded";
                return null;
            }
            var left = asm.WeaponSlots.FirstOrDefault(s => s.EquipSlotType == NewChrAsm.EquipSlotTypes.LeftWeapon);
            var equip = left?.EquipParam;
            if (equip == null)
            {
                why = "nothing in the left hand";
                return null;
            }
            int dummy = left.GetFinalAbsorpModelDmyPoly(0);
            var armor = ArmorWorn(asm);
            if (armor.Count == 0)
            {
                why = "no armor worn to measure against";
                return null;
            }
            var mask = (bool[])model.DrawMask.Clone();
            if (MaskOff)
                mask = Enumerable.Repeat(true, mask.Length).ToArray();
            string itemName = equip.GetPartBndName_Short(left.EquipSlotType);
            string weaponName = WeaponName(tae.ParentDocument, left.EquipID, equip);
            bool mirrored = MirrorPlayback.IsMirrored(proj, entry.SplitID);
            var preview = LeftClearFix.PreviewOf(proj, entry.SplitID, out int previewVersion);
            string sceneKey = string.Join("|", RuntimeHelpers.GetHashCode(sk), model.Name, itemName, dummy,
                                          string.Join(",", armor.Select(a => a.Item3)), new string(mask.Select(m => m ? '1' : '0').ToArray()));
            return new Request
            {
                SceneKey = sceneKey,
                Key = string.Join("|", sceneKey, hkxId.GetFullID(proj, null), RuntimeHelpers.GetHashCode(bytes.HkxBytes), mirrored, previewVersion),
                AnimName = $"{DuplicateCrop.HkxName(hkxId)}{(preview != null ? " (the left arm fix's preview)" : "")}{(mirrored ? " (mirrored)" : "")}",
                Preview = preview,
                Mirrored = mirrored,
                Hkx = bytes.HkxBytes,
                Compendium = bytes.CompendiumBytes,
                Skeleton = sk,
                Doc = tae.ParentDocument,
                ChrName = model.Name,
                Armor = armor,
                Mask = mask,
                Equip = equip,
                SlotType = left.EquipSlotType,
                Dummy = dummy,
                ItemName = weaponName,
            };
        }

        /// <summary>Starts the measure when what the editor shows has changed (cheap otherwise; called while drawing).</summary>
        public static void Update(TaeEditorScreen tae, bool now = false)
        {
            if (!Enabled || (!now && (DateTime.Now - lastLook).TotalMilliseconds < 100))
                return;
            lastLook = DateTime.Now;
            var req = Look(tae, out string why);
            lock (gate)
            {
                if (req == null)
                {
                    currentKey = null;
                    message = why;
                    return;
                }
                currentKey = req.Key;
                if (result?.Key == req.Key || runningKey == req.Key)
                    return;
                cts?.Cancel();
                cts = new CancellationTokenSource();
                runningKey = req.Key;
                progress = 0;
                message = null;
            }
            var token = cts.Token;
            Task.Run(() => Run(req, token));
        }

        static void Run(Request req, CancellationToken token)
        {
            try
            {
                var t0 = DateTime.Now;
                Scene s;
                lock (gate)
                    s = scene;
                if (s?.Key != req.SceneKey)
                    s = Build(req);
                if (token.IsCancellationRequested)
                    return;
                HkxAnimation a = req.Preview;
                try
                {
                    a ??= HkxAnimationFile.Read(req.Hkx, req.Compendium).Decoded;
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException("the hkx can't be decoded: " + e.Message);
                }
                if (req.Mirrored)
                    a = HkxAnimationOps.Mirror(s.Sk.Hkx, a);
                var (wt, wq) = s.Sk.Pose(a);
                var rows = s.Measure.Run(wt, wq, new[] { s.Item }, p => progress = p, () => token.IsCancellationRequested);
                if (rows == null)
                    return;
                var r = new Result
                {
                    Key = req.Key,
                    Scene = s,
                    AnimName = req.AnimName,
                    Mirrored = req.Mirrored,
                    Fps = a.Fps,
                    Frames = rows.Length,
                    Rows = rows.Select(x => x[0]).ToArray(),
                    Seconds = (DateTime.Now - t0).TotalSeconds,
                };
                r.Worst = r.Rows.Select(d => d.Count == 0 ? 0 : d.Values.Max()).ToArray();
                lock (gate)
                {
                    scene = s;
                    if (runningKey == req.Key)
                    {
                        result = r;
                        runningKey = null;
                    }
                }
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    if (runningKey == req.Key)
                    {
                        runningKey = null;
                        result = null;
                        message = "the check failed: " + e.Message;
                    }
                }
            }
        }

        internal static FLVER2 MainFlver(SoulsFormats.IBinder bnd, string what)
        {
            var f = bnd?.Files.FirstOrDefault(x =>
            {
                string n = x.Name.ToLowerInvariant();
                return n.EndsWith(".flver") && !n.EndsWith("_1.flver") && !n.EndsWith("_2.flver") && !n.EndsWith("_3.flver") && !n.EndsWith("_l.flver");
            });
            if (f == null)
                throw new InvalidOperationException($"{what} has no model");
            return FLVER2.Read(f.Bytes);
        }

        static Scene Build(Request req)
        {
            var sk = new LcSkeleton(HkxSkeleton.FromHavok(req.Skeleton));
            var body = BodyOf(sk, req.Armor, req.Mask);
            var chr = MainFlver(req.Doc.GameData.ReadBinder($"/chr/{req.ChrName}.chrbnd.dcx"), req.ChrName);
            var item = ItemOf(sk, chr, MainFlver(req.Equip.GetPartBnd(req.SlotType, false), req.ItemName), req.Dummy, req.ItemName);
            bool shield = item.Kind == LcItemKind.Plate;
            return new Scene
            {
                Key = req.SceneKey,
                Sk = sk,
                Body = body,
                Item = item,
                Measure = new LcMeasure(body),
                ItemText = $"{req.ItemName}, {(shield ? "the shield's face" : "its model's points")}, on {item.Hanging}",
                ArmorText = string.Join(", ", req.Armor.Select(a => a.name)),
            };
        }

        // ---- reading the result

        static Result Current(out string state)
        {
            lock (gate)
            {
                if (currentKey == null)
                {
                    state = message ?? "nothing to measure";
                    return null;
                }
                if (result?.Key == currentKey)
                {
                    state = null;
                    return result;
                }
                state = runningKey == currentKey ? $"measuring ({progress * 100:0}%)" : message ?? "waiting";
                return null;
            }
        }

        public static string Describe(Dictionary<string, double> d, double floor = Floor)
        {
            var parts = d.Where(kv => kv.Value > floor).OrderByDescending(kv => kv.Value).Select(kv => $"{LcParts.Display(kv.Key)} {kv.Value * 100:0.0} cm").ToList();
            return parts.Count > 0 ? string.Join(", ", parts) : "clear";
        }

        static Color DepthColor(double d)
        {
            if (d <= Floor)
                return new Color(40, 150, 60);
            if (d <= 0.01)
                return new Color(230, 210, 40);
            float t = (float)Math.Clamp((d - 0.01) / (Deep - 0.01), 0, 1);
            return Color.Lerp(new Color(255, 140, 0), new Color(220, 20, 20), t);
        }

        // ---- the timeline strip (NewGraphDrawer, in the playback cursor's coordinates)

        public static void DrawStrip(NewGraph graph, Rectangle viewportRect)
        {
            if (!Enabled || graph?.MainScreen == null)
                return;
            Update(graph.MainScreen);
            var r = Current(out string state);
            float y = graph.TimeLineHeight - 8, h = 5;
            double px = graph.LayoutManager.SecondsPixelSize, scroll = graph.ScrollViewer.RoundedScroll.X;
            if (r == null)
            {
                double end = graph.PlaybackCursor?.MaxTime ?? 0;
                if (state != null && state.StartsWith("measuring"))
                {
                    float w = (float)(end * px * progress);
                    ImGuiDebugDrawer.DrawRect(new Vector2((float)-scroll, y), new Vector2((float)(end * px), h), Color.Gray * 0.4f);
                    ImGuiDebugDrawer.DrawRect(new Vector2((float)-scroll, y), new Vector2(w, h), Color.Gray);
                }
                return;
            }
            for (int f = 0; f < r.Frames; f++)
            {
                double a = Math.Max(0, (f - 0.5) / r.Fps), b = (f + 0.5) / r.Fps;
                float x0 = (float)(a * px - scroll), x1 = (float)(b * px - scroll);
                if (x1 < 0 || x0 > viewportRect.Width)
                    continue;
                ImGuiDebugDrawer.DrawRect(new Vector2(x0, y), new Vector2(Math.Max(1, x1 - x0), h), DepthColor(r.Worst[f]));
            }
            // hovering the strip names the frame's parts
            var mouse = Main.Input.MousePosition - new Vector2(viewportRect.X, viewportRect.Y);
            if (mouse.Y >= y - 2 && mouse.Y <= y + h + 2 && mouse.X >= 0 && mouse.X <= viewportRect.Width)
            {
                double t = (mouse.X + scroll) / px;
                int f = r.FrameAt(t);
                if (t >= -0.5 / r.Fps && t <= (r.Frames - 0.5) / r.Fps)
                {
                    string text = $"{r.AnimName} frame {f}{(r.Fps > 31 ? $" ({r.Fps:0} fps)" : "")}: {Describe(r.Rows[f])}";
                    ImGuiDebugDrawer.DrawText(text, new Vector2(mouse.X + 12, y + h + 2), Color.White, Color.Black, 16);
                }
            }
        }

        // ---- the viewport: the crossing at the frame shown, and the status lines

        static readonly object liveGate = new object();
        static (string key, double time, DateTime at, List<LcHit> hits, Dictionary<string, double> parts) live;

        static (List<LcHit> hits, Dictionary<string, double> parts) Live(Model model, Result r)
        {
            double time = model.AnimContainer?.CurrentAnimTime ?? 0;
            lock (liveGate)
            {
                if (live.key == r.Key && (live.time == time || (DateTime.Now - live.at).TotalMilliseconds < 40))
                    return (live.hits, live.parts);
            }
            var bones = model.AnimContainer?.Skeleton?.Bones;
            var sk = r.Scene.Sk;
            if (bones == null || bones.Count != sk.Count)
                return (null, null);
            var wt = new D3[1, sk.Count];
            var wq = new DQ[1, sk.Count];
            for (int b = 0; b < sk.Count; b++)
            {
                bones[b].FKMatrix.Decompose(out _, out var q, out var t);
                wt[0, b] = new D3(t.X, t.Y, t.Z);
                wq[0, b] = new DQ(q.X, q.Y, q.Z, q.W).Normalized();
            }
            var it = r.Scene.Item;
            var fr = r.Scene.Measure.Prepare(wt, wq, 0, it.Kind == LcItemKind.Plate, it.Kind == LcItemKind.Points, trees: false);
            var hits = new List<LcHit>();
            var parts = r.Scene.Measure.Depths(fr, it, wt, wq, hits);
            lock (liveGate)
                live = (r.Key, time, DateTime.Now, hits, parts);
            return (hits, parts);
        }

        /// <summary>Draws the crossing at the frame shown (Main's viewport pass, after the status).</summary>
        public static void DrawViewport(TaeEditorScreen tae)
        {
            if (!Enabled || tae == null)
                return;
            Update(tae);
            var model = tae.Graph?.ViewportInteractor?.CurrentModel;
            var r = Current(out _);
            if (model == null || r == null)
                return;
            var (hits, _) = Live(model, r);
            if (hits == null || hits.Count == 0)
                return;
            var world = model.CurrentTransform.WorldMatrix;
            foreach (var h in hits.Where(h => h.Depth > Floor).OrderByDescending(h => h.Depth).Take(600))
            {
                var p = Vector3.Transform(new Vector3((float)h.P.X, (float)h.P.Y, (float)h.P.Z), world);
                var s = GFX.CurrentWorldView.Project3DPosToScreen(p);
                ImGuiDebugDrawer.DrawCircle(s, h.Depth > 0.01 ? 3 : 2, DepthColor(h.Depth));
            }
        }

        /// <summary>The status lines (the viewport's status, TaeViewportInteractor.BuildStatus).</summary>
        public static void AppendStatus(StatusPrinter printer, TaeEditorScreen tae, bool full)
        {
            var proj = tae?.Proj;
            var entry = tae?.SelectedAnim;
            if (proj != null && entry != null && MirrorPlayback.IsMirrored(proj, entry.SplitID))
                printer.AppendLine("Playing mirrored (as a mirrored behavior clip)", Color.Cyan);
            if (proj != null && entry != null && LeftClearFix.PreviewOf(proj, entry.SplitID, out _) != null)
                printer.AppendLine("Previewing the left arm fix (Edit > Left Arm Clearance: write the copy or discard it)", Color.Cyan);
            if (!Enabled)
                return;
            var r = Current(out string state);
            if (r == null)
            {
                printer.AppendLine($"[{MenuText}] {state}", Color.Yellow);
                return;
            }
            var model = tae.Graph?.ViewportInteractor?.CurrentModel;
            var (_, parts) = model != null ? Live(model, r) : (null, null);
            int over = r.Worst.Count(w => w > 0.01);
            printer.AppendLine($"[{MenuText}] {r.Scene.ItemText}", Color.White);
            if (full)
                printer.AppendLine($"    against {r.Scene.ArmorText}", Color.White);
            if (parts != null)
                printer.AppendLine($"    this frame: {Describe(parts)}", parts.Values.Any(v => v > Floor) ? DepthColor(parts.Values.Max()) : Color.White);
            printer.AppendLine($"    {r.AnimName}: {over} of {r.Frames} frames over 1 cm", over > 0 ? Color.Orange : Color.White);
            if (full && over > 0)
            {
                var per = new Dictionary<string, (int n, double deep, int at)>();
                for (int f = 0; f < r.Frames; f++)
                    foreach (var (p, v) in r.Rows[f])
                        if (v > 0.01)
                        {
                            var (n, deep, at) = per.TryGetValue(p, out var x) ? x : (0, 0.0, -1);
                            per[p] = (n + 1, Math.Max(deep, v), v > deep ? f : at);
                        }
                foreach (var kv in per.OrderByDescending(kv => kv.Value.deep))
                    printer.AppendLine($"      {LcParts.Display(kv.Key)}: {kv.Value.n} frames, {kv.Value.deep * 100:0.0} cm at frame {kv.Value.at}", Color.White);
            }
        }

        // ---- the menu (Animation)

        static void Tip(string text)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        public static void BuildMenu(TaeEditorScreen tae)
        {
            var entry = tae?.SelectedAnim;
            var proj = tae?.Proj;
            bool canMirror = entry != null && proj != null && !entry.IS_DUMMY_ANIM;
            bool mirrored = canMirror && MirrorPlayback.IsMirrored(proj, entry.SplitID);
            string cant = MirrorPlayback.CantMirror(tae?.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer?.Skeleton?.OriginalHavokSkeleton);
            bool newMirrored = ImguiOSD.MenuBar.Checkbox(MirrorMenuText + " (this entry)", mirrored, enabled: canMirror && cant == null,
                shortcut: canMirror ? entry.SplitID.GetFormattedIDString(proj, null) : null);
            Tip("Plays the selected entry as a behavior clip with the mirror flag plays it in game: left and right "
                + "swapped. The editor reads no behavior, so it can't know which clips are mirrored; mark them here. Kept for the session."
                + (cant != null ? "\n\nNot available: " + cant + "." : ""));
            if (canMirror && newMirrored != mirrored)
            {
                MirrorPlayback.SetMirrored(proj, entry.SplitID, newMirrored);
                tae.Graph?.ViewportInteractor?.OnNewAnimSelected(0, false, entry);
            }
            Enabled = ImguiOSD.MenuBar.Checkbox(MenuText, Enabled);
            Tip("Whether the item in the left hand goes into the body: the equipped left weapon or shield against the "
                + "equipped armor, every frame of the selected animation. A strip under the timeline's frame numbers colours each frame "
                + "(green clear, yellow under 1 cm, orange to red deeper; hover it for the body parts), the viewport marks where it crosses "
                + "and the status lists it. A shield counts the armor coming through its face, any other item its points inside the armor.");
        }
    }
}
