export const MAX_TEXT_FILE_BYTES = 10 * 1024 * 1024;
export type TextFileKind = 'text' | 'json' | 'markdown';
export type TextEncoding = 'auto' | 'utf-8' | 'utf-16le' | 'utf-16be' | 'windows-1251';

const TEXT_EXTS = new Set([
  'txt', 'md', 'markdown', 'log', 'csv', 'tsv', 'json', 'xml', 'yaml', 'yml', 'ini', 'conf', 'cfg', 'env',
  'html', 'htm', 'css', 'js', 'jsx', 'ts', 'tsx', 'cs', 'py', 'java', 'go', 'rs', 'rb', 'php',
  'c', 'cpp', 'h', 'hpp', 'sh', 'bat', 'ps1', 'sql', 'svg',
  'toml', 'properties', 'config', 'jsonc', 'jsonl', 'ndjson', 'mdown', 'mkd', 'kt', 'kts', 'swift',
  'dart', 'lua', 'r', 'fs', 'fsx', 'pl', 'pm', 'groovy', 'gradle', 'ex', 'exs', 'vue', 'svelte', 'scss', 'sass', 'less',
]);
const TEXT_NAMES = new Set(['dockerfile', 'makefile', '.gitignore', '.gitattributes', '.editorconfig', '.dockerignore']);
const MARKDOWN_EXTS = new Set(['md', 'markdown', 'mdown', 'mkd']);

export function getTextFileKind(name: string): TextFileKind | null {
  const lower = name.toLowerCase();
  if (TEXT_NAMES.has(lower) || lower === '.env' || lower.startsWith('.env.')) return 'text';
  const ext = lower.includes('.') ? lower.slice(lower.lastIndexOf('.') + 1) : '';
  if (!TEXT_EXTS.has(ext)) return null;
  return ext === 'json' ? 'json' : MARKDOWN_EXTS.has(ext) ? 'markdown' : 'text';
}

export type TextFileSource =
  | { kind: 'owned' | 'shared'; fileId: string }
  | { kind: 'public'; token: string }
  | { kind: 'publicFolder'; token: string; dir: string; fileId: string };

export function textContentUrl(source: TextFileSource): string {
  switch (source.kind) {
    case 'owned': return '/api/files/text?id=' + encodeURIComponent(source.fileId);
    case 'shared': return '/api/shared/text?fileId=' + encodeURIComponent(source.fileId);
    case 'public': return `/api/public/shares/${encodeURIComponent(source.token)}/text`;
    case 'publicFolder': return `/api/public/folder-shares/${encodeURIComponent(source.token)}/text?dir=${encodeURIComponent(source.dir)}&fileId=${encodeURIComponent(source.fileId)}`;
  }
}

export function decodeTextFile(bytes: ArrayBuffer, requested: TextEncoding = 'auto'): { text: string; encoding: Exclude<TextEncoding, 'auto'> } {
  const prefix = new Uint8Array(bytes, 0, Math.min(bytes.byteLength, 3));
  const encoding = requested === 'auto'
    ? prefix[0] === 255 && prefix[1] === 254 ? 'utf-16le' : prefix[0] === 254 && prefix[1] === 255 ? 'utf-16be' : 'utf-8'
    : requested;
  let text: string;
  try { text = new TextDecoder(encoding, { fatal: true }).decode(bytes); }
  catch { throw new Error('Не удалось прочитать текст. Выберите другую кодировку.'); }
  let controls = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code === 0) throw new Error('Файл содержит бинарные данные. Проверьте кодировку или скачайте файл.');
    if (code < 32 && code !== 9 && code !== 10 && code !== 13 && code !== 12) controls++;
  }
  if (controls > text.length * 0.01) throw new Error('Файл содержит бинарные данные. Проверьте кодировку или скачайте файл.');
  return { text, encoding };
}
