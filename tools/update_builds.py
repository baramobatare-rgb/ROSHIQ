#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Обновляет базовые сборки героев (src/Roshiq.Data/Resources/hero_builds.txt) по открытым данным OpenDota.

Источник: GET https://api.opendota.com/api/heroes/{id}/itemPopularity — что покупают на герое в начале,
в ранней, средней и поздней игре (выборка профессиональных матчей). Процента побед у OpenDota в этих данных нет:
сборка — это самые частые покупки, а не «предметы с лучшим винрейтом».

Что делает скрипт:
  * берёт героев, позиции и известные предметы из данных ROSHIQ (heroes.txt, items.txt);
  * убирает компоненты, если на герое покупают и собранный из них предмет (Boots → Phase Boots, Yasha → Manta);
  * оставляет предметы, подходящие роли (поле «позиции» в items.txt), расходники — только в стартовой закупке;
  * каждый предмет ставит в ту стадию, где его покупают чаще всего;
  * не трогает файл, если данных слишком мало или результат выглядит сломанным.

Запуск: python tools/update_builds.py            — скачать и обновить файлы
        python tools/update_builds.py --cache D  — взять ответы API из папки D (для проверки без сети)
Запускается раз в неделю из .github/workflows/update-builds.yml. Только стандартная библиотека Python.
"""
import argparse
import datetime
import json
import os
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = os.path.join(ROOT, "src", "Roshiq.Data", "Resources")
API = "https://api.opendota.com/api"

PHASES = [("start", "start_game_items", 4), ("early", "early_game_items", 3),
          ("mid", "mid_game_items", 3), ("late", "late_game_items", 3)]
# расходники и служебное: вне стартовой закупки в сборку не идут
CONSUMABLES = {
    "item_tango", "item_tango_single", "item_flask", "item_clarity", "item_faerie_fire", "item_enchanted_mango",
    "item_ward_observer", "item_ward_sentry", "item_ward_dispenser", "item_tpscroll", "item_smoke_of_deceit", "item_dust",
    "item_blood_grenade", "item_tome_of_knowledge", "item_cheese", "item_aegis", "item_refresher_shard", "item_bottle",
    "item_famango", "item_great_famango", "item_greater_famango", "item_infused_raindrop",
}
# то, что керри и мидер на старте не покупают, даже если в выборке это изредка встречается
SUPPORT_START = {"item_ward_observer", "item_ward_sentry", "item_ward_dispenser", "item_smoke_of_deceit", "item_dust", "item_blood_grenade"}
MIN_SAMPLE = 20        # меньше покупок в середине игры — героя почти не берут, сборку не выводим
MIN_SHARE = 0.12       # предмет реже 12% от самого частого в стадии — случайная покупка
MIN_HEROES = 100       # меньше героев со сборками — считаем, что API отдал мусор, и файл не трогаем


def read_rows(name, min_fields):
    rows = []
    with open(os.path.join(RES, name), encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split("|")
            if len(parts) >= min_fields:
                rows.append(parts)
    return rows


def get_json(path, cache_dir, cache_name):
    if cache_dir:
        p = os.path.join(cache_dir, cache_name)
        if not os.path.exists(p):
            return None
        with open(p, encoding="utf-8") as f:
            return json.load(f)
    last = None
    for attempt in range(5):
        try:
            req = urllib.request.Request(API + path, headers={"User-Agent": "ROSHIQ-data-update (github.com/baramobatare-rgb/ROSHIQ)"})
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            last = e
            if e.code == 404:
                return None
            time.sleep(15 if e.code == 429 else 5 * (attempt + 1))
        except Exception as e:  # сеть, таймаут, битый JSON
            last = e
            time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"OpenDota не ответил на {path}: {last}")


def all_components(key, consts, seen=None):
    """Все компоненты предмета, включая вложенные (ключи item_xxx)."""
    seen = seen or set()
    for c in (consts.get(key, {}).get("components") or []):
        ck = "item_" + c if not c.startswith("item_") else c
        if ck not in seen:
            seen.add(ck)
            all_components(ck, consts, seen)
    return seen


def build_for(hero_positions, raw, known, consts):
    """raw: ответ itemPopularity. Возвращает {pos: {phase: [keys]}} или {} если данных мало."""
    counts = {}  # phase -> {key: count}
    for phase, field, _ in PHASES:
        counts[phase] = {}
        for item_id, n in (raw.get(field) or {}).items():
            key = consts["__by_id"].get(str(item_id))
            if key and key in known and isinstance(n, int) and n > 0:
                counts[phase][key] = counts[phase].get(key, 0) + n
    if sum(counts["mid"].values()) < MIN_SAMPLE:
        return {}

    total = {}
    for phase in counts:
        for k, n in counts[phase].items():
            total[k] = total.get(k, 0) + n
    # компонент убираем, если на герое собирают предмет из него хотя бы в трети случаев
    dropped = set()
    for big, n_big in total.items():
        for comp in all_components(big, consts):
            if comp in total and n_big >= total[comp] / 3:
                dropped.add(comp)
    # каждый предмет — в стадии, где его покупают чаще всего (при равенстве — в более ранней); стартовые считаем отдельно
    best_phase = {}
    for phase in ("early", "mid", "late"):
        for k, n in counts[phase].items():
            if k not in best_phase or n > counts[best_phase[k]][k]:
                best_phase[k] = phase

    result = {}
    for pos in hero_positions:
        per_phase = {}
        used = set()
        boots = [False]
        for phase, _, size in PHASES:
            pool = counts[phase]
            if not pool:
                continue
            top = max(pool.values())

            def ok(k, strict=True):
                item = known[k]
                if phase == "start":
                    if pos <= 3 and k in SUPPORT_START:
                        return False
                    # стартовая закупка: то, что в базе ROSHIQ помечено стартовым и укладывается в стартовое золото
                    return item["phase"] == "start" and consts.get(k, {}).get("cost", 0) <= 600
                if k in CONSUMABLES or k in dropped or k in used or best_phase.get(k) != phase:
                    return False
                if item["catalog"] and not consts.get(k, {}).get("created"):
                    return False                                       # голый компонент из каталога
                return not strict or item["positions"] is None or pos in item["positions"]

            ranked = sorted(pool.items(), key=lambda kv: (-kv[1], kv[0]))

            def take(strict):
                out = []
                for k, n in ranked:
                    if len(out) >= size or n < max(2, top * MIN_SHARE):
                        break
                    if not ok(k, strict):
                        continue
                    if known[k]["group"] == "boots":
                        if boots[0]:
                            continue            # в сборке одна пара сапог — первая, которую покупают
                        boots[0] = True
                    out.append(k)
                return out

            had_boots = boots[0]
            picks = take(True)
            if phase != "start" and len(picks) < 2:      # фильтр роли слишком строг для этого героя — ослабляем
                boots[0] = had_boots
                picks = take(False)
            if phase == "start" and pos == 5 and "item_ward_observer" in known and "item_ward_observer" not in picks:
                picks = (["item_ward_observer"] + picks)[:size]   # пятёрка всегда начинает с обсервера
            if picks:
                per_phase[phase] = picks
                if phase != "start":
                    used.update(picks)
        if "mid" in per_phase or "late" in per_phase:
            result[pos] = per_phase
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", help="папка с сохранёнными ответами API (items.json, hero_<id>.json)")
    ap.add_argument("--out", help="куда писать hero_builds.txt (по умолчанию — в ресурсы)")
    ap.add_argument("--date", help="дата для dataVersion, ГГГГ-ММ-ДД (по умолчанию — сегодня по UTC)")
    args = ap.parse_args()

    heroes = [(int(r[0]), r[1], [int(p) for p in r[5].split(",") if p.strip().isdigit()]) for r in read_rows("heroes.txt", 6)]
    known = {}
    for r in read_rows("items.txt", 6):
        pos = None if r[5].strip() in ("*", "", "-") else {int(p) for p in r[5].split(",") if p.strip().isdigit()}
        known[r[0]] = {"catalog": r[4] == "catalog", "positions": pos, "phase": r[3], "group": r[4]}

    items = get_json("/constants/items", args.cache, "items.json")
    if not items:
        sys.exit("нет справочника предметов OpenDota")
    consts = {"item_" + k: v for k, v in items.items()}
    consts["__by_id"] = {str(v.get("id")): "item_" + k for k, v in items.items() if v.get("id") is not None}

    lines, built, skipped = [], 0, []
    for hero_id, key, positions in sorted(heroes):
        raw = get_json(f"/heroes/{hero_id}/itemPopularity", args.cache, f"hero_{hero_id}.json")
        if not args.cache:
            time.sleep(1.2)                      # бесплатный лимит OpenDota — 60 запросов в минуту
        builds = build_for(positions, raw, known, consts) if raw else {}
        if not builds:
            skipped.append(key)
            continue
        built += 1
        for pos in positions:
            for phase, _, _ in PHASES:
                picks = builds.get(pos, {}).get(phase)
                if picks:
                    lines.append(f"{key}|{pos}|{phase}|{','.join(picks)}")

    if args.cache and built < len([h for h in heroes if os.path.exists(os.path.join(args.cache, f'hero_{h[0]}.json'))]):
        print("внимание: не для всех героев из кэша получилась сборка", file=sys.stderr)
    if not args.cache and built < MIN_HEROES:
        sys.exit(f"сборки получились только для {built} героев (нужно не меньше {MIN_HEROES}) — файл не изменён")
    bad = [l for l in lines if any(k not in known for k in l.split("|")[3].split(","))]
    if bad:
        sys.exit("в результате неизвестные предметы: " + bad[0])

    day = args.date or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    header = [
        "# Базовые сборки героев по ролям (справочные, без учёта противника).",
        "# Формат: hero|позиция|фаза|предметы через запятую.",
        "# Файл создаётся автоматически: tools/update_builds.py, раз в неделю (.github/workflows/update-builds.yml).",
        "# Источник — открытые данные OpenDota (api.opendota.com, heroes/{id}/itemPopularity): самые частые покупки",
        "# на герое по стадиям игры в профессиональных матчах. Это популярность, а не процент побед.",
        f"# Обновлено {day}. Героев со сборкой: {built}." + (f" Мало данных: {', '.join(skipped)}." if skipped else ""),
    ]
    text = "\n".join(header + lines) + "\n"
    out = args.out or os.path.join(RES, "hero_builds.txt")
    old = ""
    if os.path.exists(out):
        with open(out, encoding="utf-8-sig") as f:
            old = f.read().replace("\r\n", "\n")
    body = lambda t: [l for l in t.split("\n") if l and not l.startswith("#")]
    if body(old) == body(text):
        print(f"сборки не изменились ({built} героев)")
        return
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"обновлено: {built} героев, {len(lines)} строк; без данных: {len(skipped)}")

    if not args.out:       # новая версия данных, чтобы установленные программы забрали файл с GitHub
        vp = os.path.join(RES, "data_version.txt")
        with open(vp, encoding="utf-8-sig") as f:
            v = f.read().replace("\r\n", "\n").split("\n")
        stamp = day.replace("-", ".")
        cur = next((l.split("=", 1)[1] for l in v if l.startswith("dataVersion=")), "")
        if cur.startswith(stamp):   # второй запуск за день: 2026.11.11 → 2026.11.11.1
            tail = cur[len(stamp):].lstrip(".")
            stamp = f"{stamp}.{int(tail or 0) + 1}"
        v = [("dataVersion=" + stamp) if l.startswith("dataVersion=") else l for l in v]
        v = [("patchBaseline=" + l.split("=", 1)[1].split(" (")[0] + " (id и атрибуты сверены с OpenDota; сборки — популярные покупки по данным OpenDota)")
             if l.startswith("patchBaseline=") else l for l in v]
        with open(vp, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(v))
        print("dataVersion=" + stamp)


if __name__ == "__main__":
    main()
