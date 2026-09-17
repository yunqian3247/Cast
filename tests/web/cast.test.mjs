import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { verifyVirtualLogs } from './virtual-log.test.mjs';
import { verifyProtocolLogs } from './protocol-log.test.mjs';
import { verifyControlStates } from './control-states.test.mjs';
import { verifyInputSettings } from './input-settings.test.mjs';
import { verifyResponsiveLayout } from './responsive-layout.test.mjs';
import { verifyPreferences } from './preferences.test.mjs';
import { verifyScrolling } from './scrolling.test.mjs';
import { verifyUpdates } from './updates.test.mjs';
const { chromium } = process.env.PEBREL_PLAYWRIGHT_MODULE
  ? await import(pathToFileURL(process.env.PEBREL_PLAYWRIGHT_MODULE)) : await import('playwright');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(root, 'artifacts/pebrel-checks');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
const context = await browser.newContext({ viewport: { width: 1120, height: 700 } });
await context.addInitScript(() => {
  const profile = { portName: '', baudRate: 115200, dataBits: 8, parity: 0, stopBits: 0, flowControl: 0, encoding: 0, dtrEnable: false, rtsEnable: false };
  const base = { version: 1, profile, presets: [{ id: 'p1', name: '握手心跳', content: 'AT+PING', format: 'text', desc: '心跳探测', rxMatch: '+PONG', rxDesc: '心跳应答' }, { id: 'p2', name: '查询状态', content: '01 03 00 00 00 02 C4 0B', format: 'hex', desc: '读取保持寄存器', rxMatch: '01 03 04', rxDesc: '寄存器应答' }], workflows: { wf: { name: '基础自检', steps: [{ presetId: 'p1', wait: 50 }, { presetId: 'p2', wait: 100 }] } }, ui: {}, history: [] };
  const mock = window.hostMock = { document: JSON.parse(localStorage.getItem('fixture') || 'null') || base, writes: [], exports: [], imports: null, requests: [], logs: [], status: { connected: false, port: '', tx: 0, rx: 0, run: { kind: 'idle', paused: false, step: -1, round: 0, name: '' }, pins: null } };
  mock.window = { topMost: false, maximized: false };
  mock.update = { phase:'unconfigured', message:'更新地址尚未配置', canCheck:false, canDownload:false, canInstall:false };
  mock.ports = [{portName:'COM3',displayName:'COM3 (CH340)',deviceInstanceId:'USB\\VID_1A86'}];
  let listener;
  const emit = data => listener?.({ data: structuredClone(data) });
  mock.emit = emit;
  function encode(data) {
    let bytes;
    if (data.hex) { const hex = data.text.replace(/[\s,]/g,''); if (!/^(?:[0-9a-f]{2})*$/i.test(hex)) throw Error('HEX 无效'); bytes = [...hex.matchAll(/.{2}/g)].map(x => parseInt(x[0],16)); }
    else bytes = [...new TextEncoder().encode(data.text)];
    const ending = { none:[],cr:[13],lf:[10],crlf:[13,10] }[data.ending];
    if (ending) bytes.push(...ending); else if (data.ending === 'custom') { const custom = data.customEnding.replace(/\s/g,''); if (!/^(?:[0-9a-f]{2})*$/i.test(custom)) throw Error('后缀无效'); bytes.push(...[...custom.matchAll(/.{2}/g)].map(x=>parseInt(x[0],16))); }
    return { hex:bytes.map(b=>b.toString(16).padStart(2,'0').toUpperCase()).join(' '),byteCount:bytes.length };
  }
  window.chrome = { webview: { addEventListener: (_, fn) => listener = fn, postMessage: message => {
    mock.requests.push(message);
    setTimeout(() => {
      try {
        const d = message.data; let result = true;
        switch(message.command) {
          case 'init': result = { document:mock.document, ports:[{portName:'COM3',displayName:'COM3 (CH340)',deviceInstanceId:'USB\\VID_1A86'}],logs:mock.logs,status:mock.status,window:mock.window,update:mock.update,version:'1.0.2-preview.20260917' }; break;
          case 'updateStatus': result = mock.update; break;
          case 'updateCheck':
            mock.update = mock.noUpdate ? { phase:'idle', message:'已是最新版本', canCheck:true } : { phase:'available', message:'发现新版本', version:'1.0.3-preview.20260918', canCheck:true, canDownload:true };
            emit({event:'update',data:mock.update}); result=mock.update; break;
          case 'updateDownload':
            mock.update = { phase:'downloading', message:'正在下载更新...', progress:42, version:'1.0.3-preview.20260918' };
            emit({event:'update',data:mock.update});
            setTimeout(() => {
              mock.update = mock.failUpdateDownload ? { phase:'error', message:'下载更新失败，请检查网络或更新源后重试', canCheck:true, canDownload:true } : { phase:'ready', message:'更新已下载', version:'1.0.3-preview.20260918', canInstall:true };
              emit({event:'update',data:mock.update}); emit({id:message.id,ok:true,result:mock.update});
            },250); return;
          case 'updateInstall':
            if(mock.failSave) throw Error('配置写入失败');
            mock.document=structuredClone(d);mock.update={phase:'restarting',message:'正在安装更新并重启...'};
            result=mock.update;break;
          case 'window':
            if (d.action === 'pin') mock.window.topMost = !mock.window.topMost;
            if (d.action === 'maximize') mock.window.maximized = !mock.window.maximized;
            result = mock.window; break;
          case 'ports': if(mock.failPorts) throw Error('设备枚举失败');result=mock.ports;break;
          case 'save': if(mock.failSave) throw Error('配置写入失败');mock.document=structuredClone(d);localStorage.setItem('fixture',JSON.stringify(d));break;
          case 'encode': result = encode(d); break;
          case 'connect': mock.status.connected=true;mock.status.port=d.portName;mock.status.pins={dtr:false,rts:false,cts:true,dsr:false};emit({event:'status',data:mock.status});result=mock.status;break;
          case 'disconnect':mock.status.connected=false;mock.status.pins=null;mock.status.run={kind:'idle',paused:false,step:-1};emit({event:'status',data:mock.status});break;
          case 'send': { const encoded=encode(d);mock.writes.push(d);mock.status.tx+=encoded.byteCount;const log={id:Date.now(),timestamp:new Date().toISOString(),dir:'TX',text:d.text,...encoded,source:'manual'};mock.logs.push(log);emit({event:'log',data:log});emit({event:'status',data:mock.status});break; }
          case 'repeat':mock.status.run={kind:'repeat',paused:false,step:-1};emit({event:'status',data:mock.status});break;
          case 'workflow':mock.status.run={kind:'workflow',paused:d.stepOnly,step:0,round:1,name:'基础自检'};emit({event:'status',data:mock.status});break;
          case 'pause':mock.status.run.paused=d.paused;emit({event:'status',data:mock.status});break;
          case 'step':mock.status.run.step++;emit({event:'status',data:mock.status});break;
          case 'stop':mock.status.run={kind:'idle',paused:false,step:-1};emit({event:'status',data:mock.status});break;
          case 'pins':mock.status.pins={...mock.status.pins,...d};emit({event:'status',data:mock.status});break;
          case 'resetStats':mock.status.tx=mock.status.rx=0;emit({event:'status',data:mock.status});break;
          case 'clearLogs':if(mock.failClear) throw Error('清空失败');mock.logs=[];break;
          case 'copy':mock.clipboard=d.text;break;
          case 'export':mock.exports.push(d);result={saved:true};break;
          case 'import':result=mock.imports;break;
          default:throw Error('Unknown command');
        }
        emit({id:message.id,ok:true,result});
      }catch(error){emit({id:message.id,ok:false,error:error.message});}
    },mock.delayCommand===message.command?500:5);
  } } };
});
const page = await context.newPage();
const errors = [];
page.on('pageerror',error=>errors.push(error.message));
page.on('dialog',dialog=>{ errors.push(`Unexpected native ${dialog.type()}: ${dialog.message()}`); dialog.dismiss(); });
async function acceptConfirmation() {
  await page.waitForSelector('#confirmationModal.show');
  await page.click('#btnConfirmAction');
  await page.waitForSelector('#confirmationModal.show',{state:'hidden'});
}
async function checkConfirmation(label) {
  await page.waitForSelector('#confirmationModal.show');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'btnCancelConfirmation');
  assert.equal(await page.getAttribute('#confirmationModal','role'),'alertdialog');
  await page.keyboard.press('Shift+Tab');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'btnCloseConfirmation');
  await page.keyboard.press('Shift+Tab');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'btnConfirmAction');
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'btnCloseConfirmation');
  const originalSize=page.viewportSize();
  const wasDark=await page.locator('body').evaluate(el=>el.classList.contains('theme-dark'));
  for (const theme of ['light','dark']) {
    await page.locator('body').evaluate((el,dark)=>el.classList.toggle('theme-dark',dark),theme==='dark');
    for (const size of [{width:880,height:560},{width:1120,height:700}]) {
      await page.setViewportSize(size);
      await assertModalAlignment(`${label} ${theme} ${size.width}`);
      if (theme==='dark') {
        await assertDarkContrast(label);
        await page.hover('#btnConfirmAction'); await assertDarkContrast(`${label} hover`);
      }
      await page.screenshot({path:path.join(output,`web-confirm-${label}-${theme}-${size.width}.png`)});
    }
  }
  await page.setViewportSize(originalSize);
  await page.locator('body').evaluate((el,dark)=>el.classList.toggle('theme-dark',dark),wasDark);
}
async function assertDarkContrast(label) {
  await page.waitForTimeout(180);
  const failures = await page.evaluate(() => {
    const parse = color => color.match(/[\d.]+/g).map(Number);
    const blend = (front, back) => front.slice(0,3).map((value, i) => value*(front[3]??1)+back[i]*(1-(front[3]??1)));
    const luminance = rgb => rgb.map(value => { const c=value/255; return c<=.04045?c/12.92:((c+.055)/1.055)**2.4; }).reduce((sum,c,i)=>sum+c*[.2126,.7152,.0722][i],0);
    const background = element => element ? blend(parse(getComputedStyle(element).backgroundColor),background(element.parentElement)) : [255,255,255];
    const selectors = 'button,input:not([type="checkbox"]):not([type="radio"]):not([type="hidden"]),select,textarea,.log-time,.log-dir,.log-text,.log-text-hex,.proto-tag,.proto-desc,.proto-raw-inline,.preset-desc,.preset-format-tag,.step-index,.step-name,.empty-state,.titlebar-version';
    return [...document.querySelectorAll(selectors)].filter(element => {
      const box=element.getBoundingClientRect();
      if (!box.width||!box.height||getComputedStyle(element).visibility==='hidden') return false;
      for(let ancestor=element;ancestor;ancestor=ancestor.parentElement) if(Number(getComputedStyle(ancestor).opacity)===0) return false;
      return true;
    }).map(element => {
      const style=getComputedStyle(element),bg=background(element);
      const color=blend(parse(style.color),bg);
      const a=luminance(color),b=luminance(bg),ratio=(Math.max(a,b)+.05)/(Math.min(a,b)+.05);
      return { element:element.id||element.className,ratio:Number(ratio.toFixed(2)),color:style.color,background:bg };
    }).filter(result=>result.ratio<4.5);
  });
  assert.deepEqual(failures,[],`dark contrast: ${label}`);
}
async function assertModalAlignment(label) {
  assert.equal(await page.locator('#globalToast.show').count(),0,`stale toast over modal ${label}`);
  const geometry = await page.locator('.modal-overlay.show .modal-dialog').evaluate(dialog => {
    const rect = element => element.getBoundingClientRect();
    const middle = element => { const box=rect(element); return box.top+box.height/2; };
    const heading=dialog.querySelector('.modal-title > span'),close=dialog.querySelector('.modal-close'),icon=close.querySelector('svg');
    const header=dialog.querySelector('.modal-title'),headerBottom=rect(header).bottom-parseFloat(getComputedStyle(header).borderBottomWidth);
    const headerTop=rect(dialog).top+parseFloat(getComputedStyle(dialog).borderTopWidth);
    const contentRight=rect(dialog).right-parseFloat(getComputedStyle(dialog).paddingRight)-1;
    return {
      headerHeight:rect(header).bottom-headerTop,
      headerCenterOffset:Math.abs(middle(heading)-(headerTop+headerBottom)/2),
      titleOffset:Math.abs(middle(heading)-middle(close)),
      iconOffset:Math.abs(middle(icon)-middle(close)),
      closeEdge:Math.abs(rect(icon).right-contentRight),
      rowOffsets:[...dialog.querySelectorAll('.shortcut-list > div')].map(row=>Math.abs(middle(row.querySelector('dt'))-middle(row.querySelector('dd')))),
      contained:rect(dialog).top>=0&&rect(dialog).bottom<=innerHeight,
      overflow:dialog.scrollWidth>dialog.clientWidth+1
    };
  });
  assert.ok(geometry.titleOffset<1&&geometry.iconOffset<1&&geometry.closeEdge<1,`modal header alignment ${label}: ${JSON.stringify(geometry)}`);
  assert.ok(geometry.headerHeight<=37&&geometry.headerCenterOffset<1,`compact centered header ${label}: ${JSON.stringify(geometry)}`);
  assert.ok(geometry.rowOffsets.every(offset=>offset<1),`shortcut row alignment ${label}`);
  assert.ok(geometry.contained&&!geometry.overflow,`modal bounds ${label}`);
}
try {
  await page.goto(pathToFileURL(path.join(root,'src/cast.Desktop/Web/index.html')).href);
  await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('.preset-row').count(),2);
  await page.screenshot({path:path.join(output,'web-1120-default.png')});
  assert.equal(await page.locator('[title]').count(),0);
  await page.hover('#selPort');await page.waitForSelector('#portDetailPopover',{state:'visible'});
  await page.waitForFunction(()=>getComputedStyle(document.getElementById('portDetailPopover')).opacity==='1');
  assert.equal(await page.textContent('#popoverPortTitle'),'未选择串口');
  assert.equal(await page.locator('#controlTooltip').isVisible(),false);
  await page.screenshot({path:path.join(output,'web-port-tooltip-empty.png')});
  await page.selectOption('#selPort','COM3');
  assert.equal(await page.textContent('#popoverPortTitle'),'COM3 (CH340)');
  assert.match(await page.textContent('#popoverPortHwid'),/VID_1A86/);
  await page.evaluate(()=>{hostMock.ports[0].displayName='COM3 ('+'LongDeviceName'.repeat(8)+')';hostMock.ports[0].deviceInstanceId='USB\\'+'1234567890'.repeat(18);});
  await page.click('#btnRefreshPorts');await page.waitForFunction(()=>document.querySelector('#selPort option[value="COM3"]').textContent.includes('LongDeviceName'));
  await page.selectOption('#selPort','COM3');await page.waitForFunction(()=>document.getElementById('popoverPortHwid').textContent.length>100);
  await page.setViewportSize({width:880,height:560});await page.hover('#selPort');
  await page.waitForFunction(()=>getComputedStyle(document.getElementById('portDetailPopover')).opacity==='1');
  assert.equal(await page.locator('#portDetailPopover').evaluate(el=>el.scrollWidth<=el.clientWidth+1),true);
  await page.screenshot({path:path.join(output,'web-port-tooltip-long.png')});
  await page.evaluate(()=>hostMock.failPorts=true);await page.click('#btnRefreshPorts');
  await page.waitForFunction(()=>document.getElementById('popoverPortError').textContent.includes('设备枚举失败'));
  assert.equal(await page.getAttribute('#selPort','title'),null);
  await page.evaluate(()=>{hostMock.failPorts=false;hostMock.ports=[{portName:'COM3',displayName:'COM3 (CH340)',deviceInstanceId:'USB\\VID_1A86'}];});
  await page.click('#btnRefreshPorts');await page.waitForFunction(()=>document.getElementById('popoverPortError').textContent==='');
  await page.selectOption('#selPort','');await page.setViewportSize({width:1120,height:700});
  await page.waitForSelector('#globalToast.show',{state:'hidden'});
  await page.hover('#btnOpenSettings');await page.waitForSelector('#controlTooltip',{state:'visible'});
  assert.equal(await page.textContent('#controlTooltip'),'设置');
  assert.equal(await page.locator('#controlTooltip').evaluate(el=>getComputedStyle(el).backgroundColor),'rgb(255, 255, 255)');
  await page.keyboard.press('Escape');assert.equal(await page.locator('#controlTooltip').isVisible(),false);
  await page.hover('#manualInput');await page.hover('#btnToggleRight');await page.waitForTimeout(450);
  assert.equal(await page.locator('#controlTooltip').isVisible(),false);
  await page.hover('.preset-body');await page.waitForTimeout(450);
  assert.equal(await page.locator('#controlTooltip').isVisible(),false);
  await page.locator('.preset-row[data-id="p1"]').hover();await page.hover('.preset-row[data-id="p1"] button[data-action="edit"]');
  await page.waitForSelector('#controlTooltip',{state:'visible'});assert.equal(await page.textContent('#controlTooltip'),'编辑');
  await page.screenshot({path:path.join(output,'web-icon-tooltip-preset.png')});
  await page.hover('#manualInput');assert.equal(await page.locator('#controlTooltip').isVisible(),false);
  await page.click('#btnToggleTheme');await page.hover('#btnWinClose');await page.waitForSelector('#controlTooltip',{state:'visible'});
  assert.equal(await page.locator('#controlTooltip').evaluate(el=>getComputedStyle(el).backgroundColor),'rgb(255, 255, 255)');
  assert.equal(await page.locator('#controlTooltip').evaluate(el=>el.getBoundingClientRect().right<=innerWidth-8),true);
  await page.screenshot({path:path.join(output,'web-icon-tooltip-dark.png')});
  await page.click('#btnToggleTheme');
  assert.equal(await page.locator('#sendBox').evaluate(el => el.offsetHeight),135);
  assert.equal(await page.locator('.desktop-titlebar').evaluate(el => el.offsetHeight),36);
  assert.equal(await page.locator('.titlebar-center-drag').textContent(),'');
  await page.click('#btnPinWindow'); await page.waitForFunction(()=>hostMock.window.topMost);
  assert.equal(await page.getAttribute('#btnPinWindow','aria-pressed'),'true');
  await page.click('#btnPinWindow'); await page.waitForFunction(()=>!hostMock.window.topMost);
  await page.click('#btnWinMax'); await page.waitForFunction(()=>document.getElementById('btnWinMax').getAttribute('aria-label')==='还原');
  await page.click('#btnWinMax'); await page.waitForFunction(()=>document.getElementById('btnWinMax').getAttribute('aria-label')==='最大化');
  await page.click('#btnToggleTheme');
  await page.keyboard.press('Control+,'); await page.waitForSelector('#settingsModal.show');
  await page.selectOption('#cfgTerminalFontSize','13');
  await page.check('#cfgClearAfterSend'); await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(()=>hostMock.document.ui.theme==='dark');
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('body').evaluate(el=>el.classList.contains('theme-dark')),true);
  await page.click('#btnOpenSettings'); assert.equal(await page.inputValue('#cfgTerminalFontSize'),'13');
  assert.equal(await page.isChecked('#cfgClearAfterSend'),true);
  await page.selectOption('#cfgTerminalFontSize','11'); await page.click('#btnCancelSettingsModal');
  assert.equal(await page.locator('body').evaluate(el=>el.classList.contains('theme-dark')),true);
  await page.keyboard.press('Alt+/'); await page.waitForSelector('#shortcutsModal.show'); await page.keyboard.press('Escape');
  await page.click('#btnOpenAbout'); assert.equal(await page.textContent('#aboutVersion'),'v1.0.2-preview.20260917'); await page.keyboard.press('Escape');
  await page.selectOption('#selPort','COM3');await page.click('#btnConnect');
  await page.waitForFunction(()=>!document.getElementById('btnSend').disabled);
  await page.fill('#manualInput','你好');await page.click('#btnSend');
  await page.waitForFunction(()=>hostMock.writes.length===1);
  assert.equal(await page.inputValue('#manualInput'),'');
  await page.click('#btnOpenSettings'); await page.click('#btnResetSettingsDefault'); await page.click('#btnSaveSettingsModal');
  await page.waitForFunction(()=>hostMock.document.ui.theme==='dark'&&!hostMock.document.ui.clearAfterSend);
  await page.click('#btnToggleTheme');
  assert.equal(await page.locator('#statusStats').textContent(),'TX: 8 B  |  RX: 0 B');
  await page.check('#chkSendHex');await page.fill('#manualInput','01 03 00 00 00 02');await page.click('#btnSendTools');await page.click('#btnAppendCrc');
  await page.waitForFunction(()=>document.getElementById('manualInput').value==='01 03 00 00 00 02 C4 0B');
  await page.fill('#manualInput','01G');await page.dispatchEvent('#manualInput','input');
  await page.waitForFunction(()=>document.getElementById('sendValidationTip').style.display==='');
  await page.fill('#manualInput','0103');await page.click('#btnSendTools');await page.click('#btnFormatHex');await page.waitForFunction(()=>document.getElementById('manualInput').value==='01 03');
  await page.click('#btnToggleProtocol');
  await page.evaluate(()=>{const log={id:100,timestamp:new Date().toISOString(),dir:'RX',text:'+PONG',hex:'2B 50 4F 4E 47',byteCount:5,source:'serial'};hostMock.logs.push(log);hostMock.emit({event:'log',data:log});});
  await page.waitForSelector('.proto-tag.rx');assert.match(await page.locator('#logStream').textContent(),/握手心跳/);
  await page.dblclick('.log-entry[data-id="100"]');await page.waitForFunction(()=>hostMock.clipboard==='+PONG');
  await page.fill('#terminalSearch','PONG');await page.click('#btnSearchNext');assert.equal(await page.locator('#searchResultCount').textContent(),'1/1');
  await page.click('#btnExportLog');await page.selectOption('#exportFormat','json');await page.check('input[name="exportScope"][value="filtered"]');await page.click('#btnConfirmExport');
  await page.waitForFunction(()=>hostMock.exports.length===1);const exported=await page.evaluate(()=>JSON.parse(hostMock.exports[0].content));assert.equal(exported.length,1);assert.equal(exported[0].hex,'2B 50 4F 4E 47');
  await page.fill('#terminalSearch','');
  await page.click('#btnNewPreset');await page.fill('#modalPresetName','新指令');await page.fill('#modalPresetContent','AT+TEST');await page.fill('#modalPresetRxMatch','+TEST');await page.fill('#modalPresetRxDesc','测试应答');await page.click('#btnSavePresetModal');
  await page.waitForFunction(()=>hostMock.document.presets.length===3);assert.equal(await page.locator('.preset-row').count(),3);
  await page.click('#btnNewPreset');await page.fill('#modalPresetName','保存失败的预设');await page.fill('#modalPresetContent','AT+FAIL');
  await page.evaluate(()=>hostMock.failSave=true);await page.click('#btnSavePresetModal');
  await page.waitForFunction(()=>document.getElementById('globalToast').textContent==='配置写入失败');
  await page.click('#btnCancelPresetModal');await page.evaluate(()=>hostMock.failSave=false);
  await page.click('#btnDispHex');await page.waitForTimeout(350);assert.equal(await page.evaluate(()=>hostMock.document.presets.length),3);
  await page.click('.preset-row[data-id="p2"]');assert.equal(await page.inputValue('#selEnding'),'none');
  await page.click('#btnExportLog');await page.selectOption('#exportFormat','json');await page.uncheck('#chkExportHex');await page.click('#btnConfirmExport');
  await page.waitForFunction(()=>hostMock.exports.length===2);
  assert.deepEqual(await page.evaluate(()=>JSON.parse(hostMock.exports[1].content).find(log=>log.dir==='RX').bytes),[43,80,79,78,71]);
  await page.check('#chkLineByLine');await page.uncheck('#chkSendHex');await page.fill('#manualInput','A\nB');
  await page.selectOption('#selEnding','lf');await page.waitForFunction(()=>document.getElementById('sendByteCounter').textContent==='2 行 · 4 字节');
  await page.uncheck('#chkLineByLine');await page.selectOption('#selEnding','none');
  await page.fill('#manualInput','AT+NEXT');await page.press('#manualInput','Enter');await page.waitForFunction(()=>hostMock.writes.length===2);
  await page.waitForTimeout(350);await page.fill('#manualInput','');await page.press('#manualInput','ArrowUp');assert.equal(await page.inputValue('#manualInput'),'AT+NEXT');
  await page.click('#btnToggleRight');await page.click('#btnAddStepToWf');
  await page.waitForFunction(()=>hostMock.document.workflows.wf.steps.length===3);
  await page.hover('#workflowStepsContainer button[data-action="up"] >> nth=0');await page.waitForSelector('#controlTooltip',{state:'visible'});
  assert.equal(await page.textContent('#controlTooltip'),'上移');
  await page.hover('#manualInput');
  assert.equal(await page.locator('[title]').count(),0);
  await page.click('#btnStepWf');await page.waitForFunction(()=>hostMock.status.run.paused);assert.equal(await page.locator('#wfStateLabel').textContent(),'已暂停');
  await page.click('#btnStepWf');assert.equal(await page.locator('#btnStartWf').isDisabled(),true);
  await page.click('#btnPauseWf');await page.waitForFunction(()=>!hostMock.status.run.paused);await page.click('#btnStopWf');
  await page.waitForFunction(()=>hostMock.status.run.kind==='idle');
  await page.check('#chkRepeat');await page.waitForFunction(()=>hostMock.status.run.kind==='repeat');await page.uncheck('#chkRepeat');await page.waitForFunction(()=>hostMock.status.run.kind==='idle');
  await page.click('#btnOpenSettings'); await page.check('#cfgShowPins'); await page.click('#btnSaveSettingsModal');
  await page.check('#chkDtrPin');await page.waitForFunction(()=>hostMock.status.pins.dtr);
  await page.click('#btnConnect');await page.waitForFunction(()=>!hostMock.status.connected);
  await page.click('#btnAdvancedPort');await page.fill('#advCustomBaud','1500000');await page.selectOption('#advFormat','7-E-1');await page.selectOption('#advEncoding','GBK / GB2312');await page.click('#btnSaveAdvModal');
  await page.waitForFunction(()=>hostMock.document.profile.baudRate===1500000);
  for (const size of [{width:880,height:560},{width:1120,height:700},{width:1280,height:800}]) {
    await page.setViewportSize(size);await page.screenshot({path:path.join(output,`web-${size.width}-workflow.png`)});
    const overflow = await page.evaluate(()=>[...document.querySelectorAll('.topbar button,.topbar select,.send-box button,.send-box select,.send-box input,.terminal-bar button,.terminal-bar input')].filter(el=>el.getClientRects().length&&!el.closest('.modal-overlay')).filter(el=>{const r=el.getBoundingClientRect();return r.left<0||r.top<0||r.right>innerWidth+1||r.bottom>innerHeight+1;}).map(el=>el.id));
    assert.deepEqual(overflow,[],`overflow at ${size.width}`);
    const stepOverlap = await page.locator('.workflow-step-card').evaluateAll(rows => rows.some(row => {
      const delay = row.querySelector('.step-delay-box').getBoundingClientRect();
      const actions = row.querySelector('.step-actions-group').getBoundingClientRect();
      const overlap = Math.min(delay.right, actions.right) > Math.max(delay.left, actions.left) + 1 && Math.min(delay.bottom, actions.bottom) > Math.max(delay.top, actions.top) + 1;
      return overlap || row.scrollWidth > row.clientWidth + 1;
    }));
    assert.equal(stepOverlap,false,`workflow step overlap at ${size.width}`);
    const navigation = await page.locator('.desktop-titlebar button,.topbar button,.topbar select').evaluateAll(elements => elements.map(el => {
      const r = el.getBoundingClientRect(); return { id: el.id, left:r.left, right:r.right, top:r.top, bottom:r.bottom };
    }));
    for (let i=0;i<navigation.length;i++) for (let j=i+1;j<navigation.length;j++) {
      const a=navigation[i],b=navigation[j];
      assert.ok(a.right<=b.left+1||b.right<=a.left+1||a.bottom<=b.top||b.bottom<=a.top,`navigation overlap: ${a.id}, ${b.id} at ${size.width}`);
    }
    assert.ok(await page.locator('.topbar').evaluate(el=>el.offsetHeight)>=34);
    const titlebarGeometry = await page.locator('.titlebar-right-zone button').evaluateAll(buttons => buttons.map(button => {
      const bar = document.getElementById('desktopTitlebar').getBoundingClientRect();
      const box = button.getBoundingClientRect(), icon = button.querySelector('svg').getBoundingClientRect();
      return { id:button.id, fullHeight:box.top===bar.top&&box.bottom===bar.bottom, centered:Math.abs((icon.top+icon.bottom)-(box.top+box.bottom))<1 };
    }));
    for (const geometry of titlebarGeometry) { assert.ok(geometry.fullHeight,`${geometry.id} full height`); assert.ok(geometry.centered,`${geometry.id} centered`); }
    for (const id of ['btnNewPreset','btnAdvancedPort','btnExportLog','btnOpenSettings','btnOpenShortcuts','btnOpenAbout','btnNewWorkflow']) {
      await page.click(`#${id}`);
      const modalOverflow = await page.locator('.modal-overlay.show .modal-dialog').evaluate(el=>el.scrollWidth>el.clientWidth+1);
      assert.equal(modalOverflow,false,`modal overflow ${id} at ${size.width}`);
      await assertModalAlignment(`${id} at ${size.width}`);
      if(size.width===880) await page.screenshot({path:path.join(output,`web-modal-alignment-${id}-light.png`)});
      await page.keyboard.press('Escape');
    }
  }
  await page.setViewportSize({width:880,height:560});
  await page.locator('#centerSplitter').focus();await page.keyboard.press('ArrowUp');await page.keyboard.press('ArrowUp');
  assert.ok(await page.locator('#logStream').evaluate(el=>el.clientHeight)>=60);
  await page.selectOption('#selEnding','custom');await page.fill('#customEndingHex','0D 0A');
  await page.screenshot({path:path.join(output,'web-880-custom-ending.png')});
  const regions = await page.evaluate(()=>['.send-options-row','.send-editor-body','.send-bottom-bar'].map(s=>{const r=document.querySelector(s).getBoundingClientRect();return {top:r.top,bottom:r.bottom};}));
  assert.ok(regions[0].bottom<=regions[1].top&&regions[1].bottom<=regions[2].top);
  await page.selectOption('#selEnding','none');
  await page.click('#btnNewPreset');await page.screenshot({path:path.join(output,'web-preset-modal.png')});await page.click('#btnCancelPresetModal');
  await page.click('#btnAdvancedPort');await page.screenshot({path:path.join(output,'web-advanced-modal.png')});await page.click('#btnCloseAdvModal');
  await page.click('#btnExportLog');await page.screenshot({path:path.join(output,'web-export-modal.png')});await page.click('#btnCancelExportModal');
  await page.evaluate(()=>hostMock.imports=[{id:'bad',name:'bad',content:'GG',format:'hex'}]);await page.click('#btnImportPresets');await page.waitForTimeout(30);assert.equal(await page.locator('.preset-row').count(),3);
  await page.reload();await page.waitForSelector('body[data-ready="true"]');assert.equal(await page.locator('.preset-row').count(),3);assert.equal(await page.inputValue('#selBaud'),'1500000');
  if (!await page.locator('#btnNewWorkflow').isVisible()) await page.click('#btnToggleRight');
  await page.click('#btnNewWorkflow'); await page.waitForSelector('#workflowModal.show');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'workflowName');
  assert.equal(await page.locator('#workflowName').evaluate(el=>el.value.slice(el.selectionStart,el.selectionEnd)),'新工作流');
  await page.screenshot({path:path.join(output,'web-new-workflow-light.png')});
  await page.fill('#workflowName','   '); await page.keyboard.press('Enter');
  assert.equal(await page.textContent('#workflowNameError'),'请输入工作流名称');
  assert.equal(await page.evaluate(()=>Object.keys(hostMock.document.workflows).length),1);
  await page.fill('#workflowName','取消的工作流'); await page.click('#btnCancelWorkflowModal');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'btnNewWorkflow');
  await page.click('#btnNewWorkflow'); await page.keyboard.press('Escape');
  assert.equal(await page.locator('#workflowModal.show').count(),0);
  await page.click('#btnNewWorkflow'); await page.fill('#workflowName','测试工作流');
  await page.evaluate(()=>hostMock.failSave=true); await page.click('#btnCreateWorkflow');
  await page.waitForFunction(()=>document.getElementById('workflowNameError').textContent==='配置写入失败');
  assert.equal(await page.inputValue('#workflowName'),'测试工作流');
  assert.equal(await page.locator('#workflowModal.show').count(),1);
  assert.equal(await page.evaluate(()=>Object.keys(hostMock.document.workflows).length),1);
  await page.evaluate(()=>hostMock.failSave=false); await page.keyboard.press('Enter');
  await page.waitForFunction(()=>Object.keys(hostMock.document.workflows).length===2);
  await page.waitForSelector('#workflowModal.show',{state:'hidden'});
  assert.equal(await page.locator('#selWorkflow option:checked').textContent(),'测试工作流');
  await page.click('#btnAddStepToWf');await page.waitForFunction(()=>document.querySelectorAll('.workflow-step-card').length===1);
  await page.click('#workflowStepsContainer button[data-action="remove"]');await page.waitForFunction(()=>document.querySelectorAll('.workflow-step-card').length===0);
  await page.click('#btnDeleteWorkflow');await checkConfirmation('delete-workflow');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#confirmationModal.show').count(),0);
  assert.equal(await page.evaluate(()=>document.activeElement.id),'btnDeleteWorkflow');
  assert.equal(await page.evaluate(()=>Object.keys(hostMock.document.workflows).length),2);
  await page.click('#btnDeleteWorkflow');await page.click('#btnCloseConfirmation');
  assert.equal(await page.evaluate(()=>Object.keys(hostMock.document.workflows).length),2);
  await page.click('#btnDeleteWorkflow');await page.keyboard.press('Enter');
  assert.equal(await page.locator('#confirmationModal.show').count(),0);
  assert.equal(await page.evaluate(()=>Object.keys(hostMock.document.workflows).length),2);
  await page.click('#btnDeleteWorkflow');
  await page.evaluate(()=>{hostMock.failSave=true;hostMock.delayCommand='save';});
  const savesBefore=await page.evaluate(()=>hostMock.requests.filter(r=>r.command==='save').length);
  await page.click('#btnConfirmAction');
  await page.waitForSelector('#confirmationModal[aria-busy="true"]');
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'confirmationModal');
  await page.evaluate(()=>document.getElementById('btnConfirmAction').click());
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#confirmationModal.show').count(),1);
  await page.waitForFunction(()=>document.getElementById('confirmationError').textContent==='配置写入失败');
  assert.equal(await page.evaluate(()=>hostMock.requests.filter(r=>r.command==='save').length),savesBefore+1);
  assert.equal(await page.evaluate(()=>Object.keys(hostMock.document.workflows).length),2);
  assert.equal(await page.locator('#selWorkflow option:checked').textContent(),'测试工作流');
  await page.evaluate(()=>{hostMock.failSave=false;hostMock.delayCommand=null;});
  await acceptConfirmation();await page.waitForFunction(()=>Object.keys(hostMock.document.workflows).length===1);
  await page.selectOption('#selWorkflow','wf');
  await page.click('#workflowStepsContainer .workflow-step-card:first-child button[data-action="down"]');
  await page.waitForFunction(()=>hostMock.document.workflows.wf.steps[0].presetId==='p2');
  await page.locator('.preset-row[data-id="p1"]').hover();await page.click('.preset-row[data-id="p1"] button[data-action="edit"]');
  await page.fill('#modalPresetDesc','修改后的释义');await page.click('#btnSavePresetModal');
  await page.waitForFunction(()=>hostMock.document.presets[0].desc==='修改后的释义');
  await page.evaluate(()=>hostMock.imports=hostMock.document.presets.map(p=>({...p,desc:'导入的释义'})));
  await page.click('#btnImportPresets');await checkConfirmation('replace-presets');
  await page.click('#btnCancelConfirmation');
  assert.equal(await page.evaluate(()=>hostMock.document.presets[0].desc),'修改后的释义');
  await page.click('#btnImportPresets');await page.waitForSelector('#confirmationModal.show');
  await page.evaluate(()=>hostMock.failSave=true);await page.click('#btnConfirmAction');
  await page.waitForFunction(()=>document.getElementById('confirmationError').textContent==='配置写入失败');
  assert.equal(await page.evaluate(()=>hostMock.document.presets[0].desc),'修改后的释义');
  await page.evaluate(()=>hostMock.failSave=false);
  await acceptConfirmation();await page.waitForFunction(()=>hostMock.document.presets[0].desc==='导入的释义');
  await page.click('#btnExportPresets');await page.waitForFunction(()=>hostMock.exports.some(e=>e.fileName==='cast-presets.json'));
  await page.locator('.preset-row').last().hover();await page.locator('.preset-row').last().locator('button[data-action="delete"]').click();
  await checkConfirmation('delete-preset');await page.click('#btnCancelConfirmation');
  assert.equal(await page.locator('.preset-row').count(),3);
  await page.locator('.preset-row').last().hover();await page.locator('.preset-row').last().locator('button[data-action="delete"]').click();
  await page.evaluate(()=>hostMock.failSave=true);await page.click('#btnConfirmAction');
  await page.waitForFunction(()=>document.getElementById('confirmationError').textContent==='配置写入失败');
  await page.click('#btnCancelConfirmation');
  assert.equal(await page.evaluate(()=>document.activeElement.id),'presetSearchInput');
  assert.equal(await page.locator('.preset-row').count(),3);
  await page.evaluate(()=>hostMock.failSave=false);
  await page.locator('.preset-row').last().hover();await page.locator('.preset-row').last().locator('button[data-action="delete"]').click();
  await page.evaluate(()=>{hostMock.status.run.kind='workflow';hostMock.emit({event:'status',data:hostMock.status});});
  await page.click('#btnConfirmAction');await page.waitForFunction(()=>document.getElementById('confirmationError').textContent.length>0);
  assert.equal(await page.evaluate(()=>hostMock.document.presets.length),3);
  await page.evaluate(()=>{hostMock.status.run.kind='idle';hostMock.emit({event:'status',data:hostMock.status});});
  await acceptConfirmation();
  await page.waitForFunction(()=>hostMock.document.presets.length===2);
  assert.equal(await page.evaluate(()=>document.activeElement.id),'presetSearchInput');
  await page.evaluate(()=>hostMock.emit({event:'log',data:{id:1000,timestamp:new Date().toISOString(),dir:'RX',text:'状态正常',hex:'4F 4B',byteCount:2,source:'serial'}}));
  await page.click('#btnToggleProtocol');await page.click('#btnDispBoth');await page.selectOption('#selTimestampFormat','none');
  assert.equal(await page.locator('.log-time').count(),0);assert.ok(await page.locator('.log-text-hex').count()>0);
  await page.selectOption('#filterDir','rx');assert.ok((await page.locator('.log-dir').allTextContents()).every(dir=>dir==='RX'));
  await page.click('#btnClearTerminal');await checkConfirmation('clear-terminal');await page.click('#btnCancelConfirmation');
  assert.ok(await page.locator('.log-entry').count()>0);
  await page.click('#btnClearTerminal');await page.evaluate(()=>hostMock.failClear=true);await page.click('#btnConfirmAction');
  await page.waitForFunction(()=>document.getElementById('confirmationError').textContent==='清空失败');
  assert.ok(await page.locator('.log-entry').count()>0);
  await page.evaluate(()=>hostMock.failClear=false);await acceptConfirmation();
  await page.waitForFunction(()=>document.querySelectorAll('.log-entry').length===0);
  const cdp = await context.newCDPSession(page);
  await cdp.send('Emulation.setDeviceMetricsOverride',{width:1120,height:700,deviceScaleFactor:1.5,mobile:false});
  await page.screenshot({path:path.join(output,'web-1120-dpi150.png')});
  await cdp.send('Emulation.clearDeviceMetricsOverride');
  const voicePresets = JSON.parse(fs.readFileSync(path.join(root, 'samples/presets/main-controller-voice/main-controller-voice-presets.json'), 'utf8'));
  await page.evaluate(presets => hostMock.imports = presets, voicePresets);
  await page.click('#btnImportPresets');
  await acceptConfirmation();
  await page.waitForFunction(() => hostMock.document.presets.length === 60);
  assert.equal(await page.locator('.preset-row').count(), 60);
  assert.equal(await page.locator('#selAddStepPreset option').count(), 60);
  await page.evaluate(() => hostMock.emit({event:'log',data:{id:2000,timestamp:new Date().toISOString(),dir:'RX',text:'OK',hex:'4F 4B',byteCount:2,source:'serial'}}));
  await page.waitForSelector('.log-entry[data-id="2000"]');
  await page.fill('#terminalSearch','OK');
  await page.waitForFunction(()=>document.getElementById('searchResultCount').textContent==='1/1');
  assert.equal(await page.locator('#searchResultCount').textContent(),'1/1');
  await page.fill('#terminalSearch','');
  await page.fill('#presetSearchInput','主机已关闭');
  await page.click('.preset-row[data-id="main-voice-81-00-05"]');
  assert.equal(await page.inputValue('#manualInput'), 'A5 FA 81 00 05 25 FB');
  assert.equal(await page.isChecked('#chkSendHex'),true);
  assert.equal(await page.inputValue('#selEnding'),'none');
  await page.selectOption('#selPort','COM3');await page.click('#btnConnect');
  await page.waitForFunction(()=>hostMock.status.connected);
  const voiceWrites = await page.evaluate(()=>hostMock.writes.length);
  await page.locator('.preset-row[data-id="main-voice-81-00-05"]').hover();
  await page.click('.preset-row[data-id="main-voice-81-00-05"] button[data-action="send"]');
  await page.waitForFunction(count=>hostMock.writes.length === count+1,voiceWrites);
  assert.deepEqual(await page.evaluate(()=>hostMock.writes.at(-1)),{text:'A5 FA 81 00 05 25 FB',hex:true,ending:'none'});
  await page.click('#btnConnect');await page.waitForFunction(()=>!hostMock.status.connected);
  await page.fill('#presetSearchInput','');
  await page.click('#btnExportPresets');
  await page.waitForFunction(() => hostMock.exports.some(e => e.fileName === 'cast-presets.json' && JSON.parse(e.content).length === 60));
  await page.reload(); await page.waitForSelector('body[data-ready="true"]');
  assert.equal(await page.locator('.preset-row').count(),60);
  if (!await page.locator('#btnNewWorkflow').isVisible()) await page.click('#btnToggleRight');
  await page.setViewportSize({width:1120,height:700});
  await page.screenshot({path:path.join(output,'web-voice-presets.png')});
  await page.click('#btnToggleTheme'); await page.waitForFunction(()=>document.body.classList.contains('theme-dark'));
  await page.screenshot({path:path.join(output,'web-titlebar-dark.png')});
  await page.hover('#btnWinClose'); await page.screenshot({path:path.join(output,'web-titlebar-close-hover.png')});
  await page.locator('#manualInput').hover();
  await page.fill('#manualInput','GG'); await page.click('#btnSendTools'); await page.click('#btnFormatHex');
  await page.waitForSelector('#globalToast.show');
  await page.waitForTimeout(200);
  const toastContrast = await page.locator('#globalToast').evaluate(el => {
    const style = getComputedStyle(el);
    const luminance = color => {
      const rgb = color.match(/[\d.]+/g).slice(0,3).map(value => {
        const channel = Number(value)/255; return channel<=.04045 ? channel/12.92 : ((channel+.055)/1.055)**2.4;
      });
      return rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722;
    };
    const a=luminance(style.color),b=luminance(style.backgroundColor);
    return (Math.max(a,b)+.05)/(Math.min(a,b)+.05);
  });
  assert.ok(toastContrast>=4.5,`toast contrast ${toastContrast}`);
  for (const size of [{width:880,height:560},{width:1120,height:700}]) {
    await page.setViewportSize(size);
    for (const toggle of [null,'btnToggleLeft','btnToggleRight']) {
      if (toggle) await page.click(`#${toggle}`);
      const geometry=await page.locator('#globalToast').evaluate(el => {
        const toast=el.getBoundingClientRect(),log=document.getElementById('logStream').getBoundingClientRect();
        return { centered:Math.abs((toast.left+toast.right)-(log.left+log.right))<1, bottom:log.bottom-toast.bottom, contained:toast.left>=log.left&&toast.right<=log.right };
      });
      assert.ok(geometry.centered&&geometry.contained);
      assert.equal(geometry.bottom,12);
    }
    await page.click('#btnToggleLeft'); await page.click('#btnToggleRight');
  }
  await page.fill('#manualInput','GG'); await page.click('#btnSendTools'); await page.click('#btnFormatHex');
  await page.screenshot({path:path.join(output,'web-toast-dark.png')});
  await page.click('#btnOpenSettings'); await page.screenshot({path:path.join(output,'web-settings-dark.png')}); await page.keyboard.press('Escape');
  await assertDarkContrast('disconnected');
  for (const id of ['btnNewPreset','btnAdvancedPort','btnExportLog','btnOpenSettings','btnOpenShortcuts','btnOpenAbout','btnNewWorkflow']) {
    await page.click(`#${id}`); await assertDarkContrast(id);
    await assertModalAlignment(`${id} dark`);
    await page.screenshot({path:path.join(output,`web-modal-alignment-${id}-dark.png`)});
    if(id==='btnNewWorkflow') await page.screenshot({path:path.join(output,'web-new-workflow-dark.png')});
    const primary=page.locator('.modal-overlay.show .btn-primary:visible');
    if(await primary.count()) { await primary.hover(); await assertDarkContrast(`${id} primary hover`); }
    await page.keyboard.press('Escape');
  }
  await page.fill('#manualInput','01 03');
  await page.click('#btnConnect'); await page.waitForFunction(()=>hostMock.status.connected);
  await assertDarkContrast('connected');
  await page.hover('#btnSend'); await assertDarkContrast('send hover');
  await page.screenshot({path:path.join(output,'web-dark-send-enabled.png')});
  await page.selectOption('#filterDir','all'); await page.selectOption('#selTimestampFormat','ms');
  await page.evaluate(()=>hostMock.emit({event:'log',data:{id:3000,timestamp:new Date().toISOString(),dir:'RX',text:'STATUS=OK',hex:'4F 4B',byteCount:2,source:'serial'}}));
  await page.fill('#terminalSearch','STATUS'); await assertDarkContrast('search highlight');
  await page.click('#btnToggleProtocol'); await assertDarkContrast('search raw data');
  await page.fill('#terminalSearch','');
  await page.check('#chkRepeat'); await page.waitForFunction(()=>hostMock.status.run.kind==='repeat');
  await page.locator('.preset-row').first().hover(); await assertDarkContrast('running disabled controls');
  await page.screenshot({path:path.join(output,'web-dark-send-disabled.png')});
  await page.uncheck('#chkRepeat'); await page.waitForFunction(()=>hostMock.status.run.kind==='idle');
  await page.click('#btnConnect'); await page.waitForFunction(()=>!hostMock.status.connected);
  await page.click('#btnToggleTheme');
  await cdp.send('Emulation.setDeviceMetricsOverride',{width:1120,height:700,deviceScaleFactor:2,mobile:false});
  await page.locator('#desktopTitlebar').screenshot({path:path.join(output,'web-titlebar-alignment-200.png')});
  await cdp.send('Emulation.clearDeviceMetricsOverride');
  const reference=await browser.newPage({viewport:{width:1100,height:760}});
  await reference.goto(pathToFileURL(path.join(root,'ui-preview/index-pebrel.html')).href);
  await reference.screenshot({path:path.join(output,'design-reference.png')});await reference.close();
  await verifyControlStates(page,output);
  await verifyProtocolLogs(page,output);
  await verifyInputSettings(page,output);
  await verifyResponsiveLayout(page,output);
  await verifyPreferences(page,output);
  await verifyScrolling(page,output);
  await verifyUpdates(page,output);
  await verifyVirtualLogs(page,output);
  assert.deepEqual(errors,[]);
  assert.equal(await page.locator('[title]').count(),0);
  console.log('PASS: bridge UI, presets, CRC, encoding count, search/export, workflow controls, pins, persistence, three viewports and modal screenshots');
} finally { await browser.close(); }
