#!/usr/bin/env python3
"""Regenerate the Windows icon assets from the Mac build's icon.

The mark is drawn once, in code, by Tools/make-icon.swift, and baked into
Resources/AppIcon.icns at release. macOS ships that single file; Windows
packaging wants every size on disk as its own PNG, and a tray and window icon
as a multi-resolution .ico. Rather than draw the mark a second time in a second
language — two drawings to keep looking alike, which is the thing the Mac tool
went out of its way to avoid — this scales all of them from the 1024 master the
Swift already produced. Run it whenever that icon changes, so the two builds
never drift into wearing different faces.

    python windows/Tools/make-icons.py

Needs Pillow (`pip install Pillow`).
"""

from pathlib import Path
from PIL import Image

REPO = Path(__file__).resolve().parents[2]
MASTER = REPO / "Resources" / "AppIcon.icns"
ASSETS = REPO / "windows" / "ClaudeGraft" / "Assets"

# The tile and logo PNGs the app package names, each at the exact pixel size its
# filename promises — a scale-200 tile is twice its base, a targetsize is itself.
SQUARE = {
    "Square44x44Logo.scale-200.png": 88,
    "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
    "Square44x44Logo.targetsize-48_altform-lightunplated.png": 48,
    "Square150x150Logo.scale-200.png": 300,
    "LockScreenLogo.scale-200.png": 48,
    "StoreLogo.png": 50,
}

# The wide tiles are not square; the mark sits at their height, centred, on the
# transparency Windows fills with the accent colour behind it.
WIDE = {
    "Wide310x150Logo.scale-200.png": (620, 300),
    "SplashScreen.scale-200.png": (1240, 600),
}

# The sizes a Windows .ico carries, from the taskbar's largest down to the 16
# the notification area shows. The mark still reads at 16, which is the whole
# reason it is a single stroke and not a scene.
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


def master_1024() -> Image.Image:
    icon = Image.open(MASTER)
    icon.size = (1024, 1024)  # the largest the .icns carries
    icon.load()
    return icon.convert("RGBA")


def main() -> None:
    if not MASTER.exists():
        raise SystemExit(f"no master icon at {MASTER} — draw it on the Mac first")

    master = master_1024()

    def square(size: int) -> Image.Image:
        return master.resize((size, size), Image.LANCZOS)

    for name, size in SQUARE.items():
        square(size).save(ASSETS / name)

    for name, (width, height) in WIDE.items():
        canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        mark = square(min(width, height))
        canvas.paste(mark, ((width - mark.width) // 2, (height - mark.height) // 2), mark)
        canvas.save(ASSETS / name)

    master.save(ASSETS / "AppIcon.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES])

    print(f"wrote {len(SQUARE) + len(WIDE) + 1} assets to {ASSETS}")


if __name__ == "__main__":
    main()
