"""Crops the game's UI sprites used by the installer out of the project's textures.

    python -I Tools/installer/extract_ui_sprites.py [sprite names...]

Reads Unity/Assets/Sprite/<name>.asset (m_Rect, m_Border, texture GUID), finds the texture by its .meta GUID under
Unity/Assets and writes Tools/installer/Assets/<name>.png plus sprites.json (border left, bottom, right, top in
pixels, as Unity's m_Border x/y/z/w). Unity's rect origin is bottom-left, so y is flipped.
"""
import json
import os
import re
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "Unity", "Assets")
OUT = os.path.join(ROOT, "Tools", "installer", "Assets")
DEFAULT = [
    "info_window_back", "header_panel", "border_generic", "border_16", "mainbutton_idle", "mainbutton_redglow",
    "close_window_x", "close_button", "experience_bar_background", "experience_bar_gradient", "level_fill_bar",
    "level_fill_bar_current", "input_box", "checkbox", "checkbox_checked", "checkbox_empty",
    "escape_from_tarkov_logo", "header_shadow", "tab_bar_gradient", "bottom_bar_tile", "health_bar_border",
    "health_bar_fill", "subbutton", "test_infowindow_external", "option_button_on", "option_button_off",
    "scroller_button_drag_vertical", "slider_background", "tab_button", "statlist_header", "item_header_long",
    "notification_item_background", "location_button_background", "trader_panel_diagonal_lines", "skill_border",
    "search_panel_gradient",
]


def guid_index():
    index = {}
    for folder in ("Texture2D", "Sprite"):
        base = os.path.join(ASSETS, folder)
        for name in os.listdir(base):
            if name.endswith(".png.meta"):
                with open(os.path.join(base, name), encoding="utf-8", errors="replace") as handle:
                    m = re.search(r"^guid: ([0-9a-f]+)", handle.read(), re.M)
                if m:
                    index[m.group(1)] = os.path.join(base, name[:-5])
    return index


def num(text, key):
    m = re.search(r"\b" + key + r": (-?[0-9.e+-]+)", text)
    return float(m.group(1)) if m else 0.0


def main():
    names = sys.argv[1:] or DEFAULT
    os.makedirs(OUT, exist_ok=True)
    index = guid_index()
    info = {}
    for name in names:
        path = os.path.join(ASSETS, "Sprite", name + ".asset")
        if not os.path.exists(path):
            print("missing sprite %s" % name)
            continue
        text = open(path, encoding="utf-8", errors="replace").read()
        rect = re.search(r"m_Rect:\s*serializedVersion: 2\s*x: (\S+)\s*y: (\S+)\s*width: (\S+)\s*height: (\S+)", text)
        border = re.search(r"m_Border: \{x: (\S+), y: (\S+), z: (\S+), w: (\S+)\}", text)
        tex = re.search(r"m_RD:\s*texture: \{fileID: \d+, guid: ([0-9a-f]+)", text)
        if not (rect and tex) or tex.group(1) not in index:
            print("no texture for %s" % name)
            continue
        x, y, w, h = (float(v) for v in rect.groups())
        image = Image.open(index[tex.group(1)]).convert("RGBA")
        top = image.height - (y + h)
        crop = image.crop((int(round(x)), int(round(top)), int(round(x + w)), int(round(top + h))))
        crop.save(os.path.join(OUT, name + ".png"))
        b = [int(float(v)) for v in border.groups()] if border else [0, 0, 0, 0]
        info[name] = {"width": crop.width, "height": crop.height, "border": b,
                      "texture": os.path.relpath(index[tex.group(1)], ASSETS).replace(os.sep, "/")}
        print("%-32s %4dx%-4d border %s  from %s" % (name, crop.width, crop.height, b, info[name]["texture"]))
    with open(os.path.join(OUT, "sprites.json"), "w", encoding="utf-8") as handle:
        json.dump(info, handle, indent=1, sort_keys=True)


main()
