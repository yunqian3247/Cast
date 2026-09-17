import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyInputSettings(page, output) {
  assert.equal(await page.locator('#selSendHotkey,#cfgSendHotkey,#cfgTheme,#cfgTimestamp,#btnToggleScroll').count(), 0);
  assert.deepEqual(await page.locator('#settingsModal input,#settingsModal select').evaluateAll(elements => elements.map(el => el.id)), ['cfgUiFontSize', 'cfgTerminalFontSize', 'cfgLeftFunction', 'cfgLeftDefaultVisible', 'cfgRightFunction', 'cfgRightDefaultVisible', 'cfgAutoScroll', 'cfgShowPins', 'cfgRememberInput', 'cfgClearAfterSend', 'cfgHoverTips']);
  await page.evaluate(() => {
    const fixture = JSON.parse(localStorage.getItem('fixture'));
    fixture.ui.hotkey = 'ctrlenter';
    fixture.ui.terminalFontSize = 12;
    fixture.ui.clearAfterSend = false;
    fixture.ui.sendHex = false;
    fixture.ui.lineByLine = false;
    localStorage.setItem('fixture', JSON.stringify(fixture));
  });
  await page.reload();
  await page.waitForSelector('body[data-ready="true"]');
  await page.selectOption('#selPort', 'COM3');
  await page.click('#btnConnect');
  await page.waitForFunction(() => !document.getElementById('btnSend').disabled);
  await page.selectOption('#selEnding', 'none');
  await page.fill('#manualInput', 'AT+FIRST');
  await page.press('#manualInput', 'Shift+Enter');
  assert.equal(await page.inputValue('#manualInput'), 'AT+FIRST\n');
  assert.equal(await page.evaluate(() => hostMock.writes.length), 0);
  await page.type('#manualInput', 'AT+SECOND');
  await page.press('#manualInput', 'Enter');
  await page.waitForFunction(() => hostMock.writes.length === 1);
  assert.equal(await page.evaluate(() => hostMock.writes[0].text), 'AT+FIRST\nAT+SECOND');
  await page.fill('#manualInput', '中文输入');
  await page.dispatchEvent('#manualInput', 'keydown', { key: 'Enter', code: 'Enter', isComposing: true });
  await page.dispatchEvent('#manualInput', 'keydown', { key: 'Enter', code: 'Enter', keyCode: 229 });
  await page.dispatchEvent('#manualInput', 'keydown', { key: 'Enter', code: 'Enter', repeat: true });
  await page.waitForTimeout(100);
  assert.equal(await page.evaluate(() => hostMock.writes.length), 1, 'composition and held Enter must not send');
  await page.press('#manualInput', 'Enter');
  await page.waitForFunction(() => hostMock.writes.length === 2);
  await page.check('#chkSendHex');
  await page.fill('#manualInput', '01 03');
  await page.press('#manualInput', 'Shift+Enter');
  await page.type('#manualInput', '00 00');
  await page.press('#manualInput', 'Enter');
  await page.waitForFunction(() => hostMock.writes.length === 3);
  assert.equal(await page.evaluate(() => hostMock.writes[2].text), '01 03\n00 00');
  assert.equal(await page.evaluate(() => hostMock.writes[2].hex), true);
  await page.click('#btnConnect');
  await page.waitForFunction(() => !hostMock.status.connected);
  await page.press('#manualInput', 'Enter');
  await page.waitForTimeout(100);
  assert.equal(await page.evaluate(() => hostMock.writes.length), 3, 'disconnected Enter follows the disabled send button');
  await page.click('#btnOpenShortcuts');
  assert.equal(await page.locator('#shortcutsModal dt').filter({ hasText: /^发送指令$/ }).locator('+ dd').textContent(), 'Enter');
  assert.equal(await page.locator('#shortcutsModal dt').filter({ hasText: /^输入换行$/ }).locator('+ dd').textContent(), 'Shift + Enter');
  await page.screenshot({ path: path.join(output, 'web-enter-operation-tips.png') });
  await page.keyboard.press('Escape');

  async function populateLogs() {
    await page.evaluate(() => {
      logs = [];
      translateProtocol = true; displayMode = 'hex'; autoScroll = false;
      const entry = { id: 71000, timestamp: '2026-09-17T00:00:00Z', dir: 'TX', text: 'AT+FONT', hex: '41 54 2B 46 4F 4E 54', byteCount: 7 };
      documentState.presets = [{ id: 'font', name: '字号显示验证', format: 'text', content: entry.text, desc: '通信内容与发送输入框统一显示字号' }];
      appendLogs([entry]);
      expandedProtocols.add(logs[0]);
      document.getElementById('filterDir').value = 'all';
      document.getElementById('terminalSearch').value = '';
      document.getElementById('selTimestampFormat').value = 'ms';
      renderPresets(); updateViewButtons(); renderLogs();
    });
    await page.waitForSelector('.log-entry[data-id="71000"] .protocol-detail');
  }
  await populateLogs();
  const measured = [];
  async function assertFont(size) {
    const actual = await page.evaluate(() => {
      const row = document.querySelector('.log-entry[data-id="71000"]');
      const elements = [document.getElementById('manualInput'), row, ...row.querySelectorAll('.log-time,.log-dir,.log-text,.proto-tag,.protocol-detail,.log-text-hex')];
      const range = document.createRange();
      range.selectNodeContents(row.querySelector('.proto-tag') || row.querySelector('.log-text'));
      return { sizes: elements.map(el => getComputedStyle(el).fontSize), width: range.getBoundingClientRect().width, overflow: row.scrollWidth > row.clientWidth + 1 };
    });
    assert.ok(actual.sizes.every(value => value === `${size}px`), `font ${size}: ${actual.sizes}`);
    assert.equal(actual.overflow, false);
    return actual.width;
  }
  for (const size of [11, 12, 13]) {
    await page.click('#btnOpenSettings');
    await page.selectOption('#cfgTerminalFontSize', String(size));
    measured.push(await assertFont(size));
    await page.click('#btnSaveSettingsModal');
    await page.waitForFunction(expected => hostMock.document.ui.terminalFontSize === expected, size);
    await assertFont(size);
    await page.screenshot({ path: path.join(output, `web-terminal-font-${size}.png`) });
  }
  assert.ok(measured[0] < measured[1] && measured[1] < measured[2], `rendered glyph widths increase with font size: ${measured}`);
  for (const close of ['#btnCancelSettingsModal', '#btnCloseSettingsModal', 'Escape']) {
    await page.click('#btnOpenSettings');
    await page.selectOption('#cfgTerminalFontSize', '11');
    await assertFont(11);
    if (close === 'Escape') await page.keyboard.press('Escape'); else await page.click(close);
    await assertFont(13);
    assert.equal(await page.evaluate(() => hostMock.document.ui.terminalFontSize), 13);
  }
  await page.reload();
  await page.waitForSelector('body[data-ready="true"]');
  await populateLogs();
  await assertFont(13);
  await page.click('#btnToggleProtocol');
  await page.click('#btnDispBoth');
  await assertFont(13);
  await page.click('#btnOpenSettings');
  await page.click('#btnResetSettingsDefault');
  await assertFont(12);
  await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(() => hostMock.document.ui.terminalFontSize === 12);
  assert.equal(await page.evaluate(() => autoScroll), true, 'settings reset enables automatic scrolling');
  await page.click('#btnOpenSettings');
  await page.screenshot({ path: path.join(output, 'web-settings-trimmed.png') });
  await page.keyboard.press('Escape');
  assert.equal(await page.evaluate(() => 'hotkey' in hostMock.document.ui), false);
  console.log('PASS: Enter send, Shift+Enter newline, IME protection, live font sizes, cancel/reset and persistence');
}
