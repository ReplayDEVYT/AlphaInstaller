"""Lists the sprites (with Image colour and type) used by a prefab or scene, by GameObject name.

    python -I Tools/installer/prefab_sprites.py <prefab or scene>
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "Unity", "Assets")

sprites = {}
for folder in ("Sprite", "Texture2D"):
    for name in os.listdir(os.path.join(ASSETS, folder)):
        if name.endswith(".meta"):
            m = re.search(r"^guid: ([0-9a-f]+)", open(os.path.join(ASSETS, folder, name), encoding="utf-8", errors="replace").read(), re.M)
            if m:
                sprites[m.group(1)] = name[:-5]
text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
docs = re.split(r"^--- !u!(\d+) &(\d+).*$", text, flags=re.M)
objects, gos = {}, {}
for i in range(1, len(docs) - 2, 3):
    cls, fid, body = docs[i], docs[i + 1], docs[i + 2]
    objects[fid] = (cls, body)
    if cls == "1":
        m = re.search(r"m_Name: (.*)", body)
        gos[fid] = m.group(1).strip() if m else "?"
for fid, (cls, body) in objects.items():
    m = re.search(r"m_Sprite: \{fileID: \d+, guid: ([0-9a-f]+)", body)
    if not m:
        continue
    go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
    color = re.search(r"m_Color: \{r: (\S+), g: (\S+), b: (\S+), a: (\S+)\}", body)
    typ = re.search(r"m_Type: (\d)", body)
    c = tuple(int(round(float(v.rstrip(",")) * 255)) for v in color.groups()) if color else None
    print("%-36s %-34s type %s color %s" % (gos.get(go.group(1), "?") if go else "?", sprites.get(m.group(1), m.group(1)), typ.group(1) if typ else "?", c))
