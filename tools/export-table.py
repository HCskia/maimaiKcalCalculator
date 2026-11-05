# 把 dataout\charts.json + 游戏包里的封面 bundle 整成「一行一张谱」的表格。
#
#   1) 按曲目 id 从 <StreamingAssets>\*\AssetBundleImages\jacket\ui_jacket_0<id%10000:05d>.ab
#      里解出 Texture2D（UnityPy + Pillow），落到 dataout\jackets\<id>.png
#   2) 写 dataout\charts.csv  —— 7 列，UTF-8 with BOM（Excel 直接开不乱码）
#   3) 写 dataout\charts.xlsx —— 同一份数据，封面缩略图嵌进第二列，
#      难度那列按难度上色（白字）：BASIC 绿 / ADV 黄 / EXPERT 红 / MASTER 深紫 / Re:MASTER 浅紫 / UTAGE 粉
#      （CSV 本身带不了颜色和图片，所以样式只在 xlsx 里）
#
# 用法：
#   python tools\export-table.py                      # 读 dataout\charts.json，产物落在 dataout\
#   python tools\export-table.py --limit 20           # 只处理前 20 行（试跑）
#   python tools\export-table.py --no-xlsx            # 只要 CSV + 封面
#   python tools\export-table.py --format jpg         # 封面存 JPEG（体积小很多）
#   python tools\export-table.py --game-root D:\otogame\maimai\SDEZ170
import argparse
import csv
import io
import json
import os
import sys
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor

import UnityPy
from PIL import Image
from openpyxl import Workbook
from openpyxl.drawing.image import Image as XLImage
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

# 难度 → (底色, 字体色)。底色按用户要求挑：绿 / 黄 / 红 / 深紫 / 浅一点的紫；UTAGE 没指定，用粉。
DIFF_STYLE = {
    "BASIC":      "45C124",   # 绿（游戏里 BSC 底板那个绿）
    "ADVANCED":   "FFBA01",   # 黄（ADV 的金）
    "EXPERT":     "FF5A66",   # 红（EXP 的珊瑚红）
    "MASTER":     "7A2FBF",   # 深紫
    "Re:MASTER":  "B48CE8",   # 偏白一点的紫
    "UTAGE":      "E0409A",   # 宴（用户没指定）
}
DIFF_ORDER = ["BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER", "UTAGE"]

HEADERS = ["乐曲ID", "封面", "乐曲名称", "难度", "难度等级", "预计手部移动距离(m)", "预计消耗的卡路里(kcal)"]
COL_WIDTHS = [10, 9, 46, 13, 10, 20, 22]


def find_streaming_assets(game_root):
    """游戏根 / Package / StreamingAssets 三种层级都认。"""
    if not game_root:
        return None
    cands = [
        game_root,
        os.path.join(game_root, "Package", "Sinmai_Data", "StreamingAssets"),
        os.path.join(game_root, "Sinmai_Data", "StreamingAssets"),
        os.path.join(game_root, "StreamingAssets"),
    ]
    for c in cands:
        if os.path.isdir(c) and (os.path.isdir(os.path.join(c, "A000")) or
                                 os.path.isdir(os.path.join(c, "A997"))):
            return os.path.abspath(c)
    return None


def jacket_dirs(sa):
    out = []
    for root, dirs, _files in os.walk(sa):
        for d in dirs:
            if d.lower() == "jacket":
                out.append(os.path.join(root, d))
        # 只往下挖两层：<区域>\AssetBundleImages\jacket
        if root.count(os.sep) - sa.count(os.sep) >= 2:
            dirs[:] = []
    return out


def jacket_bundle_name(music_id):
    # 封面文件名 = ui_jacket_0 + 曲目编号 5 位（DX / 宴谱都取 id % 10000）
    return "ui_jacket_0%05d.ab" % (music_id % 10000)


def resolve_bundle(dirs, music_id):
    name = jacket_bundle_name(music_id)
    for d in dirs:
        p = os.path.join(d, name)
        if os.path.isfile(p):
            return p
    return None


