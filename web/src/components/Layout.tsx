import { useEffect, useRef, useState } from 'react';
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import {
  ChevronRight, History, LayoutDashboard, LogOut, Moon, PanelLeftClose, PanelLeftOpen,
  ShieldCheck, Sun, Workflow,
} from 'lucide-react';
import { useAuth } from '../auth/AuthContext';
import { useTheme } from '../lib/theme';
import { initials } from '../lib/format';
import { safeStorageGet, safeStorageSet } from '../lib/hooks';
import { DynamicIcon } from './DynamicIcon';
import { Button } from './ui';

const SIDEBAR_KEY = 'erp.sidebar.collapsed';
const NARROW_QUERY = '(max-width: 900px)'; // igual que el @media del sidebar flotante en app.css

const isNarrowViewport = () => window.matchMedia(NARROW_QUERY).matches;

function useNarrowViewport(): boolean {
  const [narrow, setNarrow] = useState(isNarrowViewport);
  useEffect(() => {
    const mq = window.matchMedia(NARROW_QUERY);
    const onChange = () => setNarrow(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);
  return narrow;
}

function Brand() {
  return (
    <div className="brand-mark" aria-hidden>
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
        <path d="M5 7h14M5 12h10M5 17h14" />
      </svg>
    </div>
  );
}

function useBreadcrumb(): { label: string; to?: string }[] {
  const { pathname } = useLocation();
  const { entities } = useAuth();
  const parts = pathname.split('/').filter(Boolean);
  const crumbs: { label: string; to?: string }[] = [{ label: 'Inicio', to: '/' }];
  if (parts[0] === 'c' && parts[1]) {
    const name = decodeURIComponent(parts[1]);
    const def = entities.find((e) => e.name.toLowerCase() === name.toLowerCase());
    crumbs.push({ label: def?.labelPlural ?? name, to: `/c/${encodeURIComponent(name)}` });
    if (parts[2] === 'new') crumbs.push({ label: 'Nuevo' });
    else if (parts[2]) crumbs.push({ label: `#${decodeURIComponent(parts[2])}` });
  } else if (parts[0] === 'admin') {
    crumbs.push({ label: 'Configuración' });
    const map: Record<string, string> = { model: 'Modelador', security: 'Usuarios y roles', audit: 'Auditoría' };
    if (parts[1]) crumbs.push({ label: map[parts[1]] ?? parts[1] });
  }
  return crumbs;
}

function UserMenu() {
  const { me, logout } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDoc); document.removeEventListener('keydown', onKey); };
  }, [open]);

  if (!me) return null;
  return (
    <div className="menu-anchor" ref={ref}>
      <button className="user-btn" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)} aria-label="Menú de usuario">
        <span className="avatar">{initials(me.displayName || me.username)}</span>
      </button>
      {open && (
        <div className="menu" role="menu">
          <div className="menu-header">
            <div style={{ fontWeight: 500 }}>{me.displayName}</div>
            <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{me.username}{me.roles.length ? ` · ${me.roles.join(', ')}` : ''}</div>
          </div>
          <button
            className="menu-item"
            role="menuitem"
            onClick={async () => { setOpen(false); await logout(); navigate('/login', { replace: true }); }}
          >
            <LogOut size={15} aria-hidden /> Cerrar sesión
          </button>
        </div>
      )}
    </div>
  );
}

