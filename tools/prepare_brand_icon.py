"""Prepare the generated Metaroq icon for WPF and Windows packaging."""

from __future__ import annotations

from collections import deque
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
PNG_PATH = ROOT / "src" / "Archivio.App" / "Assets" / "Branding" / "metaroq-icon.png"
ICO_PATH = PNG_PATH.with_suffix(".ico")


def remove_connected_light_background(image: Image.Image) -> Image.Image:
    """Remove the generated checkerboard without touching white nodes inside the tile."""

    image = image.convert("RGBA")
    width, height = image.size
    pixels = image.load()
    visited = bytearray(width * height)
    pending: deque[tuple[int, int]] = deque()

    def is_background(x: int, y: int) -> bool:
        red, green, blue, _ = pixels[x, y]
        return min(red, green, blue) > 145

    for x in range(width):
        pending.append((x, 0))
        pending.append((x, height - 1))
    for y in range(height):
        pending.append((0, y))
        pending.append((width - 1, y))

    while pending:
        x, y = pending.popleft()
        offset = y * width + x
        if visited[offset] or not is_background(x, y):
            continue

        visited[offset] = 1
        red, green, blue, _ = pixels[x, y]
        pixels[x, y] = (red, green, blue, 0)

        if x:
            pending.append((x - 1, y))
        if x + 1 < width:
            pending.append((x + 1, y))
        if y:
            pending.append((x, y - 1))
        if y + 1 < height:
            pending.append((x, y + 1))

    return image


def main() -> None:
    image = remove_connected_light_background(Image.open(PNG_PATH))
    bounds = image.getchannel("A").getbbox()
    if bounds is None:
        raise RuntimeError("The generated icon contains no visible pixels.")

    image = image.crop(bounds)
    side = max(image.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(image, ((side - image.width) // 2, (side - image.height) // 2))
    square = square.resize((1024, 1024), Image.Resampling.LANCZOS)
    square.save(PNG_PATH, optimize=True)
    square.save(
        ICO_PATH,
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    print(f"Prepared {PNG_PATH.name}: {square.size}, alpha={square.getchannel('A').getextrema()}")


if __name__ == "__main__":
    main()
