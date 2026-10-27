import { useCallback, useEffect, useRef, useState } from 'react';
import { api, setToken, setUnauthorizedHandler, tokenClaims } from './api';
import { AuditLogPage } from './audit/AuditLogPage';
import { JobPage } from './audit/JobPage';
import { BatchProvider } from './batch/BatchContext';
import { UploadDialog } from './batch/UploadDialog';
import { BrowsePage } from './browse/BrowsePage';
import { HierarchyProvider } from './browse/hierarchy';
import { ChevronDown, Logo } from './icons';
import { LoginPage } from './pages/LoginPage';

type Route = { page: 'browse' } | { page: 'audit' } | { page: 'job'; jobId: string };

function parseHash(): Route {
  const [, page, id] = location.hash.split('/');
  // #/jobs/<id> is the older job link format; keep it working.
  if ((page === 'audit' || page === 'jobs') && id) return { page: 'job', jobId: id };
  if (page === 'audit' || page === 'jobs') return { page: 'audit' };
  return { page: 'browse' };
}

export function navigate(path: string) {
  location.hash = `#/${path}`;
}

function UserMenu({ email, onSignOut }: { email: string; onSignOut(): void }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  return (
    <div className="user-menu" ref={ref}>
      <button onClick={() => setOpen(!open)} aria-haspopup="menu" aria-expanded={open}>
        <span className="avatar">{email.slice(0, 1).toUpperCase()}</span>
        <span className="user-email">{email}</span>
        <ChevronDown />
      </button>
      {open && (
        <div className="menu" role="menu">
          <p>Signed in as {email}</p>
          <button role="menuitem" onClick={onSignOut}>Sign out</button>
        </div>
      )}
    </div>
  );
}

export function App() {
  const [session, setSession] = useState<{ token: string; email: string } | null>(null);
  const [route, setRoute] = useState<Route>(parseHash);

  useEffect(() => {
    const onHash = () => setRoute(parseHash());
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  const logout = useCallback(() => {
    setToken(null);
    setSession(null);
  }, []);

  useEffect(() => setUnauthorizedHandler(logout), [logout]);

  // 15-minute access token: when it expires, back to login (v1, no refresh tokens).
  useEffect(() => {
    if (!session) return;
    const ms = tokenClaims(session.token).exp * 1000 - Date.now();
    const timer = setTimeout(logout, Math.max(ms, 0));
    return () => clearTimeout(timer);
  }, [session, logout]);

  const login = async (email: string, password: string) => {
    const res = await api<{ accessToken: string }>('/auth/login', { body: { email, password } });
    setToken(res.accessToken);
    setSession({ token: res.accessToken, email: tokenClaims(res.accessToken).email ?? email });
  };

  if (!session) return <LoginPage onLogin={login} />;

  return (
    <BatchProvider key={session.token}>
      <HierarchyProvider>
        <div className="shell">
          <header className="topbar">
            <a className="brand" href="#/browse"><Logo /> DocBridge</a>
            <nav>
              <a className={route.page === 'browse' ? 'active' : ''} href="#/browse">Browse</a>
              <a className={route.page !== 'browse' ? 'active' : ''} href="#/audit">Audit log</a>
            </nav>
            <span className="spacer" />
            <UserMenu email={session.email} onSignOut={logout} />
          </header>
          <main>
            {route.page === 'browse' && <BrowsePage />}
            {route.page === 'audit' && <AuditLogPage />}
            {route.page === 'job' && <JobPage jobId={route.jobId} />}
          </main>
          <UploadDialog />
        </div>
      </HierarchyProvider>
    </BatchProvider>
  );
}
