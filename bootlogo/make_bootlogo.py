"""Boot logo variants for the D6 (one 800x480 screen under 15 keys, 5x3 cells of 160 px).

Everything that matters sits in the middle of a cell: only the key windows are visible on the
device. Writes <variant>.png (800x480, to upload) and preview.png (all variants as seen through
the keys, approximately) next to this script. Renders with headless Edge, like make_icons.py.

Usage: python make_bootlogo.py
"""
import colorsys
import os
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
EDGE = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe"
W, H, CELL = 800, 480, 160
WINDOW, RADIUS = 124, 20  # approximate visible part of a key


def center(col, row):
    return CELL * col + CELL // 2, CELL * row + CELL // 2


def hsl(h, s, l):
    r, g, b = colorsys.hls_to_rgb((h % 360) / 360, l, s)
    return f"#{round(r * 255):02x}{round(g * 255):02x}{round(b * 255):02x}"


def palette():
    """15 gradient tiles in a hue sweep, 'M' monogram on the dark centre key."""
    defs, body = [], ['<rect width="800" height="480" fill="#0b0b0f"/>']
    for row in range(3):
        for col in range(5):
            x, y = CELL * col, CELL * row
            if (col, row) == (2, 1):
                body.append(f'<rect x="{x}" y="{y}" width="{CELL}" height="{CELL}" fill="#141419"/>')
                continue
            hue = 190 + (col + row) * 34
            gid = f"g{col}{row}"
            defs.append(f'<linearGradient id="{gid}" x1="0" y1="0" x2="1" y2="1">'
                        f'<stop offset="0" stop-color="{hsl(hue, .85, .74)}"/>'
                        f'<stop offset="1" stop-color="{hsl(hue + 28, .80, .60)}"/></linearGradient>')
            body.append(f'<rect x="{x}" y="{y}" width="{CELL}" height="{CELL}" fill="url(#{gid})"/>')
    defs.append('<linearGradient id="m" x1="0" y1="0" x2="1" y2="1">'
                f'<stop offset="0" stop-color="{hsl(200, .9, .75)}"/><stop offset=".5" stop-color="{hsl(290, .85, .72)}"/>'
                f'<stop offset="1" stop-color="{hsl(20, .9, .68)}"/></linearGradient>')
    cx, cy = center(2, 1)
    body.append(f'<text x="{cx}" y="{cy + 38}" text-anchor="middle" font-family="Bahnschrift" font-weight="700" '
                f'font-size="112" fill="url(#m)">M</text>')
    return f'<defs>{"".join(defs)}</defs>{"".join(body)}'


def rink():
    """Top view of a hockey rink laid onto the key grid."""
    red, blue = "#d0353b", "#1f5fbf"
    b = ['<rect width="800" height="480" fill="#eef4f8"/>']
    # centre red line, blue lines, centre faceoff circle and spot
    b.append(f'<rect x="395" y="0" width="10" height="480" fill="{red}"/>')
    for x in (240, 560):
        b.append(f'<rect x="{x - 8}" y="0" width="16" height="480" fill="{blue}"/>')
    b.append(f'<circle cx="400" cy="240" r="56" fill="none" stroke="{blue}" stroke-width="5"/>')
    b.append(f'<circle cx="400" cy="240" r="9" fill="{blue}"/>')
    # neutral zone dots
    for x in (290, 510):
        for y in (80, 400):
            b.append(f'<circle cx="{x}" cy="{y}" r="9" fill="{red}"/>')
    # end zones: goal line, crease, net, faceoff circles
    for side in (1, -1):
        gx = 40 if side == 1 else 760
        b.append(f'<rect x="{gx - 2}" y="0" width="4" height="480" fill="{red}"/>')
        b.append(f'<path d="M{gx} 196 A44 44 0 0 {1 if side == 1 else 0} {gx} 284 Z" fill="#9cc9f2" stroke="{red}" stroke-width="3"/>')
        b.append(f'<rect x="{gx - 22 if side == 1 else gx}" y="224" width="22" height="32" fill="none" stroke="{red}" stroke-width="4"/>')
        fx = 96 if side == 1 else 704
        for fy in (80, 400):
            b.append(f'<circle cx="{fx}" cy="{fy}" r="50" fill="none" stroke="{red}" stroke-width="4"/>')
            b.append(f'<circle cx="{fx}" cy="{fy}" r="9" fill="{red}"/>')
            for dx in (-14, 14):
                b.append(f'<path d="M{fx + dx} {fy - 50}v-12M{fx + dx} {fy + 50}v12" stroke="{red}" stroke-width="4"/>')
    return "".join(b)


