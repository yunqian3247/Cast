import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyProtocolLogs(page, output) {
  await page.evaluate(() => {
    logs = []; translateProtocol = false; displayMode = 'hex'; autoScroll = true;
    document.getElementById('terminalSearch').value = '';
    document.getElementById('filterDir').value = 'all';
    document.getElementById('selTimestampFormat').value = 'ms';
    document.getElementById('workspaceBody').classList.add('hide-right');
    document.getElementById('workspaceBody').classList.remove('hide-left');
    const longText = 'STATUS=' + '设备运行正常，采样温度为 25.4 ℃。'.repeat(12);
    documentState.presets = [
      { id: 'volume', name: '设置音量', format: 'hex', content: 'A5 FA 81 00 91 B1 FB', desc: '音量设为 5', rxMatch: 'OK', rxDesc: '设备已确认音量设置' },
      { id: 'status', name: '查询设备状态与传感器采样结果'.repeat(5), format: 'text', content: 'AT+STATUS', desc: '读取当前设备运行状态', rxMatch: 'STATUS=', rxDesc: longText.slice(7) },
      { id: 'empty', name: '空释义指令', format: 'text', content: 'AT+EMPTY', desc: '' }
    ];
    const samples = [
      ['TX', '', 'A5 FA 81 00 91 B1 FB'], ['RX', 'OK', '4F 4B'],
      ['TX', 'AT+STATUS', '41 54 2B 53 54 41 54 55 53'], ['RX', 'ERROR: 设备忙，请稍后重试'],
      ['SYS', '端口已连接，115200 bps'], ['RX', '+PONG'], ['RX', '', 'A5 FA 00 00 01 B1 FB'],
      ['TX', 'AT+EMPTY'], ['RX', longText], ['RX', 'A'.repeat(800)]
    ];
    appendLogs(samples.map(([dir, text, hex], i) => {
      hex ??= dir === 'SYS' ? '' : [...new TextEncoder().encode(text)].map(b => b.toString(16).padStart(2, '0').toUpperCase()).join(' ');
      return { id: 60000 + i, timestamp: `2026-09-17T06:32:0${i}.125Z`, dir, text, hex, byteCount: hex ? hex.split(' ').length : 0, source: 'fixture' };
    }));
    renderPresets(); renderWorkflow(); updateViewButtons(); renderLogs();
    logView.follow = false;
    document.getElementById('logStream').scrollTop = 0;
  });
  await page.setViewportSize({ width: 1120, height: 700 });
  const row = page.locator('.log-entry[data-id="60001"]');
  assert.equal(await page.locator('#protocolHeader').count(), 0);
  assert.equal(await page.locator('.protocol-detail').count(), 0);
  assert.equal(await row.locator('.log-text').textContent(), '4F 4B');
  const immediate = await row.evaluate(el => {
    const text = el.querySelector('.log-text');
    const start = performance.now();
    text.click();
    const opened = !!el.querySelector('.protocol-detail');
    const duration = performance.now() - start;
    text.click();
    return { opened, closed: !el.querySelector('.protocol-detail'), duration };
  });
  assert.ok(immediate.opened && immediate.closed, 'row disclosure updates within the click event');
  console.log(`Row disclosure update: ${immediate.duration.toFixed(1)} ms`);
  for (const selector of ['.log-time', '.log-dir', '.log-text']) {
    await row.locator(selector).click();
    await row.locator('.protocol-detail').waitFor();
    await row.locator(selector).click();
    await row.locator('.protocol-detail').waitFor({ state: 'detached' });
  }
  await row.locator('.log-text').dblclick();
  await page.waitForTimeout(350);
  assert.equal(await row.locator('.protocol-detail').count(), 0, 'double-click copies without opening the row');
  assert.equal(await page.evaluate(() => hostMock.clipboard), '4F 4B');
  await row.locator('[data-log-action="expand"]').click();
  assert.equal(await row.locator('.protocol-detail').textContent(), '设备已确认音量设置');
  const compact = await row.evaluate(el => {
    const text = el.querySelector('.log-text').getBoundingClientRect(), detail = el.querySelector('.protocol-detail').getBoundingClientRect();
    return { height: el.getBoundingClientRect().height, textHeight: text.height, detailHeight: detail.height, gap: detail.top - text.bottom };
  });
  assert.ok(compact.height <= 46 && compact.gap <= 1 && compact.textHeight === 20 && compact.detailHeight <= 22, JSON.stringify(compact));
  assert.equal(await row.locator('.protocol-detail button,.protocol-detail-title,.protocol-payload').count(), 0);
  await page.mouse.move(0, 0);
  await row.locator('[data-log-action="expand"]').hover();
  await page.waitForSelector('#controlTooltip', { state: 'visible' });
  assert.equal(await page.textContent('#controlTooltip'), '收起协议释义');
  assert.equal(await page.locator('#controlTooltip').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 255, 255)');
  await page.mouse.move(0, 0);

  async function assertGeometry(label) {
    const geometry = await page.evaluate(() => {
      const stream = document.getElementById('logStream'), rows = [...stream.querySelectorAll('.log-entry')];
      const columns = ['.log-time', '.log-dir', '.log-text', '.protocol-expand'];
      const first = rows[0];
      return {
        overflow: stream.scrollWidth > stream.clientWidth + 1 || rows.some(row => row.scrollWidth > row.clientWidth + 1),
        aligned: rows.every(row => columns.every(selector => {
          const cell = row.querySelector(selector), reference = first.querySelector(selector);
          return !cell || Math.abs(cell.getBoundingClientRect().left - reference.getBoundingClientRect().left) < 1;
        })),
        details: rows.every(row => {
          const detail = row.querySelector('.protocol-detail');
          if (!detail) return true;
          const data = row.querySelector('.log-text').getBoundingClientRect(), box = detail.getBoundingClientRect();
          return Math.abs(box.left - data.left) < 1 && box.top >= data.bottom && detail.scrollWidth <= detail.clientWidth + 1;
        }),
        overlapping: rows.some((row, i) => i > 0 && row.getBoundingClientRect().top < rows[i - 1].getBoundingClientRect().bottom - 1),
        clipped: rows.some(row => { const style = getComputedStyle(row.querySelector('.log-text')); return style.textOverflow === 'ellipsis' || style.whiteSpace === 'nowrap'; })
        , inset: rows.every(row => row.querySelector('.log-time,.log-dir').getBoundingClientRect().left - row.getBoundingClientRect().left >= 8)
        , equalGaps: rows.every(row => {
          const time = row.querySelector('.log-time'), dir = row.querySelector('.log-dir'), data = row.querySelector('.log-text');
          if (!time) return true;
          const range = document.createRange();
          range.selectNodeContents(time);
          const timeBox = range.getBoundingClientRect();
          range.selectNodeContents(dir);
          const dirBox = range.getBoundingClientRect();
          const leftGap = dirBox.left - timeBox.right, rightGap = data.getBoundingClientRect().left - dirBox.right;
          return leftGap >= 11 && Math.abs(leftGap - rightGap) < 1;
        })
        , lineBoxes: rows.every(row => {
          const time = row.querySelector('.log-time'), dir = row.querySelector('.log-dir'), data = row.querySelector('.log-text');
          return (!time || getComputedStyle(time).lineHeight === getComputedStyle(data).lineHeight) && getComputedStyle(dir).lineHeight === getComputedStyle(data).lineHeight;
        })
      };
    });
    assert.deepEqual(geometry, { overflow: false, aligned: true, details: true, overlapping: false, clipped: false, inset: true, equalGaps: true, lineBoxes: true }, label);
  }

  for (const translated of [false, true]) {
    if (translated) await page.click('#btnToggleProtocol');
    const systemRow = page.locator('.log-entry[data-id="60004"]');
    await systemRow.scrollIntoViewIfNeeded();
    assert.equal(await systemRow.locator('.log-text').textContent(), '端口已连接，115200 bps');
    assert.equal(await systemRow.locator('button,.protocol-detail').count(), 0);
    for (const size of [{ width: 1120, height: 700 }, { width: 880, height: 560 }, { width: 1440, height: 900 }]) {
      await page.setViewportSize(size);
      await page.evaluate(narrow => document.getElementById('workspaceBody').classList.toggle('hide-right', !narrow), size.width === 880);
      await page.locator('#logStream').evaluate(el => el.scrollTop = 0);
      await page.waitForTimeout(120);
      await assertGeometry(`protocol=${translated} at ${size.width}`);
      assert.equal(await row.locator('.protocol-detail').count(), 1, 'expanded state survives mode and width changes');
      await page.screenshot({ path: path.join(output, `web-protocol-${translated ? 'translated' : 'raw'}-${size.width}.png`) });
    }
    for (const mode of ['text', 'hex', 'both']) {
      await page.click({ text: '#btnDispText', hex: '#btnDispHex', both: '#btnDispBoth' }[mode]);
      assert.equal(await row.locator('.log-text').textContent(), translated ? '设置音量·应答' : { text: 'OK', hex: '4F 4B', both: 'OK4F 4B' }[mode]);
      await row.locator('.log-text').dblclick();
      await page.waitForFunction(expected => hostMock.clipboard === expected, { text: 'OK', hex: '4F 4B', both: 'OK\n4F 4B' }[mode]);
      await page.waitForTimeout(300);
      assert.equal(await row.locator('.protocol-detail').count(), 1, 'double-click preserves the expanded row');
    }
    await page.evaluate(() => { clearTimeout(toastTimer); document.getElementById('globalToast').classList.remove('show'); });
  }
  await page.locator('.log-entry[data-id="60003"]').waitFor();
  assert.equal(await page.locator('.log-entry[data-id="60003"]').evaluate(el => el.classList.contains('protocol-error')), true);
  const longRow = page.locator('.log-entry[data-id="60008"]');
  await longRow.locator('[data-log-action="expand"]').click();
  assert.equal(await longRow.locator('.protocol-detail').textContent(), await page.evaluate(() => documentState.presets[1].rxDesc));
  await assertGeometry('long translated name and description');
  await page.click('#btnToggleProtocol');
  await longRow.locator('.log-text').waitFor();
  assert.equal(await longRow.locator('.log-text > span').first().textContent(), await page.evaluate(() => logs.find(log => log.id === 60008).text));
  await assertGeometry('long raw frame and description');
  await page.click('#btnDispText');
  const unbroken = page.locator('.log-entry[data-id="60009"]');
  await unbroken.locator('.log-text').scrollIntoViewIfNeeded();
  assert.equal(await unbroken.locator('.log-text').textContent(), 'A'.repeat(800));
  assert.ok(await unbroken.locator('.log-text').evaluate(el => el.offsetHeight > 24));
  await assertGeometry('unbroken raw frame');
  await page.locator('#logStream').evaluate(el => el.scrollTop = 0);
  await page.waitForTimeout(100);

  await page.click('#btnDispHex');
  await page.locator('#logStream').evaluate(el => el.scrollTop = 0);
  await page.waitForFunction(() => document.getElementById('logStream').scrollTop === 0 && logView.lastScroll === 0);
  const stableRows = async () => page.locator('.log-entry[data-id="60000"],.log-entry[data-id="60001"]').evaluateAll(rows => rows.map(row => {
    const rect = row.getBoundingClientRect(), text = row.querySelector('.log-text').getBoundingClientRect();
    return { top: rect.top, height: rect.height, textTop: text.top, textLeft: text.left };
  }));
  const beforeSwitch = await stableRows();
  for (let i = 0; i < 6; i++) {
    await page.click('#btnToggleProtocol');
    await page.waitForTimeout(50);
    assert.deepEqual(await stableRows(), beforeSwitch, 'short rows remain stationary across protocol toggles');
  }

  for (const timestamp of ['none', 'sec', 'ms']) {
    await page.selectOption('#selTimestampFormat', timestamp);
    await page.waitForTimeout(100);
    await assertGeometry(`timestamp=${timestamp}`);
    assert.equal(await row.locator('.log-time').count(), timestamp === 'none' ? 0 : 1);
    if (timestamp !== 'none') assert.match(await row.locator('.log-time').textContent(), timestamp === 'sec' ? /^\[\d{2}:\d{2}:\d{2}\]$/ : /^\[\d{2}:\d{2}:\d{2}\.\d{3}\]$/);
  }
  for (const size of [11, 12, 16, 20]) {
    await page.evaluate(size => applyTypography(12, size), size);
    await page.waitForTimeout(100);
    await assertGeometry(`log font=${size}`);
  }
  await page.evaluate(() => applyTypography());
  const directionColors = () => page.evaluate(() => Object.fromEntries(['rx', 'tx', 'sys'].map(dir => [dir, getComputedStyle(document.querySelector(`.log-dir.${dir}`)).color])));
  assert.deepEqual(await directionColors(), { rx: 'rgb(63, 95, 135)', tx: 'rgb(14, 116, 144)', sys: 'rgb(0, 0, 0)' });
  await page.locator('#logStream').screenshot({ path: path.join(output, 'web-log-columns-light.png') });
  await page.click('#btnDispHex');
  await page.click('#btnToggleTheme');
  await page.setViewportSize({ width: 1120, height: 700 });
  await page.waitForTimeout(120);
  assert.deepEqual(await directionColors(), { rx: 'rgb(127, 159, 189)', tx: 'rgb(103, 232, 249)', sys: 'rgb(244, 244, 245)' });
  await page.screenshot({ path: path.join(output, 'web-protocol-raw-dark.png') });
  await page.click('#btnToggleProtocol');
  await page.screenshot({ path: path.join(output, 'web-protocol-translated-dark.png') });
  await page.click('#btnToggleTheme');
  await row.locator('[data-log-action="expand"]').focus();
  await page.keyboard.press('Space');
  assert.equal(await row.locator('.protocol-detail').count(), 0);
  await page.keyboard.press('Enter');
  assert.equal(await row.locator('.protocol-detail').count(), 1);
  const emptyRow = page.locator('.log-entry[data-id="60007"]');
  await emptyRow.locator('[data-log-action="expand"]').click();
  assert.equal(await emptyRow.locator('.protocol-detail').textContent(), '暂无协议释义');
  await page.click('#btnOpenShortcuts');
  assert.match(await page.locator('#shortcutsModal').textContent(), /复制原始数据帧（当前显示格式）双击数据行/);
  await page.screenshot({ path: path.join(output, 'web-protocol-operation-tips.png') });
  await page.keyboard.press('Escape');
  await page.click('#btnToggleProtocol');
  console.log('PASS: raw/translated rows, aligned columns and disclosure, full wrapping, copying and operation tips');
}
