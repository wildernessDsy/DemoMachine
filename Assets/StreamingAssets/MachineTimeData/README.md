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

---

## 动态保存（dsy-new 起）

上面这套 CSV / SQLite 是**静态基线**：打包进 WebGL 之后就不会再变。
从 `dsy-new` 分支开始，演示过程中真正跑出来的实时参数会被单独采样保存，
下次打开同一个站点会**优先读当天存档**，静态演示就变成了动态演示。

### 读数优先级

```
当天本机存档（演示时存下来的） → 原始 machine_parameters.csv → 内置兜底最小值
```

想强制回到“纯静态”的效果：取消勾选 `MachineTimeController` 的 `preferSavedData`，
或者点面板上的「清空今日」。

### 相关文件

| 文件 | 作用 |
|---|---|
| `Assets/Scripts/MachineDayStore.cs` | 存档结构 + 本地持久化（PlayerPrefs；WebGL 下就是浏览器 IndexedDB） |
| `Assets/Scripts/MachineDataRecorder.cs` | 每 0.25 真实秒采样一次，按 15 分钟时段做加权平均，定期落盘 |
| `Assets/Scripts/MachineDataPanel.cs` | 左上角「实时参数保存」面板（`M` 键开关） |
| `Assets/Scripts/MachineCloudSync.cs` | 可选的云端上传（`?api=` 参数或 `SetCloudEndpoint`） |
| `Assets/Scripts/MachineDataDownload.cs` | 导出当日 JSON |
| `Tools/DynamicSite/` | 可选后端接口 + 网页查看页 |

### 存档键名

```
dsy.machine.day.<yyyy-MM-dd>.<machineIndex>   # 一台机器一天的 JSON
dsy.machine.day.index                         # 有数据的日期列表
dsy.cloud.baseUrl                             # 可选：云端接口地址
```

默认保留最近 7 天（`MachineDayStore.MaxDaysRetained`），避免吃满 WebGL 下
PlayerPrefs 的 1MB 配额。

### 字段说明

| 字段 | 单位 | 说明 |
|---|---|---|
| `slotIndex` / `timeSlot` | - | 0..95 / `00:00`…`23:45` |
| `worktableSpeedMms` | mm/s | 该时段内工作台速度的加权平均 |
| `spindleSpeedMms` | mm/s | 主轴往复平均速度 |
| `drillRpm` | RPM | 钻头转速 |
| `machineState` | - | idle / warmup / running / paused / maintenance |
| `sampleSeconds` | 秒 | 该时段的采样权重（真实时长） |
| `sampleCount` | 次 | 该时段被采样了几次 |
| `manual` | - | 演示时用户是否手动拖过滑块 |
| `updatedAt` | - | 最后一次刷新的真实时间（`HH:mm:ss`） |

