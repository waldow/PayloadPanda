"""Convert SVG icon to multi-size .ico for WPF application."""

import io
from pathlib import Path

import cairosvg
from PIL import Image

SIZES = [16, 24, 32, 48, 64, 128, 256]

here = Path(__file__).parent
svg_path = here / "svgforicons.svg"
ico_path = here / "app.ico"

svg_data = svg_path.read_bytes()

images = []
for size in SIZES:
    png_data = cairosvg.svg2png(bytestring=svg_data, output_width=size, output_height=size)
    img = Image.open(io.BytesIO(png_data)).convert("RGBA")
    # Ensure exact pixel dimensions
    assert img.size == (size, size), f"Expected {size}x{size}, got {img.size}"
    images.append(img)

# Pillow ICO: save the largest image, append the rest
# The 'sizes' param tells Pillow which resolutions to embed
largest = images[-1]  # 256x256
largest.save(
    ico_path,
    format="ICO",
    append_images=images[:-1],
    sizes=[(s, s) for s in SIZES],
)
print(f"Created {ico_path} ({ico_path.stat().st_size / 1024:.1f} KB) with sizes: {SIZES}")
