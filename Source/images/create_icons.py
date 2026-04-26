"""Create the PayloadPanda multi-size .ico assets."""

from __future__ import annotations

import shutil
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

SIZES = [16, 24, 32, 48, 64, 128, 256]
BASE = 1024
UNIT = BASE / 512

HERE = Path(__file__).parent
SOURCE_ROOT = HERE.parent
ICO_PATH = HERE / "app.ico"
WINDOW_ICO_PATH = SOURCE_ROOT / "app.ico"
PREVIEW_PATH = HERE / "app-preview.png"

# Keep icon regeneration lightweight: the repo no longer needs CairoSVG just to
# refresh the Windows icon files.


def s(value: float) -> int:
    return round(value * UNIT)


def box(values: tuple[float, float, float, float]) -> tuple[int, int, int, int]:
    return tuple(s(value) for value in values)


def rgba(hex_color: str, alpha: int = 255) -> tuple[int, int, int, int]:
    hex_color = hex_color.lstrip("#")
    return (
        int(hex_color[0:2], 16),
        int(hex_color[2:4], 16),
        int(hex_color[4:6], 16),
        alpha,
    )


def lerp(a: int, b: int, t: float) -> int:
    return round(a + (b - a) * t)


def gradient(size: tuple[int, int], top: str, bottom: str) -> Image.Image:
    width, height = size
    top_rgba = rgba(top)
    bottom_rgba = rgba(bottom)
    img = Image.new("RGBA", size)
    pixels = img.load()

    for y in range(height):
        t = y / max(height - 1, 1)
        color = tuple(lerp(top_rgba[i], bottom_rgba[i], t) for i in range(4))
        for x in range(width):
            pixels[x, y] = color

    return img


def paste_rounded_gradient(
    target: Image.Image,
    rect: tuple[int, int, int, int],
    radius: int,
    top: str,
    bottom: str,
) -> None:
    width = rect[2] - rect[0]
    height = rect[3] - rect[1]
    fill = gradient((width, height), top, bottom)
    mask = Image.new("L", (width, height), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, width, height), radius=radius, fill=255)
    target.paste(fill, rect[:2], mask)


def draw_glow(
    image: Image.Image,
    draw_func,
    color: tuple[int, int, int, int],
    blur: int,
) -> None:
    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw_func(ImageDraw.Draw(glow), color)
    image.alpha_composite(glow.filter(ImageFilter.GaussianBlur(blur)))
    image.alpha_composite(glow)


def load_font(size: int, bold: bool = False) -> ImageFont.ImageFont:
    font_names = ["consolab.ttf", "seguisym.ttf", "arialbd.ttf"] if bold else ["consola.ttf", "segoeui.ttf", "arial.ttf"]
    font_dirs = [Path("C:/Windows/Fonts"), Path("/usr/share/fonts/truetype/dejavu")]

    for font_dir in font_dirs:
        for name in font_names:
            path = font_dir / name
            if path.exists():
                return ImageFont.truetype(str(path), size)

    return ImageFont.load_default()


def centered_text(draw: ImageDraw.ImageDraw, xy: tuple[int, int], text: str, font: ImageFont.ImageFont, fill) -> None:
    try:
        draw.text(xy, text, font=font, fill=fill, anchor="mm")
    except TypeError:
        bounds = draw.textbbox((0, 0), text, font=font)
        draw.text((xy[0] - (bounds[2] - bounds[0]) / 2, xy[1] - (bounds[3] - bounds[1]) / 2), text, font=font, fill=fill)


