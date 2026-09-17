import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyResponsiveLayout(page, output) {
  await page.evaluate(() => {
    clearTimeout(saveTimer);
    document.getElementById('workspaceBody').classList.remove('hide-left', 'hide-right');
    document.documentElement.style.setProperty('--send-box-h', '135px');
    document.getElementById('presetSearchInput').value = '';
    documentState.presets = Array.from({ length: 60 }, (_, i) => ({
      id: `layout-${i}`, name: i % 2 ? '设置唤醒词为小E小E并读取当前设备工作状态 [91]' : '欢迎使用 [60]',
      content: 'A5 FA 81 00 60 80 FB', format: 'hex', desc: '欢迎使用晾霸智能晾衣机，我叫小E'
    }));
    documentState.workflows = { layout: { name: '设备运行检查', steps: documentState.presets.map(p => ({ presetId: p.id, wait: 500 })) } };
    currentWorkflow = 'layout';
    logs = []; displayMode = 'hex'; translateProtocol = false; autoScroll = false;
    appendLogs(Array.from({ length: 80 }, (_, i) => ({ id: 72000 + i, timestamp: '2026-09-17T02:45:48.988Z', dir: i ? 'TX' : 'SYS', text: i ? '' : 'cast已就绪', hex: i ? 'A5 FA 81 00 60 80 FB' : '', byteCount: i ? 7 : 0 })));
    expandedProtocols.add(logs[1]);
    renderPresets(); renderWorkflow(); updateViewButtons(); renderLogs();
    logView.follow = false;
    document.getElementById('logStream').scrollTop = 0;
  });

  async function measure(label) {
    await page.waitForTimeout(100);
    const layout = await page.evaluate(() => {
      const rect = selector => document.querySelector(selector).getBoundingClientRect();
      const bounds = rect('#workspaceBody');
      const selectors = '#leftPane,#rightPane,.center-pane,#manualInput,.send-options-row,.send-bottom-bar,.terminal-bar,.workflow-body,#workflowStepsContainer,.workflow-add-box,.workflow-control-bar,.step-controls';
      const overflow = [...document.querySelectorAll(selectors)].filter(el => getComputedStyle(el).visibility !== 'hidden' && el.scrollWidth > el.clientWidth + 1).map(el => el.id || el.className);
      const visible = ['#selWorkflow', '#selAddStepPreset', '#inputStepWait', '#btnAddStepToWf', '#selWfMode', '#btnStartWf', '#btnStopWf', '#btnSend'];
      const outside = visible.filter(selector => {
        const box = rect(selector);
        return getComputedStyle(document.querySelector(selector)).visibility !== 'hidden' && (box.left < -1 || box.right > innerWidth + 1 || box.top < bounds.top - 1 || box.bottom > bounds.bottom + 1);
      });
      const clippedNames = [...document.querySelectorAll('.step-name,.preset-title > span:first-child')].some(el => el.scrollWidth > el.clientWidth + 1 || el.scrollHeight > el.clientHeight + 1 || getComputedStyle(el).whiteSpace === 'nowrap');
      const overlappingControls = [...document.querySelectorAll('.step-controls')].some(el => {
        const delay = el.querySelector('.step-delay-box').getBoundingClientRect(), actions = el.querySelector('.step-actions-group').getBoundingClientRect();
        return Math.min(delay.right, actions.right) > Math.max(delay.left, actions.left) + 1 && Math.min(delay.bottom, actions.bottom) > Math.max(delay.top, actions.top) + 1;
      });
      const panelsAligned = ['#leftPane', '#rightPane', '.center-pane'].every(selector => Math.abs(rect(selector).top - bounds.top) < 1 && Math.abs(rect(selector).bottom - bounds.bottom) < 1);
      return { overflow, outside, clippedNames, overlappingControls, panelsAligned, left: rect('#leftPane').width, right: rect('#rightPane').width, send: rect('#sendBox').height, input: rect('#manualInput').height, log: rect('.log-viewport').height };
    });
    assert.deepEqual(layout.overflow, [], `horizontal overflow ${label}`);
    assert.deepEqual(layout.outside, [], `controls outside workspace ${label}`);
    assert.equal(layout.clippedNames, false, `complete names ${label}`);
    assert.equal(layout.overlappingControls, false, `separate controls ${label}`);
    assert.equal(layout.panelsAligned, true, `panel edges ${label}`);
    assert.ok(layout.input >= 40 && layout.log >= 60, `usable editor and log ${label}: ${JSON.stringify(layout)}`);
    return layout;
  }

  let previous, defaultLayout;
  for (const size of [{ width: 880, height: 560 }, { width: 1120, height: 700 }, { width: 1536, height: 864 }, { width: 1920, height: 1080 }, { width: 2560, height: 1440 }]) {
    await page.setViewportSize(size);
    const layout = await measure(`${size.width}x${size.height}`);
    assert.equal(await page.locator('.step-controls').evaluateAll(rows => rows.every(row => {
      const delay = row.querySelector('.step-delay-box').getBoundingClientRect(), actions = row.querySelector('.step-actions-group').getBoundingClientRect();
      return Math.abs((delay.top + delay.bottom) - (actions.top + actions.bottom)) < 1;
    })), true, `delay and actions share one row at ${size.width}`);
    assert.equal(await page.locator('.step-delay-box input').first().evaluate(el => el.getBoundingClientRect().width), 48);
    assert.ok(layout.left >= 180 && layout.left <= 320 && layout.right >= 210 && layout.right <= 340);
    if (previous) assert.ok(layout.left >= previous.left && layout.right >= previous.right, 'both sidebars grow with the window');
    if (size.height === 700) defaultLayout = layout;
    if (size.height > 700) assert.ok(layout.send >= defaultLayout.send && layout.send <= defaultLayout.send + 60, 'automatic editor growth remains bounded');
    previous = layout;
    console.log(`Layout ${size.width}x${size.height}: left=${layout.left}, right=${layout.right}, send=${layout.send}, editor=${layout.input}`);
    await page.screenshot({ path: path.join(output, `web-responsive-${size.width}.png`) });
  }

  await page.setViewportSize({ width: 880, height: 560 });
  await measure('restore minimum window');
  await page.locator('#workflowStepsContainer').evaluate(el => el.scrollTop = el.scrollHeight);
  await page.locator('#presetListContainer').evaluate(el => el.scrollTop = el.scrollHeight);
  for (const selector of ['.workflow-step-card', '.preset-row']) {
    const last = page.locator(selector).last();
    assert.ok(await last.evaluate(el => {
      const item = el.getBoundingClientRect(), list = el.parentElement.getBoundingClientRect();
      return item.top >= list.top && item.bottom <= list.bottom;
    }), `last item remains reachable: ${selector}`);
  }
  const originalCenter = await page.locator('.center-pane').evaluate(el => el.clientWidth);
  for (const button of ['#btnToggleLeft', '#btnToggleRight']) {
    await page.click(button);
    assert.ok(await page.locator('.center-pane').evaluate(el => el.clientWidth) > originalCenter);
    await page.click(button);
    await measure(`${button} restored`);
  }

  await page.setViewportSize({ width: 1920, height: 1080 });
  const initialSend = (await measure('before manual resize')).send;
  await page.locator('#centerSplitter').focus();
  await page.keyboard.press('ArrowUp');
  assert.ok(Math.abs((await measure('keyboard resize')).send - initialSend - 10) < 1);
  const splitter = await page.locator('#centerSplitter').boundingBox();
  await page.mouse.move(splitter.x + splitter.width / 2, splitter.y + 3);
  await page.mouse.down();
  await page.mouse.move(splitter.x + splitter.width / 2, splitter.y - 27, { steps: 3 });
  await page.mouse.up();
  assert.ok(Math.abs((await measure('pointer resize')).send - initialSend - 40) < 1);
  await page.locator('#centerSplitter').dblclick();
  assert.ok(Math.abs((await measure('collapsed editor')).send - initialSend) < 1);
  await page.locator('#centerSplitter').dblclick();
  assert.ok((await measure('expanded editor')).send > initialSend);
  await page.setViewportSize({ width: 880, height: 560 });
  await measure('large editor at minimum window');
  await page.evaluate(() => document.documentElement.style.setProperty('--send-box-h', '135px'));
  await page.setViewportSize({ width: 1120, height: 700 });
  await measure('final restored layout');
  await page.click('#manualInput');
  assert.deepEqual(await page.locator('#manualInput').evaluate(el => {
    const css = getComputedStyle(el);
    return { outline: css.outlineStyle, top: css.borderTopWidth, bottom: css.borderBottomWidth, shadow: css.boxShadow };
  }), { outline: 'none', top: '0px', bottom: '0px', shadow: 'none' });
  await page.screenshot({ path: path.join(output, 'web-input-focus-compact-workflow.png') });
  await verifySendToolbar(page, output);
  await verifyTerminalToolbar(page, output);
  console.log('PASS: responsive sidebars, complete workflow names, reachable controls and bounded editor resizing');
}

