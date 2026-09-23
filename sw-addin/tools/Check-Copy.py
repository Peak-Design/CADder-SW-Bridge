"""Checks the copy in this repository for punctuation that should not be in
it.

Readers take an em dash as a sign that a machine wrote the text, so it is
out of every line a person can read: comments, documentation, UI strings,
commit messages and release notes. The replacement is the punctuation the
sentence needs, usually a colon, a comma, brackets or two sentences, and
never a hyphen or an en dash in the same role.

It also looks for client names. The list is in .claude/client-names.txt,
one regular expression per line. That folder is never committed, so the
list is not published by the check that enforces it. Without the file,
only the punctuation is checked.

Only the files git tracks are read, because those are the files that are
published. Outside a git checkout, every file is read.

    python sw-addin\\tools\\Check-Copy.py [root]

Exits 1 and lists every line it finds. Nothing is changed.
"""
import io
import os
import re
import subprocess
import sys

EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
SKIP_DIRS = {".git", "bin", "obj", "node_modules", "__pycache__", "packages",
             ".claude", "test-assemblies", "assets"}
# Project files and scripts carry comments that are published like any
# other text. CopyStyleTests.cs reads the same list.
SUFFIXES = (".cs", ".md", ".py", ".ps1", ".iss", ".json", ".yml", ".yaml", ".txt",
            ".props", ".targets", ".csproj", ".bat", ".cmd", ".toml",
            ".cpp", ".h", ".xml")


def files(root):
    """The files to read: what git tracks, or everything outside git."""
    try:
        out = subprocess.run(["git", "-C", root, "ls-files", "-z"],
                             capture_output=True, check=True).stdout
        for rel in out.decode("utf-8").split("\0"):
            if rel and rel.endswith(SUFFIXES):
                yield os.path.join(root, rel)
        return
    except (OSError, subprocess.CalledProcessError):
        pass
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in names:
            if name.endswith(SUFFIXES):
                yield os.path.join(folder, name)


def client_names(root):
    """The patterns in .claude/client-names.txt, or none."""
    path = os.path.join(root, ".claude", "client-names.txt")
    try:
        lines = io.open(path, encoding="utf-8").read().split("\n")
    except OSError:
        return []
    return [re.compile(line.strip(), re.IGNORECASE) for line in lines
            if line.strip() and not line.strip().startswith("#")]


def offences(root):
    names = client_names(root)
    for path in files(root):
        try:
            text = io.open(path, encoding="utf-8").read()
        except (OSError, UnicodeDecodeError):
            continue
        for number, line in enumerate(text.split("\n"), 1):
            what = None
            if EM_DASH in line:
                what = "em dash"
            elif " " + EN_DASH + " " in line:
                what = "en dash as punctuation"
            elif any(n.search(line) for n in names):
                what = "client name"
            if what:
                yield os.path.relpath(path, root), number, what, line.strip()


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    found = list(offences(root))
    for path, number, what, line in found:
        print("%s:%d: %s: %s" % (path, number, what, line[:110]))
    if found:
        print("\n%d line(s). Use the punctuation the sentence needs: a colon, a "
              "comma, brackets, or two sentences. Replace a client name with "
              "a description of the model." % len(found))
        return 1
    print("copy check: no em dashes%s" % (
        ", no client names" if client_names(root) else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
