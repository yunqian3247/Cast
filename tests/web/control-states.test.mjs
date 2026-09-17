import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyControlStates(page, output) {
  await page.click('#btnOpenSettings');
  await page.check('#cfgShowPins');
  await page.click('#btnSaveSettingsModal');
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => { theme = value; applyAppearance(); }, theme);
    await page.selectOption('#selPort', 'COM3');
    await page.click('#btnConnect');
    await page.waitForFunction(() => hostMock.status.connected);
    await page.hover('#btnConnect');
    await page.waitForTimeout(200);
    const connected = await page.locator('#btnConnect').evaluate(el => {
      const style = getComputedStyle(el), rgb = style.backgroundColor.match(/\d+/g).map(Number);
      return { red: rgb[0] > rgb[1] * 2 && rgb[0] > rgb[2] * 2, text: style.color, label: el.textContent };
    });
    assert.deepEqual(connected, { red: true, text: 'rgb(255, 255, 255)', label: '关闭端口' });
    await page.check('#chkSendHex');
    await page.check('#chkDtrPin');
    assert.equal(await page.locator('input[type="checkbox"],input[type="radio"]').evaluateAll(inputs => inputs.every(input => getComputedStyle(input).accentColor === getComputedStyle(document.body).getPropertyValue('--primary-bg').trim() || getComputedStyle(input).accentColor === (document.body.classList.contains('theme-dark') ? 'rgb(228, 228, 231)' : 'rgb(24, 24, 27)'))), true);
    await page.screenshot({ path: path.join(output, `web-controls-${theme}-connected.png`) });
    await page.click('#btnConnect');
    await page.waitForFunction(() => !hostMock.status.connected);

    await page.click('#btnPinWindow');
    await page.waitForFunction(() => hostMock.window.topMost);
    await page.hover('#manualInput');
    await page.waitForTimeout(200);
    const pinned = await page.locator('#btnPinWindow').evaluate(el => ({ background: getComputedStyle(el).backgroundColor, fill: getComputedStyle(el.querySelector('svg')).fill, color: getComputedStyle(el).color }));
    assert.equal(pinned.background, 'rgba(0, 0, 0, 0)');
    assert.equal(pinned.fill, pinned.color);
    await page.locator('#desktopTitlebar').screenshot({ path: path.join(output, `web-pin-${theme}-active.png`) });
    await page.click('#btnPinWindow');
    await page.waitForFunction(() => !hostMock.window.topMost);
    assert.equal(await page.locator('#btnPinWindow svg').evaluate(el => getComputedStyle(el).fill), 'none');
    await page.click('#btnExportLog');
    await page.screenshot({ path: path.join(output, `web-selection-${theme}.png`) });
    await page.keyboard.press('Escape');
  }
  await page.evaluate(() => { theme = 'light'; applyAppearance(); });
  console.log('PASS: neutral selections, red disconnect hover, filled pin without persistent background in both themes');
}
