"""Exports every assembly in test-assemblies and reports what each rigs to.

One SolidWorks session, one pass: open, export the manifest, close. The
table it prints is the joint shape and the warnings of each assembly, and
the differences against the manifests already on disk, which is what shows
a regression after a change to the readers or the classifier.

The lab listener must be running with lab operations on:

    python tools\\swlab.py start
    python tools\\Sweep-Corpus.py [--filter hinge] [--keep]

--keep leaves each document open, which is faster but uses more memory.
Nothing here saves a document.
"""
import argparse
import json
import os
import sys
import time
import urllib.error

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from swlab import call                                       # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
CORPUS = os.path.join(REPO, "test-assemblies")


def assemblies(pattern):
    found = []
    for root, _dirs, files in os.walk(CORPUS):
        for name in sorted(files):
            if not name.lower().endswith(".sldasm"):
                continue
            path = os.path.join(root, name)
            if pattern and pattern.lower() not in path.lower():
                continue
            found.append(path)
    return sorted(found)


def on_disk(path):
    """The joint shape of the manifest beside the assembly, or None."""
    manifest = os.path.splitext(path)[0] + ".rig.json"
    if not os.path.isfile(manifest):
        return None
    try:
        with open(manifest, encoding="utf-8") as fh:
            m = json.load(fh)
    except (OSError, ValueError):
        return None
    shape = {}
    for joint in m.get("joints", []):
        shape[joint["type"]] = shape.get(joint["type"], 0) + 1
    return shape


def shape_text(shape):
    if not shape:
        return "none"
    return " ".join("%s=%d" % (k, shape[k]) for k in sorted(shape))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--filter", help="only assemblies whose path holds this")
    ap.add_argument("--keep", action="store_true",
                    help="leave the documents open")
    args = ap.parse_args()

    paths = assemblies(args.filter)
    if not paths:
        sys.exit("no assemblies found")
    print("%d assembly(s)\n" % len(paths))

    rows, changed, failed = [], [], []
    for path in paths:
        name = os.path.relpath(path, CORPUS).replace("\\", "/")
        before = on_disk(path)
        started = time.time()
        try:
            opened = call({"op": "open", "path": path})
            if not opened.get("ok"):
                failed.append((name, opened.get("error")))
                continue
            reply = call({"op": "export", "step": False, "mesh": False})
        except (urllib.error.URLError, OSError) as exc:
            failed.append((name, str(exc)))
            continue
        finally:
            if not args.keep:
                try:
                    call({"op": "close", "all": True})
                except (urllib.error.URLError, OSError):
                    pass
        seconds = time.time() - started
        if not reply.get("ok"):
            failed.append((name, reply.get("error")))
            continue
        after = {k: int(v) for k, v in (reply.get("joints") or {}).items()}
        warnings = reply.get("warnings", 0)
        rows.append((name, after, warnings, seconds))
        mark = " "
        if before is not None and before != after:
            mark = "*"
            changed.append((name, before, after))
        print("%s %-34s %-44s %2d warning(s)  %4.1f s"
              % (mark, name, shape_text(after), warnings, seconds))

    print("\n%d exported, %d failed, %d changed shape"
          % (len(rows), len(failed), len(changed)))
    for name, before, after in changed:
        print("  %s\n      was %s\n      now %s"
              % (name, shape_text(before), shape_text(after)))
    for name, error in failed:
        print("  FAILED %s: %s" % (name, error))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
