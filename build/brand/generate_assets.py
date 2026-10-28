"""
Gera os arquivos de marca do NinjaOptimizer a partir da geometria do símbolo (shuriken).

    python build/brand/generate_assets.py

Requer Pillow. Saídas:
  src/NinjaOptimizer.App/Assets/app.ico      ícone do .exe (16–256 px; 16/24 simplificados)
  src/NinjaOptimizer.App/Assets/logo.png     ícone arredondado 256 px (README, instalador)
  installer/wizard-small*.bmp, wizard-large*.bmp   imagens do assistente de instalação
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src" / "NinjaOptimizer.App" / "Assets"
FONTS = ASSETS / "Fonts"
SS = 4  # supersampling

# Shuriken no grid de 64 (mesmos pontos do SVG do guia de marca)
SHURIKEN = [(32, 3), (40, 21.8), (61, 32), (42.2, 40), (32, 61), (24, 42.2), (3, 32), (21.8, 24)]
HOLE_R = 4.5


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def gradient(size, stops, direction):
    """direction: 'diag' (135°, canto sup. esq. → inf. dir.) ou 'down'."""
    w, h = size
    img = Image.new("RGBA", size)
    px = img.load()
    stops = [(o, hex_rgb(c) + (255,)) for o, c in stops]
    for y in range(h):
        for x in range(w):
            t = (x + y) / (w + h - 2) if direction == "diag" else y / (h - 1)
            for i in range(len(stops) - 1):
                (o0, c0), (o1, c1) = stops[i], stops[i + 1]
                if t <= o1 or i == len(stops) - 2:
                    px[x, y] = lerp(c0, c1, min(1, max(0, (t - o0) / (o1 - o0))))
                    break
    return img


def shuriken_mask(size, offset, scale, hole=True):
    m = Image.new("L", size, 0)
    d = ImageDraw.Draw(m)
    d.polygon([(offset + x * scale, offset + y * scale) for x, y in SHURIKEN], fill=255)
    if hole:
        c, r = offset + 32 * scale, HOLE_R * scale
        d.ellipse([c - r, c - r, c + r, c + r], fill=0)
    return m


def glow(size, radius_frac=118 / 256, color="#B98BFF", alpha=0.35):
    w = size[0]
    g = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    rgb = hex_rgb(color)
    steps = 64
    for i in range(steps, 0, -1):
        r = w * radius_frac * i / steps
        a = round(255 * alpha * (1 - i / steps))
        d.ellipse([w / 2 - r, w / 2 - r, w / 2 + r, w / 2 + r], fill=rgb + (a,))
    return g.filter(ImageFilter.GaussianBlur(w / 40))


BG_STOPS = [(0, "#6A1FD0"), (0.55, "#44088C"), (1, "#2A055A")]


def icon(px, variant):
    """variant: 'square' (exe), 'rounded' (Win11/site), 'small' (16–24 px)."""
    s = px * SS
    size = (s, s)
    bg = gradient(size, BG_STOPS, "diag")
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    shape = Image.new("L", size, 0)
    if variant == "square":
        shape.paste(255, [0, 0, s, s])
    else:
        ImageDraw.Draw(shape).rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 58 / 256, fill=255)
    out.paste(bg, (0, 0), shape)

    if variant == "small":
        star = shuriken_mask(size, s * 33.6 / 256, s * 2.95 / 256, hole=False)
        out.paste(Image.new("RGBA", size, (255, 255, 255, 255)), (0, 0), star)
    else:
        out.alpha_composite(Image.composite(glow(size), Image.new("RGBA", size, (0, 0, 0, 0)), shape))
        fg = gradient(size, [(0, "#FFFFFF"), (1, "#DCC6FF")], "down")
        out.paste(fg, (0, 0), shuriken_mask(size, s * 39.7 / 256, s * 2.76 / 256))
        if variant == "rounded":
            d = ImageDraw.Draw(out)
            d.rounded_rectangle([SS, SS, s - SS - 1, s - SS - 1], radius=s * 57 / 256,
                                outline=(255, 255, 255, 41), width=2 * SS)
    return out.resize((px, px), Image.LANCZOS)


def wordmark(draw, xy, size, anchor="mm"):
    bold = ImageFont.truetype(str(FONTS / "Sora-700.ttf"), size)
    light = ImageFont.truetype(str(FONTS / "Sora-300.ttf"), size)
    w1 = draw.textlength("Ninja", font=bold)
    w2 = draw.textlength("Optimizer", font=light)
    x, y = xy
    x0 = x - (w1 + w2) / 2 if anchor == "mm" else x
    draw.text((x0, y), "Ninja", font=bold, fill=hex_rgb("#F5F3FA"), anchor="lm")
    draw.text((x0 + w1, y), "Optimizer", font=light, fill=hex_rgb("#B98BFF"), anchor="lm")


def wizard_large(scale):
    w, h = 164 * scale, 314 * scale
    img = Image.new("RGBA", (w, h), hex_rgb("#07060B") + (255,))
    g = glow((w, w), radius_frac=0.62, color="#44088C", alpha=0.8)
    img.alpha_composite(g, (0, int(h * 0.36 - w / 2)))
    sym = 72 * scale
    star = Image.new("RGBA", (sym * SS, sym * SS), (0, 0, 0, 0))
    fg = gradient(star.size, [(0, "#D5BAFF"), (1, "#7B2FE0")], "diag")
    star.paste(fg, (0, 0), shuriken_mask(star.size, 0, sym * SS / 64))
    star = star.resize((sym, sym), Image.LANCZOS)
    img.alpha_composite(star, ((w - sym) // 2, int(h * 0.36 - sym / 2)))
    wordmark(ImageDraw.Draw(img), (w / 2, h * 0.36 + sym * 0.85), 17 * scale)
    return img.convert("RGB")


def wizard_small(scale):
    s = 55 * scale
    img = Image.new("RGBA", (s, 58 * scale), (255, 255, 255, 0))
    ic = icon(s - 6 * scale, "rounded")
    img.alpha_composite(ic, (3 * scale, 4 * scale))
    return img


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [icon(n, "small" if n <= 24 else "square") for n in sizes]
    frames[-1].save(ASSETS / "app.ico", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    icon(256, "rounded").save(ASSETS / "logo.png")

    inst = ROOT / "installer"
    for scale, suffix in ((1, ""), (2, "-200")):
        wizard_large(scale).save(inst / f"wizard-large{suffix}.bmp")
        wizard_small(scale).save(inst / f"wizard-small{suffix}.bmp")
    print("Arquivos de marca gerados.")


if __name__ == "__main__":
    main()
