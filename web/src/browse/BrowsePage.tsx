import { DragEvent, useEffect, useRef, useState } from 'react';
import { DocumentRow } from '../api';
import { MAX_TARGETS, useBatch } from '../batch/BatchContext';
import { plural } from '../batch/UploadDialog';
import { BinderIcon, CloseIcon, FileIcon, FolderIcon } from '../icons';
import { ALLOWED } from '../upload/fileRules';
import { binderNode, folderNode, nodeKey, NodeRef, teamNode, useHierarchy } from './hierarchy';
import { Tree } from './Tree';

/** Browse: the tree on the left; the open team/binder/folder, the send composer and its contents on the right. */
export function BrowsePage() {
  const h = useHierarchy();
  const [selected, setSelected] = useState<NodeRef | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [notice, setNotice] = useState<string | null>(null);

  // Open the first team by default, like opening a file browser at its first drive.
  useEffect(() => {
    if (!selected && h.teams && h.teams.length > 0) {
      const first = teamNode(h.teams[0]);
      setSelected(first);
      setExpanded(new Set([nodeKey(first)]));
    }
  }, [h.teams, selected]);

  const open = (node: NodeRef) => {
    setSelected(node);
    // Expand the path down to the node so the tree shows where you are.
    setExpanded((current) => {
      const next = new Set(current);
      node.trail.forEach((t) => t.node && next.add(nodeKey(t.node)));
      next.add(nodeKey(node));
      return next;
    });
    if (node.kind === 'team') h.loadBinders(node.teamId, true);
    else h.loadContents(node.binderId!, node.folderId ?? null, true); // fresh: deliveries may have landed
  };

  const toggleExpand = (node: NodeRef) =>
    setExpanded((current) => {
      const next = new Set(current);
      const key = nodeKey(node);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const onLimit = () => {
    setNotice(`A batch can go to at most ${MAX_TARGETS} folders.`);
    setTimeout(() => setNotice(null), 4000);
  };

  return (
    <div className="browse">
      <Tree selected={selected} expanded={expanded} onSelect={open} onToggleExpand={toggleExpand} onLimit={onLimit} />
      <section className="pane">
        {selected ? <ContentPane node={selected} onOpen={open} notice={notice} /> : <p className="muted">Select a team to browse.</p>}
      </section>
    </div>
  );
}

function ContentPane({ node, onOpen, notice }: { node: NodeRef; onOpen(n: NodeRef): void; notice: string | null }) {
  const h = useHierarchy();
  const binders = node.kind === 'team' ? h.binders(node.teamId) : undefined;
  const contents = node.kind !== 'team' ? h.contents(node.binderId!, node.folderId ?? null) : undefined;
  const loading = node.kind === 'team' ? !binders : !contents;

  const counts = node.kind === 'team'
    ? `${binders?.length ?? 0} ${plural(binders?.length ?? 0, 'binder')}`
    : `${contents?.folders.length ?? 0} ${plural(contents?.folders.length ?? 0, 'folder')} · ${contents?.documents.length ?? 0} ${plural(contents?.documents.length ?? 0, 'file')}`;

  return (
    <>
      <nav className="crumbs" aria-label="Breadcrumb">
        {node.trail.map((c, i) => (
          <span key={i}>
            {c.node ? <button onClick={() => onOpen(c.node!)}>{c.name}</button> : <span>{c.name}</span>}
            <span className="sep">/</span>
          </span>
        ))}
        <span className="current">{node.name}</span>
      </nav>
      <div className="pane-head">
        <h1>{node.name}</h1>
        {!loading && <span className="count-note">{counts}</span>}
      </div>

      <Composer notice={notice} />

      <table className="contents">
        <thead>
          <tr><th>Name</th><th>Type</th><th>Size</th><th>Modified</th></tr>
        </thead>
        <tbody>
          {loading && <tr><td colSpan={4} className="muted">Loading…</td></tr>}
          {binders?.map((b) => (
            <tr key={b.id} className="clickable" onClick={() => onOpen(binderNode(node, b))}>
              <td><span className="cell-name"><BinderIcon /> {b.name}</span></td><td><span className="type-tag">Binder</span></td><td className="muted">—</td><td className="muted">—</td>
            </tr>
          ))}
          {contents?.folders.map((f) => (
            <tr key={f.id} className="clickable" onClick={() => onOpen(folderNode(node, f))}>
              <td><span className="cell-name"><FolderIcon /> {f.name}</span></td><td><span className="type-tag">Folder</span></td><td className="muted">—</td><td className="muted">—</td>
            </tr>
          ))}
          {contents?.documents.map((d) => <DocumentLine key={d.id} doc={d} />)}
          {!loading && (binders?.length === 0 || (contents && contents.folders.length + contents.documents.length === 0)) && (
            <tr><td colSpan={4} className="muted">Empty</td></tr>
          )}
        </tbody>
      </table>
    </>
  );
}

function DocumentLine({ doc }: { doc: DocumentRow }) {
  return (
    <tr>
      <td><span className="cell-name strong"><FileIcon /> {doc.name}</span></td>
      <td><span className="type-tag">{typeLabel(doc.contentType, doc.name)}</span></td>
      <td>{formatBytes(doc.sizeBytes)}</td>
      <td>{formatDate(doc.createdAt)}</td>
    </tr>
  );
}

/** Tick destinations in the tree, drop files, optionally narrow a file's folders, Send. */
function Composer({ notice }: { notice: string | null }) {
  const b = useBatch();
  const input = useRef<HTMLInputElement>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [dragging, setDragging] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const busy = b.phase === 'uploading' || b.phase === 'submitting' || b.phase === 'error';
  const enabled = b.targets.length > 0 && !busy;

  const add = (files: File[]) => {
    if (!enabled) return;
    setMessages(b.addFiles(files));
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setDragging(false);
    add([...e.dataTransfer.files]);
  };

  const n = b.targets.length;
  const prompt = busy
    ? 'Sending the previous batch…'
    : n === 0
      ? 'Tick folders in the tree, then drop files here'
      : `Drop files to send to ${n} ${plural(n, 'folder')}`;

  return (
    <div className="composer">
      <div
        className={`dropzone ${enabled ? '' : 'disabled'} ${dragging ? 'dragging' : ''}`}
        onDragOver={(e) => { e.preventDefault(); if (enabled) setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
      >
        <span className="drop-icon"><FileIcon size={20} /></span>
        <span className="prompt">{prompt}<small>PDF, DOCX, XLSX, PNG or JPG · up to 100 files · 1 GB each</small></span>
        <button className="browse-btn" disabled={!enabled} onClick={() => input.current?.click()}>Browse</button>
        <input ref={input} type="file" multiple hidden accept={Object.values(ALLOWED).flat().join(',')} data-testid="file-input"
          onChange={(e) => { add([...(e.target.files ?? [])]); e.target.value = ''; }} />
      </div>

      {notice && <p className="notice">{notice}</p>}
      {messages.map((m) => <p key={m} className="error small">{m}</p>)}

      {n > 0 && (
        <div className="chips">
          {b.targets.map((t) => (
            <span key={t.key} className="chip" title={t.label}>
              {t.name}
              <button aria-label={`Remove ${t.name}`} onClick={() => b.removeTarget(t.key)} disabled={busy}><CloseIcon /></button>
            </span>
          ))}
        </div>
      )}

      {b.phase !== 'uploading' && b.phase !== 'submitting' && b.files.length > 0 && (
        <ul className="queue">
          {b.files.map((f) => {
            const mine = b.targetsFor(f.localId);
            return (
              <li key={f.localId}>
                <div className="queue-row">
                  <span className="name" title={f.file.name}>{f.file.name}</span>
                  <span className={`state ${f.status}`}>{f.status === 'checking' ? 'checking…' : f.status === 'error' ? 'error' : 'ready'}</span>
                  {n > 1 && (
                    <button className="text-btn" onClick={() => setEditing(editing === f.localId ? null : f.localId)} aria-expanded={editing === f.localId}>
                      {mine.length === n ? `All ${n} folders` : `${mine.length} of ${n} folders`}
                    </button>
                  )}
                  <span className="spacer" />
                  <button className="text-btn strong" onClick={() => b.removeFile(f.localId)} disabled={busy}>Remove</button>
                </div>
                {f.error && <p className="error small">{f.error}</p>}
                {editing === f.localId && (
                  <div className="file-targets">
                    {b.targets.map((t) => (
                      <label key={t.key} title={t.label}>
                        <input type="checkbox" checked={mine.some((m) => m.key === t.key)} onChange={() => b.toggleFileTarget(f.localId, t.key)} />
                        {t.label}
                      </label>
                    ))}
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}

      {(n > 0 || b.files.length > 0) && (
        <div className="send-bar">
          <span className="muted">
            {b.files.length} {plural(b.files.length, 'file')} → {n} {plural(n, 'folder')}
            {b.deliveries > 0 && ` · ${b.deliveries} ${plural(b.deliveries, 'delivery', 'deliveries')}`}
          </span>
          <button className="primary" disabled={!b.canSend} onClick={b.send} data-testid="send">Send</button>
        </div>
      )}
    </div>
  );
}

export function typeLabel(contentType: string, name: string) {
  const ext = name.slice(name.lastIndexOf('.') + 1).toUpperCase();
  const byType: Record<string, string> = {
    'application/pdf': 'PDF', 'image/png': 'PNG', 'image/jpeg': 'JPG',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'DOCX',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'XLSX',
  };
  return byType[contentType] ?? ext;
}

export function formatBytes(n: number) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 ** 2) return `${Math.round(n / 1024)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`;
  return `${(n / 1024 ** 3).toFixed(2)} GB`;
}

export const formatDate = (iso: string) => new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
