import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { api, ApiError, Counts, JobFile, JobSummary, TaskRow } from '../api';
import { openJobSocket } from '../jobSocket';
import { plural } from '../batch/UploadDialog';

type Filter = 'all' | TaskRow['status'];
const PAGE = 500;

const STATUS: Record<TaskRow['status'], { label: string; tone: string }> = {
  pending: { label: 'Waiting', tone: 'waiting' },
  in_progress: { label: 'Sending', tone: 'sending' },
  completed: { label: 'Delivered', tone: 'delivered' },
  failed: { label: 'Failed', tone: 'failed' },
};

const FILE_STATUS: Record<JobFile['status'], string> = {
  ready: 'Ready',
  not_uploaded: 'Not uploaded',
  expired: 'Staging deadline passed',
};

function headline(j: JobSummary) {
  if (j.aggregateStatus === 'pending') return 'Waiting to send';
  if (j.aggregateStatus === 'in_progress') return 'Sending';
  if (j.aggregateStatus === 'completed') return 'Delivered';
  return j.counts.completed === 0 ? 'Failed' : 'Finished with failures';
}

/** "FILE_NOT_UPLOADED: 'a.pdf' → Team / Binder: detail" → code + the rest. */
function splitReason(reason: string | null) {
  if (!reason) return null;
  const i = reason.indexOf(':');
  return i > 0 ? { code: reason.slice(0, i), detail: reason.slice(i + 1).trim() } : { code: 'ERROR', detail: reason };
}

const lastSegment = (label: string | null) => (label ? label.split(' / ').at(-1)! : '—');

const RANK: Record<TaskRow['status'], number> = { pending: 0, in_progress: 1, completed: 2, failed: 2 };

function countRows(tasks: Map<string, TaskRow>, job: JobSummary): Counts {
  if (tasks.size < job.taskCount) return job.counts; // still loading pages
  const c: Counts = { pending: 0, inProgress: 0, completed: 0, failed: 0 };
  for (const t of tasks.values()) {
    if (t.status === 'pending') c.pending++;
    else if (t.status === 'in_progress') c.inProgress++;
    else if (t.status === 'completed') c.completed++;
    else c.failed++;
  }
  return c;
}

/** Same rule as the server's TaskCounts.Aggregate. */
function aggregate(c: Counts): JobSummary['aggregateStatus'] {
  const total = c.pending + c.inProgress + c.completed + c.failed;
  if (c.pending === 0 && c.inProgress === 0) return c.failed > 0 ? 'completed_with_errors' : 'completed';
  return c.pending === total ? 'pending' : 'in_progress';
}

