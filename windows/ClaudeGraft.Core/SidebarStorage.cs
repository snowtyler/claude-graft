using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// Runs the pin/order storage read or write in a bundled Electron, which owns the
/// Chromium storage locks and commits the IndexedDB transaction — the port never
/// writes a LevelDB file itself. The Mac build clones Claude's own runtime and
/// swaps in its asar; on Windows Claude's binary has asar integrity fused on, so
/// Graft ships its own Electron beside itself instead.
/// </summary>
public static class SidebarStorage
{
    /// The bundled runtime. Beside the running executable for the app, and one
    /// level up for the shortcut launcher, which lives in its own <c>launcher\</c>
    /// subfolder while Electron sits at the install root. Absent in a dev build
    /// that has not staged Electron, in which case sidebar sync simply skips.
    public static string? ElectronExe()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..") })
        {
            var exe = Path.GetFullPath(Path.Combine(dir, "electron", "electron.exe"));
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static string PrepareApp()
    {
        var dir = Path.Combine(GraftPaths.OwnData, "sidebar-helper");
        Directory.CreateDirectory(dir);
        WriteIfChanged(Path.Combine(dir, "package.json"),
            "{\"name\":\"graft-sidebar\",\"version\":\"1.0.0\",\"main\":\"main.js\"}");
        WriteIfChanged(Path.Combine(dir, "main.js"), Script);
        return dir;
    }

    private static void WriteIfChanged(string path, string content)
    {
        try { if (File.Exists(path) && File.ReadAllText(path) == content) return; } catch { }
        File.WriteAllText(path, content);
    }

    public static Dictionary<string, SidebarSnapshot> Run(IReadOnlyList<string> profiles,
        Dictionary<string, SidebarSnapshot>? changes, Dictionary<string, HashSet<string>> shared)
    {
        var electron = ElectronExe() ?? throw new SidebarSync.Failure("runtime-unavailable");
        var appDir = PrepareApp();
        var scratch = Path.Combine(GraftPaths.OwnData, ".sidebar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var backups = Path.Combine(GraftPaths.OwnData, "sidebar-backups");
            Directory.CreateDirectory(backups);
            var output = Path.Combine(scratch, "result.json");

            var entries = new JsonArray();
            foreach (var profile in profiles)
            {
                var entry = new JsonObject { ["path"] = profile };
                if (changes is not null && changes.TryGetValue(profile, out var snap)
                    && shared.TryGetValue(profile, out var ids))
                {
                    var scoped = snap.Restricted(ids);
                    entry["change"] = new JsonObject
                    {
                        ["expected"] = snap.Fingerprint,
                        ["shared"] = Arr(ids.OrderBy(x => x, StringComparer.Ordinal)),
                        ["pins"] = Arr(scoped.Pins),
                        ["order"] = Arr(scoped.Order),
                        ["sort"] = scoped.Sort,
                    };
                }
                entries.Add(entry);
            }

            var request = new JsonObject
            {
                ["action"] = changes is null ? "read" : "write",
                ["profiles"] = entries,
                ["scratch"] = scratch,
                ["output"] = output,
                ["backup"] = Path.Combine(backups, Guid.NewGuid().ToString("N") + ".json"),
            };
            var requestFile = Path.Combine(scratch, "request.json");
            File.WriteAllBytes(requestFile, JsonSerializer.SerializeToUtf8Bytes(request));

            var psi = new ProcessStartInfo(electron)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add(appDir);
            psi.ArgumentList.Add("--request=" + requestFile);
            // Inherited Electron/Node switches must not turn the helper into an
            // inspector or redirect it to a different entry point.
            foreach (var key in psi.Environment.Keys.ToList())
                if (key.StartsWith("ELECTRON_", StringComparison.Ordinal) || key.StartsWith("NODE_", StringComparison.Ordinal))
                    psi.Environment.Remove(key);

            using var proc = Process.Start(psi) ?? throw new SidebarSync.Failure("storage-unavailable");
            // Drain both streams, or a chatty Electron fills the redirected pipe,
            // blocks on the write, and the wait below times out on otherwise good
            // work. The content is discarded; only exit and the output file matter.
            proc.OutputDataReceived += (_, _) => { };
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(30000))
            {
                // Block until it is actually gone before returning — Synchronize
                // swallows the throw and the launcher opens Claude next, so the
                // only safe guarantee is that the helper has released the store by
                // the time we return. A forced kill makes this wait short.
                try { proc.Kill(entireProcessTree: true); } catch { }
                proc.WaitForExit();
                throw new SidebarSync.Failure("storage-timeout");
            }
            if (proc.ExitCode != 0) throw new SidebarSync.Failure("storage-unavailable");

            if (JsonNode.Parse(File.ReadAllBytes(output)) is not JsonObject reply)
                throw new SidebarSync.Failure("storage-unavailable");
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new SidebarSync.Failure(reply["error"]?.GetValue<string>() ?? "storage-unavailable");

            var result = new Dictionary<string, SidebarSnapshot>();
            foreach (var node in reply["profiles"]?.AsArray() ?? new JsonArray())
            {
                if (node is not JsonObject item || item["path"]?.GetValue<string>() is not string path) continue;
                result[path] = new SidebarSnapshot
                {
                    Pins = Strings(item["pins"]),
                    Order = Strings(item["order"]),
                    Sort = item["sort"]?.GetValue<string>() ?? "recency",
                    OrderTime = item["orderTime"]?.GetValue<double>() ?? 0,
                    Scope = item["scope"]?.GetValue<string>(),
                    Fingerprint = item["fingerprint"]?.GetValue<string>() ?? "",
                };
            }
            if (result.Count != profiles.Count) throw new SidebarSync.Failure("storage-unavailable");
            return result;
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch { } }
    }

