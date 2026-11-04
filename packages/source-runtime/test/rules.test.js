import test from 'node:test';
import assert from 'node:assert/strict';
import {
  DocumentRegistry,
  domText,
  parseMarkup,
  queryDom,
  queryJson,
  readRuleString,
  readRuleValues,
  readStaticRuleString,
  readStaticRuleValues,
  transformCapabilityScript,
} from '../src/index.js';

test('DOM parsing and mutations enforce the configured node budget', () => {
  assert.throws(() => parseMarkup('<i></i>'.repeat(32), 16), { code: 'RESOURCE_LIMIT' });
  const registry = new DocumentRegistry({
    maximumDocuments: 1,
    maximumNodes: 16,
    maximumBytes: 256,
  });
  const { rootId } = registry.parse('<div id="root">ok</div>');
  const [{ nodeId }] = registry.operation('query', { nodeId: rootId, selector: '#root' });
  assert.throws(() => registry.operation('setAttr', {
    nodeId,
    name: 'data-large',
    value: 'x'.repeat(512),
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(registry.operation('attr', { nodeId, name: 'data-large' }), null);
  assert.throws(() => registry.operation('prop', {
    nodeId,
    name: 'value',
    value: 'x'.repeat(512),
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(registry.operation('prop', { nodeId, name: 'value' }), null);
  assert.throws(() => registry.operation('classList', {
    nodeId,
    operation: 'add',
    token: `large-${'x'.repeat(512)}`,
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(registry.operation('attr', { nodeId, name: 'class' }), null);
  assert.throws(() => registry.operation('setHtml', {
    nodeId,
    html: '<i></i>'.repeat(32),
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(registry.operation('html', { nodeId }), 'ok');
  assert.throws(() => registry.operation('prop', {
    nodeId,
    name: 'innerHTML',
    value: '<i></i>'.repeat(32),
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(registry.operation('html', { nodeId }), 'ok');
  const detached = registry.operation('create', { nodeId: rootId, tag: 'section' });
  assert.throws(() => registry.operation('setHtml', {
    nodeId: detached.nodeId,
    html: '<i></i>'.repeat(32),
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(registry.operation('html', { nodeId: detached.nodeId }), '');
  const clone = registry.operation('clone', { nodeId, deep: true });
  assert.equal(registry.operation('prop', {
    nodeId: clone.nodeId,
    name: 'nodeType',
  }), 1);
  assert.equal(registry.operation('html', { nodeId: clone.nodeId }), 'ok');
  registry.close();

  const bytes = new DocumentRegistry({
    maximumDocuments: 1,
    maximumNodes: 64,
    maximumBytes: 128,
  });
  assert.throws(() => bytes.parse(`<div>${'x'.repeat(256)}</div>`), {
    code: 'RESOURCE_LIMIT',
  });
  bytes.close();
});

test('DOM rules cover legacy selectors, slices, attributes and XPath', () => {
  const document = parseMarkup(`
    <main class="items">
      <li data-id="1">alpha</li>
      <li data-id="2">beta <b>child</b></li>
      <li data-id="3">gamma</li>
      <li data-id="4">delta</li>
    </main>
  `);
  assert.deepEqual(
    queryDom(document, 'class.items@tag.li.1:-1@ownText'),
    ['beta', 'gamma'],
  );
  assert.deepEqual(queryDom(document, 'tag.li.1@data-id'), ['2']);
  const [innerHtml] = queryDom(document, 'main@html');
  assert.match(innerHtml, /<li data-id="1">alpha<\/li>/);
  assert.match(innerHtml, /<li data-id="4">delta<\/li>/);
  const xpath = queryDom(document, '@xpath://li[@data-id="3"]');
  assert.equal(xpath.length, 1);
  assert.equal(domText(xpath[0]), 'gamma');
  assert.deepEqual(queryDom(document, '@xpath://li[@data-id="3"]/@data-id'), ['3']);
  assert.deepEqual(queryDom(document, '@xpath:string(//li[@data-id="3"])'), ['gamma']);
});

test('DOM node identity survives queries and traversal without consuming more handles', () => {
  const registry = new DocumentRegistry({
    maximumDocuments: 1,
    maximumNodes: 8,
    maximumBytes: 64,
  });
  const markup = '<div><a></a><a></a></div>';
  const { documentId, rootId } = registry.parse(markup);
  const query = (nodeId, selector) => registry.operation('query', { nodeId, selector });
  const [parent] = query(rootId, 'div');
  const children = query(rootId, 'a');
  const childNodes = registry.operation('childNodes', { nodeId: parent.nodeId });
  assert.deepEqual(children, childNodes);
  assert.equal(query(rootId, ':scope')[0].nodeId, rootId);
  assert.notEqual(children[0].nodeId, children[1].nodeId);
  for (let index = 0; index < 32; index++) {
    assert.deepEqual(query(rootId, 'a'), children);
    assert.deepEqual(registry.operation('children', { nodeId: parent.nodeId }), children);
    for (const child of children) {
      assert.deepEqual(registry.operation('parent', child), parent);
      assert.deepEqual(registry.operation('closest', {
        nodeId: child.nodeId, selector: 'div',
      }), parent);
    }
    assert.deepEqual(registry.operation('next', children[0]), children[1]);
    assert.deepEqual(registry.operation('previous', children[1]), children[0]);
  }
  const clone = registry.operation('clone', { nodeId: children[0].nodeId, deep: true });
  assert.notEqual(clone.nodeId, children[0].nodeId);
  registry.operation('remove', children[0]);
  registry.operation('append', { nodeId: parent.nodeId, childId: children[0].nodeId });
  assert.deepEqual(query(parent.nodeId, 'a'), [children[1], children[0]]);
  assert.equal(registry.release(documentId), true);
  assert.equal(registry.release(documentId), false);
  for (const handle of [rootId, parent.nodeId, ...children.map(child => child.nodeId), clone.nodeId]) {
    assert.throws(() => registry.operation('text', { nodeId: handle }), {
      code: 'INVALID_ARGUMENT',
    });
  }
  const next = registry.parse(markup);
  assert.notEqual(next.rootId, rootId);
  registry.close();
  assert.throws(() => registry.operation('text', { nodeId: next.rootId }), {
    code: 'INVALID_ARGUMENT',
  });
  assert.doesNotThrow(() => registry.parse(markup));
  registry.close();
});

test('DOM release and close restore node and byte budgets while real growth stays bounded', () => {
  const markup = '<div><a></a><a></a></div>';
  const nodeBudget = new DocumentRegistry({
    maximumDocuments: 2,
    maximumNodes: 5,
    maximumBytes: 1024,
  });
  const first = nodeBudget.parse(markup);
  const [parent] = nodeBudget.operation('query', { nodeId: first.rootId, selector: 'div' });
  const created = nodeBudget.operation('create', { nodeId: first.rootId, tag: 'i' });
  assert.throws(() => nodeBudget.operation('create', {
    nodeId: first.rootId, tag: 'b',
  }), { code: 'RESOURCE_LIMIT' });
  assert.throws(() => nodeBudget.operation('setHtml', {
    nodeId: parent.nodeId, html: '<b></b><b></b><b></b>',
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(nodeBudget.operation('html', { nodeId: parent.nodeId }), '<a></a><a></a>');
  const children = nodeBudget.operation('query', { nodeId: first.rootId, selector: 'a' });
  assert.equal(nodeBudget.operation('parent', created), null);
  assert.equal(nodeBudget.operation('text', children[0]), '');
  assert.throws(() => nodeBudget.parse(markup), { code: 'RESOURCE_LIMIT' });
  assert.equal(nodeBudget.release(first.documentId), true);
  assert.doesNotThrow(() => nodeBudget.parse(markup));
  nodeBudget.close();
  assert.doesNotThrow(() => nodeBudget.parse(markup));
  nodeBudget.close();

  const byteBudget = new DocumentRegistry({
    maximumDocuments: 2,
    maximumNodes: 64,
    maximumBytes: 64,
  });
  const value = byteBudget.parse('<p>small</p>');
  const [{ nodeId }] = byteBudget.operation('query', { nodeId: value.rootId, selector: 'p' });
  for (let index = 0; index < 32; index++) {
    assert.equal(byteBudget.operation('query', {
      nodeId: value.rootId, selector: 'p',
    })[0].nodeId, nodeId);
  }
  assert.throws(() => byteBudget.operation('setAttr', {
    nodeId, name: 'data-size', value: 'x'.repeat(64),
  }), { code: 'RESOURCE_LIMIT' });
  assert.equal(byteBudget.operation('attr', { nodeId, name: 'data-size' }), null);
  assert.throws(() => byteBudget.parse(`<p>${'x'.repeat(48)}</p>`), {
    code: 'RESOURCE_LIMIT',
  });
  assert.equal(byteBudget.release(value.documentId), true);
  assert.doesNotThrow(() => byteBudget.parse(`<p>${'x'.repeat(48)}</p>`));
  byteBudget.close();
  assert.doesNotThrow(() => byteBudget.parse(`<p>${'x'.repeat(48)}</p>`));
  byteBudget.close();
});

test('JSONPath, regular expressions and replacements retain structured values', () => {
  const value = {
    groups: [
      { name: 'first', enabled: false },
      { name: 'second', enabled: true },
      { name: 'third', enabled: true },
    ],
  };
  assert.deepEqual(queryJson(value, '$.groups[?(@.enabled == true)].name'), ['second', 'third']);
  assert.deepEqual(queryJson(value, '$..name'), ['first', 'second', 'third']);
  const matches = readStaticRuleValues(':item-(\\d+)', 'item-12 item-34');
  assert.deepEqual(matches.map(match => [match.value, match.groups[0], match.index]), [
    ['item-12', '12', 0],
    ['item-34', '34', 8],
  ]);
});

test('regular expression replacements use matched text in static and asynchronous rules', async () => {
  const content = 'item-12 item-34';
  const context = {
    async evaluateScript() { assert.fail('static regular expressions do not evaluate scripts'); },
  };
  for (const rule of [':item-(\\d+)##item-##', ':item-(\\d+)##item-(\\d+)##$1']) {
    assert.deepEqual(readStaticRuleValues(rule, content), ['12', '34']);
    assert.equal(readStaticRuleString(rule, content), '12\n34');
    assert.deepEqual(await readRuleValues(rule, content, context), ['12', '34']);
    assert.equal(await readRuleString(rule, content, context), '12\n34');
  }
  const matches = [
    { value: 'item-12', groups: ['12'], namedGroups: { id: '12' }, index: 0 },
    { value: 'item-34', groups: ['34'], namedGroups: { id: '34' }, index: 8 },
  ];
  const rule = ':item-(?<id>\\d+)';
  assert.deepEqual(readStaticRuleValues(rule, content), matches);
  assert.deepEqual(await readRuleValues(rule, content, context), matches);
  assert.equal(await readRuleString(rule, content, context), 'item-12\nitem-34');
});

test('@put and @get variables compose with mixed script rules', async () => {
  const variables = new Map();
  const evaluations = [];
  const context = {
    baseUrl: 'https://fixture.invalid/',
    getVariable: key => variables.get(key) ?? '',
    setVariable(key, value) {
      variables.set(key, value);
      return value;
    },
    async evaluateScript(source, bindings) {
      evaluations.push({ source, bindings });
      return `${bindings.result}:${variables.get('title')}`;
    },
  };
  const content = JSON.stringify({ title: 'Fixture' });
  assert.equal(await readRuleString(
    '@put:{"title":"$.title"}@get:{title}',
    content,
    context,
  ), 'Fixture');
  assert.equal(await readRuleString(
    '@put:{"title":"$.title"}<js>result</js>',
    JSON.stringify({ title: 'Fixture' }),
    context,
  ), `${JSON.stringify({ title: 'Fixture' })}:Fixture`);
  assert.equal(evaluations.length, 1);
  assert.equal(evaluations[0].source, 'result');
  assert.equal(evaluations[0].bindings.result, JSON.stringify({ title: 'Fixture' }));
});

test('script transformation propagates await through local callers and rejects unsafe callbacks', () => {
  const transformed = transformCapabilityScript(`
    function load() { return java.get('https://fixture.invalid'); }
    function run() { return load(); }
    run()
  `, {
    asyncRoots: ['java'],
    asyncMethods: ['get'],
  });
  assert.match(transformed, /async function load\(\)/);
  assert.match(transformed, /await java\.get/);
  assert.match(transformed, /async function run\(\)/);
  assert.match(transformed, /await load\(\)/);
  assert.match(transformed, /return \(await run\(\)\)/);

  assert.throws(() => transformCapabilityScript(
    `[1].map(() => java.get('https://fixture.invalid'))`,
    { asyncRoots: ['java'], asyncMethods: ['get'] },
  ), { code: 'UNSUPPORTED_SCRIPT_ASYNC' });
});

test('script transformation rejects host-dependent named and variable Array callbacks', () => {
  const declarations = [
    'function keep(id) { return java.ajax(String(id)) === "yes"; }',
    'const keep = id => java.ajax(String(id)) === "yes";',
    'const keep = function (id) { return java.ajax(String(id)) === "yes"; };',
    'const keep = function predicate(id) { return java.ajax(String(id)) === "yes"; };',
    'function check(id) { return java.ajax(String(id)) === "yes"; } const fn = check; const keep = fn;',
    'let keep; keep = function (id) { return java.ajax(String(id)) === "yes"; };',
    'let keep; const fn = id => java.ajax(String(id)) === "yes"; keep = fn;',
    'function check(id) { return java.ajax(String(id)); } function keep(id) { return check(id) === "yes"; }',
  ];
  for (const declaration of declarations) {
    for (const method of ['forEach', 'some', 'every', 'filter', 'find', 'findIndex',
      'map', 'flatMap', 'reduce', 'reduceRight', 'sort']) {
      assert.throws(() => transformCapabilityScript(`
        ${declaration}
        [1, 2].${method}(keep)
      `), { code: 'UNSUPPORTED_SCRIPT_ASYNC' }, `${method}: ${declaration}`);
    }
    assert.throws(() => transformCapabilityScript(`
      ${declaration}
      [1, 2]['filter'](keep)
    `), { code: 'UNSUPPORTED_SCRIPT_ASYNC' });
  }
});

test('script transformation resolves same-named callbacks in their lexical scopes', () => {
  assert.throws(() => transformCapabilityScript(`
    function a() {
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      return [1].some(keep);
    }
    function b() {
      function keep(x) { return x > 0; }
      return [1].some(keep);
    }
    a()
  `), { code: 'UNSUPPORTED_SCRIPT_ASYNC' });

  for (const declaration of [
    "const keep = x => java.ajax(String(x)) === 'yes';",
    "const keep = function predicate(x) { return java.ajax(String(x)) === 'yes'; };",
    "function load(x) { return java.ajax(String(x)) === 'yes'; } const keep = load;",
    "var keep; keep = x => java.ajax(String(x)) === 'yes';",
  ]) {
    assert.throws(() => transformCapabilityScript(`
      function a() { ${declaration} return [1].some(keep); }
      function b() { const keep = x => x > 0; return [1].some(keep); }
      a()
    `), { code: 'UNSUPPORTED_SCRIPT_ASYNC' });
  }

  assert.throws(() => transformCapabilityScript(`
    const keep = x => x > 0;
    {
      const keep = x => java.ajax(String(x)) === 'yes';
      [1].some(keep);
    }
    [1].some(keep)
  `), { code: 'UNSUPPORTED_SCRIPT_ASYNC' });
});

test('script transformation preserves local synchronous callbacks beside unused host callers', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const unused = `
    function a() {
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      return keep(1);
    }
  `;
  const synchronous = `
    function b() {
      function keep(x) { return x > 0; }
      return [1].some(keep);
    }
    b()
  `;
  for (const source of [
    synchronous,
    unused + synchronous,
    `
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      { const keep = x => x > 0; return [1].some(keep); }
    `,
    `
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      function b(keep) { return [1].some(keep); }
      b(x => x > 0)
    `,
    `
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      function b() {
        var keep = x => x > 0;
        return [1].some(keep);
      }
      b()
    `,
    `
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      try { throw x => x > 0; }
      catch (keep) { return [1].some(keep); }
    `,
  ]) {
    const script = transformCapabilityScript(source);
    assert.equal(await new AsyncFunction('java', script)({
      ajax() { assert.fail('a local synchronous callback must not call the host'); },
    }), true);
  }
});

test('script transformation propagates direct calls and aliases through the same lexical bindings', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const script = transformCapabilityScript(`
    function a() {
      function keep(x) { return java.ajax(String(x)) === 'yes'; }
      const fn = keep;
      return fn(1);
    }
    function b() {
      function keep(x) { return x > 0; }
      const fn = keep;
      return fn(0);
    }
    [a(), b()]
  `);
  const calls = [];
  assert.deepEqual(await new AsyncFunction('java', script)({
    async ajax(value) { calls.push(value); return 'no'; },
  }), [false, false]);
  assert.deepEqual(calls, ['1']);
});

test('script transformation rejects Array callbacks with any host-dependent reassignment or alias candidate', () => {
  for (const declaration of [
    `
      let keep = x => x > 0;
      keep = x => java.ajax(String(x)) === 'yes';
    `,
    `
      let check = x => x > 0;
      check = x => java.ajax(String(x)) === 'yes';
      const keep = check;
    `,
    `
      let check = x => x > 0;
      check = x => java.ajax(String(x)) === 'yes';
      let fn = x => x < 0;
      fn = check;
      const keep = fn;
    `,
    `
      let keep = x => x > 0;
      let fn = keep;
      fn = x => java.ajax(String(x)) === 'yes';
      keep = fn;
    `,
  ]) {
    for (const method of ['forEach', 'some', 'every', 'filter', 'find', 'findIndex',
      'map', 'flatMap', 'reduce', 'reduceRight', 'sort']) {
      assert.throws(() => transformCapabilityScript(`
        ${declaration}
        [1].${method}(keep)
      `), { code: 'UNSUPPORTED_SCRIPT_ASYNC' }, `${method}: ${declaration}`);
    }
  }
});

test('script transformation awaits all possible direct and alias candidates while preserving synchronous values', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const script = transformCapabilityScript(`
    function run(remote) {
      let keep = x => x > 0;
      if (remote) keep = x => java.ajax(String(x)) === 'yes';
      let fn = x => x < 0;
      fn = keep;
      function inner() { return fn(1); }
      return [keep(1), inner()];
    }
    [run(false), run(true)]
  `);
  const calls = [];
  assert.deepEqual(await new AsyncFunction('java', script)({
    async ajax(value) { calls.push(value); return 'no'; },
  }), [[true, true], [false, false]]);
  assert.deepEqual(calls, ['1', '1']);
});

test('script transformation keeps synchronous multi-candidate bindings local to their lexical scope', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const script = transformCapabilityScript(`
    let keep = x => java.ajax(String(x)) === 'yes';
    keep = x => x > 0;
    {
      let keep = x => x < 0;
      keep = x => x > 0;
      let fn = keep;
      fn = x => x === 1;
      return [1].some(fn);
    }
  `);
  assert.equal(await new AsyncFunction('java', script)({
    ajax() { assert.fail('a local synchronous callback must not call the host'); },
  }), true);
});

test('script transformation preserves synchronous callbacks and awaits direct function variables', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const synchronous = transformCapabilityScript(`
    function keep(id) { return id === 2; }
    [1, 2].filter(keep).join(',')
  `);
  assert.equal(await new AsyncFunction(synchronous)(), '2');

  const thisArgument = transformCapabilityScript(`
    const context = () => java.ajax('https://fixture.invalid/');
    [1, 2].filter(id => id === 2, context).join(',')
  `);
  assert.equal(await new AsyncFunction(thisArgument)(), '2');

  for (const source of [
    `
      const keep = function predicate(id) { return java.ajax(String(id)) === 'yes'; };
      keep(2)
    `,
    `
      async function keep(id) { return await java.ajax(String(id)) === 'yes'; }
      await keep(2)
    `,
  ]) {
    assert.equal(await new AsyncFunction('java', transformCapabilityScript(source))({
      ajax: async value => value === '2' ? 'yes' : 'no',
    }), true);
  }
});

test('Jsoup request chains await execute, get and post and reject unsafe function shapes', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  for (const method of ['execute', 'get', 'post']) {
    const script = transformCapabilityScript(`
      const response = Jsoup.connect('https://fixture.invalid/').header('X-Test', 'yes').${method}();
      response.body()
    `);
    let header;
    const builder = {
      header(name, value) { header = [name, value]; return builder; },
      async [method]() { return { body: () => method }; },
    };
    assert.equal(await new AsyncFunction('Jsoup', script)({
      connect: () => builder,
    }), method);
    assert.deepEqual(header, ['X-Test', 'yes']);
    for (const shape of [
      `class Reader { constructor() { Jsoup.connect('https://fixture.invalid/').${method}(); } }`,
      `function read(value = Jsoup.connect('https://fixture.invalid/').${method}()) { return value; } read()`,
      `[1].map(() => Jsoup.connect('https://fixture.invalid/').${method}())`,
    ]) {
      assert.throws(() => transformCapabilityScript(shape), {
        code: 'UNSUPPORTED_SCRIPT_ASYNC',
      });
    }
  }
});
