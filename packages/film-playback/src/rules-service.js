import fs from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { isAbsolute, join, resolve } from 'node:path';
import { isUtf8 } from 'node:buffer';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { bindRuleSnapshot, filmRuleFailure, filmRuleKernelRevision, filmRuleSource, validateRules, validRuleText } from './rules.js';

const fault = code => Object.assign(new Error(code), { code });
const digest = value => createHash('sha256').update(value).digest('hex');
function freeze(value) {
  if (value && typeof value === 'object') {
    for (const child of Object.values(value)) freeze(child);
    Object.freeze(value);
  }
  return value;
}
function copyRules(rules) {
  return { episodeCleanRegexRules: [...rules.episodeCleanRegexRules],
    episodeRules: rules.episodeRules.map(({ pattern, replace, flags }) => ({ pattern, replace, flags })),
    movieRules: rules.movieRules.map(({ pattern, replace, flags }) => ({ pattern, replace, flags })),
    aggregateRegexRules: [...rules.aggregateRegexRules] };
}
function previewText(value) {
  return validRuleText(value) || Array.isArray(value) &&
    value.every(unit => Number.isInteger(unit) && unit >= 0 && unit <= 65535);
}
const ruleGroups = {
  episodeClean: ['episodeCleanRegexRules', 'episode'],
  episode: ['episodeRules', 'episode'],
  movie: ['movieRules', 'movie'],
  search: ['aggregateRegexRules', 'search'],
};
const emptyRules = () => ({ episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] });
function groupInfo(group) {
  if (typeof group !== 'string' || !Object.hasOwn(ruleGroups, group)) throw fault('INVALID_REQUEST');
  return ruleGroups[group];
}
function groupDraft(group, rows) {
  const [key] = groupInfo(group);
  const draft = { ...emptyRules(), [key]: rows };
  validateRules(draft);
  return copyRules(draft);
}
function validatePreview(request) {
  if (!request || !['episode', 'movie', 'search'].includes(request.section) || !previewText(request.text) ||
      request.query !== undefined && !previewText(request.query)) throw fault('INVALID_REQUEST');
}