    private static JsonArray Arr(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }

    private static List<string> Strings(JsonNode? node) =>
        node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();

    /// <summary>
    /// The Electron main process. Ported from the Mac build's SidebarStorage,
    /// unchanged but for two Windows adjustments: it reads its request path from
    /// <c>--request=</c> (stock Electron takes the app path as argv[1]), and it
    /// links each profile's storage in as a junction. It runs in an empty offline
    /// document with no Claude scripts, cookies or sessions loaded.
    /// </summary>
    public const string Script = """
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const FRAME = 'dframe-store';
const PINS = 'store:pin-state:dframe-starred-code';
const LEGACY_PINS = 'LSS-persisted.starred-local-code-sessions';
const SLICE = 'LSS-persisted.dframe-local-slice';
const OWNER = 'ccd-sync-owner';
const ACTIVE = 'ccd-sync-active';
const QUARANTINE = 'ccd-sync-quarantine';
const PENDING = 'ccd-sync-pending:ccd/dframe-store';
const unique = values => [...new Set(values)];
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const strings = value => Array.isArray(value) && value.every(item => typeof item === 'string');
const localID = value => /^local_[a-zA-Z0-9-]+$/.test(value);
const digest = value => crypto.createHash('sha256').update(JSON.stringify(value)).digest('hex');
function requireShape(ok) { if (!ok) throw Error('unsupported-storage'); }
function parse(value) { try { return JSON.parse(value); } catch { throw Error('unreadable-storage'); } }

function replaceShared(existing, shared, wanted) {
    let next = 0;
    const result = [];
    for (const item of existing) {
        if (!shared.has(item)) result.push(item);
        else if (next < wanted.length) result.push(wanted[next++]);
    }
    return unique(result.concat(wanted.slice(next)));
}

function decode(raw) {
    const frame = parse(raw.local[FRAME]);
    const pin = parse(raw.pin);
    requireShape(object(frame) && frame.version === 1 && object(frame.state)
        && strings(frame.state.pinnedOrder)
        && object(frame.state.sortByByMode)
        && object(pin) && pin.version === 0 && object(pin.state)
        && strings(pin.state.starredIds));
    const sort = frame.state.sortByByMode.code ?? 'recency';
    requireShape(['recency', 'alpha', 'created'].includes(sort));
    for (const key of [LEGACY_PINS, SLICE]) {
        if (raw.local[key] === null) continue;
        const value = parse(raw.local[key]);
        requireShape(object(value) && (key === LEGACY_PINS ? strings(value.value)
            : object(value.value) && strings(value.value.pinnedOrder)));
    }
    return {frame, pin, sort};
}

function snapshot(raw) {
    const {frame, pin, sort} = decode(raw);
    const pins = unique(pin.state.starredIds.filter(localID));
    const pinned = new Set(pins);
    const order = unique(frame.state.pinnedOrder.filter(id => id.startsWith('code:'))
        .map(id => id.slice(5)).filter(id => pinned.has(id)).concat(pins));
    const slice = raw.local[SLICE] === null ? null : parse(raw.local[SLICE]);
    return {pins: pins.sort(), order, sort,
        orderTime: typeof slice?.timestamp === 'number' ? slice.timestamp : 0,
        scope: frame.state.lastSidebarScopeKey ?? null, fingerprint: digest(raw)};
}

function patch(raw, change) {
    const {frame, pin, sort} = decode(raw);
    requireShape(strings(change.shared) && change.shared.every(localID)
        && strings(change.pins) && strings(change.order)
        && change.pins.every(id => change.shared.includes(id))
        && change.order.length === new Set(change.order).size
        && change.order.length === change.pins.length
        && change.order.every(id => change.pins.includes(id))
        && ['recency', 'alpha', 'created'].includes(change.sort));
    const shared = new Set(change.shared);
    const sharedKeys = new Set(change.shared.map(id => 'code:' + id));
    const orderKeys = change.order.map(id => 'code:' + id);
    const now = Math.max(Date.now(), (pin.updatedAt ?? 0) + 1);
    pin.state.starredIds = replaceShared(pin.state.starredIds, shared, change.order);
    pin.updatedAt = now;
    frame.state.pinnedOrder = replaceShared(frame.state.pinnedOrder, sharedKeys, orderKeys);
    frame.state.sortByByMode.code = change.sort;
    const local = {...raw.local, [FRAME]: JSON.stringify(frame)};
    if (sort !== change.sort) {
        const scope = frame.state.lastSidebarScopeKey;
        const owner = raw.local[OWNER];
        requireShape(raw.local[QUARANTINE] !== '1');
        if (owner !== null) {
            requireShape(typeof scope === 'string' && scope.split('/').length === 2
                && owner === scope.split('/')[0]);
            const pending = raw.local[PENDING];
            requireShape(pending === null || pending === '1' || pending === scope
                || pending === scope + '|migrate');
            local[PENDING] = scope;
        } else {
            requireShape(raw.local[ACTIVE] !== '1' && raw.local[PENDING] === null);
        }
    }
    const legacy = raw.local[LEGACY_PINS] === null ? {value: [], tabId: 'graft', timestamp: 0}
        : parse(raw.local[LEGACY_PINS]);
    legacy.value = replaceShared(legacy.value, shared, change.order);
    legacy.timestamp = Math.max(now, (legacy.timestamp ?? 0) + 1);
    local[LEGACY_PINS] = JSON.stringify(legacy);
    const slice = raw.local[SLICE] === null
        ? {value: {pinnedOrder: [], homeProjectsPinnedOrder: []}, tabId: 'graft', timestamp: 0}
        : parse(raw.local[SLICE]);
    slice.value.pinnedOrder = replaceShared(slice.value.pinnedOrder, sharedKeys, orderKeys);
    slice.timestamp = Math.max(now, (slice.timestamp ?? 0) + 1);
    local[SLICE] = JSON.stringify(slice);
    return {...raw, local, pin: JSON.stringify(pin)};
}

function patchPreferences(data, change) {
    const result = structuredClone(data);
    requireShape(object(result) && (result.preferences === undefined || object(result.preferences)));
    const prefs = result.preferences ??= {};
    requireShape(prefs.epitaxyPrefs === undefined || object(prefs.epitaxyPrefs));
    const epi = prefs.epitaxyPrefs ??= {};
    const pins = epi['starred-local-code-sessions'] ?? [];
    const slice = epi['dframe-local-slice'] ?? {pinnedOrder: [], homeProjectsPinnedOrder: []};
    requireShape(strings(pins) && object(slice) && strings(slice.pinnedOrder));
    epi['starred-local-code-sessions'] = replaceShared(pins, new Set(change.shared), change.order);
    epi['dframe-local-slice'] = {...slice, pinnedOrder: replaceShared(slice.pinnedOrder,
        new Set(change.shared.map(id => 'code:' + id)), change.order.map(id => 'code:' + id))};
    return result;
}

async function storageOperation(action, raw) {
    const names = await indexedDB.databases();
    if (!names.some(db => db.name === 'keyval-store')) throw Error('not-initialized');
    const db = await new Promise((resolve, reject) => {
        const request = indexedDB.open('keyval-store');
        request.onupgradeneeded = () => { request.transaction.abort(); reject(Error('not-initialized')); };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(Error('unreadable-storage'));
    });
    try {
        if (!db.objectStoreNames.contains('keyval')) throw Error('unsupported-storage');
        if (action === 'read') {
            const pin = await new Promise((resolve, reject) => {
                const transaction = db.transaction('keyval', 'readonly');
                const request = transaction.objectStore('keyval').get('store:pin-state:dframe-starred-code');
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(Error('unreadable-storage'));
            });
            // dframe-store is Claude's marker that this profile's sidebar exists;
            // the code-pin key is absent until a code session is first pinned, so
            // a freshly grafted profile that is to receive pins reads as an empty
            // set rather than uninitialised.
            if (localStorage.getItem('dframe-store') === null) throw Error('not-initialized');
            const pinState = typeof pin === 'string' ? pin : JSON.stringify({state: {starredIds: []}, version: 0});
            return {pin: pinState, local: Object.fromEntries(['dframe-store',
                'LSS-persisted.starred-local-code-sessions', 'LSS-persisted.dframe-local-slice',
                'ccd-sync-owner', 'ccd-sync-active', 'ccd-sync-quarantine', 'ccd-sync-pending:ccd/dframe-store']
                .map(key => [key, localStorage.getItem(key)]))};
        }
        await new Promise((resolve, reject) => {
            const transaction = db.transaction('keyval', 'readwrite', {durability: 'strict'});
            transaction.objectStore('keyval').put(raw.pin, 'store:pin-state:dframe-starred-code');
            transaction.oncomplete = resolve;
            transaction.onerror = transaction.onabort = () => reject(Error('write-failed'));
        });
        for (const key of ['dframe-store', 'LSS-persisted.starred-local-code-sessions',
            'LSS-persisted.dframe-local-slice', 'ccd-sync-pending:ccd/dframe-store']) {
            if (raw.local[key] === null) localStorage.removeItem(key);
            else localStorage.setItem(key, raw.local[key]);
        }
        return true;
    } finally { db.close(); }
}

function atomicJSON(file, data) {
    const temporary = file + '.graft-' + crypto.randomUUID();
    try {
        fs.writeFileSync(temporary, JSON.stringify(data), {mode: 0o600, flag: 'wx'});
        fs.renameSync(temporary, file);
    } finally { try { fs.unlinkSync(temporary); } catch {} }
}

async function run() {
    const {app, BrowserWindow, session, protocol} = require('electron');
    const arg = prefix => { const found = process.argv.find(a => a.startsWith(prefix)); return found ? found.slice(prefix.length) : null; };
    const request = parse(fs.readFileSync(arg('--request='), 'utf8'));
    requireShape(['read', 'write'].includes(request.action) && Array.isArray(request.profiles)
        && request.profiles.length > 0 && request.profiles.length <= 32);
    app.setPath('userData', path.join(request.scratch, 'runtime'));
    app.commandLine.appendSwitch('disable-gpu');
    app.commandLine.appendSwitch('disable-background-networking');
    app.commandLine.appendSwitch('disable-component-update');
    app.commandLine.appendSwitch('host-resolver-rules', 'MAP * ~NOTFOUND');
    protocol.registerSchemesAsPrivileged([{scheme: 'app', privileges: {standard: true, secure: true}}]);
    const timeout = setTimeout(() => app.exit(2), 20000);
    await app.whenReady();
    app.dock?.hide();
    const opened = [];
    try {
        for (const [index, profile] of request.profiles.entries()) {
            const scratch = path.join(request.scratch, 'profile-' + index);
            fs.mkdirSync(scratch, {recursive: true, mode: 0o700});
            for (const name of ['Local Storage', 'IndexedDB']) {
                const original = path.join(profile.path, name);
                if (!fs.statSync(original).isDirectory()) throw Error('not-initialized');
                fs.symlinkSync(original, path.join(scratch, name), 'junction');
            }
            const ses = session.fromPath(scratch, {cache: false});
            ses.setPermissionRequestHandler((contents, permission, callback) => callback(false));
            const origins = ['https://claude.ai', 'app://localhost'];
            for (const scheme of ['https', 'http', 'app']) await ses.protocol.handle(scheme, req => {
                if (!origins.some(origin => req.url === origin + '/')) return new Response('', {status: 403});
                return new Response('<!doctype html><title>Sidebar storage</title>', {headers: {
                    'Content-Type': 'text/html', 'Content-Security-Policy': "default-src 'none'"}});
            });
            const window = new BrowserWindow({show: false, webPreferences: {
                session: ses, sandbox: true, contextIsolation: true, nodeIntegration: false}});
            const evaluate = (action, raw) => window.webContents.executeJavaScript(
                '(' + storageOperation.toString() + ')(' + JSON.stringify(action) + ',' + JSON.stringify(raw) + ')');
            const candidates = [];
            for (const origin of origins) {
                await window.loadURL(origin + '/');
                try {
                    const raw = await evaluate('read', null);
                    candidates.push({origin, raw});
                } catch (error) {
                    if (!String(error).includes('not-initialized')) throw error;
                }
            }
            requireShape(candidates.length === 1);
            const {origin, raw} = candidates[0];
            await window.loadURL(origin + '/');
            const prefsPath = path.join(profile.path, 'claude_desktop_config.json');
            let prefsText = null;
            if (fs.existsSync(prefsPath)) {
                requireShape(fs.lstatSync(prefsPath).isFile());
                prefsText = fs.readFileSync(prefsPath, 'utf8');
            }
            const prefs = prefsText === null ? {} : parse(prefsText);
            const state = snapshot(raw);
            state.fingerprint = digest({raw, prefsText, origin});
            const entry = {profile, window, ses, evaluate, raw, prefs, prefsText, prefsPath, state, origin};
            opened.push(entry);
            if (request.action === 'write') {
                requireShape(profile.change.expected === state.fingerprint);
                entry.next = patch(raw, profile.change);
                entry.nextPrefs = patchPreferences(prefs, profile.change);
            }
        }
        if (request.action === 'write') {
            atomicJSON(request.backup, opened.map(item => ({profile: item.profile.path,
                origin: item.origin, raw: item.raw,
                preferences: item.prefs.preferences?.epitaxyPrefs ?? null})));
            for (const item of opened) {
                if ((fs.existsSync(item.prefsPath) ? fs.readFileSync(item.prefsPath, 'utf8') : null) !== item.prefsText)
                    throw Error('changed-during-sync');
                try {
                    await item.evaluate('write', item.next);
                    atomicJSON(item.prefsPath, item.nextPrefs);
                    await item.ses.flushStorageData();
                    const readBack = await item.evaluate('read', null);
                    requireShape(JSON.stringify(readBack) === JSON.stringify(item.next));
                } catch (error) {
                    await item.evaluate('write', item.raw);
                    if (item.prefsText !== null) atomicJSON(item.prefsPath, item.prefs);
                    else if (fs.existsSync(item.prefsPath)) fs.unlinkSync(item.prefsPath);
                    throw error;
                }
            }
        }
        atomicJSON(request.output, {ok: true, profiles: opened.map(item => ({path: item.profile.path,
            ...item.state, ...(request.action === 'write' ? snapshot(item.next) : {})}))});
    } catch (error) {
        atomicJSON(request.output, {ok: false, error: String(error.message ?? error).slice(0,300)});
    } finally {
        for (const item of opened) item.window.destroy();
        clearTimeout(timeout);
        app.quit();
    }
}

run().catch(() => { try { require('electron').app.exit(1); } catch {} });
""";
}
