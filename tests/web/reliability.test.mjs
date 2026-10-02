import assert from 'node:assert/strict';
import path from 'node:path';

export async function verifyReliability(page, output) {
  const original = await page.evaluate(() => structuredClone(documentState));
  await page.evaluate(() => { hostMock.status.run = { kind:'idle',paused:false,step:-1 }; hostMock.emit({event:'status',data:hostMock.status}); });
  await page.setViewportSize({ width:880, height:560 });
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => { theme=value;applyAppearance(); }, theme);
    await page.click('#btnOpenSettings');
    await page.locator('.reliability-settings').evaluate(el => el.open=true);
    await page.selectOption('#cfgReceiveMode','auto');
    await page.fill('#cfgReceiveIdle','45'); await page.fill('#cfgReceiveLength','16');
    await page.check('#cfgContinuousLog'); await page.fill('#cfgLogFileMiB','2'); await page.fill('#cfgLogFiles','3');
    await page.locator('#cfgLogFiles').scrollIntoViewIfNeeded();
    assert.equal(await page.locator('#settingsModal .modal-dialog').evaluate(el=>el.scrollWidth>el.clientWidth+1),false);
    await page.screenshot({path:path.join(output,`web-reliability-settings-${theme}.png`)});
    await page.click('#btnSaveSettingsModal');
    await page.waitForSelector('#settingsModal.show',{state:'hidden'});
    assert.deepEqual(await page.evaluate(()=>({mode:hostMock.document.ui.receiveFrameMode,idle:hostMock.document.ui.receiveIdleMs,logging:hostMock.document.ui.continuousLog,file:hostMock.document.ui.logFileMiB,files:hostMock.document.ui.logFiles})), {mode:'auto',idle:45,logging:true,file:2,files:3});
  }
  await page.evaluate(()=>{
    clearTimeout(saveTimer);
    documentState.presets=[{id:'response',name:'应答控制测试',content:'AT',format:'text',desc:'',rxMatch:'',rxDesc:''}];
    documentState.workflows={reply:{name:'应答工作流',steps:[{presetId:'response',wait:0,response:'OK',responseFormat:'text',timeoutMs:1000,retries:0}]}};
    currentWorkflow='reply';renderPresets();renderWorkflow();
    document.getElementById('workspaceBody').classList.remove('hide-right');
  });
  await page.locator('.step-response').evaluate(el=>el.open=true);
  await page.fill('.step-response [data-field="timeoutMs"]','2500');
  await page.fill('.step-response [data-field="retries"]','2');
  await page.check('.step-response [data-field="waitForResponse"]');
  await page.waitForFunction(()=>hostMock.document.workflows.reply.steps[0].waitForResponse===true);
  await page.selectOption('#selWfMode','count'); await page.fill('#workflowRepeatCount','7');
  await page.evaluate(async ()=>{await saveNow();});
  assert.equal(await page.evaluate(()=>hostMock.document.ui.workflowCount),7);
  assert.equal(await page.locator('.step-response').evaluate(el=>el.scrollWidth>el.clientWidth+1),false);
  assert.equal(await page.locator('.workflow-control-bar').evaluate(el=>el.scrollWidth>el.clientWidth+1),false);
  await page.screenshot({path:path.join(output,'web-reliability-workflow-880.png')});

  const csv = await page.evaluate(()=>{
    const rows=['=1+1','  +SUM(1,2)','@x','-1','plain','a"b'].map((text,id)=>({id,dir:'RX',text,hex:'',byteCount:0,source:'test'}));
    return {safe:exportData(rows,'csv',{}),raw:exportData(rows,'csv',{csvSafe:false}),hex:parseHex('0x1 2,0xAB')};
  });
  assert.ok(csv.safe.includes('"\'=1+1"')); assert.ok(csv.safe.includes('"\'  +SUM(1,2)"'));
  assert.ok(csv.safe.includes('"\'@x"')); assert.ok(csv.safe.includes('"plain"'));
  assert.ok(csv.raw.includes('"=1+1"')); assert.deepEqual(csv.hex,[18,171]);

  const transfer = await page.evaluate(async()=>{
    clearTimeout(saveTimer);
    const presets=Array.from({length:5},(_,i)=>({id:`large-${i}`,name:'大型预设',content:'A'.repeat(900000)+'😀',format:'text',desc:'',rxMatch:'',rxDesc:''}));
    const text=JSON.stringify(presets), post=chrome.webview.postMessage;
    const chunks=[];let finished=false;
    chrome.webview.postMessage=message=>{
      if(message.command==='import') hostMock.emit({id:message.id,ok:true,result:{transferId:'large',length:text.length}});
      else if(message.command==='importChunk') {
        let end=Math.min(text.length,message.data.offset+131072);
        if(end<text.length&&/[\uD800-\uDBFF]/.test(text[end-1]))end--;
        const content=text.slice(message.data.offset,end);chunks.push(content);hostMock.emit({id:message.id,ok:true,result:{content}});
      } else if(message.command==='importFinish'){finished=true;hostMock.emit({id:message.id,ok:true,result:true});}
      else post(message);
    };
    try {
      const imported=validatePresets(await importPresets());
      documentState.presets=imported;
      const exported=await exportContent(JSON.stringify(documentState.presets),'json','large-presets.json');
      renderPresets();
      return {equal:JSON.stringify(imported)===text,finished,chunkCount:chunks.length,surrogates:chunks.every(part=>!/[\uD800-\uDBFF]/.test(part.at(-1))),exported:exported.saved,bytes:hostMock.exports.at(-1).content.length,bodyMax:Math.max(...[...document.querySelectorAll('.preset-body')].map(el=>el.textContent.length))};
    } finally {chrome.webview.postMessage=post;}
  });
  assert.equal(transfer.equal,true); assert.equal(transfer.finished,true); assert.ok(transfer.chunkCount>30);
  assert.equal(transfer.surrogates,true); assert.equal(transfer.exported,true); assert.ok(transfer.bytes>4*1024*1024); assert.ok(transfer.bodyMax<=257);
  await page.evaluate(()=>{recordError('第 2 行：非法 HEX 字符 G');document.getElementById('sendValidationTip').textContent='第 2 行：非法 HEX 字符 G';document.getElementById('sendValidationTip').style.display='';});
  await page.click('#btnOpenErrors'); assert.ok(await page.locator('#operationErrors').textContent().then(text=>text.includes('第 2 行')));
  await page.screenshot({path:path.join(output,'web-reliability-errors-880.png')}); await page.keyboard.press('Escape');
  await page.evaluate(async snapshot=>{ documentState=structuredClone(snapshot);await saveNow();restoreUi();renderPresets();renderWorkflow();document.getElementById('sendValidationTip').style.display='none'; },original);
  console.log('PASS: framing/log settings, response controls/count, CSV formula protection, shared HEX grammar, large preset import/export and persistent errors');
}
