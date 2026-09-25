---
title: CADder Bridge development: the lab session and the helpers
applies to: development and tests of the CADder Bridge add-in, in a lab SolidWorks
tags: cadder, bridge, lab, swlab, status, log, listener, debug, development
run: return CadderStatus();
---
## What this is

The CADder Bridge had its own test harness: `swlab.py` and the lab
operations of its listener. SW-MCP does that work now. These recipes run
the code of the Bridge itself in a lab SolidWorks. The listener of the
Bridge answers only what Blender asks for: `status`, `poses`,
`retessellate` and `export`.

The code file of this recipe has the helper class `Cadder`, which the
other CADder recipes use (`uses: cadder-bridge`). It finds the Bridge
assembly in SolidWorks and calls its classes by name. The Bridge test
`RecipeContractTests` checks these names, so a rename in the Bridge fails
a test.

## Before you start

1. Use `instances` to see the SolidWorks sessions. A session that you did
   not start is a session of the user. Do not run a CADder recipe that
   changes, closes or sends in it. Ask the user first.
2. Start a lab session: `instances` with `start` 2022. SolidWorks loads
   the Bridge at its start.
3. Use `run` with recipe `cadder-bridge` (no code). It returns
   `CadderStatus()`: the Bridge version, the DLL that SolidWorks loaded,
   the listener and the settings. Check that the DLL is the build that you
   want to test.

## Rebuild the Bridge

SolidWorks keeps the add-in DLL open.

1. Stop each lab session (`instances` with `stop`).
2. If a SolidWorks of the user loads the same build, ask the user to close it.
3. Build the Bridge.
4. Start a lab session again, and check `CadderStatus()`.

## The CADder recipes

| Recipe | Use |
| --- | --- |
| `cadder-export` | The export as Blender asks for it: manifest, STEP, mesh. Its test is the corpus sweep. |
| `cadder-send` | Send to Blender with no dialogs, and push the poses to Blender. |
| `cadder-ribbon` | The ribbon as SolidWorks shows it, the ribbon commands with their dialogs, and a check of the progress bar. |
| `cadder-mates` | The mate graph as the Bridge reads it, the probe of the constraint status, and a drag of a component. |
| `cadder-geometry` | Small features and the closure of the body after a defeature, texture coordinates on planes and faces. |
| `cadder-appearance` | A dump of the appearances and decals, and an appearance put on a part, a face or a component. |
| `cadder-compose` | Test assemblies and configurations, saved only under the temp folder. |

## Methods of this recipe

- `CadderStatus()`: the Bridge in this SolidWorks.
- `CadderLog(50)`: the last lines of `cadder-debug.log`.
- `CadderAsk("poses", fields)`: what the listener answers Blender for one
  request, run in SolidWorks with no HTTP. For example
  `return CadderAsk("status");`.

## The old swlab commands

| swlab | Now |
| --- | --- |
| `start`, `quit` | `instances` with `start` or `stop` (a lab session closes without a save). |
| `status`, `log` | `CadderStatus()`, `CadderLog()`, or `CadderAsk("status")`. |
| `open`, `close`, `activate`, `documents` | `run` with plain code, see below. `close --all` only in a lab session. |
| `rebuild`, `suppress`, `unsuppress`, `dimension`, `select` | `run` with plain code, see below. Set `undo_label`. |
| `screenshot` | The `view` tool. |
| `export` | `cadder-export`. |
| `send`, `refresh` | `cadder-send`: `CadderSend()` and `CadderPushPoses()`. |
| `run_command`, `ribbon`, `progress_demo` | `cadder-ribbon`. |
| `mates`, `status_probe`, `move` | `cadder-mates`. |
| `small_features`, `plane_uv`, `tess_uv` | `cadder-geometry`. |
| `appearances`, `apply_appearance` | `cadder-appearance`. |
| `compose`, `configure` | `cadder-compose`. |
| `Sweep-Corpus.py`, `swlab_sweep.py` | `recipe_test` with `cadder-export`, then `rig_report.py` in Blender. |

Plain code for the common steps (a lab session):

```csharp
// Open a corpus assembly read-only and make it the active document
string path = @"C:\PeakDesign\CADder-SW-Bridge\test-assemblies\01-hinge\hinge.SLDASM";
int e = 0, w = 0;
var m = swApp.OpenDoc6(path, (int)swDocumentTypes_e.swDocASSEMBLY,
    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref e, ref w);
if (m == null) throw new InvalidOperationException("SolidWorks did not open " + path + " (error " + e + ").");
swApp.ActivateDoc3(m.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e);
return m.GetTitle();
```

```csharp
// Suppress one mate, set one dimension, rebuild
string mate = "Concentric1";
string dimension = "D1@Distance1";
double value = 0.05;   // metres or radians

var f = Features(doc, true).FirstOrDefault(x => x.Name == mate);
if (f == null) throw new InvalidOperationException("No feature " + mate + ".");
f.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
    (int)swInConfigurationOpts_e.swThisConfiguration, null);
var d = (Dimension)doc.Parameter(dimension);
if (d == null) throw new InvalidOperationException("No dimension " + dimension + ".");
d.SetSystemValue3(value, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
doc.EditRebuild3();
return new { suppressed = mate, dimension = d.SystemValue };
```

```csharp
// Close all documents without a save (only in a lab session)
swApp.CloseAllDocuments(true);
return true;
```

## The corpus sweep

The corpus is `test-assemblies` at the root of the repository. It is not
in git. `recipe_test` with `cadder-export` opens each assembly of it,
exports the manifest and compares the joint shape, the counts and the
limits with the approved results. To rig each manifest in Blender after
the sweep:

```
blender -b --factory-startup -P sw-addin\tools\rig_report.py -- <WorkDir>\cadder-export
```

`<WorkDir>` is the work folder of the lab session. The reply of a run
shows it (`CadderStatus()` does not).

## Traps

- Do not change a decal through the API in a lab session. A new decal
  position and a redraw crashed SolidWorks 2022 at each try (2026-09-15).
- A render material that the Bridge read and released late, after its
  document closed, crashed SolidWorks (2026-09-23). The CADder methods call
  `Cadder.Flush()` at their end. Keep that in new methods that run the
  readers.
- A drag or a probe moves the assembly in memory. Close the document
  without a save, or undo, before the next test.
