import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import { LocalFile, Uploader } from '../upload/uploader';

export const MAX_TARGETS = 20; // requirements: up to 20 destinations per job

/** A destination folder, always inside a team (the UI only lets folders be ticked; see toTarget). */
export interface Target {
  key: string;
  teamId: string;
  binderId: string;
  folderId: string | null;
  name: string;
  label: string; // "Team / Binder / Folder / Sub-folder"
}

export const targetKey = (binderId: string, folderId: string | null) => `${binderId}:${folderId ?? 'root'}`;

type Phase = 'editing' | 'uploading' | 'submitting' | 'done' | 'error';

export interface ProgressRow {
  localId: string;
  name: string;
  progress: number;
  status: LocalFile['status'];
  error?: string;
}

interface BatchApi {
  targets: Target[];
  files: LocalFile[];
  phase: Phase;
  jobId: string | null;
  error: string | null;
  minimized: boolean;
  progress: ProgressRow[];
  sent: { files: number; targets: number; deliveries: number } | null;
  isTarget(key: string): boolean;
  toggleTarget(target: Target): boolean;
  removeTarget(key: string): void;
  addFiles(files: File[]): string[];
  removeFile(localId: string): void;
  targetsFor(localId: string): Target[];
  toggleFileTarget(localId: string, key: string): void;
  deliveries: number;
  canSend: boolean;
  send(): void;
  retry(): void;
  dismiss(): void;
  setMinimized(value: boolean): void;
}

const BatchContext = createContext<BatchApi | null>(null);

export function useBatch() {
  const ctx = useContext(BatchContext);
  if (!ctx) throw new Error('useBatch outside BatchProvider');
  return ctx;
}

/**
 * One batch at a time: tick destinations, drop files (hashed on arrival), optionally narrow the folders of a single
 * file, then Send — uploads run (they keep running while you browse), and once every file is in staging the job is
 * submitted with one request. Lives above the router so navigating never interrupts an upload.
 */
