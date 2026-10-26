#!/usr/bin/env python3
"""Standards check of the engine's stroke and fill.

One list of cases (paths, fill / stroke, width, cap, join, miter limit, dash, dash offset, fill rule) is written as
  - a PDF content stream   -> rendered by MuPDF (PyMuPDF)          : oracle A, the PDF specification's own rules
  - an SVG 2 / SVG 1.1 file -> rendered by cairo (CairoSVG)        : oracle B, the SVG painting rules
  - cases.json              -> rendered by the engine (tests/cs/stdcheck)  : the system under test

Each render is 200 x 200 px, white background, black ink, so the three can be compared pixel by pixel.
The comparison reports, per case: mean absolute difference (0-255 grey) and the share of pixels that differ by 64 or
more. Oracle A against oracle B is printed too: where the two standards' renderers disagree, the difference is the noise
floor and no engine can be held to a tighter number.

Usage:  python3 tests/std/stdcheck.py            (run from the repository root; builds the engine harness)
        python3 tests/std/stdcheck.py --no-build (reuse the harness already built)
Outputs: /home/user/.cache/stdcheck/  (renders, PDF / SVG inputs)  and  /home/user/stdcheck_report/  (report.md, sheet.png)
"""
import json
import math
import os
import shutil
import subprocess
import sys

import cairosvg
import fitz  # PyMuPDF (MuPDF)
from PIL import Image, ImageChops, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CACHE = "/home/user/.cache/stdcheck"
REPORT = "/home/user/stdcheck_report"
W = H = 200
THRESHOLD = 64          # a pixel differs "strongly" at this grey difference
PASS_SHARE = 0.01       # a case passes when at most 1 % of its pixels differ strongly from BOTH oracles


# ----------------------------------------------------------------------------- the cases
def circle(cx, cy, r):
    k = 0.5522847498 * r
    return [["M", cx + r, cy],
            ["C", cx + r, cy + k, cx + k, cy + r, cx, cy + r],
            ["C", cx - k, cy + r, cx - r, cy + k, cx - r, cy],
            ["C", cx - r, cy - k, cx - k, cy - r, cx, cy - r],
            ["C", cx + k, cy - r, cx + r, cy - k, cx + r, cy],
            ["Z"]]


def polyline(pts, closed=False):
    ops = [["M", pts[0][0], pts[0][1]]] + [["L", x, y] for x, y in pts[1:]]
    if closed:
        ops.append(["Z"])
    return ops


def pentagram():
    # five points, joined every second vertex: the centre pentagon has winding 2 (nonzero fills it, even-odd does not)
    pts = []
    for k in range(5):
        a = math.radians(-90 + 72 * k)
        pts.append((100 + 80 * math.cos(a), 100 + 80 * math.sin(a)))
    order = [0, 2, 4, 1, 3]
    return polyline([pts[i] for i in order], closed=True)


def case(name, ops, **kw):
    c = {"name": name, "w": W, "h": H, "ops": ops, "fill": False, "stroke": False, "evenOdd": False,
         "width": 16.0, "cap": 0, "join": 0, "miter": 4.0, "dash": [], "dashOffset": 0.0,
         "transform": [1, 0, 0, 1, 0, 0]}
    c.update(kw)
    return c


SPIKE = [(70, 180), (100, 20), (130, 180)]          # half-angle 10.6 deg: miter ratio 5.4, over a limit of 4
LINE = [(30, 100), (170, 100)]
def _spike(th):
    # apex at (100, 30), arms of length 120 at half-angle th: miter ratio = 1 / sin(th)
    dx, dy = 120 * math.sin(math.radians(th)), 120 * math.cos(math.radians(th))
    return [(100 - dx, 30 + dy), (100, 30), (100 + dx, 30 + dy)]
SPIKE_AT = _spike(math.degrees(math.asin(1 / 3.9)))   # ratio 3.9: under the limit 4, miter kept (not on the exact limit: that is a rounding question)
SPIKE_OVER = _spike(14.0)                            # ratio 4.13: over the limit, bevel