/** The sole rules document owner. Every accepted write publishes only after rename. */
export function createFilmRulesService({ dataRoot } = {}) {
  if (typeof dataRoot !== 'string' || !isAbsolute(dataRoot) || dataRoot.includes('\0') ||
      dataRoot.split(/[\\/]/).includes('..')) throw fault('INVALID_REQUEST');
  const root = join(resolve(dataRoot), 'film-rules');
  const path = join(root, 'rules.json');
  const program = new NativeJsonProgram(filmRuleSource), jobs = new Set();
  let closed = false, closing, initialized, committed, fingerprint = null;
  let writes = Promise.resolve();
  function open(signal) {
    if (closed) throw fault('CLOSED');
    if (signal?.aborted) throw fault('CANCELLED');
  }
  function tracked(work) {
    jobs.add(work);
    void work.finally(() => jobs.delete(work)).catch(() => {});
    return work;
  }
  function readDisk() {
    let descriptor;
    try {
      descriptor = fs.openSync(path, fs.constants.O_RDONLY);
      const bytes = fs.readFileSync(descriptor);
      let document;
      try {
        if (!isUtf8(bytes)) throw fault('CORRUPT_STORAGE');
        document = JSON.parse(bytes.toString('utf8'));
        validateRules(document.rules);
      } catch { throw fault('CORRUPT_STORAGE'); }
      return { rules: copyRules(document.rules), fingerprint: digest(bytes) };
    } catch (error) {
      if (descriptor === undefined && error.code === 'ENOENT') return null;
      throw fault(error.code === 'CORRUPT_STORAGE' ? error.code : 'STORAGE_ERROR');
    } finally {
      if (descriptor !== undefined) {
        try { fs.closeSync(descriptor); } catch { throw fault('STORAGE_ERROR'); }
      }
    }
  }
  function unchanged() {
    if ((readDisk()?.fingerprint ?? null) !== fingerprint) throw fault('STORAGE_CHANGED');
  }
  function write(rules) {
    let temporary = join(root, `.rules-${randomUUID()}.tmp`), descriptor, failure;
    try {
      fs.mkdirSync(root, { recursive: true, mode: 0o700 });
      fs.chmodSync(root, 0o700);
      unchanged();
      const bytes = JSON.stringify({ rules });
      const next = digest(bytes);
      descriptor = fs.openSync(temporary, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
      fs.writeFileSync(descriptor, bytes);
      fs.fsyncSync(descriptor);
      fs.closeSync(descriptor); descriptor = undefined;
      unchanged();
      fs.renameSync(temporary, path);
      temporary = undefined;
      fingerprint = next;
    } catch (error) {
      failure = error;
    } finally {
      if (descriptor !== undefined) {
        try { fs.closeSync(descriptor); } catch (error) { failure ??= error; }
      }
      if (temporary !== undefined) {
        try { fs.unlinkSync(temporary); }
        catch (error) { if (error.code !== 'ENOENT') failure ??= error; }
      }
    }
    if (failure) throw fault(['STORAGE_CHANGED', 'CORRUPT_STORAGE'].includes(failure.code) ? failure.code : 'STORAGE_ERROR');
  }
  function snapshotOf(rules) {
    const snapshot = freeze({ rules, revision: digest(JSON.stringify([filmRuleKernelRevision, rules])) });
    return bindRuleSnapshot(snapshot, (request, options) => invokeNative(request, options));
  }
  function invokeNative(request, { signal } = {}) {
    open(signal);
    return tracked(program.invoke(request, { signal }).catch(error => { throw filmRuleFailure(error); }));
  }
  function initialize() {
    return initialized ??= tracked((async () => {
      const disk = readDisk();
      if (disk) {
        fingerprint = disk.fingerprint;
        committed = snapshotOf(disk.rules);
      } else {
        const { rules } = await invokeNative({ operation: 'defaults' });
        open();
        write(rules);
        committed = snapshotOf(rules);
      }
    })().catch(error => {
      initialized = undefined;
      throw error;
    }));
  }
  async function snapshot() {
    open();
    await initialize();
    await writes;
    open();
    return committed;
  }
  function mutate(operation, { signal } = {}) {
    open(signal);
    const work = writes.then(async () => {
      open(signal);
      await initialize();
      open(signal);
      return operation(signal);
    });
    writes = work.catch(() => {});
    return tracked(work);
  }
  function inputRules(value) {
    validateRules(value);
    return copyRules(value);
  }
  return {
    get: snapshot, snapshot,
    set({ rules }, options) {
      const draft = inputRules(rules);
      return mutate(async signal => {
        const normalized = await invokeNative({ operation: 'save', rules: draft }, { signal });
        open(signal);
        write(normalized.rules);
        return committed = snapshotOf(normalized.rules);
      }, options);
    },
    setGroup({ group, rules }, options) {
      const [key] = groupInfo(group), draft = groupDraft(group, rules);
      return mutate(async signal => {
        const normalized = await invokeNative({ operation: 'save', rules: draft }, { signal });
        open(signal);
        const merged = { ...copyRules(committed.rules), [key]: normalized.rules[key] };
        write(merged);
        return committed = snapshotOf(merged);
      }, options);
    },
    async groupDefaults({ group }, options) {
      const [key, section] = groupInfo(group);
      const result = await invokeNative({ operation: 'restore', section, rules: emptyRules() }, options);
      return result.rules[key];
    },
    async previewGroup({ group, rules, request }, { signal } = {}) {
      const [key, section] = groupInfo(group), draft = groupDraft(group, rules);
      validatePreview(request);
      if (request.section !== section) throw fault('INVALID_REQUEST');
      open(signal);
      await writes;
      open(signal);
      const saved = committed?.rules ?? readDisk()?.rules ??
        (await invokeNative({ operation: 'defaults' }, { signal })).rules;
      return invokeNative({ operation: 'preview', request,
        rules: { ...copyRules(saved), [key]: draft[key] } }, { signal });
    },
    restore({ section, rules }, options) {
      if (!['episode', 'movie', 'search'].includes(section)) throw fault('INVALID_REQUEST');
      const draft = rules === undefined ? undefined : inputRules(rules);
      return mutate(async signal => {
        const normalized = await invokeNative({ operation: 'restore', section, rules: draft ?? committed.rules }, { signal });
        open(signal);
        write(normalized.rules);
        return committed = snapshotOf(normalized.rules);
      }, options);
    },
    async preview({ request, rules }, { signal } = {}) {
      open(signal);
      validatePreview(request);
      let selected;
      if (rules === undefined) {
        await writes;
        open(signal);
        selected = committed?.rules ?? readDisk()?.rules ??
          (await invokeNative({ operation: 'defaults' }, { signal })).rules;
      } else selected = inputRules(rules);
      return invokeNative({ operation: 'preview', request, rules: selected }, { signal });
    },
    normalize({ kind, pattern, replace }, options) {
      open(options?.signal);
      if (!['match', 'clean'].includes(kind) || !validRuleText(pattern) ||
          replace !== undefined && !validRuleText(replace)) throw fault('INVALID_REQUEST');
      return invokeNative({ operation: kind === 'match' ? 'newMatch' : 'newClean', pattern, replace }, options);
    },
    clear(_input = {}, options) {
      return mutate(async signal => {
        const { rules } = await invokeNative({ operation: 'defaults' }, { signal });
        open(signal);
        unchanged();
        try { fs.unlinkSync(path); }
        catch (error) { if (error.code !== 'ENOENT') throw fault('STORAGE_ERROR'); }
        fingerprint = null;
        return committed = snapshotOf(rules);
      }, options);
    },
    async invoke(request, { snapshot: selected, signal } = {}) {
      const current = selected ?? await snapshot();
      validateRules(current.rules);
      return invokeNative({ ...request, rules: current.rules }, { signal });
    },
    close() {
      if (!closing) {
        closed = true;
        closing = (async () => {
          await program.close();
          await Promise.allSettled([...jobs]);
        })();
      }
      return closing;
    },
  };
}
