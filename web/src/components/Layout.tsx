import { useEffect, useRef, useState } from 'react';
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import {
  ArrowLeft, BarChart3, Building2, Check, ChevronDown, ChevronRight, Gauge, LayoutDashboard, LayoutGrid, LogOut, Moon, PanelLeftClose, PanelLeftOpen,
  Plug, Search, Settings, Sun,
} from 'lucide-react';
import { dashboardsApi, reportsApi } from '../api/endpoints';
import type { AppSummary, EntityDef } from '../api/types';
import { useAsync } from '../lib/hooks';
import { useAuth } from '../auth/AuthContext';
import { useToast } from './Toast';
import { useTheme } from '../lib/theme';
import { initials } from '../lib/format';
import { safeStorageGet, safeStorageSet } from '../lib/hooks';
import { ADMIN_GROUPS, adminGroupsFor } from '../lib/adminNav';
import { AppsProvider, useApps } from '../lib/apps';
import { CommandPalette } from './CommandPalette';
import { DynamicIcon } from './DynamicIcon';
import { Button } from './ui';

const SIDEBAR_KEY = 'erp.sidebar.collapsed';
const NARROW_QUERY = '(max-width: 900px)'; // igual que el @media del sidebar flotante en app.css

const isNarrowViewport = () => window.matchMedia(NARROW_QUERY).matches;
const isMac = () => /Mac|iPhone|iPad/.test(navigator.platform);

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

const ADMIN_LABELS: Record<string, string> = Object.fromEntries(ADMIN_GROUPS.flatMap((g) => g.items.map((i) => [i.to.split('/')[2], i.label])));

/** Spec 024: la miga de pan lleva el módulo (enlace a su página) y «Configuración» enlaza al área. */
function useBreadcrumb(): { label: string; to?: string }[] {
  const { pathname } = useLocation();
  const { entities } = useAuth();
  const { appOf, find } = useApps();
  const parts = pathname.split('/').filter(Boolean);
  const crumbs: { label: string; to?: string }[] = [{ label: 'Inicio', to: '/' }];
  const appCrumb = (a?: AppSummary) => { if (a) crumbs.push({ label: a.label, to: `/m/${encodeURIComponent(a.name)}` }); };
  if (parts[0] === 'm' && parts[1]) {
    const a = find(decodeURIComponent(parts[1]));
    crumbs.push({ label: a?.label ?? decodeURIComponent(parts[1]) });
  } else if (parts[0] === 'c' && parts[1]) {
    const name = decodeURIComponent(parts[1]);
    const def = entities.find((e) => e.name.toLowerCase() === name.toLowerCase());
    appCrumb(appOf(name));
    crumbs.push({ label: def?.labelPlural ?? name, to: `/c/${encodeURIComponent(name)}` });
    if (parts[2] === 'new') crumbs.push({ label: 'Nuevo' });
    else if (parts[2]) crumbs.push({ label: `#${decodeURIComponent(parts[2])}` });
  } else if (parts[0] === 'reports' || parts[0] === 'dashboards') {
    const reports = parts[0] === 'reports';
    if (reports && parts[1]) appCrumb(appOf(decodeURIComponent(parts[1])));
    crumbs.push({ label: reports ? 'Reportes' : 'Tableros', to: reports ? '/reports' : '/dashboards' });
    if (parts[1]) crumbs.push({ label: reports ? 'Reporte' : decodeURIComponent(parts[1]) });
  } else if (parts[0] === 'conexiones') {
    crumbs.push({ label: 'Conexiones de Claude' }); // spec 038 (revisión exploratoria F-02)
  } else if (parts[0] === 'admin') {
    crumbs.push({ label: 'Configuración', to: parts[1] ? '/admin' : undefined });
    if (parts[1]) crumbs.push({ label: ADMIN_LABELS[parts[1]] ?? parts[1] });
  }
  return crumbs;
}

function UserMenu() {
  const { me, logout } = useAuth();
  const navigate = useNavigate();
  const toast = useToast();
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
            {me.company && (
              <div className="subtle row" style={{ fontSize: 'var(--text-sm)', gap: 6, marginTop: 2 }}>
                <Building2 size={13} aria-hidden /> {me.company.name} · {me.company.baseCurrency}
              </div>
            )}
          </div>
          <button
            className="menu-item"
            role="menuitem"
            onClick={() => { setOpen(false); navigate('/conexiones'); }}
          >
            <Plug size={15} aria-hidden /> Conexiones de Claude
          </button>
          <button
            className="menu-item"
            role="menuitem"
            onClick={async () => {
              setOpen(false);
              if (await logout()) navigate('/login', { replace: true });
              else toast.error('No pudimos cerrar la sesión', 'El servidor no respondió. Inténtalo de nuevo antes de dejar el equipo.');
            }}
          >
            <LogOut size={15} aria-hidden /> Cerrar sesión
          </button>
        </div>
      )}
    </div>
  );
}

