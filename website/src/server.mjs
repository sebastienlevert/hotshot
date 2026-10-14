import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const output = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const base = process.env.HOTSHOT_SITE_BASE ?? '/hotshot/';
const port = Number(process.env.PORT ?? 4173);
if (!/^\/(?:[\w-]+\/)*$/.test(base)) throw new Error('Unsupported site base path.');
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('PORT must be between 1 and 65535.');
const mime = { '.html': 'text/html; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.xml': 'application/xml; charset=utf-8', '.txt': 'text/plain; charset=utf-8', '.png': 'image/png',
  '.mp4': 'video/mp4', '.vtt': 'text/vtt; charset=utf-8' };

http.createServer(async (request, response) => {
  const fail = async status => {
    response.writeHead(status, { 'Content-Type': mime['.html'] });
    response.end(request.method === 'HEAD' ? '' : await fs.readFile(path.join(output, '404.html')));
  };
  try {
    if (!['GET', 'HEAD'].includes(request.method)) {
      response.writeHead(405, { Allow: 'GET, HEAD' }); response.end(); return;
    }
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    if (base !== '/' && pathname === base.slice(0, -1)) {
      response.writeHead(308, { Location: base }); response.end(); return;
    }
    if (!pathname.startsWith(base)) { await fail(404); return; }
    const relative = pathname.slice(base.length);
    const file = path.resolve(output, relative, pathname.endsWith('/') ? 'index.html' : '');
    if (!file.startsWith(`${output}${path.sep}`)) { await fail(404); return; }
    let bytes;
    try { bytes = await fs.readFile(file); }
    catch (error) {
      if (['ENOENT', 'EISDIR'].includes(error.code)) { await fail(404); return; }
      throw error;
    }
    const headers = { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream',
      'Content-Length': bytes.length, 'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-cache' };
    if (path.extname(file) === '.mp4') {
      headers['Accept-Ranges'] = 'bytes';
      if (request.method === 'GET' && request.headers.range) {
        const range = /^bytes=(\d*)-(\d*)$/.exec(request.headers.range);
        const suffix = range?.[1] === '' ? Number(range[2]) : null;
        const start = suffix !== null ? Math.max(0, bytes.length - suffix) : Number(range?.[1]);
        const end = suffix !== null || range?.[2] === '' ? bytes.length - 1 : Math.min(Number(range?.[2]), bytes.length - 1);
        if (!range || range[1] === '' && range[2] === '' || suffix !== null && (!Number.isSafeInteger(suffix) || suffix <= 0) ||
            !Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || start >= bytes.length || end < start) {
          response.writeHead(416, { ...headers, 'Content-Length': 0, 'Content-Range': `bytes */${bytes.length}` });
          response.end(); return;
        }
        response.writeHead(206, { ...headers, 'Content-Length': end - start + 1, 'Content-Range': `bytes ${start}-${end}/${bytes.length}` });
        response.end(bytes.subarray(start, end + 1)); return;
      }
    }
    response.writeHead(200, headers);
    response.end(request.method === 'HEAD' ? '' : bytes);
  } catch (error) {
    console.error('Site preview request failed:', error);
    response.writeHead(error instanceof URIError ? 400 : 500, { 'Content-Type': 'text/plain' });
    response.end('The requested preview could not be served.');
  }
}).listen(port, '127.0.0.1', () => console.log(`Hotshot preview: http://127.0.0.1:${port}${base}`));
