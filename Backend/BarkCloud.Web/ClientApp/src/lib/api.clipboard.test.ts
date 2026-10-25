import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchClipboardImage } from './api';

afterEach(() => vi.unstubAllGlobals());

describe('fetchClipboardImage', () => {
  it('rejects 415 before decoding and explains that the safe representation is unavailable', async () => {
    const blob = vi.fn();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ status: 415, ok: false, blob }));

    await expect(fetchClipboardImage('/api/files/image')).rejects.toThrow('Безопасное представление');
    expect(blob).not.toHaveBeenCalled();
  });

  it('decodes the response body only after a successful status', async () => {
    const image = new Blob(['image']);
    const blob = vi.fn().mockResolvedValue(image);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ status: 200, ok: true, blob }));

    await expect(fetchClipboardImage('/api/files/image')).resolves.toBe(image);
    expect(blob).toHaveBeenCalledOnce();
  });
});
