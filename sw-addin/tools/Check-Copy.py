"""Checks the copy in this repository for punctuation that should not be in
it.

Readers take an em dash as a sign that a machine wrote the text, so it is
out of every line a person can read: comments, documentation, UI strings,
commit messages and release notes. The replacement is the punctuation the
sentence needs, usually a colon, a comma, brackets or two sentences, and
never a hyphen or an en dash in the same role.

    python sw-addin\\tools\\Check-Copy.py [root]

Exits 1 and lists every line it finds. Nothing is changed.
"""
import io
import os
import sys

EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
SKIP_DIRS = {".git", "bin", "obj", "node_modules", "__pycache__", "packages"}
SUFFIXES = (".cs", ".md", ".py", ".ps1", ".iss", ".json", ".yml", ".yaml", ".txt")


def offences(root):
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in names:
            if not name.endswith(SUFFIXES):
                continue
            path = os.path.join(folder, name)
            try:
                text = io.open(path, encoding="utf-8").read()
            except (OSError, UnicodeDecodeError):
                continue
            if EM_DASH not in text and (" " + EN_DASH + " ") not in text:
                continue
            for number, line in enumerate(text.split("\n"), 1):
                what = None
                if EM_DASH in line:
                    what = "em dash"
                elif " " + EN_DASH + " " in line:
                    what = "en dash as punctuation"
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
              "comma, brackets, or two sentences." % len(found))
        return 1
    print("copy check: no em dashes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
