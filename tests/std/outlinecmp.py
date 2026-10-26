#!/usr/bin/env python3
"""Compare our own outline technique (VectorOffset.Outline, as the demo's Make() calls it) with the standards.

Reference: the same shape drawn as a STROKE by two independent renderers (MuPDF from PDF, cairo from SVG), which is what
the stroke should look like:
  CENTRE  (pos 0): stroke of width w, centred;
  OUTSIDE (pos 1): stroke of width 2w clipped to the outside of the closed subpaths (ring of width w);
  INSIDE  (pos 2): stroke of width 2w clipped to the inside of the closed subpaths (ring of width w).
Open subpaths (the OBD bar) are stroke-only, centred, width w, with the cap and join of the case.

Ours: the outline fill from the engine harness (tests/cs/stdcheck, "outline" mode).
Our technique has no cap or join selector, so ours is one picture per (position, width); each reference combination
(cap x join) is compared with that same picture.

Shape: CENTER_OBD from demo/assets_builtin/outline_test_shapes.svg, in mm (0.01 mm per unit), as Shapes() loads it.
Usage: python3 tests/std/outlinecmp.py [--bbox] [--no-build]
Outputs: /home/user/.cache/stdcheck/outline/ (renders) and /home/user/stdcheck_report/outline/ (report.md, sheets).
"""
import json
import math
import os
import re
import subprocess
import sys

import cairosvg
import pymupdf as fitz
from PIL import Image, ImageChops, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CACHE = "/home/user/.cache/stdcheck/outline"
REPORT = "/home/user/stdcheck_report/outline"
SVG_SOURCE = os.path.join(ROOT, "demo", "assets_builtin", "outline_test_shapes.svg")
MM_PER_UNIT = 0.01
W = H = 900                 # canvas, px
CLIP_MM = 2000.0            # half-size of the clip rectangle for the OUTSIDE reference
POSITIONS = [0, 1, 2]
POS_NAME = {0: "CENTRE", 1: "OUTSIDE", 2: "INSIDE"}
WIDTHS = [0.5, 1.0, 2.0, 4.0]    # band width in mm (the demo uses 2 / 1 / 1)
CAPS = [(0, "butt"), (1, "round"), (2, "square")]
JOINS = [(0, "miter"), (1, "round"), (2, "bevel")]
OUR_JOIN = {0: 0, 1: 2, 2: 1}   # PDF/SVG join codes (0 miter, 1 round, 2 bevel) -> VectorOffset join (0 miter, 1 bevel, 2 round)
PASS_SHARE = 0.01
THRESHOLD = 64


# ----------------------------------------------------------------------------- the shape
def read_obd_d():
    text = open(SVG_SOURCE, encoding="utf-8").read()
    m = re.search(r'id="CENTER_OBD"[^>]*\sd="([^"]*)"', text)
    if not m:
        raise SystemExit("CENTER_OBD not found")
    return m.group(1)


def parse_subpaths(d):
    """Absolute M / L / C / Z ops per subpath, from the relative m, c, l, z used by the file. Returns [(ops, closed)]."""
    toks = re.findall(r"[MmLlCcZz]|-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?", d)
    subs, ops, closed = [], None, False
    i, cmd = 0, None
    cx = cy = sx = sy = 0.0

    def num():
        nonlocal i
        v = float(toks[i]); i += 1
        return v

    while i < len(toks):
        t = toks[i]
        if t.isalpha():
            cmd = t; i += 1
            if cmd in "Zz":
                if ops is not None:
                    ops.append(["Z"]); closed = True
                cx, cy = sx, sy
                continue
        if cmd in "Mm":
            x, y = num(), num()
            if cmd == "m": x, y = cx + x, cy + y
            if ops is not None:
                subs.append((ops, closed))
            ops, closed = [["M", x, y]], False
            cx, cy = sx, sy = x, y
            cmd = "l" if cmd == "m" else "L"    # further pairs are implicit line-tos
        elif cmd in "Ll":
            x, y = num(), num()
            if cmd == "l": x, y = cx + x, cy + y
            ops.append(["L", x, y]); cx, cy = x, y
        elif cmd in "Cc":
            p = [num() for _ in range(6)]
            if cmd == "c":
                p = [cx + p[0], cy + p[1], cx + p[2], cy + p[3], cx + p[4], cy + p[5]]
            ops.append(["C"] + p); cx, cy = p[4], p[5]
        else:
            raise SystemExit("unsupported command %r" % cmd)
    if ops is not None:
        subs.append((ops, closed))
    out = []
    for ops, cl in subs:
        out.append(([[o[0]] + [v * MM_PER_UNIT for v in o[1:]] for o in ops], cl))
    return out


