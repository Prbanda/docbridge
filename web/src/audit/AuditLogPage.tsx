import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, JobSummary } from '../api';
import { navigate } from '../App';
import { useBatch } from '../batch/BatchContext';

const PAGE = 50;

/** Status pill for a job, from its task counts (job status is derived on read, TDD). */
export function jobPill(j: Pick<JobSummary, 'aggregateStatus' | 'counts'>): { label: string; tone: string } {
  switch (j.aggregateStatus) {
    case 'pending':
      return { label: 'Waiting', tone: 'waiting' };
    case 'in_progress':
      return { label: 'Sending', tone: 'sending' };
    case 'completed':
      return { label: 'Delivered', tone: 'delivered' };
    default:
      return j.counts.completed === 0 ? { label: 'Failed', tone: 'failed' } : { label: 'Partly failed', tone: 'partial' };
  }
}

export const formatWhen = (iso: string) =>
  new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/** Audit log: every job you have sent, newest first, with its delivery progress. */
export function AuditLogPage() {
  const batch = useBatch();
  const [jobs, setJobs] = useState<JobSummary[] | null>(null);
  const [pages, setPages] = useState(1);
  const [more, setMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const all: JobSummary[] = [];
      for (let page = 1; page <= pages; page++) {
        const batchOf = await api<JobSummary[]>(`/jobs?page=${page}&limit=${PAGE}`);
        all.push(...batchOf);
        if (batchOf.length < PAGE) break;
      }
      setJobs(all);
      setMore(all.length === pages * PAGE);
      setError(null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    }
  }, [pages]);

  useEffect(() => {
    load();
    const timer = setInterval(load, 10_000);
    return () => clearInterval(timer);
  }, [load]);

  // A batch still uploading has no job yet: show it on top so "progress continues under Audit log" holds.
  const uploading = (batch.phase === 'uploading' || batch.phase === 'submitting') && batch.sent;
  const uploadPct = batch.progress.length
    ? Math.round((batch.progress.reduce((n, r) => n + r.progress, 0) / batch.progress.length) * 100)
    : 0;

  return (
    <section className="page">
      <div className="page-head">
        <div>
          <h1>Audit log</h1>
          <p className="muted">Jobs you have sent, newest first</p>
        </div>
        <button className="text-btn strong" onClick={load}>Refresh</button>
      </div>
      {error && <p className="error">{error}</p>}
      {!jobs && !error && <p className="muted">Loading…</p>}
      <ul className="joblist">
        {uploading && batch.sent && (
          <li className="jobrow" onClick={() => batch.setMinimized(false)}>
            <span className="when">Now</span>
            <span className="mono muted">uploading</span>
            <span className="progress-cell">
              <span className="bar"><span style={{ width: `${uploadPct}%` }} /></span>
              <span className="small muted">{batch.sent.files} files uploading · {uploadPct}%</span>
            </span>
            <span className="pill sending">Uploading</span>
          </li>
        )}
        {jobs?.map((j) => {
          const pill = jobPill(j);
          const done = j.counts.completed;
          return (
            <li key={j.jobId} className="jobrow" onClick={() => navigate(`audit/${j.jobId}`)} role="link" tabIndex={0}
              onKeyDown={(e) => e.key === 'Enter' && navigate(`audit/${j.jobId}`)}>
              <span className="when">{formatWhen(j.createdAt)}</span>
              <span className="mono muted">{j.jobId.slice(-8)}</span>
              <span className="progress-cell">
                <span className="bar"><span style={{ width: `${(done / Math.max(j.taskCount, 1)) * 100}%` }} /></span>
                <span className="small muted">
                  {done} of {j.taskCount}
                  {j.counts.failed > 0 && ` · ${j.counts.failed} failed`}
                </span>
              </span>
              <span className={`pill ${pill.tone}`}>{pill.label}</span>
            </li>
          );
        })}
      </ul>
      {jobs?.length === 0 && !uploading && <p className="muted">No jobs yet. Tick folders under Browse and send some files.</p>}
      {more && <button className="text-btn strong load-more" onClick={() => setPages(pages + 1)}>Load more</button>}
    </section>
  );
}
