/// <reference lib="webworker" />
// SHA-256 off the main thread (TDD 2.6) so the UI never freezes. Incremental: files are read in
// 8 MB slices, so a 1 GB file is never held in memory at once.
import { createSHA256 } from 'hash-wasm';

const CHUNK = 8 * 1024 * 1024;

self.onmessage = async (event: MessageEvent<{ id: string; file: File }>) => {
  const { id, file } = event.data;
  try {
    const hasher = await createSHA256();
    hasher.init();
    for (let offset = 0; offset < file.size; offset += CHUNK) {
      const chunk = new Uint8Array(await file.slice(offset, offset + CHUNK).arrayBuffer());
      hasher.update(chunk);
      self.postMessage({ id, progress: Math.min(1, (offset + CHUNK) / file.size) });
    }
    self.postMessage({ id, sha256: hasher.digest('hex') });
  } catch (err) {
    self.postMessage({ id, error: String(err) });
  }
};
