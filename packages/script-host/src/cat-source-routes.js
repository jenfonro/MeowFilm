/** Read-only fallback for small HTTP services without a framework route hook.
 * The parser and this function run in the guest. Only constant expressions and
 * explicit dispatch declarations contribute routes; no source is evaluated. */
export function discoverSourceRoutes(source, parse) {
  let tree;
  try { tree = parse(source, { ecmaVersion: 'latest', allowReturnOutsideFunction: true }); }
  catch { return { paths: [], patterns: [] }; }
  const candidates = [], paths = [], patterns = [];
  const bind = (scope, pattern, value = null) => {
    if (!pattern) return;
    if (pattern.type === 'Identifier') {
      if (scope.bindings.has(pattern.name)) scope.bindings.set(pattern.name, null);
      else scope.bindings.set(pattern.name, value);
    } else if (pattern.type === 'RestElement') bind(scope, pattern.argument);
    else if (pattern.type === 'AssignmentPattern') bind(scope, pattern.left);
    else if (pattern.type === 'ArrayPattern')
      for (const element of pattern.elements) bind(scope, element);
    else if (pattern.type === 'ObjectPattern')
      for (const property of pattern.properties) bind(scope, property.value ?? property.argument);
  };
  function walk(node, parent) {
    if (!node || typeof node.type !== 'string') return;
    const fn = /^(?:FunctionDeclaration|FunctionExpression|ArrowFunctionExpression)$/.test(node.type);
    if (node.type === 'FunctionDeclaration' || node.type === 'ClassDeclaration') bind(parent, node.id);
    const scope = fn || ['Program', 'BlockStatement', 'CatchClause'].includes(node.type)
      ? { parent, bindings: new Map(), function: fn || node.type === 'Program' } : parent;
    if (fn) {
      if (node.type === 'FunctionExpression') bind(scope, node.id);
      for (const parameter of node.params) bind(scope, parameter);
    }
    if (node.type === 'CatchClause') bind(scope, node.param);
    if (node.type === 'VariableDeclaration') {
      let owner = scope;
      if (node.kind === 'var') while (owner && !owner.function) owner = owner.parent;
      for (const declaration of node.declarations)
        bind(owner, declaration.id, node.kind === 'const' && declaration.init
          ? { node: declaration.init, scope } : null);
    }
    if (['CallExpression', 'NewExpression', 'BinaryExpression', 'SwitchStatement'].includes(node.type))
      candidates.push({ node, scope });
    for (const value of Object.values(node)) {
      if (Array.isArray(value)) { for (const child of value) walk(child, scope); }
      else if (value && typeof value === 'object') walk(value, scope);
    }
  }
  walk(tree, { parent: null, bindings: new Map(), function: true });
  function value(node, scope, visiting = new Set()) {
    if (!node) return undefined;
    if (node.type === 'Literal')
      return !node.regex && ['string', 'number'].includes(typeof node.value) ? node.value : undefined;
    if (node.type === 'Identifier') {
      let owner = scope;
      while (owner && !owner.bindings.has(node.name)) owner = owner.parent;
      const binding = owner?.bindings.get(node.name);
      if (!binding || visiting.has(binding)) return undefined;
      visiting.add(binding);
      const result = value(binding.node, binding.scope, visiting);
      visiting.delete(binding);
      return result;
    }
    if (node.type === 'TemplateLiteral') {
      let result = node.quasis[0].value.cooked;
      if (result === null) return undefined;
      for (let index = 0; index < node.expressions.length; index++) {
        const part = value(node.expressions[index], scope, visiting);
        const tail = node.quasis[index + 1].value.cooked;
        if (part === undefined || tail === null) return undefined;
        result += String(part) + tail;
      }
      return result;
    }
    if (node.type === 'BinaryExpression' && node.operator === '+') {
      const left = value(node.left, scope, visiting), right = value(node.right, scope, visiting);
      return left === undefined || right === undefined ? undefined : left + right;
    }
    if (node.type === 'ArrayExpression') {
      const items = node.elements.map(item => value(item, scope, visiting));
      return items.includes(undefined) ? undefined : items;
    }
    return undefined;
  }
  const member = node => node?.type === 'MemberExpression'
    ? node.computed ? node.property.type === 'Literal' ? node.property.value : null : node.property.name
    : null;
  const requestPath = node => ['url', 'pathname'].includes(member(node));
  const record = (path, method = 'POST') => {
    if (typeof path === 'string' && path.startsWith('/') && !path.startsWith('//'))
      paths.push([path, method]);
  };
  const properties = (node, scope) => {
    if (node?.type !== 'ObjectExpression' ||
      node.properties.some(property => property.type !== 'Property' || property.kind !== 'init'))
      return {};
    return Object.fromEntries(node.properties.map(property => [
      property.computed ? value(property.key, scope) : property.key.name ?? property.key.value,
      value(property.value, scope),
    ]));
  };
  for (const { node, scope } of candidates) {
    if (node.type === 'CallExpression') {
      const method = member(node.callee);
      if (['get', 'post', 'put', 'patch', 'delete', 'head', 'options', 'all'].includes(method))
        record(value(node.arguments[0], scope), method === 'all'
          ? ['POST', 'GET', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] : method.toUpperCase());
      if (method === 'route') {
        const route = properties(node.arguments[0], scope);
        record(route.url ?? route.path, route.method ?? 'GET');
      }
      const literal = ['exec', 'test'].includes(method) && requestPath(node.arguments[0])
        ? node.callee.object : method === 'match' && requestPath(node.callee.object)
          ? node.arguments[0] : null;
      const regex = literal?.regex;
      if (regex?.pattern.startsWith('^') && regex.pattern.endsWith('$')) {
        const tail = regex.pattern.match(/\((?:\?:)?([A-Za-z][\w-]*(?:\|[A-Za-z][\w-]*)*)\)\$$/);
        if (tail) patterns.push({ source: regex.pattern, flags: regex.flags.replace(/[gy]/g, ''),
          actions: tail[1].split('|'), method: 'POST' });
      }
    } else if (node.type === 'NewExpression' && node.callee.type === 'Identifier' &&
      node.callee.name === 'Map' && node.arguments[0]?.type === 'ArrayExpression') {
      for (const entry of node.arguments[0].elements) {
        if (entry?.type !== 'ArrayExpression' || entry.elements.length !== 2) continue;
        const key = value(entry.elements[0], scope);
        const route = typeof key === 'string' && key.match(/^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS) (\/.*)$/);
        if (route) record(route[2], route[1]);
      }
    } else if (node.type === 'BinaryExpression' && ['==', '==='].includes(node.operator)) {
      if (requestPath(node.left)) record(value(node.right, scope));
      else if (requestPath(node.right)) record(value(node.left, scope));
    } else if (node.type === 'SwitchStatement' && requestPath(node.discriminant)) {
      for (const branch of node.cases) record(value(branch.test, scope));
    }
  }
  return { paths, patterns };
}

