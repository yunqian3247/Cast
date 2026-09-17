'use strict';
const $ = id => document.getElementById(id);
const $$ = selector => [...document.querySelectorAll(selector)];
const pending = new Map();
let requestId = 0;
let documentState, ports = [], logs = [], currentWorkflow = '', status = { connected: false, run: { kind: 'idle', paused: false, step: -1 } };
let displayMode = 'text', translateProtocol = false, autoScroll = true, searchIndex = -1, matches = [], historyIndex = -1;
let initialized = false, saveTimer, toastTimer, renderQueued = false, validationVersion = 0, refreshPending = false;
let savedDocument;
let modalReturnFocus, modalFallbackFocus;
let confirmationAction;
let updateState = { phase: 'unconfigured', message: '更新地址尚未配置' }, updateRequestPending = false;
let tooltipTimer, tooltipTarget;
let logClick;
let theme = 'light', terminalFontSize = 12, clearAfterSend = false;
let uiFontSize = 12, rememberInput = true, hoverTips = true, showPins = false;
let leftFunction = 'presets', leftDefaultVisible = true, rightDefaultVisible = false;
const UI_FONT_SIZES = [11, 12, 13, 14, 16], TERMINAL_FONT_SIZES = [11, 12, 13, 14, 16, 18, 20];
const MAX_LOGS = 10000, MAX_LOG_TEXT_BYTES = 16 * 1024 * 1024;
const logCache = new WeakMap();
const expandedProtocols = new WeakSet(), protocolIcons = new Map();
const clockFormat = new Intl.DateTimeFormat('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' });
let protocolVersion = 0, protocolRules = [], searchTimer, logView, filteredSource, filteredDirection, filteredLogs = [];

function cachedLog(log) {
  let cached = logCache.get(log);
  if (!cached) { cached = {}; logCache.set(log, cached); }
  return cached;
}

function appendLogs(entries) {
  const combined = logs.concat(entries);
  let start = combined.length, bytes = 0;
  while (start > 0 && combined.length - start < MAX_LOGS) {
    const log = combined[start - 1], size = 2 * (log.text.length + log.hex.length);
    if (bytes + size > MAX_LOG_TEXT_BYTES && start < combined.length) break;
    bytes += size; start--;
  }
  logs = combined.slice(start);
}

function request(command, data = {}) {
  if (!window.chrome?.webview) return Promise.reject(new Error('桌面串口服务尚未就绪'));
  const id = String(++requestId);
  return new Promise((resolve, reject) => {
    const timeoutMs = command === 'updateDownload' ? 0 : command === 'updateCheck' ? 120000 : command === 'export' || command === 'import' ? 300000 : 30000;
    const timeout = timeoutMs ? setTimeout(() => { pending.delete(id); reject(new Error('操作超时，请重试')); }, timeoutMs) : null;
    pending.set(id, { resolve, reject, timeout });
    window.chrome.webview.postMessage({ id, command, data });
  });
}

function toast(text) {
  $('globalToast').textContent = text;
  $('globalToast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('globalToast').classList.remove('show'), 4500);
}

function safe(action) { return async event => { try { await action(event); } catch (error) { toast(error.message); } }; }

function renderUpdate(next = updateState) {
  updateState = next;
  const busy = updateRequestPending || ['checking', 'downloading', 'restarting'].includes(next.phase);
  $('updateStatus').textContent = next.message + (next.phase === 'downloading' ? ` ${next.progress || 0}%` : '');
  $('updateVersion').hidden = !next.version;
  $('updateVersion').textContent = next.version ? `v${next.version}` : '';
  $('updateProgress').hidden = next.phase !== 'downloading';
  $('updateProgress').value = next.progress || 0;
  $('btnCheckUpdate').disabled = busy || !next.canCheck;
  $('btnDownloadUpdate').hidden = !next.canDownload;
  $('btnDownloadUpdate').disabled = busy;
  $('btnInstallUpdate').hidden = !next.canInstall;
  $('btnInstallUpdate').disabled = busy || status.run.kind !== 'idle';
  $('updateRestartNotice').hidden = !next.canInstall;
  $('updateRestartNotice').textContent = status.run.kind !== 'idle'
    ? '请先停止发送或工作流，再安装更新。' : '安装将关闭串口并重启应用，当前通信记录会清空。';
}

async function runUpdate(command) {
  if (updateRequestPending) return;
  updateRequestPending = true;
  $('updateError').textContent = '';
  renderUpdate();
  try {
    let data = {};
    if (command === 'updateInstall') { ensureIdle(); clearTimeout(saveTimer); captureUi(); data = structuredClone(documentState); }
    renderUpdate(await request(command, data));
  } catch (error) { $('updateError').textContent = error.message; }
  finally { updateRequestPending = false; renderUpdate(); }
}
function on(id, event, action) { $(id).addEventListener(event, safe(action)); }
function escapeHtml(value) { return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function icons() { window.lucide?.createIcons(); }
function iconButton(id, name, label) {
  const button = $(id);
  button.innerHTML = `<i data-lucide="${name}"></i>`;
  button.setAttribute('aria-label', label);
  button.classList.add('icon-button');
}

function hideTooltip() {
  clearTimeout(tooltipTimer);
  const tooltip = $('controlTooltip');
  if (tooltip) tooltip.hidden = true;
  if (tooltipTarget) {
    const description = (tooltipTarget.getAttribute('aria-describedby') || '').split(/\s+/).filter(id => id && id !== 'controlTooltip').join(' ');
    if (description) tooltipTarget.setAttribute('aria-describedby', description);
    else tooltipTarget.removeAttribute('aria-describedby');
  }
  tooltipTarget = null;
}

function queueTooltip(target) {
  if (document.body.classList.contains('tooltips-disabled')) { hideTooltip(); return; }
  const button = target.closest('button');
  if (!button || button.disabled || button.textContent.trim() || !button.querySelector('svg, [data-lucide]')) { hideTooltip(); return; }
  if (tooltipTarget === button) return;
  hideTooltip();
  tooltipTarget = button;
  tooltipTimer = setTimeout(() => {
    if (!button.isConnected || button.disabled || !button.getClientRects().length) { hideTooltip(); return; }
    const label = button.dataset.tooltip || button.getAttribute('aria-label');
    if (!label) return;
    const tooltip = $('controlTooltip');
    tooltip.textContent = label;
    tooltip.hidden = false;
    const anchor = button.getBoundingClientRect(), box = tooltip.getBoundingClientRect();
    const left = Math.max(8, Math.min(innerWidth - box.width - 8, anchor.left + (anchor.width - box.width) / 2));
    const top = anchor.bottom + box.height + 6 <= innerHeight - 8 ? anchor.bottom + 6 : Math.max(8, anchor.top - box.height - 6);
    tooltip.style.left = `${left}px`; tooltip.style.top = `${top}px`;
    button.setAttribute('aria-describedby', [button.getAttribute('aria-describedby'), 'controlTooltip'].filter(Boolean).join(' '));
  }, 400);
}

function configureTooltips() {
  document.addEventListener('pointerover', event => queueTooltip(event.target));
  document.addEventListener('pointerout', event => {
    if (tooltipTarget?.contains(event.target) && !tooltipTarget.contains(event.relatedTarget)) hideTooltip();
  });
  document.addEventListener('focusin', event => { if (event.target.matches(':focus-visible')) queueTooltip(event.target); });
  document.addEventListener('focusout', hideTooltip);
  document.addEventListener('pointerdown', hideTooltip);
  document.addEventListener('keydown', hideTooltip);
  document.addEventListener('scroll', hideTooltip, true);
  window.addEventListener('resize', hideTooltip);
  window.addEventListener('blur', hideTooltip);
}

function closeSendTools(restoreFocus = false) {
  $('sendToolsMenu').hidden = true;
  $('btnSendTools').setAttribute('aria-expanded', 'false');
  if (restoreFocus) $('btnSendTools').focus();
}

function configureSendTools() {
  const trigger = $('btnSendTools'), menu = $('sendToolsMenu'), container = trigger.parentElement;
  const items = () => [...menu.querySelectorAll('button:not(:disabled)')];
  const open = (last = false) => {
    if (trigger.disabled) return;
    hideTooltip();
    menu.hidden = false;
    trigger.setAttribute('aria-expanded', 'true');
    items().at(last ? -1 : 0)?.focus();
  };
  on('btnSendTools', 'click', () => menu.hidden ? open() : closeSendTools(true));
  on('btnSendTools', 'keydown', event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); open(event.key === 'ArrowUp'); }
  });
  menu.addEventListener('keydown', event => {
    const controls = items(), index = controls.indexOf(document.activeElement);
    if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
      event.preventDefault();
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? controls.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + controls.length) % controls.length;
      controls[next]?.focus();
    }
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); closeSendTools(true); }
  });
  container.addEventListener('focusout', event => { if (!container.contains(event.relatedTarget)) closeSendTools(); });
  document.addEventListener('pointerdown', event => { if (!container.contains(event.target)) closeSendTools(); });
  window.addEventListener('blur', () => closeSendTools());
}

