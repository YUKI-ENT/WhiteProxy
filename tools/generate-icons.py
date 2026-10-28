"""Generate the original WhiteProxy vector-style icon assets. Requires Pillow.

Run from any directory: python tools/generate-icons.py
The white gate represents a proxy between two network endpoints.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/WhiteProxy.Admin/Assets"
ASSETS.mkdir(parents=True, exist_ok=True)
SCALE = 4
SIZES = [(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)]


def icon(state=None):
    img = Image.new("RGBA", (256 * SCALE, 256 * SCALE))
    d = ImageDraw.Draw(img)
    def box(coords): return tuple(round(n * SCALE) for n in coords)
    def line(points, fill, width):
        d.line(box(points), fill=fill, width=width * SCALE, joint="curve")
    slate, white = "#26364b", "#ffffff"
    d.rounded_rectangle(box((8, 8, 248, 248)), radius=54 * SCALE, fill=slate)
    d.rounded_rectangle(box((100, 58, 156, 198)), radius=20 * SCALE,
                        outline=white, width=12 * SCALE)
    line((52, 128, 204, 128), white, 12)
    for x in (52, 204):
        d.ellipse(box((x - 12, 116, x + 12, 140)), fill=white)
    line((126, 110, 144, 128, 126, 146), white, 11)
    if state:
        color = {"running": "#148651", "stopped": "#697586", "attention": "#be790d"}[state]
        d.ellipse(box((156, 156, 252, 252)), fill=slate)
        d.ellipse(box((164, 164, 244, 244)), fill=color)
        if state == "running":
            d.polygon([box(p) for p in ((195, 184), (195, 224), (222, 204))], fill=white)
        elif state == "stopped":
            d.rounded_rectangle(box((190, 190, 218, 218)), radius=3 * SCALE, fill=white)
        else:
            d.rounded_rectangle(box((200, 182, 208, 208)), radius=4 * SCALE, fill=white)
            d.ellipse(box((200, 216, 208, 224)), fill=white)
    return img.resize((256, 256), Image.Resampling.LANCZOS)


for name, state in (("WhiteProxy", None), ("Tray-running", "running"),
                    ("Tray-stopped", "stopped"), ("Tray-attention", "attention")):
    img = icon(state)
    img.save(ASSETS / f"{name}.ico", sizes=SIZES)
    img.save(ASSETS / f"{name}.png")

(ASSETS / "WhiteProxy.svg").write_text('''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="8" y="8" width="240" height="240" rx="54" fill="#26364b"/>
  <g fill="none" stroke="white" stroke-width="12">
    <rect x="100" y="58" width="56" height="140" rx="20"/>
    <path d="M52 128h152 M126 110l18 18-18 18"/>
  </g>
  <g fill="white"><circle cx="52" cy="128" r="12"/><circle cx="204" cy="128" r="12"/></g>
</svg>
''', encoding="utf-8")