def bbox(subs):
    xs, ys = [], []
    for ops, _ in subs:
        for o in ops:
            if o[0] in "ML":
                xs.append(o[1]); ys.append(o[2])
            elif o[0] == "C":
                xs += [o[1], o[3], o[5]]; ys += [o[2], o[4], o[6]]
    return min(xs), min(ys), max(xs), max(ys)


def fit_transform(subs):
    x0, y0, x1, y1 = bbox(subs)
    margin = 60.0
    s = min((W - 2 * margin) / (x1 - x0), (H - 2 * margin) / (y1 - y0))
    ox = margin - s * x0 + (W - 2 * margin - s * (x1 - x0)) / 2
    oy = margin - s * y0 + (H - 2 * margin - s * (y1 - y0)) / 2
    return [s, 0.0, 0.0, s, ox, oy]


# ----------------------------------------------------------------------------- reference renders (stroke + clip)
def path_d(ops):
    parts = []
    for o in ops:
        if o[0] == "M": parts.append("M%.9g %.9g" % (o[1], o[2]))
        elif o[0] == "L": parts.append("L%.9g %.9g" % (o[1], o[2]))
        elif o[0] == "C": parts.append("C%.9g %.9g %.9g %.9g %.9g %.9g" % tuple(o[1:7]))
        elif o[0] == "Z": parts.append("Z")
    return " ".join(parts)


def rect_ops():
    b = CLIP_MM
    return [["M", -b, -b], ["L", b, -b], ["L", b, b], ["L", -b, b], ["Z"]]


def reference_pdf(subs, pos, w, cap, join, tr):
    closed = [o for o, c in subs if c]
    opened = [o for o, c in subs if not c]
    content = ["1 0 0 -1 0 %d cm" % H, "%.9g %.9g %.9g %.9g %.9g %.9g cm" % tuple(tr), "0 g 0 G", "0 J 0 j 4 M"]
    # line width is in user space (mm), the transform scales it to px
    if pos == 0:
        content.append("%.9g w %d J %d j 4 M" % (w, cap, join))
        for o, _ in subs:
            content.append(_pdf_path(o) + " S")
    else:
        if closed:
            content.append("q")
            clip_ops = (rect_ops() if pos == 1 else []) + [x for o in closed for x in o]
            content.append(_pdf_path(clip_ops) + (" W* n" if pos == 1 else " W n"))
            content.append("%.9g w 0 J 0 j 4 M" % (2 * w))
            for o in closed:
                content.append(_pdf_path(o) + " S")
            content.append("Q")
        content.append("%.9g w %d J %d j 4 M" % (w, cap, join))
        for o in opened:
            content.append(_pdf_path(o) + " S")
    return "\n".join(content) + "\n"


def _pdf_path(ops):
    out = []
    for o in ops:
        if o[0] == "M": out.append("%.9g %.9g m" % (o[1], o[2]))
        elif o[0] == "L": out.append("%.9g %.9g l" % (o[1], o[2]))
        elif o[0] == "C": out.append("%.9g %.9g %.9g %.9g %.9g %.9g c" % tuple(o[1:7]))
        elif o[0] == "Z": out.append("h")
    return "\n".join(out)


def make_pdf(content):
    body = content.encode("latin-1")
    objs = [b"<< /Type /Catalog /Pages 2 0 R >>",
            b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Contents 4 0 R /Resources << >> >>" % (W, H)).encode(),
            b"<< /Length %d >>\nstream\n" % len(body) + body + b"endstream"]
    out = bytearray(b"%PDF-1.4\n")
    offs = []
    for i, b in enumerate(objs, 1):
        offs.append(len(out)); out += b"%d 0 obj\n" % i + b + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for o in offs: out += b"%010d 00000 n \n" % o
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return bytes(out)


def _segments(ops):
    """(start, segments) of one closed subpath: segments are ('L', end) or ('C', c1, c2, end) from the previous point."""
    start = (ops[0][1], ops[0][2]); cur = start; segs = []
    for o in ops[1:]:
        if o[0] == "L":
            segs.append(("L", (o[1], o[2]))); cur = (o[1], o[2])
        elif o[0] == "C":
            segs.append(("C", (o[1], o[2]), (o[3], o[4]), (o[5], o[6]))); cur = (o[5], o[6])
    return start, segs


