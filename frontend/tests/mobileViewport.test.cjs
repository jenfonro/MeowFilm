const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const postcss = require('postcss');

const read = path => readFileSync(resolve(__dirname, '..', path), 'utf8');
const styles = postcss.parse(read('public/css/styles.css'));
const mobileQuery = '(hover: none) and (pointer: coarse)';
const mobileRules = styles.nodes.find(node => node.type === 'atrule' &&
  node.name === 'media' && node.params === mobileQuery);

for (const entry of ['index.html', 'dashboard.html']) {
  test(`${entry} shares the mobile styles without globally locking desktop zoom`, () => {
    const html = read(entry);
    const viewports = [...html.matchAll(/<meta name="viewport" content="([^"]+)"\s*\/?>/g)];
    assert.equal(viewports.length, 1);
    const viewport = Object.fromEntries(viewports[0][1].split(',').map(part => part.trim().split('=')));
    assert.equal(viewport.width, 'device-width');
    assert.equal(Number(viewport['initial-scale']), 1);
    assert.equal(viewport['viewport-fit'], 'cover');
    for (const key of ['minimum-scale', 'maximum-scale', 'user-scalable']) {
      assert.equal(viewport[key], undefined);
    }
    const stylesheet = html.indexOf('<link rel="stylesheet" href="/css/styles.css');
    assert.ok(stylesheet > 0 && stylesheet < html.indexOf('<script type="module"'));
  });
}

test('page zoom is disabled only on primary-touch devices, without disabling either scroll axis', () => {
  assert.ok(mobileRules, 'keep the fix independent of viewport width and desktop mouse input');
  const zoomRules = [];
  styles.walkDecls('touch-action', decl => {
    if (decl.parent.selectors.some(selector => ['html', 'body'].includes(selector))) {
      zoomRules.push(decl);
      assert.equal(decl.parent.parent, mobileRules);
      assert.deepEqual(decl.parent.selectors, ['html', 'body']);
      assert.equal(decl.value, 'pan-x pan-y', 'manipulation permits pinch; none blocks scrolling');
      assert.ok(!decl.important, 'do not override component-specific gestures');
    }
  });
  assert.equal(zoomRules.length, 1);
});

test('mobile text fields override small component fonts without styling range, checkbox or radio controls', () => {
  assert.ok(mobileRules);
  const fields = mobileRules.nodes.find(node => node.type === 'rule' && node.selectors.includes('textarea'));
  assert.ok(fields);
  assert.deepEqual(fields.selectors, [
    'input:not([type="range"]):not([type="checkbox"]):not([type="radio"])',
    'textarea',
    'select',
  ]);
  const size = fields.nodes.find(node => node.prop === 'font-size');
  assert.ok(size);
  assert.equal(size.value, '16px');
  assert.equal(size.important, true, 'scoped login/search/dashboard styles must not reduce this size');
});

test('the mobile fix changes only touch-action and field font size, leaving focus and scrolling native', () => {
  assert.ok(mobileRules);
  const properties = [];
  mobileRules.walkDecls(decl => properties.push(decl.prop));
  assert.deepEqual(properties.sort(), ['font-size', 'touch-action']);
  assert.doesNotMatch(read('src/pages/index/main.js') + read('src/pages/dashboard/main.js'),
    /preventDefault\s*\(|\.blur\s*\(|(?:touchstart|touchmove|touchend|gesturestart|gesturechange|dblclick)/);
});
