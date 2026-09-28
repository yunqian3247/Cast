import assert from 'node:assert/strict';

export async function verifyAuditRegressions(page) {
  const fixture = await page.evaluate(() => structuredClone(hostMock.document));
  await page.evaluate(async () => {
    clearTimeout(saveTimer);
    hostMock.status.connected = false; hostMock.status.run = { kind: 'idle', paused: false, step: -1 };
    hostMock.emit({ event: 'status', data: hostMock.status });
    documentState.history = Array.from({ length: 50 }, (_,i) => `${i}:` + 'A'.repeat(90000));
    documentState.history[0] = 'chunk-marker';
    const markerOffset = JSON.stringify(documentState).indexOf('chunk-marker');
    documentState.history[0] = 'A'.repeat(131071-markerOffset)+'😀'+'B'.repeat(10000);
    await saveNow();
  });
  assert.equal(await page.evaluate(() => hostMock.document.history.length), 50);
  assert.ok(await page.evaluate(() => hostMock.requests.filter(item => item.command === 'saveChunk').length) > 30);
  assert.equal(await page.evaluate(() => hostMock.saveTransfer), null);
  assert.equal(await page.evaluate(() => hostMock.requests.filter(item => item.command==='saveChunk')
    .every(item => !/[\uD800-\uDBFF]/.test(item.data.content.at(-1)))),true);
  assert.equal(await page.evaluate(() => hostMock.document.history[0].includes('😀')),true);
  const rollback = await page.evaluate(async () => {
    const previous = hostMock.document.history[0];
    hostMock.failChunk = true; documentState.history[0] = 'changed';
    let error;
    try { await saveNow(); } catch(e) { error = e.message; }
    hostMock.failChunk = false;
    return { error, kept: hostMock.document.history[0] === previous, cleaned: hostMock.saveTransfer === null };
  });
  assert.deepEqual(rollback, { error: '分块保存失败', kept: true, cleaned: true });
  await page.evaluate(async original => { documentState = structuredClone(original); await saveNow(); }, fixture);

  const queued = await page.evaluate(async () => {
    const post = chrome.webview.postMessage;
    let first = true;
    chrome.webview.postMessage = message => {
      if (message.command === 'save' && first) {
        first = false;
        setTimeout(() => hostMock.emit({id:message.id,ok:false,error:'first save failed'}),50);
      } else post(message);
    };
    try {
      documentState.history = ['queued-first'];
      const earlier = saveNow().catch(error => error.message);
      documentState.history = ['queued-latest'];
      const later = saveNow();
      const error = await earlier; await later;
      return {error, current:documentState.history[0], stored:hostMock.document.history[0]};
    } finally {chrome.webview.postMessage = post;}
  });
  assert.deepEqual(queued,{error:'first save failed',current:'queued-latest',stored:'queued-latest'});
  await page.evaluate(async original => { documentState = structuredClone(original); await saveNow(); }, fixture);

  const multiline = await page.evaluate(async () => {
    clearTimeout(saveTimer); clearTimeout(validationTimer);
    document.getElementById('chkLineByLine').checked = true;
    document.getElementById('chkSendHex').checked = false;
    document.getElementById('selEnding').value = 'lf';
    document.getElementById('manualInput').value = 'A\n'.repeat(20000);
    hostMock.requests = [];
    await validateInput();
    return { count: hostMock.requests.filter(item => item.command === 'validateSend').length,
      perLineCount: hostMock.requests.filter(item => item.command === 'encode').length,
      counter: document.getElementById('sendByteCounter').textContent };
  });
  assert.deepEqual(multiline, { count: 1, perLineCount: 0, counter: '20001 行 · 40000 字节' });
  await page.evaluate(() => {
    hostMock.requests = [];
    const input = document.getElementById('manualInput');
    for (let i=0; i<10; i++) {input.value='draft-'+i;input.dispatchEvent(new Event('input',{bubbles:true}));}
  });
  await page.waitForFunction(() => hostMock.requests.some(item => item.command === 'validateSend'));
  assert.equal(await page.evaluate(() => hostMock.requests.filter(item => item.command === 'validateSend').length), 1);

  const staleCounter = await page.evaluate(async () => {
    clearTimeout(saveTimer); clearTimeout(validationTimer);
    const post = chrome.webview.postMessage;
    let first = true;
    chrome.webview.postMessage = message => {
      if (message.command === 'validateSend' && first) {
        first = false;
        setTimeout(() => hostMock.emit({id:message.id,ok:true,result:{lineCount:999,byteCount:999}}),50);
      } else post(message);
    };
    try {
      document.getElementById('manualInput').value = 'old'; const old = validateInput();
      document.getElementById('manualInput').value = 'new'; const current = validateInput();
      await Promise.all([old,current]);
      return document.getElementById('sendByteCounter').textContent;
    } finally {chrome.webview.postMessage = post;}
  });
  assert.equal(staleCounter,'1 行 · 4 字节');

  const lifecycle = await page.evaluate(async () => {
    clearTimeout(saveTimer);
    const post = chrome.webview.postMessage, nativeTimeout = window.setTimeout;
    let exportActive = null, starts = 0, aborts = 0;
    chrome.webview.postMessage = message => {
      if (message.command === 'exportStart') {
        starts++;
        nativeTimeout(() => {exportActive={...message.data,content:''};hostMock.emit({id:message.id,ok:true,result:{saved:true}});},100);
      } else if(message.command === 'exportChunk') {
        exportActive.content+=message.data.content;hostMock.emit({id:message.id,ok:true,result:true});
      } else if(message.command === 'exportFinish') {
        exportActive=null;hostMock.emit({id:message.id,ok:true,result:{saved:true}});
      } else if(message.command === 'exportAbort') {
        aborts++;exportActive=null;hostMock.emit({id:message.id,ok:true,result:true});
      } else post(message);
    };
    // A reply after 100 ms exceeds the former timeout mapped to 30 ms.
    window.setTimeout=(fn,ms,...args)=>nativeTimeout(fn,ms===30000?30:ms,...args);
    let duplicate;
    try {
      const first=exportContent('A'.repeat(530000),'csv','large.csv');
      try {await exportContent('B'.repeat(530000),'csv','duplicate.csv');} catch(e) {duplicate=e.message;}
      const completed=await first;
      return {completed:completed.saved,duplicate,starts,active:exportActive!==null,aborts};
    } finally {window.setTimeout=nativeTimeout;chrome.webview.postMessage=post;}
  });
  assert.deepEqual(lifecycle, {completed:true,duplicate:'已有导出任务正在进行',starts:1,active:false,aborts:0});
  const failedExport = await page.evaluate(async () => {
    hostMock.failExportChunk=true;
    let error;
    try {await exportContent('A'.repeat(530000),'txt','failed.txt');} catch(e) {error=e.message;}
    hostMock.failExportChunk=false;
    const cleaned=hostMock.exportTransfer===null;
    const retry=await exportContent('B'.repeat(530000),'txt','retry.txt');
    hostMock.cancelExport=true;
    const cancelled=await exportContent('C'.repeat(530000),'txt','cancelled.txt');
    hostMock.cancelExport=false;
    return {error,cleaned,retry:retry.saved,cancelled:cancelled.saved};
  });
  assert.deepEqual(failedExport,{error:'导出写入失败',cleaned:true,retry:true,cancelled:false});

  await page.evaluate(() => {
    hostMock.status.connected=true;hostMock.status.run={kind:'manual',paused:false,step:-1};
    hostMock.emit({event:'status',data:hostMock.status});
  });
  assert.equal(await page.textContent('#btnSendLabel'),'停止发送');
  assert.equal(await page.isDisabled('#btnSend'),false);
  await page.click('#btnSend');
  await page.waitForFunction(() => hostMock.status.run.kind==='idle');
  assert.ok(await page.evaluate(() => hostMock.requests.some(item => item.command==='stop')));

  await page.evaluate(() => {
    document.getElementById('manualInput').value='closing-latest';
    document.getElementById('manualInput').dispatchEvent(new Event('input',{bubbles:true}));
    hostMock.emit({event:'closing',data:{id:'close-probe'}});
  });
  await page.waitForFunction(() => hostMock.closeResult?.id==='close-probe');
  assert.equal(await page.evaluate(() => hostMock.document.ui.inputDraft),'closing-latest');
  assert.equal(await page.evaluate(() => document.body.inert),true);
  await page.evaluate(() => hostMock.emit({event:'closeCancelled',data:{error:'probe reset'}}));
  assert.equal(await page.evaluate(() => document.body.inert),false);
  await page.evaluate(async original => {
    documentState=structuredClone(original);hostMock.status.connected=false;
    hostMock.emit({event:'status',data:hostMock.status});await saveNow();
  },fixture);
  console.log('PASS: large configuration chunks/abort, batched 20k lines/debounce, long file dialog/retry, manual stop and final close snapshot');
}
