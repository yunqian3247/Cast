'use strict';

class VirtualLogView {
  constructor(element, createRow) {
    this.element = element;
    this.createRow = createRow;
    this.items = [];
    this.offsets = [0];
    this.heights = new WeakMap();
    this.nodes = new Map();
    this.follow = true;
    this.lastScroll = 0;
    this.top = document.createElement('div');
    this.bottom = document.createElement('div');
    this.top.className = this.bottom.className = 'log-spacer';
    this.top.setAttribute('aria-hidden', 'true');
    this.bottom.setAttribute('aria-hidden', 'true');
    element.replaceChildren(this.top, this.bottom);
    element.addEventListener('scroll', () => {
      if (Math.abs(element.scrollTop - this.lastScroll) > 1) {
        this.follow = element.scrollHeight - element.clientHeight - element.scrollTop < 4;
      }
      this.schedule();
    });
    new ResizeObserver(() => {
      const width = element.clientWidth;
      if (width !== this.width) { this.width = width; this.invalidate(); }
      this.schedule();
    }).observe(element);
  }

  schedule() {
    if (this.scheduled) return;
    this.scheduled = true;
    requestAnimationFrame(() => { this.scheduled = false; this.paint(); });
  }

  indexAt(offset) {
    let low = 0, high = this.items.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if (this.offsets[middle + 1] <= offset) low = middle + 1; else high = middle;
    }
    return Math.min(low, Math.max(0, this.items.length - 1));
  }

  anchor() {
    const index = this.indexAt(Math.max(0, this.element.scrollTop - 6));
    return { id: this.items[index]?.id, offset: this.element.scrollTop - this.offsets[index] };
  }

  rebuildOffsets() {
    this.offsets = new Float64Array(this.items.length + 1);
    for (let i = 0; i < this.items.length; i++) this.offsets[i + 1] = this.offsets[i] + (this.heights.get(this.items[i]) || 20);
  }

  invalidate(preserveAnchor = true) {
    const anchor = this.anchor();
    this.heights = new WeakMap();
    for (const node of this.nodes.values()) node.remove();
    this.nodes.clear();
    this.rebuildOffsets();
    const index = this.items.findIndex(item => item.id === anchor.id);
    if (preserveAnchor && index >= 0) this.pendingScroll = this.offsets[index] + anchor.offset;
  }

  setItems(items, revision, followEnabled) {
    const anchor = this.externalAnchor || this.anchor(); this.externalAnchor = null;
    const changed = this.items !== items || this.itemCount !== items.length || this.lastItemId !== items.at(-1)?.id;
    const layoutChanged = this.revision !== revision;
    this.items = items;
    this.itemCount = items.length; this.lastItemId = items.at(-1)?.id;
    if (layoutChanged) { this.revision = revision; this.invalidate(false); }
    else if (changed) this.rebuildOffsets();
    if (followEnabled && !this.followEnabled) this.follow = true;
    this.followEnabled = followEnabled;
    if (!(this.follow && followEnabled) && (changed || layoutChanged)) {
      const index = items.findIndex(item => item.id === anchor.id);
      this.pendingScroll = index >= 0 ? this.offsets[index] + anchor.offset : 0;
    }
    this.paint();
  }

  scrollToId(id) {
    const index = this.items.findIndex(item => item.id === id);
    if (index < 0) return;
    this.follow = false;
    this.pendingScroll = Math.max(0, this.offsets[index] - this.element.clientHeight / 2);
    this.paint();
    this.nodes.get(id)?.scrollIntoView({ block: 'center' });
    this.lastScroll = this.element.scrollTop;
  }

  paint() {
    const element = this.element;
    if (!element.clientHeight) return;
    if (!this.items.length) {
      this.follow = true;
      this.pendingScroll = null;
      this.lastScroll = 0;
      for (const node of this.nodes.values()) node.remove();
      this.nodes.clear();
      this.top.style.height = this.bottom.style.height = '0px';
      if (!this.empty) { this.empty = document.createElement('div'); this.empty.className = 'empty-state'; this.empty.textContent = '暂无通信数据'; element.insertBefore(this.empty, this.bottom); }
      return;
    }
    this.empty?.remove(); this.empty = null;
    // Correct estimated heights while keeping the visible record anchored.
    for (let pass = 0; pass < 3; pass++) {
      const total = this.offsets[this.items.length];
      const scroll = this.follow && this.followEnabled ? Math.max(0, total + 12 - element.clientHeight) : (this.pendingScroll ?? element.scrollTop);
      const start = this.indexAt(Math.max(0, scroll - 200));
      const end = Math.min(this.items.length, this.indexAt(scroll + element.clientHeight + 200) + 1);
      const anchorIndex = this.indexAt(scroll), anchorOffset = scroll - this.offsets[anchorIndex];
      const active = new Set(this.items.slice(start, end).map(item => item.id));
      for (const [id, node] of this.nodes) if (!active.has(id)) { node.remove(); this.nodes.delete(id); }
      let previous = this.top;
      for (let i = start; i < end; i++) {
        const item = this.items[i];
        let node = this.nodes.get(item.id);
        if (!node) { node = this.createRow(item); this.nodes.set(item.id, node); }
        if (previous.nextSibling !== node) previous.after(node);
        previous = node;
      }
      let changed = false;
      for (let i = start; i < end; i++) {
        const height = this.nodes.get(this.items[i].id).getBoundingClientRect().height;
        if (height > 0 && height !== this.heights.get(this.items[i])) { this.heights.set(this.items[i], height); changed = true; }
      }
      if (changed) this.rebuildOffsets();
      this.top.style.height = `${this.offsets[start]}px`;
      this.bottom.style.height = `${this.offsets[this.items.length] - this.offsets[end]}px`;
      const target = this.follow && this.followEnabled ? element.scrollHeight : this.offsets[anchorIndex] + anchorOffset;
      element.scrollTop = target;
      this.lastScroll = element.scrollTop;
      this.pendingScroll = null;
      if (!changed) break;
      if (pass === 2) this.schedule();
    }
  }
}
