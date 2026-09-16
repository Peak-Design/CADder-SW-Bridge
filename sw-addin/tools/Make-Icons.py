"""Builds the ribbon icon strips from the 128 px masters.

Masters: icons/*.png at the repository root, one per command plus logo.png
for the command group, 128 x 128 each. Output: src/Peak.SwToBlender/icons/
SwToBlender_<size>.png, one strip per size with the command icons side by
side in the order AddIn.cs registers them, and SwToBlenderMain_<size>.png
for the group. SolidWorks asks for 20, 32, 40, 64, 96 and 128 px.

Needs Pillow.

    python tools\\Make-Icons.py
"""
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
MASTERS = os.path.join(REPO, "icons")
ICONS = os.path.normpath(os.path.join(HERE, "..", "src", "Peak.SwToBlender", "icons"))
SIZES = (20, 32, 40, 64, 96, 128)
MASTER_PX = 128

# The order of AddCommandItem2 calls in AddIn.cs. The image index each
# command passes is its position here.
COMMANDS = (
    "send_direct",       # Send to Blender
    "options",           # Export Options
    "refresh",           # Refresh Poses
    "step+",             # Export STEP+ (advanced)
    "export_rig",        # Export Rig (advanced)
)
GROUP = "logo"


def load(name):
    path = os.path.join(MASTERS, name + ".png")
    im = Image.open(path).convert("RGBA")
    if im.size != (MASTER_PX, MASTER_PX):
        sys.exit("%s is %dx%d, expected %dx%d" % (path, im.width, im.height, MASTER_PX, MASTER_PX))
    return im


def scaled(im, size):
    return im if size == MASTER_PX else im.resize((size, size), Image.LANCZOS)


def main():
    masters = {name: load(name) for name in COMMANDS + (GROUP,)}
    for size in SIZES:
        strip = Image.new("RGBA", (size * len(COMMANDS), size), (0, 0, 0, 0))
        for i, name in enumerate(COMMANDS):
            strip.paste(scaled(masters[name], size), (i * size, 0))
        strip.save(os.path.join(ICONS, "SwToBlender_%d.png" % size))
        scaled(masters[GROUP], size).save(os.path.join(ICONS, "SwToBlenderMain_%d.png" % size))
        print("%3d px: strip %dx%d, group %dx%d" % (size, strip.width, strip.height, size, size))


if __name__ == "__main__":
    main()
