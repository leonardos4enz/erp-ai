import { createContext, useCallback, useContext, useEffect, useMemo, type ReactNode } from 'react';
import { matchPath, useLocation } from 'react-router-dom';
import { modelApi } from '../api/endpoints';
import type { AppSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { safeStorageGet, safeStorageSet, useAsync } from './hooks';

/** Spec 024: módulo de los catálogos sin grupo (lo arma el servidor si existen). */
export const GENERAL_APP = 'General';

interface AppsState {
  /** Módulos del usuario: el servidor ya filtró los que no tienen catálogos que pueda leer. */
  apps: AppSummary[];
  loading: boolean;
  find: (name: string | null | undefined) => AppSummary | undefined;
  /** Módulo de un catálogo (sin distinguir mayúsculas). */
  appOf: (entity: string | null | undefined) => AppSummary | undefined;
  /** Módulo del grupo de un catálogo (`group` de un reporte): vacío = General. */
  appOfGroup: (group: string | null | undefined) => AppSummary | undefined;
  /** El de la página actual o, si la página no es de un módulo, el último que usó esta persona. */
  current: AppSummary | undefined;
}

const AppsContext = createContext<AppsState | null>(null);
const eq = (a?: string | null, b?: string | null) => (a ?? '').toLowerCase() === (b ?? '').toLowerCase();

/** Módulo al que pertenece la ruta (null si la página no es de un módulo). */
function routeApp(pathname: string, search: string, appOf: (e: string) => AppSummary | undefined, find: (n: string) => AppSummary | undefined) {
  const m = matchPath('/m/:app', pathname);
  if (m?.params.app) return find(m.params.app);
  const c = matchPath('/c/:entity/*', pathname) ?? matchPath('/reports/:entity/:name', pathname);
  if (c?.params.entity) return appOf(c.params.entity);
  if (matchPath('/reports', pathname) || matchPath('/dashboards', pathname)) {
    const q = new URLSearchParams(search).get('app');
    return q ? find(q) : undefined;
  }
  return undefined;
}

export function AppsProvider({ children }: { children: ReactNode }) {
  const { me, entities } = useAuth();
  const { pathname, search } = useLocation();
  // Se recarga al cambiar de usuario, de empresa o de modelo (la lista de catálogos se vuelve a pedir en cada caso).
  const { data, loading } = useAsync((s) => (me ? modelApi.apps(false, s) : Promise.resolve([])), [me, entities]);
  const apps = useMemo(() => data ?? [], [data]);
  const key = me ? `erp.app.${me.id}` : null;

  const find = useCallback((name: string | null | undefined) => apps.find((a) => eq(a.name, name)), [apps]);
  const appOf = useCallback((entity: string | null | undefined) => apps.find((a) => a.entities.some((e) => eq(e, entity))), [apps]);
  const appOfGroup = useCallback((group: string | null | undefined) => find(group?.trim() ? group.trim() : GENERAL_APP), [find]);

  const fromRoute = routeApp(pathname, search, appOf, find);
  const remembered = key ? find(safeStorageGet(key)) : undefined;
  const current = fromRoute ?? remembered ?? apps[0];

  // H1 CA3: el último módulo de cada usuario (preferencia de presentación, en el navegador).
  useEffect(() => { if (key && fromRoute) safeStorageSet(key, fromRoute.name); }, [key, fromRoute]);

  const value = useMemo<AppsState>(() => ({ apps, loading, find, appOf, appOfGroup, current }),
    [apps, loading, find, appOf, appOfGroup, current]);
  return <AppsContext.Provider value={value}>{children}</AppsContext.Provider>;
}

export function useApps(): AppsState {
  const ctx = useContext(AppsContext);
  if (!ctx) throw new Error('useApps must be used inside AppsProvider');
  return ctx;
}
