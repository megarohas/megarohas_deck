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


VPN = ("#6ee7b7", "#10b981")
STEAM = ("#93c5fd", "#3b82f6")
XBOX = ("#86efac", "#16a34a")

PAD = ("M84 96H172C198 96 214 118 218 148C222 178 208 192 193 187C182 183 174 170 163 162H93C82 170 74 183 63 187"
       "C48 192 34 178 38 148C42 118 58 96 84 96Z")


def gamepad(g, s, w):
    return (f'<path d="{PAD}" fill="{s}" stroke="{g}" stroke-width="{w}" stroke-linejoin="round"/>'
            f'<path d="M86 122V146M74 134H98" stroke="{g}" stroke-width="{w * 0.8:g}" stroke-linecap="round"/>'
            f'<circle cx="166" cy="124" r="{w * 0.62:g}" fill="{g}"/><circle cx="182" cy="142" r="{w * 0.62:g}" fill="{g}"/>')


def vpn(g, s, w, on):
    shield = "M128 38L196 64V120C196 164 166 196 128 214C90 196 60 164 60 120V64Z"
    path = "M98 126L120 148L160 104" if on else "M106 104L150 148M150 104L106 148"
    mark = (f'<path d="{path}" fill="none" stroke="{g}" stroke-width="{w}" stroke-linecap="round" '
            f'stroke-linejoin="round"/>')
    return f'<path d="{shield}" fill="{s}" stroke="{g}" stroke-width="{w}" stroke-linejoin="round"/>{mark}'


def bigpicture(g, s, w):
    return (f'<rect x="28" y="40" width="200" height="140" rx="14" fill="{s}" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M72 180L62 204M184 180L194 204" stroke="{g}" stroke-width="{w}" stroke-linecap="round"/>'
            f'<g transform="translate(128,112) scale(.62) translate(-128,-142)">{gamepad(g, "none", w * 1.45)}</g>')


def xbox_mode(g, w):
    corners = "M34 72V44H62M194 44H222V72M222 184V212H194M62 212H34V184"
    return (f'<path d="{corners}" fill="none" stroke="{g}" stroke-width="{w}" stroke-linecap="round" stroke-linejoin="round"/>'
            f'<g transform="translate(128,128) scale(.78) translate(-128,-142)">{gamepad(g, "none", w * 1.25)}</g>')


DESKTOP = ("#c4b5fd", "#8b5cf6")
COFFEE = ("#fcd34d", "#f59e0b")


def coffee(g, s, w, steam):
    cup = (f'<path d="M58 112H170V148C170 178 146 200 114 200C82 200 58 178 58 148Z" fill="{s}" stroke="{g}" '
           f'stroke-width="{w}" stroke-linejoin="round"/>'
           f'<path d="M170 126H182C196 126 204 136 204 148C204 160 196 170 182 170H166" fill="none" stroke="{g}" '
           f'stroke-width="{w}" stroke-linecap="round"/>')
    if steam:
        cup += (f'<path d="M90 50C80 62 100 72 90 88M124 44C114 56 134 66 124 82M158 50C148 62 168 72 158 88" '
                f'fill="none" stroke="{g}" stroke-width="{w * 0.8:g}" stroke-linecap="round"/>')
    return cup


def sofa(g, w):
    return (f'<path d="M66 150V122C66 110 74 102 86 102H170C182 102 190 110 190 122V150" fill="none" stroke="{g}" '
            f'stroke-width="{w}" stroke-linejoin="round"/>'
            f'<path d="M48 150C48 140 55 134 63 134C71 134 78 140 78 150V162H178V150C178 140 185 134 193 134C201 134 208 140 '
            f'208 150V192H48Z" fill="none" stroke="{g}" stroke-width="{w}" stroke-linejoin="round"/>'
            f'<path d="M62 192V208M194 192V208" stroke="{g}" stroke-width="{w}" stroke-linecap="round"/>')


def couch(g, w, badge):
    """Sofa with the target mode drawn small above it."""
    return (f'<g transform="translate(128,64) scale(.42) translate(-128,-128)">{badge}</g>'
            f'<g transform="translate(0,20)">{sofa(g, w)}</g>')


