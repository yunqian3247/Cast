import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyPreferences(page, output) {
  await page.setViewportSize({ width: 1120, height: 700 });
  await verifyViewPreferences(page, output);
  await page.evaluate(() => {
    const fixture = JSON.parse(localStorage.getItem('fixture'));
    fixture.ui.fontFamily = 'Maple Mono NF CN';
    localStorage.setItem('fixture', JSON.stringify(fixture));
  });
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  await page.click('#btnOpenSettings');
  assert.equal(await page.locator('#cfgFontFamily').count(), 0);
  assert.match(await page.locator('#manualInput').evaluate(el => getComputedStyle(el).fontFamily), /^"Sarasa Gothic SC"/);
  assert.equal(await page.isChecked('#cfgRememberInput'), true);
  await page.keyboard.press('Escape');
  await page.fill('#manualInput', 'A5 FA 81 00 60 80 FB');
  await page.waitForFunction(() => hostMock.document.ui.inputDraft === 'A5 FA 81 00 60 80 FB');
  assert.equal(await page.evaluate(() => 'fontFamily' in hostMock.document.ui), false, 'obsolete font selection is removed when saving');
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.inputValue('#manualInput'), 'A5 FA 81 00 60 80 FB');
  await page.fill('#manualInput', '');
  await page.waitForFunction(() => hostMock.document.ui.inputDraft === '');
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.inputValue('#manualInput'), '', 'empty draft overrides send history');
  await page.fill('#manualInput', '01 03');
  await page.click('#btnOpenSettings');
  await page.uncheck('#cfgRememberInput');
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.rememberInput === false);
  assert.equal(await page.inputValue('#manualInput'), '01 03', 'changing retention preserves the active draft');
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.inputValue('#manualInput'), '');

  await page.click('#btnOpenSettings');
  await page.selectOption('#cfgLeftFunction', 'workflow');
  assert.equal(await page.inputValue('#cfgRightFunction'), 'presets');
  await page.uncheck('#cfgLeftDefaultVisible');
  await page.check('#cfgRightDefaultVisible');
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.leftFunction === 'workflow');
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('.left-pane').getAttribute('id'), 'rightPane');
  assert.equal(await page.locator('.right-pane').getAttribute('id'), 'leftPane');
  assert.equal(await page.locator('#btnToggleLeft').getAttribute('aria-expanded'), 'false');
  assert.equal(await page.locator('#btnToggleRight').getAttribute('aria-expanded'), 'true');
  assert.match(await page.locator('#btnToggleLeft').textContent(), /工作流/);
  await page.click('#btnToggleLeft');
  await page.click('#btnToggleRight');
  await page.waitForFunction(() => hostMock.document.ui.leftHidden === false && hostMock.document.ui.rightHidden === true);
  assert.equal(await page.evaluate(() => hostMock.document.ui.leftDefaultVisible), false);
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('#btnToggleLeft').getAttribute('aria-expanded'), 'false', 'startup defaults survive temporary toolbar toggles');
  assert.equal(await page.locator('#btnToggleRight').getAttribute('aria-expanded'), 'true');
  await page.click('#btnToggleLeft');

  await page.click('#btnOpenSettings');
  await page.uncheck('#cfgHoverTips');
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.hoverTips === false);
  for (const target of ['#selPort', '#btnRefreshPorts']) {
    await page.hover(target); await page.waitForTimeout(500);
    assert.equal(await page.locator('#controlTooltip').isVisible(), false);
    assert.equal(await page.locator('#portDetailPopover').isVisible(), false);
  }
  await page.click('#btnOpenSettings');
  await page.check('#cfgHoverTips');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('body').evaluate(el => el.classList.contains('tooltips-disabled')), true, 'cancel restores hover preference');

  async function populateLogs() {
    await page.evaluate(() => {
      logs = []; displayMode = 'hex'; translateProtocol = true;
      const preset = documentState.presets[0];
      appendLogs([{ id: 73000, timestamp: '2026-09-17T00:00:00Z', dir: 'TX', text: '', hex: preset.content, byteCount: 7 }]);
      expandedProtocols.add(logs[0]); renderLogs();
    });
    await page.waitForSelector('.log-entry[data-id="73000"]');
  }
  await populateLogs();
  const typography = await page.evaluate(() => {
    const style = selector => {
      const css = getComputedStyle(document.querySelector(selector));
      return { size: css.fontSize, weight: css.fontWeight, family: css.fontFamily };
    };
    return {
      controls: ['.app-title', '#btnDeleteWorkflow', '#btnNewWorkflow', '#selPort', '#btnConnect', '#btnToggleProtocol', '#btnSend', '#selWorkflow', '#selEnding', '#repeatInterval', '#terminalSearch', '#btnAddStepToWf'].map(style),
      headings: ['.pane-header', '.preset-title', '.step-name'].map(style),
      secondary: ['.preset-desc', '.preset-format-tag', '.send-input-status', '.statusbar'].map(style)
    };
  });
  for (const style of typography.controls) assert.deepEqual({ size: style.size, weight: style.weight }, { size: '12px', weight: '400' });
  for (const style of typography.headings) assert.deepEqual({ size: style.size, weight: style.weight }, { size: '12px', weight: '700' });
  for (const style of typography.secondary) assert.deepEqual({ size: style.size, weight: style.weight }, { size: '11px', weight: '400' });
  for (const style of Object.values(typography).flat()) assert.match(style.family, /^"Sarasa Gothic SC"/);
  for (const size of [11, 14, 16]) {
    await page.click('#btnOpenSettings');
    await page.selectOption('#cfgUiFontSize', String(size));
    await page.selectOption('#cfgTerminalFontSize', '18');
    const sizes = await page.evaluate(() => ({ ui: getComputedStyle(document.body).fontSize, log: getComputedStyle(document.querySelector('.log-text')).fontSize, input: getComputedStyle(document.getElementById('manualInput')).fontSize }));
    assert.deepEqual(sizes, { ui: `${size}px`, log: '18px', input: '18px' });
    await page.click('#btnSaveSettingsModal');
    await page.waitForFunction(expected => hostMock.document.ui.uiFontSize === expected, size);
    for (const viewport of [{ width: 880, height: 560 }, { width: 1920, height: 1080 }]) {
      await page.setViewportSize(viewport); await page.waitForTimeout(120);
      const overflow = await page.evaluate(() => [...document.querySelectorAll('.topbar button,.topbar select,.send-box button,.send-box select,.workflow-body button,.workflow-body input,.workflow-body select,.log-entry,.step-name')].filter(el => {
        if (!el.getClientRects().length || getComputedStyle(el).visibility === 'hidden') return false;
        const box = el.getBoundingClientRect();
        if (el.closest('#workflowStepsContainer')) return el.scrollWidth > el.clientWidth + 1;
        return box.left < 0 || box.right > innerWidth + 1 || box.bottom > innerHeight + 1;
      }).map(el => el.id || el.className));
      assert.deepEqual(overflow, [], `font ${size} at ${viewport.width}`);
      const textSizes = await page.locator('body *').evaluateAll(elements => elements.filter(el => el.getClientRects().length && !el.closest('svg') && [...el.childNodes].some(node => node.nodeType === Node.TEXT_NODE && node.textContent.trim())).map(el => ({ id: el.id || el.className, size: parseFloat(getComputedStyle(el).fontSize) })).filter(item => !Number.isInteger(item.size) || item.size < 11));
      assert.deepEqual(textSizes, [], `readable integer text sizes at ${size}px`);
      await page.screenshot({ path: path.join(output, `web-preferences-font-${size}-${viewport.width}.png`) });
    }
  }

  await page.click('#btnOpenSettings');
  await page.selectOption('#cfgUiFontSize', '12');
  assert.equal(await page.locator('body').evaluate(el => getComputedStyle(el).fontSize), '12px');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('body').evaluate(el => getComputedStyle(el).fontSize), '16px');
  await page.click('#btnOpenSettings');
  await page.selectOption('#cfgUiFontSize', '14');
  await page.evaluate(() => hostMock.failSave = true);
  await page.click('#btnSaveSettingsModal');
  await page.waitForSelector('#globalToast.show');
  assert.equal(await page.locator('body').evaluate(el => getComputedStyle(el).fontSize), '16px', 'failed save restores typography');
  await page.evaluate(() => hostMock.failSave = false);
  await page.keyboard.press('Escape');
  await page.click('#btnToggleTheme');
  await page.click('#btnOpenSettings');
  await page.selectOption('#cfgTerminalFontSize', '20');
  await page.setViewportSize({ width: 880, height: 560 });
  const modalFits = await page.locator('#settingsModal .modal-dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1 && el.getBoundingClientRect().bottom <= innerHeight);
  assert.equal(modalFits, true);
  await page.screenshot({ path: path.join(output, 'web-preferences-dark-large.png') });
  await page.keyboard.press('Escape');
  await page.click('#btnToggleTheme');
  await page.click('#btnOpenSettings');
  await page.click('#btnResetSettingsDefault');
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.uiFontSize === 12 && hostMock.document.ui.hoverTips === true);
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  await page.setViewportSize({ width: 1120, height: 700 });
  assert.equal(await page.locator('.left-pane').getAttribute('id'), 'leftPane');
  await page.hover('#btnRefreshPorts'); await page.waitForSelector('#controlTooltip', { state: 'visible' });
  await page.hover('#manualInput');
  await page.click('#btnOpenSettings');
  await page.screenshot({ path: path.join(output, 'web-preferences-sarasa.png') });
  await page.keyboard.press('Escape');
  const scrollbar = await page.locator('#presetListContainer').evaluate(el => ({ width: getComputedStyle(el, '::-webkit-scrollbar').width, gutter: el.offsetWidth - el.clientWidth }));
  assert.equal(scrollbar.width, '10px'); assert.ok(scrollbar.gutter <= 10);
  const port = await page.locator('#selPort').evaluate(el => ({ padding: parseFloat(getComputedStyle(el).paddingRight), appearance: getComputedStyle(el).appearance }));
  assert.ok(port.padding >= 26); assert.equal(port.appearance, 'none');
  const cdp = await page.context().newCDPSession(page);
  const { root } = await cdp.send('DOM.getDocument');
  await cdp.send('CSS.enable').catch(async () => { await cdp.send('DOM.enable'); await cdp.send('CSS.enable'); });
  await populateLogs();
  await page.fill('#manualInput', '字体检查 A5 FA 81');
  const loadedFonts = await page.evaluate(async () => {
    await document.fonts.ready;
    return [...document.fonts].map(font => ({ family: font.family, weight: font.weight, status: font.status }));
  });
  for (const weight of ['400', '700']) assert.ok(loadedFonts.some(font => font.family === 'Sarasa Gothic SC' && font.weight === weight && font.status === 'loaded'), `bundled font ${weight} loaded: ${JSON.stringify(loadedFonts)}`);
  for (const selector of ['.titlebar-brand', '.app-title', '#btnDeleteWorkflow', '.preset-title', '.log-text']) {
    const { nodeId } = await cdp.send('DOM.querySelector', { nodeId: root.nodeId, selector });
    const { fonts } = await cdp.send('CSS.getPlatformFontsForNode', { nodeId });
    assert.ok(fonts.some(font => font.familyName === 'Sarasa Gothic SC' && font.isCustomFont && font.glyphCount > 0), `actual bundled font for ${selector}: ${JSON.stringify(fonts)}`);
  }
  assert.equal(await page.locator('#manualInput').evaluate(el => getComputedStyle(el).fontFamily), await page.locator('.log-text').first().evaluate(el => getComputedStyle(el).fontFamily));
  await page.evaluate(async () => {
    const node=document.createElement('span');node.id='font-fallback-check';node.style.fontFamily='"Sarasa Gothic SC"';node.textContent='龘';document.body.append(node);await document.fonts.ready;
  });
  const fallbackNode = await cdp.send('DOM.querySelector', { nodeId:root.nodeId,selector:'#font-fallback-check' });
  const fallbackFonts = await cdp.send('CSS.getPlatformFontsForNode', { nodeId:fallbackNode.nodeId });
  assert.ok(fallbackFonts.fonts.some(font=>font.familyName==='Sarasa Gothic SC'&&font.isCustomFont&&font.glyphCount>0), 'full bundled font covers characters outside the UI subset');
  await page.locator('#font-fallback-check').evaluate(el=>el.remove());
  await page.evaluate(() => document.getElementById('workspaceBody').classList.remove('hide-left', 'hide-right'));
  for (const scale of [1, 1.25, 1.5, 2]) {
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1120, height: 700, deviceScaleFactor: scale, mobile: false });
    await page.screenshot({ path: path.join(output, `web-sarasa-ui-scale-${scale * 100}.png`) });
  }
  await cdp.send('Emulation.clearDeviceMetricsOverride');
  await page.fill('#manualInput', '');
  await cdp.detach();
  console.log('PASS: bundled Sarasa Gothic SC regular and bold rendering, consistent text sizes and weights, DPI screenshots, independent font sizes, preferences and slim scrollbars');
}

