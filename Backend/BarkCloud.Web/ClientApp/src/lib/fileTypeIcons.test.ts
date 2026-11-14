import { describe, expect, it } from 'vitest';
import { fileTypeIconKey } from './fileTypeIcons';
import { findFolderIcon } from './folderIcons';

describe('fileTypeIconKey', () => {
  it('выбирает иконку по расширению без учёта регистра', () => {
    expect(fileTypeIconKey({ ext: 'XLSX', iconKind: 'doc', kind: 'document' })).toBe('spreadsheet');
    expect(fileTypeIconKey({ ext: 'iso', iconKind: 'doc', kind: 'other' })).toBe('disc');
    expect(fileTypeIconKey({ ext: 'SRT', iconKind: 'doc', kind: 'other' })).toBe('subtitles');
  });

  it('падает на iconKind, затем на вид медиа, затем на общий файл', () => {
    expect(fileTypeIconKey({ ext: 'weird', iconKind: 'zip', kind: 'other' })).toBe('file-archive');
    expect(fileTypeIconKey({ ext: 'FILE', iconKind: 'doc', kind: 'audio' })).toBe('music');
    expect(fileTypeIconKey({ ext: 'FILE', iconKind: 'doc', kind: 'other' })).toBe('file');
    expect(fileTypeIconKey(null)).toBe('file');
  });

  it('каждый возможный ключ есть в паке', () => {
    const exts = 'jpg mp4 mp3 pdf xlsx pptx zip iso srt ts sh sql ttf exe apk epub psd pem torrent'.split(' ');
    for (const ext of exts) {
      const key = fileTypeIconKey({ ext, iconKind: '', kind: 'other' });
      expect(findFolderIcon(key), `${ext} → ${key}`).not.toBeNull();
    }
    expect(findFolderIcon('file')).not.toBeNull();
  });
});
