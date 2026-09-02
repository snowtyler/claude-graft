#!/usr/bin/env python3
"""Regenerate the Windows icon from the Mac build's icon.

The mark is drawn once, in code, by Tools/make-icon.swift, and baked into
Resources/AppIcon.icns at release. macOS ships that single file; Windows wants a
multi-resolution .ico for the taskbar, the Start shortcut and the window. Rather
than draw the mark a second time in a second language — two drawings to keep
looking alike, which is the thing the Mac tool went out of its way to avoid —
this scales it from the 1024 master the Swift already produced. Run it whenever
that icon changes, so the two builds never drift into wearing different faces.

    python windows/Tools/make-icons.py

Needs Pillow (`pip install Pillow`).
"""

from pathlib import Path
from PIL import Image

REPO = Path(__file__).resolve().parents[2]
MASTER = REPO / "Resources" / "AppIcon.icns"
ASSETS = REPO / "windows" / "ClaudeGraft" / "Assets"

# The sizes a Windows .ico carries, from the taskbar's largest down to the 16
# the notification area shows. The mark still reads at 16, which is the whole
# reason it is a single stroke and not a scene.
ICO_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 64, 128, 256, 512]


def master_1024() -> Image.Image:
    icon = Image.open(MASTER)
    icon.size = (1024, 1024)  # the largest the .icns carries
    icon.load()
    return icon.convert("RGBA")


def main() -> None:
    if not MASTER.exists():
        raise SystemExit(f"no master icon at {MASTER} — draw it on the Mac first")

    master = master_1024()

    # The master carries transparent padding for the macOS menu bar, which adds
    # its own inset. The taskbar wants the mark filling the frame, so the .ico is
    # scaled from the content bbox with the padding cropped away — or the mark
    # sits inset and reads a size smaller than Claude's beside it in the taskbar.
    bbox = master.getbbox()
    cropped = master.crop(bbox) if bbox else master
    # Make it square again, centred, in case the bbox was not.
    side = max(cropped.size)
    if cropped.size != (side, side):
        sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        sq.paste(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2), cropped)
        cropped = sq

    # Pillow's ICO writer takes one image and a sizes list; it rescales
    # internally, so hand it the largest and let it downsample.
    ico_master = cropped.resize((max(ICO_SIZES), max(ICO_SIZES)), Image.LANCZOS)
    ico_master.save(
        ASSETS / "AppIcon.ico", format="ICO",
        sizes=[(s, s) for s in ICO_SIZES])

    print(f"wrote AppIcon.ico to {ASSETS}")


if __name__ == "__main__":
    main()
