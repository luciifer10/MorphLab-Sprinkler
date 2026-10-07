# MorphLab Sprinkler Designer — Revit 2023 · 2024 · 2025 · 2026

Automatic fire-sprinkler layout built from **Sprinkler System – Overall Notes** and the
INOX 5 GW roof-level layout (drawing FF-01-102/2).

## What it does

| Step | Tab | What happens |
|---|---|---|
| 1 | **Rooms** | Reads rooms from the host model **and every loaded architecture link**. Finds the ceiling height (lowest ceiling over the room, else room height). Auto-excludes Substation, Electrical Panel, Transformer, UPS, Server, TMA, BCL3, Battery, SCADA & IT and LV rooms (notes §6). Picks **K80**, or **K160** at/above the double-height threshold (notes §5). Height, K-type and upright/pendant can all be overridden per room. |
| 2 | **Heads** | The sprinkler **family library**. The four MorphLab families (K80 upright, K80 pendant, K160 upright, K240 upright) are built in and load into the project automatically when you place. **Add .rfa files** (K-factor, orientation and type detected automatically), **import** families already in the project, or create a **new K-factor version** of a head (copies the family, sets K-Factor in every type, renames it — the "same family, only the K changes" workflow). Choose per K-type/orientation which family is placed, or leave it on Auto. |
| 3 | **Plan** | Solves the sprinkler grid per room, aligned to the room's longest wall: fewest heads that keep head-to-head spacing and wall distance inside the K-rule (notes §1 and §3). Live preview with the branch lines and cross main. |
| 4 | **Place** | Places the heads and (optionally) builds **centre-fed branch lines + cross main + drops**, every segment sized by the number of heads it feeds (notes §2 and §4). One undo step. Re-running replaces the previous result in that room instead of duplicating it. |
| 5 | **Size** | Sizes any **existing** network (hand-drawn too): click the inlet pipe, every downstream pipe is sized by head count. **Manual override** sets a size and locks it so auto-sizing never changes it back (notes §2). |
| 6 | **Check** | Audits every head in the view: too close or too far from other heads, too close or too far from the wall, heads inside no-sprinkler rooms. Double-click to zoom; export CSV. Upright/pendant counts and a legend schedule (like the drawing legend). Installation-valve **zones** coloured like the 7-zone roof drawing. |
| ⚙ | **Rules** | Every number from the notes, editable and saved to `%AppData%\MorphLab\Sprinkler\settings.xml`. |

## Rules loaded by default

| | K80 | K160 / K320 |
|---|---|---|
| Head-to-head | 2500 – 4600 mm | 1800 – 4600 mm |
| Head-to-wall | 500 – 1500 mm | 500 – 2000 mm |
| Pipe schedule | 2→25, 3→32, 5→40, 10→50, 30→65, 60→80 | 1→25, 2→40, 4→50, 8→65, 9+→100 |

**Assumptions to confirm with your designer** (marked "assumed" in the UI):

1. K80 table stops at 60 heads → 80 mm. To size cross mains I added **≤100 → 100 mm** and **100+ → 150 mm** (the drawing's mains are 100/150).
2. No K320 pipe table was given → the K160 table is copied.
3. Double-height threshold defaults to **8000 mm** (notes don't give a number).
4. Max coverage per head: **12 m² (K80) / 9 m² (K160)**. Your notes only give spacing, but at 4600 × 4600 one head would cover 21 m², more than typical code limits. Set to 0 to switch off.
5. K160 "6–8 sprinklers → 65": 5 heads is not listed, so it takes the next size up (65 mm).

## Build

1. Copy the folder to `D:\Dev\MorphLab.Sprinkler` and open `MorphLab.Sprinkler.csproj` in Visual Studio 2022.
2. Choose configuration **Debug R23** (or R24/R25/R26) and build. Debug builds copy themselves into
   `%AppData%\Autodesk\Revit\Addins\<year>` automatically.
3. Release: build **Release R23, R24, R25, R26**, then compile `Installer\MorphLabSprinkler.iss` in Inno Setup →
   `C:\MorphLabSprinklerInstaller\MorphLabSprinkler_1.0.0_Setup.exe`.

Using your senior's Nice3point template instead: create the project from the template, then copy in
`App.cs`, `Core\`, `Revit\`, `UI\`, `Resources\` (Build Action = Resource for the PNGs) and the `.addin`.
The code has no `#if` version switches — it uses only API that exists in 2023–2026.

## Sprinkler families

Built-in (shipped in `…\Addins\<year>\MorphLab.Sprinkler\Families`):

| File | Used as | Note |
|---|---|---|
| K-80_Upright_Sprinkler.rfa | K80 upright | — |
| K80_PENDENT_SPRINKLER.rfa | K80 pendant | — |
| K160_UPRIGHT_SPRINKLER_0001.rfa | K160 upright | type name + K-Factor still say 80 (copied family) |
| K240_UPRIGHT_SPRINKLER.rfa | K240 upright | uses type "K-240 Upright @ 74°C"; K-Factor still 80 |

K-factor is read from the **family (file) name first**, then the type name, then the K-Factor parameter,
because copied families keep the old value. With *Correct K-Factor* on (default) the plugin writes the right
value into the type when it loads it, so schedules and hydraulic tools see K160 / K240.

Your own families are copied to `%AppData%\MorphLab\Sprinkler\Families` and indexed in `library.xml`, so
they survive plugin updates. Name them with the K-factor and orientation (e.g. `K115_PENDENT_SPRINKLER.rfa`)
and detection needs no correction. A family with a new K (e.g. K115) gets a rule copied from the nearest K,
marked "assumed" — confirm it in Rules.

There is **no K160/K240 pendant** in the library yet. Rooms that would need one switch to upright with a note
in the preview — add a pendant family or a K-factor version of the K80 pendant if you need it.

## Before the first run in a project

1. Nothing to load by hand — the library families load themselves. Your own families must be
   **non-hosted** (face-hosted ones can't be placed in open space and are rejected with a message).
2. The pipe type's **Routing Preferences** need an elbow, a tee and a cross. Without them pipes are still
   placed and sized, but fittings are counted as "failed".
3. Rooms must be placed and enclosed (host or link). Set realistic room heights — the default 2400 mm
   room height is flagged in the preview.

## Workflow

1. MorphLab tab → **Sprinkler Designer** → **Scan rooms** (active level or all levels).
2. Check the red rows (no-sprinkler rooms) and the Height column. Untick anything you don't want.
3. **Plan** → choose families, pipe type, system → **Plan selected rooms**. Review the preview and any amber notes.
4. **Place** → *Place sprinklers + piping*.
5. Join each room's inlet stub ("IN" in the preview) to your riser / installation valve.
6. **Size** → *Pick inlet pipe & size* on the zone's first pipe after the valve.
7. **Check** → *Check active view*; fix the red items; assign zones; colour zones; create the legend schedule.

## Known limits (v1.0)

- Spacing is grid-based per room. Columns, beams and ducts are **not** yet treated as obstructions — use Check and move heads where needed.
- Irregular (L-shaped) rooms are covered by one grid clipped to the outline; points that land too close to a re-entrant wall are dropped and reported.
- Gridded / looped networks: pipe-schedule sizing assumes a tree; loops are detected and flagged — use hydraulic calculation for those.
- Each room gets its own network with an inlet stub; connection to the riser is manual.
