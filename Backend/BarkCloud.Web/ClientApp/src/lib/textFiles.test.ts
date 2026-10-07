import { describe, expect, it } from 'vitest';
import { decodeTextFile, getTextFileKind, textContentUrl } from './textFiles';

describe('text files', () => {
  it.each(['note.TXT', 'config.yml', 'data.json', 'page.svg', 'index.html', 'Dockerfile', 'Makefile', '.env', '.env.local', '.gitignore', 'App.swift', 'app.svelte', 'items.ndjson'])('recognizes %s', (name) => {
    expect(getTextFileKind(name)).not.toBeNull();
  });
  it.each(['report.doc', 'report.docx', 'report.pdf', 'sheet.xlsx', 'archive.zip', 'photo.png', 'unknown', 'note.txt.exe'])('excludes %s', (name) => {
    expect(getTextFileKind(name)).toBeNull();
  });
  it('distinguishes JSON and Markdown from JSON-like source', () => {
    expect(getTextFileKind('data.JSON')).toBe('json');
    expect(getTextFileKind('note.mdown')).toBe('markdown');
    expect(getTextFileKind('data.jsonc')).toBe('text');
  });
  it('decodes UTF-8, BOM and UTF-16 both byte orders', () => {
    expect(decodeTextFile(new TextEncoder().encode('Привет').buffer).text).toBe('Привет');
    expect(decodeTextFile(new Uint8Array([239, 187, 191, 65]).buffer).text).toBe('A');
    expect(decodeTextFile(new Uint8Array([255, 254, 65, 0]).buffer)).toEqual({ text: 'A', encoding: 'utf-16le' });
    expect(decodeTextFile(new Uint8Array([254, 255, 0, 65]).buffer)).toEqual({ text: 'A', encoding: 'utf-16be' });
    expect(decodeTextFile(new ArrayBuffer(0)).text).toBe('');
  });
  it('supports manual Windows-1251 and rejects broken encoding or binary data', () => {
    const bytes = new Uint8Array([207, 240, 232, 226, 229, 242]).buffer;
    expect(() => decodeTextFile(bytes)).toThrow('кодировк');
    expect(decodeTextFile(bytes, 'windows-1251').text).toBe('Привет');
    expect(() => decodeTextFile(new Uint8Array([65, 0, 66]).buffer)).toThrow('бинарн');
    expect(() => decodeTextFile(new Uint8Array([65, 1, 2, 3]).buffer)).toThrow('бинарн');
  });
  it('builds same-origin routes for each access context', () => {
    expect(textContentUrl({ kind: 'owned', fileId: 'id' })).toBe('/api/files/text?id=id');
    expect(textContentUrl({ kind: 'shared', fileId: 'id' })).toBe('/api/shared/text?fileId=id');
    expect(textContentUrl({ kind: 'public', token: 'a/b' })).toBe('/api/public/shares/a%2Fb/text');
    expect(textContentUrl({ kind: 'publicFolder', token: 't', dir: 'd', fileId: 'f' })).toBe('/api/public/folder-shares/t/text?dir=d&fileId=f');
  });
});
