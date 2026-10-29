import { api, ApiError, UploadPlan } from '../api';
import { contentTypeFor, MAX_BYTES, MAX_FILES } from './fileRules';

export { ALLOWED, MAX_FILES } from './fileRules';

const URL_WAVE = 20; // TDD 2.6: request presigned URLs in waves of at most 20
const CONCURRENCY = 4; // TDD 2.6: bounded upload concurrency (4–6)

/** checking = SHA-256 in progress; ready = hashed, waiting for Send; then uploading → uploaded (or error). */
export type UploadStatus = 'checking' | 'ready' | 'uploading' | 'uploaded' | 'error';

export interface LocalFile {
  localId: string;
  file: File;
  contentType: string;
  status: UploadStatus;
  progress: number;
  sha256?: string;
  fileId?: string;
  error?: string;
}

type Listener = (files: LocalFile[]) => void;

/**
 * Files are hashed in a Web Worker as soon as they are added (so "ready" means the checksum is known), and only
 * uploaded after start() — the Send button. Uploads go straight to S3 (ADR-002): URLs requested in waves, bounded
 * concurrency, and multipart uploads keep their finished parts so a failed part resumes rather than restarts.
 */
export class Uploader {
  private files = new Map<string, LocalFile>();
  private plans = new Map<string, UploadPlan>();
  private multipartDone = new Map<string, Map<number, string>>();
  private worker = new Worker(new URL('./hashWorker.ts', import.meta.url), { type: 'module' });
  private requesting = false;
  private active = 0;
  private started = false;

  constructor(private listener: Listener) {
    this.worker.onmessage = (e: MessageEvent<{ id: string; progress?: number; sha256?: string; error?: string }>) => {
      const f = this.files.get(e.data.id);
      if (!f) return;
      if (e.data.error) this.update(f, { status: 'error', error: `Could not read the file: ${e.data.error}` });
      else if (e.data.sha256) this.update(f, { sha256: e.data.sha256, status: 'ready', progress: 0 });
      else this.update(f, { progress: e.data.progress ?? 0 });
      this.pump();
    };
  }

  list = () => [...this.files.values()];

  add(files: File[]): string[] {
    const errors: string[] = [];
    for (const file of files) {
      if (this.files.size >= MAX_FILES) {
        errors.push(`At most ${MAX_FILES} files per batch`);
        break;
      }
      const type = contentTypeFor(file);
      if (!type) errors.push(`${file.name}: only PDF, DOCX, XLSX, PNG and JPG are allowed`);
      else if (file.size === 0 || file.size > MAX_BYTES) errors.push(`${file.name}: must be between 1 byte and 1 GB`);
      else {
        const f: LocalFile = { localId: crypto.randomUUID(), file, contentType: type, status: 'checking', progress: 0 };
        this.files.set(f.localId, f);
        this.worker.postMessage({ id: f.localId, file });
      }
    }
    this.emit();
    return errors;
  }

  remove(localId: string) {
    this.files.delete(localId);
    this.emit();
  }

  /** Begin uploading every ready file (Send). */
  start() {
    this.started = true;
    this.pump();
  }

  /** Put failed uploads back in the queue. */
  retryFailed() {
    for (const f of this.files.values()) {
      if (f.status === 'error' && f.sha256) this.update(f, { status: 'ready', error: undefined, progress: 0 });
    }
    this.pump();
  }

  /** Forget everything (after a batch has become a job). */
  reset() {
    this.files.clear();
    this.plans.clear();
    this.multipartDone.clear();
    this.started = false;
    this.emit();
  }

  private update(f: LocalFile, patch: Partial<LocalFile>) {
    Object.assign(f, patch);
    this.emit();
  }

  private emit() {
    this.listener(this.list().map((f) => ({ ...f })));
  }