CASES = [
    # fill
    case("fill_nonzero_pentagram", pentagram(), fill=True, width=0),
    case("fill_evenodd_pentagram", pentagram(), fill=True, evenOdd=True, width=0),
    case("fill_circle", circle(100, 100, 70), fill=True, width=0),
    case("fill_thin_rect_fraction", [["M", 50.3, 40], ["L", 50.9, 40], ["L", 50.9, 160], ["L", 50.3, 160], ["Z"]],
         fill=True, width=0),
    # stroke: joins
    case("join_miter", polyline([(30, 150), (100, 40), (170, 150)]), stroke=True, join=0),
    case("join_round", polyline([(30, 150), (100, 40), (170, 150)]), stroke=True, join=1),
    case("join_bevel", polyline([(30, 150), (100, 40), (170, 150)]), stroke=True, join=2),
    case("miterlimit_4_falls_back_to_bevel", polyline(SPIKE), stroke=True, join=0, miter=4),
    case("miterlimit_10_stays_miter", polyline(SPIKE), stroke=True, join=0, miter=10),
    # stroke: caps
    case("cap_butt", polyline(LINE), stroke=True, width=24, cap=0),
    case("cap_round", polyline(LINE), stroke=True, width=24, cap=1),
    case("cap_square", polyline(LINE), stroke=True, width=24, cap=2),
    # stroke: closed, zero length
    case("closed_square_miter", polyline([(50, 50), (150, 50), (150, 150), (50, 150)], closed=True),
         stroke=True, width=20, join=0),
    case("zero_length_round_dot", [["M", 100, 100], ["L", 100, 100]], stroke=True, width=24, cap=1),
    case("zero_length_square_dot", [["M", 100, 100], ["L", 100, 100]], stroke=True, width=24, cap=2),
    case("zero_length_butt_nothing", [["M", 100, 100], ["L", 100, 100]], stroke=True, width=24, cap=0),
    # opt-in (compatibility flag SquareDotCap): the engine draws the square; the oracles draw nothing. Not a default case.
    case("zero_length_square_dot_flag", [["M", 100, 100], ["L", 100, 100]], stroke=True, width=24, cap=2, squareDot=True, optin=True),
    # stroke: dashes
    case("dash_butt", polyline(LINE), stroke=True, width=10, dash=[24, 12]),
    case("dash_offset_10", polyline(LINE), stroke=True, width=10, dash=[24, 12], dashOffset=10),
    case("dash_round_caps", polyline(LINE), stroke=True, width=10, dash=[24, 12], cap=1),
    case("dash_circle", circle(100, 100, 70), stroke=True, width=8, dash=[20, 8]),
    # stroke: curves, reversals, self-intersection, hairline
    case("curve_cubic", [["M", 20, 180], ["C", 60, 0, 140, 200, 180, 20]], stroke=True, width=12),
    case("cusp_reversal_round", polyline([(30, 100), (170, 100), (60, 100)]), stroke=True, width=16, join=1),
    case("cusp_reversal_miter", polyline([(30, 100), (170, 100), (60, 100)]), stroke=True, width=16, join=0),
    case("self_loop_cubic", [["M", 30, 150], ["C", 200, -50, -20, -50, 170, 150]], stroke=True, width=12),
    case("thin_line_0p5_at_fraction", polyline([(20, 100.25), (180, 100.25)]), stroke=True, width=0.5),
    # ---- round 2: more geometry (fills with holes, join thresholds, tiny segments, dash edge cases, tangents)
    case("fill_evenodd_square_hole", [["M", 40, 40], ["L", 160, 40], ["L", 160, 160], ["L", 40, 160], ["Z"],
                                      ["M", 80, 80], ["L", 120, 80], ["L", 120, 120], ["L", 80, 120], ["Z"]],
         fill=True, width=0, evenOdd=True),
    case("fill_nonzero_same_winding_no_hole", [["M", 40, 40], ["L", 160, 40], ["L", 160, 160], ["L", 40, 160], ["Z"],
                                               ["M", 80, 80], ["L", 120, 80], ["L", 120, 120], ["L", 80, 120], ["Z"]],
         fill=True, width=0),
    case("fill_nonzero_opposite_winding_hole", [["M", 40, 40], ["L", 160, 40], ["L", 160, 160], ["L", 40, 160], ["Z"],
                                                ["M", 80, 80], ["L", 80, 120], ["L", 120, 120], ["L", 120, 80], ["Z"]],
         fill=True, width=0),
    case("fill_subpixel_triangle", [["M", 100, 100], ["L", 100.6, 100], ["L", 100, 100.6], ["Z"]], fill=True, width=0),
    case("fill_edges_at_half_pixel", [["M", 40.5, 40.5], ["L", 160.5, 40.5], ["L", 160.5, 160.5], ["L", 40.5, 160.5], ["Z"]],
         fill=True, width=0),
    case("miter_at_limit_kept", polyline(SPIKE_AT), stroke=True, join=0, miter=4, width=10),
    case("miter_just_over_limit_bevel", polyline(SPIKE_OVER), stroke=True, join=0, miter=4, width=10),
    case("join_round_tiny_segment", polyline([(30, 150), (100, 100), (100.4, 100.4), (170, 150)]), stroke=True, join=1, width=12),
    case("join_miter_tiny_segment", polyline([(30, 150), (100, 100), (100.4, 100.4), (170, 150)]), stroke=True, join=0, width=12),
    case("stroke_closed_circle_miter", circle(100, 100, 60), stroke=True, width=10, join=0),
    case("stroke_two_subpaths", polyline([(30, 40), (170, 40), (170, 90)]) + polyline([(30, 120), (170, 160)]),
         stroke=True, width=10, cap=1, join=0),
    case("stroke_half_pixel_offset_1px", polyline([(30, 60.5), (170, 60.5), (170, 140.5)]), stroke=True, width=1),
    case("dash_closed_square_phase_wraps", polyline([(50, 50), (150, 50), (150, 150), (50, 150)], closed=True),
         stroke=True, width=8, dash=[30, 10], dashOffset=20),
    case("dash_on_curve", [["M", 20, 180], ["C", 60, 0, 140, 200, 180, 20]], stroke=True, width=8, dash=[18, 9]),
    case("dash_zero_gap_round_dots", polyline(LINE), stroke=True, width=8, dash=[0, 14], cap=1),
    case("dash_odd_count", polyline(LINE), stroke=True, width=8, dash=[20, 5, 3]),
    case("cap_square_degenerate_tangent", [["M", 30, 100], ["C", 30, 100, 100, 20, 170, 100]], stroke=True, width=20, cap=2),
    case("cap_round_degenerate_tangent", [["M", 30, 100], ["C", 30, 100, 100, 20, 170, 100]], stroke=True, width=20, cap=1),
    case("stroke_wide_60_square_cap", polyline([(40, 100), (160, 60)]), stroke=True, width=60, cap=2),
    # ---- round 3: non-uniform transforms (the stroke is drawn in user space, then transformed; the width is not a mean)
    case("xform_scale4x_vertical_line", polyline([(12, 20), (12, 180)]), stroke=True, width=2, transform=[4, 0, 0, 1, 0, 0]),
    case("xform_scale3y_horizontal_line", polyline([(20, 12), (180, 12)]), stroke=True, width=4, transform=[1, 0, 0, 3, 0, 0]),
    case("xform_shear_line", polyline([(20, 100), (180, 40)]), stroke=True, width=6, transform=[1, 0, 0.6, 1, 0, 0]),
    case("xform_uniform_scale2_line", polyline([(20, 20), (90, 180)]), stroke=True, width=3, transform=[2, 0, 0, 2, 0, 0]),
    # ---- round 4: widths below one pixel (true width) and the Hairline property (ISO 32000-1 8.4.3.2: width 0 = one device pixel)
    case("width_0p5_horizontal", polyline([(20, 100.25), (180, 100.25)]), stroke=True, width=0.5),
    case("width_0p25_horizontal", polyline([(20, 100.25), (180, 100.25)]), stroke=True, width=0.25),
    case("width_0p5_vertical", polyline([(100.25, 20), (100.25, 180)]), stroke=True, width=0.5),
    case("width_0p1_vertical", polyline([(100.25, 20), (100.25, 180)]), stroke=True, width=0.1),
    case("hairline_horizontal", polyline([(20, 100.25), (180, 100.25)]), stroke=True, hairline=True),
    case("hairline_vertical", polyline([(100.25, 20), (100.25, 180)]), stroke=True, hairline=True),
    case("hairline_curve", [["M", 20, 180], ["C", 60, 0, 140, 200, 180, 20]], stroke=True, hairline=True),
]


