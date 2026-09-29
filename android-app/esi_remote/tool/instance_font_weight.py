"""Instantiate static weights from a variable font, for Flutter's static font manifest."""
import sys
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

src, out, wght = sys.argv[1], sys.argv[2], int(sys.argv[3])
font = TTFont(src)
static = instancer.instantiateVariableFont(font, {"wght": wght}, inplace=False, updateFontNames=False)
static.save(out)
print("wrote", out)
