# SPDX-License-Identifier: GPL-3.0-or-later
"""Rig every manifest under a sweep folder headlessly and write report.md.

    blender -b --factory-startup -P tools\\rig_report.py -- <sweep-dir>

Run by swlab_sweep.py; usable on its own for a folder of manifests. Each
<sweep-dir>\\<key>\\*.rig.json is loaded, planned and built in a fresh
empty scene; the line records groups, joints by type, loops by closure
kind, controls, and the first error or warning.
"""
import glob
import json
import os
import sys
import traceback

ADDONS = os.path.join(os.environ.get("APPDATA", ""), "Blender Foundation", "Blender",
                      "%d.%d" % tuple(__import__("bpy").app.version[:2]), "scripts", "addons")
sys.path.insert(0, ADDONS)
import bpy  # noqa: E402
from STEPper_NEXT.rig import graph, inputs, manifest as man_mod, rig_build  # noqa: E402


def rig_one(path):
    row = {"manifest": path}
    m = man_mod.load(path)
    row["components"] = len(m.components)
    row["groups"] = len(m.rigid_groups)
    types = {}
    for j in m.joints:
        types[j.type] = types.get(j.type, 0) + 1
    row["joints"] = types
    kinds = {}
    for lp in m.loops:
        kinds[lp.closure_kind] = kinds.get(lp.closure_kind, 0) + 1
    row["loops"] = kinds
    row["manifest_warnings"] = [w.code for w in m.warnings]
    row["choices"] = sum(1 for mech in inputs.mechanisms(m)
                         if len(inputs.candidates(m, mech)) > 1)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    plan = graph.build(m)
    row["plan_warnings"] = list(plan.warnings)
    result = rig_build.build(bpy.context, m, plan, None)
    arm = result.armature_object
    cols = {c.name: [b.name for b in c.bones] for c in arm.data.collections}
    row["controls"] = cols.get("SW_controls", [])
    row["mechanism"] = len(cols.get("SW_mechanism", []))
    row["helpers"] = len(cols.get("SW_helpers", []))
    row["build_warnings"] = list(result.warnings)
    return row


def main():
    out = sys.argv[sys.argv.index("--") + 1]
    rows = []
    for folder in sorted(os.listdir(out)):
        full = os.path.join(out, folder)
        if not os.path.isdir(full):
            continue
        manifests = glob.glob(os.path.join(full, "*.rig.json"))
        export = {}
        try:
            with open(os.path.join(full, "export.json"), "r", encoding="utf-8") as fh:
                export = json.load(fh)
        except (OSError, ValueError):
            pass
        row = {"key": folder, "export_ok": export.get("ok", None),
               "export_error": export.get("error"), "export_warnings": export.get("warnings")}
        if manifests:
            try:
                row.update(rig_one(manifests[0]))
            except Exception as exc:
                row["error"] = "%s: %s" % (type(exc).__name__, str(exc).splitlines()[0][:160])
                row["trace"] = traceback.format_exc()
        elif export.get("ok") is None:
            row["error"] = "not exported"
        elif export.get("ok"):
            row["error"] = "no manifest (a part, not an assembly?)"
        rows.append(row)
        with open(os.path.join(full, "rig.json"), "w", encoding="utf-8") as fh:
            json.dump(row, fh, indent=1)

    lines = ["# Sweep report", "",
             "| assembly | groups | joints | loops | controls | choices | status |",
             "|---|---|---|---|---|---|---|"]
    for r in rows:
        if r.get("error") or r.get("export_error"):
            status = "ERROR " + (r.get("error") or r.get("export_error"))
        else:
            notes = []
            if r.get("plan_warnings"):
                notes.append("plan: " + r["plan_warnings"][0][:70])
            if r.get("build_warnings"):
                notes.append("build: " + str(r["build_warnings"][0])[:70])
            if r.get("export_warnings"):
                notes.append("%d export warning(s)" % r["export_warnings"])
            status = "ok" + ("; " + "; ".join(notes) if notes else "")
        joints = ", ".join("%d %s" % (n, t) for t, n in sorted((r.get("joints") or {}).items()))
        loops = ", ".join("%d %s" % (n, k) for k, n in sorted((r.get("loops") or {}).items()))
        lines.append("| %s | %s | %s | %s | %s | %s | %s |" % (
            r["key"], r.get("groups", ""), joints, loops,
            ", ".join(r.get("controls", [])) if r.get("controls") is not None else "",
            r.get("choices", ""), status.replace("|", "/")))
    with open(os.path.join(out, "report.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    for line in lines[3:]:
        print("REPORT " + line)
    ok = sum(1 for r in rows if not r.get("error") and not r.get("export_error"))
    print("rig_report: %d of %d rigged" % (ok, len(rows)))


main()
