'use strict';

/**
 * 腾讯云 SCF（或任何「事件 → 返回值」型函数平台）适配入口。
 *
 * 推荐用「函数 URL / API 网关 + 集成响应」直连，函数里这样配：
 *   执行方法：scf.handler
 *   API 网关触发：打开「集成响应」，透传 statusCode / headers / body
 *
 * 数据落在函数实例的本地磁盘上，实例回收就没了——正式环境请把
 * server/api.js 里的 readDayStore / writeDayStore 换成云数据库、COS 或 MySQL。
 * 这里给的是「能立刻跑通链路」的最小实现。
 */

const { handleRequest } = require('./api');

function toQuery(event) {
  const query = {};

  if (event.queryStringParameters && typeof event.queryStringParameters === 'object') {
    Object.keys(event.queryStringParameters).forEach((key) => {
      query[key] = event.queryStringParameters[key];
    });
  }

  if (event.queryString && typeof event.queryString === 'object') {
    Object.keys(event.queryString).forEach((key) => {
      query[key] = event.queryString[key];
    });
  }

  return query;
}

function toMethod(event) {
  if (event.httpMethod) return String(event.httpMethod).toUpperCase();
  if (event.requestContext && event.requestContext.http && event.requestContext.http.method) {
    return String(event.requestContext.http.method).toUpperCase();
  }
  return 'GET';
}

function toPath(event) {
  if (event.rawPath) return event.rawPath;               // API 网关 v2 / 函数 URL
  if (event.path) return event.path;                     // API 网关 v1
  if (event.requestContext && event.requestContext.path) return event.requestContext.path;
  return '/';
}

function toBody(event) {
  if (typeof event.body !== 'string') return '';
  if (event.isBase64Encoded) return Buffer.from(event.body, 'base64').toString('utf8');
  return event.body;
}

exports.handler = async function handler(event) {
  const result = handleRequest({
    method: toMethod(event || {}),
    pathname: toPath(event || {}),
    query: toQuery(event || {}),
    body: toBody(event || {}),
  });

  return {
    statusCode: result.statusCode,
    headers: result.headers,
    body: result.body,
    isBase64Encoded: false,
  };
};
