#!/usr/bin/env python3
"""Drive a running SolidWorks through the add-in's localhost listener.

    python swlab.py status
    python swlab.py start                     launch SolidWorks, wait for the listener
    python swlab.py open <file.sldasm>
    python swlab.py documents
    python swlab.py ribbon               what the add-in put on the ribbon
    python swlab.py export [--step] [--mesh] [--dir D] [--quality Q]
    python swlab.py send [--step] [--timeout S]   (default: direct link, native mesh)
    python swlab.py send --update [--rig-mode KEEP|APPEND|REGENERATE]
                                              what Refresh Model sends
    python swlab.py mates [--json]
    python swlab.py suppress <MateName> | unsuppress <MateName>
    python swlab.py dimension <D1@Distance1> [<value in m or rad>]
    python swlab.py rebuild
    python swlab.py screenshot [--out file.bmp] [--width W] [--height H]
    python swlab.py log [--lines N]
    python swlab.py close [--all]
    python swlab.py activate <title>
    python swlab.py quit
    python swlab.py raw '{"op": "...", ...}'

The listener is found through the registry files the add-in writes under
%LOCALAPPDATA%\\PeakDesign\\CADder\\solidworks (one per live instance,
with the port and the shared token). Every reply is JSON; the "log" list
in export, send, mates and open replies is what the add-in logged while
the operation ran.

COM activation is not possible from a shell without an interactive window
station, but launching SLDWORKS.exe and talking HTTP to it is, which is
the whole reason this client exists.
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

REGISTRY = os.path.join(os.environ.get("LOCALAPPDATA", ""), "PeakDesign",
                        "CADder", "solidworks")
SLDWORKS = r"C:\Program Files\SOLIDWORKS 2022\SOLIDWORKS\SLDWORKS.exe"


def instances():
    """Every registry entry whose listener answers a ping, newest first."""
    out = []
    try:
        names = os.listdir(REGISTRY)
    except OSError:
        return out
    for name in names:
        if not name.endswith(".json"):
            continue
        path = os.path.join(REGISTRY, name)
        try:
            with open(path, "r", encoding="utf-8") as fh:
                info = json.load(fh)
        except (OSError, ValueError):
            continue
        try:
            with urllib.request.urlopen(
                    "http://127.0.0.1:%d/ping" % info["port"], timeout=2) as r:
                pong = json.loads(r.read().decode("utf-8"))
        except Exception:
            continue
        if pong.get("pid") not in (None, info.get("pid")):
            continue        # another instance now owns that port
        info["mtime"] = os.path.getmtime(path)
        out.append(info)
    out.sort(key=lambda i: i["mtime"], reverse=True)
    return out


def call(request, timeout=None):
    live = instances()
    if not live:
        raise SystemExit("no SolidWorks with the add-in listener is running "
                         "(python swlab.py start)")
    inst = live[0]
    data = json.dumps(request).encode("utf-8")
    req = urllib.request.Request(
        "http://127.0.0.1:%d/" % inst["port"], data=data, method="POST",
        headers={"X-CADLink-Token": inst["token"],
                 "X-SWTB-Token": inst["token"],   # add-in built before the rename
                 "Content-Type": "application/json"})
    if timeout is None:
        timeout = float(request.get("timeout_s", 600)) + 30
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def start(wait_s=180):
    if instances():
        print("SolidWorks is already listening")
        return 0
    if not os.path.isfile(SLDWORKS):
        raise SystemExit("SolidWorks not found at " + SLDWORKS)
    subprocess.Popen([SLDWORKS], close_fds=True)
    deadline = time.time() + wait_s
    while time.time() < deadline:
        time.sleep(3)
        live = instances()
        if live:
            print("listening on port %d (pid %d)" % (live[0]["port"], live[0]["pid"]))
            return 0
    raise SystemExit("SolidWorks did not start listening within %d s" % wait_s)


def show(reply, brief_log=True):
    if brief_log and isinstance(reply, dict) and "log" in reply:
        log = reply.get("log") or []
        body = dict(reply)
        del body["log"]
        print(json.dumps(body, indent=1))
        if log:
            print("--- log (%d lines) ---" % len(log))
            for line in log:
                print(line)
    else:
        print(json.dumps(reply, indent=1))
    return 0 if (not isinstance(reply, dict) or reply.get("ok")) else 1


def main(argv):
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("op")
    p.add_argument("args", nargs="*")
    p.add_argument("--step", action="store_true")
    p.add_argument("--mesh", action="store_true")
    p.add_argument("--all", action="store_true")
    p.add_argument("--selected", action="store_true",
                   help="export only the selected components")
    p.add_argument("--json", action="store_true", help="print the whole reply as JSON")
    p.add_argument("--dir")
    p.add_argument("--out")
    p.add_argument("--quality", type=float)
    p.add_argument("--timeout", type=float)
    p.add_argument("--update", action="store_true",
                   help="send: bring the Blender scene up to date, as Refresh Model does")
    p.add_argument("--rig-mode", choices=("KEEP", "APPEND", "REGENERATE"),
                   help="send --update: what to do with the rig")
    p.add_argument("--lines", type=int, default=50)
    p.add_argument("--width", type=int, default=1280)
    p.add_argument("--height", type=int, default=800)
    a = p.parse_args(argv)

    if a.op == "start":
        return start()
    if a.op == "raw":
        return show(call(json.loads(a.args[0])), brief_log=not a.json)

    req = {"op": a.op}
    if a.timeout:
        req["timeout_s"] = a.timeout
    if a.op == "open":
        req["path"] = os.path.abspath(a.args[0])
    elif a.op == "activate":
        req["title"] = a.args[0]
    elif a.op == "export":
        req.update(step=a.step, mesh=a.mesh)
        if a.selected:
            req["only_selected"] = True
        if a.dir:
            req["dir"] = os.path.abspath(a.dir)
        if a.quality is not None:
            req["quality"] = a.quality
    elif a.op == "send":
        req["native"] = not a.step
        if a.dir:
            req["dir"] = os.path.abspath(a.dir)
        if a.update:
            req["update"] = True
        if a.rig_mode:
            req["rig_mode"] = a.rig_mode
    elif a.op in ("suppress", "unsuppress"):
        req["mate"] = a.args[0]
    elif a.op == "dimension":
        req["name"] = a.args[0]
        if len(a.args) > 1:
            req["value"] = float(a.args[1])
    elif a.op == "screenshot":
        req.update(width=a.width, height=a.height)
        if a.args:
            req["view"] = a.args[0]
        if a.out:
            req["out"] = os.path.abspath(a.out)
    elif a.op == "log":
        req["lines"] = a.lines
    elif a.op == "tess_uv":
        if a.args:
            req["face"] = float(a.args[0])
        req["limit"] = a.lines
    elif a.op == "refresh":
        pass
    elif a.op == "move":
        req["component"] = a.args[0]
        for key, value in zip(("x", "y", "z", "angle"), a.args[1:]):
            req[key] = float(value)
    elif a.op == "progress_demo":
        if a.args:
            req["steps"] = float(a.args[0])
        if len(a.args) > 1:
            req["hold_ms"] = float(a.args[1])
    elif a.op == "select":
        if a.args:
            req["components"] = list(a.args)
        req["append"] = a.all
    elif a.op == "appearances":
        req["max_faces"] = a.lines
        if a.args:
            req["face"] = int(a.args[0])
    elif a.op == "apply_appearance":
        req["path"] = os.path.abspath(a.args[0])
        req["target"] = a.args[1] if len(a.args) > 1 else "document"
        if len(a.args) > 2:
            req["width"] = req["height"] = float(a.args[2])
        if len(a.args) > 3:
            req["mapping_type"] = int(a.args[3])
        if len(a.args) > 4:
            req["rotation"] = float(a.args[4])
    elif a.op == "close":
        req["all"] = a.all
    try:
        reply = call(req)
    except urllib.error.URLError as exc:
        raise SystemExit("request failed: %s" % exc)
    if a.op == "mates" and not a.json:
        comps = reply.get("components") or []
        for c in comps:
            print("component %-6s %-36s %s%s" % (
                c["id"], c["path"], "FIXED " if c.get("fixed") else "",
                "suppressed " if c.get("suppressed") else ""))
        for m in reply.get("mates") or []:
            ents = ", ".join("%s@%s" % (e.get("type"), e.get("component")) for e in m["entities"])
            extra = ""
            lo, hi, cur = m.get("min"), m.get("max"), m.get("current")
            if cur is not None or (lo is not None and hi is not None and lo != hi):
                extra += " range=[%s,%s,%s]" % (lo, hi, cur)
            print("mate %-24s %-22s%s%s | %s" % (
                m["name"], m["type"], " suppressed" if m.get("suppressed") else "", extra, ents))
        return 0 if reply.get("ok") else 1
    return show(reply, brief_log=not a.json)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
