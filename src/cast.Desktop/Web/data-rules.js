'use strict';
globalThis.CastRules = Object.freeze({
  maxDocumentChars: 64 * 1024 * 1024,
  parseHex(value) {
    const source = String(value ?? '');
    const bytes = []; let high = -1, position = -1;
    for (let i = 0; i < source.length; i++) {
      const c = source[i];
      if (/[\s,\u0085]/u.test(c)) continue;
      if (c === '0' && /[xX]/.test(source[i + 1] || '')) {
        if (high >= 0 || !/[0-9a-f]/i.test(source[i + 2] || '')) throw new Error(`HEX 前缀无效（位置 ${i}）`);
        i++; continue;
      }
      if (!/[0-9a-f]/i.test(c)) throw new Error(`HEX 含非法字符 '${c}'（位置 ${i}）`);
      const nibble = parseInt(c, 16);
      if (high < 0) { high = nibble; position = i; }
      else { bytes.push(high * 16 + nibble); high = -1; }
    }
    if (high >= 0) throw new Error(`HEX 须为完整字节（位置 ${position}）`);
    return bytes;
  },
  csvCell(value, safe = true) {
    let text = String(value ?? '');
    if (safe && /^[\s\u0000-\u001f]*[=+@-]/u.test(text)) text = "'" + text;
    return `"${text.replaceAll('"', '""')}"`;
  }
});