def render() -> Image.Image:
    img = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))

    paste_rounded_gradient(img, box((18, 18, 494, 494)), s(112), "#101925", "#05080e")
    draw = ImageDraw.Draw(img)

    def outer_border(layer: ImageDraw.ImageDraw, color) -> None:
        layer.rounded_rectangle(box((23, 23, 489, 489)), radius=s(107), outline=color, width=s(5))

    draw_glow(img, outer_border, rgba("#42cfff", 190), s(8))
    draw.rounded_rectangle(box((52, 61, 460, 455)), radius=s(78), fill=rgba("#0a1520", 238), outline=rgba("#1f4e78", 210), width=s(3))

    for coords, color in [
        ((88, 126, 133, 126), "#3bdcff"),
        ((83, 163, 152, 163), "#7d68ff"),
        ((91, 199, 146, 199), "#2489ff"),
        ((371, 150, 413, 150), "#3bdcff"),
        ((360, 189, 430, 189), "#2489ff"),
        ((381, 228, 427, 228), "#7d68ff"),
    ]:
        draw.line(box(coords), fill=rgba(color, 90), width=s(7))

    brace_font = load_font(s(74), bold=True)

    def braces(layer: ImageDraw.ImageDraw, color) -> None:
        centered_text(layer, (s(99), s(337)), "{", brace_font, color)
        centered_text(layer, (s(413), s(337)), "}", brace_font, color)

    draw_glow(img, braces, rgba("#61f4ff", 210), s(7))

    # Hoodie and paws give the icon a dark base that reads well on the taskbar.
    draw.ellipse(box((96, 324, 416, 534)), fill=rgba("#111722"))
    draw.ellipse(box((124, 342, 244, 464)), fill=rgba("#080b10"))
    draw.ellipse(box((268, 342, 388, 464)), fill=rgba("#080b10"))
    draw.arc(box((112, 302, 400, 520)), start=196, end=344, fill=rgba("#1595ff", 220), width=s(15))

    # Ears.
    draw.ellipse(box((91, 88, 211, 208)), fill=rgba("#080a10"), outline=rgba("#65eaff", 235), width=s(5))
    draw.ellipse(box((301, 88, 421, 208)), fill=rgba("#080a10"), outline=rgba("#65eaff", 235), width=s(5))
    draw.ellipse(box((116, 113, 186, 183)), fill=rgba("#02040a", 215))
    draw.ellipse(box((326, 113, 396, 183)), fill=rgba("#02040a", 215))

    # Head and a tiny tuft.
    draw.ellipse(box((92, 125, 420, 421)), fill=rgba("#f1eee8"))
    draw.ellipse(box((116, 138, 398, 408)), fill=rgba("#fffaf2", 210))
    draw.polygon(
        [
            (s(206), s(150)),
            (s(226), s(103)),
            (s(249), s(145)),
            (s(276), s(103)),
            (s(295), s(151)),
        ],
        fill=rgba("#fff8ef"),
    )

    # Eye patches.
    draw.ellipse(box((151, 205, 245, 305)), fill=rgba("#111219"))
    draw.ellipse(box((267, 205, 361, 305)), fill=rgba("#111219"))

    # Glasses.
    for rect in [(124, 198, 247, 292), (265, 198, 388, 292)]:
        draw.rounded_rectangle(box(rect), radius=s(28), fill=rgba("#07101b", 232))
        draw.rounded_rectangle(box(rect), radius=s(28), outline=rgba("#07090e"), width=s(14))
        draw.rounded_rectangle(box(rect), radius=s(28), outline=rgba("#92f7ff", 145), width=s(4))
    draw.line(box((246, 241, 266, 241)), fill=rgba("#07090e"), width=s(15))
    draw.line(box((118, 214, 96, 199)), fill=rgba("#07090e"), width=s(12))
    draw.line(box((394, 214, 416, 199)), fill=rgba("#07090e"), width=s(12))

    # Eyes.
    for cx in [190, 322]:
        draw.ellipse(box((cx - 28, 219, cx + 28, 275)), fill=rgba("#ffffff"))
        draw.ellipse(box((cx - 20, 227, cx + 20, 267)), fill=rgba("#111623"))
        draw.ellipse(box((cx + 2, 231, cx + 16, 245)), fill=rgba("#ffffff"))

    for coords, color, width in [
        ((155, 264, 202, 264), "#1c9fff", 7),
        ((301, 264, 348, 264), "#1c9fff", 7),
        ((166, 280, 218, 280), "#23f2aa", 6),
        ((291, 280, 343, 280), "#23f2aa", 6),
    ]:
        draw.line(box(coords), fill=rgba(color, 230), width=s(width))

    # Nose and smile.
    draw.rounded_rectangle(box((225, 306, 287, 341)), radius=s(18), fill=rgba("#07090e"))
    draw.polygon([(s(225), s(319)), (s(287), s(319)), (s(256), s(354))], fill=rgba("#07090e"))
    draw.arc(box((194, 322, 256, 370)), start=12, end=168, fill=rgba("#0a0b0f"), width=s(7))
    draw.arc(box((256, 322, 318, 370)), start=12, end=168, fill=rgba("#0a0b0f"), width=s(7))

    # Payload arrows.
    def arrows(layer: ImageDraw.ImageDraw, color) -> None:
        layer.line(box((167, 408, 272, 408)), fill=color, width=s(13))
        layer.line(box((250, 386, 284, 415)), fill=color, width=s(13))
        layer.line(box((250, 444, 284, 415)), fill=color, width=s(13))
        layer.line(box((348, 431, 243, 431)), fill=rgba("#23f2aa", color[3]), width=s(13))
        layer.line(box((265, 409, 231, 438)), fill=rgba("#23f2aa", color[3]), width=s(13))
        layer.line(box((265, 467, 231, 438)), fill=rgba("#23f2aa", color[3]), width=s(13))

    draw_glow(img, arrows, rgba("#54e7ff", 230), s(7))

    return img


def main() -> None:
    base = render()
    icons = [base.resize((size, size), Image.Resampling.LANCZOS) for size in SIZES]
    largest = icons[-1]
    largest.save(
        ICO_PATH,
        format="ICO",
        append_images=icons[:-1],
        sizes=[(size, size) for size in SIZES],
    )
    shutil.copyfile(ICO_PATH, WINDOW_ICO_PATH)
    base.resize((512, 512), Image.Resampling.LANCZOS).save(PREVIEW_PATH)

    print(f"Created {ICO_PATH}, {WINDOW_ICO_PATH}, and {PREVIEW_PATH} with sizes: {SIZES}")


if __name__ == "__main__":
    main()
