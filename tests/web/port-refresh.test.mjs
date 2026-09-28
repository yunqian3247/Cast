import assert from 'node:assert/strict';

export async function verifyPortRefresh(page) {
  const original = await page.evaluate(() => structuredClone(hostMock.ports));
  await page.selectOption('#selPort', 'COM3');
  await page.focus('#selPort');
  await page.evaluate(() => hostMock.ports.push({ portName: 'COM7', displayName: 'COM7', deviceInstanceId: null }));
  await page.waitForFunction(() => !!document.querySelector('#selPort option[value="COM7"]'), { }, { timeout: 6000 });
  assert.equal(await page.evaluate(() => document.activeElement.id), 'selPort');
  assert.equal(await page.inputValue('#selPort'), 'COM3');

  await page.selectOption('#selPort', 'COM7');
  await page.evaluate(() => hostMock.ports = hostMock.ports.filter(port => port.portName !== 'COM7'));
  await page.waitForFunction(() => !document.querySelector('#selPort option[value="COM7"]'), { }, { timeout: 6000 });
  assert.equal(await page.inputValue('#selPort'), '');

  await page.evaluate(() => hostMock.failPorts = true);
  await page.click('#btnRefreshPorts');
  await page.waitForFunction(() => document.getElementById('popoverPortError').textContent.includes('设备枚举失败'));
  assert.equal(await page.locator('#selPort option[value="COM3"]').count(), 1);
  await page.evaluate(() => hostMock.failPorts = false);
  await page.focus('#selPort');
  await page.waitForFunction(() => document.getElementById('popoverPortError').textContent === '', { }, { timeout: 6000 });

  await page.selectOption('#selPort', 'COM3');
  await page.evaluate(() => {
    hostMock.delayCommand = 'ports';
    hostMock.ports = [{ portName: 'COM8', displayName: 'COM8' }];
    window.portRefreshDone = false;
    refreshPorts().finally(() => window.portRefreshDone = true);
    hostMock.status.connected = true; hostMock.status.port = 'COM3';
    hostMock.emit({ event: 'status', data: hostMock.status });
  });
  await page.waitForFunction(() => window.portRefreshDone);
  assert.equal(await page.inputValue('#selPort'), 'COM3', 'late scan preserves the active connection');
  assert.equal(await page.locator('#selPort option[value="COM8"]').count(), 0);
  await page.evaluate(ports => {
    hostMock.delayCommand = null; hostMock.ports = ports;
    hostMock.status.connected = false;
    hostMock.emit({ event: 'status', data: hostMock.status });
  }, original);
  await page.click('#btnRefreshPorts');
  await page.waitForFunction(() => !refreshPending);
  await page.selectOption('#selPort', '');
  console.log('PASS: COM hot-plug while focused, removal, scan failure recovery and connection race');
}