# ----------------------------------------------------------------------------- oracle A: PDF content + MuPDF
PDF_CAP = {0: 0, 1: 1, 2: 2}
PDF_JOIN = {0: 0, 1: 1, 2: 2}


def pdf_content(c):
    t = c["transform"]
    # PDF cm pre-multiplies: the LAST cm in the stream acts first on the path. The shape transform therefore comes after
    # the y-flip, so the path is transformed in the y-down (SVG-like) space and then flipped.
    out = ["1 0 0 -1 0 %d cm" % H, "%.9g %.9g %.9g %.9g %.9g %.9g cm" % tuple(t), "0 g 0 G", "%g w" % (0 if c.get("hairline") else c["width"]), "%d J" % c["cap"], "%d j" % c["join"],
           "%g M" % c["miter"]]
    dash = c["dash"]
    out.append("[%s] %g d" % (" ".join("%g" % v for v in dash), c["dashOffset"]))
    for op in c["ops"]:
        if op[0] == "M":
            out.append("%.9g %.9g m" % (op[1], op[2]))
        elif op[0] == "L":
            out.append("%.9g %.9g l" % (op[1], op[2]))
        elif op[0] == "C":
            out.append("%.9g %.9g %.9g %.9g %.9g %.9g c" % tuple(op[1:7]))
        elif op[0] == "Z":
            out.append("h")
    if c["fill"] and c["stroke"]:
        out.append("B*" if c["evenOdd"] else "B")
    elif c["fill"]:
        out.append("f*" if c["evenOdd"] else "f")
    else:
        out.append("S")
    return "\n".join(out) + "\n"


