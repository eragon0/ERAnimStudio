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
                var thisDialogSize = new Vector2(520, 340) * Main.DPIVector;
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
                    "- Crops and mirrors need Havok Content Tools 2018.1: see Toolbox > [Havok Content Tools].\n\n" +
                    "- Fixes: no lag with weapons that carry hundreds of animations, Enter saves Edit Animation " +
                    "Properties (F3), duplicates land in ID order.\n\n" +
                    "Everything else works as in DS Anim Studio. More on github.com/eragon0/ERAnimStudio.");
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
