import { useEffect, useState } from 'react';
import { api, ApiError, SearchHit } from '../api';
import { useBatch } from '../batch/BatchContext';
import { BinderIcon, CheckIcon, ChevronDown, ChevronRight, FolderIcon, SearchIcon, TeamIcon } from '../icons';
import { binderNode, folderNode, hitNode, nodeKey, NodeRef, teamNode, toTarget, useHierarchy } from './hierarchy';

interface TreeProps {
  selected: NodeRef | null;
  expanded: Set<string>;
  onSelect(node: NodeRef): void;
  onToggleExpand(node: NodeRef): void;
  onLimit(): void;
}

/** Sidebar: name search across every reachable team/binder/folder, or the lazily expanded tree. */
export function Tree(props: TreeProps) {
  const [query, setQuery] = useState('');
  const h = useHierarchy();
  const searching = query.trim().length >= 2;

  return (
    <aside className="sidebar">
      <label className="search">
        <SearchIcon />
        <input placeholder="Find teams, binders, folders" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Search destinations" />
      </label>
      {searching ? (
        <SearchResults query={query.trim()} {...props} />
      ) : (
        <div className="tree" role="tree">
          <p className="tree-label">Teams</p>
          {h.error && <p className="error small">Could not load destinations: {h.error}</p>}
          {!h.teams && !h.error && <p className="muted small pad">Loading…</p>}
          {h.teams?.length === 0 && <p className="muted small pad">You are not a member of any DocBridge-enabled team.</p>}
          {h.teams?.map((t) => <NodeRow key={t.id} node={teamNode(t)} depth={0} {...props} />)}
          {h.teams && h.teamsLoadMs !== null && (
            <p className="muted tiny pad" data-testid="teams-load-ms">{h.teams.length} teams · loaded in {h.teamsLoadMs} ms</p>
          )}
        </div>
      )}
    </aside>
  );
}

function NodeRow({ node, depth, ...props }: TreeProps & { node: NodeRef; depth: number }) {
  const h = useHierarchy();
  const key = nodeKey(node);
  const open = props.expanded.has(key);
  const isSelected = props.selected !== null && nodeKey(props.selected) === key;
  const batch = useBatch();
  const target = toTarget(node);
  const isTicked = target !== null && batch.isTarget(target.key);

  let children: NodeRef[] | undefined;
  if (node.kind === 'team') children = h.binders(node.teamId)?.map((b) => binderNode(node, b));
  else children = h.contents(node.binderId!, node.folderId ?? null)?.folders.map((f) => folderNode(node, f));

  useEffect(() => {
    if (!open) return;
    if (node.kind === 'team') h.loadBinders(node.teamId);
    else h.loadContents(node.binderId!, node.folderId ?? null);
  }, [open]); // loaders are stable; re-run only when the node opens

  return (
    <div role="treeitem" aria-expanded={open}>
      <div className={`node ${isSelected ? 'selected' : ''} ${isTicked ? 'ticked' : ''}`} style={{ paddingLeft: 10 + depth * 18 }}>
        <button className="chev" onClick={() => props.onToggleExpand(node)} aria-label={open ? 'Collapse' : 'Expand'}>
          {open ? <ChevronDown /> : <ChevronRight />}
        </button>
        {node.kind === 'team' ? <span className="node-icon"><TeamIcon /></span> : <TargetToggle node={node} onLimit={props.onLimit} />}
        <button className="node-name" onClick={() => props.onSelect(node)} title={node.name}>{node.name}</button>
        {node.kind === 'team' && <span className="badge">{node.region}</span>}
      </div>
      {open && (
        <div role="group">
          {!children && <p className="muted tiny" style={{ paddingLeft: 46 + depth * 18 }}>Loading…</p>}
          {children?.map((c) => <NodeRow key={nodeKey(c)} node={c} depth={depth + 1} {...props} />)}
        </div>
      )}
    </div>
  );
}

/** Folder icon that becomes a checkbox on hover or when ticked as a destination; binders just show their icon. */
export function TargetToggle({ node, onLimit }: { node: NodeRef; onLimit(): void }) {
  const batch = useBatch();
  const target = toTarget(node);
  if (!target) return <span className="node-icon">{node.kind === 'binder' ? <BinderIcon /> : <TeamIcon />}</span>;
  const checked = batch.isTarget(target.key);
  return (
    <button
      className={`target-toggle ${checked ? 'checked' : ''}`}
      role="checkbox"
      aria-checked={checked}
      aria-label={`${checked ? 'Untick' : 'Tick'} ${node.name} as a destination`}
      title={checked ? 'Remove from destinations' : 'Send files here'}
      onClick={(e) => {
        e.stopPropagation();
        if (!batch.toggleTarget(target)) onLimit();
      }}
    >
      <span className="as-icon"><FolderIcon /></span>
      <span className="as-box">{checked && <CheckIcon />}</span>
    </button>
  );
}

function SearchResults({ query, ...props }: TreeProps & { query: string }) {
  const [hits, setHits] = useState<SearchHit[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setHits(null);
    setError(null);
    const timer = setTimeout(() => {
      api<SearchHit[]>(`/destinations/search?q=${encodeURIComponent(query)}`)
        .then(setHits)
        .catch((e: ApiError) => setError(e.message));
    }, 250);
    return () => clearTimeout(timer);
  }, [query]);

  if (error) return <p className="error small pad">{error}</p>;
  if (!hits) return <p className="muted small pad">Searching…</p>;
  if (hits.length === 0) return <p className="muted small pad">No teams, binders or folders match “{query}”.</p>;
  return (
    <ul className="search-results">
      {hits.map((hit) => {
        const node = hitNode(hit);
        return (
          <li key={`${hit.type}:${hit.id}`} className={props.selected && nodeKey(props.selected) === nodeKey(node) ? 'selected' : ''}>
            {hit.type === 'team' ? <span className="node-icon"><TeamIcon /></span> : <TargetToggle node={node} onLimit={props.onLimit} />}
            <button className="hit" onClick={() => props.onSelect(node)}>
              <span className="hit-name">{hit.name}</span>
              <span className="hit-path" title={hit.path.join(' / ')}>{hit.path.slice(0, -1).join(' / ') || hit.region}</span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