  private async pump() {
    if (!this.started) return;
    const needIds = this.list().filter((f) => f.status === 'ready' && !f.fileId).slice(0, URL_WAVE);
    if (needIds.length > 0 && !this.requesting) {
      this.requesting = true;
      try {
        const created = await api<{ fileId: string; upload: UploadPlan }[]>('/files', {
          body: { files: needIds.map((f) => ({ name: f.file.name, sizeBytes: f.file.size, contentType: f.contentType, sha256: f.sha256 })) },
        });
        created.forEach((c, i) => {
          needIds[i].fileId = c.fileId;
          this.plans.set(c.fileId, c.upload);
        });
      } catch (err) {
        needIds.forEach((f) => this.update(f, { status: 'error', error: (err as Error).message }));
      } finally {
        this.requesting = false;
      }
      this.pump();
    }
    while (this.active < CONCURRENCY) {
      const next = this.list().find((f) => f.status === 'ready' && f.fileId);
      if (!next) break;
      this.active++;
      this.update(next, { status: 'uploading', progress: 0 });
      this.upload(next)
        .then(() => this.update(next, { status: 'uploaded', progress: 1 }))
        .catch((err: Error) => this.update(next, { status: 'error', error: err.message }))
        .finally(() => {
          this.active--;
          this.pump();
        });
    }
  }

  private async freshPlan(f: LocalFile): Promise<UploadPlan> {
    let plan = this.plans.get(f.fileId!)!;
    if (new Date(plan.expiresAt).getTime() - Date.now() < 30_000) {
      // AM-2: re-issue for the same file resource instead of creating a new one.
      const res = await api<{ upload: UploadPlan }>(`/files/${f.fileId}/upload-url`, {
        body: plan.type === 'multipart' ? { uploadId: plan.uploadId } : {},
      });
      plan = res.upload;
      this.plans.set(f.fileId!, plan);
    }
    return plan;
  }

  private async upload(f: LocalFile) {
    const plan = await this.freshPlan(f);
    if (plan.type === 'post') {
      const form = new FormData();
      Object.entries(plan.fields).forEach(([k, v]) => form.append(k, v));
      form.append('file', f.file); // must be the last field
      await xhr('POST', plan.url, form, (p) => this.update(f, { progress: p }));
      return;
    }
    const done = this.multipartDone.get(f.fileId!) ?? new Map<number, string>();
    this.multipartDone.set(f.fileId!, done);
    for (const part of plan.parts) {
      if (done.has(part.partNumber)) continue;
      const start = (part.partNumber - 1) * plan.partSize;
      const blob = f.file.slice(start, start + plan.partSize);
      let etag: string | null = null;
      for (let attempt = 1; !etag; attempt++) {
        try {
          const current = await this.freshPlan(f);
          const url = current.type === 'multipart' ? current.parts[part.partNumber - 1].url : part.url;
          etag = await xhr('PUT', url, blob, (p) => this.update(f, { progress: (done.size + p) / plan.parts.length }));
          if (!etag) throw new Error('Storage did not return an ETag for the part');
        } catch (err) {
          etag = null;
          if (attempt >= 3) throw err;
        }
      }
      done.set(part.partNumber, etag);
    }
    await api(`/files/${f.fileId}/multipart/complete`, {
      body: { uploadId: plan.uploadId, parts: [...done].map(([partNumber, etag]) => ({ partNumber, etag })) },
    });
  }
}

/** XHR (not fetch) for upload progress events. Resolves with the ETag header. */
function xhr(method: string, url: string, body: Blob | FormData, onProgress: (p: number) => void): Promise<string> {
  return new Promise((resolve, reject) => {
    const req = new XMLHttpRequest();
    req.open(method, url);
    req.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total);
    req.onload = () =>
      req.status >= 200 && req.status < 300
        ? resolve(req.getResponseHeader('ETag') ?? '')
        : reject(new ApiError(req.status, 'UPLOAD_FAILED', `Storage rejected the upload (${req.status})`));
    req.onerror = () => reject(new ApiError(0, 'NETWORK', 'Network error during upload'));
    req.send(body);
  });
}
