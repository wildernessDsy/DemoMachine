# Machine Time Demo Database

生成时间：2026-11-03T18:47:36
随机种子：20261103

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
