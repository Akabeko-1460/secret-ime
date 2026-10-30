import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, copyFile, writeFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const origin = `chrome-extension://${'a'.repeat(32)}/`;
const stage = path.join(root, 'build', 'test-runs', randomUUID());
await mkdir(stage, { recursive: true });
const executable = path.join(stage, 'SecretIME.Host.exe');
await copyFile(path.join(root, 'build', 'SecretIME.Host.exe'), executable);
await writeFile(path.join(stage, 'host-settings.json'), JSON.stringify({
  automationEnabled: false,
  allowedOrigins: [origin],
}), { flag: 'wx' });

function frame(message) {
  const body = Buffer.from(JSON.stringify(message));
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length);
  return Buffer.concat([header, body]);
}

function host(caller = origin) {
  const child = spawn(executable, [caller], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let pending = Buffer.alloc(0);
  const queue = [];
  const readers = [];
  const done = new Promise(resolve => child.on('exit', (code, signal) => resolve({ code, signal })));
  child.stdout.on('data', chunk => {
    pending = Buffer.concat([pending, chunk]);
    while (pending.length >= 4 && pending.length >= 4 + pending.readUInt32LE(0)) {
      const size = pending.readUInt32LE(0);
      assert.ok(size > 0 && size <= 2048);
      const message = JSON.parse(pending.subarray(4, 4 + size).toString('utf8'));
      pending = pending.subarray(4 + size);
      if (readers.length) readers.shift()(message);
      else queue.push(message);
    }
  });
  return {
    child, done,
    async request(message) {
      const reply = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error('Native reply timed out')), 10000);
        const receive = value => { clearTimeout(timeout); resolve(value); };
        if (queue.length) receive(queue.shift()); else readers.push(receive);
      });
      child.stdin.write(frame(message));
      return reply;
    },
    async close() { child.stdin.end(); return done; },
  };
}

test('real native processes share leases, reject active restore, and exit idle without touching IME', { timeout: 30000 }, async () => {
  const first = host();
  const second = host();
  try {
    const a = await first.request({ v: 1, op: 'sync', active: true });
    assert.equal(a.state, 'not-configured');
    assert.equal(a.activeClients, 1);
    assert.equal(a.modeEnabled, null);
    assert.equal(a.effectiveness, 'unverified');
    const b = await second.request({ v: 1, op: 'sync', active: true });
    assert.equal(b.activeClients, 2);
    assert.deepEqual(await first.close(), { code: 0, signal: null });
    const c = await second.request({ v: 1, op: 'status' });
    assert.equal(c.activeClients, 1, 'First browser exit must preserve the second lease');
    assert.equal(c.state, 'not-configured');
    const restore = await second.request({ v: 1, op: 'restore' });
    assert.equal(restore.activeClients, 1);
    assert.equal(restore.modeEnabled, null);
    const released = await second.request({ v: 1, op: 'sync', active: false });
    assert.equal(released.activeClients, 0);
    assert.deepEqual(await second.close(), { code: 0, signal: null });
    await new Promise(resolve => setTimeout(resolve, 1000));
    assert.equal((await readFile(path.join(stage, 'recovery-state.txt'), 'utf8')).trim(), 'none');
    const processes = execFileSync('powershell.exe', ['-NoProfile', '-Command',
      "@(Get-Process -Name SecretIME.Host -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id) | ConvertTo-Json -Compress"],
    { encoding: 'utf8', windowsHide: true }).trim();
    assert.ok(!processes || processes === '[]', 'No helper process should remain after final disconnect');
  } finally {
    first.child.stdin.end(); second.child.stdin.end();
  }
});

test('unapproved native origin exits without protocol output', { timeout: 5000 }, async () => {
  const rejected = host(`chrome-extension://${'b'.repeat(32)}/`);
  let received = false;
  rejected.child.stdout.on('data', () => { received = true; });
  const exit = await rejected.done;
  assert.equal(exit.code, 2);
  assert.equal(received, false);
});

test('malformed native request cannot start a lease', { timeout: 5000 }, async () => {
  const invalid = host();
  invalid.child.stdin.write(frame({ v: 1, op: 'sync', active: 'true' }));
  assert.equal((await invalid.done).code, 2);
});
