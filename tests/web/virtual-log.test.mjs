import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyVirtualLogs(page, output) {
  await page.evaluate(() => {
    logs = []; autoScroll = true; displayMode = 'text'; translateProtocol = false;
    document.getElementById('filterDir').value = 'all';
    document.getElementById('terminalSearch').value = '';
    document.getElementById('selTimestampFormat').value = 'ms';
    if (logView) logView.follow = true;
    renderLogs();
    const entries = Array.from({ length: 10050 }, (_, i) => {
      const text = `RECORD-${String(i + 1).padStart(5, '0')}` + (i % 101 === 0 ? '\n' + 'Long wrapped line '.repeat(40) : ' OK');
      const bytes = [...new TextEncoder().encode(text)];
      return { id: i + 1, timestamp: '2026-09-17T00:00:00Z', dir: i % 2 ? 'RX' : 'TX', text,
        hex: bytes.map(byte => byte.toString(16).padStart(2, '0').toUpperCase()).join(' '), byteCount: bytes.length, source: 'fixture' };
    });
    hostMock.emit({ event: 'logs', data: entries });
  });
  await page.waitForSelector('.log-entry[data-id="10050"]');
  assert.equal(await page.evaluate(() => logs.length),10000);
  assert.equal(await page.evaluate(() => logs[0].id),51);
  assert.ok(await page.locator('.log-entry').count()<150);
  await page.fill('#terminalSearch','RECORD-00051');
  await page.waitForFunction(()=>document.getElementById('searchResultCount').textContent==='1/1');
  await page.click('#btnSearchNext');await page.waitForSelector('.log-entry[data-id="51"]');
  const disclosure = await page.locator('.log-entry[data-id="51"]').evaluate(row => {
    const start = performance.now();
    row.querySelector('.log-text').click();
    const expanded = !!row.querySelector('.protocol-detail');
    const duration = performance.now() - start;
    row.querySelector('.log-text').click();
    return { expanded, duration };
  });
  assert.equal(disclosure.expanded, true, '10,000-record disclosure is synchronous');
  console.log(`10,000-record disclosure update: ${disclosure.duration.toFixed(1)} ms`);
  await page.dblclick('.log-entry[data-id="51"]');
  await page.waitForFunction(()=>hostMock.clipboard==='RECORD-00051 OK');
  await page.click('#btnExportLog');await page.selectOption('#exportFormat','json');
  await page.check('input[name="exportScope"][value="filtered"]');await page.click('#btnConfirmExport');
  await page.waitForFunction(()=>hostMock.exports.some(item=>JSON.parse(item.content)?.[0]?.id===51));
  assert.equal(await page.evaluate(()=>JSON.parse(hostMock.exports.at(-1).content).length),1);
  await page.fill('#terminalSearch','');await page.waitForTimeout(200);
  await page.click('#btnExportLog');await page.selectOption('#exportFormat','json');
  await page.check('input[name="exportScope"][value="all"]');await page.click('#btnConfirmExport');
  await page.waitForFunction(()=>JSON.parse(hostMock.exports.at(-1).content).length===10000);
  assert.equal(await page.evaluate(()=>JSON.parse(hostMock.exports.at(-1).content).at(-1).id),10050);

  await page.locator('#logStream').evaluate(el=>el.scrollTop=50000);
  await page.waitForTimeout(100);
  const before=await page.evaluate(()=>logView.anchor());
  for (let i=0;i<4;i++) {
    await page.click('#btnToggleProtocol');
    await page.waitForTimeout(80);
    const switched=await page.evaluate(()=>logView.anchor());
    assert.equal(switched.id,before.id,'protocol toggle preserves the viewed record');
    assert.ok(Math.abs(switched.offset-before.offset)<2,'protocol toggle preserves the pixel offset');
  }
  await page.evaluate(()=>hostMock.emit({event:'logs',data:Array.from({length:200},(_,i)=>({id:10051+i,timestamp:'2026-09-17T00:00:01Z',dir:'RX',text:'New data',hex:'4E',byteCount:1,source:'fixture'}))}));
  await page.waitForTimeout(200);
  const after=await page.evaluate(()=>logView.anchor());
  assert.equal(after.id,before.id,'receiving must preserve the viewed record');
  assert.ok(Math.abs(after.offset-before.offset)<2,'receiving must preserve the pixel offset');
  assert.equal(await page.evaluate(()=>logs.length),10000);
  await page.locator('#logStream').evaluate(el=>el.scrollTop=0);await page.waitForTimeout(100);
  assert.ok(await page.locator('.log-entry').count()<150);
  await page.click('#btnDispBoth');
  await page.click('#btnToggleProtocol');
  for(const size of [{width:880,height:560},{width:1280,height:800}]) {
    await page.setViewportSize(size);await page.waitForTimeout(100);
    const geometry=await page.locator('.log-entry').evaluateAll(rows=>rows.map(row=>{
      const r=row.getBoundingClientRect();return {top:r.top,bottom:r.bottom,overflow:row.scrollWidth>row.clientWidth+1};
    }));
    assert.ok(geometry.every(row=>!row.overflow));
    assert.ok(geometry.every((row,i)=>i===0||row.top>=geometry[i-1].bottom-1));
  }
  await page.screenshot({path:path.join(output,'web-virtual-log-10000.png')});

  await page.fill('#terminalSearch','RECORD-00304');await page.waitForFunction(()=>document.getElementById('searchResultCount').textContent==='1/1');
  await page.click('#btnSearchNext');await page.waitForSelector('.log-entry[data-id="304"]');
  const collapsedHeight = await page.locator('.log-entry[data-id="304"]').evaluate(el=>el.offsetHeight);
  assert.ok(collapsedHeight >= 22 && collapsedHeight <= 24);
  await page.locator('.log-entry[data-id="304"] [data-log-action="expand"]').click();
  assert.ok(await page.locator('.log-entry[data-id="304"]').evaluate(el=>el.offsetHeight)>collapsedHeight);
  assert.match(await page.locator('.log-entry[data-id="304"] .protocol-detail').textContent(),/未匹配协议/);
  const expandedAnchor = await page.evaluate(()=>logView.anchor());
  await page.evaluate(()=>hostMock.emit({event:'logs',data:Array.from({length:20},(_,i)=>({id:10300+i,timestamp:'2026-09-17T00:00:02Z',dir:'RX',text:'New data',hex:'4E',byteCount:1,source:'fixture'}))}));
  await page.waitForTimeout(200);
  const receivedAnchor = await page.evaluate(()=>logView.anchor());
  assert.equal(receivedAnchor.id,expandedAnchor.id);
  assert.ok(Math.abs(receivedAnchor.offset-expandedAnchor.offset)<2);
  assert.ok(await page.locator('.log-entry').count()<150);
  await page.locator('#logStream').evaluate(el=>el.scrollTop=100000);
  await page.waitForTimeout(100);
  await page.click('#btnSearchNext');
  await page.waitForSelector('.log-entry[data-id="304"] .protocol-detail');
  await page.locator('.log-entry[data-id="304"] [data-log-action="expand"]').click();
  assert.equal(await page.locator('.log-entry[data-id="304"] .protocol-detail').count(),0);
  await page.evaluate(()=>{
    const log=logs.find(item=>item.id===304);
    documentState.presets=[{id:'match',name:'Cached protocol name',format:'text',content:log.text,desc:'Rule one',rxMatch:'RECORD-00304',rxDesc:''}];
    renderPresets();renderLogs();
  });
  assert.match(await page.locator('.log-entry[data-id="304"]').textContent(),/Cached protocol name/);
  await page.evaluate(()=>{documentState.presets[0].name='Updated protocol name';renderPresets();renderLogs();});
  assert.match(await page.locator('.log-entry[data-id="304"]').textContent(),/Updated protocol name/);
  await page.click('#btnToggleProtocol');
  await page.click('#btnSearchNext');
  assert.ok(await page.locator('.log-entry[data-id="304"]').evaluate(el=>el.offsetHeight)>28);
  await page.locator('.log-entry[data-id="304"] [data-log-action="expand"]').click();
  assert.equal(await page.locator('.log-entry[data-id="304"] .protocol-detail').textContent(),'Rule one');
  assert.equal(await page.locator('.log-entry[data-id="304"] .log-text > span').first().textContent(),await page.evaluate(()=>logs.find(item=>item.id===304).text));

  await page.evaluate(()=>{
    const entries=Array.from({length:200},(_,i)=>({id:20000+i,timestamp:'2026-09-17T00:00:02Z',dir:'RX',text:'A'.repeat(16384),hex:'41 '.repeat(16384).trim(),byteCount:16384,source:'fixture'}));
    hostMock.emit({event:'logs',data:entries});
  });
  await page.waitForTimeout(200);
  const retained=await page.evaluate(()=>({count:logs.length,bytes:logs.reduce((sum,item)=>sum+2*(item.text.length+item.hex.length),0),last:logs.at(-1).id}));
  assert.ok(retained.bytes<=16*1024*1024);
  assert.ok(retained.count<200);
  assert.equal(retained.last,20199);
  await page.click('#btnClearTerminal');await page.click('#btnConfirmAction');
  await page.waitForFunction(()=>logs.length===0);
  assert.equal(await page.locator('.log-entry').count(),0);
}
