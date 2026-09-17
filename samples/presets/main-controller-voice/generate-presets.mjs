import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = path.dirname(fileURLToPath(import.meta.url));
const protocolPath = path.join(directory, 'protocol-source.json');
const protocol = JSON.parse(fs.readFileSync(protocolPath, 'utf8'));
const parseBytes = text => {
  if (!/^[0-9A-F]{2}(?: [0-9A-F]{2})*$/.test(text)) throw new Error('Invalid protocol bytes');
  return text.split(' ').map(byte => Number.parseInt(byte, 16));
};
const formatBytes = bytes => bytes.map(byte => byte.toString(16).toUpperCase().padStart(2, '0')).join(' ');
const prefix = parseBytes(`${protocol.frame.header} ${protocol.frame.cmd} ${protocol.frame.data0}`);
const ending = parseBytes(protocol.frame.end);
if (protocol.frame.check !== 'sum8(header,cmd,data0,data1)') throw new Error('Unsupported checksum rule');
const codes = new Set();
const presets = protocol.commands.map(command => {
  if (!/^[0-9A-F]{2}$/.test(command.data1) || codes.has(command.data1)) throw new Error('Invalid or duplicate command code');
  codes.add(command.data1);
  const bytes = [...prefix, ...parseBytes(command.data1)];
  const checksum = bytes.reduce((sum, byte) => sum + byte, 0) & 0xFF;
  return {
    id: `main-voice-81-00-${command.data1.toLowerCase()}`,
    name: `${command.name} [${command.data1}]`,
    content: formatBytes([...bytes, checksum, ...ending]),
    format: 'hex',
    desc: command.speech,
    rxMatch: '',
    rxDesc: '',
  };
});
const verified = protocol.verifiedExample;
if (presets.find(preset => preset.id === `main-voice-81-00-${verified.data1.toLowerCase()}`)?.content !== verified.frame) {
  throw new Error('Generated frame differs from the verified example');
}
for (const name of ['main-controller-voice-presets.json', 'source.json']) {
  fs.writeFileSync(path.join(directory, name), JSON.stringify(presets, null, 2) + '\n');
}
console.log(`Generated ${presets.length} complete HEX presets. Verified example: ${verified.frame}`);
