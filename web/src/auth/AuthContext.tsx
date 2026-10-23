import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiError, setUnauthorizedHandler } from '../api/client';
import { authApi, modelApi } from '../api/endpoints';
import type { EntityDef, EntityPerms, Me } from '../api/types';

interface AuthState {
  me: Me | null;
  loading: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  /** Entities visible to the current user (read permission or superuser / modeler). */
  entities: EntityDef[];
  entitiesLoading: boolean;
  reloadEntities: () => Promise<void>;
  /** Vuelve a leer permisos y catálogos (p. ej. después de instalar un módulo). */
  refresh: () => Promise<void>;
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
    setUnauthorizedHandler(() => setMe(null));
    return () => setUnauthorizedHandler(null);
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    const user = await authApi.login(username, password);
    setMe(user);
  }, []);

  const logout = useCallback(async () => {
    try { await authApi.logout(); } catch (e) { if (!(e instanceof ApiError)) throw e; }
    setMe(null);
  }, []);

  // setMe con un objeto nuevo dispara la recarga de catálogos (efecto de arriba).
  const refresh = useCallback(async () => {
    try { setMe(await authApi.me()); } catch (e) { if (!(e instanceof ApiError)) throw e; }
  }, []);

  const visible = useMemo(
    () => entities
      .filter((e) => !e.isArchived && !e.isChild && (me?.isSuperuser || perms(e.name).read))
      .sort((a, b) => a.labelPlural.localeCompare(b.labelPlural, 'es')),
    [entities, me, perms],
  );

  const value = useMemo<AuthState>(() => ({
    me, loading, login, logout, entities: visible, entitiesLoading, reloadEntities, refresh, perms,
  }), [me, loading, login, logout, visible, entitiesLoading, reloadEntities, refresh, perms]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
