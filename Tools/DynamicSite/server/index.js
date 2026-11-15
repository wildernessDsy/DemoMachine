'use strict';

/**
 * 本地 / 服务器上直接跑的入口：
 *   node index.js
 * 默认监听 8787，同时提供：
 *   - /api/*      当日实时参数接口
 *   - /           仓库里的查看页（Tools/DynamicSite/web/index.html）
 *
 * 环境变量：
 *   PORT      默认 8787
 *   DATA_DIR  默认 ./data
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const { URL } = require('url');
const { handleRequest, DATA_DIR } = require('./api');

const PORT = Number(process.env.PORT || 8787);
const WEB_DIR = path.join(__dirname, '..', 'web');
const MAX_BODY_BYTES = 8 * 1024 * 1024;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

function readBody(req, callback) {
  const chunks = [];
  let size = 0;
  let aborted = false;

  req.on('data', (chunk) => {
    if (aborted) return;
    size += chunk.length;
    if (size > MAX_BODY_BYTES) {
      aborted = true;
      callback(new Error('请求体过大'));
      return;
    }
    chunks.push(chunk);
  });

  req.on('end', () => {
    if (aborted) return;
    callback(null, Buffer.concat(chunks).toString('utf8'));
  });

  req.on('error', (err) => {
    if (aborted) return;
    aborted = true;
    callback(err);
  });
}

function serveStatic(pathname, res) {
  const relative = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
  const target = path.join(WEB_DIR, relative);

  // 防止 ../ 穿越
  if (!target.startsWith(WEB_DIR)) {
    res.writeHead(403).end('forbidden');
    return;
  }

  fs.readFile(target, (err, data) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }).end('not found');
      return;
    }

    res.writeHead(200, {
      'Content-Type': MIME[path.extname(target).toLowerCase()] || 'application/octet-stream',
      'Cache-Control': 'no-cache',
    });
    res.end(data);
  });
}

const server = http.createServer((req, res) => {
  const parsed = new URL(req.url, 'http://' + (req.headers.host || 'localhost'));

  if (!parsed.pathname.startsWith('/api/')) {
    serveStatic(parsed.pathname, res);
    return;
  }

  readBody(req, (err, body) => {
    if (err) {
      res.writeHead(413, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({ ok: false, error: err.message }));
      return;
    }

    const query = {};
    parsed.searchParams.forEach((value, key) => {
      query[key] = value;
    });

    const result = handleRequest({
      method: req.method,
      pathname: parsed.pathname,
      query: query,
      body: body,
    });

    res.writeHead(result.statusCode, result.headers);
    res.end(result.body || '');
  });
});

server.listen(PORT, () => {
  console.log('DemoMachine 当日实时参数接口已启动');
  console.log('  接口:   http://127.0.0.1:' + PORT + '/api/days');
  console.log('  查看页: http://127.0.0.1:' + PORT + '/');
  console.log('  数据目录: ' + DATA_DIR);
  console.log('');
  console.log('把这个地址填进 WebGL 页面参数即可开始上传，例如：');
  console.log('  http://你的静态站地址/?api=http://127.0.0.1:' + PORT);
});
