import csv
import json
import math
import os
import sqlite3
from datetime import datetime

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(PROJECT_ROOT, "Assets", "StreamingAssets", "MachineTimeData")
SEED = 20261103

MIN_WORKTABLE = 5.0
MAX_WORKTABLE = 300.0
MIN_SPINDLE = 10.0
MAX_SPINDLE = 800.0
MIN_DRILL = 0.0
MAX_DRILL = 300.0

BASE_WORKTABLE = 180.0
BASE_SPINDLE = 480.0
BASE_DRILL = 170.0

SLOTS = 96


def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3.0 - 2.0 * t)


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def ramp(i, start, end, a, b):
    """Linear ramp from a at slot start to b at slot end (inclusive)."""
    if end <= start:
        return b
    t = (i - start) / float(end - start)
    return a + (b - a) * clamp(t, 0.0, 1.0)


def state_for(i):
    if 0 <= i <= 20:
        return "idle"
    if 21 <= i <= 29:
        return "warmup"
    if 30 <= i <= 43:
        return "running"
    if 44 <= i <= 51:
        return "paused"
    if 52 <= i <= 59:
        return "warmup"
    if 60 <= i <= 71:
        return "running"
    if 72 <= i <= 79:
        return "paused"
    if 80 <= i <= 87:
        return "idle"
    return "maintenance"


def values_for(i):
    # Idle: minimum values.
    if i <= 20 or i >= 80:
        return MIN_WORKTABLE, MIN_SPINDLE, MIN_DRILL

    # Morning warm-up 05:15-07:15 (slots 21..29)
    if 21 <= i <= 29:
        return (ramp(i, 21, 29, MIN_WORKTABLE, BASE_WORKTABLE),
                ramp(i, 21, 29, MIN_SPINDLE, BASE_SPINDLE),
                ramp(i, 21, 29, MIN_DRILL, BASE_DRILL))

    # Morning running 07:30-10:45 (slots 30..43), ripple zero at both ends.
    if 30 <= i <= 43:
        p = (i - 30) / float(43 - 30)
        ripple = math.sin(math.pi * p)  # 0 at both ends
        return (BASE_WORKTABLE + 14.0 * ripple,
                BASE_SPINDLE + 38.0 * ripple,
                BASE_DRILL + 14.0 * ripple)

    # Lunch ramp-down 11:00-12:45 (slots 44..51)
    if 44 <= i <= 51:
        return (ramp(i, 44, 51, BASE_WORKTABLE, MIN_WORKTABLE),
                ramp(i, 44, 51, BASE_SPINDLE, MIN_SPINDLE),
                ramp(i, 44, 51, BASE_DRILL, MIN_DRILL))

    # Afternoon warm-up 13:00-14:45 (slots 52..59)
    if 52 <= i <= 59:
        return (ramp(i, 52, 59, MIN_WORKTABLE, BASE_WORKTABLE),
                ramp(i, 52, 59, MIN_SPINDLE, BASE_SPINDLE),
                ramp(i, 52, 59, MIN_DRILL, BASE_DRILL))

    # Afternoon running 15:00-17:45 (slots 60..71)
    if 60 <= i <= 71:
        p = (i - 60) / float(71 - 60)
        ripple = math.sin(math.pi * p)
        return (BASE_WORKTABLE - 10.0 * ripple,
                BASE_SPINDLE - 34.0 * ripple,
                BASE_DRILL - 12.0 * ripple)

    # Evening ramp-down 18:00-19:45 (slots 72..79)
    if 72 <= i <= 79:
        return (ramp(i, 72, 79, BASE_WORKTABLE, MIN_WORKTABLE),
                ramp(i, 72, 79, BASE_SPINDLE, MIN_SPINDLE),
                ramp(i, 72, 79, BASE_DRILL, MIN_DRILL))

    return MIN_WORKTABLE, MIN_SPINDLE, MIN_DRILL


