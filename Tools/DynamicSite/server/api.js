'use strict';

/**
 * DemoMachine 当日实时参数接口（零依赖 Node.js 实现）。
 *
 * 数据形状与 Unity 侧 MachineSavedDay / MachineSavedSlot 的 JsonUtility 输出完全一致：
 *
 *   {
 *     "schemaVersion": 1,
 *     "date": "2026-11-16",
 *     "machineIndex": 0,
 *     "machineName": "机器 1",
 *     "createdAt": "2026-11-16 18:40:00",
 *     "updatedAt": "2026-11-16 18:45:12",
 *     "runSeconds": 41.7,
 *     "sampleCount": 168,
 *     "uploaded": true,
 *     "uploadedAt": "2026-11-16 18:45:13",
 *     "slots": [
 *       {
 *         "slotIndex": 44,
 *         "timeSlot": "11:00",
 *         "worktableSpeedMms": 96.4,
 *         "spindleSpeedMms": 312.5,
 *         "drillRpm": 88.0,
 *         "machineState": "running",
 *         "sampleSeconds": 1.51,
 *         "sampleCount": 6,
 *         "manual": false,
 *         "updatedAt": "18:45:12"
 *       }
 *     ]
 *   }
 *
 * 存储：DATA_DIR 下一天一个 JSON 文件，内容为 { date, machines: { "0": <day> } }。
 * 想换成 MySQL / MongoDB / 云开发数据库，只要替换下面 readDayStore / writeDayStore 两个函数。
 */

const fs = require('fs');
const path = require('path');

const DATA_DIR = process.env.DATA_DIR || path.join(__dirname, 'data');
const MAX_SLOTS = 96;
const MAX_MACHINES = 8;

// ─────────────────────────────── 存储层 ───────────────────────────────

function ensureDataDir() {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
  } catch (err) {
    if (err.code !== 'EEXIST') throw err;
  }
}

function nowStamp() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  return (
    d.getFullYear() +
    '-' + pad(d.getMonth() + 1) +
    '-' + pad(d.getDate()) +
    ' ' + pad(d.getHours()) +
    ':' + pad(d.getMinutes()) +
    ':' + pad(d.getSeconds())
  );
}

function isValidDate(value) {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);
}

function dayFilePath(date) {
  return path.join(DATA_DIR, date + '.json');
}

function emptyStore(date) {
  return { date: date, machines: {} };
}

function readDayStore(date) {
  try {
    const raw = fs.readFileSync(dayFilePath(date), 'utf8');
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return emptyStore(date);
    if (!parsed.machines || typeof parsed.machines !== 'object') parsed.machines = {};
    parsed.date = date;
    return parsed;
  } catch (err) {
    if (err.code !== 'ENOENT') {
      console.warn('[store] 读取失败 ' + date + '：' + err.message);
    }
    return emptyStore(date);
  }
}

function writeDayStore(date, store) {
  ensureDataDir();
  const target = dayFilePath(date);
  const temp = target + '.tmp';
  fs.writeFileSync(temp, JSON.stringify(store, null, 2), 'utf8');
  fs.renameSync(temp, target);
}

function listDates() {
  ensureDataDir();
  let entries = [];
  try {
    entries = fs.readdirSync(DATA_DIR);
  } catch (err) {
    return [];
  }

  return entries
    .filter((name) => /^\d{4}-\d{2}-\d{2}\.json$/.test(name))
    .map((name) => name.slice(0, 10))
    .sort()
    .reverse();
}

// ─────────────────────────────── 数据清洗 ───────────────────────────────

