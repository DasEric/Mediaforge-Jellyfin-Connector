import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../Jellyfin.Plugin.MediaForge/Web/requests.js', import.meta.url), 'utf8');

test('background request refresh keeps rendered content in place', () => {
  assert.match(source, /const initial = Boolean\(options && options\.initial\) \|\| !mineRendered/);
  assert.match(source, /if \(initial\) q\('mine'\)\.innerHTML/);
  assert.match(source, /card\.dataset\.requestId = key/);
  assert.match(source, /existing = new Map/);
  assert.match(source, /updateRequestCard\(card, item/);
});

test('polling pauses outside a visible connected request view', () => {
  assert.match(source, /document\.visibilityState === 'hidden'/);
  assert.match(source, /view\.addEventListener\('viewhide'/);
  assert.match(source, /stopMinePolling\(\)/);
  assert.match(source, /removeEventListener\('visibilitychange', onVisibilityChange\)/);
  assert.match(source, /return function dispose\(\)/);
  assert.match(source, /if \(discoverTimer\) clearTimeout\(discoverTimer\)/);
  assert.match(source, /searchGeneration\+\+/);
  assert.match(source, /detailGeneration\+\+/);
});

test('structural updates preserve the visible request anchor', () => {
  assert.match(source, /anchorId/);
  assert.match(source, /window\.scrollBy\(0, delta\)/);
});
