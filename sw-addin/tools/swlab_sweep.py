#!/usr/bin/env python3
"""Sweep a folder of assemblies through the lab: open, export the manifest,
close, one after another, then rig every manifest headlessly in Blender
and print one line per assembly.

    python tools\\swlab_sweep.py <folder-or-assembly> [...] --out <dir>
        [--blender <blender.exe>] [--skip-export] [--skip-rig]

Phase 1 needs a SolidWorks with the add-in listening (python tools\\swlab.py
start). Each assembly gets a folder under <out> named after its path, with
the manifest, the export reply (paths, joint shape, warnings, the log lines
the export wrote) and any error. Phase 2 runs tools\\rig_report.py inside
Blender over every manifest and writes <out>\\report.md.

The point is the table: which assemblies rig, with how many controls, and
which fail where. A reader or classifier change is checked against every
assembly in one command, and the failures are the next corpus entries.
"""
import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import swlab  # noqa: E402

BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"


def find_assemblies(paths):
    files = []
    for p in paths:
        if os.path.isdir(p):
            for root, _dirs, names in os.walk(p):
                for n in names:
                    if n.lower().endswith(".sldasm") and not n.startswith("~$"):
                        files.append(os.path.join(root, n))
        elif os.path.isfile(p):
            files.append(p)
        else:
            print("skipped (not found):", p)
    files.sort(key=lambda f: f.lower())
    return files


def key_for(path, roots):
    """A folder name that keeps two assemblies with one file name apart."""
    for r in roots:
        if os.path.isdir(r) and os.path.normcase(path).startswith(os.path.normcase(os.path.abspath(r))):
            rel = os.path.relpath(path, r)
            break
    else:
        rel = os.path.basename(path)
    stem = os.path.splitext(rel)[0]
    return stem.replace("\\", "__").replace("/", "__").replace(" ", "_")


def export_all(files, roots, out, timeout_s, resume=False):
    results = []
    for i, path in enumerate(files, 1):
        key = key_for(path, roots)
        folder = os.path.join(out, key)
        os.makedirs(folder, exist_ok=True)
        row = {"key": key, "assembly": path, "ok": False}
        if resume and os.path.isfile(os.path.join(folder, "export.json")):
            with open(os.path.join(folder, "export.json"), "r", encoding="utf-8") as fh:
                row = json.load(fh)
            if row.get("ok"):
                results.append(row)
                continue
        t0 = time.time()
        # SolidWorks can die on an assembly (a crash, or a modal dialog the
        # silent open could not avoid, then a kill): the sweep restarts it
        # and goes on, recording the loss against the assembly it was on.
        if not swlab.instances():
            print("SolidWorks is gone; starting it again")
            sys.stdout.flush()
            try:
                swlab.start()
            except SystemExit as exc:
                row["error"] = "restart: %s" % exc
                results.append(row)
                continue
        try:
            opened = swlab.call({"op": "open", "path": path, "timeout_s": timeout_s})
            if not opened.get("ok"):
                row["error"] = "open: " + str(opened.get("error"))
            else:
                exported = swlab.call({"op": "export", "dir": folder, "timeout_s": timeout_s})
                row.update({k: v for k, v in exported.items() if k != "log"})
                row["log"] = exported.get("log") or []
                if not exported.get("ok"):
                    row["error"] = "export: " + str(exported.get("error"))
        except (Exception, SystemExit) as exc:  # the listener failed, timed out or died
            row["error"] = "request: %r" % (exc,)
        finally:
            try:
                swlab.call({"op": "close", "all": True, "timeout_s": 60})
            except (Exception, SystemExit) as exc:
                row["close_error"] = repr(exc)
        row["seconds"] = round(time.time() - t0, 1)
        with open(os.path.join(folder, "export.json"), "w", encoding="utf-8") as fh:
            json.dump(row, fh, indent=1)
        status = "ok" if row.get("ok") else "FAIL"
        print("[%3d/%d] %-55s %s %5.1fs %s" % (
            i, len(files), key[:55], status, row["seconds"],
            row.get("joints") if row.get("ok") else row.get("error", "")[:80]))
        sys.stdout.flush()
        results.append(row)
    return results


def main(argv):
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("paths", nargs="+")
    p.add_argument("--out", required=True)
    p.add_argument("--blender", default=BLENDER)
    p.add_argument("--timeout", type=float, default=180.0,
                   help="seconds per SolidWorks request")
    p.add_argument("--skip-export", action="store_true")
    p.add_argument("--skip-rig", action="store_true")
    p.add_argument("--resume", action="store_true",
                   help="keep assemblies already exported into --out")
    a = p.parse_args(argv)
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)

    if not a.skip_export:
        files = find_assemblies(a.paths)
        print("%d assembly(s)" % len(files))
        if not swlab.instances():
            swlab.start()
        export_all(files, a.paths, out, a.timeout, resume=a.resume)

    if not a.skip_rig:
        report = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rig_report.py")
        cmd = [a.blender, "-b", "--factory-startup", "-P", report, "--", out]
        print("rigging every manifest in Blender...")
        sys.stdout.flush()
        proc = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
        for line in proc.stdout.splitlines():
            if line.startswith("REPORT ") or line.startswith("rig_report"):
                print(line[7:] if line.startswith("REPORT ") else line)
        if proc.returncode != 0:
            print(proc.stderr[-2000:])
            return 1
        print("report:", os.path.join(out, "report.md"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