def _signed_area(ops):
    pts = [(o[1], o[2]) for o in ops if o[0] in "ML"] + [(o[5], o[6]) for o in ops if o[0] == "C"]
    return 0.5 * sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts)))


def _reversed(ops):
    """The same closed subpath traversed the other way (same geometry, opposite winding)."""
    start, segs = _segments(ops)
    ends = [start] + [sg[-1] for sg in segs]
    out = [["M", ends[-1][0], ends[-1][1]]]
    for k in range(len(segs) - 1, -1, -1):
        sg = segs[k]; p0 = ends[k]
        if sg[0] == "L":
            out.append(["L", p0[0], p0[1]])
        else:
            out.append(["C", sg[2][0], sg[2][1], sg[1][0], sg[1][1], p0[0], p0[1]])
    out.append(["Z"])
    return out


def reference_svg(subs, pos, w, cap, join, tr):
    closed = [o for o, c in subs if c]
    opened = [o for o, c in subs if not c]
    capn = ["butt", "round", "square"][cap]; joinn = ["miter", "round", "bevel"][join]
    g = ['<g transform="matrix(%s)">' % " ".join("%.9g" % v for v in tr)]
    if pos == 0:
        for o, _ in subs:
            g.append('<path d="%s" fill="none" stroke="#000" stroke-width="%.9g" stroke-linecap="%s" stroke-linejoin="%s" stroke-miterlimit="4"/>'
                     % (path_d(o), w, capn, joinn))
    else:
        if closed:
            # cairosvg ignores clip-rule, so the rule is made by winding: for OUTSIDE the rectangle plus every closed subpath
            # traversed opposite to the rectangle (winding 0 inside the shape, 1 outside it)
            if pos == 1:
                rect_sign = _signed_area(rect_ops())
                parts = [path_d(rect_ops())]
                for o in closed:
                    parts.append(path_d(_reversed(o) if _signed_area(o) * rect_sign > 0 else o))
                clip_d = " ".join(parts)
            else:
                clip_d = " ".join(path_d(o) for o in closed)
            g.append('<defs><clipPath id="cl" clipPathUnits="userSpaceOnUse"><path d="%s"/></clipPath></defs>' % clip_d)
            for o in closed:
                g.append('<path d="%s" fill="none" stroke="#000" stroke-width="%.9g" clip-path="url(#cl)"/>' % (path_d(o), 2 * w))
        for o in opened:
            g.append('<path d="%s" fill="none" stroke="#000" stroke-width="%.9g" stroke-linecap="%s" stroke-linejoin="%s" stroke-miterlimit="4"/>'
                     % (path_d(o), w, capn, joinn))
    g.append("</g>")
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d">'
            '<rect width="100%%" height="100%%" fill="#fff"/>%s</svg>\n') % (W, H, W, H, "".join(g))


def render_mupdf(pdf_bytes, png):
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    doc[0].get_pixmap(matrix=fitz.Matrix(1, 1), alpha=False).save(png)
    doc.close()


def render_cairo(svg, png):
    cairosvg.svg2png(bytestring=svg.encode("utf-8"), write_to=png, output_width=W, output_height=H)


def load_gray(p):
    return Image.open(p).convert("L")


def compare(a, b):
    d = ImageChops.difference(a, b)
    px = list(d.getdata())
    n = len(px)
    return sum(px) / (255.0 * n), sum(1 for v in px if v >= THRESHOLD) / n


