# Changes from DS Anim Studio

ER Anim Studio is a modified version of DS Anim Studio by Meowmaritus. This file lists every change, with its date, as the GNU General Public License v3.0 asks of a modified version.

## 5.0-RC6.1-er.1 (2026-11-18)

Based on DS Anim Studio 5.0-RC6.1 (upstream commit `f1bff06`, "v5-rc6.1 - crash fixes").

### Added

- **New Animation from Current** (2026-11-17), in the Edit menu after Duplicate Animation, also on Ctrl+Insert (`TaeEditor\DuplicateCrop.cs`): a new animation from the selected one, with its own hkx, copied or moved to a new ID, optionally cropped to a frame range and / or mirrored, into any open tab and the split animation binder picked from the mod folder's. Its dialog checks the request as it is typed and sums up what Accept will write. Elden Ring projects only.
- **Elden Ring hkx editing** (2026-11-17, `HkxEdit\`): reads Elden Ring animation tagfiles through Havoc (spline-compressed or uncompressed, typed by their own types or by the binder's compendium, raw exports, default or directional root motion), crops and mirrors the frames, writes an uncompressed tagfile on the source's own types and has Havok Content Tools 2018.1 spline-compress it, then reads the result back and compares it. Two faults of Havoc's tagfile writer are worked around after writing: its type count leaves out Havok's empty slot 0, and two pointers to one object wrote two copies of it.
- **Start the body on the character** (2026-11-18, `HkxAnimationOps.EaseRootOntoCharacter`): an option of crops and mirrors, on by default. The root bone's sideways offset from the character at the first frame kept goes into the root motion and is handed back to the root bone over the animation, so the body's motion is unchanged (checked rigid within 1 mm), starts on the character and ends where the source has it; the root motion's travel changes by that offset. The root bone's turn stays with it: in attacks it is the body twisting through the swing, and moving it would turn the attack's direction.
- **Play Mirrored** (2026-11-18), in the Animation menu (`LeftClear\MirrorPlayback.cs`): the selected entry plays the way a behavior clip with the mirror flag plays it in game, left and right swapped. The editor reads no behavior, so such entries are marked by hand; the mark lasts for the session.
- **Left Item Check** (2026-11-18), in the Animation menu (`LeftClear\LeftItemCheck.cs`): whether the item in the left hand goes into the body. The equipped left weapon or shield is measured against the equipped armor on every frame of the entry's animation, on the models' meshes: a strip under the timeline's frame numbers (green clear, yellow under 1 cm, orange to red deeper; hover it for the body parts), the crossing marked in the viewport, the parts in the status.
- **Left Arm Clearance** (2026-11-18), in the Edit menu (`LeftClear\LeftClearFix.cs`): the selected entry's left arm changed so the left-hand items stay off the body, written as New Animation from Current writes: a new entry at a new ID (the first free one offered) with the entry's events and its own hkx, or, with 'Move the original to the new ID', the original moved there and the copy in its place. The items are the equipped one and a list of weapon IDs (each must clear, or best effort); the move and turn limits are the user's (12 cm, 30, 60, 60, 60 degrees by default); pins keep the arm's own pose for a left-hand strike; an entry marked Play Mirrored is solved as the game plays it. The solve runs in the background, then a preview (the entry plays the result and the Left Item Check measures it) comes before anything is written. Writing needs Havok Content Tools 2018.1.
- **Havok Content Tools in the settings** (2026-11-17): Toolbox > [Havok Content Tools], the path to `hctStandAloneFilterManager.exe`, filled on launch when empty.
- **A self-test** of New Animation from Current (2026-11-17, `TaeEditor\DuplicateCropSelfTest.cs`), compiled only with `-p:DuplicateCropSelfTest=true`, never in a release; and `Tools\HkxEditCheck`, a console check of the hkx editing. Self-tests of the Left Item Check and the Left Arm Clearance (2026-11-18, `LeftClear\LeftItemCheckSelfTest.cs`, `LeftClearFixSelfTest.cs`) under the same switch, and `Tools\LeftClearCheck`, a console check of their measures.

### Fixed

- **Lag with weapons that have many animations** (2026-11-17, `NewChrAsmWpnTaeManager.GetTaeAnimSolveRefs`): its dummy-entry fill ran on every playback update and grew with the square of the weapon's animation count; it is skipped when nothing changed since the last pass.
- **Enter in Edit Animation Properties** (2026-11-17, `Dialog.TaeAnimPropertiesEdit`): Enter saves and accepts while the window has focus.
- **Duplicate Animation in ID order** (2026-11-17): the copy is sorted into its category straight away.
- **Duplicates of an entry importing its own hkx** (2026-11-17, `TaeEditorScreen.ImmediateImportAnim`): the copy keeps the import, instead of playing an hkx under its new ID that doesn't exist.

### Changed

- **The name** (2026-11-17): the window title, the welcome dialog and the program's messages say ER Anim Studio; the version is 5.0-RC6.1-er.1. The program file, assemblies and code keep DS Anim Studio's names.
- **Credits** (2026-11-17): ER Anim Studio's line follows Meowmaritus's.
- **Menus** (2026-11-17): the Support Meowmaritus menu and the link to Meowmaritus's own Discord server are removed.
- **Licence text** (2026-11-18): the program's licence file in the release is the GPL-3.0 text of the repository (`LICENSE\ER ANIM STUDIO LICENSE.txt`), replacing an older MIT text that no longer applied.
- **Welcome dialog** (2026-11-18): it lists what the fork adds, in place of DS Anim Studio's note on its v5 layout, with a link to the project's page that opens in the browser; the box is larger, so it reads without scrolling.
- **README** (2026-11-18): `README.md` describes the fork; DS Anim Studio's own is kept unchanged as `README-DSAnimStudio.md`.
- **New Animation from Current counts in the animation's own frames** (2026-11-19): its frame fields, Playhead buttons, the source's range and the report count the source hkx's frames at its own rate (30 or 60 a second, or a raw export's), as the transport's Frame counter does; the source line and the help name the rate. It counted 30 a second whatever the file's rate, so on a 60 fps file its numbers were half the counter's. The timeline's frame numbers keep following Graph > Snap Actions To Framerate. `Tools\HkxEditCheck` takes the file's own frames too.
- **TAEs start collapsed** (2026-11-18): the setting Graph > Start with all TAEs collapsed is on by default, so an opened anibnd lists its TAEs closed. Configs that already have the setting keep their value.
- **Small accessors** for the new features (2026-11-17): `DialogManager.AddDialog` is internal, `zzz_DocumentManager.GetOpenFileDocuments` and `NewAnimationContainer.SwapLoadedHkx` are new. For the left-hand tools (2026-11-18): small hooks in `NewAnimationContainer` (mirrored and previewed playback), `NewHavokAnimation`, `NewGraphDrawer` (the strip), `TaeViewportInteractor` and `Main`.
