import assert from 'node:assert/strict';
import {createHash, createPublicKey} from 'node:crypto';
import {readFile} from 'node:fs/promises';
import test from 'node:test';
import {HOST_NAME, ImeController, isHostReply} from '../extension/controller.mjs';
import {describeView} from '../extension/presentation.mjs';

function event() {
    const listeners = [];
    return {
        addListener: listener => listeners.push(listener),
        emit: (...args) => listeners.forEach(listener => listener(...args)),
    };
}

function hostReply(overrides = {}) {
    return {
        v: 1, state: 'mode-enabled', detail: 'mode-enabled',
        effectiveness: 'unverified', restoreRequired: true,
        activeClients: 1, modeEnabled: true, ...overrides,
    };
}

function storageArea(initial = {}) {
    const values = {...initial};
    return {
        values,
        get: async defaults => ({...defaults, ...values}),
        set: async update => Object.assign(values, update),
    };
}

function mockChrome({enabled = false, allowed = true, windows = [], responder} = {}) {
    const state = {allowed, windows, ports: [], messages: [], badges: [], queryError: false};
    const api = {
        storage: {local: storageArea({enabled}), session: storageArea()},
        extension: {isAllowedIncognitoAccess: callback => callback(state.allowed)},
        windows: {
            getAll: async options => {
                assert.deepEqual(options, {populate: false});
                if (state.queryError) throw new Error('query-failed');
                return state.windows;
            },
        },
        runtime: {
            connectNative: name => {
                assert.equal(name, HOST_NAME);
                const port = {
                    onMessage: event(), onDisconnect: event(), disconnected: false,
                    disconnect() { this.disconnected = true; this.onDisconnect.emit(); },
                    postMessage(message) {
                        state.messages.push(message);
                        const reply = responder ? responder(message, port) : hostReply({
                            activeClients: message.active === false ? 0 : 1,
                        });
                        if (reply !== undefined) queueMicrotask(() => port.onMessage.emit(reply));
                    },
                };
                state.ports.push(port);
                return port;
            },
        },
        action: {
            setBadgeText: async badge => state.badges.push(badge.text),
            setBadgeBackgroundColor: async () => {}, setTitle: async () => {},
        },
    };
    return {api, state};
}

test('manifest has only native messaging and settings permissions, with stable public key', async () => {
    const manifest = JSON.parse(await readFile(new URL('../extension/manifest.json', import.meta.url)));
    assert.deepEqual(manifest.permissions, ['nativeMessaging', 'storage']);
    assert.equal(manifest.incognito, 'spanning');
    assert.equal(manifest.background.type, 'module');
    for (const key of ['host_permissions', 'content_scripts', 'externally_connectable']) {
        assert.equal(manifest[key], undefined);
    }
    const publicDer = Buffer.from(manifest.key, 'base64');
    assert.equal(createPublicKey({key: publicDer, type: 'spki', format: 'der'}).asymmetricKeyType, 'rsa');
    const id = [...createHash('sha256').update(publicDer).digest().subarray(0, 16)]
        .map(byte => String.fromCharCode(97 + (byte >> 4), 97 + (byte & 15))).join('');
    assert.equal(id, 'djbbbndacfniipaiopkjbeipamnjaneo');
});

test('disabled by default and ordinary windows never launch a native host', async () => {
    for (const options of [{windows: [{incognito: true}]}, {enabled: true}]) {
        const {api, state} = mockChrome(options);
        const controller = new ImeController(api);
        await controller.refresh();
        assert.equal(state.ports.length, 0);
    }
});

test('missing private permission warns and never claims protection', async () => {
    const {api, state} = mockChrome({enabled: true, allowed: false});
    const controller = new ImeController(api);
    await controller.refresh();
    assert.equal(controller.view().status.detail, 'incognito-permission-required');
    assert.equal(state.ports.length, 0);
    assert.equal(state.badges.at(-1), '!');
});

test('private windows share one lease and the final close releases it after acknowledgement', async () => {
    const {api, state} = mockChrome({enabled: true, windows: [{incognito: true}, {incognito: true}]});
    const controller = new ImeController(api);
    await controller.refresh();
    assert.deepEqual(state.messages, [{v: 1, op: 'sync', active: true}]);
    state.windows = [{incognito: true}, {incognito: false}];
    await controller.refresh();
    assert.equal(state.messages.length, 1);
    state.windows = [{incognito: false}];
    await controller.refresh();
    assert.deepEqual(state.messages.at(-1), {v: 1, op: 'sync', active: false});
    assert.equal(state.ports[0].disconnected, true);
    assert.equal(controller.view().status.restoreRequired, true);
    assert.equal(state.badges.at(-1), '!');
});

