import test from 'node:test';
import assert from 'node:assert/strict';
import {
  DocumentRegistry,
  domText,
  parseMarkup,
  queryDom,
  queryJson,
  readRuleString,
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