function showModal(id) {
  hideTooltip();
  closeSendTools();
  clearTimeout(toastTimer);
  $('globalToast').classList.remove('show');
  modalReturnFocus = document.activeElement;
  modalFallbackFocus = null;
  $(id).classList.add('show');
  $(id).setAttribute('role', 'dialog');
  $(id).setAttribute('aria-modal', 'true');
  $(id).querySelector('input:not([type="hidden"]),select,button')?.focus();
}
function hideModal(id) {
  hideTooltip();
  if ($(id).getAttribute('aria-busy') === 'true') return;
  if (id === 'advancedPortModal') applyProfile(documentState.profile);
  if (id === 'settingsModal') { applyTypography(); applyHoverTips(); applyPinVisibility(); }
  if (id === 'confirmationModal') confirmationAction = null;
  $(id).classList.remove('show');
  const focusTarget = modalReturnFocus?.isConnected && !modalReturnFocus.disabled ? modalReturnFocus : modalFallbackFocus;
  focusTarget?.focus();
}

function showConfirmation({ title, message, confirmLabel, action, fallbackFocus }) {
  if (document.querySelector('.modal-overlay.show')) return;
  confirmationAction = action;
  $('confirmationTitle').textContent = title;
  $('confirmationMessage').textContent = message;
  $('confirmationError').textContent = '';
  $('btnConfirmAction').textContent = confirmLabel;
  showModal('confirmationModal');
  modalFallbackFocus = fallbackFocus;
  $('confirmationModal').setAttribute('role', 'alertdialog');
  $('btnCancelConfirmation').focus();
}

async function submitConfirmation() {
  const modal = $('confirmationModal');
  if (!confirmationAction || modal.getAttribute('aria-busy') === 'true') return;
  modal.setAttribute('aria-busy', 'true');
  const controls = [...modal.querySelectorAll('button')];
  controls.forEach(control => control.disabled = true);
  modal.tabIndex = -1;
  modal.focus();
  $('confirmationError').textContent = '';
  let completed = false;
  try { await confirmationAction(); completed = true; }
  catch (error) { $('confirmationError').textContent = error.message; }
  finally { modal.removeAttribute('aria-busy'); controls.forEach(control => control.disabled = false); }
  if (completed) hideModal('confirmationModal'); else $('btnCancelConfirmation').focus();
}

function applyTerminalFontSize(size = terminalFontSize) {
  const style = document.documentElement.style;
  if (style.getPropertyValue('--terminal-font-size') === `${size}px`) return;
  style.setProperty('--terminal-font-size', `${size}px`);
  if (logView) { logView.invalidate(); logView.paint(); }
}

function applyTypography(size = uiFontSize, terminalSize = terminalFontSize) {
  const style = document.documentElement.style;
  style.setProperty('--ui-font-size', `${size}px`);
  applyTerminalFontSize(terminalSize);
}

function applyHoverTips(enabled = hoverTips) {
  hideTooltip();
  document.body.classList.toggle('tooltips-disabled', !enabled);
}

function applyPinVisibility(visible = showPins) {
  $('statusPins').hidden = !visible;
}

function applySidebarLayout() {
  const swapped = leftFunction === 'workflow', workspace = $('workspaceBody');
  const left = swapped ? $('rightPane') : $('leftPane'), right = swapped ? $('leftPane') : $('rightPane');
  left.className = 'left-pane'; right.className = 'right-pane';
  workspace.insertBefore(left, workspace.querySelector('.center-pane'));
  workspace.append(right);
  workspace.classList.toggle('swapped-panels', swapped);
  for (const [id, label, side] of [['btnToggleLeft', swapped ? '工作流' : '预设', 'left'], ['btnToggleRight', swapped ? '预设' : '工作流', 'right']]) {
    $(id).innerHTML = `${label} <i data-lucide="panel-${side}"></i>`;
    $(id).setAttribute('aria-label', `展开/收起${side === 'left' ? '左' : '右'}侧${label}栏`);
  }
  icons();
}

function applyAppearance() {
  hideTooltip();
  document.body.classList.add('theme-changing');
  document.body.classList.toggle('theme-dark', theme === 'dark');
  applyTypography(); applyHoverTips(); applyPinVisibility();
  const button = $('btnToggleTheme');
  button.innerHTML = `<i data-lucide="${theme === 'dark' ? 'sun' : 'moon'}"></i>`;
  button.setAttribute('aria-label', theme === 'dark' ? '切换浅色模式' : '切换深色模式');
  button.setAttribute('aria-pressed', String(theme === 'dark'));
  icons();
  requestAnimationFrame(() => requestAnimationFrame(() => document.body.classList.remove('theme-changing')));
}

function updateWindowState(state) {
  if (!state) return;
  $('btnPinWindow').classList.toggle('active', state.topMost);
  $('btnPinWindow').setAttribute('aria-pressed', String(state.topMost));
  $('btnPinWindow').setAttribute('aria-label', state.topMost ? '取消窗口置顶' : '窗口置顶');
  $('btnWinMax').setAttribute('aria-label', state.maximized ? '还原' : '最大化');
  $('btnWinMax').innerHTML = `<i data-lucide="${state.maximized ? 'copy' : 'square'}"></i>`;
  icons();
}

function openSettings() {
  $('cfgUiFontSize').value = String(uiFontSize);
  $('cfgTerminalFontSize').value = String(terminalFontSize);
  $('cfgClearAfterSend').checked = clearAfterSend;
  $('cfgRememberInput').checked = rememberInput;
  $('cfgHoverTips').checked = hoverTips;
  $('cfgAutoScroll').checked = autoScroll;
  $('cfgShowPins').checked = showPins;
  $('cfgLeftFunction').value = leftFunction;
  $('cfgRightFunction').value = leftFunction === 'presets' ? 'workflow' : 'presets';
  $('cfgLeftDefaultVisible').checked = leftDefaultVisible;
  $('cfgRightDefaultVisible').checked = rightDefaultVisible;
  showModal('settingsModal');
}

function profileFromControls() {
  const parts = $('advFormat').value.split('-');
  const selected = ports.find(port => port.portName === $('selPort').value);
  return { ...documentState.profile, portName: selected?.portName || '', deviceInstanceId: selected?.deviceInstanceId || null,
    baudRate: Number($('selBaud').value), dataBits: Number(parts[0]), parity: { N: 0, O: 1, E: 2 }[parts[1]],
    stopBits: parts[2] === '2' ? 2 : 0, encoding: { 'UTF-8': 0, 'GBK / GB2312': 1, ASCII: 2 }[$('advEncoding').value],
    flowControl: { none: 0, rtscts: 1, xonxoff: 2 }[$('advFlowControl').value],
    dtrEnable: $('chkDtrPin').checked, rtsEnable: $('chkRtsPin').checked };
}

function applyProfile(profile) {
  const format = `${profile.dataBits}-${['N', 'O', 'E'][profile.parity] || 'N'}-${profile.stopBits === 2 ? 2 : 1}`;
  $('advFormat').value = format;
  $('lblAdvFormat').textContent = format;
  $('advEncoding').value = ['UTF-8', 'GBK / GB2312', 'ASCII'][profile.encoding] || 'UTF-8';
  $('advFlowControl').value = ['none', 'rtscts', 'xonxoff'][profile.flowControl] || 'none';
  if (![...$('selBaud').options].some(option => option.value === String(profile.baudRate))) $('selBaud').add(new Option(profile.baudRate, profile.baudRate));
  $('selBaud').value = String(profile.baudRate);
  $('chkDtrPin').checked = !!profile.dtrEnable;
  $('chkRtsPin').checked = !!profile.rtsEnable;
}

function renderPorts(list, initial = false) {
  const previous = ports.find(port => port.portName === $('selPort').value) || (initial ? documentState.profile : null);
  ports = list;
  const selected = previous && ports.find(port => port.portName === previous.portName && (!previous.deviceInstanceId || port.deviceInstanceId === previous.deviceInstanceId));
  $('selPort').replaceChildren(new Option(ports.length ? '请选择串口' : '未检测到串口', ''));
  for (const port of ports) $('selPort').add(new Option(port.displayName, port.portName));
  $('selPort').value = selected?.portName || '';
  updatePortDetail();
}