test('release waits for native acknowledgement instead of disconnecting early', async () => {
    let releasePort;
    const {api, state} = mockChrome({
        enabled: true, windows: [{incognito: true}],
        responder: (message, port) => {
            if (message.active === false) { releasePort = port; return undefined; }
            return hostReply();
        },
    });
    const controller = new ImeController(api);
    await controller.refresh();
    state.windows = [];
    const release = controller.refresh();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(releasePort.disconnected, false);
    releasePort.onMessage.emit(hostReply({activeClients: 0}));
    await release;
    assert.equal(releasePort.disconnected, true);
});

test('enabling while a private window already exists immediately reconciles it', async () => {
    const {api, state} = mockChrome({windows: [{incognito: true}]});
    const controller = new ImeController(api);
    await controller.refresh();
    await controller.command({action: 'enable', enabled: true});
    assert.equal(state.messages[0].active, true);
    await controller.command({action: 'enable', enabled: false});
    assert.equal(state.messages.at(-1).active, false);
    assert.equal(controller.view().enabled, false);
});

test('a stale window snapshot cannot acquire a lease', async () => {
    const {api, state} = mockChrome({enabled: true});
    let finishFirstQuery;
    let queryCount = 0;
    api.windows.getAll = () => {
        queryCount += 1;
        if (queryCount === 1) return new Promise(resolve => { finishFirstQuery = resolve; });
        return Promise.resolve([]);
    };
    const controller = new ImeController(api);
    const first = controller.refresh();
    await new Promise(resolve => setImmediate(resolve));
    const second = controller.refresh();
    finishFirstQuery([{incognito: true}]);
    await Promise.all([first, second]);
    assert.equal(state.ports.length, 0);
});

test('query failure preserves an existing lease rather than reporting zero windows', async () => {
    const {api, state} = mockChrome({enabled: true, windows: [{incognito: true}]});
    const controller = new ImeController(api);
    await controller.refresh();
    state.queryError = true;
    await assert.rejects(controller.refresh());
    await controller.queue;
    assert.equal(state.ports[0].disconnected, false);
    assert.equal(state.messages.length, 1);
    assert.equal(controller.view().status.state, 'unavailable');
});

test('native disconnect is latched across worker recreation until an explicit retry', async () => {
    const {api, state} = mockChrome({enabled: true, windows: [{incognito: true}]});
    const controller = new ImeController(api);
    await controller.refresh();
    state.ports[0].onDisconnect.emit();
    await controller.refresh();
    assert.equal(state.ports.length, 1);
    const nextWorker = new ImeController(api);
    await nextWorker.refresh();
    assert.equal(state.ports.length, 1);
    await nextWorker.command({action: 'retry'});
    assert.equal(state.ports.length, 2);
});

test('unavailable host status does not cause reconnection on every focus event', async () => {
    const {api, state} = mockChrome({
        enabled: true, windows: [{incognito: true}],
        responder: () => hostReply({state: 'not-configured', modeEnabled: null, restoreRequired: false}),
    });
    const controller = new ImeController(api);
    await controller.refresh();
    await controller.refresh();
    await controller.refresh();
    assert.equal(state.ports.length, 1);
    assert.equal(controller.view().status.state, 'not-configured');
    assert.equal(state.ports[0].disconnected, false);
});

test('a new private session may retry after the previous session completely closed', async () => {
    const {api, state} = mockChrome({enabled: true, windows: [{incognito: true}]});
    const controller = new ImeController(api);
    await controller.refresh();
    state.ports[0].onDisconnect.emit();
    state.windows = [];
    await controller.refresh();
    state.windows = [{incognito: true}];
    await controller.refresh();
    assert.equal(state.ports.length, 2);
});

test('diagnosis is user initiated and closes an idle host connection', async () => {
    const {api, state} = mockChrome({responder: () => hostReply({state: 'idle', activeClients: 0})});
    const controller = new ImeController(api);
    await controller.refresh();
    await controller.command({action: 'view'});
    assert.equal(state.ports.length, 0);
    await controller.command({action: 'diagnose'});
    assert.deepEqual(state.messages, [{v: 1, op: 'status'}]);
    assert.equal(state.ports[0].disconnected, true);
});