function toNumber(value, fallback) {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

function round1(value) {
  return Math.round(value * 10) / 10;
}

function normalizeSlot(slot, index) {
  const slotIndex = Math.min(MAX_SLOTS - 1, Math.max(0, Math.round(toNumber(slot.slotIndex, index))));
  const minutes = slotIndex * 15;
  const fallbackLabel =
    String(Math.floor(minutes / 60)).padStart(2, '0') + ':' + String(minutes % 60).padStart(2, '0');

  return {
    slotIndex: slotIndex,
    timeSlot: typeof slot.timeSlot === 'string' && slot.timeSlot ? slot.timeSlot : fallbackLabel,
    worktableSpeedMms: round1(toNumber(slot.worktableSpeedMms, 0)),
    spindleSpeedMms: round1(toNumber(slot.spindleSpeedMms, 0)),
    drillRpm: round1(toNumber(slot.drillRpm, 0)),
    machineState: typeof slot.machineState === 'string' && slot.machineState ? slot.machineState : 'idle',
    sampleSeconds: round1(toNumber(slot.sampleSeconds, 0)),
    sampleCount: Math.max(0, Math.round(toNumber(slot.sampleCount, 0))),
    manual: slot.manual === true,
    updatedAt: typeof slot.updatedAt === 'string' ? slot.updatedAt : '',
  };
}

function normalizeDay(input) {
  if (!input || typeof input !== 'object') return null;

  const date = input.date;
  if (!isValidDate(date)) return null;

  const machineIndex = Math.round(toNumber(input.machineIndex, -1));
  if (machineIndex < 0 || machineIndex >= MAX_MACHINES) return null;

  const slots = Array.isArray(input.slots) ? input.slots : [];
  const seen = new Set();
  const cleanSlots = [];

  for (let i = 0; i < slots.length; i += 1) {
    const slot = normalizeSlot(slots[i] || {}, i);
    if (seen.has(slot.slotIndex)) continue;
    seen.add(slot.slotIndex);
    cleanSlots.push(slot);
  }

  cleanSlots.sort((a, b) => a.slotIndex - b.slotIndex);

  const filled = cleanSlots.filter((s) => s.sampleCount > 0).length;

  return {
    schemaVersion: 1,
    date: date,
    machineIndex: machineIndex,
    machineName:
      typeof input.machineName === 'string' && input.machineName
        ? input.machineName
        : '机器 ' + (machineIndex + 1),
    createdAt: typeof input.createdAt === 'string' && input.createdAt ? input.createdAt : nowStamp(),
    updatedAt: nowStamp(),
    runSeconds: round1(toNumber(input.runSeconds, 0)),
    sampleCount: Math.max(0, Math.round(toNumber(input.sampleCount, 0))),
    uploaded: true,
    uploadedAt: nowStamp(),
    filledSlots: filled,
    slots: cleanSlots,
  };
}

// ─────────────────────────────── 业务接口 ───────────────────────────────

function saveDays(input) {
  const list = Array.isArray(input) ? input : Array.isArray(input && input.days) ? input.days : [input];
  let saved = 0;
  const touched = new Set();

  for (let i = 0; i < list.length; i += 1) {
    const day = normalizeDay(list[i]);
    if (!day) continue;

    const store = readDayStore(day.date);
    store.machines[String(day.machineIndex)] = day;
    store.date = day.date;
    writeDayStore(day.date, store);

    saved += 1;
    touched.add(day.date);
  }

  return { saved: saved, dates: Array.from(touched) };
}

function summarizeDate(date) {
  const store = readDayStore(date);
  const machines = [];

  Object.keys(store.machines)
    .map((key) => Number(key))
    .filter((index) => Number.isInteger(index) && index >= 0 && index < MAX_MACHINES)
    .sort((a, b) => a - b)
    .forEach((index) => {
      const day = store.machines[String(index)];
      if (!day || !Array.isArray(day.slots)) return;
      const filled = day.slots.filter((slot) => slot.sampleCount > 0).length;
      machines.push({
        machineIndex: index,
        machineName: day.machineName || '机器 ' + (index + 1),
        filledSlots: filled,
        sampleCount: day.sampleCount || 0,
        runSeconds: day.runSeconds || 0,
        updatedAt: day.updatedAt || '',
        uploadedAt: day.uploadedAt || '',
      });
    });

  return { date: date, machines: machines };
}

function getDay(date, machineIndex) {
  const store = readDayStore(date);
  return store.machines[String(machineIndex)] || null;
}

// ─────────────────────────────── 路由 ───────────────────────────────

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET,POST,DELETE,OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type,Authorization',
  'Access-Control-Max-Age': '86400',
};

function jsonResponse(statusCode, payload) {
  return {
    statusCode: statusCode,
    headers: Object.assign(
      {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-store',
      },
      CORS_HEADERS
    ),
    body: JSON.stringify(payload),
  };
}

/**
 * 统一的请求处理入口。
 * @param {{method: string, pathname: string, query: Object, body: string}} request
 */
function handleRequest(request) {
  const method = String(request.method || 'GET').toUpperCase();
  const pathname = String(request.pathname || '/');
  const query = request.query || {};

  if (method === 'OPTIONS') {
    return { statusCode: 204, headers: Object.assign({}, CORS_HEADERS), body: '' };
  }

  try {
    if (pathname === '/api/health' && method === 'GET') {
      return jsonResponse(200, {
        ok: true,
        service: 'demomachine-machine-data',
        time: nowStamp(),
        dataDir: DATA_DIR,
        days: listDates().length,
      });
    }

    if (pathname === '/api/days' && method === 'GET') {
      const days = listDates().map(summarizeDate);
      return jsonResponse(200, { ok: true, days: days });
    }

    if (pathname === '/api/day' && method === 'GET') {
      const date = query.date;
      const machine = Math.round(toNumber(query.machine, 0));

      if (!isValidDate(date)) {
        return jsonResponse(400, { ok: false, error: 'date 必须是 yyyy-MM-dd' });
      }

      const day = getDay(date, machine);
      if (!day) {
        return jsonResponse(404, { ok: false, error: '没有找到该机器当天的数据' });
      }

      return jsonResponse(200, day);
    }

    if (pathname === '/api/day' && method === 'POST') {
      let payload;
      try {
        payload = JSON.parse(request.body || '{}');
      } catch (err) {
        return jsonResponse(400, { ok: false, error: '请求体不是合法 JSON：' + err.message });
      }

      const result = saveDays(payload);
      if (result.saved === 0) {
        return jsonResponse(400, { ok: false, error: '没有可保存的数据（检查 date / machineIndex）' });
      }

      return jsonResponse(201, { ok: true, saved: result.saved, dates: result.dates, time: nowStamp() });
    }

    if (pathname === '/api/day' && method === 'DELETE') {
      const date = query.date;
      const machine = Math.round(toNumber(query.machine, -1));

      if (!isValidDate(date)) {
        return jsonResponse(400, { ok: false, error: 'date 必须是 yyyy-MM-dd' });
      }

      const store = readDayStore(date);
      if (machine >= 0) {
        delete store.machines[String(machine)];
      } else {
        store.machines = {};
      }
      writeDayStore(date, store);

      return jsonResponse(200, { ok: true });
    }

    return jsonResponse(404, { ok: false, error: '未知接口 ' + method + ' ' + pathname });
  } catch (err) {
    console.error('[api] 处理失败：', err);
    return jsonResponse(500, { ok: false, error: err.message });
  }
}

module.exports = {
  handleRequest: handleRequest,
  DATA_DIR: DATA_DIR,
};