def make_pdf(c):
    content = pdf_content(c).encode("latin-1")
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Contents 4 0 R /Resources << >> >>" % (W, H)).encode(),
        b"<< /Length %d >>\nstream\n" % len(content) + content + b"endstream",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, body in enumerate(objs, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for off in offsets:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return bytes(out)


def svg_path(ops):
    parts = []
    for op in ops:
        if op[0] == "M":
            parts.append("M%.9g %.9g" % (op[1], op[2]))
        elif op[0] == "L":
            parts.append("L%.9g %.9g" % (op[1], op[2]))
        elif op[0] == "C":
            parts.append("C%.9g %.9g %.9g %.9g %.9g %.9g" % tuple(op[1:7]))
        elif op[0] == "Z":
            parts.append("Z")
    return " ".join(parts)


def make_svg(c):
    attrs = ['d="%s"' % svg_path(c["ops"]),
             'fill="%s"' % ("#000" if c["fill"] else "none"),
             'fill-rule="%s"' % ("evenodd" if c["evenOdd"] else "nonzero")]
    if c["stroke"]:
        attrs += ['stroke="#000"', 'stroke-width="%g"' % (1 if c.get("hairline") else c["width"]),
                  'stroke-linecap="%s"' % ["butt", "round", "square"][c["cap"]],
                  'stroke-linejoin="%s"' % ["miter", "round", "bevel"][c["join"]],
                  'stroke-miterlimit="%g"' % c["miter"]]
        if c.get("hairline"):
            attrs += ['vector-effect="non-scaling-stroke"']
        if c["dash"]:
            attrs += ['stroke-dasharray="%s"' % " ".join("%g" % v for v in c["dash"]),
                      'stroke-dashoffset="%g"' % c["dashOffset"]]
    else:
        attrs += ['stroke="none"']
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d">\n'
            '  <path %s/>\n</svg>\n') % (W, H, W, H, " ".join(attrs + ['transform="matrix(%s)"' % " ".join("%.9g" % v for v in c["transform"])]))


