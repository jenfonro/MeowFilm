import test from 'node:test';
import assert from 'node:assert/strict';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';

const base = {
  bookSourceName: 'Reader compatibility', bookSourceUrl: 'https://books.invalid/',
  searchUrl: 'https://books.invalid/search',
  ruleSearch: { bookList: '.book', name: 'h2@text', bookUrl: 'a@href' },
  ruleBookInfo: { name: 'h1@text', tocUrl: '.toc@href' },
  ruleToc: { chapterList: 'li', chapterName: 'a@text', chapterUrl: 'a@href', nextTocUrl: '.next@href' },
  ruleContent: { title: 'h1@text', content: 'article@html', nextContentUrl: '.next@href', imageStyle: 'FULL' },
};
function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.value;
}
async function fixture(t, source, routes) {
  const requests = [];
  const module = createSourceRuntimeModule({ network: async input => {
    const url = new URL(input.url);
    requests.push(url.pathname);
    const body = typeof routes === 'function' ? await routes(input) : routes[url.pathname];
    assert.notEqual(body, undefined, `unexpected HTTP ${url.pathname}`);
    return { statusCode: 200, headers: {}, body, url: input.url };
  } });
  const rpc = moduleHttp(t, module);
  const imported = okay(await rpc('library.import', { text: JSON.stringify(source) }));
  assert.deepEqual(imported.failures, []);
  const item = imported.sources[0];
  return { rpc, item, requests, call: async (action, payload) => okay(await rpc('library.invoke', {
    installationId: item.installationId, revision: item.revision, action, payload,
  })) };
}

test('directory pre-script reuses book HTML and evaluates chapter metadata and multiple pages', async t => {
  const source = { ...base, ruleToc: { ...base.ruleToc,
    preUpdateJs: `book.tocUrl = 'https://books.invalid/cached'; java.put('pre', 'updated');`,
    isVolume: '.volume@text', isVip: '.vip@text', isPay: '.pay@text', updateTime: '.time@text',
    chapterName: `@js:java.put('row', java.get('pre')); java.getString('a@text')`,
  } };
  const { call, requests } = await fixture(t, source, {
    '/p2': '<li><a href="/chapter/2">Two</a></li><a class="next" href="/cached">cycle</a>',
    '/p3': '<li><a href="/chapter/3">Three</a></li>',
  });
  const result = await call('toc', { book: { bookUrl: 'https://books.invalid/book',
    tocUrl: 'https://books.invalid/old',
    tocHtml: '<li><a href="/chapter/1">One</a><i class="volume">false</i><i class="vip">true</i>' +
      '<i class="pay">1</i><i class="time">Today</i></li>' +
      '<a class="next" href="/p2">2</a><a class="next" href="/p3">3</a>',
    variable: JSON.stringify({ inherited: 'book' }),
  } });
  assert.deepEqual(requests, ['/p2', '/p3']);
  assert.deepEqual(result.chapters.map(chapter => chapter.name), ['One', 'Two', 'Three']);
  assert.equal(result.chapters[0].isVolume, false);
  assert.equal(result.chapters[0].isVip, true);
  assert.equal(result.chapters[0].isPay, true);
  assert.equal(result.chapters[0].updateTime, 'Today');
  assert.equal(JSON.parse(result.chapters[0].variable).row, 'updated');
  assert.equal(JSON.parse(result.book.variable).inherited, 'book');
});

test('content merges pages before source replacement, retains image order and excludes the next chapter', async t => {
  const source = { ...base, ruleContent: { ...base.ruleContent, replaceRegex: '##LEFT\\nRIGHT##Joined' } };
  const { call, requests } = await fixture(t, source, {
    '/chapter/one': '<h1>Actual title</h1><article>LEFT</article>' +
      '<a class="next" href="/nested/page">p2</a><a class="next" href="/chapter/two">next chapter</a>',
    '/nested/page': '<article>RIGHT<p>Before<img src="image.png" alt="illustration">After</p></article>' +
      '<a class="next" href="/chapter/one">cycle</a>',
  });
  const result = await call('content', { book: { bookUrl: 'https://books.invalid/book',
    chapters: [{ chapterUrl: 'https://books.invalid/chapter/one' },
      { chapterUrl: 'https://books.invalid/chapter/two' }] },
    chapter: { chapterUrl: 'https://books.invalid/chapter/one', name: 'Old title', index: 0,
      variable: JSON.stringify({ inherited: 'chapter' }) } });
  assert.deepEqual(requests, ['/chapter/one', '/nested/page']);
  assert.match(result.content, /^Joined/);
  assert.equal(result.title, 'Actual title');
  assert.equal(result.chapter.name, 'Actual title');
  assert.equal(JSON.parse(result.chapter.variable).inherited, 'chapter');
  assert.deepEqual(result.blocks, [
    { type: 'text', text: 'Joined' }, { type: 'text', text: 'Before' },
    { type: 'image', src: 'https://books.invalid/nested/image.png', alt: 'illustration' },
    { type: 'text', text: 'After' },
  ]);
  assert.equal(result.imageStyle, 'FULL');
});