def desktop(g, w):
    return (f'<rect x="36" y="44" width="184" height="128" rx="14" fill="none" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M108 212H148M128 172V212" stroke="{g}" stroke-width="{w}" stroke-linecap="round"/>'
            f'<path d="M128 78V134M104 112L128 136L152 112" fill="none" stroke="{g}" stroke-width="{w}" '
            f'stroke-linecap="round" stroke-linejoin="round"/>')


OFFICE = ("#7dd3fc", "#818cf8")


def office_chair(g, w):
    return (f'<rect x="88" y="34" width="80" height="84" rx="24" fill="none" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M62 112V138M194 112V138" stroke="{g}" stroke-width="{w}" stroke-linecap="round"/>'
            f'<rect x="68" y="130" width="120" height="26" rx="13" fill="none" stroke="{g}" stroke-width="{w}"/>'
            f'<path d="M128 156V186M128 186L86 202M128 186L170 202M128 186V206" stroke="{g}" stroke-width="{w}" '
            f'stroke-linecap="round" stroke-linejoin="round"/>'
            f'<circle cx="84" cy="210" r="{w * 0.7:g}" fill="{g}"/><circle cx="172" cy="210" r="{w * 0.7:g}" fill="{g}"/>'
            f'<circle cx="128" cy="216" r="{w * 0.7:g}" fill="{g}"/>')


def mode_keys():
    """name -> svg body for the VPN / Big Picture / Xbox mode keys."""
    black = '<rect width="256" height="256" fill="#000"/>'
    dark = f'<rect width="256" height="256" rx="44" fill="{OFF_TILE}"/>'
    return {
        "vpn_off": black + dark + f'<g opacity="0.75">{vpn(VPN[0], "none", 13, False)}</g>',
        "vpn_on": black + tile(VPN) + vpn(DARK_GLYPH, LIT_SCREEN, 13, True),
        "bigpicture_off": black + dark + f'<g opacity="0.75">{bigpicture(STEAM[0], "none", 13)}</g>',
        "bigpicture_on": black + tile(STEAM) + bigpicture(DARK_GLYPH, LIT_SCREEN, 13),
        "xbox_mode_off": black + dark + f'<g opacity="0.75">{xbox_mode(XBOX[0], 13)}</g>',
        "xbox_mode_on": black + tile(XBOX) + xbox_mode(DARK_GLYPH, 13),
        "office": black + dark + f'<g opacity="0.9">{office_chair(OFFICE[0], 13)}</g>',
        "couch_bigpicture": black + dark + f'<g opacity="0.9">{couch(STEAM[0], 13, bigpicture(STEAM[0], "none", 22))}</g>',
        "couch_xbox": black + dark + f'<g opacity="0.9">{couch(XBOX[0], 13, xbox_mode(XBOX[0], 22))}</g>',
        "desktop": black + dark + f'<g opacity="0.9">{desktop(DESKTOP[0], 13)}</g>',
        "awake_off": black + dark + f'<g opacity="0.75" transform="translate(0,-28)">{coffee(COFFEE[0], "none", 13, False)}</g>',
        "awake_on": black + tile(COFFEE) + f'<g transform="translate(0,6)">{coffee(DARK_GLYPH, LIT_SCREEN, 13, True)}</g>',
    }


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
    for name, body in mode_keys().items():
        render(body, 256, OUT / f"{name}.png")
    for name, body in (("vpn", vpn("#fff", "none", 18, True)), ("bigpicture", bigpicture("#fff", "none", 18)),
                       ("xbox_mode", xbox_mode("#fff", 18)), ("couch", sofa("#fff", 18)),
                       ("desktop", desktop("#fff", 18)), ("awake", coffee("#fff", "none", 18, True)),
                       ("office", office_chair("#fff", 18))):
        for size, suffix in ((20, ""), (40, "@2x")):
            render(body, size, OUT / f"list_{name}{suffix}.png")
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
