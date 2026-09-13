import { describe, expect, it, vi } from 'vitest';
import { ApiError } from './api';
import { completeUploadWithRecovery, uploadMissingParts, type UploadSession } from './uploadSessions';

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

  it('reuploads a same-size part when Files could not confirm its ETag', async () => {
    const sender = vi.fn(async (request: {
      body: Blob;
      onProgress: (loaded: number) => void;
    }) => request.onProgress(request.body.size));

    await uploadMissingParts(
      new File(['abcdefghij'], 'ten.bin'),
      {
        sessionId: 'session',
        fileId: 'file',
        status: 'uploading',
        fileSize: 10,
        partSize: 10,
        expiresAt: '',
        uploadToken: 'secret',
        error: null,
        uploadedParts: [{ partNumber: 1, size: 10, hasEtag: false }],
      },
      undefined,
      undefined,
      sender,
    );

    expect(sender).toHaveBeenCalledTimes(1);
  });
});

describe('completeUploadWithRecovery', () => {
  it('resumes and sends missing parts when complete reports an incomplete session', async () => {
    const file = new File(['abcdefghij'], 'ten.bin');
    const initial: UploadSession = {
      sessionId: 'session',
      fileId: 'file',
      status: 'uploading',
      fileSize: 10,
      partSize: 4,
      expiresAt: '',
      uploadToken: 'old-token',
      error: null,
      uploadedParts: [],
    };
    const resumed = { ...initial, uploadToken: 'new-token' };
    const complete = vi.fn()
      .mockRejectedValueOnce(new ApiError('Не все части файла загружены', {
        status: 400,
        code: '09BF4D7B-7DB9-4284-BDB0-85457B22A589',
      }))
      .mockResolvedValueOnce({ ...initial, status: 'processing' as const });
    const resume = vi.fn().mockResolvedValue(resumed);
    const upload = vi.fn().mockResolvedValue(undefined);

    const result = await completeUploadWithRecovery(
      file,
      initial,
      undefined,
      undefined,
      { complete, resume, upload },
    );

    expect(result.status).toBe('processing');
    expect(complete).toHaveBeenCalledTimes(2);
    expect(resume).toHaveBeenCalledWith('session');
    expect(upload).toHaveBeenCalledWith(file, resumed, undefined, undefined);
  });
});
