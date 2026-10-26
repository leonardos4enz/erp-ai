import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError, newSessionGeneration, setUnauthorizedHandler } from '../api/client';
import { authApi, modelApi } from '../api/endpoints';
import type { EntityDef, EntityPerms, Me } from '../api/types';

/**
 * Spec 032: por qué terminó la sesión, para decirlo en el login, y de quién era (F-03: solo esa persona regresa a donde
 * estaba). Solo en memoria: recargar lo olvida.
 */
export type SessionEnd = { reason: 'expired' | 'signedOut'; username: string } | null;

interface AuthState {
  me: Me | null;
  loading: boolean;
  sessionEnd: SessionEnd;
  login: (username: string, password: string) => Promise<Me>;
  /** Falso si el servidor no confirmó el cierre (la cookie puede seguir viva): la sesión sigue abierta en la SPA. */
  logout: () => Promise<boolean>;
  /** Entities visible to the current user (read permission or superuser / modeler). */
  entities: EntityDef[];
  entitiesLoading: boolean;
  reloadEntities: () => Promise<void>;
  /** Vuelve a leer permisos y catálogos (p. ej. después de instalar un módulo). */
  refresh: () => Promise<void>;
  /** Spec 010: cambia la empresa actual (recarga permisos, catálogos y la página). */
  switchCompany: (companyId: number) => Promise<void>;
  perms: (entity: string) => EntityPerms;
}

const AuthContext = createContext<AuthState | null>(null);
const NONE: EntityPerms = { read: false, create: false, update: false, delete: false };
const ALL: EntityPerms = { read: true, create: true, update: true, delete: true };

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);
  const [entities, setEntities] = useState<EntityDef[]>([]);
  const [entitiesLoading, setEntitiesLoading] = useState(false);
  const [sessionEnd, setSessionEnd] = useState<SessionEnd>(null);
  const meRef = useRef<Me | null>(null);
  meRef.current = me;

  const perms = useCallback((entity: string): EntityPerms => {
    if (!me) return NONE;
    if (me.isSuperuser) return ALL;
    const key = Object.keys(me.entities).find((k) => k.toLowerCase() === entity.toLowerCase());
    return key ? me.entities[key] : NONE;
  }, [me]);

  const reloadEntities = useCallback(async () => {
    setEntitiesLoading(true);
    try {
      setEntities(await modelApi.entities());
    } catch {
      setEntities([]);
    } finally {
      setEntitiesLoading(false);
    }
  }, []);

  useEffect(() => {
    authApi.me()
      .then(setMe)
      .catch(() => setMe(null))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    if (me) void reloadEntities();
    else setEntities([]);
  }, [me, reloadEntities]);

  useEffect(() => {
    // Un 401 con la sesión abierta es una sesión que expiró (o se revocó); sin sesión, solo es que no hay una.
    setUnauthorizedHandler(() => {
      const previous = meRef.current;
      // F-02: tras «Cerraste sesión» un 401 rezagado no lo cambia por «expiró».
      if (previous) setSessionEnd((end) => (end?.reason === 'signedOut' ? end : { reason: 'expired', username: previous.username }));
      setMe(null);
    });
    return () => setUnauthorizedHandler(null);
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    const user = await authApi.login(username, password);
    newSessionGeneration();
    setSessionEnd(null);
    setMe(user);
    return user;
  }, []);

  const logout = useCallback(async () => {
    // F-01: «Cerraste sesión» solo si el servidor la cerró (204) o ya no había sesión (401). Si falla la red o el servidor,
    // la cookie puede seguir viva: no se le dice a la persona que salió.
    try { await authApi.logout(); } catch (e) {
      if (!(e instanceof ApiError)) throw e;
      if (e.status !== 401) return false;
    }
    newSessionGeneration();
    setSessionEnd({ reason: 'signedOut', username: meRef.current?.username ?? '' });
    setMe(null);
    return true;
  }, []);

  // setMe con un objeto nuevo dispara la recarga de catálogos (efecto de arriba).
  const refresh = useCallback(async () => {
    try { setMe(await authApi.me()); } catch (e) { if (!(e instanceof ApiError)) throw e; }
  }, []);

  const switchCompany = useCallback(async (companyId: number) => {
    setMe(await authApi.switchCompany(companyId));
  }, []);

  const visible = useMemo(
    () => entities
      .filter((e) => !e.isArchived && !e.isChild && (me?.isSuperuser || perms(e.name).read))
      .sort((a, b) => a.labelPlural.localeCompare(b.labelPlural, 'es')),
    [entities, me, perms],
  );

  const value = useMemo<AuthState>(() => ({
    me, loading, sessionEnd, login, logout, entities: visible, entitiesLoading, reloadEntities, refresh, perms, switchCompany,
  }), [me, loading, sessionEnd, login, logout, visible, entitiesLoading, reloadEntities, refresh, perms, switchCompany]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
