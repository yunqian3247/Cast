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
  return probe.snapshot();
})();
