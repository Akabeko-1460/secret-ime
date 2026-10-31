export const HOST_NAME = 'jp.secretime.host';
const HOST_STATES = new Set([
    'mode-enabled', 'idle', 'unavailable', 'recovery-needed', 'not-configured',
]);
const REPLY_KEYS = [
    'v', 'state', 'detail', 'effectiveness', 'restoreRequired', 'activeClients', 'modeEnabled',
];

export function isHostReply(reply) {
    return reply !== null && typeof reply === 'object'
        && Object.keys(reply).length === REPLY_KEYS.length
        && REPLY_KEYS.every(key => Object.hasOwn(reply, key))
        && reply.v === 1 && HOST_STATES.has(reply.state)
        && typeof reply.detail === 'string' && /^[a-z0-9_-]{1,80}$/.test(reply.detail)
        && reply.effectiveness === 'unverified'
        && typeof reply.restoreRequired === 'boolean'
        && Number.isSafeInteger(reply.activeClients) && reply.activeClients >= 0
        && (typeof reply.modeEnabled === 'boolean' || reply.modeEnabled === null);
}

function localStatus(state, detail, restoreRequired = false) {
    return {
        v: 1, state, detail, effectiveness: 'unverified', restoreRequired,
        activeClients: 0, modeEnabled: null,
    };
}

export class ImeController {
    constructor(chromeApi, {requestTimeoutMs = 45_000} = {}) {
        this.chrome = chromeApi;
        this.requestTimeoutMs = requestTimeoutMs;
        this.queue = Promise.resolve();
        this.revision = 0;
        this.port = null;
        this.pending = null;
        this.hasLease = false;
        this.potentialChange = false;
        this.enabled = false;
        this.incognitoAllowed = false;
        this.privateWindowPresent = false;
        this.status = localStatus('idle', 'disabled');
    }

    view() {
        return {
            enabled: this.enabled, incognitoAllowed: this.incognitoAllowed,
            privateWindowPresent: this.privateWindowPresent,
            status: this.displayStatus(),
        };
    }

    displayStatus() {
        if (!this.potentialChange || this.hasLease) return {...this.status};
        if (this.status.detail === 'pre-existing-or-unknown-mode-preserved') {
            return {...this.status, restoreRequired: true};
        }
        if (this.status.state === 'idle' || this.status.state === 'mode-enabled') {
            return localStatus('recovery-needed', 'potential-change-needs-review', true);
        }
        return {...this.status, restoreRequired: true};
    }

    enqueue(operation) {
        const result = this.queue.then(operation);
        this.queue = result.catch(async () => {
            this.status = localStatus('unavailable', 'operation-failed',
                this.hasLease || this.potentialChange || this.status.restoreRequired);
            await this.paintBadge();
        });
        return result;
    }

    refresh() {
        const revision = ++this.revision;
        return this.enqueue(() => this.reconcile(revision));
    }

    async readIncognitoPermission() {
        return new Promise((resolve, reject) => {
            this.chrome.extension.isAllowedIncognitoAccess(allowed => {
                if (this.chrome.runtime.lastError) reject(new Error('permission-unavailable'));
                else resolve(allowed === true);
            });
        });
    }

    async reconcile(revision) {
        const settings = await this.chrome.storage.local.get({
            enabled: false, lastStatus: null, potentialChange: false,
        });
        this.potentialChange = settings.potentialChange === true;
        if (this.status.detail === 'disabled' && isHostReply(settings.lastStatus)) {
            this.status = settings.lastStatus;
        }
        const allowed = await this.readIncognitoPermission();
        const windows = await this.chrome.windows.getAll({populate: false});
        if (revision !== this.revision) return this.view();
        this.enabled = settings.enabled === true;
        this.incognitoAllowed = allowed;
        this.privateWindowPresent = windows.some(window => window.incognito === true);
        const shouldHoldLease = this.enabled && allowed && this.privateWindowPresent;

        if (!shouldHoldLease) {
            if (this.port && this.hasLease) {
                await this.request({v: 1, op: 'sync', active: false});
            }
            this.closePort();
            if (!this.privateWindowPresent) {
                await this.chrome.storage.session.set({connectionBlocked: false});
            }
            if (this.enabled && !allowed) {
                this.status = localStatus('unavailable', 'incognito-permission-required',
                    this.status.restoreRequired);
            } else if (!this.status.restoreRequired) {
                this.status = localStatus('idle', this.enabled ? 'no-private-windows' : 'disabled');
            }
            await this.paintBadge();
            return this.view();
        }

        const {connectionBlocked} = await this.chrome.storage.session.get({connectionBlocked: false});
        if (revision !== this.revision) return this.view();
        if (!this.port && connectionBlocked) {
            this.status = localStatus('unavailable', 'retry-required', this.status.restoreRequired);
        } else if (!this.hasLease) {
            await this.request({v: 1, op: 'sync', active: true});
            // Sync registers a client even when mode configuration is unavailable.
            this.hasLease = this.port !== null && this.status.activeClients >= 1;
            if (!this.hasLease) {
                this.closePort();
                await this.chrome.storage.session.set({connectionBlocked: true});
            }
        }
        await this.paintBadge();
        return this.view();
    }

