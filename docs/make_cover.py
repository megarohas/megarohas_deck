"""Cover image for the README / GitHub social preview (1280x640): title on the left, a D6 deck with the
plugin's real key images on the right. Renders with headless Edge, like make_icons.py.

Usage: python docs/make_cover.py  (writes docs/cover.png)
"""
import base64
import os
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
IMAGES = HERE.parent / "com.megarohas.deck.sdPlugin" / "images"
EDGE = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe"

# Key layout on the cover, top row first.
LAYOUT = [
    ["bigpicture_on", "xbox_mode_off", "record_off", "desktop", "awake_on"],
    ["couch_bigpicture", "couch_xbox", "office", "extend_off", "mute_off"],
    ["internal_on", "external_off", "mute_on", "volume_down", "volume_up"],
]
FEATURES = [
    "Win+P display switching with live state",
    "Volume keys with a level bar",
    "One-tap OBS recording with a timer",
    "Steam Big Picture & Xbox mode",
    "Couch / Office scenes in one press",
    "Stay awake, minimize all",
]


def data_uri(name):
    return "data:image/png;base64," + base64.b64encode((IMAGES / f"{name}.png").read_bytes()).decode()


def deck(x, y, key=96, gap=16):
    cols, rows = 5, 3
    w = cols * key + (cols - 1) * gap + 2 * 34
    h = rows * key + (rows - 1) * gap + 2 * 34
    parts = [
        # RGB ring glow + body
        f'<rect x="{x - 10}" y="{y - 10}" width="{w + 20}" height="{h + 20}" rx="46" fill="none" stroke="url(#ring)" '
        f'stroke-width="7" filter="url(#glow)"/>',
        f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="38" fill="#0e0e12" stroke="#26262e" stroke-width="2"/>',
    ]
    for r, row in enumerate(LAYOUT):
        for c, name in enumerate(row):
            kx, ky = x + 34 + c * (key + gap), y + 34 + r * (key + gap)
            # the key PNGs have black square corners for the LCD; round them off on the cover
            parts.append(f'<clipPath id="k{r}{c}"><rect x="{kx}" y="{ky}" width="{key}" height="{key}" rx="{key * 44 / 256}"/></clipPath>'
                         f'<image href="{data_uri(name)}" x="{kx}" y="{ky}" width="{key}" height="{key}" clip-path="url(#k{r}{c})"/>')
            if name in ("volume_down", "volume_up"):  # live level bar, as the plugin draws it
                parts.append(f'<rect x="{kx + key * 58 / 256}" y="{ky + key * 198 / 256}" width="{key * 140 / 256 * 0.62}" '
                             f'height="{key * 16 / 256}" rx="{key * 8 / 256}" fill="#67e8f9"/>')
    return "".join(parts)


def cover():
    feats = "".join(
        f'<circle cx="86" cy="{352 + i * 40}" r="5" fill="url(#accent)"/>'
        f'<text x="104" y="{359 + i * 40}" font-family="Segoe UI" font-size="23" fill="#c9c9d6">{f}</text>'
        for i, f in enumerate(FEATURES))
    return f'''
<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0b0b11"/><stop offset="1" stop-color="#171225"/></linearGradient>
  <radialGradient id="blob1"><stop offset="0" stop-color="#6366f1" stop-opacity=".45"/><stop offset="1" stop-color="#6366f1" stop-opacity="0"/></radialGradient>
  <radialGradient id="blob2"><stop offset="0" stop-color="#ec4899" stop-opacity=".32"/><stop offset="1" stop-color="#ec4899" stop-opacity="0"/></radialGradient>
  <radialGradient id="blob3"><stop offset="0" stop-color="#22d3ee" stop-opacity=".28"/><stop offset="1" stop-color="#22d3ee" stop-opacity="0"/></radialGradient>
  <linearGradient id="accent" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#7dd3fc"/><stop offset=".5" stop-color="#c084fc"/><stop offset="1" stop-color="#fb7185"/></linearGradient>
  <linearGradient id="ring" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0" stop-color="#22d3ee"/><stop offset=".33" stop-color="#a855f7"/><stop offset=".66" stop-color="#f43f5e"/><stop offset="1" stop-color="#facc15"/>
  </linearGradient>
  <filter id="glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="9" result="b"/>
    <feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
</defs>
<rect width="1280" height="640" fill="url(#bg)"/>
<circle cx="1010" cy="170" r="380" fill="url(#blob1)"/>
<circle cx="1180" cy="560" r="300" fill="url(#blob2)"/>
<circle cx="140" cy="80" r="260" fill="url(#blob3)"/>
<text x="76" y="150" font-family="Bahnschrift" font-weight="700" font-size="76" fill="#f4f4f8">megarohas</text>
<text x="76" y="228" font-family="Bahnschrift" font-weight="700" font-size="76" fill="url(#accent)">deck</text>
<text x="78" y="282" font-family="Segoe UI" font-size="25" fill="#9a9aae">StreamDock plugin for fifine AmpliGame D6</text>
<text x="78" y="314" font-family="Segoe UI" font-size="19" fill="#6f6f84">Mirabox StreamDock · Windows · keys that show the real state</text>
{feats}
{deck(628, 126)}
'''


def render(svg, width, height, out_png):
    html = (f'<!doctype html><html><body style="margin:0;background:#000">'
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" style="display:block">{svg}</svg>'
            f'</body></html>')
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "cover.html"
        page.write_text(html, encoding="utf-8")
        subprocess.run([str(EDGE), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                        f"--user-data-dir={Path(tmp) / 'profile'}", f"--window-size={width},{height}",
                        f"--screenshot={out_png}", page.as_uri()], check=True, capture_output=True, timeout=60)


if __name__ == "__main__":
    render(cover(), 1280, 640, HERE / "cover.png")
    print(f"cover -> {HERE / 'cover.png'}")
