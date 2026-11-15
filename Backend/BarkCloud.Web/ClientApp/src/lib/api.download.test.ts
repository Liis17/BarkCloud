import { afterEach, describe, expect, it, vi } from 'vitest';
import { downloadArchive, downloadFile } from './api';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function stubJson(body: unknown) {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })));
}

// Safari блокирует window.open, вызванный после await: скачивание должно идти без всплывающего окна.
describe('download without popup', () => {
  it('downloadFile clicks an anchor with the temp url instead of window.open', async () => {
    stubJson({ urls: { f1: 'https://cloud.test/web/download/t1' } });
    const open = vi.spyOn(window, 'open');
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      expect(this.href).toBe('https://cloud.test/web/download/t1');
    });

    await downloadFile('f1');

    expect(click).toHaveBeenCalledOnce();
    expect(open).not.toHaveBeenCalled();
    expect(document.querySelector('a[download]')).toBeNull();
  });

  it('downloadArchive clicks an anchor with the archive url instead of window.open', async () => {
    stubJson({ url: 'https://cloud.test/web/download/a1', fileName: 'x.zip' });
    const open = vi.spyOn(window, 'open');
    const hrefs: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      hrefs.push(this.href);
    });

    await downloadArchive({ fileIds: ['f1'] });

    expect(hrefs).toEqual(['https://cloud.test/web/download/a1']);
    expect(open).not.toHaveBeenCalled();
  });
});
