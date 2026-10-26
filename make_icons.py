"""Draws the plugin's key images as SVG and renders them to PNG with headless Edge.

Each action has two key states: 0 = mode is off (dark tile, tinted outline),
1 = mode is active (bright gradient tile, lit screen). Also renders the small
white glyphs for the action list and the category.

Usage: python make_icons.py  (writes into com.megarohas.deck.sdPlugin/images)
"""
import os
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "com.megarohas.deck.sdPlugin" / "images"
EDGE = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe"

DARK_GLYPH = "#25252b"
LIT_SCREEN = "rgba(255,255,255,0.6)"
OFF_TILE = "#1b1b20"

# action -> (gradient start, gradient end)
COLORS = {
    "internal": ("#7dd3fc", "#818cf8"),
    "external": ("#fdba74", "#fb7185"),
    "extend": ("#c4b5fd", "#f0abfc"),
    "clone": ("#86efac", "#5eead4"),
}


def monitor(g, s, w):
    return (f'<rect x="44" y="50" width="168" height="114" rx="14" fill="{s}" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M128 164V196M88 202H168" stroke="{g}" stroke-width="{w}" stroke-linecap="round" fill="none"/>')


def tv(g, s, w):
    return (f'<rect x="28" y="58" width="200" height="122" rx="12" fill="{s}" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M72 180L62 200M184 180L194 200" stroke="{g}" stroke-width="{w}" stroke-linecap="round" fill="none"/>'
            f'<path d="M114 96L150 119L114 142Z" fill="{g}" stroke="{g}" stroke-width="{w * 0.8:g}" stroke-linejoin="round"/>')


def extend(g, s, w):
    parts = []
    for x in (24, 138):
        cx = x + 47
        parts.append(f'<rect x="{x}" y="50" width="94" height="66" rx="10" fill="{s}" stroke="{g}" stroke-width="{w}"/>')
        parts.append(f'<path d="M{cx} 116V136M{cx - 20} 142H{cx + 20}" stroke="{g}" stroke-width="{w}" stroke-linecap="round" fill="none"/>')
    parts.append(f'<path d="M58 188H198M74 172L58 188L74 204M182 172L198 188L182 204" stroke="{g}" stroke-width="{w}" '
                 f'stroke-linecap="round" stroke-linejoin="round" fill="none"/>')
    return "".join(parts)


def clone(g, s, w):
    # Back screen is masked where the front one covers it, so the front can stay see-through.
    return (f'<mask id="m"><rect width="256" height="256" fill="#fff"/><rect x="74" y="76" width="168" height="124" rx="22" fill="#000"/></mask>'
            f'<rect x="30" y="46" width="136" height="92" rx="12" fill="{s}" stroke="{g}" stroke-width="{w}" mask="url(#m)"/>'
            f'<rect x="90" y="92" width="136" height="92" rx="12" fill="{s}" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M158 184V202M128 208H188" stroke="{g}" stroke-width="{w}" stroke-linecap="round" fill="none"/>')


GLYPHS = {"internal": monitor, "external": tv, "extend": extend, "clone": clone}

AUDIO = ("#67e8f9", "#38bdf8")
MUTED = ("#fda4af", "#ef4444")
# Volume level bar; Audio.cs draws the fill over this track, keep the numbers in sync.
BAR = dict(x=58, y=198, width=140, height=16)


def speaker(g, s, w, extra):
    return (f'<path d="M44 104H74L116 70V186L74 152H44Z" fill="{s}" stroke="{g}" stroke-width="{w}" stroke-linejoin="round"/>'
            f'<path d="{extra}" stroke="{g}" stroke-width="{w}" stroke-linecap="round" stroke-linejoin="round" fill="none"/>')


WAVES = "M146 100Q164 128 146 156M170 80Q200 128 170 176"
CROSS = "M150 104L198 152M198 104L150 152"
PLUS = "M150 128H206M178 100V156"
MINUS = "M150 128H206"


def tile(colors):
    a, b = colors
    return (f'<defs><linearGradient id="t" x1="0" y1="0" x2="1" y2="1">'
            f'<stop offset="0" stop-color="{a}"/><stop offset="1" stop-color="{b}"/></linearGradient></defs>'
            f'<rect width="256" height="256" rx="44" fill="url(#t)"/>')


def led_ring(color, dot, radius=84):
    """Ring of 18 dots like the D6 light ring; Lights.cs draws the live version of the same picture."""
    import math
    return "".join(f'<circle cx="{128 + radius * math.cos(2 * math.pi * i / 18 - math.pi / 2):.1f}" '
                   f'cy="{128 + radius * math.sin(2 * math.pi * i / 18 - math.pi / 2):.1f}" r="{dot}" fill="{color}"/>'
                   for i in range(18))