async function verifyTerminalToolbar(page, output) {
  const original = await page.evaluate(() => ({ timestamp: document.getElementById('selTimestampFormat').value }));
  await page.evaluate(() => {
    document.getElementById('workspaceBody').classList.remove('hide-left', 'hide-right');
    document.getElementById('terminalSearch').value = 'A5';
    document.getElementById('searchResultCount').textContent = '10000/10000';
  });
  for (const width of [880, 1120, 1920]) {
    await page.setViewportSize({ width, height: width === 880 ? 560 : 700 });
    for (const font of [12, 16]) {
      await page.evaluate(font => applyTypography(font, 12), font);
      for (const panels of ['both', 'left', 'right', 'none']) {
        await page.evaluate(panels => {
          const body = document.getElementById('workspaceBody');
          body.classList.toggle('hide-left', panels === 'right' || panels === 'none');
          body.classList.toggle('hide-right', panels === 'left' || panels === 'none');
          document.getElementById('searchResultCount').textContent = '10000/10000';
        }, panels);
        const geometry = await page.locator('.terminal-bar').evaluate(bar => {
          const bounds = bar.getBoundingClientRect();
          const controls = [...bar.querySelectorAll('input,select,button,#searchResultCount')];
          const rects = controls.map(el => el.getBoundingClientRect());
          const middle = rect => rect.top + rect.height / 2;
          const canvas = document.createElement('canvas').getContext('2d');
          const clippedOptions = [...bar.querySelectorAll('select')].filter(select => {
            const css = getComputedStyle(select);
            canvas.font = `${css.fontWeight} ${css.fontSize} ${css.fontFamily}`;
            return [...select.options].some(option => canvas.measureText(option.textContent).width + parseFloat(css.paddingLeft) + parseFloat(css.paddingRight) + 2 > select.clientWidth);
          }).map(el => el.id);
          return {
            height: bounds.height,
            overflow: bar.scrollWidth > bar.clientWidth + 1,
            outside: controls.filter((_, i) => rects[i].left < bounds.left || rects[i].right > bounds.right || rects[i].top < bounds.top || rects[i].bottom > bounds.bottom).map(el => el.id),
            aligned: rects.every(rect => Math.abs(middle(rect) - middle(rects[0])) < 1),
            overlap: rects.some((rect, i) => rects.slice(i + 1).some(other => Math.min(rect.right, other.right) > Math.max(rect.left, other.left) + 1)),
            clippedOptions,
            inputWidth: document.getElementById('terminalSearch').clientWidth
          };
        });
        const label = `${width}px font=${font} panels=${panels}: ${JSON.stringify(geometry)}`;
        assert.equal(geometry.overflow, false, label);
        assert.deepEqual(geometry.outside, [], label);
        assert.equal(geometry.aligned, true, label);
        assert.equal(geometry.overlap, false, label);
        assert.deepEqual(geometry.clippedOptions, [], label);
        assert.ok(geometry.height <= font + 20 && geometry.inputWidth >= 60, label);
        if (panels === 'both') await page.screenshot({ path: path.join(output, `web-terminal-toolbar-${width}-font-${font}.png`) });
      }
    }
  }
  await page.evaluate(() => {
    applyTypography();
    document.getElementById('workspaceBody').classList.remove('hide-left', 'hide-right');
    document.getElementById('terminalSearch').value = '';
    renderLogs();
  });
  await page.setViewportSize({ width: 1120, height: 700 });
  await page.keyboard.press('Control+f');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'terminalSearch');
  await page.fill('#terminalSearch', 'A5');
  await page.waitForFunction(() => document.getElementById('searchResultCount').textContent === '1/79');
  const height = await page.locator('.terminal-bar').evaluate(el => el.offsetHeight);
  await page.click('#btnSearchNext');
  assert.equal(await page.textContent('#searchResultCount'), '2/79');
  await page.click('#btnSearchPrev');
  assert.equal(await page.textContent('#searchResultCount'), '1/79');
  await page.fill('#terminalSearch', 'absent-record');
  await page.waitForFunction(() => document.getElementById('searchResultCount').textContent === '0/0');
  assert.equal(await page.locator('.terminal-bar').evaluate(el => el.offsetHeight), height);
  await page.fill('#terminalSearch', '');
  await page.waitForFunction(() => document.getElementById('searchResultCount').textContent === '');
  assert.equal(await page.locator('#btnToggleScroll').count(), 0);
  for (const mode of ['none', 'sec', 'ms']) {
    await page.selectOption('#selTimestampFormat', mode);
    assert.equal(await page.locator('.terminal-bar').evaluate(el => el.offsetHeight), height);
  }
  await page.selectOption('#selTimestampFormat', original.timestamp);
  await page.hover('#manualInput');
  await page.screenshot({ path: path.join(output, 'web-terminal-toolbar-final.png') });
  console.log('PASS: single-row log toolbar, large search counts, all sidebar states, font sizes and search navigation');
}