async function verifyViewPreferences(page, output) {
  await page.evaluate(() => {
    clearTimeout(saveTimer);
    const fixture = JSON.parse(localStorage.getItem('fixture'));
    delete fixture.ui.showPins;
    delete fixture.ui.autoScroll;
    localStorage.setItem('fixture', JSON.stringify(fixture));
  });
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('#btnToggleScroll').count(), 0);
  assert.equal(await page.locator('#statusPins').isVisible(), false);
  assert.equal(await page.evaluate(() => autoScroll), true);
  for (const close of ['#btnCancelSettingsModal', '#btnCloseSettingsModal', 'Escape']) {
    await page.click('#btnOpenSettings');
    assert.equal(await page.isChecked('#cfgAutoScroll'), true);
    assert.equal(await page.isChecked('#cfgShowPins'), false);
    await page.check('#cfgShowPins');
    await page.uncheck('#cfgAutoScroll');
    assert.equal(await page.locator('#statusPins').isVisible(), true);
    if (close === 'Escape') await page.keyboard.press('Escape'); else await page.click(close);
    assert.equal(await page.locator('#statusPins').isVisible(), false);
    assert.equal(await page.evaluate(() => autoScroll), true);
  }
  await page.click('#btnOpenSettings');
  await page.check('#cfgShowPins'); await page.uncheck('#cfgAutoScroll');
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.showPins === true && hostMock.document.ui.autoScroll === false);
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('#statusPins').isVisible(), true);
  assert.equal(await page.evaluate(() => autoScroll), false);
  await page.click('#btnOpenSettings');
  assert.equal(await page.isChecked('#cfgAutoScroll'), false);
  await page.uncheck('#cfgShowPins'); await page.check('#cfgAutoScroll');
  await page.evaluate(() => hostMock.failSave = true);
  await page.click('#btnSaveSettingsModal'); await page.waitForSelector('#globalToast.show');
  assert.equal(await page.locator('#statusPins').isVisible(), true);
  assert.equal(await page.evaluate(() => autoScroll), false);
  await page.evaluate(() => hostMock.failSave = false);
  await page.keyboard.press('Escape');

  await page.selectOption('#selPort', 'COM3'); await page.click('#btnConnect');
  await page.waitForFunction(() => hostMock.status.connected);
  await page.check('#chkDtrPin'); await page.check('#chkRtsPin');
  await page.waitForFunction(() => hostMock.status.pins.dtr && hostMock.status.pins.rts);
  const pinsBefore = await page.evaluate(() => ({ pins: structuredClone(hostMock.status.pins), commands: hostMock.requests.filter(r => r.command === 'pins').length }));
  await page.click('#btnOpenSettings'); await page.uncheck('#cfgShowPins'); await page.click('#btnSaveSettingsModal');
  assert.equal(await page.locator('#statusPins').isVisible(), false);
  assert.deepEqual(await page.evaluate(() => ({ pins: hostMock.status.pins, commands: hostMock.requests.filter(r => r.command === 'pins').length })), pinsBefore);
  await page.evaluate(() => { hostMock.status.pins.dsr = true; hostMock.emit({ event: 'status', data: hostMock.status }); });
  await page.click('#btnOpenSettings'); await page.check('#cfgShowPins'); await page.click('#btnSaveSettingsModal');
  assert.equal(await page.isChecked('#chkDtrPin'), true);
  assert.equal(await page.isChecked('#chkRtsPin'), true);
  assert.equal(await page.textContent('#ledDsrPin'), 'DSR: ●');
  await page.click('#btnConnect'); await page.waitForFunction(() => !hostMock.status.connected);

  await page.evaluate(() => {
    logs = []; displayMode = 'text'; translateProtocol = false;
    document.getElementById('terminalSearch').value = '';
    document.getElementById('filterDir').value = 'all';
    appendLogs(Array.from({ length: 100 }, (_, i) => ({ id: 80000 + i, timestamp: '2026-09-17T00:00:00Z', dir: 'RX', text: `SCROLL-${i}`, hex: '41', byteCount: 1 })));
    renderLogs(); document.getElementById('logStream').scrollTop = 0;
  });
  await page.waitForTimeout(100);
  await page.evaluate(() => hostMock.emit({ event: 'log', data: { id: 80100, timestamp: '2026-09-17T00:00:00Z', dir: 'RX', text: 'NEW', hex: '42', byteCount: 1 } }));
  await page.waitForTimeout(100);
  assert.equal(await page.locator('#logStream').evaluate(el => el.scrollTop), 0, 'disabled auto-scroll preserves position on new records');
  await page.click('#btnOpenSettings'); await page.click('#btnResetSettingsDefault');
  assert.equal(await page.isChecked('#cfgAutoScroll'), true);
  assert.equal(await page.isChecked('#cfgShowPins'), false);
  await page.screenshot({ path: path.join(output, 'web-settings-pins-scroll.png') });
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.showPins === false && hostMock.document.ui.autoScroll === true);
  await page.waitForFunction(() => { const el = document.getElementById('logStream'); return el.scrollHeight - el.clientHeight - el.scrollTop < 4; });
  await page.screenshot({ path: path.join(output, 'web-pins-hidden-scroll-settings.png') });
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('#statusPins').isVisible(), false);
  assert.equal(await page.evaluate(() => autoScroll), true);
  console.log('PASS: pin visibility and auto-scroll defaults, save/reload, cancel, failed save, reset and preserved pin states');
}
