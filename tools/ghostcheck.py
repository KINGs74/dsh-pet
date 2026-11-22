#!/usr/bin/env python3
"""拖动残影检测：把一段连续截帧和一张"干净基准图"比对。

思路很简单：先把宠物拖到目标区域再拖走，趁它不在时截一张基准图（此时屏幕上
只有桌面或聊天区）。之后把宠物拖回来、在拖动过程中按固定间隔连拍，最后拖走
再截一张。逐帧和基准图相减：

  * 每帧里"和基准不同的像素"聚成几团 —— 正常情况是两团（气泡 + 宠物），
    一旦出现第三团，说明旧位置留下了残影；
  * 松手后那张图应当与基准图逐像素一致（bbox 为 None，最大差 0）。

用法：
  python tools/ghostcheck.py --ref ref.png --frames mid-*.png
  python tools/ghostcheck.py --ref ref.png --after after.png      # 只验松手
  python tools/ghostcheck.py --ref ref.png --frames mid-*.png --after after.png

阈值 --threshold 默认 30（RGB 三通道差的绝对值之和），只统计面积 >= --min-area
的团，避免抗锯齿噪点被当成残影。
"""

from __future__ import annotations

import argparse
import glob
import sys
from collections import deque

from PIL import Image, ImageChops


def load_rgb(path: str) -> Image.Image:
    return Image.open(path).convert("RGB")


def residue(ref: Image.Image, after: Image.Image) -> tuple[tuple[int, int, int, int] | None, int]:
    """两张同尺寸图的差异框与最大通道差。"""
    if ref.size != after.size:
        raise SystemExit(f"尺寸不一致：ref={ref.size} after={after.size}")
    diff = ImageChops.difference(ref, after)
    box = diff.getbbox()
    worst = 0
    if box is not None:
        worst = max(max(px) for px in diff.getdata())
    return box, worst


def mask_of(ref: Image.Image, frame: Image.Image, threshold: int) -> list[list[bool]]:
    """把"和基准差得够多"的像素标成 True，隔行隔列抽样以加快速度。"""
    ra = ref.load()
    fa = frame.load()
    w, h = ref.size
    m = [[False] * w for _ in range(h)]
    for y in range(0, h, 2):
        row_ref = ra
        for x in range(0, w, 2):
            c1 = row_ref[x, y]
            c2 = fa[x, y]
            if abs(c1[0] - c2[0]) + abs(c1[1] - c2[1]) + abs(c1[2] - c2[2]) > threshold:
                m[y][x] = True
    return m


def clusters(mask: list[list[bool]], min_area: int, merge_gap: int = 24) -> list[dict]:
    """四邻域连通块；面积按抽样倍率折算回真实像素数。

    气泡和宠物本来就是两块分开的形状，间距只有几像素，所以先把相距不到
    merge_gap 的块并成一组：正常一帧只有"一组"，出现两组以上就说明旧位置
    还留着东西。
    """
    h = len(mask)
    w = len(mask[0]) if h else 0
    seen = [[False] * w for _ in range(h)]
    parts: list[dict] = []
    for y in range(h):
        for x in range(w):
            if not mask[y][x] or seen[y][x]:
                continue
            seen[y][x] = True
            q = deque([(x, y)])
            pts = 0
            minx = maxx = x
            miny = maxy = y
            while q:
                cx, cy = q.popleft()
                pts += 1
                minx = min(minx, cx)
                maxx = max(maxx, cx)
                miny = min(miny, cy)
                maxy = max(maxy, cy)
                for dx, dy in ((2, 0), (-2, 0), (0, 2), (0, -2)):
                    nx, ny = cx + dx, cy + dy
                    if 0 <= nx < w and 0 <= ny < h and mask[ny][nx] and not seen[ny][nx]:
                        seen[ny][nx] = True
                        q.append((nx, ny))
            area = pts * 4  # 抽样步长 2 x 2
            if area >= min_area:
                parts.append({"area": area, "box": (minx, miny, maxx + 1, maxy + 1)})

    groups: list[dict] = []
    for part in parts:
        x1, y1, x2, y2 = part["box"]
        merged = False
        for g in groups:
            gx1, gy1, gx2, gy2 = g["box"]
            if not (x1 - merge_gap > gx2 or gx1 - merge_gap > x2 or
                    y1 - merge_gap > gy2 or gy1 - merge_gap > y2):
                g["box"] = (min(x1, gx1), min(y1, gy1), max(x2, gx2), max(y2, gy2))
                g["area"] += part["area"]
                merged = True
                break
        if not merged:
            groups.append({"area": part["area"], "box": part["box"]})
    groups.sort(key=lambda g: -g["area"])
    return groups


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="拖动残影检测")
    ap.add_argument("--ref", required=True, help="干净基准图（宠物不在该区域时截的）")
    ap.add_argument("--after", help="松手后的画面；与基准逐像素比对")
    ap.add_argument("--frames", nargs="*", default=[], help="拖动过程中的连拍帧")
    ap.add_argument("--threshold", type=int, default=30, help="RGB 差之和的阈值，默认 30")
    ap.add_argument("--min-area", type=int, default=600, help="团的最小面积，默认 600")
    ap.add_argument("--expect-groups", type=int, default=2,
                    help="每帧允许的最大块数：气泡和宠物各一块，默认 2；出现更多说明有残影")
    args = ap.parse_args(argv)

    ref = load_rgb(args.ref)
    failed = False

    if args.after:
        box, worst = residue(ref, load_rgb(args.after))
        if box is None:
            print(f"[OK]  松手后与基准逐像素一致（bbox=None，最大通道差 0）")
        else:
            failed = True
            print(f"[FAIL] 松手后有残留：bbox={box} 最大通道差 {worst}")

    frames: list[str] = []
    for pattern in args.frames:
        frames.extend(sorted(glob.glob(pattern)))
    if frames:
        worst_count = 0
        worst_frame = ""
        for path in frames:
            frame = load_rgb(path)
            if frame.size != ref.size:
                print(f"[SKIP] {path} 尺寸 {frame.size} 与基准 {ref.size} 不同")
                continue
            cs = clusters(mask_of(ref, frame, args.threshold), args.min_area)
            if len(cs) > worst_count:
                worst_count = len(cs)
                worst_frame = path
            if len(cs) > args.expect_groups:
                failed = True
                print(f"[FAIL] {path} 同时出现 {len(cs)} 块（最多允许 {args.expect_groups} 块）：")
                for c in cs:
                    print(f"         area={c['area']} box={c['box']}")
        verdict = "OK" if worst_count <= args.expect_groups else "FAIL"
        print("[%s]  连拍 %d 帧，单帧最多 %d 块（%s）"
              % (verdict, len(frames), worst_count, worst_frame or "n/a"))

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))