function updatePortDetail(error = '') {
  const port = ports.find(item => item.portName === $('selPort').value);
  $('popoverPortTitle').textContent = port?.displayName || (ports.length ? '未选择串口' : '未检测到串口');
  $('popoverPortHwid').textContent = port?.deviceInstanceId ? `硬件识别码: ${port.deviceInstanceId}` : '';
  $('popoverPortError').textContent = error ? `读取串口列表失败：${error}` : '';
}

function captureUi() {
  documentState.ui = { displayMode, translateProtocol, autoScroll, currentWorkflow, theme, terminalFontSize, clearAfterSend,
    uiFontSize, rememberInput, hoverTips, showPins, leftFunction, leftDefaultVisible, rightDefaultVisible,
    inputDraft: rememberInput ? $('manualInput').value.slice(0, 1048576) : '',
    leftHidden: $('workspaceBody').classList.contains('hide-left'), rightHidden: $('workspaceBody').classList.contains('hide-right'),
    sendHeight: parseInt(getComputedStyle(document.documentElement).getPropertyValue('--send-box-h')) || 135,
    timestamp: $('selTimestampFormat').value, ending: $('selEnding').value, customEnding: $('customEndingHex').value,
    sendHex: $('chkSendHex').checked, lineByLine: $('chkLineByLine').checked,
    interval: Number($('repeatInterval').value), workflowMode: $('selWfMode').value };
  if (!status.connected) documentState.profile = profileFromControls();
}

async function saveNow() {
  clearTimeout(saveTimer);
  if (!initialized || status.run.kind !== 'idle') return;
  captureUi();
  const snapshot = structuredClone(documentState);
  try {
    await request('save', snapshot);
    savedDocument = snapshot;
  } catch (error) {
    documentState = structuredClone(savedDocument);
    applyProfile(documentState.profile); restoreUi(); renderPresets(); renderWorkflow(); renderLogs();
    throw error;
  }
}
function scheduleSave() { clearTimeout(saveTimer); saveTimer = setTimeout(() => saveNow().catch(error => toast(error.message)), 300); }

function restoreUi() {
  const ui = documentState.ui || {};
  theme = ui.theme === 'dark' ? 'dark' : 'light';
  uiFontSize = UI_FONT_SIZES.includes(ui.uiFontSize) ? ui.uiFontSize : 12;
  terminalFontSize = TERMINAL_FONT_SIZES.includes(ui.terminalFontSize) ? ui.terminalFontSize : 12;
  rememberInput = ui.rememberInput !== false; hoverTips = ui.hoverTips !== false;
  showPins = ui.showPins === true;
  leftFunction = ui.leftFunction === 'workflow' ? 'workflow' : 'presets';
  leftDefaultVisible = ui.leftDefaultVisible ?? !ui.leftHidden;
  rightDefaultVisible = ui.rightDefaultVisible ?? ui.rightHidden === false;
  applySidebarLayout();
  clearAfterSend = !!ui.clearAfterSend;
  applyAppearance();
  displayMode = ['text', 'hex', 'both'].includes(ui.displayMode) ? ui.displayMode : 'text';
  translateProtocol = !!ui.translateProtocol;
  autoScroll = ui.autoScroll !== false;
  currentWorkflow = ui.currentWorkflow || Object.keys(documentState.workflows)[0] || '';
  $('workspaceBody').classList.toggle('hide-left', initialized ? !!ui.leftHidden : !leftDefaultVisible);
  $('workspaceBody').classList.toggle('hide-right', initialized ? ui.rightHidden !== false : !rightDefaultVisible);
  if (!initialized) $('manualInput').value = rememberInput ? (ui.inputDraft ?? documentState.history[0] ?? '') : '';
  document.documentElement.style.setProperty('--send-box-h', `${Math.max(135, Math.min(400, ui.sendHeight || 135))}px`);
  $('selTimestampFormat').value = ui.timestamp || 'ms';
  $('selEnding').value = ui.ending || 'crlf';
  $('customEndingHex').value = ui.customEnding || '';
  $('chkSendHex').checked = !!ui.sendHex;
  $('chkLineByLine').checked = !!ui.lineByLine;
  $('repeatInterval').value = ui.interval || 1000;
  $('selWfMode').value = ui.workflowMode || 'once';
  updateViewButtons();
}

function updateViewButtons() {
  for (const [id, mode] of [['btnDispText', 'text'], ['btnDispHex', 'hex'], ['btnDispBoth', 'both']]) {
    $(id).classList.toggle('active', displayMode === mode);
    $(id).setAttribute('aria-pressed', String(displayMode === mode));
  }
  $('labelTopProto').textContent = `协议翻译: ${translateProtocol ? '开' : '关'}`;
  $('btnToggleProtocol').classList.toggle('active', translateProtocol);
  $('btnToggleLeft').classList.toggle('active', !$('workspaceBody').classList.contains('hide-left'));
  $('btnToggleRight').classList.toggle('active', !$('workspaceBody').classList.contains('hide-right'));
  $('btnToggleLeft').setAttribute('aria-expanded', String(!$('workspaceBody').classList.contains('hide-left')));
  $('btnToggleRight').setAttribute('aria-expanded', String(!$('workspaceBody').classList.contains('hide-right')));
  $('customEndingHex').style.display = $('selEnding').value === 'custom' ? '' : 'none';
}

function updateStatus(next) {
  const wasBusy = status.run.kind !== 'idle';
  status = next;
  renderUpdate();
  const run = next.run, busy = run.kind !== 'idle';
  document.body.classList.toggle('busy-edit', busy);
  $('btnConnect').textContent = next.connected ? '关闭端口' : '打开端口';
  $('btnConnect').classList.toggle('connected', next.connected);
  $('statusDot').classList.toggle('online', next.connected);
  $('statusPortLabel').textContent = next.connected ? `${next.port} · 已连接` : '未连接';
  $('statusStats').textContent = `TX: ${next.tx || 0} B  |  RX: ${next.rx || 0} B`;
  $('statusChannel').textContent = run.kind === 'idle' ? '发送通道: 就绪' : run.kind === 'repeat' ? '发送通道: 定时循环' : run.kind === 'workflow' ? `工作流: ${run.paused ? '已暂停' : '执行中'} · 第 ${run.round || 1} 轮` : '发送通道: 发送中';
  for (const id of ['selPort', 'selBaud', 'btnAdvancedPort', 'btnRefreshPorts']) $(id).disabled = next.connected;
  for (const id of ['btnSend', 'chkSendHex', 'selEnding', 'customEndingHex', 'chkLineByLine', 'manualInput', 'repeatInterval', 'btnSendTools', 'btnAppendCrc', 'btnFormatHex', 'btnClearInput', 'btnNewPreset', 'btnImportPresets', 'btnSavePresetModal', 'btnNewWorkflow', 'btnDeleteWorkflow', 'selWorkflow', 'selAddStepPreset', 'inputStepWait', 'btnAddStepToWf', 'selWfMode']) $(id).disabled = busy;
  if (busy) closeSendTools();
  $('btnSend').disabled = busy || !next.connected;
  $('chkRepeat').disabled = !next.connected || (busy && run.kind !== 'repeat');
  $('chkRepeat').checked = run.kind === 'repeat';
  $('btnStartWf').disabled = busy || !next.connected;
  $('btnStepWf').disabled = !next.connected || (busy && (run.kind !== 'workflow' || !run.paused));
  $('btnStopWf').disabled = run.kind !== 'workflow';
  $('btnPauseWf').style.display = run.kind === 'workflow' ? '' : 'none';
  $('btnPauseWf').textContent = run.paused ? '继续' : '暂停';
  $('wfStateLabel').textContent = run.kind === 'workflow' ? (run.paused ? '已暂停' : '执行中') : '空闲';
  for (const id of ['chkDtrPin', 'chkRtsPin']) $(id).disabled = !next.connected || !next.pins;
  $('chkRtsPin').disabled ||= [1, 3].includes(documentState?.profile.flowControl);
  if (next.pins) { $('chkDtrPin').checked = next.pins.dtr; $('chkRtsPin').checked = next.pins.rts; }
  for (const [id, key, name] of [['ledCtsPin', 'cts', 'CTS'], ['ledDsrPin', 'dsr', 'DSR']]) {
    $(id).textContent = `${name}: ${next.pins ? (next.pins[key] ? '●' : '○') : '--'}`;
    $(id).style.color = next.pins?.[key] ? 'var(--rx)' : '';
    $(id).setAttribute('aria-label', next.pinError || `${name} 输入状态`);
  }
  $$('#workflowStepsContainer input, #workflowStepsContainer button').forEach(control => control.disabled = busy);
  $$('#workflowStepsContainer .workflow-step-card').forEach((row, index) => row.classList.toggle('active-step', run.kind === 'workflow' && index === run.step));
  if (wasBusy && !busy) scheduleSave();
}

