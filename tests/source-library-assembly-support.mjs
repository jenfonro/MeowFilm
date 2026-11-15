import assert from 'node:assert/strict';

const prefix = 'source.runtime.';
function value(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, 'source.runtime.v2');
  return reply.value;
}

// A self-authored source exercises private state without any external HTTP,
// account or payment. The caller owns the two real App runtime processes.
const definition = {
    bookSourceName: 'App novel persistence fixture',
    bookSourceUrl: 'https://novel-persistence.invalid/',
    searchUrl: 'https://novel-persistence.invalid/search?key={{key}}',
    ruleSearch: { bookList: '.book', name: 'h2@text', bookUrl: 'a@href' },
    coverDecodeJs: `
      var count = Number(source.get('fixture-count') || '0') + 1;
      source.put('fixture-count', String(count));
      String(count)
    `,
};

export async function seedSourceLibrary(client) {
  const imported = value(await client.call(prefix + 'library.import', { text: JSON.stringify(definition) }));
  assert.deepEqual(imported.failures, []);
  assert.equal(imported.sources.length, 1);
  let source = imported.sources[0];
  const result = value(await client.call(prefix + 'library.invoke', {
    installationId: source.installationId, revision: source.revision,
    action: 'image', payload: { body: '', kind: 'cover' },
  }));
  assert.equal(result.body, '1');
  source = value(await client.call(prefix + 'library.update', {
    installationId: source.installationId, expectedRevision: source.revision,
    patch: { group: 'Persistent fixture', order: -2, enabledExplore: false },
  })).source;
  return source;
}

export async function verifyRecoveredSourceLibrary(client, source) {
  const sources = value(await client.call(prefix + 'library.list')).sources;
  assert.deepEqual(sources, [source], 'the App-owned runtime data root retains installation and management fields');
  const result = value(await client.call(prefix + 'library.invoke', {
    installationId: source.installationId, revision: source.revision,
    action: 'image', payload: { body: '', kind: 'cover' },
  }));
  assert.equal(result.body, '2', 'a new native runtime process restores source-private state');
}

export async function seedNativeSourceLibrary(client) {
  let entry = {
    installationId: 'native-novel-fixture', revision: 1, name: definition.bookSourceName,
    sourceUrl: definition.bookSourceUrl, group: '', order: 0, enabled: true, enabledExplore: false,
    protocol: 'legado', definition: JSON.stringify(definition),
  };
  let snapshot = { revision: 1, sources: [entry] };
  let source = value(await client.call(prefix + 'library.apply', snapshot)).sources[0];
  const result = value(await client.call(prefix + 'library.invoke', {
    installationId: source.installationId, revision: source.revision,
    action: 'image', payload: { body: '', kind: 'cover' },
  }));
  assert.equal(result.body, '1');
  entry = { ...entry, revision: 2, group: 'Persistent fixture', order: -2,
    definition: JSON.stringify({ ...definition, bookSourceGroup: 'Persistent fixture',
      customOrder: -2, enabledExplore: false }) };
  snapshot = { revision: 2, sources: [entry] };
  source = value(await client.call(prefix + 'library.apply', snapshot)).sources[0];
  return { source, snapshot };
}

export async function verifyRecoveredNativeSourceLibrary(client, { source, snapshot }) {
  assert.deepEqual(value(await client.call(prefix + 'library.list')).sources, [],
    'a new Windows runtime awaits its native configuration snapshot');
  const applied = value(await client.call(prefix + 'library.apply', snapshot));
  assert.equal(applied.revision, snapshot.revision);
  assert.deepEqual(applied.sources, [source]);
  const result = value(await client.call(prefix + 'library.invoke', {
    installationId: source.installationId, revision: source.revision,
    action: 'image', payload: { body: '', kind: 'cover' },
  }));
  assert.equal(result.body, '2', 'a new Windows runtime process restores only source-private state');
}
