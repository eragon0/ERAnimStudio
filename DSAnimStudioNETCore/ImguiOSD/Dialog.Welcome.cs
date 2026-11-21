using ImGuiNET;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DSAnimStudio.ImguiOSD
{
    public abstract partial class Dialog
    {
        public class Welcome : Dialog
        {

            
            public Welcome()
                : base("Welcome###NewWelcomeDialog")
            {
                CancelHandledByInheritor = true;
                AcceptHandledByInheritor = true;
                TitleBarXToCancel = false;
                EscapeKeyToCancel = false;
                AllowsResultCancel = false;

                AutoResize = false;
                NoMove = true;
                NoResize = true;

                
            }
            
            protected override void BuildInsideOfWindow()
            {
                var thisDialogSize = new Vector2(640, 600) * Main.DPIVector;   // ERAnimStudio: room for the text without scrolling
                var windowSize = Program.MainInstance.Window.ClientBounds.Size.ToVector2();

                ImGui.SetWindowPos(((windowSize / 2) - (thisDialogSize / 2)).ToCS());
                ImGui.SetWindowSize(thisDialogSize.ToCS());

                ImGui.TextWrapped(
                    // ERAnimStudio: what the fork adds, in place of DS Anim Studio's note on its v5 layout
                    "Welcome to ER Anim Studio, an Elden Ring fork of DS Anim Studio v5.0.\n\n" +
                    "What it adds:\n\n" +
                    "- Edit > New Animation from Current (Ctrl+Insert): a new animation from the selected one, " +
                    "with its own hkx, copied or moved to a new ID, cropped to a frame range and / or mirrored, " +
                    "written into the split animation binder you pick.\n\n" +
                    "- Animation > Play Mirrored: an entry played left and right swapped, as the game plays a " +
                    "mirrored clip. Animation > Left Item Check: whether the left-hand item goes into the body " +
                    "or the right-hand weapon, frame by frame.\n\n" +
                    "- Edit > Left Arm Clearance (Ctrl+Shift+L): the left arm moved so the left-hand items stay " +
                    "off the body, previewed before it is written as a new animation.\n\n" +
                    "- Crops, mirrors and the clearance's copies need Havok Content Tools 2018.1: see " +
                    "Toolbox > [Havok Content Tools].\n\n" +
                    "- Fixes: the in-game reload (F5) no longer crashes Elden Ring, no lag with weapons that carry " +
                    "hundreds of animations, Enter saves Edit Animation Properties (F3), duplicates land in ID order.\n\n" +
                    "Everything else works as in DS Anim Studio. More on the project's page:");
                // ERAnimStudio: the project's page on a row of its own, opened in the browser on a click
                const string projectPage = "https://github.com/eragon0/ERAnimStudio";
                ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.75f, 1f, 1f), projectPage);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip("Open in the browser");
                }
                if (ImGui.IsItemClicked())
                    MenuBar.OpenSite(projectPage);
                ImGui.Text(" ");
                ImGui.Separator();
                bool iUnderstand = Main.Config.WelcomeMessageDisabled;
                ImGui.SetCursorPosX(240 * Main.DPI);
                ImGui.Checkbox("Don't show me this again.", ref iUnderstand);
                Main.Config.WelcomeMessageDisabled = iUnderstand;
                ImGui.Separator();
                if (Tools.SimpleClickButton("OK"))
                {
                    ResultType = ResultTypes.Accept;
                    Main.SaveConfig();
                    Dismiss();
                }
            }
        }
    }
}
