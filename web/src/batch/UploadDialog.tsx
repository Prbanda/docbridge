import { navigate } from '../App';
import { useBatch } from './BatchContext';

/** The Send progress dialog, and its minimized pill. Rendered at app level so it survives navigation. */
export function UploadDialog() {
  const b = useBatch();
  if (b.phase === 'editing' || !b.sent) return null;

  const overall = b.progress.length === 0 ? 0 : b.progress.reduce((n, r) => n + r.progress, 0) / b.progress.length;
  const pct = Math.round((b.phase === 'done' ? 1 : overall) * 100);
  const title = b.phase === 'done' ? 'Uploaded' : b.phase === 'error' ? 'Upload stopped' : b.phase === 'submitting' ? 'Creating job…' : 'Uploading';
  const summary = `${b.sent.files} ${plural(b.sent.files, 'file')} → ${b.sent.targets} ${plural(b.sent.targets, 'folder')} · ${pct}%`;

  const openAudit = () => {
    if (b.jobId) navigate(`audit/${b.jobId}`);
    else navigate('audit');
    b.dismiss();
  };

  if (b.minimized) {
    return (
      <button className="upload-pill" onClick={() => b.setMinimized(false)} title="Show upload progress">
        <span className="pill-title">{title}</span>
        <span className="pill-bar"><span style={{ width: `${pct}%` }} /></span>
        <span className="pill-pct">{pct}%</span>
      </button>
    );
  }

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={title}>
      <div className="modal">
        <div className="modal-head">
          <div>
            <h2>{title}</h2>
            <p className="muted">{summary}</p>
          </div>
          {b.phase !== 'done' && b.phase !== 'error' && (
            <button className="text-btn" onClick={() => b.setMinimized(true)}>Minimize</button>
          )}
        </div>
        <div className="bar big"><span style={{ width: `${pct}%` }} /></div>
        <ul className="progress-list">
          {b.progress.map((r) => (
            <li key={r.localId}>
              <span className="name" title={r.name}>{r.name}</span>
              <span className={`bar ${r.status === 'error' ? 'bad' : ''}`}><span style={{ width: `${Math.round(r.progress * 100)}%` }} /></span>
              <span className="pct">{r.status === 'error' ? 'failed' : `${Math.round(r.progress * 100)}%`}</span>
            </li>
          ))}
        </ul>
        {b.error && <p className="error">{b.error}</p>}
        {b.phase === 'error' ? (
          <div className="modal-actions">
            <button className="primary" onClick={b.retry}>Retry</button>
            <button className="text-btn" onClick={b.dismiss}>Cancel batch</button>
          </div>
        ) : (
          <>
            <p className="muted small">Minimize to keep browsing. Progress continues under Audit log.</p>
            <div className="modal-actions">
              <button className="primary" onClick={openAudit}>Open in audit log</button>
              <button className="text-btn" onClick={b.dismiss}>Keep browsing</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

export const plural = (n: number, word: string, many = `${word}s`) => (n === 1 ? word : many);
