import { decodeJSON, image, number, rows, scalar, text } from './model.mjs';
import { fail } from './errors.mjs';
import { createSyncOperations, settleWrite } from './sync.mjs';
import { createFeedbackOperations } from './feedback.mjs';

export function createMutations() {
  const feedback = createFeedbackOperations(), sync = createSyncOperations();
  return { operations: {
    ...sync.operations, ...feedback.operations,
    async 'vip.signCard'(c) {
      if (c.platform !== 'wy') fail('UNSUPPORTED');
      c.accountRequired();
      const raw = await c.wy('/api/vipnewcenter/app/minidesk/music/sign/pc', { type: 1 }, false, { vip: true });
      const background = image(raw.bgTexture);
      return background ? { text: text(raw.text), subText: text(raw.subText), redPrize: Boolean(raw.redPrize),
        redDay: Boolean(raw.redDay), buttonText: text(raw.btnText), background, days: rows(raw.signInfoList ?? []).map(row => ({
          text: text(row.dayText), signed: Boolean(row.sign), cover: image(row.songCoverUrl),
          signedAt: number(row.signTime), today: Boolean(row.today) })) } : null;
    },
    async 'vip.banner'(c) {
      if (c.platform !== 'wy') fail('UNSUPPORTED');
      c.accountRequired();
      const raw = await c.wy('/api/vipnewcenter/app/resource/common/list', { positionType: 16 }, false, { vip: true });
      for (const value of rows(raw.data?.contentList ?? [])) {
        let row;
        try { row = typeof value === 'string' ? decodeJSON(Buffer.from(value)) : value; }
        catch { continue; } // contentList carries independent optional banner documents.
        if (Number(row.algFlag) === 1 || !image(row.imgUrl) || !['SONG', 'PLAYLIST'].includes(row.resourceType)) continue;
        return { image: image(row.imgUrl), resourceType: row.resourceType, resourceId: scalar(row.resourceId) };
      }
      return null;
    },
    async 'vip.checkIn'(c) {
      if (c.platform !== 'wy') fail('UNSUPPORTED');
      c.accountRequired();
      return settleWrite(c, async callbacks => {
        const raw = await c.wy('/api/vip-center-bff/task/sign', {}, true, callbacks);
        if (!raw.data) fail('INVALID_RESPONSE');
        return {};
      });
    },
  }, invalidate(platform) { sync.invalidate(platform); feedback.invalidate(platform); }, close() { sync.close(); feedback.close(); } };
}
