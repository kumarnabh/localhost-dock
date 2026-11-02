import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { resolve, join, sep } from 'node:path';
import { readLocalhostDockStatus } from './localhost-dock.mjs';

const token = 'A'.repeat(64);
const status = () => ({ schema: 'localhost-dock/aura-status@1', generatedAt: new Date().toISOString(), services: [], workspaces: [] });

async function fixture(t, handler) {
  const root = resolve('.artifacts', 'aura-adapter');
  await mkdir(root, { recursive: true });
  const home = await mkdtemp(join(root, 'run-'));
  const server = createServer(handler);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}/v1/status`;
  const endpoint = { schema: 'localhost-dock/aura-endpoint@1', url, token };
  const save = value => writeFile(join(home, 'aura-endpoint.json'), JSON.stringify(value));
  await save(endpoint);
  t.after(async () => {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    assert.ok(resolve(home).startsWith(root + sep));
    await rm(home, { recursive: true });
  });
  return { home, endpoint, save };
}

test('reads authenticated status using only GET and never returns endpoint credentials', async t => {
  const f = await fixture(t, (request, response) => {
    assert.equal(request.method, 'GET');
    assert.equal(request.url, '/v1/status');
    assert.equal(request.headers.authorization, `Bearer ${token}`);
    response.end(JSON.stringify(status()));
  });
  const result = await readLocalhostDockStatus({ home: f.home });
  assert.equal(result.schema, 'localhost-dock/aura-status@1');
  assert.ok(!JSON.stringify(result).includes(token));
});

test('rejects remote endpoints, alternate loopbacks, credentials, and query strings before making requests', async t => {
  let calls = 0;
  const f = await fixture(t, (_, response) => { calls++; response.end(); });
  for (const url of ['https://example.com/v1/status', 'http://127.0.0.2:4780/v1/status',
    'http://localhost:4780/v1/status', 'http://127.0.0.1:4780/v1/status?token=abc',
    'http://user:pass@127.0.0.1:4780/v1/status']) {
    await f.save({ ...f.endpoint, url });
    await assert.rejects(readLocalhostDockStatus({ home: f.home }), /Invalid Localhost Dock endpoint/);
  }
  assert.equal(calls, 0);
});

test('never follows redirects or sends credentials to redirect targets', async t => {
  let redirected = 0;
  const target = await fixture(t, (_, response) => { redirected++; response.end(); });
  const f = await fixture(t, (_, response) => {
    response.writeHead(302, { Location: target.endpoint.url }); response.end();
  });
  await assert.rejects(readLocalhostDockStatus({ home: f.home }), /HTTP 302/);
  assert.equal(redirected, 0);
});

test('expired and incompatible snapshots are not live status', async t => {
  let data = { ...status(), generatedAt: new Date(Date.now() - 60000).toISOString() };
  const f = await fixture(t, (_, response) => response.end(JSON.stringify(data)));
  await assert.rejects(readLocalhostDockStatus({ home: f.home }), /invalid or stale/);
  data = { ...status(), schema: 'unknown' };
  await assert.rejects(readLocalhostDockStatus({ home: f.home }), /invalid or stale/);
});

test('bounded response and deadline failures contain no bearer token', async t => {
  const large = await fixture(t, (_, response) => response.end('x'.repeat(1024 * 1024 + 1)));
  await assert.rejects(readLocalhostDockStatus({ home: large.home }), /too large/);
  const hang = await fixture(t, () => {});
  await assert.rejects(readLocalhostDockStatus({ home: hang.home, timeoutMs: 100 }), error =>
    !error.message.includes(token) && /connection failed/.test(error.message));
});
