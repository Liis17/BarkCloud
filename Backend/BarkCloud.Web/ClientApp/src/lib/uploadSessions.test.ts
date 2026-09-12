import { describe, expect, it, vi } from 'vitest';
import { uploadMissingParts } from './uploadSessions';

describe('uploadMissingParts', () => {
  it('uploads only missing parts sequentially with exact ranges', async () => {
    const calls: Array<{ part: number; start: number; end: number; size: number }> = [];
    let active = 0;
    let maxActive = 0;
    const sender = vi.fn(async (request: {
      partNumber: number;
      start: number;
      end: number;
      body: Blob;
      onProgress: (loaded: number) => void;
    }) => {
      active++;
      maxActive = Math.max(maxActive, active);
      calls.push({
        part: request.partNumber,
        start: request.start,
        end: request.end,
        size: request.body.size,
      });
      request.onProgress(request.body.size);
      await Promise.resolve();
      active--;
    });
    const progress: number[] = [];

    await uploadMissingParts(
      new File(['abcdefghij'], 'ten.bin'),
      {
        sessionId: 'session',
        fileId: 'file',
        status: 'uploading',
        fileSize: 10,
        partSize: 4,
        expiresAt: '',
        uploadToken: 'secret',
        error: null,
        uploadedParts: [{ partNumber: 2, size: 4 }],
      },
      (value) => progress.push(value),
      undefined,
      sender,
    );

    expect(calls).toEqual([
      { part: 1, start: 0, end: 3, size: 4 },
      { part: 3, start: 8, end: 9, size: 2 },
    ]);
    expect(maxActive).toBe(1);
    expect(progress[progress.length - 1]).toBe(1);
  });
});
