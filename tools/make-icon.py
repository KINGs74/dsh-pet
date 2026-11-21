"""生成 dsh-pet 的浮层图标：native/app.ico 与 icon.svg 使用同一套几何。

设计：圆角方形徽章（蓝渐变）+ 白色对话气泡 + 两只眼睛。
远看是「一个在说话的伙伴」，16px 下仍能辨认出气泡与眼睛。
"""
import os
import struct
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
NATIVE = os.path.join(ROOT, "native")


def lerp(a, b, t):
    return int(round(a + (b - a) * t))


def render(size):
    scale = 8  # 先 8 倍绘制再缩小，得到平滑边缘
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 圆角方形徽章 + 竖向渐变
    radius = int(s * 0.24)
    badge = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    bd = ImageDraw.Draw(badge)
    top = (58, 132, 255)
    bottom = (26, 76, 208)
    for y in range(s):
        t = y / max(1, s - 1)
        bd.line([(0, y), (s, y)], fill=(lerp(top[0], bottom[0], t), lerp(top[1], bottom[1], t), lerp(top[2], bottom[2], t), 255))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=255)
    img.paste(badge, (0, 0), mask)

    # 白色对话气泡（圆角矩形 + 左下小尾巴）
    bx0, by0 = int(s * 0.17), int(s * 0.20)
    bx1, by1 = int(s * 0.83), int(s * 0.66)
    d.rounded_rectangle([bx0, by0, bx1, by1], radius=int(s * 0.16), fill=(255, 255, 255, 255))
    tail = [
        (int(s * 0.30), int(s * 0.63)),
        (int(s * 0.46), int(s * 0.63)),
        (int(s * 0.34), int(s * 0.82)),
    ]
    d.polygon(tail, fill=(255, 255, 255, 255))

    # 眼睛 + 微笑：气泡里的「伙伴」
    eye_r = int(s * 0.046)
    cy = int(s * 0.41)
    for cx in (int(s * 0.37), int(s * 0.63)):
        d.ellipse([cx - eye_r, cy - eye_r, cx + eye_r, cy + eye_r], fill=(23, 43, 92, 255))
    if size >= 32:
        d.arc([int(s * 0.39), int(s * 0.44), int(s * 0.61), int(s * 0.60)], start=15, end=165,
              fill=(23, 43, 92, 255), width=max(2, int(s * 0.022)))

    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [render(n) for n in sizes]
    ico_path = os.path.join(NATIVE, "app.ico")
    frames[-1].save(ico_path, format="ICO", sizes=[(n, n) for n in sizes])
    print("wrote", ico_path, os.path.getsize(ico_path), "bytes")

    png_path = os.path.join(ROOT, "icon.png")
    frames[-1].save(png_path)
    print("wrote", png_path)


if __name__ == "__main__":
    main()