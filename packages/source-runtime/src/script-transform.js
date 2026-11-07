import { parse } from 'acorn';
import { sourceFault, sourceRequire } from './errors.js';

const functionTypes = new Set([
  'FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression',
]);

function children(node) {
  const result = [];
  for (const [key, value] of Object.entries(node ?? {})) {
    if (['start', 'end', 'loc', 'range'].includes(key)) continue;
    if (Array.isArray(value)) {
      for (const item of value) if (item?.type) result.push([item, key]);
    } else if (value?.type) result.push([value, key]);
  }
  return result;
}

function propertyName(node) {
  if (!node) return '';
  if (!node.computed && node.property?.type === 'Identifier') return node.property.name;
  if (node.computed && node.property?.type === 'Literal') return String(node.property.value);
  return '';
}

function rootName(node) {
  let current = node;
  while (current?.type === 'ChainExpression') current = current.expression;
  while (current?.type === 'MemberExpression') current = current.object;
  return current?.type === 'Identifier' ? current.name : current?.type === 'ThisExpression' ? 'this' : '';
}

function parseProgram(source) {
  try {
    return parse(source, {
      ecmaVersion: 'latest',
      sourceType: 'script',
      allowAwaitOutsideFunction: true,
      allowReturnOutsideFunction: true,
    });
  } catch { throw sourceFault('SCRIPT_PROTOCOL_ERROR'); }
}

function isAlreadyAwaited(node, parentByNode) {
  let current = node, parent = parentByNode.get(current);
  while (parent?.type === 'ChainExpression') {
    current = parent;
    parent = parentByNode.get(current);
  }
  return parent?.type === 'AwaitExpression' && parent.argument === current;
}

function isChained(node, parentByNode) {
  let current = node;
  let parent = parentByNode.get(current);
  while (parent?.type === 'ChainExpression') {
    current = parent;
    parent = parentByNode.get(current);
  }
  return parent?.type === 'MemberExpression' && parent.object === current;
}

function isPromiseReceiver(node, parentByNode) {
  let current = node, parent = parentByNode.get(current);
  while (parent?.type === 'ChainExpression') {
    current = parent;
    parent = parentByNode.get(current);
  }
  return parent?.type === 'MemberExpression' && parent.object === current &&
    ['then', 'catch', 'finally'].includes(propertyName(parent));
}

function asyncCall(node, options) {
  if (node.type !== 'CallExpression') return false;
  let callee = node.callee;
  if (callee.type === 'ChainExpression') callee = callee.expression;
  if (callee.type === 'Identifier') return options.asyncGlobals.has(callee.name);
  if (callee.type !== 'MemberExpression') return false;
  const name = propertyName(callee);
  const root = rootName(callee.object);
  if (options.asyncRoots.has(root) && (!options.asyncMethods.size || options.asyncMethods.has(name))) return true;
  return options.asyncChains.some(chain => {
    const text = sourceSlice(options.source, callee.object).replace(/\s+/g, '');
    return (text.endsWith(chain) || text.includes(`${chain}(`)) && options.asyncMethods.has(name);
  });
}

const sourceSlice = (source, node) => source.slice(node.start, node.end);

function assertFunctionPosition(node, parentByNode) {
  const parent = parentByNode.get(node);
  if (parent?.type === 'MethodDefinition' &&
      ['constructor', 'get', 'set'].includes(parent.kind)) {
    throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
  }
  if (parent?.type === 'Property' && ['get', 'set'].includes(parent.kind)) {
    throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
  }
}

function insertionTextForFunction(node, parentByNode, source) {
  assertFunctionPosition(node, parentByNode);
  const parent = parentByNode.get(node);
  if (node.async) return null;
  if (parent?.type === 'Property' && parent.method) return {
    position: parent.start, text: 'async ', priority: 0,
  };
  if (parent?.type === 'MethodDefinition') {
    if (!parent.static) return { position: parent.start, text: 'async ', priority: 0 };
    const prefix = source.slice(parent.start, parent.key.start);
    const match = /\bstatic\b\s*/.exec(prefix);
    sourceRequire(match, 'UNSUPPORTED_SCRIPT_ASYNC');
    return {
      position: parent.start + match.index + match[0].length,
      text: 'async ', priority: 0,
    };
  }
  return { position: node.start, text: 'async ', priority: 0 };
}

