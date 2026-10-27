// API client. The access token lives only in memory (TDD 1.6): a reload or the 15-minute
// expiry sends the user back to login (refresh tokens are v2).

declare global {
  interface Window {
    DOCBRIDGE_CONFIG?: { apiBaseUrl: string; wsUrl: string };
  }
}

export const config = {
  apiBaseUrl: window.DOCBRIDGE_CONFIG?.apiBaseUrl ?? '',
  wsUrl: window.DOCBRIDGE_CONFIG?.wsUrl ?? `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`,
};

export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    message: string,
  ) {
    super(message);
  }
}

let token: string | null = null;
let onUnauthorized: () => void = () => {};

export function setToken(value: string | null) {
  token = value;
}

export function getToken() {
  return token;
}

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

export async function api<T>(path: string, init: { method?: string; body?: unknown; headers?: Record<string, string> } = {}): Promise<T> {
  const headers: Record<string, string> = { ...(init.headers ?? {}) };
  if (init.body !== undefined) headers['Content-Type'] = 'application/json';
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(`${config.apiBaseUrl}${path}`, {
    method: init.method ?? (init.body !== undefined ? 'POST' : 'GET'),
    headers,
    body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) {
    if (response.status === 401 && token) onUnauthorized();
    const err = data?.error ?? data;
    throw new ApiError(response.status, err?.code ?? 'ERROR', err?.message ?? response.statusText);
  }
  return data as T;
}

/** Claims from our own JWT (display only; the server validates). */
export function tokenClaims(jwt: string): { sub: string; email?: string; name?: string; exp: number } {
  const part = jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  return JSON.parse(atob(part.padEnd(part.length + ((4 - (part.length % 4)) % 4), '=')));
}

// ---- Shared types (mirror the TDD contracts) ----

export interface Team { id: string; name: string; region: string }
export interface Binder { id: string; name: string }
export interface Folder { id: string; name: string }
export interface DocumentRow { id: string; name: string; sizeBytes: number; contentType: string; createdAt: string }
export interface Contents { folders: Folder[]; documents: DocumentRow[] }
export interface SearchHit {
  type: 'team' | 'binder' | 'folder';
  id: string;
  name: string;
  teamId: string;
  teamName: string;
  region: string;
  binderId: string | null;
  binderName: string | null;
  folderId: string | null;
  path: string[];
}

export interface Counts { pending: number; inProgress: number; completed: number; failed: number }
export interface JobSummary {
  jobId: string;
  submittedByUserId: string;
  createdAt: string;
  completedAt: string | null;
  taskCount: number;
  counts: Counts;
  aggregateStatus: 'pending' | 'in_progress' | 'completed' | 'completed_with_errors';
}
export interface JobFile {
  fileId: string;
  name: string;
  sizeBytes: number;
  contentType: string;
  status: 'ready' | 'not_uploaded' | 'expired';
}
export interface TaskRow {
  taskId: string;
  fileId: string;
  fileName: string;
  destinationLabel: string | null;
  binderId: string;
  folderId: string | null;
  region: string;
  status: 'pending' | 'in_progress' | 'completed' | 'failed';
  attemptCount: number;
  failureReason: string | null;
  platformDocumentId: string | null;
  updatedAt: string;
}

export type UploadPlan =
  | { type: 'post'; url: string; fields: Record<string, string>; expiresAt: string }
  | { type: 'multipart'; uploadId: string; partSize: number; parts: { partNumber: number; url: string }[]; expiresAt: string };