test('diagnosis preserves a live lease and local restore gate blocks active private windows', async () => {
    const {api, state} = mockChrome({
        enabled: true, windows: [{incognito: true}],
        responder: message => hostReply({state: message.op === 'restore' ? 'recovery-needed' : 'mode-enabled'}),
    });
    const controller = new ImeController(api);
    await controller.refresh();
    await controller.command({action: 'diagnose'});
    await controller.command({action: 'restore'});
    assert.equal(state.ports.length, 1);
    assert.equal(state.ports[0].disconnected, false);
    assert.deepEqual(state.messages.at(-1), {v: 1, op: 'status'});
    assert.equal(state.messages.some(message => message.op === 'restore'), false);
    assert.equal(controller.view().status.state, 'recovery-needed');
});

test('successful manual restore clears persistent restoration warning', async () => {
    const {api} = mockChrome({responder: () => hostReply({
        state: 'idle', restoreRequired: false, activeClients: 0, modeEnabled: false,
    })});
    api.storage.local.values.lastStatus = hostReply({activeClients: 0});
    const controller = new ImeController(api);
    await controller.refresh();
    assert.equal(controller.view().status.restoreRequired, true);
    await controller.command({action: 'restore'});
    assert.equal(api.storage.local.values.lastStatus.restoreRequired, false);
});

test('restoration warning survives worker and browser lifecycle without starting a host', async () => {
    const {api, state} = mockChrome({enabled: true});
    api.storage.local.values.lastStatus = hostReply({activeClients: 0});
    const controller = new ImeController(api);
    await controller.refresh();
    assert.equal(controller.view().status.restoreRequired, true);
    assert.equal(state.badges.at(-1), '!');
    assert.equal(state.ports.length, 0);
});

test('malformed and overclaiming native replies fail closed', async () => {
    for (const overrides of [
        {effectiveness: 'verified'}, {v: 2}, {state: 'protected'},
        {detail: 'private input text'}, {activeClients: -1}, {modeEnabled: 'yes'},
        {unexpectedInput: 'must-not-be-saved'},
    ]) {
        const {api, state} = mockChrome({
            enabled: true, windows: [{incognito: true}], responder: () => hostReply(overrides),
        });
        const controller = new ImeController(api);
        await controller.refresh();
        assert.equal(controller.view().status.state, 'unavailable');
        assert.equal(state.ports[0].disconnected, true);
    }
    assert.equal(isHostReply(null), false);
});

test('native timeout warns, disconnects and never retries itself', async () => {
    const {api, state} = mockChrome({enabled: true, windows: [{incognito: true}], responder: () => undefined});
    const controller = new ImeController(api, {requestTimeoutMs: 5});
    await controller.refresh();
    await controller.refresh();
    assert.equal(state.ports.length, 1);
    assert.equal(state.ports[0].disconnected, true);
    assert.equal(controller.view().status.state, 'unavailable');
});

test('unknown popup messages cannot become arbitrary native commands', async () => {
    const {api, state} = mockChrome();
    const controller = new ImeController(api);
    await assert.rejects(controller.command({action: 'native', op: 'set-config'}));
    assert.equal(state.ports.length, 0);
});

test('window-query failure cannot erase a pending restoration warning', async () => {
    const {api, state} = mockChrome({enabled: true});
    api.storage.local.values.lastStatus = hostReply({activeClients: 0});
    const controller = new ImeController(api);
    await controller.refresh();
    state.queryError = true;
    await assert.rejects(controller.refresh());
    await controller.queue;
    assert.equal(controller.view().status.restoreRequired, true);
});

test('popup explains restore rejection and manual recovery instead of suggesting success', () => {
    const view = detail => ({enabled: true, incognitoAllowed: true, status: hostReply({detail})});
    assert.equal(describeView(view('active-requests-prevent-restore'))[0], '復元できません');
    assert.match(describeView(view('pre-existing-or-unknown-mode-preserved'))[1], /設定で解除/);
    assert.equal(describeView(view('previous-session-needs-manual-review'))[0], 'IME設定で復旧してください');
    assert.equal(describeView(view('explicit-restore-confirmed'))[0], '元の設定へ復元しました');
    assert.equal(describeView(view('mode-effectiveness-unverified'))[0], '設定時にONを確認');
});

test('native response deadline allows bounded three-step UI operations', () => {
    const {api} = mockChrome();
    assert.equal(new ImeController(api).requestTimeoutMs, 45_000);
});

test('SET describes the previous observation without claiming continuous monitoring', async () => {
    const {api, state} = mockChrome({
        enabled: true, windows: [{incognito: true}],
        responder: () => hostReply({restoreRequired: false}),
    });
    const controller = new ImeController(api);
    await controller.refresh();
    assert.equal(state.badges.at(-1), 'SET');
    await controller.refresh();
    assert.equal(state.messages.length, 1);
    assert.match(describeView(controller.view())[1], /自動監視していません/);
});

