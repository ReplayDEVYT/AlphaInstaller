"""Converts the game's Bender fonts (CFF OpenType, which GDI+ cannot load) to TrueType for the installer.

    set PYTHONPATH=Build\\pylib   (pip install --target Build\\pylib fonttools)
    python Tools/installer/otf_to_ttf.py

Writes Tools/installer/Assets/Bender.ttf and Bender-Bold.ttf. Cubic CFF outlines become quadratic (cu2qu, max error
1 unit); hinting is dropped.
"""
import os

from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont, newTable

def _project_root():
    # the folder holding TarkovAlphaServer, wherever this script was moved to
    d = os.path.dirname(os.path.abspath(__file__))
    while not os.path.isdir(os.path.join(d, "TarkovAlphaServer")):
        parent = os.path.dirname(d)
        if parent == d:
            raise SystemExit("project root (the folder with TarkovAlphaServer) not found above " + __file__)
        d = parent
    return d


ROOT = _project_root()
SRC = os.path.join(ROOT, "Unity", "Assets", "Font")
OUT = os.path.join(ROOT, "Tools", "installer", "Assets")


def convert(src, dst):
    font = TTFont(src)
    glyph_order = font.getGlyphOrder()
    glyph_set = font.getGlyphSet()
    glyf = newTable("glyf")
    glyf.glyphOrder = glyph_order
    glyf.glyphs = {}
    for name in glyph_order:
        pen = TTGlyphPen(glyph_set)
        glyph_set[name].draw(Cu2QuPen(pen, max_err=1.0, reverse_direction=True))
        glyf.glyphs[name] = pen.glyph()
    font["glyf"] = glyf
    font["loca"] = newTable("loca")
    maxp = font["maxp"]
    maxp.tableVersion = 0x00010000
    for attr in ("maxZones", "maxTwilightPoints", "maxStorage", "maxFunctionDefs", "maxInstructionDefs",
                 "maxStackElements", "maxSizeOfInstructions", "maxComponentElements"):
        setattr(maxp, attr, 0)
    maxp.maxZones = 1
    maxp.maxComponentDepth = 0
    maxp.maxPoints = maxp.maxContours = maxp.maxCompositePoints = maxp.maxCompositeContours = 0
    font["head"].glyphDataFormat = 0
    post = font["post"]
    post.formatType = 2.0
    post.extraNames = []
    post.mapping = {}
    post.glyphOrder = glyph_order
    for tag in ("CFF ", "VORG"):
        if tag in font:
            del font[tag]
    font.sfntVersion = "\x00\x01\x00\x00"
    font.save(dst)
    print("%s -> %s" % (os.path.basename(src), dst))


os.makedirs(OUT, exist_ok=True)
convert(os.path.join(SRC, "Jovanny Lemonad - Bender.otf"), os.path.join(OUT, "Bender.ttf"))
convert(os.path.join(SRC, "Jovanny Lemonad - Bender-Bold.otf"), os.path.join(OUT, "Bender-Bold.ttf"))
