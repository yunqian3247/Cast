(() => {
  const probe = window.perfProbe = { renders: [], longTasks: [], frames: [], lastFrame: 0 };
  const originalRender = renderLogs;
  renderLogs = function () {
    const start = performance.now();
    originalRender();
    probe.renders.push(performance.now() - start);
  };
  new PerformanceObserver(list => probe.longTasks.push(...list.getEntries().map(entry => entry.duration))).observe({ type: 'longtask', buffered: false });
  function frame(now) {
    if (probe.lastFrame) probe.frames.push(now - probe.lastFrame);
    probe.lastFrame = now;
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
  const summarize = values => {
    const sorted = [...values].sort((a, b) => a - b);
    return { count: sorted.length, median: sorted[Math.floor(sorted.length / 2)] || 0,
      p95: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * .95))] || 0,
      max: sorted.at(-1) || 0, total: sorted.reduce((a, b) => a + b, 0) };
  };
  probe.reset = () => { probe.renders = []; probe.longTasks = []; probe.frames = []; probe.lastFrame = 0; };
  probe.snapshot = () => ({ rows: document.querySelectorAll('.log-entry').length, retained: logs.length,
    nodes: document.querySelectorAll('*').length, rendersMs: summarize(probe.renders), longTasksMs: summarize(probe.longTasks),
    frameGapsMs: summarize(probe.frames), heap: performance.memory ? { used: performance.memory.usedJSHeapSize, total: performance.memory.totalJSHeapSize } : null });
  probe.configure = (mode, protocol, keyword = '') => {
    displayMode = mode; translateProtocol = protocol; $('terminalSearch').value = keyword;
    updateViewButtons(); renderLogs();
  };
  probe.redraw = async (mode, protocol) => {
    const start = performance.now();
    probe.configure(mode, protocol);
    probe.lastModeChangeMs = performance.now() - start;
    const durations = [];
    for (let i = 0; i < 3; i++) {
      await new Promise(resolve => requestAnimationFrame(resolve));
      const start = performance.now(); renderLogs(); durations.push(performance.now() - start);
    }
    return summarize(durations);
  };
  probe.search = async () => {
    const durations = [];
    probe.searchWorkMs = [];
    for (const keyword of ['0', '00', '00 ']) {
      await new Promise(resolve => requestAnimationFrame(resolve));
      const start = performance.now();
      $('terminalSearch').value = keyword; $('terminalSearch').dispatchEvent(new Event('input'));
      while (searchTimer) await new Promise(resolve => setTimeout(resolve, 10));
      await new Promise(resolve => requestAnimationFrame(resolve));
      durations.push(performance.now() - start);
      probe.searchWorkMs.push(probe.renders.at(-1));
    }
    return summarize(durations);
  };
  probe.settings = () => new Promise(resolve => {
    const start = performance.now(); $('btnOpenSettings').click();
    requestAnimationFrame(() => requestAnimationFrame(() => { const elapsed = performance.now() - start; hideModal('settingsModal'); resolve(elapsed); }));
  });
  probe.clear = () => { const start = performance.now(); logs = []; renderLogs(); return performance.now() - start; };
  // Run the former algorithm and the current production function in this WebView,
  // using identical retained records and batches. This isolates append cost from
  // bridge timing, font loading, rendering and process-memory fluctuations.
  probe.appendComparison = () => {
    const saved = { logs, logTextBytes, logBudgetSource, logRevision, anchor: logView.externalAnchor };
    const seed = logs.slice(-10000), batch = seed.slice(-5), repetitions = 1000;
    if (seed.length !== 10000) throw new Error('Append comparison requires 10000 retained records');
    let legacy = seed.slice(), legacyBytes = 0;
    const former = () => {
      const combined = legacy.concat(batch);
      let start = combined.length, bytes = 0;
      while (start > 0 && combined.length - start < maxLogCount) {
        const log = combined[start - 1], size = 2 * (log.text.length + log.hex.length);
        if (bytes + size > MAX_LOG_TEXT_BYTES && start < combined.length) break;
        bytes += size; start--;
      }
      legacy = combined.slice(start); legacyBytes = bytes;
    };
    try {
      logs = seed.slice(); logBudgetSource = logs;
      logTextBytes = logs.reduce((sum,log)=>sum+2*(log.text.length+log.hex.length),0);
      const begin = performance.now();
      for (let i=0;i<repetitions;i++) former();
      const formerMs = performance.now()-begin, currentBegin = performance.now();
      for (let i=0;i<repetitions;i++) appendLogs(batch);
      const currentMs = performance.now()-currentBegin;
      const equal = logs.length===legacy.length && logTextBytes===legacyBytes && logs.every((log,i)=>log===legacy[i]);
      if (!equal) throw new Error('Append implementations produced different retained data');
      return { repetitions, batchSize:batch.length, retained:seed.length, formerMs, currentMs, speedup:formerMs/currentMs, equal };
    } finally {
      logs=saved.logs;logTextBytes=saved.logTextBytes;logBudgetSource=saved.logBudgetSource;logRevision=saved.logRevision;logView.externalAnchor=saved.anchor;
    }
  };
  return probe.snapshot();
})();
