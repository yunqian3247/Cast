import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyScrolling(page, output) {
  await page.setViewportSize({ width: 1120, height: 700 });
  await page.evaluate(() => {
    clearTimeout(saveTimer);
    const fixture = JSON.parse(localStorage.getItem('fixture'));
    fixture.ui = { ...fixture.ui, autoScroll: true, leftFunction: 'presets', leftDefaultVisible: true, rightDefaultVisible: false, uiFontSize: 12, terminalFontSize: 12 };
    localStorage.setItem('fixture', JSON.stringify(fixture));
  });
  await page.reload();
  await page.waitForSelector('body[data-ready="true"]');
  await page.evaluate(async () => {
    await document.fonts.ready;
    logs = []; displayMode = 'text'; translateProtocol = false;
    document.getElementById('terminalSearch').value = '';
    document.getElementById('filterDir').value = 'all';
    renderLogs();
  });
  assert.equal(await page.getAttribute('#btnToggleRight', 'aria-expanded'), 'false');
  const append = count => page.evaluate(count => {
    const first = (logs.at(-1)?.id || 90000) + 1;
    hostMock.emit({ event: 'logs', data: Array.from({ length: count }, (_, i) => ({
      id: first + i, dir: i % 2 ? 'RX' : 'TX', timestamp: '2026-09-17T08:00:00Z', text: `SCROLL-${first + i}`, hex: '41', byteCount: 1
    })) });
  }, count);
  const atBottom = () => page.waitForFunction(() => {
    const el = document.getElementById('logStream');
    return el.scrollHeight - el.clientHeight - el.scrollTop < 4;
  });
  await append(1);
  await page.waitForSelector('.log-entry[data-id="90001"]');
  await page.locator('.log-entry[data-id="90001"] .protocol-expand').click();
  await append(100);
  await page.waitForSelector('.log-entry[data-id="90101"]');
  await atBottom();

  await page.locator('#logStream').hover();
  await page.mouse.wheel(0, -350);
  await page.waitForFunction(() => !logView.follow && document.getElementById('logStream').scrollTop > 0);
  await page.waitForTimeout(100);
  const anchor = await page.evaluate(() => logView.anchor());
  await append(20);
  await page.waitForFunction(() => logs.length === 121);
  const after = await page.evaluate(() => logView.anchor());
  assert.equal(after.id, anchor.id);
  assert.ok(Math.abs(after.offset - anchor.offset) < 2);
  await page.mouse.wheel(0, 10000);
  await atBottom();
  await append(20);
  await page.waitForSelector('.log-entry[data-id="90141"]');
  await atBottom();
  await page.locator('.log-entry[data-id="90141"] .protocol-expand').click();
  await page.locator('.log-entry[data-id="90141"] .protocol-expand').click();
  await page.waitForFunction(() => logView.follow);

  const scrollbar = await page.locator('#logStream').evaluate(el => {
    const box = el.getBoundingClientRect();
    const thumbHeight = el.clientHeight * el.clientHeight / el.scrollHeight;
    return { x: box.right - (el.offsetWidth - el.clientWidth) / 2, startY: box.bottom - thumbHeight / 2, endY: box.top + thumbHeight / 2 + 10 };
  });
  await page.mouse.move(scrollbar.x, scrollbar.startY);
  await page.mouse.down();
  await page.mouse.move(scrollbar.x, scrollbar.endY, { steps: 8 });
  await page.mouse.up();
  await page.waitForFunction(() => !logView.follow && document.getElementById('logStream').scrollTop < 300);
  await page.click('#btnClearTerminal');
  await page.click('#btnConfirmAction');
  await page.waitForFunction(() => logs.length === 0);
  await append(100);
  await page.waitForSelector('.log-entry[data-id="90100"]');
  await atBottom();

  await page.evaluate(() => {
    documentState.presets = Array.from({ length: 60 }, (_, i) => ({ id: `scroll-${i}`, name: `预设 ${i + 1}`, content: '41', format: 'hex' }));
    documentState.workflows = { scroll: { name: '滚动检查', steps: documentState.presets.map(p => ({ presetId: p.id, wait: 500 })) } };
    currentWorkflow = 'scroll'; renderPresets(); renderWorkflow();
    document.getElementById('workspaceBody').classList.remove('hide-right');
  });
  for (const viewport of [{ width: 1120, height: 700 }, { width: 880, height: 560 }]) {
    await page.setViewportSize(viewport);
    for (const selector of ['#logStream', '#presetListContainer', '#workflowStepsContainer']) {
      const geometry = await page.locator(selector).evaluate(el => {
        const css = getComputedStyle(el), thumb = getComputedStyle(el, '::-webkit-scrollbar-thumb');
        return { overflow: el.scrollHeight > el.clientHeight, track: parseFloat(getComputedStyle(el, '::-webkit-scrollbar').width), left: parseFloat(thumb.borderLeftWidth), right: parseFloat(thumb.borderRightWidth), padding: parseFloat(css.paddingRight) };
      });
      assert.equal(geometry.overflow, true, selector);
      assert.equal(geometry.padding, 0, selector);
      assert.equal(geometry.left, geometry.right, selector);
      assert.equal(geometry.track - geometry.left - geometry.right, 4, selector);
      await page.locator(selector).evaluate(el => el.scrollTop = 0);
      await page.locator(selector).hover();
      await page.mouse.wheel(0, 180);
      await page.waitForFunction(selector => document.querySelector(selector).scrollTop > 0, selector);
    }
    await page.screenshot({ path: path.join(output, `web-scrollbar-insets-${viewport.width}.png`) });
  }
  console.log('PASS: default-window auto-scroll, first-row disclosure, wheel history, resumed following, native scrollbar drag, clear recovery and centered thumbs');
}