export function BatchProvider({ children }: { children: ReactNode }) {
  const [targets, setTargets] = useState<Target[]>([]);
  const [files, setFiles] = useState<LocalFile[]>([]);
  const [excluded, setExcluded] = useState<Record<string, string[]>>({});
  const [phase, setPhase] = useState<Phase>('editing');
  const [jobId, setJobId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [minimized, setMinimized] = useState(false);
  const [progress, setProgress] = useState<ProgressRow[]>([]);
  const [sent, setSent] = useState<BatchApi['sent']>(null);
  const phaseRef = useRef<Phase>('editing');
  const plan = useRef<{ mappings: { localId: string; targets: Target[] }[]; key: string } | null>(null);

  const setPhaseBoth = (p: Phase) => {
    phaseRef.current = p;
    setPhase(p);
  };

  const uploader = useRef<Uploader>();
  if (!uploader.current) {
    uploader.current = new Uploader((list) => {
      setFiles(list);
      if (phaseRef.current === 'uploading' || phaseRef.current === 'error') {
        const sending = new Set(plan.current?.mappings.map((m) => m.localId));
        setProgress(list.filter((f) => sending.has(f.localId)).map((f) => ({
          localId: f.localId, name: f.file.name, progress: f.status === 'uploaded' ? 1 : f.progress, status: f.status, error: f.error,
        })));
      }
    });
  }

  const targetsFor = useCallback(
    (localId: string) => targets.filter((t) => !(excluded[localId] ?? []).includes(t.key)),
    [targets, excluded],
  );

  const deliveries = useMemo(() => files.reduce((n, f) => n + targetsFor(f.localId).length, 0), [files, targetsFor]);

  const canSend = phase === 'editing' && files.length > 0 && deliveries > 0 && files.every((f) => f.status === 'ready');

  const submit = useCallback(async () => {
    const p = plan.current!;
    const byId = new Map(uploader.current!.list().map((f) => [f.localId, f]));
    setPhaseBoth('submitting');
    try {
      const res = await api<{ jobId: string }>('/jobs', {
        headers: { 'Idempotency-Key': p.key },
        body: {
          mappings: p.mappings.map((m) => ({
            fileId: byId.get(m.localId)!.fileId,
            destinations: m.targets.map((t) => ({ teamId: t.teamId, binderId: t.binderId, folderId: t.folderId, label: t.label })),
          })),
        },
      });
      setJobId(res.jobId);
      setPhaseBoth('done');
      uploader.current!.reset();
      setExcluded({});
    } catch (err) {
      setError(err instanceof ApiError ? `${err.code}: ${err.message}` : String(err));
      setPhaseBoth('error');
    }
  }, []);

  // Uploading → submit once every file in the batch is in staging; → error if any upload failed.
  useEffect(() => {
    if (phase !== 'uploading' || !plan.current) return;
    const inBatch = files.filter((f) => plan.current!.mappings.some((m) => m.localId === f.localId));
    if (inBatch.length > 0 && inBatch.every((f) => f.status === 'uploaded')) submit();
    else if (inBatch.some((f) => f.status === 'error') && !inBatch.some((f) => f.status === 'uploading')) {
      setError('Some files could not be uploaded.');
      setPhaseBoth('error');
    }
  }, [files, phase, submit]);

  const api_: BatchApi = {
    targets,
    files,
    phase,
    jobId,
    error,
    minimized,
    progress,
    sent,
    deliveries,
    canSend,
    targetsFor,
    isTarget: (key) => targets.some((t) => t.key === key),
    toggleTarget(target) {
      if (targets.some((t) => t.key === target.key)) {
        setTargets(targets.filter((t) => t.key !== target.key));
        return true;
      }
      if (targets.length >= MAX_TARGETS) return false;
      setTargets([...targets, target]);
      return true;
    },
    removeTarget: (key) => setTargets(targets.filter((t) => t.key !== key)),
    addFiles(list) {
      if (phase === 'done') setPhaseBoth('editing');
      return uploader.current!.add(list);
    },
    removeFile(localId) {
      uploader.current!.remove(localId);
      setExcluded(({ [localId]: _, ...rest }) => rest);
    },
    toggleFileTarget(localId, key) {
      setExcluded((current) => {
        const list = current[localId] ?? [];
        return { ...current, [localId]: list.includes(key) ? list.filter((k) => k !== key) : [...list, key] };
      });
    },
    send() {
      if (!canSend) return;
      const mappings = files.map((f) => ({ localId: f.localId, targets: targetsFor(f.localId) })).filter((m) => m.targets.length > 0);
      // Files with every folder unticked aren't part of this batch.
      files.filter((f) => !mappings.some((m) => m.localId === f.localId)).forEach((f) => uploader.current!.remove(f.localId));
      plan.current = { mappings, key: crypto.randomUUID() };
      setSent({ files: mappings.length, targets: new Set(mappings.flatMap((m) => m.targets.map((t) => t.key))).size, deliveries });
      setJobId(null);
      setError(null);
      setMinimized(false);
      setProgress(mappings.map((m) => ({ localId: m.localId, name: files.find((f) => f.localId === m.localId)!.file.name, progress: 0, status: 'ready' })));
      setPhaseBoth('uploading');
      uploader.current!.start();
    },
    retry() {
      setError(null);
      setPhaseBoth('uploading');
      const pending = uploader.current!.list();
      if (pending.length > 0 && pending.every((f) => f.status === 'uploaded')) submit();
      else uploader.current!.retryFailed();
    },
    dismiss() {
      if (phase === 'uploading' || phase === 'submitting') {
        setMinimized(true); // keeps running in the background
        return;
      }
      if (phase === 'error') uploader.current!.reset(); // cancel the batch; staged leftovers expire with retention
      setPhaseBoth('editing');
      setProgress([]);
      setSent(null);
      setMinimized(false);
    },
    setMinimized,
  };

  return <BatchContext.Provider value={api_}>{children}</BatchContext.Provider>;
}
