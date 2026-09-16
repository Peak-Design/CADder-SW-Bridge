"""Compares a texture projection in Blender against SolidWorks' own picture.

A texture mapping is right when the pattern lands in the same place on the
part in both applications. This takes the SolidWorks screenshot and the
Blender render of one part in the same view, finds the ball or the shaft
in each by its silhouette, and samples the pattern over that shape in
coordinates of the shape itself, so the two images do not have to line up
pixel for pixel.

The pattern is read as a sign: each sample is compared with the average of
its neighbourhood, which removes the shading and the highlights SolidWorks
draws and Blender does not. Agreement is the share of samples that read
the same way in both.

    python Check-Texture-Parity.py <solidworks.bmp> <blender.png>
        [--shape ball|column] [--samples 40]

It prints the agreement and writes nothing.
"""
import argparse
import sys

import numpy as np
from PIL import Image


def grey(path):
    return np.asarray(Image.open(path).convert("L"), dtype=float)


def silhouette(img):
    """True where the part is, taken from the corner colour."""
    back = np.median([img[2, 2], img[2, -3], img[-3, 2], img[-3, -3]])
    return np.abs(img - back) > 25


def ball_circle(mask):
    """The centre and radius of the round part, from the widest row.

    The stud is narrower than the ball everywhere, so the widest row is
    the ball's equator, and the ball's lowest row is its bottom.
    """
    widths = mask.sum(axis=1)
    equator = int(np.argmax(widths))
    columns = np.where(mask[equator])[0]
    radius = (columns[-1] - columns[0]) / 2.0
    centre_x = (columns[-1] + columns[0]) / 2.0
    return centre_x, float(equator), radius


def column_box(mask):
    """The shaft: the rows above the ball, and their middle."""
    widths = mask.sum(axis=1)
    equator = int(np.argmax(widths))
    rows = [y for y in range(equator) if widths[y] > 4]
    if not rows:
        return None
    top, bottom = rows[0], rows[-1]
    bottom = min(bottom, int(top + (equator - top) * 0.75))
    mid = []
    for y in (top, bottom):
        cols = np.where(mask[y])[0]
        mid.append((cols[0], cols[-1]))
    return top, bottom, mid


def local_sign(img, x, y, reach):
    """Darker (True) or lighter (False) than the neighbourhood."""
    x0, x1 = int(max(0, x - reach)), int(min(img.shape[1], x + reach + 1))
    y0, y1 = int(max(0, y - reach)), int(min(img.shape[0], y + reach + 1))
    patch = img[y0:y1, x0:x1]
    if patch.size == 0:
        return None
    return img[int(y), int(x)] < patch.mean()


def ball_samples(count):
    """Points on the visible half of a ball, as fractions of its radius.

    The rim is left out: a sample there reads whichever side of an edge
    the pixel fell on, in either application.
    """
    out = []
    for i in range(count):
        for j in range(count):
            u = -0.82 + 1.64 * i / (count - 1.0)
            v = -0.82 + 1.64 * j / (count - 1.0)
            if u * u + v * v <= 0.68:
                out.append((u, v))
    return out


def compare_ball(a, b, samples):
    ca = ball_circle(silhouette(a))
    cb = ball_circle(silhouette(b))
    print("solidworks ball: centre (%.0f, %.0f) radius %.0f" % ca)
    print("blender ball:    centre (%.0f, %.0f) radius %.0f" % cb)
    agree = total = 0
    for u, v in ball_samples(samples):
        sa = local_sign(a, ca[0] + u * ca[2], ca[1] + v * ca[2], ca[2] * 0.09)
        sb = local_sign(b, cb[0] + u * cb[2], cb[1] + v * cb[2], cb[2] * 0.09)
        if sa is None or sb is None:
            continue
        total += 1
        agree += 1 if sa == sb else 0
    return agree, total


def compare_column(a, b, samples):
    ba, bb = column_box(silhouette(a)), column_box(silhouette(b))
    if ba is None or bb is None:
        return 0, 0
    agree = total = 0
    for i in range(samples):
        for j in range(samples):
            fy = i / (samples - 1.0)
            fx = 0.15 + 0.7 * j / (samples - 1.0)
            pa = column_point(ba, fx, fy)
            pb = column_point(bb, fx, fy)
            sa = local_sign(a, pa[0], pa[1], 6)
            sb = local_sign(b, pb[0], pb[1], 6)
            if sa is None or sb is None:
                continue
            total += 1
            agree += 1 if sa == sb else 0
    return agree, total


def column_point(box, fx, fy):
    top, bottom, (wide_top, wide_bottom) = box
    y = top + (bottom - top) * fy
    left = wide_top[0] + (wide_bottom[0] - wide_top[0]) * fy
    right = wide_top[1] + (wide_bottom[1] - wide_top[1]) * fy
    return left + (right - left) * fx, y


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("solidworks")
    ap.add_argument("blender")
    ap.add_argument("--shape", default="ball", choices=("ball", "column"))
    ap.add_argument("--samples", type=int, default=40)
    args = ap.parse_args()

    a, b = grey(args.solidworks), grey(args.blender)
    if args.shape == "ball":
        agree, total = compare_ball(a, b, args.samples)
    else:
        agree, total = compare_column(a, b, args.samples)
    if total == 0:
        sys.exit("no samples landed on the part in both images")
    share = 100.0 * agree / total
    print("%s: %d of %d samples agree (%.1f%%)"
          % (args.shape, agree, total, share))
    return 0 if share >= 95.0 else 1


sys.exit(main())
