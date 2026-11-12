import test from 'node:test';
import assert from 'node:assert/strict';
import { createNodeSourceCapabilities } from '../src/source-capabilities.js';
import { createCapabilityClient, hostApiVersion } from '@meowfilm/source-runtime';

const version = hostApiVersion;
const prefix = 'source:web:1:';
const profile = `${prefix}http`;
const url = 'https://books.invalid/private/page';
function setup(options = {}) {
  const requests = [];
  let result = { value: null, url, headers: {} };
  const owner = createNodeSourceCapabilities({
    ...options, web: async input => {
      requests.push(input);
      return options.web ? options.web(input) : result;
    },
  });
  const client = createCapabilityClient({ invoke: (name, input, context) => owner.invoke(name, input, context) });
  const call = (name, input, context) => client.call(name, { version, ...input }, context);
  return {
    owner, requests, call, reply: value => { result = value; },
    seed: (values, target = url, name = profile) => call('cookie.set', { profile: name, url: target, values }),
    get: (target = url, name = profile) => call('cookie.get', { profile: name, url: target }),
    web: (mode, request, signal) => call('web.interact', {
      profile: `${prefix}web`, mode, request,
    }, { signal }),
  };
}

test('Web seeds contain only live source cookies and replace guest cookies without changing raw argument positions', async () => {
  const fixture = setup();
  await fixture.seed([
    'session=private; Path=/private; Secure; HttpOnly; SameSite= sTrIcT',
    'other=domain; Domain=.books.invalid; Path=/; SameSite=Lax',
    'expired=gone; Max-Age=0',
  ]);
  await fixture.seed(['remote=redirect; Path=/; Secure; SameSite=None'], 'https://other.invalid/');
  await fixture.seed(['secret=another-source; Path=/'], url, 'source:other:1:http');
  const expected = [
    { name: 'session', value: 'private', domain: 'books.invalid', hostOnly: true, path: '/private',
      secure: true, httpOnly: true, sameSite: 'Strict', expires: null },
    { name: 'other', value: 'domain', domain: 'books.invalid', hostOnly: false, path: '/',
      secure: false, httpOnly: false, sameSite: 'Lax', expires: null },
    { name: 'remote', value: 'redirect', domain: 'other.invalid', hostOnly: true, path: '/',
      secure: true, httpOnly: false, sameSite: 'None', expires: null },
  ];
  for (const [mode, request] of [
    ['request', { url }],
    ['startBrowser', { arguments: [url, 'Title', '{}'] }],
    ['webview', { arguments: ['<html>Fixture</html>', url, null, '^https://books.invalid/api$'] }],
    ['webViewGetSource', { arguments: [null, url, null, '^https://books.invalid/api$'] }],
  ]) {
    const before = structuredClone(request);
    await fixture.web(mode, { ...request, cookies: [{ name: 'forged', value: 'guest' }] });
    const sent = fixture.requests.at(-1).request;
    assert.deepEqual(sent.cookies, expected);
    assert.equal(sent.headers.Cookie, 'session=private; other=domain');
    if (request.arguments) assert.deepEqual(sent.arguments, request.arguments);
    assert.deepEqual(request, before);
  }
  await fixture.web('webview', { arguments: ['<html>Fixture</html>', null, url] });
  assert.equal(fixture.requests.at(-1).request.headers, undefined, 'a null URL slot must not be compacted');
});

