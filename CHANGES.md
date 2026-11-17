# Changes from DS Anim Studio

ER Anim Studio is a modified version of DS Anim Studio by Meowmaritus. This file lists every change, with its date, as the GNU General Public License v3.0 asks of a modified version.

## 5.0-RC6.1-er.1 (2026-11-18)

Based on DS Anim Studio 5.0-RC6.1 (upstream commit `f1bff06`, "v5-rc6.1 - crash fixes").

### Added

- **New Animation from Current** (2026-11-17), in the Edit menu after Duplicate Animation, also on Ctrl+Insert (`TaeEditor\DuplicateCrop.cs`): a new animation from the selected one, with its own hkx, copied or moved to a new ID, optionally cropped to a frame range and / or mirrored, into any open tab and the split animation binder picked from the mod folder's. Its dialog checks the request as it is typed and sums up what Accept will write. Elden Ring projects only.
- **Elden Ring hkx editing** (2026-11-17, `HkxEdit\`): reads Elden Ring animation tagfiles through Havoc (spline-compressed or uncompressed, typed by their own types or by the binder's compendium, raw exports, default or directional root motion), crops and mirrors the frames, writes an uncompressed tagfile on the source's own types and has Havok Content Tools 2018.1 spline-compress it, then reads the result back and compares it. Two faults of Havoc's tagfile writer are worked around after writing: its type count leaves out Havok's empty slot 0, and two pointers to one object wrote two copies of it.
- **Havok Content Tools in the settings** (2026-11-17): Toolbox > [Havok Content Tools], the path to `hctStandAloneFilterManager.exe`, filled on launch when empty.
- **A self-test** of New Animation from Current (2026-11-17, `TaeEditor\DuplicateCropSelfTest.cs`), compiled only with `-p:DuplicateCropSelfTest=true`, never in a release; and `Tools\HkxEditCheck`, a console check of the hkx editing.

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
- **Welcome dialog** (2026-11-18): it lists what the fork adds, in place of DS Anim Studio's note on its v5 layout.
- **README** (2026-11-18): `README.md` describes the fork; DS Anim Studio's own is kept unchanged as `README-DSAnimStudio.md`.
- **Small accessors** for the new feature (2026-11-17): `DialogManager.AddDialog` is internal, `zzz_DocumentManager.GetOpenFileDocuments` and `NewAnimationContainer.SwapLoadedHkx` are new.
