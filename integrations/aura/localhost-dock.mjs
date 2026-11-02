// Server-side only. Import from Aura's Node server; never put discovery credentials in a client bundle.
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import http from 'node:http';

/** Read Dock's current status. No control methods, subprocesses, redirects, DNS, or configurable remote hosts. */
export async function readLocalhostDockStatus({ home = process.env.LOCALHOST_DOCK_HOME ??
  join(process.env.APPDATA ?? '', 'LocalhostDock'), timeoutMs = 3000 } = {}) {
  let endpoint;
  try {
    const data = await readFile(join(home, 'aura-endpoint.json'), 'utf8');
    if (data.length > 4096) throw new Error();
    endpoint = JSON.parse(data);
  } catch {
    throw new Error('Localhost Dock status is unavailable. Enable Aura status in the Dock tray settings.');
  }
  let url;
  try { url = new URL(endpoint.url); } catch { throw new Error('Invalid Localhost Dock endpoint.'); }
  if (endpoint.schema !== 'localhost-dock/aura-endpoint@1' || url.protocol !== 'http:' ||
      url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/v1/status' || url.search ||
      url.hash || url.username || url.password || !/^[A-F0-9]{64}$/.test(endpoint.token)) {
    throw new Error('Invalid Localhost Dock endpoint.');
  }
  return new Promise((resolve, reject) => {
    // node:http deliberately bypasses HTTP_PROXY / fetch global dispatchers; the credential stays on loopback.
    const request = http.get(url, { headers: { Authorization: `Bearer ${endpoint.token}` }, agent: false }, response => {
      if (response.statusCode !== 200) {
        response.resume();
        request.destroy();
        return reject(new Error(`Localhost Dock status is unavailable (HTTP ${response.statusCode}).`));
      }
      let size = 0;
      const parts = [];
      response.on('data', chunk => {
        size += chunk.length;
        if (size > 1024 * 1024) { request.destroy(); reject(new Error('Localhost Dock status is too large.')); }
        else parts.push(chunk);
      });
      response.on('error', () => reject(new Error('Localhost Dock status connection failed.')));
      response.on('end', () => {
        try {
          const status = JSON.parse(Buffer.concat(parts).toString('utf8'));
          const age = Date.now() - Date.parse(status.generatedAt);
          if (status.schema !== 'localhost-dock/aura-status@1' || !Array.isArray(status.services) ||
              !Array.isArray(status.workspaces) || !Number.isFinite(age) || age < -5000 || age > 15000) throw new Error();
          resolve(status);
        } catch { reject(new Error('Localhost Dock returned invalid or stale status.')); }
      });
    });
    const deadline = setTimeout(() => request.destroy(new Error('deadline')), Math.min(10000, Math.max(100, timeoutMs)));
    request.on('error', () => reject(new Error('Localhost Dock status connection failed.')));
    request.on('close', () => clearTimeout(deadline));
  });
}

// Optional read-only diagnostic: node integrations/aura/localhost-dock.mjs
if (process.argv[1] && import.meta.url === (await import('node:url')).pathToFileURL(process.argv[1]).href) {
  try { console.log(JSON.stringify(await readLocalhostDockStatus(), null, 2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
