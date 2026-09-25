---
title: CADder Bridge ribbon: the buttons, a click with its dialogs, and the progress bar
applies to: development of the CADder Bridge: the ribbon and the commands as a user runs them
tags: cadder, bridge, ribbon, command, button, click, send, refresh model, dialog, progress bar
run: return CadderRibbon();
uses: cadder-bridge
---
## What it does

- `CadderRibbon()`: reads the ribbon tab of the Bridge back from
  SolidWorks, for assemblies and parts: the buttons, the command ids, the
  Advanced setting and whether Refresh Model is enabled now. A button that
  a user cannot find is one that the add-in never added, or one that
  SolidWorks dropped. This tells which.
- `CadderClick("send")`: runs a ribbon command as a click on its button
  runs it, with all its dialogs. `"send"` is Send to Blender (direct link)
  and `"refresh"` is Refresh Model. A callback name also works:
  `SendToBlender`, `ExportRig`, `ExportRigJson`, `ExportStepPlus`,
  `BlenderOptions`. This tests the ribbon path itself. For a send with no
  dialogs, use `cadder-send`.
- `CadderProgressCheck(20, 40)`: runs the progress bar of the export
  through its stages, with no export, and returns what SolidWorks answered.

## A click with dialogs

The command waits for each dialog box, so the run waits too.

1. Use `run` with recipe `cadder-ribbon`, code
   `return CadderClick("send");` and `wait_s` 5.
2. The reply says that the run continues. Use `dialog` to read the dialog
   box (the configuration picker, the version question, the report of the
   send).
3. Press the button that the test needs, with `dialog` and `press`. Do
   this for each dialog box.
4. Use `job` to get the result: the command and the log lines that it wrote.

A click changes the scene in Blender, like `cadder-send`. Do it only in a
test, or when the user asks.