function applyInsertions(source, insertions) {
  const grouped = new Map();
  for (const insertion of insertions) {
    if (!insertion) continue;
    const key = `${insertion.position}:${insertion.text}:${insertion.priority}`;
    if (!grouped.has(key)) grouped.set(key, insertion);
  }
  const ordered = [...grouped.values()].sort((a, b) =>
    a.position - b.position || a.priority - b.priority);
  let result = '', cursor = 0;
  for (const insertion of ordered) {
    sourceRequire(insertion.position >= cursor, 'SCRIPT_PROTOCOL_ERROR');
    result += source.slice(cursor, insertion.position) + insertion.text;
    cursor = insertion.position;
  }
  return result + source.slice(cursor);
}

function addLastExpressionReturn(source, ast) {
  const last = ast.body.at(-1);
  if (!last || last.type !== 'ExpressionStatement') return source;
  return `${source.slice(0, last.start)}return (${source.slice(last.expression.start, last.expression.end)});${source.slice(last.end)}`;
}

/**
 * Converts only statically identifiable synchronous host calls. Unsupported
 * callback/constructor/default-parameter shapes fail explicitly.
 */
export function transformCapabilityScript(source, {
  asyncRoots = ['java'],
  asyncGlobals = [],
  asyncMethods = [
    'ajax', 'ajaxAll', 'connect', 'connectUrl', 'get', 'head', 'post', 'execute',
    'downloadFile', 'importScript', 'startBrowser', 'startBrowserAwait',
    'webView', 'webViewGetOverrideUrl', 'webViewGetSource',
  ],
  asyncChains = ['Jsoup.connect'],
  returnLastExpression = true,
} = {}) {
  sourceRequire(typeof source === 'string');
  let working = source;
  let ast = parseProgram(working);
  if (returnLastExpression) {
    working = addLastExpressionReturn(working, ast);
    ast = parseProgram(working);
  }
  const options = {
    source: working,
    asyncRoots: new Set(asyncRoots),
    asyncGlobals: new Set(asyncGlobals),
    asyncMethods: new Set(asyncMethods),
    asyncChains,
  };
  const parentByNode = new WeakMap();
  const ownerByNode = new WeakMap();
  const scopeByNode = new WeakMap();
  const assignments = [];
  const calls = [];
  const constructions = [];
  const blockTypes = new Set([
    'BlockStatement', 'CatchClause', 'ForStatement', 'ForInStatement',
    'ForOfStatement', 'SwitchStatement', 'StaticBlock', 'ClassDeclaration', 'ClassExpression',
  ]);
  const createScope = (parent, variable = false) => ({
    parent, variable, bindings: new Map(), methods: new Map(),
  });
  const variableScope = scope => {
    while (scope.parent && !scope.variable) scope = scope.parent;
    return scope;
  };
  const bind = (scope, pattern, value = null) => {
    if (!pattern) return;
    if (pattern.type === 'Identifier') {
      if (!scope.bindings.has(pattern.name)) scope.bindings.set(pattern.name, new Set());
      if (value) scope.bindings.get(pattern.name).add(value);
    } else if (pattern.type === 'RestElement') bind(scope, pattern.argument);
    else if (pattern.type === 'AssignmentPattern') bind(scope, pattern.left);
    else if (pattern.type === 'ArrayPattern') {
      for (const element of pattern.elements) bind(scope, element);
    } else if (pattern.type === 'ObjectPattern') {
      for (const property of pattern.properties) {
        bind(scope, property.type === 'RestElement' ? property.argument : property.value);
      }
    }
  };
  function visit(node, parent = null, owner = null, scope = null) {
    parentByNode.set(node, parent);
    const isFunction = functionTypes.has(node.type);
    if (node.type === 'FunctionDeclaration' || node.type === 'ClassDeclaration') {
      const container = parentByNode.get(parent);
      const target = node.type === 'FunctionDeclaration' &&
        parent?.type === 'BlockStatement' && functionTypes.has(container?.type) &&
        container.body === parent ? variableScope(scope) : scope;
      bind(target, node.id, node);
    }
    if (node.type === 'FunctionExpression' && node.id) {
      scope = createScope(scope);
      bind(scope, node.id, node);
    }
    if (node.type === 'Program' || isFunction || blockTypes.has(node.type)) {
      scope = createScope(scope, node.type === 'Program' || isFunction || node.type === 'StaticBlock');
    }
    scopeByNode.set(node, scope);
    if (isFunction) {
      owner = node;
      for (const parameter of node.params) bind(scope, parameter);
      if (parent?.type === 'Property' && !parent.computed) {
        const name = parent.key?.name ?? String(parent.key?.value ?? '');
        const enclosing = scopeByNode.get(parent);
        if (!enclosing.methods.has(name)) enclosing.methods.set(name, new Set());
        enclosing.methods.get(name).add(node);
      }
    }
    ownerByNode.set(node, owner);
    if (node.type === 'VariableDeclarator') {
      bind(parent.kind === 'var' ? variableScope(scope) : scope, node.id, node.init);
    }
    if (node.type === 'CatchClause') bind(scope, node.param);
    if (['ClassDeclaration', 'ClassExpression'].includes(node.type)) bind(scope, node.id, node);
    if (node.type === 'AssignmentExpression' && node.operator === '=' &&
        node.left.type === 'Identifier') assignments.push(node);
    if (node.type === 'CallExpression') calls.push(node);
    if (node.type === 'NewExpression') constructions.push(node);
    for (const [child] of children(node)) visit(child, node, owner, scope);
  }
  visit(ast);
  for (const assignment of assignments) {
    let scope = scopeByNode.get(assignment);
    while (scope.parent && !scope.bindings.has(assignment.left.name)) scope = scope.parent;
    bind(scope, assignment.left, assignment.right);
  }
  const referencedFunctions = (callee, seen = new Set(), result = new Set()) => {
    if (!callee || seen.has(callee)) return result;
    seen.add(callee);
    if (functionTypes.has(callee.type)) {
      result.add(callee);
      return result;
    }
    if (callee.type === 'ChainExpression') return referencedFunctions(callee.expression, seen, result);
    const identifier = callee.type === 'Identifier';
    if (!identifier && callee.type !== 'MemberExpression') return result;
    const name = identifier ? callee.name : propertyName(callee);
    for (let scope = scopeByNode.get(callee); scope; scope = scope.parent) {
      const bindings = identifier ? scope.bindings : scope.methods;
      if (!bindings.has(name)) continue;
      for (const candidate of bindings.get(name)) referencedFunctions(candidate, seen, result);
      return result;
    }
    return result;
  };

  const asyncFunctions = new Set();
  const awaitedCalls = new Set();
  for (const call of calls) {
    if (!asyncCall(call, options)) continue;
    let container = parentByNode.get(call);
    while (container && !functionTypes.has(container.type)) {
      if (['PropertyDefinition', 'StaticBlock'].includes(container.type)) {
        throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
      }
      container = parentByNode.get(container);
    }
    awaitedCalls.add(call);
    const owner = ownerByNode.get(call);
    if (owner) asyncFunctions.add(owner);
  }
  const requiresAwait = callee => {
    for (const fn of referencedFunctions(callee)) {
      if (asyncFunctions.has(fn)) return true;
    }
    return false;
  };
  let changed = true;
  while (changed) {
    changed = false;
    for (const call of calls) {
      if (!requiresAwait(call.callee)) continue;
      const owner = ownerByNode.get(call);
      if (owner && !asyncFunctions.has(owner)) { asyncFunctions.add(owner); changed = true; }
      awaitedCalls.add(call);
    }
  }

  for (const call of calls) {
    if (call.callee?.type !== 'MemberExpression' ||
        !['forEach', 'some', 'every', 'filter', 'find', 'findIndex', 'map', 'flatMap',
          'reduce', 'reduceRight', 'sort'].includes(propertyName(call.callee))) continue;
    if (requiresAwait(call.arguments[0])) throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
  }
  for (const fn of asyncFunctions) {
    if (fn.params.some(parameter => {
      let unsupported = false;
      const inspect = node => {
        if (node?.type === 'AssignmentPattern') unsupported = true;
        for (const [child] of children(node)) inspect(child);
      };
      inspect(parameter);
      return unsupported;
    })) throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
  }
  for (const construction of constructions) {
    if (requiresAwait(construction.callee)) {
      throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
    }
  }

  const insertions = [];
  for (const fn of asyncFunctions) insertions.push(insertionTextForFunction(fn, parentByNode, working));
  for (const call of awaitedCalls) {
    if (!isAlreadyAwaited(call, parentByNode) && !isPromiseReceiver(call, parentByNode)) {
      if (isChained(call, parentByNode)) {
        insertions.push({ position: call.start, text: '(await ', priority: 10 });
        insertions.push({ position: call.end, text: ')', priority: 0 });
      } else {
        insertions.push({ position: call.start, text: 'await ', priority: 10 });
      }
    }
  }
  return applyInsertions(working, insertions);
}