test('pagination follows all distinct pages without an arbitrary 32-page cutoff', async t => {
  const { call, requests } = await fixture(t, base, input => {
    const index = Number(new URL(input.url).pathname.slice(1)) || 1;
    return `<li><a href="/chapter/${index}">Chapter ${index}</a></li>` +
      (index < 40 ? `<a class="next" href="/${index + 1}">next</a>` : '');
  });
  const result = await call('toc', { bookUrl: 'https://books.invalid/book', tocUrl: 'https://books.invalid/1' });
  assert.equal(result.chapters.length, 40);
  assert.equal(requests.length, 40);
});

test('jsLib URL maps are resolved once per generation and current book identity remains stable', async t => {
  const source = { ...base, jsLib: JSON.stringify({ helpers: 'https://books.invalid/library.js' }),
    ruleBookInfo: { name: '@js:decorate("Title")', tocUrl: '.toc@href', bookUrl: '.canonical@href' } };
  const { call, requests } = await fixture(t, source, {
    '/library.js': 'function decorate(value) { return "Lib " + value; }',
    '/book': '<a class="toc" href="/toc">toc</a><a class="canonical" href="/redirected">canonical</a>',
  });
  for (let count = 0; count < 2; count++) {
    const info = await call('bookInfo', { bookUrl: 'https://books.invalid/book', book: { author: 'Writer' } });
    assert.equal(info.book.name, 'Lib Title');
    assert.equal(info.book.author, 'Writer');
    assert.equal(info.book.bookUrl, 'https://books.invalid/book');
  }
  assert.equal(requests.filter(path => path === '/library.js').length, 1);
});

test('concurrent books import and write back their own variables without mixing or source loss', async t => {
  const source = { ...base, ruleContent: { content: `@js:
    const previous = java.get('from');
    java.ajax('https://books.invalid/yield');
    java.put('seen', previous + ':' + book.bookUrl);
    source.put(book.bookUrl, previous);
    previous + ':' + java.get('from')`,
    imageStyle: '',
  } };
  const { call } = await fixture(t, source, { '/chapter': 'body', '/yield': 'yield' });
  const run = name => call('content', {
    book: { bookUrl: `https://books.invalid/${name}`, variable: JSON.stringify({ from: name }) },
    chapter: { chapterUrl: 'https://books.invalid/chapter', name, variable: JSON.stringify({ row: name }) },
  });
  const [a, b] = await Promise.all([run('A'), run('B')]);
  assert.equal(a.content, 'A:A');
  assert.equal(b.content, 'B:B');
  assert.equal(JSON.parse(a.chapter.variable).seen, 'A:https://books.invalid/A');
  assert.equal(JSON.parse(b.chapter.variable).seen, 'B:https://books.invalid/B');
});

test('java.get falls back to source values only when an invocation has no variable', async t => {
  const { call } = await fixture(t, { ...base, ruleContent: { content: `@js:
    source.put('sourceOnly', 'private');
    source.put('shared', 'source');
    java.get('sourceOnly') + ':' + java.get('shared')`,
  } }, { '/chapter': 'body' });
  const result = await call('content', {
    book: { bookUrl: 'https://books.invalid/book', variable: JSON.stringify({ shared: 'book' }) },
    chapter: { chapterUrl: 'https://books.invalid/chapter' },
  });
  assert.equal(result.content, 'private:book');
});

test('source values preserve null and do not expose inherited object properties', async t => {
  const { call } = await fixture(t, { ...base, ruleContent: { content: `@js:
    var absent = source.get('toString');
    source.put('private', 'old');
    source.put('private', null);
    JSON.stringify([absent, source.get('private')])`,
  } }, { '/chapter': 'body' });
  const result = await call('content', { bookUrl: 'https://books.invalid/book',
    chapterUrl: 'https://books.invalid/chapter' });
  assert.equal(result.content, '[null,null]');
});