function parseHex(value) {
  const source = String(value).trim();
  if (!source) return [];
  const clean = source.replace(/0x([0-9a-f]{2})/gi, '$1').replace(/[\s,]/g, '');
  if (!/^[0-9a-f]+$/i.test(clean) || clean.length % 2) throw new Error('HEX 须为完整字节，且只含十六进制字符');
  return clean.match(/.{2}/g).map(value => parseInt(value, 16));
}
function hexString(bytes) { return bytes.map(byte => byte.toString(16).padStart(2, '0').toUpperCase()).join(' '); }
function parseProtocol(log) {
  const cached = cachedLog(log);
  if (cached.protocolVersion !== protocolVersion) {
    cached.protocol = decodeProtocol(log);
    cached.protocolVersion = protocolVersion;
  }
  return cached.protocol;
}
function decodeProtocol(log) {
  if (log.dir === 'SYS') return { title: '系统通知', desc: log.text, matched: true };
  const text = log.text.trim(), hex = log.hex.replaceAll(' ', '');
  for (const { preset, presetHex, matchHex } of protocolRules) {
    if (log.dir === 'TX' && (text === preset.content.trim() || (presetHex && hex === presetHex))) return { title: preset.name, desc: preset.desc, matched: true };
    if (log.dir === 'RX' && preset.rxMatch) {
      if (text.includes(preset.rxMatch) || (matchHex && ` ${log.hex} `.includes(` ${matchHex} `))) return { title: `${preset.name}·应答`, desc: preset.rxDesc || preset.desc, matched: true };
    }
    if (log.dir === 'RX' && (text === preset.content || (presetHex && hex === presetHex))) return { title: `${preset.name}·回显`, desc: preset.desc, matched: true };
  }
  const bytes = parseHex(log.hex);
  const functions = { 1: '读线圈', 2: '读离散输入', 3: '读保持寄存器', 4: '读输入寄存器', 5: '写单线圈', 6: '写单寄存器', 16: '写多寄存器' };
  if (bytes.length >= 5 && bytes[0] <= 247 && functions[bytes[1] & 127]
      && modbusCrc16(bytes.slice(0, -2)) === (bytes.at(-2) | bytes.at(-1) << 8)) {
    return { title: `Modbus·${bytes[1] & 128 ? '异常应答' : functions[bytes[1]]}`, desc: `站号 ${bytes[0]} · ${bytes.length} 字节 · CRC 正确`, matched: true, error: !!(bytes[1] & 128) };
  }
  if (/^AT\+/.test(text)) return { title: 'AT 指令', desc: text.split(/[=\r\n]/)[0], matched: true };
  if (text === 'OK') return { title: '执行确认', desc: '设备返回 OK', matched: true };
  if (/^ERROR|^ERR=/.test(text)) return { title: '设备错误', desc: text, matched: true, error: true };
  if (text.startsWith('+PONG')) return { title: '心跳应答', desc: text, matched: true };
  if (text.startsWith('STATUS=')) return { title: '状态回显', desc: text.slice(7), matched: true };
  if (/^(AA55|55AA)/.test(hex)) return { title: '自定义帧头', desc: `0x${hex.slice(0, 4)} · ${bytes.length} 字节`, matched: false };
  return { title: log.dir === 'TX' ? '发送数据' : '接收数据', desc: `${log.byteCount} 字节 · 未匹配协议`, matched: false };
}

function timeText(log, mode = $('selTimestampFormat').value) {
  if (mode === 'none') return '';
  const cached = cachedLog(log);
  if (!cached.time) {
    const date = new Date(log.timestamp);
    cached.time = clockFormat.format(date);
    cached.milliseconds = `${cached.time}.${String(date.getMilliseconds()).padStart(3, '0')}`;
  }
  return mode === 'sec' ? cached.time : cached.milliseconds;
}
function matchesSearch(log, keyword = $('terminalSearch').value.trim().toLowerCase()) {
  if (!keyword) return true;
  const cached = cachedLog(log);
  if (cached.keyword !== keyword || cached.searchVersion !== protocolVersion) {
    cached.keyword = keyword; cached.searchVersion = protocolVersion;
    cached.match = log.text.toLowerCase().includes(keyword) || log.hex.toLowerCase().includes(keyword);
    if (!cached.match) {
      const protocol = parseProtocol(log);
      cached.match = protocol.title.toLowerCase().includes(keyword) || protocol.desc.toLowerCase().includes(keyword);
    }
  }
  return cached.match;
}
function directionLogs() {
  const direction = $('filterDir').value;
  if (filteredSource !== logs || filteredDirection !== direction) {
    filteredSource = logs; filteredDirection = direction;
    filteredLogs = direction === 'all' ? logs : logs.filter(log => log.dir.toLowerCase() === direction);
  }
  return filteredLogs;
}
function queueRender() {
  if (!renderQueued) { renderQueued = true; setTimeout(() => { renderQueued = false; if (!searchTimer) renderLogs(); }, 50); }
}
function protocolIcon(name) {
  if (!protocolIcons.has(name)) {
    const icon = lucide.createElement(lucide.icons[name]);
    icon.setAttribute('class', 'lucide');
    icon.setAttribute('aria-hidden', 'true');
    protocolIcons.set(name, icon.outerHTML);
  }
  return protocolIcons.get(name);
}
function rawLogText(log) {
  return displayMode === 'hex' ? log.hex || log.text : displayMode === 'both' && log.hex && log.text ? `${log.text}\n${log.hex}` : log.text || log.hex;
}
function protocolDetail(log, protocol) {
  const detail = document.createElement('div');
  detail.className = 'protocol-detail';
  detail.id = `protocol-detail-${log.id}`;
  detail.textContent = protocol.desc || '暂无协议释义';
  return detail;
}
function updateProtocolExpansion(row, log) {
  const expanded = expandedProtocols.has(log), button = row.querySelector('[data-log-action="expand"]');
  if (!button) return;
  row.classList.toggle('protocol-expanded', expanded);
  button.setAttribute('aria-expanded', String(expanded));
  button.setAttribute('aria-label', expanded ? '收起协议释义' : '展开协议释义');
  row.querySelector('.protocol-detail')?.remove();
  if (expanded) {
    const detail = protocolDetail(log, parseProtocol(log));
    row.append(detail);
    button.setAttribute('aria-controls', detail.id);
  } else button.removeAttribute('aria-controls');
}
function setLogExpanded(log, expanded) {
  const row = logView?.nodes.get(log.id);
  if (!row || !row.querySelector('[data-log-action="expand"]')) return;
  hideTooltip();
  if (expanded) expandedProtocols.add(log); else expandedProtocols.delete(log);
  updateProtocolExpansion(row, log);
  logView.follow = false;
  logView.paint();
  if (logView.followEnabled && logView.element.scrollHeight - logView.element.clientHeight - logView.element.scrollTop < 4) logView.follow = true;
}
function createLogRow(log) {
  const system = log.dir === 'SYS';
  const time = timeText(log), protocol = translateProtocol && !system ? parseProtocol(log) : null;
  const raw = rawLogText(log);
  const rawHex = !system && !protocol && log.hex && (displayMode === 'hex' || !log.text);
  const body = system ? escapeHtml(log.text) : protocol ? `<span class="proto-tag ${log.dir.toLowerCase()}">${escapeHtml(protocol.title)}</span>`
    : displayMode === 'both' && log.hex && log.text ? `<span>${escapeHtml(log.text)}</span><span class="log-text-hex">${escapeHtml(log.hex)}</span>` : escapeHtml(raw);
  const row = document.createElement('div');
  row.className = `log-entry ${protocol ? 'proto-mode' : ''} ${system ? 'system-log' : ''} ${time ? '' : 'without-time'}`;
  row.dataset.id = log.id;
  row.innerHTML = `${time ? `<span class="log-time">[${time}]</span>` : ''}<span class="log-dir ${log.dir.toLowerCase()}">${log.dir}</span><span class="log-text${rawHex ? ' log-hex' : ''}">${body}</span>${system ? '' : `<button type="button" class="protocol-action protocol-expand" data-log-action="expand" aria-label="展开协议释义">${protocolIcon('ChevronRight')}</button>`}`;
  if (protocol) {
    row.classList.toggle('protocol-error', !!protocol.error);
    row.classList.toggle('protocol-unmatched', !protocol.matched);
  }
  updateProtocolExpansion(row, log);
  row.classList.toggle('match', matches.includes(log.id));
  row.classList.toggle('current-match', matches[searchIndex] === log.id);
  return row;
}
function renderLogs() {
  clearTimeout(searchTimer); searchTimer = null;
  const visible = directionLogs();
  const keyword = $('terminalSearch').value.trim().toLowerCase();
  const selected = matches[searchIndex];
  matches = keyword ? visible.filter(log => matchesSearch(log, keyword)).map(log => log.id) : [];
  if (selected !== undefined && matches.includes(selected)) searchIndex = matches.indexOf(selected);
  searchIndex = matches.length ? Math.max(0, Math.min(searchIndex, matches.length - 1)) : -1;
  const matchedIds = new Set(matches);
  logView ||= new VirtualLogView($('logStream'), createLogRow);
  logView.setItems(visible, `${displayMode}/${translateProtocol}/${$('selTimestampFormat').value}/${terminalFontSize}/${protocolVersion}`, autoScroll && !keyword);
  for (const [id, row] of logView.nodes) {
    row.classList.toggle('match', matchedIds.has(id));
    row.classList.toggle('current-match', matches[searchIndex] === id);
  }
  $('searchResultCount').textContent = keyword ? `${searchIndex + 1}/${matches.length}` : '';
}
function searchMove(delta) {
  renderLogs();
  if (!matches.length) return;
  searchIndex = (searchIndex + delta + matches.length) % matches.length;
  renderLogs();
  logView.scrollToId(matches[searchIndex]);
}