/** A wrapper is one direct call, optionally returned/awaited. */
export function startupWrapperTarget(node) {
  if (!node || !['FunctionExpression', 'ArrowFunctionExpression'].includes(node.type)) return null;
  let expression = node.body;
  if (expression.type === 'BlockStatement') {
    const statements = expression.body.filter(statement => statement.type !== 'EmptyStatement');
    if (statements.length !== 1) return null;
    const statement = statements[0];
    expression = statement.type === 'ReturnStatement' ? statement.argument
      : statement.type === 'ExpressionStatement' ? statement.expression : null;
  }
  if (expression?.type === 'AwaitExpression') expression = expression.argument;
  return expression?.type === 'CallExpression' && expression.callee.type === 'Identifier'
    ? expression.callee.name : null;
}

/** Lazy source lookup only for unresolved closure-local startup wrappers.
 * Acorn tokenizes once to exclude strings/comments, then parses only matching
 * function expressions. It never constructs an AST for the entire bundle or
 * follows arbitrary business calls. The lookup/cache belong to this boot. */
export function createLocalStartupLookup(source, parser, strong, wrapperTarget) {
  const options = { ecmaVersion: 'latest', allowReturnOutsideFunction: true };
  const cache = new Map();
  let declarations;
  const index = () => {
    declarations = new Map();
    const record = (name, start) => {
      const positions = declarations.get(name) ?? [];
      positions.push(start);
      declarations.set(name, positions);
    };
    let previous, beforePrevious;
    for (const token of parser.tokenizer(source, options)) {
      if (token.type.label === 'name' && previous?.type.keyword === 'function')
        record(token.value, beforePrevious?.value === 'async' ? beforePrevious.start : previous.start);
      if (token.type.label === '=' && previous?.type.label === 'name')
        record(previous.value, token.end);
      beforePrevious = previous;
      previous = token;
    }
  };
  const lookup = (name, visiting = new Set()) => {
    if (typeof name !== 'string' || !/^[A-Za-z_$][\w$]*$/.test(name) || visiting.has(name)) return false;
    if (cache.has(name)) return cache.get(name);
    if (!declarations) {
      try { index(); } catch { declarations = new Map(); }
    }
    visiting.add(name);
    let found = false;
    for (const start of declarations.get(name) ?? []) {
      let node;
      try { node = parser.parseExpressionAt(source, start, options); } catch { continue; }
      if (!['FunctionExpression', 'ArrowFunctionExpression'].includes(node.type)) continue;
      if (strong(source.slice(node.start, node.end)) || lookup(wrapperTarget(node), visiting)) {
        found = true;
        break;
      }
    }
    visiting.delete(name);
    cache.set(name, found);
    return found;
  };
  return lookup;
}
