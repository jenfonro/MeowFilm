import { parse } from 'acorn';
import { sourceFault, sourceObject, sourceRequire, sourceSafeError } from './errors.js';

const actions = Object.freeze([
  'init',
  'home',
  'homeVideo',
  'category',
  'search',
  'detail',
  'play',
  'proxy',
  'action',
  'destroy',
]);
const entries = Object.freeze([
  '__meowCat', '__JS_SPIDER__', '__jsEvalReturn', 'spider', 'Spider', 'rule',
]);

function normalizeScript(value) {
  let source = String(value ?? '');
  sourceRequire(source.trim().length > 0);
  let program;
  try {
    program = parse(source, {
      ecmaVersion: 'latest',
      sourceType: 'module',
      allowHashBang: true,
    });
  } catch {
    throw sourceFault('SCRIPT_PROTOCOL_ERROR');
  }

  const replacements = [];
  let defaultExport = false;
  let defaultAlias = '';
  for (const statement of program.body) {
    if (statement.type === 'ImportDeclaration' || statement.type === 'ExportAllDeclaration' ||
        statement.type === 'ExportNamedDeclaration' && statement.source) {
      throw sourceFault('UNSUPPORTED_DEPENDENCY');
    }
    if (statement.type === 'ExportDefaultDeclaration') {
      sourceRequire(!defaultExport, 'SCRIPT_PROTOCOL_ERROR');
      defaultExport = true;
      const declaration = statement.declaration;
      const named = ['ClassDeclaration', 'FunctionDeclaration'].includes(declaration.type) &&
        declaration.id?.type === 'Identifier';
      if (named) defaultAlias = declaration.id.name;
      replacements.push({
        start: statement.start,
        end: declaration.start,
        value: named ? '' : 'globalThis.__meowCat = ',
      });
      continue;
    }
    if (statement.type !== 'ExportNamedDeclaration') continue;
    if (statement.declaration) {
      replacements.push({
        start: statement.start,
        end: statement.declaration.start,
        value: '',
      });
      continue;
    }
    let defaultLocal = '';
    for (const specifier of statement.specifiers) {
      const local = specifier.local?.name;
      const exported = specifier.exported?.name ?? specifier.exported?.value;
      sourceRequire(typeof local === 'string' && typeof exported === 'string',
        'SCRIPT_PROTOCOL_ERROR');
      if (exported === 'default') {
        sourceRequire(!defaultExport && !defaultLocal, 'SCRIPT_PROTOCOL_ERROR');
        defaultLocal = local;
      } else {
        sourceRequire(exported === local, 'SCRIPT_PROTOCOL_ERROR');
      }
    }
    if (defaultLocal) {
      defaultExport = true;
      defaultAlias = defaultLocal;
    }
    replacements.push({
      start: statement.start,
      end: statement.end,
      value: '',
    });
  }

  const stack = [program];
  while (stack.length) {
    const node = stack.pop();
    if (node.type === 'ImportExpression' ||
        node.type === 'MetaProperty' && node.meta?.name === 'import') {
      throw sourceFault('UNSUPPORTED_DEPENDENCY');
    }
    if (node.type === 'CallExpression') {
      if (node.callee?.type === 'Identifier' && node.callee.name === 'require') {
        throw sourceFault('UNSUPPORTED_DEPENDENCY');
      }
      let callee = node.callee;
      while (callee?.type === 'ChainExpression') callee = callee.expression;
      while (callee?.type === 'MemberExpression') {
        callee = callee.object;
        while (callee?.type === 'ChainExpression') callee = callee.expression;
      }
      if (callee?.type === 'Identifier' && ['process', 'Deno', 'Bun'].includes(callee.name)) {
        throw sourceFault('UNSUPPORTED_DEPENDENCY');
      }
    }
    for (const child of Object.values(node)) {
      if (Array.isArray(child)) {
        for (const item of child) {
          if (item && typeof item === 'object' && typeof item.type === 'string') stack.push(item);
        }
      } else if (child && typeof child === 'object' && typeof child.type === 'string') {
        stack.push(child);
      }
    }
  }

  for (const replacement of replacements.sort((left, right) => right.start - left.start)) {
    source = source.slice(0, replacement.start) + replacement.value + source.slice(replacement.end);
  }
  if (defaultAlias) source += `\n;globalThis.__meowCat = ${defaultAlias};`;
  return source;
}

function parseValue(value) {
  if (typeof value !== 'string') return value;
  const input = value.trim();
  if (!input || !['{', '['].includes(input[0])) return value;
  try { return JSON.parse(input); } catch { return value; }
}

export class CatAdapter {
  constructor({ source, kernel }) {
    Object.assign(this, { source, kernel });
    this.actions = Object.freeze([]);
    this.initialized = false;
    this.lifecycleTail = null;
  }

  async start() {
    const descriptor = await this.kernel.loadSandbox('cat', normalizeScript(this.source.script), {
      entries,
      actions,
    });
    this.available = new Set(descriptor.actions ?? actions);
    this.actions = Object.freeze(actions.filter(action => this.available.has(action)));
  }

  async guest(action, payload, invocation) {
    sourceRequire(this.available.has(action), 'CAPABILITY_UNAVAILABLE');
    return parseValue(await this.kernel.invokeGuest(action, payload, invocation));
  }

  runLifecycle(action, payload, invocation, automatic = false) {
    // Ordinary guest actions never join this lifecycle-only tail.
    const prior = this.lifecycleTail;
    const task = (async () => {
      if (prior) {
        // A failed owner does not lend its error or identity to its successor.
        try { await prior; } catch {}
      }
      if (invocation.signal.aborted) throw sourceSafeError(invocation.signal.reason, 'CANCELLED');
      if (automatic && this.initialized) return;
      const value = automatic && !this.available.has('init')
        ? undefined : await this.guest(action, payload, invocation);
      // A cancelled owner may settle late; it must not commit lifecycle state.
      if (invocation.signal.aborted) throw sourceSafeError(invocation.signal.reason, 'CANCELLED');
      this.initialized = action !== 'destroy';
      return value;
    })();
    this.lifecycleTail = task;
    const clear = () => {
      if (this.lifecycleTail === task) this.lifecycleTail = null;
    };
    task.then(clear, clear);
    return task;
  }

  async ensureInitialized(payload, invocation) {
    if (this.initialized && !this.lifecycleTail) return;
    await this.runLifecycle('init', {
      ext: this.source.ext ?? this.source.config ?? payload.ext ?? {},
    }, invocation, true);
  }

  async invoke(action, payload, invocation) {
    sourceObject(payload);
    if (action === 'init' || action === 'destroy') {
      return this.runLifecycle(action, payload, invocation);
    }
    await this.ensureInitialized(payload, invocation);
    if (action === 'proxy') {
      const requested = String(payload.url ?? payload.route ?? '');
      const route = this.kernel.resolveVirtual(requested);
      if (route) payload = { ...payload, ...route, route };
      else if (requested.startsWith('meow-source://')) {
        throw sourceFault('VIRTUAL_ROUTE_NOT_FOUND');
      }
    }
    return this.guest(action, payload, invocation);
  }
}