test('negative mode observations keep the registered lease and cannot permit restore', async () => {
    for (const status of ['recovery-needed', 'unavailable']) {
        const {api, state} = mockChrome({
            enabled: true, windows: [{incognito: true}],
            responder: () => hostReply({state: status, modeEnabled: null}),
        });
        const controller = new ImeController(api);
        await controller.refresh();
        assert.equal(controller.hasLease, true);
        assert.equal(state.ports[0].disconnected, false);
        await controller.command({action: 'restore'});
        assert.equal(state.messages.length, 1);
        assert.equal(state.messages[0].op, 'sync');
        assert.equal(state.ports[0].disconnected, false);
        state.windows = [];
        await controller.refresh();
        assert.deepEqual(state.messages.at(-1), {v: 1, op: 'sync', active: false});
        assert.equal(state.ports[0].disconnected, true);
    }
});

test('restore checks current windows even after disabled mode or a broken connection', async () => {
    for (const isEnabled of [true, false]) {
        const {api, state} = mockChrome({enabled: isEnabled});
        const controller = new ImeController(api);
        await controller.refresh();
        state.windows = [{incognito: true}];
        await controller.command({action: 'restore'});
        assert.equal(state.ports.length, 0);
        assert.equal(controller.view().status.detail, 'active-requests-prevent-restore');
    }
});

test('restore is never sent if permission or current window query cannot be verified', async () => {
    for (const failure of ['permission', 'windows']) {
        const {api, state} = mockChrome();
        const controller = new ImeController(api);
        await controller.refresh();
        if (failure === 'permission') state.allowed = false;
        else state.queryError = true;
        await controller.command({action: 'restore'});
        assert.equal(state.ports.length, 0);
        assert.equal(controller.view().status.state, 'unavailable');
    }
});

test('a window event during restore preflight cancels the stale zero-window result', async () => {
    const {api, state} = mockChrome();
    const controller = new ImeController(api);
    await controller.refresh();
    let completeQuery;
    api.windows.getAll = () => new Promise(resolve => { completeQuery = resolve; });
    const restore = controller.command({action: 'restore'});
    await new Promise(resolve => setImmediate(resolve));
    const refresh = controller.refresh();
    completeQuery([]);
    await restore;
    api.windows.getAll = async () => [];
    // The queued refresh may already have reached its delayed query.
    await new Promise(resolve => setImmediate(resolve));
    completeQuery([]);
    await refresh;
    assert.equal(state.ports.length, 0);
});

test('potential-change marker is durable before the first native write and covers a crash before reply', async () => {
    let crashSnapshot;
    const {api} = mockChrome({
        enabled: true, windows: [{incognito: true}],
        responder: () => {
            assert.equal(api.storage.local.values.potentialChange, true);
            crashSnapshot = structuredClone(api.storage.local.values);
            return undefined;
        },
    });
    await new ImeController(api, {requestTimeoutMs: 5}).refresh();
    const restarted = mockChrome({enabled: true});
    Object.assign(restarted.api.storage.local.values, crashSnapshot);
    const nextWorker = new ImeController(restarted.api);
    await nextWorker.refresh();
    assert.equal(nextWorker.view().status.restoreRequired, true);
    assert.equal(nextWorker.view().status.detail, 'potential-change-needs-review');
    assert.equal(restarted.state.ports.length, 0);
});

test('normal sync acknowledgement cannot erase a crash restoration marker', async () => {
    const {api, state} = mockChrome({
        enabled: true, windows: [{incognito: true}],
        responder: () => hostReply({restoreRequired: false}),
    });
    await new ImeController(api).refresh();
    assert.equal(api.storage.local.values.potentialChange, true);
    assert.equal(api.storage.local.values.lastStatus.restoreRequired, false);
    // Browser exits without firing port cleanup, and starts with ordinary windows only.
    state.windows = [];
    const nextWorker = new ImeController(api);
    await nextWorker.refresh();
    assert.equal(nextWorker.view().status.restoreRequired, true);
    assert.equal(state.ports.length, 1);
});

test('explicit actual-OFF observation clears the durable change marker', async () => {
    const {api} = mockChrome({responder: () => hostReply({
        state: 'idle', restoreRequired: false, activeClients: 0, modeEnabled: false,
    })});
    api.storage.local.values.potentialChange = true;
    const controller = new ImeController(api);
    await controller.refresh();
    assert.equal(controller.view().status.restoreRequired, true);
    await controller.command({action: 'diagnose'});
    assert.equal(api.storage.local.values.potentialChange, false);
    assert.equal(controller.view().status.restoreRequired, false);
});