function renderPresets() {
  hideTooltip();
  protocolVersion++;
  protocolRules = documentState.presets.map(preset => {
    let matchHex = '';
    try { matchHex = hexString(parseHex(preset.rxMatch)); } catch { }
    return { preset, matchHex, presetHex: preset.format === 'hex' ? hexString(parseHex(preset.content)).replaceAll(' ', '') : '' };
  });
  const keyword = $('presetSearchInput').value.toLowerCase();
  $('presetListContainer').innerHTML = documentState.presets.filter(p => `${p.name} ${p.content} ${p.desc}`.toLowerCase().includes(keyword)).map(p => `
    <div class="preset-row" data-id="${escapeHtml(p.id)}" tabindex="0" role="button" aria-label="载入 ${escapeHtml(p.name)}">
      <div class="preset-title"><span>${escapeHtml(p.name)}</span><span class="preset-format-tag">${p.format === 'hex' ? 'HEX' : 'TXT'}</span></div>
      <div class="preset-body">${escapeHtml(p.content)}</div>
      <div class="preset-desc">${escapeHtml(p.desc)}</div>
      <div class="preset-hover-actions">${[['send','send','发送'],['load','corner-down-left','载入'],['edit','pencil','编辑'],['delete','trash-2','删除']].map(([action, icon, label]) => `<button class="icon-button" data-action="${action}" data-tooltip="${label}" aria-label="${label} ${escapeHtml(p.name)}"><i data-lucide="${icon}"></i></button>`).join('')}</div>
    </div>`).join('') || '<div class="empty-state">暂无预设指令</div>';
  const previous = $('selAddStepPreset').value;
  $('selAddStepPreset').replaceChildren(...documentState.presets.map(p => new Option(p.name, p.id)));
  if (documentState.presets.some(p => p.id === previous)) $('selAddStepPreset').value = previous;
  icons();
}

function renderWorkflow() {
  hideTooltip();
  const entries = Object.entries(documentState.workflows);
  if (!documentState.workflows[currentWorkflow]) currentWorkflow = entries[0]?.[0] || '';
  $('selWorkflow').replaceChildren(...entries.map(([id, workflow]) => new Option(workflow.name, id, false, id === currentWorkflow)));
  const workflow = documentState.workflows[currentWorkflow];
  $('workflowStepsContainer').innerHTML = workflow?.steps.map((step, index) => {
    const preset = documentState.presets.find(p => p.id === step.presetId);
    return `<div class="workflow-step-card" data-index="${index}"><span class="step-index">${index + 1}</span><div class="step-name">${escapeHtml(preset?.name || '预设已删除')}</div><div class="step-controls"><div class="step-delay-box"><span>等待:</span><input type="number" min="0" max="86400000" value="${step.wait}" aria-label="步骤 ${index + 1} 等待毫秒"><span>ms</span></div><div class="step-actions-group"><button data-action="up" aria-label="上移"><i data-lucide="chevron-up"></i></button><button data-action="down" aria-label="下移"><i data-lucide="chevron-down"></i></button><button data-action="remove" aria-label="移除"><i data-lucide="x"></i></button></div></div></div>`;
  }).join('') || '<div class="empty-state">暂无步骤</div>';
  icons();
  updateStatus(status);
}

function ensureIdle() { if (status.run.kind !== 'idle') throw new Error('请先停止当前发送任务'); }
function manualRequest() { return { text: $('manualInput').value, hex: $('chkSendHex').checked, ending: $('selEnding').value, customEnding: $('customEndingHex').value, lines: $('chkLineByLine').checked }; }
async function validateInput() {
  const version = ++validationVersion;
  const data = manualRequest();
  updateViewButtons();
  if (!data.text) { $('sendByteCounter').textContent = '0 行 · 0 字节'; $('sendValidationTip').style.display = 'none'; return; }
  try {
    const parts = data.lines ? data.text.replaceAll('\r\n', '\n').split('\n').filter(Boolean) : [data.text];
    const results = await Promise.all(parts.map(text => request('encode', { ...data, text, lines: false })));
    if (version !== validationVersion) return;
    $('sendByteCounter').textContent = `${data.text.split('\n').length} 行 · ${results.reduce((sum, item) => sum + item.byteCount, 0)} 字节`;
    $('sendValidationTip').style.display = 'none';
  } catch (error) {
    if (version !== validationVersion) return;
    $('sendValidationTip').textContent = '格式错误';
    $('sendValidationTip').setAttribute('aria-label', error.message);
    $('sendValidationTip').style.display = '';
    $('sendByteCounter').textContent = '-- 字节';
  }
}
function remember(text) { documentState.history = [text, ...documentState.history.filter(item => item !== text)].slice(0, 50); historyIndex = -1; scheduleSave(); }
async function sendManual() {
  ensureIdle(); const data = manualRequest(); await saveNow(); await request('send', data); remember(data.text);
  if (clearAfterSend && $('manualInput').value === data.text) { $('manualInput').value = ''; validateInput(); }
}
async function workflowStart(stepOnly) { ensureIdle(); await saveNow(); await request('workflow', { id: currentWorkflow, mode: $('selWfMode').value, stepOnly }); }

function editPreset(preset = null) {
  ensureIdle();
  $('modalPresetEditId').value = preset?.id || '';
  $('presetModalHeaderTitle').textContent = preset ? '编辑预设指令' : '新建预设指令';
  for (const [id, field] of [['modalPresetName','name'],['modalPresetContent','content'],['modalPresetDesc','desc'],['modalPresetRxMatch','rxMatch'],['modalPresetRxDesc','rxDesc']]) $(id).value = preset?.[field] || '';
  $('modalPresetFormat').value = preset?.format || 'text';
  showModal('presetModal');
}
function validatePresets(list) {
  if (!Array.isArray(list) || list.length > 1000) throw new Error('预设文件须为不超过 1000 项的数组');
  const ids = new Set();
  return list.map(p => {
    if (!p || typeof p.id !== 'string' || !p.id || p.id.length > 128 || ids.has(p.id) || typeof p.name !== 'string' || !p.name.trim() || p.name.length > 120 || typeof p.content !== 'string' || !p.content || p.content.length > 1048576 || !['text','hex'].includes(p.format)) throw new Error('预设字段无效或 ID 重复');
    ids.add(p.id);
    if (p.format === 'hex') parseHex(p.content);
    for (const field of ['desc','rxMatch','rxDesc']) if (p[field] != null && (typeof p[field] !== 'string' || p[field].length > 4096)) throw new Error('协议释义字段无效');
    return { id: p.id, name: p.name, content: p.content, format: p.format, desc: p.desc || '', rxMatch: p.rxMatch || '', rxDesc: p.rxDesc || '' };
  });
}

function exportData(rows, format, options) {
  const items = rows.map(log => ({ id: log.id, dir: log.dir, text: log.text,
    ...(format === 'json' ? { bytes: parseHex(log.hex), byteCount: log.byteCount, source: log.source } : {}),
    ...(options.time ? { timestamp: log.timestamp } : {}), ...(options.hex ? { hex: log.hex, byteCount: log.byteCount } : {}),
    ...(options.protocol ? { protocol: parseProtocol(log) } : {}) }));
  if (format === 'json') return JSON.stringify(items, null, 2);
  if (format === 'txt') return items.map(item => [item.timestamp, item.dir, item.text, item.hex, item.protocol?.title, item.protocol?.desc].filter(value => value !== undefined).join('\t')).join('\r\n');
  const headers = [...(options.time ? ['时间'] : []), '方向', '数据内容', ...(options.hex ? ['HEX', '字节数'] : []), ...(options.protocol ? ['协议指令','协议释义'] : [])];
  const quote = value => `"${String(value ?? '').replaceAll('"','""')}"`;
  return [headers, ...items.map(item => [...(options.time ? [item.timestamp] : []), item.dir, item.text, ...(options.hex ? [item.hex,item.byteCount] : []), ...(options.protocol ? [item.protocol.title,item.protocol.desc] : [])])].map(row => row.map(quote).join(',')).join('\r\n');
}