def audio_keys():
    """name -> svg body for the audio key images."""
    black = '<rect width="256" height="256" fill="#000"/>'
    dark = f'<rect width="256" height="256" rx="44" fill="{OFF_TILE}"/>'
    track = (f'<rect x="{BAR["x"]}" y="{BAR["y"]}" width="{BAR["width"]}" height="{BAR["height"]}" '
             f'rx="{BAR["height"] // 2}" fill="#34343c"/>')
    accent = AUDIO[0]
    return {
        "mute_off": black + dark + f'<g opacity="0.85">{speaker(accent, "none", 13, WAVES)}</g>',
        "mute_on": black + tile(MUTED) + speaker(DARK_GLYPH, LIT_SCREEN, 13, CROSS),
        "volume_down": black + dark + f'<g transform="translate(0,-16)" opacity="0.85">{speaker(accent, "none", 13, MINUS)}</g>' + track,
        "volume_up": black + dark + f'<g transform="translate(0,-16)" opacity="0.85">{speaker(accent, "none", 13, PLUS)}</g>' + track,
    }


def key_svg(name, active):
    a, b = COLORS[name]
    if active:
        tile = (f'<defs><linearGradient id="t" x1="0" y1="0" x2="1" y2="1">'
                f'<stop offset="0" stop-color="{a}"/><stop offset="1" stop-color="{b}"/></linearGradient></defs>'
                f'<rect width="256" height="256" rx="44" fill="url(#t)"/>')
        glyph = GLYPHS[name](DARK_GLYPH, LIT_SCREEN, 13)
    else:
        tile = f'<rect width="256" height="256" rx="44" fill="{OFF_TILE}"/>'
        glyph = f'<g opacity="0.75">{GLYPHS[name](a, "none", 13)}</g>'
    return f'<rect width="256" height="256" fill="#000"/>{tile}{glyph}'


def render(svg_body, size, out_png):
    html = (f'<!doctype html><html><body style="margin:0;background:transparent">'
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 256 256" style="display:block">'
            f'{svg_body}</svg></body></html>')
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "icon.html"
        page.write_text(html, encoding="utf-8")
        subprocess.run([str(EDGE), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                        f"--user-data-dir={Path(tmp) / 'profile'}", "--default-background-color=00000000",
                        f"--window-size={size},{size}", f"--screenshot={out_png}", page.as_uri()],
                       check=True, capture_output=True, timeout=60)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name in GLYPHS:
        render(key_svg(name, False), 256, OUT / f"{name}_off.png")
        render(key_svg(name, True), 256, OUT / f"{name}_on.png")
        for size, suffix in ((20, ""), (40, "@2x")):
            render(GLYPHS[name]("#fff", "none", 18), size, OUT / f"list_{name}{suffix}.png")
    for name, body in audio_keys().items():
        render(body, 256, OUT / f"{name}.png")
    for name, extra in (("mute", WAVES), ("volume_down", MINUS), ("volume_up", PLUS)):
        for size, suffix in ((20, ""), (40, "@2x")):
            render(speaker("#fff", "none", 18, extra), size, OUT / f"list_{name}{suffix}.png")
    glow = ('<defs><radialGradient id="glow"><stop offset="0" stop-color="#00aaff" stop-opacity=".43"/>'
            '<stop offset="1" stop-color="#00aaff" stop-opacity="0"/></radialGradient></defs>')
    render(f'<rect width="256" height="256" fill="#000"/><rect width="256" height="256" rx="44" fill="{OFF_TILE}"/>'
           f'{glow}<circle cx="128" cy="128" r="80" fill="url(#glow)"/>{led_ring("#00aaff", 10)}', 256, OUT / "lights.png")
    for size, suffix in ((20, ""), (40, "@2x")):
        render(led_ring("#fff", 16, 96), size, OUT / f"list_lights{suffix}.png")
    render(f'<rect width="256" height="256" fill="#000"/><rect width="256" height="256" rx="44" fill="{OFF_TILE}"/>'
           f'<circle cx="128" cy="128" r="70" fill="none" stroke="#ef4444" stroke-width="13" opacity=".85"/>'
           f'<circle cx="128" cy="128" r="40" fill="#ef4444"/>', 256, OUT / "record_off.png")
    for size, suffix in ((20, ""), (40, "@2x")):
        render('<circle cx="128" cy="128" r="96" fill="none" stroke="#fff" stroke-width="22"/>'
               '<circle cx="128" cy="128" r="54" fill="#fff"/>', size, OUT / f"list_record{suffix}.png")
    for size, suffix in ((28, ""), (56, "@2x")):
        render(extend("#fff", "none", 18), size, OUT / f"category{suffix}.png")
    render(key_svg("extend", True), 144, OUT / "plugin.png")
    print(f"icons -> {OUT}")


if __name__ == "__main__":
    main()
