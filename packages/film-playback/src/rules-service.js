import { openRuleStorage } from './rule-storage.js';
import { createHash } from 'node:crypto';
import { isAbsolute } from 'node:path';
import { cloneJSONValue, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { bindRuleSnapshot, filmRuleFailure, filmRuleKernelRevision, filmRuleSource, validRuleText } from './rules.js';
import { freeze } from './identity.js';

const fault = code => Object.assign(new Error(code), { code });
const digest = value => createHash('sha256').update(value).digest('hex');
function copyRules(rules) {
  // This is the document writer's projection, not a preflight for rule execution.
  const text = value => {
    if (!validRuleText(value)) throw fault('INVALID_RULES');
    return value;
  };
  const matches = rows => rows.map(row => ({ pattern: text(row.pattern),
    ...(row.replace === undefined ? {} : { replace: text(row.replace) }),
    ...(row.flags === undefined ? {} : { flags: text(row.flags) }) }));
  try {
    return { episodeCleanRegexRules: rules.episodeCleanRegexRules.map(text),
      episodeRules: matches(rules.episodeRules), movieRules: matches(rules.movieRules),
      aggregateRegexRules: rules.aggregateRegexRules.map(text) };
  } catch { throw fault('INVALID_RULES'); }
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
  return copyRules({ ...emptyRules(), [key]: rows });
}
function previewRequest({ section, text, query }) {
  if (!['episode', 'movie', 'search'].includes(section) || !previewText(text) ||
      section === 'search' && query !== undefined && !previewText(query)) throw fault('INVALID_REQUEST');
  return { section, text: Array.isArray(text) ? [...text] : text,
    ...(section === 'search' && query !== undefined ? { query: Array.isArray(query) ? [...query] : query } : {}) };
}

/** The standalone rule owner publishes only after its typed-row transaction commits. */
export function createFilmRulesService({ dataRoot, nativeSettings, debug = null } = {}) {
  if (typeof dataRoot !== 'string' || !isAbsolute(dataRoot) || dataRoot.includes('\0') ||
      dataRoot.split(/[\\/]/).includes('..')) throw fault('INVALID_REQUEST');
  const storage = nativeSettings ? null : openRuleStorage(dataRoot);
  const program = new NativeJsonProgram(filmRuleSource, { debug, name: 'film-rules' }), jobs = new Set();
  let closed = false, closing, initialized, committed, defaults, basis, stale = false;
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
  async function write(edits, signal) {
    try { basis = await storage.save(basis, defaults, edits, signal); }
    catch (error) { if (error.code === 'STORAGE_CHANGED') stale = true; throw error; }
    stale = false;
    return committed = snapshotOf(basis.rules);
  }
  function snapshotOf(rules) {
    const snapshot = freeze({ rules, revision: digest(stringifyJSONValue([filmRuleKernelRevision(), rules])) });
    return bindRuleSnapshot(snapshot, (request, options) => invokeNative(request, options));
  }
  function invokeNative(request, { signal } = {}) {
    open(signal);
    return tracked(program.invoke(request, { signal }).catch(error => { throw filmRuleFailure(error); }));
  }
  function initialize() {
    return initialized ??= tracked((async () => {
      defaults ??= (await invokeNative({ operation: 'defaults' })).rules;
      open();
      if (storage) {
        basis = await storage.read(defaults);
        committed = snapshotOf(basis.rules);
        stale = false;
      } else {
        const value = nativeSettings.read('film.rules');
        committed = snapshotOf(value === null ? defaults : copyRules(value.rules));
      }
    })().catch(error => {
      initialized = undefined;
      throw error;
    }));
  }
  async function snapshot() {
    open();
    await writes;
    if (stale) initialized = undefined;
    await initialize();
    open();
    return committed;
  }
  function mutate(operation, { signal } = {}) {
    open(signal);
    if (nativeSettings) return Promise.reject(fault('PERMISSION_DENIED'));
    const work = writes.then(async () => {
      open(signal);
      if (stale) initialized = undefined;
      await initialize();
      open(signal);
      return operation(signal);
    });
    writes = work.catch(() => {});
    return tracked(work);
  }
  return {
    get: snapshot, snapshot,
    async settingsChanged() {
      if (!nativeSettings) return;
      await writes;
      await initialize();
      const value = nativeSettings.read('film.rules');
      open();
      committed = snapshotOf(value === null ? defaults : copyRules(value.rules));
    },
    set({ rules }, options) {
      const draft = copyRules(rules);
      return mutate(async signal => {
        const normalized = await invokeNative({ operation: 'save', rules: draft }, { signal });
        open(signal);
        return write(copyRules(normalized.rules), signal);
      }, options);
    },
    setGroup({ group, rules }, options) {
      const [key] = groupInfo(group), draft = groupDraft(group, rules);
      return mutate(async signal => {
        const normalized = await invokeNative({ operation: 'save', rules: draft }, { signal });
        open(signal);
        return write({ [key]: normalized.rules[key] }, signal);
      }, options);
    },
    async groupDefaults({ group }, options) {
      const [key, section] = groupInfo(group);
      const result = await invokeNative({ operation: 'restore', section, rules: emptyRules() }, options);
      return result.rules[key];
    },
    async previewGroup({ group, rules, request }, { signal } = {}) {
      const [key, section] = groupInfo(group);
      const draft = cloneJSONValue(rules);
      request = previewRequest(request);
      if (request.section !== section) throw fault('INVALID_REQUEST');
      open(signal);
      const saved = (await snapshot()).rules;
      open(signal);
      return invokeNative({ operation: 'preview', request,
        rules: { ...saved, [key]: draft } }, { signal });
    },
    restore({ section, rules }, options) {
      if (!['episode', 'movie', 'search'].includes(section)) throw fault('INVALID_REQUEST');
      const draft = rules === undefined ? undefined : cloneJSONValue(rules);
      return mutate(async signal => {
        const normalized = await invokeNative({ operation: 'restore', section, rules: draft ?? committed.rules }, { signal });
        open(signal);
        return write(copyRules(normalized.rules), signal);
      }, options);
    },
    async preview({ request, rules }, { signal } = {}) {
      open(signal);
      request = previewRequest(request);
      let selected;
      if (rules === undefined) {
        selected = (await snapshot()).rules;
        open(signal);
      } else selected = rules;
      return invokeNative({ operation: 'preview', request, rules: selected }, { signal });
    },
    normalize({ kind, pattern, replace }, options) {
      open(options?.signal);
      if (!['match', 'clean'].includes(kind) || !validRuleText(pattern) ||
          kind === 'match' && replace !== undefined && !validRuleText(replace)) throw fault('INVALID_REQUEST');
      return invokeNative({ operation: kind === 'match' ? 'newMatch' : 'newClean', pattern,
        ...(kind === 'match' ? { replace } : {}) }, options);
    },
    clear(_input = {}, options) {
      return mutate(async signal => {
        return write(Object.fromEntries(Object.keys(defaults).map(key => [key, null])), signal);
      }, options);
    },
    diagnostics() {
      return {
        closed,
        activeJobs: jobs.size,
        program: program.stats(),
      };
    },
    close() {
      if (!closing) {
        closed = true;
        closing = (async () => {
          await program.close();
          await Promise.allSettled([...jobs]);
          await storage?.close();
        })();
      }
      return closing;
    },
  };
}
