# 当日实时参数：动态保存（dsy-new 分支）

这个目录是「静态演示网站 → 动态演示网站」的可选后端。**不启动它也能用**：Unity 端会把演示过的当日实时参数存进浏览器本地（IndexedDB），静态托管照样“记得住”。启动它则多一份服务端存档和查看页。

```
Tools/DynamicSite/
├─ server/
│  ├─ api.js        # 路由 + 校验 + 存储（零依赖，换成 MySQL / 云开发数据库只改两个函数）
│  ├─ index.js      # `node index.js` 启动 HTTP 服务，顺带托管 web/ 查看页
│  ├─ scf.js        # 腾讯云 SCF / 函数 URL 的适配入口
│  └─ package.json
├─ web/
│  └─ index.html    # 查看页：按日期 + 机器看 96 个时段的三条参数
└─ README.md
```

---

## 零、站点入口页（WebGL 的 index.html）

演示网站的入口页在 **[Assets/WebGLTemplates/DemoMachine/index.html](../../Assets/WebGLTemplates/DemoMachine/index.html)**，
就是「登录 → 选机 → 进场景 → 返回选择」那一层，额外带了一条**当日参数存档操作条**（保存面板 / 立即保存 / 导出 JSON / 同步云端 / 清空今日）。

它已经设成工程的默认 WebGL 模板（`ProjectSettings.asset` 里 `webGLTemplate: APPLICATION:DemoMachine`），
所以 **Build 出来的 `index.html` 就是这个页面**，不用手动复制。

它自己会找构建产物，按顺序探测：

```
game/Build/WebGLBuild.loader.js      ← 队友现在线上的布局
game/Build/DemoMachine.loader.js
Build/WebGLBuild.loader.js           ← Unity 默认输出目录
Build/DemoMachine.loader.js
```

压缩格式也会自动判断（先 HEAD 探测 `.data.gz`，没有再退回 `.data`），
所以线上那份 gzip 的和本地新构建的未压缩版都能直接用。

可用的 URL 参数：

| 参数 | 作用 |
|---|---|
| `?api=https://你的接口域名` | 配置云端接口，页面就绪后自动推给 Unity |
| `?build=Build/xxx.loader.js` | 手动指定构建入口，跳过自动探测 |
| `?gz=0` / `?gz=1` | 强制不用 / 用 gzip 文件名 |
| `?ready=1000` | 实例就绪后多久显示选机界面（默认 2200ms） |

> ⚠️ 直接双击打开（`file://`）只能看到登录页 —— 浏览器不允许从 `file://`
> 取 wasm/data。页面会自动提示：在该目录执行 `python -m http.server 8000`，
> 然后访问 `http://localhost:8000/index.html`。

---

## 一、三种运行模式

| 模式 | 怎么开 | 数据在哪 | 适合 |
|---|---|---|---|
| **纯本地** | 什么都不用配 | 浏览器 IndexedDB（PlayerPrefs） | 静态托管在 COS / OSS / GitHub Pages，演示完刷新不丢 |
| **本地 + 自建接口** | `node server/index.js`，页面带 `?api=http://127.0.0.1:8787` | 本地 + `server/data/*.json` | 开发、答辩现场 |
| **本地 + 云函数** | 部署 `server/`，页面带 `?api=https://你的域名` | 本地 + 云数据库 | 正式上线的“动态网站” |

Unity 端两种都写：**先保证本地一定存下来**，云端只是多加一层（失败也不影响演示）。

---

## 二、本机跑起来

```bash
cd Tools/DynamicSite/server
node index.js
# 接口:   http://127.0.0.1:8787/api/days
# 查看页: http://127.0.0.1:8787/
```

然后在 WebGL 演示地址后面加参数（也可以直接在演示页里调 `SetCloudEndpoint`）：

```
http://你的静态站地址/?api=http://127.0.0.1:8787
```

演示十几秒后，查看页会出现当天的机器和 96 个时段里的实时参数。

---

## 三、部署成“真的动态网站”

按截图里的方案 A 走：**页面继续放 COS 静态网站，接口单独上云**。

1. **静态部分**：Unity WebGL 构建产物照旧上传 COS，域名、证书不用动。
2. **接口部分**（二选一）：
   - 任意有 Node 的服务器：`npm start`（无需 `npm install`，零依赖），前面挂 Nginx 反代并开 HTTPS。
   - 腾讯云 SCF：把 `server/` 目录传上去，入口设为 `scf.handler`，触发方式选「函数 URL」或「API 网关 + 集成响应」，打开 CORS。
3. **把地址告诉前端**：演示页地址加 `?api=https://你的接口域名`，或调用
   `unityInstance.SendMessage("MachineDataRecorder", "SetCloudEndpoint", "https://你的接口域名")`。
4. **跨域**：`api.js` 已经对所有响应带上 `Access-Control-Allow-Origin: *`，SCF 需要确认网关没有覆盖掉这些响应头。

> ⚠️ SCF 的本地磁盘只在实例存活期间有效。正式环境请把 `api.js` 里的
> `readDayStore` / `writeDayStore` 换成云数据库（云开发数据库、MySQL、COS 都可以），
> 其余路由、校验、字段名都不用动。

---

## 四、接口

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/health` | 健康检查，返回数据目录和已有天数 |
| `GET` | `/api/days` | 有数据的日期 + 每天每台机器的概况 |
| `GET` | `/api/day?date=yyyy-MM-dd&machine=0` | 某天某台机器的完整 96 段存档 |
| `POST` | `/api/day` | 上传存档，body 是单台机器的一天（也接受 `{ "days": [...] }`） |
| `DELETE` | `/api/day?date=yyyy-MM-dd[&machine=0]` | 删除某天（某台机器）的存档 |

`POST` 与 `GET` 的 JSON 字段和 Unity 侧 `MachineSavedDay` / `MachineSavedSlot` 一一对应：

```json
{
  "schemaVersion": 1,
  "date": "2026-11-16",
  "machineIndex": 0,
  "machineName": "机器 1",
  "createdAt": "2026-11-16 18:40:00",
  "updatedAt": "2026-11-16 18:45:12",
  "runSeconds": 41.7,
  "sampleCount": 168,
  "uploaded": true,
  "uploadedAt": "2026-11-16 18:45:13",
  "slots": [
    {
      "slotIndex": 44,
      "timeSlot": "11:00",
      "worktableSpeedMms": 96.4,
      "spindleSpeedMms": 312.5,
      "drillRpm": 88.0,
      "machineState": "running",
      "sampleSeconds": 1.51,
      "sampleCount": 6,
      "manual": false,
      "updatedAt": "18:45:12"
    }
  ]
}
```

---

## 五、排错

| 现象 | 原因 |
|---|---|
| 查看页提示 `Failed to fetch` | 接口没启动，或地址写成 `127.0.0.1` 但页面是 `localhost`（跨域）。统一用同一个写法 |
| 演示面板显示“上传失败 … CORS” | 网关没透传 `Access-Control-Allow-Origin`，或没处理 `OPTIONS` 预检 |
| 演示面板显示“本地模式” | 没有配 `?api=`，属于正常状态：本地存档功能照常工作 |
| 刷新后数据没了 | 无痕模式 / 清了站点数据。PlayerPrefs 存在 IndexedDB，清站点数据会一起清掉 |
| 存档天数不够 | 客户端默认保留最近 7 天（`MachineDayStore.MaxDaysRetained`），避免 WebGL 的 1MB 配额 |
