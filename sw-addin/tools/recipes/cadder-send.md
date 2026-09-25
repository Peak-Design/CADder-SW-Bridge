---
title: CADder Bridge send to Blender with no dialogs, and a push of the poses
applies to: development of the CADder Bridge: live tests of Send to Blender, Refresh Model and Update from CAD
tags: cadder, bridge, send, blender, refresh, update, poses, configurations, append, link parts, direct link
run: return CadderSend();
uses: cadder-bridge
---
## What it does

- `CadderSend(options)`: Send to Blender of the active document, with no
  dialogs. It uses the parts of the ribbon's send (the export of each
  configuration, the payload, the post to Blender), so the files and the
  payloads are the same as the ribbon's. It does not ask about the CADder
  versions and does not show the report box. It returns one row for each
  configuration, with the reply of Blender.
- `CadderPushPoses()`: pushes the poses of the open assembly to Blender,
  with the rows that the listener gives Blender for `poses`. Use it after a
  drag (`cadder-mates`) to see a mechanism move in Blender.

To test the ribbon's own send with its dialogs, use `cadder-ribbon`.

## Steps

1. A lab session with the document open and active (see `cadder-bridge`).
2. A Blender with CADder must run, or the Export Options must allow a
   launch. With two or more Blender sessions, give `BlenderPid`.
3. Use `run` with recipe `cadder-send`. For other options, give code:

```
return CadderSend(new CadderSendOptions { Configurations = new List<string> { "Open", "Closed" } });
return CadderSend(new CadderSendOptions { Update = true, RigMode = "KEEP" });
return CadderSend(new CadderSendOptions { Append = true, LinkParts = false });
return CadderSend(new CadderSendOptions { Native = false });
return CadderPushPoses();
```

## Options

| Option | Default | Use |
| --- | --- | --- |
| `Configurations` | the active one | The configurations to send, each as a send of its own. |
| `Native` | true | True: the direct link (mesh). False: a STEP file. |
| `Update` | false | The payload of Refresh Model: it goes to the Blender that holds the document. |
| `RigMode` | the setting | With `Update`: `KEEP`, `APPEND` or `REGENERATE`. |
| `Append`, `LinkParts` | the setting | "Append as a new copy" and "Link identical parts" for this send only. The settings file does not change. |
| `OnlySelected` | as the ribbon | Only the selected components. |
| `BlenderPid` | the only one | The Blender to send to. |
| `Dir` | the export folder | The folder of the files. The default is the folder of the Export Options, as the ribbon uses. Give `Path.Combine(WorkDir, "cadder-send")` for a test. |

## Safety

A send changes the scene in Blender. Send only to a Blender that a test
started, or ask the user first.