    connect() {
        if (this.port) return this.port;
        const port = this.chrome.runtime.connectNative(HOST_NAME);
        this.port = port;
        port.onMessage.addListener(reply => {
            if (this.port !== port || !this.pending) return;
            if (!isHostReply(reply)) {
                this.pending.reject(new Error('invalid-host-reply'));
                return;
            }
            this.status = reply;
            this.pending.resolve(reply);
        });
        port.onDisconnect.addListener(() => {
            // Consume Chrome's diagnostic without collecting its text.
            void this.chrome.runtime.lastError;
            if (this.port !== port) return;
            this.port = null;
            this.hasLease = false;
            this.pending?.reject(new Error('native-disconnected'));
            this.status = localStatus('unavailable', 'native-disconnected', true);
            void this.chrome.storage.session.set({connectionBlocked: true});
            void this.chrome.storage.local.set({lastStatus: this.status});
            void this.paintBadge();
        });
        return port;
    }

    async request(message) {
        try {
            if (message.op === 'sync' && message.active === true) {
                // A crash can happen after the host changes the setting but before its reply.
                this.potentialChange = true;
                await this.chrome.storage.local.set({potentialChange: true});
            }
            const port = this.connect();
            const reply = await new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error('native-timeout')), this.requestTimeoutMs);
                this.pending = {
                    resolve: response => { clearTimeout(timer); resolve(response); },
                    reject: error => { clearTimeout(timer); reject(error); },
                };
                try { port.postMessage(message); }
                catch { this.pending.reject(new Error('native-send-failed')); }
            });
            if (reply.modeEnabled === false) this.potentialChange = false;
            await this.chrome.storage.local.set({
                lastStatus: reply, potentialChange: this.potentialChange,
            });
            return reply;
        } catch {
            this.status = localStatus('unavailable', 'native-request-failed', true);
            this.closePort();
            await this.chrome.storage.session.set({connectionBlocked: true});
            await this.chrome.storage.local.set({lastStatus: this.status});
            return this.status;
        } finally {
            this.pending = null;
        }
    }

    closePort() {
        const port = this.port;
        this.port = null;
        this.hasLease = false;
        if (port) port.disconnect();
    }

    async paintBadge() {
        const status = this.displayStatus();
        const needsAttention = status.state === 'unavailable'
            || status.state === 'not-configured'
            || status.state === 'recovery-needed' || status.restoreRequired;
        const text = needsAttention ? '!' : this.hasLease ? 'SET' : this.enabled ? '' : 'OFF';
        await Promise.all([
            this.chrome.action.setBadgeText({text}),
            this.chrome.action.setBadgeBackgroundColor({color: '#94631b'}),
            this.chrome.action.setTitle({title: 'SecretIME：学習停止の実効性は未検証'}),
        ]);
    }

    async restoreAllowed() {
        const revision = this.revision;
        try {
            const allowed = await this.readIncognitoPermission();
            const windows = await this.chrome.windows.getAll({populate: false});
            this.incognitoAllowed = allowed;
            this.privateWindowPresent = windows.some(window => window.incognito === true);
            if (!allowed) {
                this.status = localStatus('unavailable', 'incognito-permission-required',
                    this.potentialChange || this.status.restoreRequired);
                return false;
            }
            if (this.privateWindowPresent || this.hasLease || revision !== this.revision) {
                this.status = localStatus('recovery-needed', 'active-requests-prevent-restore',
                    this.potentialChange || this.status.restoreRequired);
                return false;
            }
            return true;
        } catch {
            this.status = localStatus('unavailable', 'restore-prerequisites-unconfirmed',
                this.potentialChange || this.status.restoreRequired);
            return false;
        }
    }

    async command(message) {
        if (message?.action === 'view') return this.queue.then(() => this.view());
        if (message?.action === 'enable' && typeof message.enabled === 'boolean') {
            const revision = ++this.revision;
            return this.enqueue(async () => {
                await this.chrome.storage.local.set({enabled: message.enabled});
                await this.chrome.storage.session.set({connectionBlocked: false});
                return this.reconcile(revision);
            });
        }
        if (message?.action === 'retry') {
            const revision = ++this.revision;
            return this.enqueue(async () => {
                await this.chrome.storage.session.set({connectionBlocked: false});
                return this.reconcile(revision);
            });
        }
        if (message?.action === 'diagnose' || message?.action === 'restore') {
            return this.enqueue(async () => {
                if (message.action === 'restore' && !await this.restoreAllowed()) {
                    await this.paintBadge();
                    return this.view();
                }
                await this.request({v: 1, op: message.action === 'restore' ? 'restore' : 'status'});
                if (!this.hasLease) this.closePort();
                await this.paintBadge();
                return this.view();
            });
        }
        throw new Error('unsupported-popup-command');
    }
}