def gold():
    """Black and gold: MEG / ARO / HAS one letter per key, honeycomb on the side columns."""
    b = ['<defs><linearGradient id="au" x1="0" y1="0" x2="0" y2="1">'
         '<stop offset="0" stop-color="#f6dc96"/><stop offset=".55" stop-color="#d4a94e"/>'
         '<stop offset="1" stop-color="#9c7330"/></linearGradient>'
         '<pattern id="hex" width="42" height="72.7" patternUnits="userSpaceOnUse">'
         '<path d="M21 0L42 12.1V36.4L21 48.5L0 36.4V12.1Z M21 48.5V72.7" fill="none" stroke="#c9a24b" stroke-width="2"/>'
         '</pattern></defs>',
         '<rect width="800" height="480" fill="#0a0a0c"/>',
         f'<rect width="{CELL}" height="480" fill="url(#hex)" opacity=".45"/>',
         f'<rect x="{W - CELL}" width="{CELL}" height="480" fill="url(#hex)" opacity=".45"/>']
    for i, ch in enumerate("MEGAROHAS"):
        cx, cy = center(1 + i % 3, i // 3)
        b.append(f'<rect x="{cx - 56}" y="{cy - 56}" width="112" height="112" rx="16" fill="none" stroke="#c9a24b" '
                 f'stroke-opacity=".35" stroke-width="2"/>')
        b.append(f'<text x="{cx}" y="{cy + 34}" text-anchor="middle" font-family="Bahnschrift" font-weight="300" '
                 f'font-size="98" fill="url(#au)">{ch}</text>')
    return "".join(b)


VARIANTS = {"palette": ("1 — Палитра", palette), "rink": ("2 — Хоккейная площадка", rink), "gold": ("3 — Золото", gold)}


def device(body, uid):
    """The logo as seen through the 15 key windows of the deck."""
    wins = "".join(f'<rect x="{CELL * c + (CELL - WINDOW) // 2}" y="{CELL * r + (CELL - WINDOW) // 2}" width="{WINDOW}" '
                   f'height="{WINDOW}" rx="{RADIUS}"/>' for r in range(3) for c in range(5))
    return (f'<rect x="-40" y="-40" width="880" height="560" rx="46" fill="#121216" stroke="#2c2c33" stroke-width="3"/>'
            f'<clipPath id="k{uid}">{wins}</clipPath><g clip-path="url(#k{uid})">{body}</g>'
            f'<g fill="none" stroke="#3a3a44" stroke-width="2">{wins}</g>')


def render(svg, width, height, out_png):
    html = (f'<!doctype html><html><body style="margin:0;background:#1e1e22">'
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" style="display:block">{svg}</svg>'
            f'</body></html>')
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "logo.html"
        page.write_text(html, encoding="utf-8")
        subprocess.run([str(EDGE), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                        f"--user-data-dir={Path(tmp) / 'profile'}", f"--window-size={width},{height}",
                        f"--screenshot={out_png}", page.as_uri()], check=True, capture_output=True, timeout=60)


def main():
    for name, (_, make) in VARIANTS.items():
        render(make(), W, H, HERE / f"{name}.png")
    rows = []
    for i, (name, (title, make)) in enumerate(VARIANTS.items()):
        y = 60 + i * 640
        rows.append(f'<text x="60" y="{y - 14}" font-family="Bahnschrift" font-size="34" fill="#e8e8ee">{title}</text>'
                    f'<g transform="translate(100,{y + 50})">{device(make(), i)}</g>')
    render("".join(rows), 1000, 60 + 3 * 640, HERE / "preview.png")
    print(f"boot logos -> {HERE}")


if __name__ == "__main__":
    main()
