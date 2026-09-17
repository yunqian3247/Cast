import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyUpdates(page, output) {
  await page.click('#btnOpenAbout');
  assert.equal(await page.title(), 'cast');
  assert.equal(await page.textContent('.titlebar-brand'), 'cast');
  assert.equal(await page.textContent('#aboutTitle'), '关于 cast');
  assert.equal(await page.textContent('.about-brand strong'), 'cast');
  assert.equal(await page.textContent('#updateStatus'), '更新地址尚未配置');
  assert.equal(await page.isDisabled('#btnCheckUpdate'), true);
  assert.equal(await page.isVisible('#btnInstallUpdate'), false);
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'btnCloseAboutModal');
  await page.evaluate(() => {
    hostMock.update = { phase: 'idle', message: '尚未检查更新', canCheck: true };
    hostMock.emit({ event: 'update', data: hostMock.update });
    hostMock.noUpdate = true;
  });
  await page.click('#btnCheckUpdate');
  await page.waitForFunction(() => document.getElementById('updateStatus').textContent === '已是最新版本');
  await page.evaluate(() => { hostMock.noUpdate = false; hostMock.delayCommand = 'updateCheck'; });
  await page.click('#btnCheckUpdate');
  assert.equal(await page.isDisabled('#btnCheckUpdate'), true);
  await page.waitForSelector('#btnDownloadUpdate:visible');
  await page.evaluate(() => { hostMock.failUpdateDownload = true; });
  await page.click('#btnDownloadUpdate');
  await page.waitForSelector('#updateProgress:visible');
  assert.equal(await page.getAttribute('#updateProgress', 'value'), '42');
  await page.waitForFunction(() => document.getElementById('updateStatus').textContent.includes('下载更新失败'));
  await page.evaluate(() => { hostMock.failUpdateDownload = false; });
  await page.click('#btnDownloadUpdate');
  await page.waitForSelector('#btnInstallUpdate:visible');
  await page.evaluate(() => {
    hostMock.status.run = { kind: 'repeat', paused: false, step: -1 };
    hostMock.emit({ event: 'status', data: hostMock.status });
  });
  assert.equal(await page.isDisabled('#btnInstallUpdate'), true);
  await page.evaluate(() => {
    hostMock.status.run = { kind: 'idle', paused: false, step: -1 };
    hostMock.emit({ event: 'status', data: hostMock.status });
    hostMock.failSave = true;
  });
  await page.click('#btnInstallUpdate');
  await page.waitForFunction(() => document.getElementById('updateError').textContent === '配置写入失败');
  assert.equal(await page.isDisabled('#btnInstallUpdate'), false);
  await page.keyboard.press('Escape');
  await page.click('#btnOpenAbout');
  assert.equal(await page.isVisible('#btnInstallUpdate'), true);
  const original = page.viewportSize();
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => { theme = value; applyAppearance(); }, theme);
    for (const size of [{ width: 880, height: 560 }, { width: 1120, height: 700 }]) {
      await page.setViewportSize(size);
      const bounds = await page.locator('.about-dialog').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth, bottom: el.getBoundingClientRect().bottom }));
      assert.ok(bounds.scroll <= bounds.width && bounds.bottom <= size.height);
      await page.screenshot({ path: path.join(output, `web-update-${theme}-${size.width}.png`) });
    }
  }
  await page.setViewportSize(original);
  await page.evaluate(() => { hostMock.failSave = false; theme = 'light'; applyAppearance(); });
  await page.click('#btnInstallUpdate');
  await page.waitForFunction(() => document.getElementById('updateStatus').textContent.includes('正在安装'));
  assert.equal(await page.evaluate(() => hostMock.requests.filter(request => request.command === 'updateInstall').at(-1).data.version), 1);
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('body').innerText().then(text => text.includes('工作台') || text.includes('SerialDebugTool')), false);
  console.log('PASS: update configuration, check, progress, retry, save failure, busy guard, restart and two-theme layouts');
}