/** One job: live counts and status for every delivery, the failure reasons, and team-scoped downloads (Flows 3 + 4). */
export function JobPage({ jobId }: { jobId: string }) {
  const [job, setJob] = useState<JobSummary | null>(null);
  const [files, setFiles] = useState<JobFile[]>([]);
  const [tasks, setTasks] = useState<Map<string, TaskRow>>(new Map());
  const [filter, setFilter] = useState<Filter>('all');
  const [openReason, setOpenReason] = useState<string | null>(null);
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  const refetch = useCallback(async () => {
    try {
      const detail = await api<{ job: JobSummary; files: JobFile[] }>(`/jobs/${jobId}`);
      const all = new Map<string, TaskRow>();
      for (let page = 1; ; page++) {
        const res = await api<{ total: number; tasks: TaskRow[] }>(`/jobs/${jobId}/tasks?page=${page}&limit=${PAGE}`);
        res.tasks.forEach((t) => all.set(t.taskId, t));
        if (page * PAGE >= res.total) break;
      }
      setJob(detail.job);
      setFiles(detail.files);
      setTasks(all);
    } catch (e) {
      setError(e instanceof ApiError && e.status === 404 ? 'Job not found' : e instanceof ApiError ? e.message : String(e));
    }
  }, [jobId]);

  useEffect(() => {
    refetch();
    return openJobSocket(
      jobId,
      (event) => {
        if (event.type === 'subscribed') refetch(); // catch-up on subscribe and every reconnect (TDD 3.3)
        if (event.type === 'task_update') {
          setTasks((current) => {
            const row = current.get(event.taskId);
            // Pushes can arrive out of order: never move a task back from a later state.
            if (!row || RANK[event.status] < RANK[row.status]) return current;
            const next = new Map(current);
            next.set(event.taskId, { ...row, status: event.status, failureReason: event.failureReason });
            return next;
          });
        }
      },
      setConnected,
    );
  }, [jobId, refetch]);

  const rows = useMemo(() => [...tasks.values()].filter((t) => filter === 'all' || t.status === filter), [tasks, filter]);
  const parent = useRef<HTMLDivElement>(null);
  const virtual = useVirtualizer({ count: rows.length, getScrollElement: () => parent.current, estimateSize: () => 52, overscan: 12 });

  const download = async (fileId: string) => {
    setDownloadError(null);
    try {
      const { url } = await api<{ url: string }>(`/files/${fileId}/download-url`, { method: 'POST' });
      window.location.assign(url); // straight from staging; bytes never pass through our servers
    } catch (e) {
      setDownloadError(e instanceof ApiError ? e.message : String(e));
    }
  };

  if (error) {
    return (
      <section className="page">
        <a className="back" href="#/audit">← All jobs</a>
        <p className="error">{error}</p>
      </section>
    );
  }
  if (!job) return <section className="page"><p className="muted">Loading job…</p></section>;
  // Counts come from the rows on screen (all tasks are loaded), so the header can never disagree with the table.
  const c = countRows(tasks, job);
  const view: JobSummary = { ...job, counts: c, aggregateStatus: aggregate(c) };

  return (
    <section className="page">
      <a className="back" href="#/audit">← All jobs</a>
      <div className="job-head">
        <div>
          <h1 data-testid="job-status">{headline(view)}</h1>
          <p className="muted">
            <span className="mono">{jobId.slice(-8)}</span> · {files.length} {plural(files.length, 'file')} · {c.completed} of {job.taskCount} delivered
            <span className={`live ${connected ? 'on' : ''}`}>{connected ? '● live' : '○ reconnecting'}</span>
          </p>
          <div className="count-chips">
            <span className="count waiting"><strong>{c.pending}</strong>Waiting</span>
            <span className="count sending"><strong>{c.inProgress}</strong>Sending</span>
            <span className="count delivered"><strong>{c.completed}</strong>Delivered</span>
            <span className="count failed"><strong>{c.failed}</strong>Failed</span>
          </div>
        </div>
      </div>

      <div className="segmented" role="tablist">
        {(['all', 'pending', 'in_progress', 'completed', 'failed'] as Filter[]).map((f) => (
          <button key={f} role="tab" aria-selected={filter === f} className={filter === f ? 'active' : ''} onClick={() => setFilter(f)}>
            {f === 'all' ? 'All' : STATUS[f].label}
          </button>
        ))}
      </div>

      <div className="card">
        <h3>Files</h3>
        {downloadError && <p className="error small">{downloadError}</p>}
        <ul className="file-list">
          {files.map((f) => (
            <li key={f.fileId}>
              <span className="name">{f.name}</span>
              <span className="muted small">{FILE_STATUS[f.status]}</span>
              <button className="text-btn strong" disabled={f.status !== 'ready'} onClick={() => download(f.fileId)}>Download</button>
            </li>
          ))}
        </ul>
      </div>

      <div className="card table-card">
        <div className="task-head"><span>File</span><span>Folder</span><span>Status</span><span>Reason</span></div>
        <div ref={parent} className="task-scroll" style={{ height: Math.min(Math.max(virtual.getTotalSize(), 56), window.innerHeight * 0.6) }}>
          <div style={{ height: virtual.getTotalSize(), position: 'relative' }}>
            {virtual.getVirtualItems().map((item) => {
              const t = rows[item.index];
              const reason = splitReason(t.failureReason);
              const expanded = openReason === t.taskId;
              return (
                <div key={t.taskId} ref={virtual.measureElement} data-index={item.index} className="task-row"
                  style={{ transform: `translateY(${item.start}px)` }}>
                  <span className="name" title={t.fileName}>{t.fileName}</span>
                  <span className="dest" title={t.destinationLabel ?? t.binderId}>{lastSegment(t.destinationLabel)}</span>
                  <span><span className={`pill ${STATUS[t.status].tone}`}>{STATUS[t.status].label}</span></span>
                  <span className="reason">
                    {t.status === 'failed' && reason ? (
                      <>
                        <code className="code">{reason.code}</code>
                        <button className="text-btn" onClick={() => setOpenReason(expanded ? null : t.taskId)}>{expanded ? 'Show less' : 'Show more'}</button>
                        {expanded && <span className="detail">{reason.detail} {t.attemptCount > 1 && `(after ${t.attemptCount} attempts)`}</span>}
                      </>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </span>
                </div>
              );
            })}
          </div>
          {rows.length === 0 && <p className="muted pad">No deliveries in this view.</p>}
        </div>
      </div>
    </section>
  );
}