function configureEvents() {
  for (const id of ['btnCloseConfirmation','btnCancelConfirmation']) on(id,'click',() => hideModal('confirmationModal'));
  on('btnConfirmAction','click',submitConfirmation);
  on('btnOpenSettings','click',openSettings);
  on('btnToggleTheme','click',() => { theme = theme === 'dark' ? 'light' : 'dark'; applyAppearance(); scheduleSave(); });
  on('btnOpenShortcuts','click',() => showModal('shortcutsModal'));
  on('btnOpenAbout','click',() => showModal('aboutModal'));
  on('btnCheckUpdate','click',() => runUpdate('updateCheck'));
  on('btnDownloadUpdate','click',() => runUpdate('updateDownload'));
  on('btnInstallUpdate','click',() => runUpdate('updateInstall'));
  for (const [id, modal] of [['btnCloseSettingsModal','settingsModal'],['btnCancelSettingsModal','settingsModal'],['btnCloseShortcutsModal','shortcutsModal'],['btnCloseAboutModal','aboutModal']]) on(id,'click',() => hideModal(modal));
  on('btnResetSettingsDefault','click',() => {
    $('cfgUiFontSize').value = '12';
    $('cfgTerminalFontSize').value = '12'; $('cfgClearAfterSend').checked = false;
    $('cfgRememberInput').checked = true; $('cfgHoverTips').checked = true;
    $('cfgAutoScroll').checked = true; $('cfgShowPins').checked = false;
    $('cfgLeftFunction').value = 'presets'; $('cfgRightFunction').value = 'workflow';
    $('cfgLeftDefaultVisible').checked = true; $('cfgRightDefaultVisible').checked = false;
    applyTypography(12, 12); applyHoverTips(true); applyPinVisibility(false);
  });
  for (const id of ['cfgUiFontSize', 'cfgTerminalFontSize']) on(id, 'change', () => applyTypography(Number($('cfgUiFontSize').value), Number($('cfgTerminalFontSize').value)));
  on('cfgHoverTips', 'change', () => applyHoverTips($('cfgHoverTips').checked));
  on('cfgShowPins', 'change', () => applyPinVisibility($('cfgShowPins').checked));
  for (const [id, other] of [['cfgLeftFunction', 'cfgRightFunction'], ['cfgRightFunction', 'cfgLeftFunction']]) on(id, 'change', () => { $(other).value = $(id).value === 'presets' ? 'workflow' : 'presets'; });
  on('btnSaveSettingsModal','click',async () => {
    ensureIdle();
    uiFontSize = Number($('cfgUiFontSize').value);
    terminalFontSize = Number($('cfgTerminalFontSize').value); clearAfterSend = $('cfgClearAfterSend').checked;
    rememberInput = $('cfgRememberInput').checked; hoverTips = $('cfgHoverTips').checked;
    autoScroll = $('cfgAutoScroll').checked; showPins = $('cfgShowPins').checked;
    leftFunction = $('cfgLeftFunction').value;
    const leftVisible = $('cfgLeftDefaultVisible').checked, rightVisible = $('cfgRightDefaultVisible').checked;
    if (leftVisible !== leftDefaultVisible) $('workspaceBody').classList.toggle('hide-left', !leftVisible);
    if (rightVisible !== rightDefaultVisible) $('workspaceBody').classList.toggle('hide-right', !rightVisible);
    leftDefaultVisible = leftVisible; rightDefaultVisible = rightVisible;
    applyTypography(); applyHoverTips(); applyPinVisibility(); applySidebarLayout(); updateViewButtons(); renderLogs(); await saveNow(); hideModal('settingsModal');
  });
  on('btnRefreshPorts','click',async () => {
    try { renderPorts(await request('ports')); }
    catch (error) { updatePortDetail(error.message); throw error; }
  });
  on('selPort','change',() => { updatePortDetail(); scheduleSave(); });
  on('selBaud','change',scheduleSave);
  on('btnConnect','click',async () => {
    $('btnConnect').disabled = true;
    try {
      if (status.connected) await request('disconnect');
      else { await saveNow(); const profile = profileFromControls(); const connected = await request('connect', profile); documentState.profile = profile; updateStatus(connected); }
    } finally { $('btnConnect').disabled = false; }
  });
  on('btnAdvancedPort','click',async () => { await saveNow(); $('advCustomBaud').value = ''; showModal('advancedPortModal'); });
  on('btnCloseAdvModal','click',() => { applyProfile(documentState.profile); hideModal('advancedPortModal'); });
  on('btnSaveAdvModal','click',async () => {
    const baud = $('advCustomBaud').value ? Number($('advCustomBaud').value) : Number($('selBaud').value);
    if (!Number.isInteger(baud) || baud < 1 || baud > 12000000) throw new Error('波特率须为 1 至 12000000 的整数');
    if (![...$('selBaud').options].some(option => option.value === String(baud))) $('selBaud').add(new Option(baud, baud));
    $('selBaud').value = String(baud);
    const previous = documentState.profile;
    documentState.profile = profileFromControls();
    try { await saveNow(); } catch (error) { documentState.profile = previous; applyProfile(previous); throw error; }
    hideModal('advancedPortModal'); validateInput();
  });
  for (const [id, mode] of [['btnDispText','text'],['btnDispHex','hex'],['btnDispBoth','both']]) on(id,'click',() => { displayMode = mode; updateViewButtons(); renderLogs(); scheduleSave(); });
  on('btnToggleProtocol','click',() => { translateProtocol = !translateProtocol; updateViewButtons(); renderLogs(); scheduleSave(); });
  for (const [id, side] of [['btnToggleLeft','left'],['btnToggleRight','right']]) on(id,'click',() => { $('workspaceBody').classList.toggle(`hide-${side}`); updateViewButtons(); scheduleSave(); });
  on('filterDir','change',() => { searchIndex = 0; renderLogs(); });
  on('terminalSearch','input',() => { clearTimeout(searchTimer); searchIndex = 0; matches = []; searchTimer = setTimeout(renderLogs, 150); });
  on('terminalSearch','keydown',event => { if (event.key === 'Enter') { event.preventDefault(); searchMove(event.shiftKey ? -1 : 1); } });
  on('btnSearchPrev','click',() => searchMove(-1));
  on('btnSearchNext','click',() => searchMove(1));
  on('selTimestampFormat','change',() => { renderLogs(); scheduleSave(); });
  on('btnClearTerminal','click',() => {
    if (logs.length) showConfirmation({ title: '清空终端记录', message: '确认清空当前终端的全部记录？清空后无法撤销。', confirmLabel: '清空',
      action: async () => { await request('clearLogs'); logs = []; renderLogs(); } });
  });
  on('logStream','click',event => {
    if (event.target.closest('.protocol-detail') || event.detail > 1) return;
    const row = event.target.closest('.log-entry'), log = logs.find(item => String(item.id) === row?.dataset.id);
    if (!log || log.dir === 'SYS') return;
    const button = event.target.closest('[data-log-action="expand"]');
    if (!button && !window.getSelection().isCollapsed) return;
    logClick = { log, expanded: expandedProtocols.has(log) };
    setLogExpanded(log, !logClick.expanded);
  });
  on('logStream','dblclick',async event => {
    if (event.target.closest('button, .protocol-detail')) return;
    const row = event.target.closest('.log-entry');
    const log = logs.find(item => String(item.id) === row?.dataset.id);
    if (logClick?.log === log) {
      if (expandedProtocols.has(log) !== logClick.expanded) setLogExpanded(log, logClick.expanded);
      logClick = null;
    }
    if (log) { await request('copy', { text: rawLogText(log) }); toast('已复制'); }
  });
  on('manualInput','input',() => { validateInput(); scheduleSave(); });
  for (const id of ['chkSendHex','selEnding','customEndingHex','chkLineByLine','repeatInterval']) on(id,'change',() => { validateInput(); scheduleSave(); });
  on('btnSend','click',sendManual);
  on('btnClearInput','click',() => { ensureIdle(); $('manualInput').value = ''; validateInput(); scheduleSave(); });
  on('btnFormatHex','click',async () => { closeSendTools(true); ensureIdle(); const encoded = await request('encode', { text: $('manualInput').value, hex: true, ending: 'none' }); $('manualInput').value = encoded.hex; $('chkSendHex').checked = true; validateInput(); scheduleSave(); $('manualInput').focus(); });
  on('btnAppendCrc','click',async () => {
    closeSendTools(true); ensureIdle(); const encoded = await request('encode', { text: $('manualInput').value, hex: true, ending: 'none' }); const bytes = parseHex(encoded.hex);
    if (!bytes.length) throw new Error('请输入 HEX 数据');
    const crc = modbusCrc16(bytes);
    $('manualInput').value = hexString([...bytes, crc & 255, crc >> 8]); $('chkSendHex').checked = true; validateInput(); scheduleSave(); $('manualInput').focus();
  });
  on('chkRepeat','change',async () => {
    try {
      if (status.run.kind === 'repeat') await request('stop');
      else { await saveNow(); await request('repeat', { request: manualRequest(), interval: Number($('repeatInterval').value) }); remember($('manualInput').value); }
    } finally { $('chkRepeat').checked = status.run.kind === 'repeat'; }
  });
  on('manualInput','keydown',async event => {
    if (event.isComposing || event.keyCode === 229) return;
    if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey) {
      event.preventDefault();
      if (!event.repeat && !$('btnSend').disabled) await sendManual();
      return;
    }
    if ((event.key === 'ArrowUp' && $('manualInput').selectionStart === 0) || (event.key === 'ArrowDown' && $('manualInput').selectionEnd === $('manualInput').value.length)) {
      if (!documentState.history.length) return;
      event.preventDefault(); historyIndex = Math.max(-1, Math.min(documentState.history.length - 1, historyIndex + (event.key === 'ArrowUp' ? 1 : -1)));
      $('manualInput').value = historyIndex < 0 ? '' : documentState.history[historyIndex]; await validateInput();
    }
  });
  on('presetSearchInput','input',renderPresets);
  on('btnNewPreset','click',() => editPreset());
  on('btnTopClosePresetModal','click',() => hideModal('presetModal'));
  on('btnCancelPresetModal','click',() => hideModal('presetModal'));
  on('btnSavePresetModal','click',async () => {
    ensureIdle();
    const preset = validatePresets([{ id: $('modalPresetEditId').value || crypto.randomUUID(), name: $('modalPresetName').value.trim(), content: $('modalPresetContent').value,
      format: $('modalPresetFormat').value, desc: $('modalPresetDesc').value.trim(), rxMatch: $('modalPresetRxMatch').value.trim(), rxDesc: $('modalPresetRxDesc').value.trim() }])[0];
    const index = documentState.presets.findIndex(p => p.id === preset.id);
    if (index < 0) documentState.presets.push(preset); else documentState.presets[index] = preset;
    await saveNow(); renderPresets(); renderWorkflow(); renderLogs(); hideModal('presetModal');
  });
  on('presetListContainer','click',async event => {
    const row = event.target.closest('.preset-row'), preset = documentState.presets.find(p => p.id === row?.dataset.id);
    if (!preset) return; ensureIdle();
    const action = event.target.closest('button')?.dataset.action || 'load';
    if (action === 'load') { $('manualInput').value = preset.content; $('chkSendHex').checked = preset.format === 'hex'; $('selEnding').value = preset.format === 'hex' ? 'none' : 'crlf'; validateInput(); scheduleSave(); $('manualInput').focus(); }
    if (action === 'edit') editPreset(preset);
    if (action === 'send') { await request('send', { text: preset.content, hex: preset.format === 'hex', ending: preset.format === 'hex' ? 'none' : 'crlf' }); remember(preset.content); }
    if (action === 'delete') showConfirmation({ title: '删除预设', message: `确认删除预设「${preset.name}」？引用它的工作流将需要重新配置。删除后无法撤销。`, confirmLabel: '删除', fallbackFocus: $('presetSearchInput'),
      action: async () => { ensureIdle(); documentState.presets = documentState.presets.filter(p => p.id !== preset.id); await saveNow(); renderPresets(); renderWorkflow(); renderLogs(); } });
  });
  on('presetListContainer','keydown',event => { if (event.key === 'Enter' && event.target.classList.contains('preset-row')) event.target.click(); });
  on('btnExportPresets','click',async () => { const result = await request('export',{ format:'json', fileName:'serial-presets.json', content:JSON.stringify(documentState.presets,null,2) }); if (result.saved) toast('预设已导出'); });
  on('btnImportPresets','click',async () => {
    ensureIdle(); const imported = await request('import'); if (imported === null) return;
    const presets = validatePresets(imported);
    showConfirmation({ title: '替换预设库', message: `确认用 ${presets.length} 条预设替换当前 ${documentState.presets.length} 条预设？覆盖后无法撤销，工作流中缺失的预设引用需要重新配置。`, confirmLabel: '替换',
      action: async () => {
        ensureIdle(); documentState.presets = presets;
        await saveNow(); renderPresets(); renderWorkflow(); renderLogs(); toast('预设已导入');
      } });
  });
  on('selWorkflow','change',() => { currentWorkflow = $('selWorkflow').value; renderWorkflow(); scheduleSave(); });
  on('selWfMode','change',scheduleSave);
  on('btnNewWorkflow','click',() => {
    ensureIdle();
    $('workflowName').value = '新工作流';
    $('workflowName').removeAttribute('aria-invalid');
    $('workflowNameError').textContent = '';
    showModal('workflowModal'); $('workflowName').focus(); $('workflowName').select();
  });
  for (const id of ['btnCloseWorkflowModal','btnCancelWorkflowModal']) on(id,'click',() => hideModal('workflowModal'));
  on('workflowName','input',() => { $('workflowName').removeAttribute('aria-invalid'); $('workflowNameError').textContent = ''; });
  on('workflowForm','submit',async event => {
    event.preventDefault();
    if ($('workflowModal').getAttribute('aria-busy') === 'true') return;
    const name = $('workflowName').value.trim();
    if (!name || name.length > 120) {
      $('workflowNameError').textContent = name ? '名称最多 120 个字符' : '请输入工作流名称';
      $('workflowName').setAttribute('aria-invalid', 'true'); $('workflowName').focus(); return;
    }
    $('workflowNameError').textContent = '';
    $('workflowModal').setAttribute('aria-busy', 'true');
    const controls = [...$('workflowForm').elements];
    controls.forEach(control => control.disabled = true);
    let created = false;
    try {
      ensureIdle();
      currentWorkflow = crypto.randomUUID(); documentState.workflows[currentWorkflow] = { name, steps: [] };
      await saveNow(); renderWorkflow(); created = true;
    } catch (error) { $('workflowNameError').textContent = error.message; }
    finally { $('workflowModal').removeAttribute('aria-busy'); controls.forEach(control => control.disabled = false); }
    if (created) hideModal('workflowModal'); else $('workflowName').focus();
  });
  on('btnDeleteWorkflow','click',() => {
    ensureIdle(); const workflowId = currentWorkflow, workflow = documentState.workflows[workflowId];
    if (workflow) showConfirmation({ title: '删除工作流', message: `确认删除工作流「${workflow.name}」及其全部步骤？删除后无法撤销。`, confirmLabel: '删除',
      action: async () => { ensureIdle(); delete documentState.workflows[workflowId]; if (currentWorkflow === workflowId) currentWorkflow = ''; await saveNow(); renderWorkflow(); } });
  });
  on('btnAddStepToWf','click',async () => {
    ensureIdle(); const workflow = documentState.workflows[currentWorkflow];
    if (!workflow || !$('selAddStepPreset').value) throw new Error('请先创建工作流并选择预设');
    const wait = Number($('inputStepWait').value);
    if (!Number.isInteger(wait) || wait < 0 || wait > 86400000) throw new Error('等待时间无效');
    workflow.steps.push({ presetId: $('selAddStepPreset').value, wait }); await saveNow(); renderWorkflow();
  });
  on('workflowStepsContainer','click',async event => {
    const button = event.target.closest('button'); if (!button) return; ensureIdle();
    const index = Number(button.closest('[data-index]').dataset.index), steps = documentState.workflows[currentWorkflow].steps;
    if (button.dataset.action === 'remove') steps.splice(index,1);
    else { const target = index + (button.dataset.action === 'up' ? -1 : 1); if (target >= 0 && target < steps.length) [steps[index],steps[target]] = [steps[target],steps[index]]; }
    await saveNow(); renderWorkflow();
  });
  on('workflowStepsContainer','change',async event => { ensureIdle(); const value = Number(event.target.value); if (!Number.isInteger(value) || value < 0 || value > 86400000) { renderWorkflow(); throw new Error('等待时间无效'); } documentState.workflows[currentWorkflow].steps[Number(event.target.closest('[data-index]').dataset.index)].wait = value; await saveNow(); });
  on('btnStartWf','click',() => workflowStart(false));
  on('btnStepWf','click',() => status.run.kind === 'idle' ? workflowStart(true) : request('step'));
  on('btnPauseWf','click',() => request('pause', { paused: !status.run.paused }));
  on('btnStopWf','click',() => request('stop'));
  on('btnResetStat','click',() => request('resetStats'));
  for (const id of ['chkDtrPin','chkRtsPin']) on(id,'change',async () => { try { await request('pins',{ dtr:$('chkDtrPin').checked,rts:$('chkRtsPin').checked }); } catch(error) { updateStatus(status); throw error; } });
  on('btnExportLog','click',() => {
    const filtered = directionLogs().filter(log => matchesSearch(log));
    $('exportTotalCount').textContent = `${logs.length} 条`; $('exportScopeAllCount').textContent = logs.length; $('exportScopeFilteredCount').textContent = filtered.length;
    $('exportBreakdown').textContent = ['TX','RX','SYS'].map(dir => `${dir}: ${logs.filter(log => log.dir === dir).length}`).join(' · ');
    $('exportTimeRange').textContent = logs.length ? `${timeText(logs[0],'sec')} - ${timeText(logs.at(-1),'sec')}` : '';
    $('exportFileName').value = `serial-log-${new Date().toISOString().replace(/[:.]/g,'-')}.${$('exportFormat').value}`;
    showModal('exportModal');
  });
  on('exportFormat','change',() => { $('exportFileName').value = $('exportFileName').value.replace(/\.[^.]+$/, '') + '.' + $('exportFormat').value; });
  for (const id of ['btnCloseExportModal','btnCancelExportModal']) on(id,'click',() => hideModal('exportModal'));
  on('btnConfirmExport','click',async () => {
    const rows = document.querySelector('input[name="exportScope"]:checked').value === 'filtered' ? directionLogs().filter(log => matchesSearch(log)) : logs;
    const format = $('exportFormat').value;
    const content = exportData(rows,format,{ time:$('chkExportTime').checked,hex:$('chkExportHex').checked,protocol:$('chkExportProto').checked });
    const result = await request('export',{ format,content,fileName:$('exportFileName').value }); if (result.saved) { hideModal('exportModal'); toast('日志已导出'); }
  });
  let dragging = false, startY = 0, startHeight = 0;
  const resizeSendBox = height => {
    const growth = Math.max(0, Math.min(60, innerHeight * .12 - 84));
    const base = Math.max(135, Math.min(400, height - growth));
    document.documentElement.style.setProperty('--send-box-h', `${base}px`);
  };
  on('centerSplitter','pointerdown',event => { dragging = true; startY = event.clientY; startHeight = $('sendBox').offsetHeight; event.target.setPointerCapture(event.pointerId); });
  on('centerSplitter','pointermove',event => { if (dragging) resizeSendBox(Math.min($('workspaceBody').clientHeight - 100, startHeight + startY - event.clientY)); });
  on('centerSplitter','pointerup',() => { dragging = false; scheduleSave(); });
  on('centerSplitter','dblclick',() => {
    const root = document.documentElement;
    const base = parseFloat(getComputedStyle(root).getPropertyValue('--send-box-h')) || 135;
    root.style.setProperty('--send-box-h', base > 170 ? '135px' : '240px');
    scheduleSave();
  });
  $('centerSplitter').tabIndex = 0;
  on('centerSplitter','keydown',event => { if (event.key === 'ArrowUp' || event.key === 'ArrowDown') { event.preventDefault(); resizeSendBox($('sendBox').offsetHeight + (event.key === 'ArrowUp' ? 10 : -10)); scheduleSave(); } });
  document.addEventListener('keydown',safe(async event => {
    const modal = document.querySelector('.modal-overlay.show');
    if (event.key === 'Escape') { if (modal) hideModal(modal.id); else if (!$('sendToolsMenu').hidden) closeSendTools(true); return; }
    if (event.key === 'Tab' && modal) {
      const controls = [...modal.querySelectorAll('button,input:not([type="hidden"]),select,textarea')].filter(control => !control.disabled && control.getClientRects().length);
      if (!controls.length) event.preventDefault();
      if (event.shiftKey && document.activeElement === controls[0]) { event.preventDefault(); controls.at(-1).focus(); }
      if (!event.shiftKey && document.activeElement === controls.at(-1)) { event.preventDefault(); controls[0].focus(); }
    }
    if (modal) return;
    if (event.ctrlKey && event.key === ',') { event.preventDefault(); openSettings(); return; }
    if (event.altKey && event.code === 'Slash') { event.preventDefault(); $('btnOpenShortcuts').click(); return; }
    const typing = /INPUT|TEXTAREA|SELECT/.test(event.target.tagName);
    if (event.key.toLowerCase() === 'p' && (!typing || event.altKey) && !event.ctrlKey && !event.metaKey) { event.preventDefault(); $('btnToggleProtocol').click(); }
    if (event.ctrlKey && event.key.toLowerCase() === 'f') { event.preventDefault(); $('terminalSearch').focus(); }
  }));
}