test('explicit Cookie headers create temporary seeds and preserve matching scope and attributes', async () => {
  const fixture = setup();
  await fixture.seed([
    'sid=current; Path=/private; Secure; HttpOnly; SameSite=Strict',
    'duplicate=deep; Path=/private; Secure',
    'duplicate=wide; Path=/; Secure',
  ]);
  await fixture.web('verificationCode', { url, headers: { COOKIE: 'sid=current; duplicate=wide; fresh=new' } });
  const seeds = fixture.requests.at(-1).request.cookies;
  assert.deepEqual(seeds.find(cookie => cookie.name === 'sid'), {
    name: 'sid', value: 'current', domain: 'books.invalid', hostOnly: true, path: '/private',
    secure: true, httpOnly: true, sameSite: 'Strict', expires: null,
  });
  assert.deepEqual(seeds.filter(cookie => cookie.name === 'duplicate').map(cookie => [cookie.value, cookie.path]),
    [['deep', '/private'], ['wide', '/']]);
  assert.deepEqual(seeds.find(cookie => cookie.name === 'fresh'), {
    name: 'fresh', value: 'new', domain: 'books.invalid', hostOnly: true, path: '/private',
    secure: true, httpOnly: false, sameSite: null, expires: null,
  });
  await fixture.web('browser', { url, headers: { cOoKiE: 'sid=temporary' } });
  assert.equal(fixture.requests.at(-1).request.cookies.find(cookie => cookie.name === 'sid').value, 'temporary');
  assert.equal(await fixture.get(), 'sid=current; duplicate=deep; duplicate=wide');
  assert.equal(await fixture.get('https://books.invalid/public'), 'duplicate=wide');
  await fixture.web('browser', { url: 'http://plain.invalid/path/page', headers: { cookie: 'plain=value' } });
  assert.equal(fixture.requests.at(-1).request.cookies.find(cookie => cookie.name === 'plain').secure, false);
});

test('Web origin changes commit once, preserve scalar types, delete cookies and retain __Host scope', async () => {
  const commits = [];
  let fixture;
  fixture = setup({ onProfileChange: name => commits.push([name, fixture.owner.snapshotProfiles(name)]) });
  await fixture.seed(['deleted=old; Path=/']);
  commits.length = 0;
  fixture.reply({ value: false, url: 'https://other.invalid/final',
    headers: { 'set-cookie': ['final=header; Path=/; Secure'] },
    cookieChanges: [
      { url: 'https://books.invalid/', setCookie: '__Host-session=a; Path=/; Secure; HttpOnly; SameSite=Strict' },
      { url: 'https://books.invalid/', setCookie: 'deleted=; Path=/; Max-Age=0' },
      { url: 'https://other.invalid/', setCookie: 'redirect=b; Path=/; Secure; SameSite=None' },
    ] });
  assert.equal(await fixture.web('browser', { url }), false);
  assert.equal(commits.length, 1);
  assert.equal(commits[0][0], prefix);
  assert.equal(await fixture.get('https://books.invalid/'), '__Host-session=a');
  assert.equal(await fixture.get('https://sub.books.invalid/'), '');
  assert.equal(await fixture.get('https://other.invalid/'), 'final=header; redirect=b');
  for (const scalar of [null, false, 7.25]) {
    fixture.reply({ value: scalar, url, headers: {}, cookieChanges: [] });
    assert.equal(await fixture.web('rule', { url }), scalar);
  }
  await fixture.web('browser', { url });
  const host = fixture.requests.at(-1).request.cookies.find(cookie => cookie.name === '__Host-session');
  assert.deepEqual(host, { name: '__Host-session', value: 'a', domain: 'books.invalid', hostOnly: true,
    path: '/', secure: true, httpOnly: true, sameSite: 'Strict', expires: null });
  assert.equal(commits.length, 1, 'empty changes must not write a private snapshot');
  fixture.reply('legacy scalar');
  assert.equal(await fixture.web('rule', { url }), 'legacy scalar');
  fixture.reply({ cookieChanges: 'original business value', other: true });
  assert.deepEqual(await fixture.web('rule', { url }),
    { cookieChanges: 'original business value', other: true });
  fixture.reply({ url, data: 'legacy response', headers: { 'set-cookie': ['legacy=yes; Path=/'] } });
  assert.equal((await fixture.web('request', { url })).data, 'legacy response');
  assert.match(await fixture.get('https://books.invalid/'), /legacy=yes/);
});

