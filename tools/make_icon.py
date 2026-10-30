#!/usr/bin/env python3
"""PadTAS アプリアイコンの生成スクリプト。

青グラデーションの角丸タイルに白いゲームパッドを描き、
マルチサイズ .ico と 1024px PNG を出力する。
使い方: python3 tools/make_icon.py
"""
from PIL import Image, ImageDraw

S = 1024
OUT_DIR = "src/PadTas.App/Assets"

# --- 背景: 青の縦グラデーション + 角丸 ---
bg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
grad = Image.new("RGBA", (1, S))
top = (0x5E, 0x9B, 0xFF)
bottom = (0x2F, 0x4E, 0xC8)
for y in range(S):
    t = y / (S - 1)
    grad.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
grad = grad.resize((S, S))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([8, 8, S - 8, S - 8], radius=210, fill=255)
bg.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(bg)
WHITE = (245, 247, 252, 255)
DARK = (30, 32, 40, 255)

# --- グリップ (本体の後ろに円を置き、下端だけ少し覗かせる) ---
d.ellipse([100, 505, 330, 735], fill=WHITE)
d.ellipse([694, 505, 924, 735], fill=WHITE)

# --- 本体 ---
d.rounded_rectangle([120, 320, 904, 668], radius=170, fill=WHITE)

# --- 左スティック ---
d.ellipse([212, 410, 356, 554], fill=DARK)
d.ellipse([244, 442, 324, 522], fill=(70, 74, 86, 255))

# --- 十字キー ---
cx, cy, a, w = 428, 482, 34, 27
d.rounded_rectangle([cx - w, cy - a - w, cx + w, cy - a + w], radius=10, fill=DARK)
d.rounded_rectangle([cx - w, cy + a - w, cx + w, cy + a + w], radius=10, fill=DARK)
d.rounded_rectangle([cx - a - w, cy - w, cx - a + w, cy + w], radius=10, fill=DARK)
d.rounded_rectangle([cx + a - w, cy - w, cx + a + w, cy + w], radius=10, fill=DARK)
d.rectangle([cx - w, cy - w, cx + w, cy + w], fill=DARK)

# --- ABXY (4つの色ドット) ---
r = 27
for (x, y, c) in [
    (688, 392, (232, 195, 60, 255)),   # Y 黄
    (622, 458, (94, 155, 255, 255)),   # X 青
    (754, 458, (255, 107, 107, 255)),  # B 赤
    (688, 524, (63, 185, 111, 255)),   # A 緑
]:
    d.ellipse([x - r, y - r, x + r, y + r], fill=c)

# --- 中央の小さなリング (ガイドボタン風) ---
d.ellipse([492, 366, 532, 406], outline=DARK, width=9)

import os
os.makedirs(OUT_DIR, exist_ok=True)
bg.save(f"{OUT_DIR}/app-icon.png")
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
bg.save(f"{OUT_DIR}/icon.ico", format="ICO", sizes=sizes)
print("wrote", f"{OUT_DIR}/app-icon.png", "and icon.ico", sizes)