async function verifySendToolbar(page, output) {
  await page.check('#chkSendHex');
  await page.selectOption('#selEnding', 'none');
  await page.fill('#manualInput', 'A5 FA 81 00 93 B3 FB');
  await page.waitForFunction(() => document.getElementById('sendByteCounter').textContent === '1 行 · 7 字节');
  for (const width of [880, 1120, 1920]) {
    await page.setViewportSize({ width, height: width === 880 ? 560 : 700 });
    for (const size of [12, 16]) {
      await page.evaluate(size => applyTypography(size, 12), size);
      for (const ending of ['none', 'crlf', 'custom']) {
        await page.selectOption('#selEnding', ending);
        if (ending === 'custom') await page.fill('#customEndingHex', '0D 0A');
        await page.waitForTimeout(100);
        const layout = await page.evaluate(() => {
          const rect = selector => document.querySelector(selector).getBoundingClientRect();
          const select = document.getElementById('selEnding'), style = getComputedStyle(select);
          const canvas = document.createElement('canvas').getContext('2d');
          canvas.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
          const optionWidth = canvas.measureText(select.selectedOptions[0].textContent).width + parseFloat(style.paddingLeft) + parseFloat(style.paddingRight) + 2;
          const top = rect('.send-options-row'), bottom = rect('.send-bottom-bar');
          const visibleControls = [...document.querySelectorAll('.send-options-row input,.send-options-row select,#btnSendTools,#chkLineByLine,#sendByteCounter,#btnClearInput,#btnSend')].filter(el => el.getClientRects().length);
          return {
            topHeight: top.height, bottomHeight: bottom.height,
            completeEnding: optionWidth <= rect('#selEnding').width,
            overflow: ['.send-options-row', '.send-options-left', '.send-bottom-bar', '.send-input-status'].filter(selector => {
              const el = document.querySelector(selector); return el.scrollWidth > el.clientWidth + 1;
            }),
            clipped: visibleControls.filter(el => {
              const box = el.getBoundingClientRect(), parent = el.closest('.send-options-row') ? top : bottom;
              return box.left < parent.left || box.right > parent.right || box.top < parent.top || box.bottom > parent.bottom;
            }).map(el => el.id),
            repeatAligned: Math.abs(rect('#chkRepeat').top + rect('#chkRepeat').height / 2 - rect('#repeatInterval').top - rect('#repeatInterval').height / 2) < 1
          };
        });
        const label = `${width}px font=${size} ending=${ending}: ${JSON.stringify(layout)}`;
        assert.deepEqual(layout.overflow, [], label);
        assert.deepEqual(layout.clipped, [], label);
        assert.equal(layout.completeEnding, true, label);
        assert.equal(layout.repeatAligned, true, label);
        assert.ok(layout.topHeight <= (ending === 'custom' ? 66 : size + 20), `compact upper toolbar ${label}`);
        assert.ok(layout.bottomHeight <= size + 24, `single lower toolbar ${label}`);
      }
      await page.screenshot({ path: path.join(output, `web-send-custom-${width}-font-${size}.png`) });
    }
  }
  await page.evaluate(() => applyTypography());
  await page.setViewportSize({ width: 1120, height: 700 });
  await page.selectOption('#selEnding', 'none');
  await page.screenshot({ path: path.join(output, 'web-send-toolbar-1120.png') });
  await page.locator('#btnSendTools').focus();
  await page.keyboard.press('ArrowDown');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'btnAppendCrc');
  await page.keyboard.press('ArrowUp');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'btnFormatHex');
  await page.keyboard.press('Home');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'btnAppendCrc');
  await page.keyboard.press('End');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'btnFormatHex');
  await page.screenshot({ path: path.join(output, 'web-send-tools-menu.png') });
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#sendToolsMenu').isVisible(), false);
  assert.equal(await page.evaluate(() => document.activeElement.id), 'btnSendTools');
  await page.click('#btnSendTools');
  await page.click('#btnSendTools');
  assert.equal(await page.locator('#sendToolsMenu').isVisible(), false);
  await page.click('#btnSendTools');
  await page.click('#manualInput');
  assert.equal(await page.getAttribute('#btnSendTools', 'aria-expanded'), 'false');
  await page.click('#btnSendTools');
  await page.keyboard.press('Tab');
  assert.equal(await page.locator('#sendToolsMenu').isVisible(), false);
  await page.fill('#manualInput', '0103');
  await page.click('#btnSendTools');
  await page.click('#btnFormatHex');
  await page.waitForFunction(() => document.getElementById('manualInput').value === '01 03');
  assert.equal(await page.locator('#sendToolsMenu').isVisible(), false);
  assert.equal(await page.evaluate(() => document.activeElement.id), 'manualInput');
  await page.waitForFunction(() => hostMock.document.ui.inputDraft === '01 03');
  await page.selectOption('#selPort', 'COM3');
  await page.click('#btnConnect');
  await page.waitForFunction(() => hostMock.status.connected);
  await page.check('#chkRepeat');
  await page.waitForFunction(() => hostMock.status.run.kind === 'repeat');
  for (const id of ['btnSendTools', 'btnClearInput', 'btnAppendCrc', 'btnFormatHex']) assert.equal(await page.isDisabled(`#${id}`), true);
  await page.uncheck('#chkRepeat');
  await page.waitForFunction(() => hostMock.status.run.kind === 'idle');
  await page.click('#btnConnect');
  await page.waitForFunction(() => !hostMock.status.connected);
  await page.click('#btnClearInput');
  await page.waitForFunction(() => document.getElementById('sendByteCounter').textContent === '0 行 · 0 字节');
  console.log('PASS: compact send toolbar, grouped controls, keyboard menu, tool actions and running state');
}
