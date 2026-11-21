# ER Anim Studio

An Elden Ring fork of [DS Anim Studio](https://github.com/Meowmaritus/DSAnimStudio) by Meowmaritus.

It is DS Anim Studio 5.0-RC6.1 with tools for Elden Ring animation modding added and a few fixes. Everything else works as in DS Anim Studio, whose own README is kept in [README-DSAnimStudio.md](README-DSAnimStudio.md).

## Download

Get the zip from the [Releases](https://github.com/eragon0/ERAnimStudio/releases) page, unzip it anywhere and run `DS ANIM STUDIO.exe`.

## Requirements

- Windows 10 or 11, 64-bit, with a DirectX 11 graphics card.
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
- Elden Ring's `oo2core_6_win64.dll` (Oodle), which isn't in the zip: the program copies it from your game folder the first time it needs it, or asks you to browse to it.
- For crops and mirrors only: **Havok Content Tools 2018.1**. It is Havok's software and can't be shipped with this program. ER Anim Studio finds it on launch (the path given in the settings, the `HCT_FILTER_MANAGER` environment variable, the entry its installer writes in the registry, or its default folder) and shows where in **Toolbox > [Havok Content Tools]**, where you can also set the path by hand. Only version 2018.1 is used, since Elden Ring's animations are Havok 2018.1. Whole copies don't need it.

## What it adds

### New Animation from Current

**Edit > New Animation from Current...** (Ctrl+Insert) makes a new animation from the selected one, with an hkx file of its own. DS Anim Studio's **Duplicate Animation** copies only the TAE entry, which then plays the same hkx as the original.

- **Copy or move:** copy the animation to a new ID, or move the original to the new ID and leave the copy in its place. When the original plays its own hkx, that file is renamed with it, and entries that import the original follow it.
- **Crop:** keep only a frame range, in the animation's own frames as the playback's **Frame: X / N** counter shows them (an hkx is 30 or 60 fps; the dialog names the rate, and the Playhead buttons take the counter's current frame). The timeline's frame numbers stay at 30 fps (Graph > Snap Actions To Framerate), so on a 60 fps file they are half the counter's. The root motion starts again from the first frame kept, and the events are cut to the same range. With **Start the body on the character** (on by default), the root bone's sideways offset from the character at that frame, which can be tens of centimetres in the middle of a game attack, is moved into the root motion and handed back to the root bone over the animation: the body moves exactly as before, starts on the character and ends where the original does. Untick it for grabs, throws and critical attacks.
- **Mirror:** swap left and right in the hkx (each bone takes its twin's pose, the root motion's sideways drift and turn flip). The events stay as they are.
- **Where it goes:** into any open tab, and into the split animation binder you pick (the character's `cNNNN_*.anibnd.dcx` in the mod folder's `chr` folder).
- **Checked as you type:** the dialog says whether the new ID is free, keeps Accept off until the request can go ahead, and sums up what Accept will write and where.

Things to know:

- The new hkx is written into the binder **immediately**, not when you save the project.
- Crops and mirrors are re-encoded the way Elden Ring's files are, spline-compressed by Havok Content Tools.
- The game plays a new animation ID only if the character's behavior (`c0000.behbnd.dcx`) has a clip for it, and weapon-side animations in a weapon's model follow animation IDs too. Neither is changed by this tool.
- Elden Ring projects only.

### Play Mirrored

**Animation > Play Mirrored (this entry)** plays the selected entry left and right swapped, the way the game plays it when its behavior clip has the mirror flag (as most of Elden Ring's left-hand attacks do). The editor doesn't read the behavior, so you mark such entries yourself; the mark lasts until you close the program. The Left Item Check and the Left Arm Clearance work on the mirrored pose of a marked entry.

### Left Item Check

**Animation > Left Item Check** shows whether what the character holds in the left hand goes into the body during the selected animation. The equipped left weapon or shield is measured on every frame against the equipped armor and the weapon in the right hand, mesh against mesh:

- a strip under the timeline's frame numbers: green where it is clear, yellow under 1 cm, orange to red deeper (hover it to see which body parts);
- the crossing marked in the viewport on the frame shown, and the parts listed in the status line.

Armor pieces the equipment hides don't count. Unequip the right weapon to leave it out.

### Left Arm Clearance

**Edit > Left Arm Clearance...** (Ctrl+Shift+L) moves the left arm of a one-handed animation so the left-hand items stay off the body and off the right-hand weapon, and keeps the arm itself from going through the right-hand weapon. Typical use: a right-hand attack whose free left arm swings a shield or a second weapon through the torso.

- **Items:** the equipped left item, plus any weapon IDs you add to the list; each one either must clear or is cleared as far as possible.
- **Limits:** how far the wrist may move and how far the arm may turn (12 cm and 30 / 60 / 60 / 60 degrees by default); your values are kept.
- **Pins:** frame ranges where the arm keeps its own pose, for a left-hand strike or a grab (in the animation's own frames, as the **Frame: X / N** counter shows them).
- **Preview first:** the solve runs in the background (you can close the window meanwhile); its report gives the depth before and after for each item and the frames still over 1 cm, and **Look at it** plays the result with the Left Item Check measuring it. Nothing is written until you choose to.
- **Writing** works as New Animation from Current does: a new entry at a new ID (the first free one is offered) with the entry's events and its own hkx, or, with **Move the original to the new ID**, the original moved there and the fixed copy in its place. It needs Havok Content Tools 2018.1.

An entry marked Play Mirrored is solved as the game plays it, and the copy is written back unmirrored.

### Fixes to DS Anim Studio

- **Force Ingame Entity Reload** (File menu, F5) no longer crashes Elden Ring. It wrote the reload request to fields of an older game version; it now finds them in the running game's own code, and refuses with a message on a game version it can't read instead of writing anywhere. The game must run without EasyAntiCheat, as before.
- **Graph > Start with all TAEs collapsed** is on by default, and now really opens an anibnd with every TAE closed (DS Anim Studio still opened the first one).
- A weapon with hundreds of animations in its own parts file no longer makes playback lag.
- **Edit Animation Properties** (F3): Enter saves and closes, as **Save & Accept** does.
- **Duplicate Animation** puts the copy in ID order straight away.
- Duplicating an entry that imports its own ID's hkx keeps that import, instead of leaving the copy with no animation.

The full list of changes is in [CHANGES.md](CHANGES.md).

## Credits and licence

DS Anim Studio and SoulsAssetPipeline © 2018-2026 Meowmaritus. ER Anim Studio is Eragon's fork of it.

ER Anim Studio is licensed under the [GNU General Public License v3.0](LICENSE), as DS Anim Studio is. The libraries it uses keep their own licences, in the `LICENSE` folder of the release.
