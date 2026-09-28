import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyMonitorSettings(page, output) {
  await page.click('#btnOpenSettings');
  assert.equal(await page.inputValue('#cfgMaxLogCount'), '10000');
  assert.equal(await page.inputValue('#cfgRefreshIntervalMs'), '50');
  await page.evaluate(() => {
    logs = []; appendLogs(Array.from({ length: 250 }, (_, i) => ({ id: 95000 + i, timestamp: new Date().toISOString(), dir: 'RX', text: `entry-${i}`, hex: '41', byteCount: 1 })));
    renderLogs();
  });
  await page.fill('#cfgMaxLogCount', '100');
  await page.fill('#cfgRefreshIntervalMs', '1000');
  await page.keyboard.press('Escape');
  assert.equal(await page.evaluate(() => logs.length), 250, 'cancel preserves history');
  await page.click('#btnOpenSettings');
  assert.equal(await page.inputValue('#cfgMaxLogCount'), '10000');
  await page.fill('#cfgMaxLogCount', '99');
  await page.click('#btnSaveSettingsModal');
  assert.equal(await page.locator('#settingsModal').isVisible(), true);
  assert.equal(await page.locator('#cfgMaxLogCount').evaluate(el => el.validity.rangeUnderflow), true);
  await page.fill('#cfgMaxLogCount', '100');
  await page.fill('#cfgRefreshIntervalMs', '20.5');
  await page.click('#btnSaveSettingsModal');
  assert.equal(await page.locator('#cfgRefreshIntervalMs').evaluate(el => el.validity.stepMismatch), true);
  await page.fill('#cfgRefreshIntervalMs', '1000');
  await page.evaluate(() => hostMock.failSave = true);
  await page.click('#btnSaveSettingsModal');
  await page.waitForSelector('#globalToast.show');
  assert.equal(await page.evaluate(() => logs.length), 250, 'failed save preserves history');
  assert.deepEqual(await page.evaluate(() => [maxLogCount, refreshIntervalMs]), [10000, 50]);
  await page.evaluate(() => { hostMock.failSave = false; $('globalToast').classList.remove('show'); });
  await page.click('#btnSaveSettingsModal');
  await page.waitForSelector('#settingsModal.show', { state: 'hidden' });
  assert.deepEqual(await page.evaluate(() => [logs.length, logs[0].id, hostMock.document.ui.maxLogCount, refreshIntervalMs]), [100, 95150, 100, 1000]);

  // Measure the scheduled batch without relying on wall-clock timing in CI.
  const queued = await page.evaluate(() => {
    clearTimeout(renderTimer); renderQueued = false;
    const original = window.setTimeout;
    let delay;
    window.setTimeout = (callback, milliseconds, ...args) => { delay = milliseconds; return original(callback, milliseconds, ...args); };
    try { queueRender(); return delay; } finally { window.setTimeout = original; }
  });
  assert.equal(queued, 1000);
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  await page.click('#btnOpenSettings');
  assert.equal(await page.inputValue('#cfgMaxLogCount'), '100');
  assert.equal(await page.inputValue('#cfgRefreshIntervalMs'), '1000');
  await page.fill('#cfgMaxLogCount', '20000');
  await page.fill('#cfgRefreshIntervalMs', '20');
  await page.click('#btnSaveSettingsModal');
  await page.waitForSelector('#settingsModal.show', { state: 'hidden' });
  await page.evaluate(() => {
    logs = []; appendLogs(Array.from({ length: 12000 }, (_, i) => ({ id: 96000 + i, timestamp: new Date().toISOString(), dir: 'RX', text: 'A', hex: '41', byteCount: 1 })));
  });
  assert.equal(await page.evaluate(() => logs.length), 12000);

  await page.click('#btnOpenSettings');
  const viewport = page.viewportSize();
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => { theme = value; applyAppearance(); }, theme);
    for (const size of [{ width: 880, height: 560 }, { width: 1120, height: 700 }]) {
      await page.setViewportSize(size);
      await page.screenshot({ path: path.join(output, `web-monitor-settings-${theme}-${size.width}.png`) });
      assert.equal(await page.locator('#settingsModal .modal-dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1 && el.getBoundingClientRect().bottom <= innerHeight), true);
    }
  }
  await page.setViewportSize(viewport);
  await page.evaluate(() => { theme = 'light'; applyAppearance(); });
  await page.click('#btnResetSettingsDefault');
  assert.equal(await page.inputValue('#cfgMaxLogCount'), '10000');
  assert.equal(await page.inputValue('#cfgRefreshIntervalMs'), '50');
  await page.click('#btnSaveSettingsModal');
  await page.waitForSelector('#settingsModal.show', { state: 'hidden' });
  assert.deepEqual(await page.evaluate(() => [maxLogCount, refreshIntervalMs, logs.length]), [10000, 50, 10000]);
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  console.log('PASS: monitor limits, refresh interval, validation, cancellation, failed save, persistence and reset');
}