# ----------------------------------------------------------------------------- main
def main():
    subs = parse_subpaths(read_obd_d())
    if "--bbox" in sys.argv:
        print("subpaths:", [(len(o), c) for o, c in subs], "bbox mm:", [round(v, 2) for v in bbox(subs)])
        return
    tr = fit_transform(subs)
    os.makedirs(CACHE, exist_ok=True); os.makedirs(REPORT, exist_ok=True)
    # 1. our outline for each position and width (the engine harness)
    cases = []
    for pos in POSITIONS:
        for w in WIDTHS:
            for cap, capn in CAPS:   # the bar ends follow the cap (butt / round / square); closed letters have no ends
                for join, joinn in JOINS:   # our outline takes the corner setting too (T113); PDF join codes -> ours (1 round <-> 2)
                    cases.append({"name": "ours_%d_%g_%s_%s" % (pos, w, capn, joinn), "w": W, "h": H, "ops": [x for o, _ in subs for x in o],
                                  "fill": True, "stroke": False, "evenOdd": False,
                                  "width": 0, "cap": 0, "join": 0, "miter": 4.0, "dash": [], "dashOffset": 0.0,
                                  "transform": tr, "outline": {"pos": pos, "w": w, "cap": cap, "join": OUR_JOIN[join]}})
    json.dump(cases, open(os.path.join(CACHE, "cases.json"), "w"))
    if "--no-build" not in sys.argv:
        subprocess.run(["dotnet", "build", os.path.join(ROOT, "tests", "cs", "stdcheck"), "-c", "Release", "--nologo", "-v", "q"], check=True)
    dll = "/home/user/.cache/stdcheck/bin/Release/net10.0/stdcheck.dll"
    so = os.path.join(ROOT, "tests", "build", "libSR2D64.so")   # generated folder: rebuilt when missing (as stdcheck.py does)
    if not os.path.exists(so):
        os.makedirs(os.path.dirname(so), exist_ok=True)
        native = os.path.join(ROOT, "native")
        subprocess.run(["g++", "-O2", "-shared", "-fPIC", "-std=c++17", "-mavx2", "-mfma",
                        os.path.join(native, "sr2d.cpp"), os.path.join(native, "sr2d_kernels_sse2.cpp"),
                        os.path.join(native, "sr2d_kernels_avx2.cpp"), "-o", so], check=True)
    import shutil
    shutil.copy(so, os.path.join(os.path.dirname(dll), "SR2D64.so"))
    subprocess.run(["dotnet", dll, os.path.join(CACHE, "cases.json"), CACHE], check=True)
    results = []
    for pos in POSITIONS:
        for w in WIDTHS:
            for cap, capn in CAPS:
                for join, joinn in JOINS:
                    ours = load_gray(os.path.join(CACHE, "ours_%d_%g_%s_%s.png" % (pos, w, capn, joinn)))
                    key = "%s_w%g_%s_%s" % (POS_NAME[pos], w, capn, joinn)
                    pdf = make_pdf(reference_pdf(subs, pos, w, cap, join, tr))
                    svg = reference_svg(subs, pos, w, cap, join, tr)
                    mu = os.path.join(CACHE, key + "_mupdf.png"); ca = os.path.join(CACHE, key + "_cairo.png")
                    render_mupdf(pdf, mu); render_cairo(svg, ca)
                    mean_mu, strong_mu = compare(ours, load_gray(mu))
                    mean_ca, strong_ca = compare(ours, load_gray(ca))
                    results.append((POS_NAME[pos], w, capn, joinn, mean_mu, strong_mu, mean_ca, strong_ca))
    write_report(results)
    print("done: %d reference combinations; report %s" % (len(results), REPORT))


def write_report(results):
    lines = ["# Our outline vs the stroke reference (MuPDF and cairo)", "",
             "Per position and width, one row per cap x join combination. Strong = share of pixels that differ by 64 or more",
             "(of 255). A row passes when ours is within 1 % strong of both references.", "",
             "| position | width mm | cap | join | vs MuPDF mean | vs MuPDF strong | vs cairo mean | vs cairo strong | result |",
             "|---|---:|---|---|---:|---:|---:|---:|:--:|"]
    worst = {}
    for pos, w, capn, joinn, mm, sm, mc, sc in results:
        ok = max(sm, sc) <= PASS_SHARE
        lines.append("| %s | %g | %s | %s | %.3f | %.2f%% | %.3f | %.2f%% | %s |" % (pos, w, capn, joinn, mm, 100 * sm, mc, 100 * sc, "pass" if ok else "**FAIL**"))
        k = (pos, w)
        worst[k] = max(worst.get(k, 0), max(sm, sc))
    lines += ["", "## Worst strong share per position and width", "", "| position | width mm | worst strong |", "|---|---:|---:|"]
    for (pos, w), v in sorted(worst.items(), key=lambda kv: (kv[0][0], kv[0][1])):
        lines.append("| %s | %g | %.2f%% |" % (pos, w, 100 * v))
    lines += ["", "Ours follows the bar-end cap and the corner setting (join): each row compares our outline made with that cap and join.",
              "Windows-only Widen (GDI+) is not part of this run."]
    open(os.path.join(REPORT, "report.md"), "w", encoding="utf-8").write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