def build_rows():
    rows = []
    for i in range(SLOTS):
        minutes = i * 15
        hh = minutes // 60
        mm = minutes % 60
        time_slot = f"{hh:02d}:{mm:02d}"
        w, s, d = values_for(i)
        w = round(clamp(w, MIN_WORKTABLE, MAX_WORKTABLE), 1)
        s = round(clamp(s, MIN_SPINDLE, MAX_SPINDLE), 1)
        d = round(clamp(d, MIN_DRILL, MAX_DRILL), 1)
        rows.append({
            "slot_index": i,
            "time_slot": time_slot,
            "minutes_since_midnight": minutes,
            "worktable_speed_mms": w,
            "spindle_reciprocating_speed_mms": s,
            "drill_rpm": d,
            "machine_state": state_for(i),
        })

    # Rate-limit adjacent differences as a final safety pass.
    limits = {
        "worktable_speed_mms": 25.0,
        "spindle_reciprocating_speed_mms": 60.0,
        "drill_rpm": 25.0,
    }
    for key, lim in limits.items():
        for i in range(1, len(rows)):
            prev = rows[i - 1][key]
            cur = rows[i][key]
            if cur - prev > lim:
                rows[i][key] = round(prev + lim, 1)
            elif prev - cur > lim:
                rows[i][key] = round(prev - lim, 1)

    return rows


def validate(rows):
    assert len(rows) == 96, f"expected 96 rows, got {len(rows)}"
    times = [r["time_slot"] for r in rows]
    minutes = [r["minutes_since_midnight"] for r in rows]
    assert len(set(times)) == 96, "duplicate time slots"
    assert len(set(minutes)) == 96, "duplicate minutes"
    assert times[0] == "00:00" and times[-1] == "23:45"
    for r in rows:
        assert MIN_WORKTABLE <= r["worktable_speed_mms"] <= MAX_WORKTABLE
        assert MIN_SPINDLE <= r["spindle_reciprocating_speed_mms"] <= MAX_SPINDLE
        assert MIN_DRILL <= r["drill_rpm"] <= MAX_DRILL

    limits = {
        "worktable_speed_mms": 25.0,
        "spindle_reciprocating_speed_mms": 60.0,
        "drill_rpm": 25.0,
    }
    for key, lim in limits.items():
        for i in range(1, len(rows)):
            delta = abs(rows[i][key] - rows[i - 1][key])
            assert delta <= lim + 1e-6, f"{key} jump at slot {i}: {delta}"

    # Loop must be seamless in the idle state.
    assert abs(rows[-1]["worktable_speed_mms"] - rows[0]["worktable_speed_mms"]) <= 25
    assert abs(rows[-1]["spindle_reciprocating_speed_mms"] - rows[0]["spindle_reciprocating_speed_mms"]) <= 60
    assert abs(rows[-1]["drill_rpm"] - rows[0]["drill_rpm"]) <= 25


def write_csv(rows, path):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def write_json(rows, path):
    payload = {
        "schema_version": 1,
        "slot_minutes": 15,
        "slots_per_day": 96,
        "units": {
            "worktable_speed_mms": "mm/s",
            "spindle_reciprocating_speed_mms": "mm/s",
            "drill_rpm": "RPM",
        },
        "rows": rows,
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)


def write_sqlite(rows, path):
    if os.path.exists(path):
        os.remove(path)
    con = sqlite3.connect(path)
    try:
        cur = con.cursor()
        cur.executescript("""
CREATE TABLE machine_parameter_slots (
    slot_index INTEGER PRIMARY KEY,
    time_slot TEXT NOT NULL UNIQUE,
    minutes_since_midnight INTEGER NOT NULL UNIQUE,
    worktable_speed_mms REAL NOT NULL,
    spindle_reciprocating_speed_mms REAL NOT NULL,
    drill_rpm REAL NOT NULL,
    machine_state TEXT NOT NULL
);
CREATE TABLE metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
""")
        cur.executemany(
            "INSERT INTO machine_parameter_slots VALUES (?, ?, ?, ?, ?, ?, ?)",
            [(r["slot_index"], r["time_slot"], r["minutes_since_midnight"],
              r["worktable_speed_mms"], r["spindle_reciprocating_speed_mms"],
              r["drill_rpm"], r["machine_state"]) for r in rows],
        )
        meta = {
            "schema_version": "1",
            "slot_minutes": "15",
            "slots_per_day": "96",
            "units": "worktable_speed_mms=mm/s; spindle_reciprocating_speed_mms=mm/s; drill_rpm=RPM",
            "generated_at": datetime.now().isoformat(timespec="seconds"),
            "seed": str(SEED),
            "description": "DemoMachine 24h time-series demo data, 15-minute slots.",
        }
        cur.executemany("INSERT INTO metadata VALUES (?, ?)", list(meta.items()))
        con.commit()
    finally:
        con.close()