test('content webJs waits for native input while payAction is a separate explicit action', async t => {
  const { rpc, item, call, requests } = await fixture(t, { ...base,
    ruleContent: { ...base.ruleContent, webJs: 'document.querySelector("article").textContent',
      sourceRegex: 'chapter-body', payAction: '@js:java.ajax("https://books.invalid/purchase"); true' },
  }, { '/purchase': 'success' });
  const content = call('content', { bookUrl: 'https://books.invalid/book',
    chapter: { chapterUrl: 'https://books.invalid/chapter', name: 'Chapter' } });
  let pending;
  for (let count = 0; count < 1000; count++) {
    pending = okay(await rpc('web.pending')).requests[0];
    if (pending) break;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.equal(pending.installationId, item.installationId);
  assert.equal(pending.request.script, 'document.querySelector("article").textContent');
  assert.equal(pending.request.sourceRegex, 'chapter-body');
  okay(await rpc('web.respond', { id: pending.id, result: {
    url: 'https://books.invalid/chapter', data: '<h1>Chapter</h1><article>Dynamic body</article>',
  } }));
  assert.equal((await content).blocks[0].text, 'Dynamic body');
  assert.deepEqual(requests, []);
  assert.equal(item.actions.includes('payAction'), true);
  const purchased = await call('payAction', { bookUrl: 'https://books.invalid/book',
    chapter: { chapterUrl: 'https://books.invalid/chapter', name: 'Chapter' } });
  assert.equal(purchased.value, true);
  assert.deepEqual(requests, ['/purchase']);
});

test('a declared purchase URL enters only its explicit Web action and can be cancelled', async t => {
  const { rpc, item, call, requests } = await fixture(t, { ...base,
    ruleContent: { ...base.ruleContent, payAction: 'https://books.invalid/purchase/{{chapter.index}}' },
  }, { '/chapter': '<article>Unchanged chapter</article>' });
  const payload = { bookUrl: 'https://books.invalid/book',
    chapter: { chapterUrl: 'https://books.invalid/chapter', name: 'Chapter', index: 3 } };
  assert.equal((await call('content', payload)).blocks[0].text, 'Unchanged chapter');
  assert.deepEqual(okay(await rpc('web.pending')).requests, []);
  const purchase = rpc('library.invoke', { installationId: item.installationId, revision: item.revision,
    action: 'payAction', payload });
  let pending;
  for (let count = 0; count < 1000; count++) {
    pending = okay(await rpc('web.pending')).requests[0];
    if (pending) break;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.equal(pending.mode, 'browser');
  assert.equal(pending.request.url, 'https://books.invalid/purchase/3');
  okay(await rpc('web.respond', { id: pending.id, cancelled: true }));
  assert.deepEqual(await purchase, { ok: false, contract: 'source.runtime.v2', code: 'CANCELLED' });
  assert.equal((await call('content', payload)).blocks[0].text, 'Unchanged chapter');
  assert.deepEqual(requests, ['/chapter', '/chapter']);
});

test('QYSG exposes declared pay separately from ordinary content with book and chapter arguments', async t => {
  const { call, requests, item } = await fixture(t, {
    bookSourceName: 'QYSG purchase', bookSourceUrl: 'https://books.invalid/',
    html: `<script>
      function content(url, book) { return 'Normal:' + url; }
      async function pay(book, chapter) {
        await fetch('https://books.invalid/purchase');
        return book + '|' + chapter;
      }
    </script>`,
  }, { '/purchase': 'Synthetic success' });
  const payload = { book: { bookUrl: 'https://books.invalid/book' },
    chapter: { chapterUrl: 'https://books.invalid/chapter' } };
  assert.equal(item.actions.includes('payAction'), true);
  assert.equal((await call('content', payload)).content, 'Normal:https://books.invalid/chapter');
  assert.deepEqual(requests, []);
  assert.equal((await call('payAction', payload)).value,
    'https://books.invalid/book|https://books.invalid/chapter');
  assert.deepEqual(requests, ['/purchase']);
});

test('volume headings do not fetch a chapter page', async t => {
  const { call, requests } = await fixture(t, base, {});
  const result = await call('content', { bookUrl: 'https://books.invalid/book',
    chapter: { chapterUrl: 'Volume One:header', name: 'Volume One', isVolume: true, updateTime: 'Today' } });
  assert.equal(result.content, 'Today');
  assert.deepEqual(result.blocks, [{ type: 'text', text: 'Today' }]);
  assert.deepEqual(requests, []);
});

test('directory volume rules generate a local heading that content can read without HTTP', async t => {
  const { call, requests } = await fixture(t, { ...base, ruleToc: {
    ...base.ruleToc, isVolume: '.volume@text', chapterInfo: '.time@text',
  } }, { '/toc': '<li><a>Volume One</a><i class="volume">true</i><i class="time">Updated</i></li>' });
  const { chapters } = await call('toc', {
    book: { bookUrl: 'https://books.invalid/book', tocUrl: 'https://books.invalid/toc' },
  });
  assert.equal(chapters[0].isVolume, true);
  assert.equal(chapters[0].chapterUrl, 'Volume One0');
  assert.equal(chapters[0].updateTime, 'Updated');
  assert.equal(chapters[0].time, 'Updated');
  const result = await call('content', {
    book: { bookUrl: 'https://books.invalid/book' }, chapter: chapters[0],
  });
  assert.equal(result.content, 'Updated');
  assert.deepEqual(requests, ['/toc']);
});