async function initialize() {
  for (const [id, action] of [['btnPinWindow','pin'],['btnWinMin','minimize'],['btnWinMax','maximize'],['btnWinClose','close']]) {
    on(id,'click',async () => {
      if (action === 'close') await saveNow();
      updateWindowState(await request('window', { action }));
    });
  }
  icons();
  for (const [id,icon,label] of [['btnRefreshPorts','refresh-cw','刷新串口'],['btnExportPresets','download','导出预设'],['btnImportPresets','upload','导入预设'],['btnNewPreset','plus','新建预设'],['btnSearchPrev','chevron-up','上一个匹配'],['btnSearchNext','chevron-down','下一个匹配'],['btnClearTerminal','eraser','清空终端'],['btnExportLog','download','导出日志'],['btnClearInput','eraser','清空输入']]) iconButton(id,icon,label);
  $('btnToggleProtocol').querySelector('kbd')?.remove();
  const initial = await request('init');
  updateWindowState(initial.window);
  if (initial.update) renderUpdate(initial.update);
  if (initial.version) { $('titlebarVersion').textContent = `v${initial.version}`; $('aboutVersion').textContent = `v${initial.version}`; }
  documentState = initial.document; documentState.history ||= [];
  savedDocument = structuredClone(documentState);
  appendLogs(initial.logs); applyProfile(documentState.profile); renderPorts(initial.ports,true); restoreUi();
  initialized = true;
  configureEvents(); configureTooltips(); configureSendTools(); renderPresets(); renderWorkflow(); updateStatus(initial.status); renderLogs(); validateInput(); icons();
  document.fonts.ready.then(() => { logView?.invalidate(); logView?.schedule(); });
  const resize = new ResizeObserver(() => {
    const root = document.documentElement;
    root.style.setProperty('--send-min-h', `${document.querySelector('.send-options-row').offsetHeight + document.querySelector('.send-bottom-bar').offsetHeight + 40}px`);
    root.style.setProperty('--terminal-bar-h', `${document.querySelector('.terminal-bar').offsetHeight}px`);
  });
  for (const selector of ['.send-options-row', '.send-bottom-bar', '.terminal-bar']) resize.observe(document.querySelector(selector));
  if (initial.portError) { updatePortDetail(initial.portError); toast('读取串口列表失败：' + initial.portError); }
  document.body.dataset.ready = 'true';
  setInterval(async () => {
    if (status.connected || refreshPending || document.activeElement === $('selPort')) return;
    refreshPending = true;
    try { const list = await request('ports'); if (JSON.stringify(list) !== JSON.stringify(ports)) renderPorts(list); else updatePortDetail(); }
    catch (error) { updatePortDetail(error.message); }
    finally { refreshPending = false; }
  }, 2000);
}

window.chrome?.webview.addEventListener('message',event => {
  const message = event.data;
  if (message.id && pending.has(message.id)) {
    const item = pending.get(message.id); clearTimeout(item.timeout); pending.delete(message.id);
    message.ok ? item.resolve(message.result) : item.reject(new Error(message.error || '操作失败'));
  } else if (message.event === 'log' || message.event === 'logs') { appendLogs(message.event === 'logs' ? message.data : [message.data]); if (initialized) queueRender(); }
  else if (message.event === 'status' && initialized) updateStatus(message.data);
  else if (message.event === 'window') updateWindowState(message.data);
  else if (message.event === 'update') renderUpdate(message.data);
});
initialize().catch(error => { toast(error.message); document.body.dataset.error = error.message; });
