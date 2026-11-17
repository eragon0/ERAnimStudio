# ER Anim Studio

An Elden Ring fork of [DS Anim Studio](https://github.com/Meowmaritus/DSAnimStudio) by Meowmaritus.

It is DS Anim Studio 5.0-RC6.1 with tools for Elden Ring animation modding added and a few fixes. Everything else works as in DS Anim Studio, whose own README is kept in [README-DSAnimStudio.md](README-DSAnimStudio.md).

## Download

Get the zip from the [Releases](https://github.com/eragon0/ERAnimStudio/releases) page, unzip it anywhere and run `DS ANIM STUDIO.exe` (the program file keeps its original name).

## Requirements

- Windows 10 or 11, 64-bit, with a DirectX 11 graphics card.
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
- For crops and mirrors only: **Havok Content Tools 2018.1**. It is Havok's software and can't be shipped with this program. ER Anim Studio finds it on launch (the path given in the settings, the `HCT_FILTER_MANAGER` environment variable, the entry its installer writes in the registry, or its default folder) and shows where in **Toolbox > [Havok Content Tools]**, where you can also set the path by hand. Only version 2018.1 is used, since Elden Ring's animations are Havok 2018.1. Whole copies don't need it.

## What it adds

### New Animation from Current

**Edit > New Animation from Current...** (Ctrl+Insert) makes a new animation from the selected one, with an hkx file of its own. DS Anim Studio's **Duplicate Animation** copies only the TAE entry, which then plays the same hkx as the original.

- **Copy or move:** copy the animation to a new ID, or move the original to the new ID and leave the copy in its place. When the original plays its own hkx, that file is renamed with it, and entries that import the original follow it.
- **Crop:** keep only a frame range (frames at 30 fps, as the timeline shows them; the Playhead buttons take the timeline's current frame). The root motion starts again from the first frame kept, and the events are cut to the same range.
- **Mirror:** swap left and right in the hkx (each bone takes its twin's pose, the root motion's sideways drift and turn flip). The events stay as they are.
- **Where it goes:** into any open tab, and into the split animation binder you pick (the character's `cNNNN_*.anibnd.dcx` in the mod folder's `chr` folder).
- **Checked as you type:** the dialog says whether the new ID is free, keeps Accept off until the request can go ahead, and sums up what Accept will write and where.

Things to know:

- The new hkx is written into the binder **at once**, not when you save the project.
- Crops and mirrors are re-encoded the way Elden Ring's files are (spline-compressed by Havok Content Tools), then read back and compared with the edit; a result more than 1 cm off is not written.
- The game plays a new animation ID only if the character's behavior (`c0000.behbnd.dcx`) has a clip for it, and weapon-side animations in a weapon's model follow animation IDs too. Neither is changed by this tool.
- Elden Ring projects only.

### Fixes to DS Anim Studio

- A weapon with hundreds of animations of its own no longer makes playback lag.
- **Edit Animation Properties** (F3): Enter saves and closes, as **Save & Accept** does.
- **Duplicate Animation** puts the copy in ID order straight away.
- Duplicating an entry that imports its own ID's hkx keeps that import, instead of leaving the copy with no animation.

The full list of changes is in [CHANGES.md](CHANGES.md).

## Credits and licence

DS Anim Studio and SoulsAssetPipeline © 2018-2026 Meowmaritus. ER Anim Studio is Eragon's fork of it.

ER Anim Studio is licensed under the [GNU General Public License v3.0](LICENSE), as DS Anim Studio is. The libraries it uses keep their own licences, in the `LICENSE` folder of the release.
