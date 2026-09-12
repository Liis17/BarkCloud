import { createSHA256 } from 'hash-wasm';

export const HASH_CHUNK_SIZE = 4 * 1024 * 1024;

interface HashBlobOptions {
  chunkSize?: number;
  signal?: AbortSignal;
  onProgress?: (fraction: number) => void;
}

export async function hashBlobInChunks(
  blob: Blob,
  options: HashBlobOptions = {},
): Promise<string> {
  const chunkSize = options.chunkSize ?? HASH_CHUNK_SIZE;
  if (chunkSize <= 0) throw new RangeError('chunkSize must be positive');
  throwIfAborted(options.signal);

  const hasher = await createSHA256();
  hasher.init();
  if (blob.size === 0) {
    options.onProgress?.(1);
    return hasher.digest('hex');
  }

  for (let offset = 0; offset < blob.size; offset += chunkSize) {
    throwIfAborted(options.signal);
    const end = Math.min(blob.size, offset + chunkSize);
    const bytes = new Uint8Array(await blob.slice(offset, end).arrayBuffer());
    throwIfAborted(options.signal);
    hasher.update(bytes);
    options.onProgress?.(end / blob.size);
  }

  return hasher.digest('hex');
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
}