/**
 * Spec 010: empresa actual y cambio de empresa. Solo aparece con dos o más empresas: quien trabaja en una sola no ve
 * nada nuevo. Al cambiar, el servidor valida la elección y la página se vuelve a montar con los datos de la otra empresa.
 */
function CompanySwitcher() {
  const { me, switchCompany } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    // El foco entra a la opción actual para poder elegir con las flechas.
    ref.current?.querySelector<HTMLElement>('[aria-checked="true"]')?.focus();
    const onDoc = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); button.current?.focus(); return; }
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      e.preventDefault();
      const items = [...(ref.current?.querySelectorAll<HTMLElement>('[role="menuitemradio"]') ?? [])];
      const i = items.indexOf(document.activeElement as HTMLElement);
      items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus();
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDoc); document.removeEventListener('keydown', onKey); };
  }, [open]);

  if (!me || me.companies.length < 2) return null;
  const current = me.company;

  const choose = async (id: number) => {
    setOpen(false);
    if (id === current?.id) return;
    setBusy(true);
    setError(null);
    try {
      await switchCompany(id);
      navigate('/');
    } catch {
      setError('No se pudo cambiar de empresa. Recarga la página e intenta de nuevo.');
    } finally {
      setBusy(false);
      button.current?.focus();
    }
  };

  return (
    <div className="menu-anchor" ref={ref}>
      <button
        ref={button}
        className="company-btn"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Empresa: ${current?.name ?? 'ninguna'}. Cambiar de empresa`}
        title={current ? `${current.name} · ${current.baseCurrency}` : undefined}
        disabled={busy}
        onClick={() => setOpen((o) => !o)}
      >
        <Building2 size={16} aria-hidden />
        <span className="company-name">{current?.name ?? 'Sin empresa'}</span>
        <ChevronDown size={14} aria-hidden />
      </button>
      {error && <span className="sr-only" role="alert">{error}</span>}
      {open && (
        <div className="menu" role="menu" aria-label="Empresas">
          <div className="menu-header subtle" style={{ fontSize: 'var(--text-sm)' }}>Trabajar en</div>
          {me.companies.map((c) => (
            <button key={c.id} className="menu-item" role="menuitemradio" aria-checked={c.id === current?.id} onClick={() => void choose(c.id)}>
              <span className="grow" style={{ minWidth: 0 }}>
                <span style={{ display: 'block', fontWeight: c.id === current?.id ? 500 : undefined }}>{c.name}</span>
                <span className="subtle" style={{ fontSize: 'var(--text-xs)' }}>{c.code} · {c.baseCurrency}</span>
              </span>
              {c.id === current?.id && <Check size={15} aria-hidden />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function EntityLink({ e }: { e: EntityDef }) {
  return (
    <NavLink to={`/c/${encodeURIComponent(e.name)}`} className="nav-link" title={e.labelPlural}>
      <DynamicIcon name={e.icon} size={17} /><span>{e.labelPlural}</span>
    </NavLink>
  );
}

/**
 * Spec 024 (H1 CA2): menú lateral del módulo actual. Solo sus documentos (con proceso, primero) y catálogos, y su
 * análisis si tiene tableros o reportes para el usuario.
 */
function AppNav({ app }: { app: AppSummary | undefined }) {
  const { entities, entitiesLoading, me } = useAuth();
  const { appOfGroup, loading } = useApps();
  const { search } = useLocation();
  const { data: boards } = useAsync((s) => (me ? dashboardsApi.list(s) : Promise.resolve([])), [me?.id, entities]);
  const { data: reports } = useAsync((s) => (me ? reportsApi.list(s) : Promise.resolve([])), [me?.id, entities]);

  if (!app) {
    return !entitiesLoading && !loading ? <div className="nav-empty">Aún no hay catálogos</div> : null;
  }
  const own = app.entities.map((n) => entities.find((e) => e.name.toLowerCase() === n.toLowerCase())).filter((e): e is EntityDef => !!e);
  const docs = own.filter((e) => e.workflow);
  const catalogs = own.filter((e) => !e.workflow);
  const hasBoards = (boards ?? []).some((d) => d.apps?.some((a) => a.toLowerCase() === app.name.toLowerCase()));
  const hasReports = (reports ?? []).some((r) => appOfGroup(r.group)?.name === app.name);
  const appQuery = `?app=${encodeURIComponent(app.name)}`;
  // El enlace de análisis del módulo se marca activo solo con su propio filtro, no en la vista general.
  const analysisActive = ({ isActive }: { isActive: boolean }) =>
    `nav-link${isActive && new URLSearchParams(search).get('app')?.toLowerCase() === app.name.toLowerCase() ? ' active' : ''}`;

  return (
    <>
      <div className="nav-section">
        <NavLink to={`/m/${encodeURIComponent(app.name)}`} end className="nav-link nav-app" title={`Resumen de ${app.label}`}>
          <span className="nav-app-icon"><DynamicIcon name={app.icon} size={16} /></span><span>{app.label}</span>
        </NavLink>
      </div>
      {docs.length > 0 && (
        <div className="nav-section">
          <div className="nav-section-title">Documentos</div>
          {docs.map((e) => <EntityLink key={e.name} e={e} />)}
        </div>
      )}
      {catalogs.length > 0 && (
        <div className="nav-section">
          <div className="nav-section-title">Catálogos</div>
          {catalogs.map((e) => <EntityLink key={e.name} e={e} />)}
        </div>
      )}
      {(hasBoards || hasReports) && (
        <div className="nav-section">
          <div className="nav-section-title">Análisis</div>
          {hasBoards && (
            <NavLink to={`/dashboards${appQuery}`} end className={analysisActive} title={`Tableros de ${app.label}`}>
              <Gauge size={17} aria-hidden /><span>Tableros</span>
            </NavLink>
          )}
          {hasReports && (
            <NavLink to={`/reports${appQuery}`} end className={analysisActive} title={`Reportes de ${app.label}`}>
              <BarChart3 size={17} aria-hidden /><span>Reportes</span>
            </NavLink>
          )}
        </div>
      )}
    </>
  );
}

/** Spec 024 (H3 CA1): menú del área de configuración, agrupado; cada opción con su capacidad. */
function AdminNav({ back }: { back: AppSummary | undefined }) {
  const { me } = useAuth();
  return (
    <>
      <div className="nav-section">
        <Link to={back ? `/m/${encodeURIComponent(back.name)}` : '/'} className="nav-link" title={back ? `Volver a ${back.label}` : 'Volver al inicio'}>
          <ArrowLeft size={17} aria-hidden /><span>{back ? `Volver a ${back.label}` : 'Volver al inicio'}</span>
        </Link>
        <NavLink to="/admin" end className="nav-link" title="Configuración">
          <Settings size={17} aria-hidden /><span>Configuración</span>
        </NavLink>
      </div>
      {adminGroupsFor(me).map((g) => (
        <div key={g.label} className="nav-section" role="group" aria-label={g.label}>
          <div className="nav-section-title" aria-hidden>{g.label}</div>
          {g.items.map((i) => (
            <NavLink key={i.to} to={i.to} className="nav-link" title={i.label}>
              <i.icon size={17} aria-hidden /><span>{i.label}</span>
            </NavLink>
          ))}
        </div>
      ))}
    </>
  );
}

export function Layout() {
  return (
    <AppsProvider>
      <Shell />
    </AppsProvider>
  );
}

/**
 * Spec 030 (guardia de foco; mismo patrón que 022 F-03, que se corrigió solo en los diálogos): el foco no se pierde en body.
 * - Un botón que se deshabilita mientras espera al servidor saca el foco; al rehabilitarse (también si el servidor rechaza)
 *   lo recupera.
 * - Si lo que tenía el foco desaparece (el «Revisar cambios» de un editor que se cierra al aplicar, la tarjeta de un módulo
 *   que se vuelve a pintar, la fila que se borró), pasa al contenido principal, que es por donde se sigue.
 * Si la persona hace clic fuera de todo lo enfocable, se respeta.
 */
function useFocusRescue() {
  useEffect(() => {
    let last: HTMLElement | null = null;
    const onFocusIn = (e: FocusEvent) => { last = e.target instanceof HTMLElement ? e.target : null; };
    const onPointerDown = (e: PointerEvent) => {
      if (!(e.target instanceof Element) || !e.target.closest('a[href], button, input, select, textarea, [tabindex]')) last = null;
    };
    const rescue = () => {
      if (!last || (document.activeElement && document.activeElement !== document.body)) return;
      // Revisión adversarial 030 (F-L): uno conectado pero oculto (pestaña o panel con display: none) no puede recibirlo.
      const hidden = last.isConnected && last.checkVisibility && !last.checkVisibility();
      if (last.isConnected && !hidden) { if (!last.matches(':disabled')) last.focus({ preventScroll: true }); return; }
      if (document.querySelector('[data-dialog-root]')) return; // el diálogo abierto maneja su foco
      last = null;
      document.getElementById('main')?.focus({ preventScroll: true });
    };
    const watcher = new MutationObserver(rescue);
    watcher.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled'] });
    document.addEventListener('focusin', onFocusIn);
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => {
      watcher.disconnect();
      document.removeEventListener('focusin', onFocusIn);
      document.removeEventListener('pointerdown', onPointerDown, true);
    };
  }, []);
}

function Shell() {
  useFocusRescue();
  const { me } = useAuth();
  const { current } = useApps();
  const { theme, toggle } = useTheme();
  const navigate = useNavigate();
  // En pantallas angostas el menú es un panel flotante: arranca cerrado y su estado no se guarda.
  const narrow = useNarrowViewport();
  const [collapsed, setCollapsed] = useState(() => isNarrowViewport() || safeStorageGet(SIDEBAR_KEY) === '1');
  const [paletteOpen, setPaletteOpen] = useState(false);
  const crumbs = useBreadcrumb();
  const location = useLocation();
  const adminArea = location.pathname === '/admin' || location.pathname.startsWith('/admin/');
  const showAdmin = adminGroupsFor(me).length > 0;

  useEffect(() => { if (!narrow) safeStorageSet(SIDEBAR_KEY, collapsed ? '1' : '0'); }, [collapsed, narrow]);
  useEffect(() => { if (narrow) setCollapsed(true); }, [narrow, location.pathname]);
  useEffect(() => { document.querySelector('.content')?.scrollTo(0, 0); }, [location.pathname]);
  useEffect(() => {
    if (!narrow || collapsed) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setCollapsed(true); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [narrow, collapsed]);
  // H5 CA1: Ctrl+K (⌘K en Mac) desde cualquier página, también con el foco en un campo.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen(true);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

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
          {adminArea ? <AdminNav back={current} /> : <AppNav app={current} />}
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
              <span key={i} className="row crumb" style={{ gap: 6, minWidth: 0 }}>
                {i > 0 && <ChevronRight size={14} aria-hidden />}
                {i === crumbs.length - 1 || !c.to
                  ? <span className={i === crumbs.length - 1 ? 'current' : undefined} aria-current={i === crumbs.length - 1 ? 'page' : undefined}>{c.label}</span>
                  : <Link to={c.to}>{c.label}</Link>}
              </span>
            ))}
          </nav>
          <div className="spacer" />
          <button type="button" className="search-btn" onClick={() => setPaletteOpen(true)} aria-haspopup="dialog"
            aria-keyshortcuts={isMac() ? 'Meta+K' : 'Control+K'} aria-label="Búsqueda global" title={`Búsqueda global (${isMac() ? '⌘K' : 'Ctrl+K'})`}>
            <Search size={15} aria-hidden />
            <span className="search-btn-text">Búsqueda</span>
            <kbd className="search-btn-kbd" aria-hidden>{isMac() ? '⌘K' : 'Ctrl K'}</kbd>
          </button>
          <Button variant="ghost" size="sm" iconOnly icon={LayoutGrid} aria-label="Cambiar de módulo" title="Módulos" onClick={() => navigate('/')} />
          <CompanySwitcher />
          {showAdmin && (
            <Button variant="ghost" size="sm" iconOnly icon={Settings} aria-label="Configuración" title="Configuración"
              aria-current={adminArea ? 'page' : undefined} onClick={() => navigate('/admin')} />
          )}
          <Button variant="ghost" size="sm" iconOnly icon={theme === 'dark' ? Sun : Moon} aria-label={theme === 'dark' ? 'Usar tema claro' : 'Usar tema oscuro'} onClick={toggle} />
          <UserMenu />
        </header>
        <main className="content" id="main" tabIndex={-1}>
          {/* Spec 010: al cambiar de empresa la página se vuelve a montar (y vuelve a pedir sus datos). */}
          <Outlet key={me?.company?.id ?? 0} />
        </main>
      </div>
      <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
    </div>
  );
}