def extract_texture(path):
    """从 .ab 里取第一张 Texture2D，返回 PIL.Image。"""
    env = UnityPy.load(path)
    for obj in env.objects:
        if obj.type.name == "Texture2D":
            return obj.read().image
    return None


def dump_one(args_tuple):
    music_id, bundle, out_path, fmt = args_tuple
    if os.path.isfile(out_path) and os.path.getsize(out_path) > 0:
        return (music_id, "cached", 0)
    try:
        img = extract_texture(bundle)
        if img is None:
            return (music_id, "no-texture", 0)
        img = img.convert("RGB" if fmt == "jpg" else "RGBA")
        if fmt == "jpg":
            img.save(out_path, "JPEG", quality=92, optimize=True)
        else:
            img.save(out_path, "PNG", optimize=False)
        return (music_id, "ok", os.path.getsize(out_path))
    except Exception as e:                      # 单个封面坏了不该毁掉整批
        return (music_id, "error: %s: %s" % (type(e).__name__, e), 0)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)

    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--dataout", default=os.path.join(root, "dataout"))
    ap.add_argument("--game-root", default="")
    ap.add_argument("--charts", default="")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--format", choices=["png", "jpg"], default="png")
    ap.add_argument("--threads", type=int, default=8)
    ap.add_argument("--no-xlsx", action="store_true")
    ap.add_argument("--skip-jackets", action="store_true")
    args = ap.parse_args()

    dataout = os.path.abspath(args.dataout)
    charts_path = args.charts or os.path.join(dataout, "charts.json")
    with io.open(charts_path, "r", encoding="utf-8") as f:
        doc = json.load(f)

    charts = doc["charts"]
    if args.limit:
        charts = charts[:args.limit]
    meta = doc.get("meta") or {}
    print("[table] 读入 %d 行：%s" % (len(charts), charts_path))

    # ---- 游戏包位置：命令行 > charts.json 里记的 absolute 路径
    sa = find_streaming_assets(args.game_root)
    if not sa:
        sa = find_streaming_assets(meta.get("source", {}).get("streamingAssets", ""))
    dirs = jacket_dirs(sa) if sa else []
    print("[table] StreamingAssets: %s" % (sa or "(找不到)"))
    print("[table] 封面目录: %s" % (", ".join(dirs) if dirs else "(无)"))

    ext = ".jpg" if args.format == "jpg" else ".png"
    jdir = os.path.join(dataout, "jackets")
    os.makedirs(jdir, exist_ok=True)

    # ---- 1) 封面：每首曲目一张，按 id 命名
    ids = []
    seen = set()
    for c in charts:
        i = c["musicId"]
        if i not in seen:
            seen.add(i)
            ids.append(i)

    if not args.skip_jackets and dirs:
        jobs = []
        missing = []
        for i in ids:
            b = resolve_bundle(dirs, i)
            if not b:
                missing.append(i)
                continue
            jobs.append((i, b, os.path.join(jdir, "%06d%s" % (i, ext)), args.format))

        t0 = time.time()
        done = 0
        stats = {}
        with ThreadPoolExecutor(max_workers=max(1, args.threads)) as ex:
            for music_id, status, size in ex.map(dump_one, jobs):
                done += 1
                key = status if status in ("ok", "cached") else "fail"
                stats[key] = stats.get(key, 0) + 1
                if status not in ("ok", "cached") and not status.startswith("error"):
                    stats.setdefault("notes", []).append("#%d %s" % (music_id, status))
                elif status.startswith("error"):
                    stats.setdefault("errors", []).append("#%d %s" % (music_id, status))
                if done % 300 == 0:
                    print("  封面 %d / %d …" % (done, len(jobs)))
        print("[table] 封面：%d 首（新导出 %d / 命中已有 %d / 失败 %d），用时 %.1f s"
              % (len(ids), stats.get("ok", 0), stats.get("cached", 0), stats.get("fail", 0), time.time() - t0))
        if missing:
            print("[table] 包里没有封面的曲目 %d 首：%s" % (len(missing), missing[:10]))
        for line in stats.get("errors", [])[:5]:
            print("  ! " + line)
    elif args.skip_jackets:
        print("[table] 跳过封面导出")

    # ---- 2) CSV
    csv_path = os.path.join(dataout, "charts.csv")
    with io.open(csv_path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(HEADERS)
        for c in charts:
            i = c["musicId"]
            jacket = "%s/%06d%s" % ("jackets", i, ext)
            w.writerow([
                i,
                jacket if os.path.isfile(os.path.join(dataout, jacket.replace("/", os.sep))) else "",
                c.get("title") or "",
                c.get("difficultyName") or "",
                c.get("levelText") or "",
                "%.3f" % c["meters"],
                "%.3f" % c["kcal"],
            ])
    print("[table] 写出 CSV：%s（%.1f KB）" % (csv_path, os.path.getsize(csv_path) / 1024.0))

    if args.no_xlsx:
        return 0

    # ---- 3) XLSX（带封面缩略图与难度配色）
    tmp = tempfile.mkdtemp(prefix="kcal_thumbs_")
    thumbs = {}
    for i in ids:
        src = os.path.join(jdir, "%06d%s" % (i, ext))
        if not os.path.isfile(src):
            continue
        p = os.path.join(tmp, "%06d.jpg" % i)
        img = Image.open(src).convert("RGB")
        img.thumbnail((64, 64), Image.LANCZOS)
        img.save(p, "JPEG", quality=78, optimize=True)
        thumbs[i] = p

    wb = Workbook()
    ws = wb.active
    ws.title = "谱面热量"
    ws.sheet_view.showGridLines = False

    head_font = Font(bold=True, color="FFFFFF", size=11)
    head_fill = PatternFill("solid", fgColor="3C3C46")
    thin = Side(style="thin", color="D9D9E0")
    border = Border(left=thin, right=thin, top=thin, bottom=thin)

    for idx, name in enumerate(HEADERS, start=1):
        cell = ws.cell(row=1, column=idx, value=name)
        cell.font = head_font
        cell.fill = head_fill
        cell.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        cell.border = border
        ws.column_dimensions[get_column_letter(idx)].width = COL_WIDTHS[idx - 1]
    ws.row_dimensions[1].height = 30
    ws.freeze_panes = "A2"

    for r, c in enumerate(charts, start=2):
        music_id = c["musicId"]
        diff = c.get("difficultyName") or ""
        ws.cell(row=r, column=1, value=music_id).alignment = Alignment(horizontal="center")
        ws.cell(row=r, column=3, value=c.get("title") or "")
        dcell = ws.cell(row=r, column=4, value=diff)
        ws.cell(row=r, column=5, value=c.get("levelText") or "").alignment = Alignment(horizontal="center")
        mcell = ws.cell(row=r, column=6, value=c["meters"])
        mcell.number_format = "0.000"
        kcell = ws.cell(row=r, column=7, value=c["kcal"])
        kcell.number_format = "0.000"

        color = DIFF_STYLE.get(diff)
        if color:
            dcell.fill = PatternFill("solid", fgColor=color)
            dcell.font = Font(bold=True, color="FFFFFF")
        dcell.alignment = Alignment(horizontal="center")

        for col in range(1, 8):
            ws.cell(row=r, column=col).border = border

        thumb = thumbs.get(music_id)
        if thumb:
            img = XLImage(thumb)
            img.width = 40
            img.height = 40
            ws.add_image(img, "B%d" % r)
        ws.row_dimensions[r].height = 32

        if r % 1000 == 0:
            print("  表格 %d 行 …" % r)

    ws.auto_filter.ref = "A1:G%d" % (1 + len(charts))
    xlsx_path = os.path.join(dataout, "charts.xlsx")
    wb.save(xlsx_path)
    print("[table] 写出 XLSX：%s（%.1f MB，含 %d 张封面缩略图）"
          % (xlsx_path, os.path.getsize(xlsx_path) / (1024.0 * 1024.0), len(thumbs)))

    # 收拾临时缩略图
    try:
        for p in thumbs.values():
            os.remove(p)
        os.rmdir(tmp)
    except OSError:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
