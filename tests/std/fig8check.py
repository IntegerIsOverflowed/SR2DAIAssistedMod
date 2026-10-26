#!/usr/bin/env python3
"""Headless check of a closed outline (default: the smooth figure-8) against OUR OWN engine stroke.

Reference is our engine's stroke (VectorShape with Stroke at 2 x band width), not MuPDF:
  OUTSIDE ring (band w) = engine stroke(2w) AND NOT engine fill
  INSIDE  ring (band w) = engine stroke(2w) AND engine fill
Ours = the outline fill from VectorOffset.Outline (pos 1 outside, pos 2 inside), rendered by the same harness.

Usage:
  python3 tests/std/fig8check.py [--no-build] [--width 5.8]                 (figure-8 from outline_test_shapes.svg)
  python3 tests/std/fig8check.py --file uploads/tangled.svg --span 37.913 --label tangled [--width 1]
    --file   SVG with the path (default: demo/assets_builtin/outline_test_shapes.svg)
    --id     id of the path (default: the figure-8; with --file and no --id, the first path)
    --span   size of the shape's larger bounding-box side in mm (scales the path; default: 0.01 mm per unit)
    --label  output folder name under the harness cache
Outputs: /home/user/.cache/stdcheck/<label>/ (renders). Prints excess / missing pixels and the largest difference
regions (centre in mm) so spikes can be located.
"""
import json
import os
import re
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import outlinecmp as oc   # noqa: E402  (parse_subpaths, fit_transform, bbox)

ROOT = oc.ROOT
DLL = "/home/user/.cache/stdcheck/bin/Release/net10.0/stdcheck.dll"
W = H = 900


def arg(name, default=None):
    if name in sys.argv:
        return sys.argv[sys.argv.index(name) + 1]
    return default


def read_d(path, sid):
    text = open(path, encoding="utf-8").read()
    if sid:
        m = re.search(r'id="%s"[^>]*\sd="([^"]*)"' % re.escape(sid), text)
    else:
        m = re.search(r'\sd="([^"]*)"', text)
    if not m:
        raise SystemExit("path %s not found in %s" % (sid or "(first)", path))
    return m.group(1)


def main():
    band = float(arg("--width", "5.8"))
    path = arg("--file", oc.SVG_SOURCE)
    sid = arg("--id", None if arg("--file") else "CENTER_x0020_eight_x0020_smooth")
    label = arg("--label", "fig8")
    cache = "/home/user/.cache/stdcheck/" + label
    d = read_d(path, sid)
    span = arg("--span")
    if span:
        oc.MM_PER_UNIT = 1.0
        raw = oc.parse_subpaths(d)
        x0, y0, x1, y1 = oc.bbox(raw)
        oc.MM_PER_UNIT = float(span) / max(x1 - x0, y1 - y0)
    subs = oc.parse_subpaths(d)
    tr = oc.fit_transform(subs)
    ops = [x for o, _ in subs for x in o]
    print("shape: %s, %d subpaths, bbox mm %s, band %g mm" % (sid or "(first path)", len(subs),
          [round(v, 2) for v in oc.bbox(subs)], band))
    os.makedirs(cache, exist_ok=True)
    base = {"w": W, "h": H, "ops": ops, "transform": tr, "dash": [], "dashOffset": 0.0,
            "evenOdd": False, "miter": 4.0, "join": 0, "cap": 0, "width": 0}
    cases = [
        dict(base, name="fill", fill=True, stroke=False),
        dict(base, name="engine_stroke_2w", fill=False, stroke=True, width=2 * band),
        dict(base, name="ours_out", fill=True, stroke=False, outline={"pos": 1, "w": band, "cap": 0}),
        dict(base, name="ours_in", fill=True, stroke=False, outline={"pos": 2, "w": band, "cap": 0}),
    ]
    json.dump(cases, open(os.path.join(cache, "cases.json"), "w"))
    if "--no-build" not in sys.argv:
        subprocess.run(["dotnet", "build", os.path.join(ROOT, "tests", "cs", "stdcheck"), "-c", "Release",
                        "--nologo", "-v", "q"], check=True)
    so = os.path.join(ROOT, "tests", "build", "libSR2D64.so")
    shutil.copy(so, os.path.join(os.path.dirname(DLL), "SR2D64.so"))
    subprocess.run(["dotnet", DLL, os.path.join(cache, "cases.json"), cache], check=True)

    def ink(name):
        return np.array(Image.open(os.path.join(cache, name + ".png")).convert("L")) < 128

    fill = ink("fill")
    stroke = ink("engine_stroke_2w")
    s, _, _, _, ox, oy = tr

    def report(label_, ours, ref):
        excess = ours & ~ref
        missing = ref & ~ours
        print("%s: ref %d px, ours %d px, excess %d, missing %d (%.2f%% of ref)"
              % (label_, ref.sum(), ours.sum(), excess.sum(), missing.sum(),
                 100.0 * (excess.sum() + missing.sum()) / max(1, ref.sum())))
        for kind, m in (("excess", excess), ("missing", missing)):
            lab, n = ndimage.label(m)
            if n == 0:
                continue
            objs = ndimage.find_objects(lab)
            sizes = ndimage.sum(m, lab, range(1, n + 1))
            for i in np.argsort(-sizes)[:8]:
                if sizes[i] < 3:
                    continue
                sl = objs[i]
                cy, cx = ndimage.center_of_mass(m, lab, i + 1)
                print("  %-7s %5d px  bbox px x%d-%d y%d-%d  centre mm (%.2f, %.2f)"
                      % (kind, sizes[i], sl[1].start, sl[1].stop, sl[0].start, sl[0].stop,
                         (cx - ox) / s, (cy - oy) / s))

    report("OUTSIDE ring", ink("ours_out"), stroke & ~fill)
    report("INSIDE ring", ink("ours_in"), stroke & fill)


if __name__ == "__main__":
    main()