def write_sql(rows, path):
    lines = [
        "CREATE TABLE IF NOT EXISTS machine_parameter_slots (",
        "  slot_index INTEGER PRIMARY KEY,",
        "  time_slot TEXT NOT NULL UNIQUE,",
        "  minutes_since_midnight INTEGER NOT NULL UNIQUE,",
        "  worktable_speed_mms REAL NOT NULL,",
        "  spindle_reciprocating_speed_mms REAL NOT NULL,",
        "  drill_rpm REAL NOT NULL,",
        "  machine_state TEXT NOT NULL",
        ");",
        "CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);",
        "",
    ]
    for r in rows:
        lines.append(
            "INSERT INTO machine_parameter_slots VALUES "
            f"({r['slot_index']}, '{r['time_slot']}', {r['minutes_since_midnight']}, "
            f"{r['worktable_speed_mms']}, {r['spindle_reciprocating_speed_mms']}, "
            f"{r['drill_rpm']}, '{r['machine_state']}');"
        )
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))


def write_readme(path, rows):
    text = f"""# Machine Time Demo Database

生成时间：{datetime.now().isoformat(timespec='seconds')}
随机种子：{SEED}

## 文件
- `machine_demo.db`：SQLite 数据库（主数据源）
- `machine_parameters.csv`：Unity 读取用的 CSV（UTF-8）
- `machine_parameters.json`：JSON 版本
- `machine_demo.sql`：建表 + INSERT 脚本
- `README.md`：本文件

## 表结构
`machine_parameter_slots` 一天 96 行，每行 15 分钟：

| 字段 | 类型 | 单位 | 说明 |
|---|---|---|---|
| slot_index | INTEGER | - | 0..95 |
| time_slot | TEXT | - | 00:00 ... 23:45 |
| minutes_since_midnight | INTEGER | 分钟 | 0..1425 |
| worktable_speed_mms | REAL | mm/s | 工作台速度，5–300 |
| spindle_reciprocating_speed_mms | REAL | mm/s | 主轴往复平均速度，10–800 |
| drill_rpm | REAL | RPM | 钻头转速，0–300 |
| machine_state | TEXT | - | idle/warmup/running/paused/maintenance |

`metadata` 表保存 schema 版本、时间粒度、单位、生成时间等。

## Unity 接入建议
1. 把 `machine_demo.db` 或 `machine_parameters.csv` 放到 `Assets/StreamingAssets/MachineTimeData/`。
2. 启动时一次性读取 96 行到内存，按 `slot_index` 建数组。
3. 当前时段：
   `slot = Mathf.FloorToInt((float)(timeOfDay.TotalSeconds / 900.0)) % 96;`
4. 演示时间可以加速（例如 1 现实秒 = 10 模拟分钟），24 小时循环。
5. SQLite 可用 `Mono.Data.Sqlite` 或 `sqlite-net`；如果插件不方便，CSV 同样可用。
6. 手动调节优先：用户拖动滑块时暂停该参数的数据库覆盖（例如 8 秒），之后恢复自动；或在面板上提供“恢复自动”按钮。

## 数据规律
- 00:00–05:00 待机（最小值）
- 05:15–07:15 升温
- 07:30–10:45 加工
- 11:00–12:45 降速/午休
- 13:00–14:45 升温
- 15:00–17:45 加工
- 18:00–19:45 降速
- 20:00–23:45 待机/维护
- 23:45 与 00:00 都是最小值，循环无缝。

相邻 15 分钟最大变化：工作台 ≤25 mm/s，主轴 ≤60 mm/s，钻头 ≤25 RPM。
"""
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


def main():
    os.makedirs(OUT, exist_ok=True)
    rows = build_rows()
    validate(rows)

    write_csv(rows, os.path.join(OUT, "machine_parameters.csv"))
    write_json(rows, os.path.join(OUT, "machine_parameters.json"))
    write_sqlite(rows, os.path.join(OUT, "machine_demo.db"))
    write_sql(rows, os.path.join(OUT, "machine_demo.sql"))
    write_readme(os.path.join(OUT, "README.md"), rows)

    print("OK rows=", len(rows))
    for key in ("worktable_speed_mms", "spindle_reciprocating_speed_mms", "drill_rpm"):
        vals = [r[key] for r in rows]
        max_delta = max(abs(vals[i] - vals[i - 1]) for i in range(1, len(vals)))
        print(f"{key}: min={min(vals)} max={max(vals)} avg={sum(vals)/len(vals):.1f} max_delta={max_delta}")
    print("first:", rows[0])
    print("last:", rows[-1])
    print("files:", os.listdir(OUT))


if __name__ == "__main__":
    main()