test('Web cookie changes are atomic on invalid input and persistent write failure', async () => {
  let failing = false, writes = 0;
  const fixture = setup({ onProfileChange() {
    writes++;
    if (failing) throw Object.assign(new Error('private write failure'), { code: 'STORAGE_ERROR' });
  } });
  await fixture.seed(['accepted=old; Path=/']);
  writes = 0;
  fixture.reply({ value: 0, url, headers: {}, cookieChanges: [
    { url, setCookie: 'accepted=new; Path=/' }, { url: 'file:///private', setCookie: 'bad=value' },
  ] });
  await assert.rejects(fixture.web('browser', { url }), { code: 'INVALID_RESPONSE' });
  assert.equal(writes, 0);
  assert.equal(await fixture.get(), 'accepted=old');
  fixture.reply({ value: 0, url, headers: {}, cookieChanges: [
    { url, setCookie: 'accepted=new; Path=/' },
    { url: 'https://other.invalid/', setCookie: 'remote=new; Path=/' },
  ] });
  failing = true;
  await assert.rejects(fixture.web('browser', { url }), { code: 'STORAGE_ERROR' });
  assert.equal(writes, 1);
  assert.equal(await fixture.get(), 'accepted=old');
  assert.equal(await fixture.get('https://other.invalid/'), '');
});

test('Web cancellation while waiting for a profile transaction prevents cookie persistence', async () => {
  let hold = false, entered, release, writes = 0;
  const ready = new Promise(resolve => { entered = resolve; });
  const blocked = new Promise(resolve => { release = resolve; });
  const fixture = setup({ async onProfileChange() {
    writes++;
    if (hold) { entered(); await blocked; }
  } });
  await fixture.seed(['accepted=old; Path=/']);
  hold = true;
  writes = 0;
  const prior = fixture.seed(['unrelated=current; Path=/']);
  await ready;
  fixture.reply({ value: true, url, headers: {}, cookieChanges: [{ url, setCookie: 'accepted=late; Path=/' }] });
  const controller = new AbortController();
  const operation = fixture.web('browser', { url }, controller.signal);
  const rejected = assert.rejects(operation, { code: 'CANCELLED' });
  await new Promise(resolve => setImmediate(resolve));
  controller.abort();
  release();
  await prior;
  await rejected;
  assert.equal(writes, 1, 'only the preceding unrelated transaction is persisted');
  assert.equal(await fixture.get(), 'accepted=old; unrelated=current');
});

for (const retire of [false, true]) {
  test(`late Web changes do not write ${retire ? 'retired' : 'cancelled'} source profiles`, async () => {
    let release, entered;
    const ready = new Promise(resolve => { entered = resolve; });
    const delayed = new Promise(resolve => { release = resolve; });
    const fixture = setup({ web: async () => { entered(); return delayed; } });
    await fixture.seed(['accepted=old; Path=/']);
    await fixture.seed(['other=untouched; Path=/'], url, 'source:other:1:http');
    const controller = new AbortController();
    const operation = fixture.web('browser', { url }, controller.signal);
    const rejected = assert.rejects(operation, { code: 'CANCELLED' });
    await ready;
    controller.abort();
    if (retire) await fixture.owner.releaseProfiles('source:web:');
    release({ value: true, url, headers: {}, cookieChanges: [{ url, setCookie: 'late=new; Path=/' }] });
    await rejected;
    assert.equal(await fixture.get(), retire ? '' : 'accepted=old');
    assert.equal(await fixture.get(url, 'source:other:1:http'), 'other=untouched');
  });
}

test('private Cookie snapshots restore new attributes and default older records without changing scope', async () => {
  const fixture = setup();
  await fixture.seed(['current=value; Path=/private; Secure; HttpOnly; SameSite=Lax; Expires=Thu, 01 Jan 2099 00:00:00 GMT']);
  const snapshot = fixture.owner.snapshotProfiles(prefix);
  await fixture.owner.releaseProfiles('source:web:');
  fixture.owner.restoreProfiles(prefix, snapshot);
  await fixture.web('browser', { url });
  const current = fixture.requests.at(-1).request.cookies[0];
  assert.equal(current.httpOnly, true);
  assert.equal(current.sameSite, 'Lax');
  assert.equal(current.expires, Date.parse('2099-01-01T00:00:00Z'));
  delete snapshot.cookies[0][1][0][1].httpOnly;
  delete snapshot.cookies[0][1][0][1].sameSite;
  fixture.owner.restoreProfiles(prefix, snapshot);
  await fixture.web('browser', { url });
  assert.deepEqual(fixture.requests.at(-1).request.cookies[0],
    { ...current, httpOnly: false, sameSite: null });
});
