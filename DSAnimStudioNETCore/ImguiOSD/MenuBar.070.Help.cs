using ImGuiNET;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DSAnimStudio.ImguiOSD
{
    public static partial class MenuBar
    {
        public static void BuildMenuBar_070_Help(ref bool anyItemFocused, ref bool isAnyMenuExpanded)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Color.Cyan.ToNVector4());
            bool helpMenu = ImGui.BeginMenu("Help");
            ImGui.PopStyleColor();
            if (helpMenu)
            {
                isAnyMenuExpanded = true;

                OSD.WindowHelp.IsOpen = Checkbox("Show Basic Help Window", OSD.WindowHelp.IsOpen, textColor: Color.White);

                ImGui.Separator();

                if (ClickItem("Souls Modding Discord Server", textColor: Color.White,
                        shortcut: "?ServerName? (https://discord.gg/mT2JJjx)", shortcutColor: Color.Cyan))
                    OpenSite("https://discord.gg/mT2JJjx");

                // ERAnimStudio: no link to Meowmaritus's own Discord server in the fork

                ImGui.EndMenu();
            }
        }
    }
}
