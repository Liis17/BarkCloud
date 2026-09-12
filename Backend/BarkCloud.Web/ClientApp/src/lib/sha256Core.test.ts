import { describe, expect, it, vi } from 'vitest';
import { hashBlobInChunks } from './sha256Core';

describe('hashBlobInChunks', () => {
  it('matches a known SHA-256 vector without reading the whole file', async () => {
    const { blob: file, wholeFileRead } = chunkedBlob('abc');

    const hash = await hashBlobInChunks(file, { chunkSize: 2 });

    expect(hash).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    expect(wholeFileRead).not.toHaveBeenCalled();
  });

  it('reports progress across multiple chunks', async () => {
    const progress: number[] = [];

    await hashBlobInChunks(chunkedBlob('abcdef').blob, {
      chunkSize: 2,
      onProgress: (value) => progress.push(value),
    });

    expect(progress).toEqual([2 / 6, 4 / 6, 1]);
  });

  it('supports cancellation between chunks', async () => {
    const controller = new AbortController();

    const hashing = hashBlobInChunks(chunkedBlob('abcdef').blob, {
      chunkSize: 2,
      signal: controller.signal,
      onProgress: () => controller.abort(),
    });

    await expect(hashing).rejects.toMatchObject({ name: 'AbortError' });
  });
});

function chunkedBlob(value: string): { blob: Blob; wholeFileRead: ReturnType<typeof vi.fn> } {
  const source = new TextEncoder().encode(value);
  const wholeFileRead = vi.fn(async () => source.buffer.slice(0));
  const blob = {
    size: source.byteLength,
    arrayBuffer: wholeFileRead,
    slice(start = 0, end = source.byteLength) {
      const bytes = source.slice(start, end);
      return {
        size: bytes.byteLength,
        arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength),
      } as Blob;
    },
  } as unknown as Blob;
  return { blob, wholeFileRead };
}
