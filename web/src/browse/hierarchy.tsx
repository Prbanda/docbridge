import { createContext, ReactNode, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { api, ApiError, Binder, Contents, SearchHit, Team } from '../api';
import { Target, targetKey } from '../batch/BatchContext';

/** A place in the hierarchy the user can open: a team, a binder (its root) or a folder. */
export interface NodeRef {
  kind: 'team' | 'binder' | 'folder';
  teamId: string;
  teamName: string;
  region: string;
  binderId?: string;
  binderName?: string;
  folderId?: string;
  name: string;
  /** Breadcrumb from the team down to this node's parent. Entries without a node aren't navigable (search hits). */
  trail: { name: string; node?: NodeRef }[];
}

export const nodeKey = (n: NodeRef) => `${n.kind}:${n.folderId ?? n.binderId ?? n.teamId}`;

export const pathOf = (n: NodeRef) => [...n.trail.map((t) => t.name), n.name];

export const teamNode = (t: Team): NodeRef => ({ kind: 'team', teamId: t.id, teamName: t.name, region: t.region, name: t.name, trail: [] });

export const binderNode = (team: NodeRef, b: Binder): NodeRef => ({
  kind: 'binder', teamId: team.teamId, teamName: team.teamName, region: team.region, binderId: b.id, binderName: b.name, name: b.name,
  trail: [{ name: team.name, node: team }],
});

export const folderNode = (parent: NodeRef, f: { id: string; name: string }): NodeRef => ({
  kind: 'folder', teamId: parent.teamId, teamName: parent.teamName, region: parent.region, binderId: parent.binderId,
  binderName: parent.binderName, folderId: f.id, name: f.name, trail: [...parent.trail, { name: parent.name, node: parent }],
});

/** Search hit → node. Intermediate folders on the path have names but no ids, so they aren't navigable. */
export function hitNode(h: SearchHit): NodeRef {
  const team: NodeRef = { kind: 'team', teamId: h.teamId, teamName: h.teamName, region: h.region, name: h.teamName, trail: [] };
  if (h.type === 'team') return team;
  const binder: NodeRef = binderNode(team, { id: h.binderId!, name: h.binderName! });
  if (h.type === 'binder') return binder;
  const middle = h.path.slice(2, -1).map((name) => ({ name }));
  return {
    kind: 'folder', teamId: h.teamId, teamName: h.teamName, region: h.region, binderId: h.binderId!, binderName: h.binderName!,
    folderId: h.folderId!, name: h.name, trail: [{ name: team.name, node: team }, { name: binder.name, node: binder }, ...middle],
  };
}

/** Binder roots and folders can receive files; teams can't. */
/** Only folders can receive files; teams and binders are for navigating. */
export function toTarget(n: NodeRef): Target | null {
  if (n.kind !== 'folder' || !n.binderId || !n.folderId) return null;
  return { key: targetKey(n.binderId, n.folderId), teamId: n.teamId, binderId: n.binderId, folderId: n.folderId, name: n.name, label: pathOf(n).join(' / ') };
}

interface Hierarchy {
  teams: Team[] | null;
  error: string | null;
  teamsLoadMs: number | null;
  binders(teamId: string): Binder[] | undefined;
  contents(binderId: string, folderId: string | null): Contents | undefined;
  loadBinders(teamId: string, force?: boolean): Promise<void>;
  loadContents(binderId: string, folderId: string | null, force?: boolean): Promise<void>;
}

const HierarchyContext = createContext<Hierarchy | null>(null);

export const useHierarchy = () => {
  const ctx = useContext(HierarchyContext);
  if (!ctx) throw new Error('useHierarchy outside HierarchyProvider');
  return ctx;
};

/** One level per request (TDD 1.6: the < 1 s target is per level), cached for the session. */
export function HierarchyProvider({ children }: { children: ReactNode }) {
  const [teams, setTeams] = useState<Team[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [teamsLoadMs, setTeamsLoadMs] = useState<number | null>(null);
  const [, setVersion] = useState(0);
  const binders = useRef(new Map<string, Binder[]>());
  const contents = useRef(new Map<string, Contents>());

  useEffect(() => {
    const started = performance.now();
    api<Team[]>('/destinations/teams')
      .then((t) => {
        setTeams(t);
        setTeamsLoadMs(Math.round(performance.now() - started));
      })
      .catch((e: ApiError) => setError(e.message));
  }, []);

  const loadBinders = useCallback(async (teamId: string, force = false) => {
    if (binders.current.has(teamId) && !force) return;
    binders.current.set(teamId, await api<Binder[]>(`/destinations/teams/${teamId}/binders`));
    setVersion((v) => v + 1);
  }, []);

  const loadContents = useCallback(async (binderId: string, folderId: string | null, force = false) => {
    const key = `${binderId}:${folderId ?? 'root'}`;
    if (contents.current.has(key) && !force) return;
    const query = folderId ? `?folderId=${folderId}` : '';
    contents.current.set(key, await api<Contents>(`/destinations/binders/${binderId}/contents${query}`));
    setVersion((v) => v + 1);
  }, []);

  const value: Hierarchy = {
    teams,
    error,
    teamsLoadMs,
    binders: (teamId) => binders.current.get(teamId),
    contents: (binderId, folderId) => contents.current.get(`${binderId}:${folderId ?? 'root'}`),
    loadBinders,
    loadContents,
  };
  return <HierarchyContext.Provider value={value}>{children}</HierarchyContext.Provider>;
}