export function Layout() {
  const { me, entities, entitiesLoading } = useAuth();
  const { theme, toggle } = useTheme();
  // En pantallas angostas el menú es un panel flotante: arranca cerrado y su estado no se guarda.
  const narrow = useNarrowViewport();
  const [collapsed, setCollapsed] = useState(() => isNarrowViewport() || safeStorageGet(SIDEBAR_KEY) === '1');
  const crumbs = useBreadcrumb();
  const location = useLocation();

  useEffect(() => { if (!narrow) safeStorageSet(SIDEBAR_KEY, collapsed ? '1' : '0'); }, [collapsed, narrow]);
  useEffect(() => { if (narrow) setCollapsed(true); }, [narrow, location.pathname]);
  useEffect(() => { document.querySelector('.content')?.scrollTo(0, 0); }, [location.pathname]);
  useEffect(() => {
    if (!narrow || collapsed) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setCollapsed(true); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [narrow, collapsed]);

  const canModel = !!(me?.canManageModel || me?.isSuperuser);
  const canUsers = !!(me?.canManageUsers || me?.isSuperuser);
  const canAudit = !!(me?.canViewAudit || me?.isSuperuser);

  return (
    <div className="shell">
      <aside className={`sidebar${collapsed ? ' collapsed' : ''}`} aria-label="Navegación principal">
        <div className="sidebar-brand">
          <Brand />
          {!collapsed && <span className="brand-name">ERP AI</span>}
        </div>
        <nav className="sidebar-nav">
          <div className="nav-section">
            <NavLink to="/" end className="nav-link" title="Inicio">
              <LayoutDashboard size={17} aria-hidden /><span>Inicio</span>
            </NavLink>
          </div>
          <div className="nav-section">
            <div className="nav-section-title">Catálogos</div>
            {entities.map((e) => (
              <NavLink key={e.name} to={`/c/${encodeURIComponent(e.name)}`} className="nav-link" title={e.labelPlural}>
                <DynamicIcon name={e.icon} size={17} /><span>{e.labelPlural}</span>
              </NavLink>
            ))}
            {!entitiesLoading && entities.length === 0 && <div className="nav-empty">Aún no hay catálogos</div>}
          </div>
          {(canModel || canUsers || canAudit) && (
            <div className="nav-section">
              <div className="nav-section-title">Configuración</div>
              {canModel && (
                <NavLink to="/admin/model" className="nav-link" title="Modelador">
                  <Workflow size={17} aria-hidden /><span>Modelador</span>
                </NavLink>
              )}
              {canUsers && (
                <NavLink to="/admin/security" className="nav-link" title="Usuarios y roles">
                  <ShieldCheck size={17} aria-hidden /><span>Usuarios y roles</span>
                </NavLink>
              )}
              {canAudit && (
                <NavLink to="/admin/audit" className="nav-link" title="Auditoría">
                  <History size={17} aria-hidden /><span>Auditoría</span>
                </NavLink>
              )}
            </div>
          )}
        </nav>
        <div className="sidebar-footer">
          <button className="nav-link" style={{ width: '100%', border: 0, background: 'none', cursor: 'pointer' }} onClick={() => setCollapsed((c) => !c)} aria-label={collapsed ? 'Expandir menú' : 'Contraer menú'} aria-expanded={!collapsed}>
            {collapsed ? <PanelLeftOpen size={17} aria-hidden /> : <PanelLeftClose size={17} aria-hidden />}
            <span>Contraer</span>
          </button>
        </div>
      </aside>
      {narrow && !collapsed && <div className="sidebar-backdrop" aria-hidden onClick={() => setCollapsed(true)} />}

      <div className="main">
        <header className="topbar">
          <Button variant="ghost" size="sm" iconOnly icon={collapsed ? PanelLeftOpen : PanelLeftClose} aria-label={collapsed ? 'Expandir menú' : 'Contraer menú'} onClick={() => setCollapsed((c) => !c)} />
          <nav className="breadcrumb" aria-label="Ruta de navegación">
            {crumbs.map((c, i) => (
              <span key={i} className="row" style={{ gap: 6, minWidth: 0 }}>
                {i > 0 && <ChevronRight size={14} aria-hidden />}
                {i === crumbs.length - 1 || !c.to
                  ? <span className={i === crumbs.length - 1 ? 'current' : undefined} aria-current={i === crumbs.length - 1 ? 'page' : undefined}>{c.label}</span>
                  : <Link to={c.to}>{c.label}</Link>}
              </span>
            ))}
          </nav>
          <div className="spacer" />
          <Button variant="ghost" size="sm" iconOnly icon={theme === 'dark' ? Sun : Moon} aria-label={theme === 'dark' ? 'Usar tema claro' : 'Usar tema oscuro'} onClick={toggle} />
          <UserMenu />
        </header>
        <main className="content" id="main">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
