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

function functionName(node, parent) {
  if (node.id?.type === 'Identifier') return node.id.name;
  if (parent?.type === 'VariableDeclarator' && parent.id?.type === 'Identifier') return parent.id.name;
  if (parent?.type === 'Property' && !parent.computed) {
    return parent.key?.name ?? String(parent.key?.value ?? '');
  }
  return '';
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
    'ajax', 'ajaxAll', 'connect', 'connectUrl', 'get', 'head', 'post',
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
  const functionNames = new Map();
  const calls = [];
  const constructions = [];
  const functions = [];
  const registerFunctionName = (name, node) => {
    if (!name) return;
    if (!functionNames.has(name)) functionNames.set(name, new Set());
    functionNames.get(name).add(node);
  };
  function visit(node, parent = null, owner = null) {
    parentByNode.set(node, parent);
    if (functionTypes.has(node.type)) {
      owner = node;
      functions.push(node);
      registerFunctionName(functionName(node, parent), node);
    }
    ownerByNode.set(node, owner);
    if (node.type === 'CallExpression') calls.push(node);
    if (node.type === 'NewExpression') constructions.push(node);
    for (const [child] of children(node)) visit(child, node, owner);
  }
  visit(ast);
  const referencedFunction = callee => {
    let name = '';
    if (callee?.type === 'Identifier') name = callee.name;
    else if (callee?.type === 'MemberExpression') name = propertyName(callee);
    const candidates = functionNames.get(name);
    return candidates?.size === 1 ? [...candidates][0] : null;
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
  let changed = true;
  while (changed) {
    changed = false;
    for (const call of calls) {
      const callee = referencedFunction(call.callee);
      if (!callee || !asyncFunctions.has(callee)) continue;
      const parent = parentByNode.get(call);
      if (parent?.type === 'CallExpression' && parent.callee?.type === 'MemberExpression' &&
          parent.callee.object === call && ['then', 'catch', 'finally'].includes(propertyName(parent.callee))) continue;
      const owner = ownerByNode.get(call);
      if (owner && !asyncFunctions.has(owner)) { asyncFunctions.add(owner); changed = true; }
      awaitedCalls.add(call);
    }
  }

  for (const fn of asyncFunctions) {
    const parent = parentByNode.get(fn);
    if (fn.params.some(parameter => {
      let unsupported = false;
      const inspect = node => {
        if (node?.type === 'AssignmentPattern') unsupported = true;
        for (const [child] of children(node)) inspect(child);
      };
      inspect(parameter);
      return unsupported;
    })) throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
    if (parent?.type === 'CallExpression' && parent.arguments.includes(fn) &&
        parent.callee?.type === 'MemberExpression') {
      const method = propertyName(parent.callee);
      if (['forEach', 'some', 'every', 'filter', 'find', 'findIndex', 'map', 'flatMap',
        'reduce', 'reduceRight', 'sort'].includes(method)) {
        throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
      }
    }
  }
  for (const construction of constructions) {
    if (asyncFunctions.has(referencedFunction(construction.callee))) {
      throw sourceFault('UNSUPPORTED_SCRIPT_ASYNC');
    }
  }

  const insertions = [];
  for (const fn of asyncFunctions) insertions.push(insertionTextForFunction(fn, parentByNode, working));
  for (const call of awaitedCalls) {
    if (!isAlreadyAwaited(call, parentByNode)) {
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
