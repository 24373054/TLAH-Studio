import http from 'node:http';
import { createReadStream } from 'node:fs';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const publicDir = path.join(__dirname, 'public');
const releaseDir = process.env.RELEASE_DIR || '/var/www/download/tlah/windows';
const host = process.env.HOST || '127.0.0.1';
const port = Number(process.env.PORT || 3131);

const signer = {
  subject: 'Beijing Ke Entropy Technology Co., Ltd. self-signed Authenticode certificate',
  thumbprint: 'F6DC173C746447A05FF83B9F7162121344CC09F0'
};

const mime = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
  ['.ico', 'image/x-icon'],
  ['.sig', 'text/plain; charset=utf-8'],
  ['.exe', 'application/octet-stream']
]);

function send(res, status, body, headers = {}) {
  res.writeHead(status, {
    'Cache-Control': status === 200 ? 'no-cache, must-revalidate' : 'no-store',
    ...headers
  });
  res.end(body);
}

function sendJson(res, status, value) {
  send(res, status, JSON.stringify(value, null, 2), {
    'Content-Type': 'application/json; charset=utf-8'
  });
}

function sizeLabel(bytes) {
  if (!Number.isFinite(bytes)) return null;
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

function formatDate(value) {
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone: 'Asia/Shanghai'
  }).format(value);
}

function safePath(base, requestPath) {
  const decoded = decodeURIComponent(requestPath);
  const normalized = path.normalize(decoded).replace(/^(\.\.[/\\])+/, '');
  const resolved = path.join(base, normalized);
  if (!resolved.startsWith(base)) return null;
  return resolved;
}

async function latestPayload() {
  const latestPath = path.join(releaseDir, 'latest.json');
  const latestRaw = await readFile(latestPath, 'utf8');
  const latest = JSON.parse(latestRaw);
  const installerFile = path.basename(new URL(latest.installerUrl).pathname);
  const installerPath = path.join(releaseDir, installerFile);
  const installerStat = await stat(installerPath);

  return {
    ...latest,
    installerFile,
    downloadUrl: `/files/${installerFile}`,
    manifestUrl: '/files/latest.json',
    signatureUrl: '/files/latest.json.sig',
    size: installerStat.size,
    sizeLabel: sizeLabel(installerStat.size),
    updatedAt: installerStat.mtime.toISOString(),
    updatedAtLabel: formatDate(installerStat.mtime),
    signer
  };
}

async function serveStatic(req, res, pathname) {
  const target = pathname === '/' ? '/index.html' : pathname;
  const filePath = safePath(publicDir, target.slice(1));
  if (!filePath) return send(res, 403, 'Forbidden', { 'Content-Type': 'text/plain; charset=utf-8' });

  try {
    const info = await stat(filePath);
    if (!info.isFile()) throw new Error('Not a file');
    res.writeHead(200, {
      'Content-Type': mime.get(path.extname(filePath)) || 'application/octet-stream',
      'Content-Length': info.size,
      'Cache-Control': filePath.endsWith('index.html') ? 'no-cache, must-revalidate' : 'public, max-age=300'
    });
    createReadStream(filePath).pipe(res);
  } catch {
    send(res, 404, 'Not found', { 'Content-Type': 'text/plain; charset=utf-8' });
  }
}

async function serveReleaseFile(res, pathname) {
  const fileName = pathname.replace(/^\/files\//, '');
  const filePath = safePath(releaseDir, fileName);
  if (!filePath) return send(res, 403, 'Forbidden', { 'Content-Type': 'text/plain; charset=utf-8' });

  try {
    const info = await stat(filePath);
    if (!info.isFile()) throw new Error('Not a file');
    const ext = path.extname(filePath);
    const headers = {
      'Content-Type': mime.get(ext) || 'application/octet-stream',
      'Content-Length': info.size,
      'Cache-Control': ext === '.exe' ? 'public, max-age=3600' : 'no-cache, must-revalidate'
    };
    if (ext === '.exe') {
      headers['Content-Disposition'] = `attachment; filename="${path.basename(filePath)}"`;
    }
    res.writeHead(200, headers);
    createReadStream(filePath).pipe(res);
  } catch {
    send(res, 404, 'Not found', { 'Content-Type': 'text/plain; charset=utf-8' });
  }
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      return send(res, 405, 'Method not allowed', { 'Content-Type': 'text/plain; charset=utf-8' });
    }

    if (url.pathname === '/healthz') {
      return sendJson(res, 200, { ok: true });
    }

    if (url.pathname === '/api/latest') {
      return sendJson(res, 200, await latestPayload());
    }

    if (url.pathname.startsWith('/files/')) {
      return serveReleaseFile(res, url.pathname);
    }

    return serveStatic(req, res, url.pathname);
  } catch (error) {
    console.error(error);
    return sendJson(res, 500, { error: 'Internal server error' });
  }
});

server.listen(port, host, () => {
  console.log(`TLAH release page listening on http://${host}:${port}`);
});