def render_mupdf(pdf_bytes, png_path):
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    pix = doc[0].get_pixmap(matrix=fitz.Matrix(1, 1), alpha=False)
    pix.save(png_path)
    doc.close()


def render_cairo(svg_text, png_path):
    cairosvg.svg2png(bytestring=svg_text.encode("utf-8"), write_to=png_path, output_width=W, output_height=H,
                     background_color="white")


# ----------------------------------------------------------------------------- comparison
def load_gray(path):
    im = Image.open(path).convert("L")
    if im.size != (W, H):
        raise SystemExit("%s is %s, expected %dx%d" % (path, im.size, W, H))
    return im


def compare(a, b):
    d = ImageChops.difference(a, b)
    px = list(d.getdata())
    n = len(px)
    mean = sum(px) / n
    strong = sum(1 for v in px if v >= THRESHOLD) / n
    return mean, strong, max(px)


def contact_sheet(rows, path):
    # columns: ours | MuPDF (PDF) | cairo (SVG) | difference ours vs MuPDF (white = same, dark = different)
    cols = ["engine", "MuPDF (PDF)", "cairo (SVG)", "diff engine-MuPDF"]
    label_h, name_w = 16, 230
    sheet = Image.new("RGB", (name_w + len(cols) * (W + 6), (label_h + H + 6) * len(rows) + label_h), "white")
    draw = ImageDraw.Draw(sheet)
    for j, cname in enumerate(cols):
        draw.text((name_w + j * (W + 6) + 4, 2), cname, fill="black")
    for i, (name, ims) in enumerate(rows):
        y = label_h + i * (label_h + H + 6)
        draw.text((4, y + 2), name, fill="black")
        for j, im in enumerate(ims):
            sheet.paste(im.convert("RGB"), (name_w + j * (W + 6), y))
    sheet.save(path)


