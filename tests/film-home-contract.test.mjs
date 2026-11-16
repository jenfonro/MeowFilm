import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { createDoubanDataModule } from '../apps/runtime/modules/metadata-data.mjs';
import { createFilmPlaybackModule } from '../apps/runtime/modules/film-playback.mjs';

test('Douban recent_hot HTTP entries project real identities while preserving unidentifiable source rows', async t => {
  const area = new URL('../tmp/test-results/film-home-contract/', import.meta.url);
  mkdirSync(area, { recursive: true });
  const dataRoot = mkdtempSync(new URL('runtime-', area)), calls = [];
  const douban = createDoubanDataModule({ dataRoot, request: async url => {
    const address = new URL(url), kind = address.pathname.split('/').at(-1);
    calls.push(address);
    return { status: 200, headers: { 'content-type': 'application/json' }, body: Buffer.from(JSON.stringify({
      items: [
        { title: '缺少作品身份' },
        { id: 100, type: kind, title: '第一部', marker: '来源原字段',
          pic: { normal: 'https://img3.doubanio.com/view/photo/s_ratio_poster/public/p100.jpg' },
          rating: { value: 8.6 } },
        { id: 'not-a-metadata-id', type: kind, title: '非法作品身份' },
        { id: '200', type: kind, title: '第二部', rating: { value: '9.0' } },
      ],
    })) };
  } });
  const film = createFilmPlaybackModule({ dataRoot, modules: [douban] });
  const app = createAppRuntime({ sessionId: randomUUID(), modules: [douban, film] });
  let client;
  t.after(async () => { await client?.close(); await app.close(); rmSync(dataRoot, { recursive: true }); });
  await app.start();
  client = createRuntimeClient({ baseUrl: app.baseUrl });

  for (const [kind, category, type] of [
    ['movie', '热门', '全部'], ['tv', 'tv', 'tv'], ['tv', 'tv', 'tv_animation'], ['tv', 'show', 'show'],
  ]) {
    const input = { provider: 'douban', path: `/rexxar/api/v2/subject/recent_hot/${kind}`,
      query: { start: 0, limit: 12, category, type } };
    const reply = await client.call('film.data.metadata.entry', input);
    assert.equal(reply.ok, true);
    assert.equal(reply.contract, 'film.playback.v1');
    const { value, cache } = reply.value;
    assert.equal(cache.status, 'fresh');
    assert.deepEqual(value.items[0], { title: '缺少作品身份' });
    assert.equal(Object.hasOwn(value.items[2], 'businessId'), false);
    assert.equal(Object.hasOwn(value.items[2], 'playbackTarget'), false);
    for (const [index, id] of [[1, '100'], [3, '200']]) {
      assert.equal(value.items[index].businessId, `douban:${kind}:${id}`);
      assert.deepEqual(value.items[index].playbackTarget, {
        id: `douban:${kind}:${id}`, kind, title: index === 1 ? '第一部' : '第二部',
        metadataProvider: 'douban', metadataId: id,
      });
    }
    assert.equal(value.items[1].id, 100);
    assert.equal(value.items[3].id, '200');
    assert.equal(value.items[1].marker, '来源原字段');
    assert.equal(value.items[1].pic.normal, '/v1/douban/images/view/photo/s_ratio_poster/public/p100.jpg');
    assert.deepEqual([value.items[1].rating.value, value.items[3].rating.value], [8.6, '9.0']);
    assert.equal(calls.at(-1).pathname, input.path);
    assert.deepEqual(Object.fromEntries(calls.at(-1).searchParams),
      { start: '0', limit: '12', category, type });
    assert.deepEqual(await client.call('film.data.metadata.entry', input), reply);
  }
  assert.equal(calls.length, 4);
});