def main():
    no_build = "--no-build" in sys.argv
    os.makedirs(CACHE, exist_ok=True)
    os.makedirs(REPORT, exist_ok=True)
    for sub in ("svg", "pdf", "engine", "mupdf", "cairo"):
        os.makedirs(os.path.join(CACHE, sub), exist_ok=True)

    with open(os.path.join(CACHE, "cases.json"), "w", encoding="utf-8") as f:
        json.dump(CASES, f, indent=1)

    harness_dir = os.path.join(ROOT, "tests", "cs", "stdcheck")
    dll = os.path.join(CACHE, "bin", "Release", "net10.0", "stdcheck.dll")
    if not no_build:
        subprocess.run(["dotnet", "build", harness_dir, "-c", "Release", "--nologo", "-v", "q"], check=True)
    so = os.path.join(ROOT, "tests", "build", "libSR2D64.so")
    if not os.path.exists(so):   # tests/build is a generated folder (excluded from snapshots): rebuild it
        os.makedirs(os.path.dirname(so), exist_ok=True)
        native = os.path.join(ROOT, "native")
        subprocess.run(["g++", "-O2", "-shared", "-fPIC", "-std=c++17", "-mavx2", "-mfma",
                        os.path.join(native, "sr2d.cpp"), os.path.join(native, "sr2d_kernels_sse2.cpp"),
                        os.path.join(native, "sr2d_kernels_avx2.cpp"), "-o", so], check=True)
    shutil.copy(so, os.path.join(os.path.dirname(dll), "SR2D64.so"))
    subprocess.run(["dotnet", dll, os.path.join(CACHE, "cases.json"), os.path.join(CACHE, "engine")], check=True)

    rows, stats, bad = [], [], []
    for c in CASES:
        name = c["name"]
        pdf_bytes = make_pdf(c)
        svg_text = make_svg(c)
        with open(os.path.join(CACHE, "pdf", name + ".pdf"), "wb") as f:
            f.write(pdf_bytes)
        with open(os.path.join(CACHE, "svg", name + ".svg"), "w", encoding="utf-8") as f:
            f.write(svg_text)
        mu = os.path.join(CACHE, "mupdf", name + ".png")
        ca = os.path.join(CACHE, "cairo", name + ".png")
        render_mupdf(pdf_bytes, mu)
        render_cairo(svg_text, ca)
        eng = load_gray(os.path.join(CACHE, "engine", name + ".png"))
        mui = load_gray(mu)
        cai = load_gray(ca)

        m_e, s_e_mu, x_e_mu = compare(eng, mui)
        m_e2, s_e_ca, x_e_ca = compare(eng, cai)
        m_o, s_o, x_o = compare(mui, cai)
        optin = bool(c.get("optin"))
        if optin:
            # opt-in: the flag must draw the square (the engine has ink there) while both oracles stay blank
            dark = lambda im: sum(1 for v in im.getdata() if v < 128)
            ok = dark(eng) >= 500 and dark(mui) <= 5 and dark(cai) <= 5
            name = name + " (opt-in)"
        else:
            ok = max(s_e_mu, s_e_ca) <= PASS_SHARE
        stats.append((name, m_e, s_e_mu, m_e2, s_e_ca, m_o, s_o, ok))
        if not ok:
            bad.append(name)
        diff = ImageChops.difference(eng, mui).point(lambda v: 255 - min(255, v * 4))
        rows.append((name, [eng, mui, cai, diff]))

    contact_sheet(rows, os.path.join(REPORT, "sheet.png"))
    write_report(stats, bad)
    print("\n".join("%-34s engine-MuPDF mean %6.2f strong %6.2f%% | engine-cairo mean %6.2f strong %6.2f%% | MuPDF-cairo strong %6.2f%% %s"
                    % (n, me, 100 * se1, me2, 100 * se2, 100 * so, "ok" if ok else "FAIL")
                    for n, me, se1, me2, se2, mo, so, ok in stats))
    dflt = [st for st in stats if "(opt-in)" not in st[0]]
    print("passed %d of %d default cases; opt-in cases checked separately; report: %s" % (sum(1 for st in dflt if st[7]), len(dflt), REPORT))


def write_report(stats, bad):
    lines = [
        "# Stroke and fill check against PDF and SVG renderers",
        "",
        "Generated by `tests/std/stdcheck.py`. Each case is drawn 200 x 200 px, black on white, with the same geometry in",
        "three renderers: the engine (`tests/cs/stdcheck`), MuPDF (PDF content stream, the PDF specification's rules) and",
        "cairo (SVG, via CairoSVG). Mean = mean absolute grey difference (0-255). Strong = share of pixels that differ by",
        "64 or more. A case passes when the engine is within 1 % strong pixels of both renderers.",
        "",
        "| case | engine vs MuPDF: mean | strong | engine vs cairo: mean | strong | MuPDF vs cairo: strong | result |",
        "|---|---:|---:|---:|---:|---:|:--:|",
    ]
    for n, me, se1, me2, se2, mo, so, ok in stats:
        lines.append("| %s | %.2f | %.2f%% | %.2f | %.2f%% | %.2f%% | %s |"
                     % (n, me, 100 * se1, me2, 100 * se2, 100 * so, "pass" if ok else "**FAIL**"))
    dflt = [st for st in stats if "(opt-in)" not in st[0]]
    lines += ["", "Passed %d of %d default cases. Opt-in cases (compatibility flags) are listed separately and checked for their own contract." % (sum(1 for st in dflt if st[7]), len(dflt)), ""]
    if bad:
        lines += ["Failing cases: " + ", ".join(bad), ""]
    lines += ["Contact sheet: `sheet.png` (columns: engine, MuPDF, cairo, difference engine vs MuPDF; dark = different).", ""]
    with open(os.path.join(REPORT, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))


if __name__ == "__main__":
    main()
