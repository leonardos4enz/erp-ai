import { api } from './client';
import type {
  AuditEntry, ChangePlan, ChangeSetSummary, EntityDef, HistoryEntry, LookupItem, Me, ModuleInfo,
  ModulePreview, Paged, PlanPreview, Role, Row, RowList, User,
} from './types';

const enc = encodeURIComponent;

export const authApi = {
  login: (username: string, password: string) =>
    api<Me>('/auth/login', { method: 'POST', body: { username, password }, skipAuthRedirect: true }),
  logout: () => api<void>('/auth/logout', { method: 'POST', skipAuthRedirect: true }),
  me: () => api<Me>('/auth/me', { skipAuthRedirect: true }),
};

export const modelApi = {
  entities: (includeArchived = false, includeChildren = false) =>
    api<EntityDef[]>('/model/entities', {
      query: { includeArchived: includeArchived || undefined, includeChildren: includeChildren || undefined },
    }),
  entity: (name: string) => api<EntityDef>(`/model/entities/${enc(name)}`),
  preview: (plan: ChangePlan) => api<PlanPreview>('/model/plan/preview', { method: 'POST', body: plan }),
  apply: (plan: ChangePlan) => api<{ changeSetId: number }>('/model/plan/apply', { method: 'POST', body: plan }),
  changes: () => api<ChangeSetSummary[]>('/model/changes'),
};

export interface ListQuery {
  page: number;
  pageSize: number;
  search?: string;
  sort?: string;
  dir?: 'asc' | 'desc';
  state?: string;
}

export interface TransitionInput {
  transition: string;
  comment?: string;
  values?: Record<string, unknown>;
  _rowVersion?: string;
}

export const dataApi = {
  list: (entity: string, q: ListQuery, signal?: AbortSignal) =>
    api<RowList>(`/data/${enc(entity)}`, { query: { ...q }, signal }),
  get: (entity: string, id: number | string) => api<Row>(`/data/${enc(entity)}/${enc(String(id))}`),
  lookup: (entity: string, search: string, signal?: AbortSignal) =>
    api<LookupItem[]>(`/data/${enc(entity)}/lookup`, { query: { search }, signal }),
  create: (entity: string, values: Record<string, unknown>) =>
    api<Row>(`/data/${enc(entity)}`, { method: 'POST', body: values }),
  update: (entity: string, id: number | string, values: Record<string, unknown>) =>
    api<Row>(`/data/${enc(entity)}/${enc(String(id))}`, { method: 'PUT', body: values }),
  remove: (entity: string, id: number | string) =>
    api<void>(`/data/${enc(entity)}/${enc(String(id))}`, { method: 'DELETE' }),
  /** Calcula valores por defecto y fórmulas de un borrador sin guardarlo. */
  compute: (entity: string, values: Record<string, unknown>, id?: number | string, signal?: AbortSignal) =>
    api<{ values: Record<string, unknown> }>(`/data/${enc(entity)}/compute`, {
      method: 'POST', body: { id: id != null ? Number(id) : undefined, values }, signal,
    }),
  transition: (entity: string, id: number | string, input: TransitionInput) =>
    api<Row>(`/data/${enc(entity)}/${enc(String(id))}/transition`, { method: 'POST', body: input }),
  history: (entity: string, id: number | string, signal?: AbortSignal) =>
    api<HistoryEntry[]>(`/data/${enc(entity)}/${enc(String(id))}/history`, { signal }),
};

export const usersApi = {
  lookup: (search: string, signal?: AbortSignal) => api<LookupItem[]>('/users/lookup', { query: { search }, signal }),
};

export interface UserInput {
  username?: string;
  displayName: string;
  password?: string;
  isActive?: boolean;
  roleIds: number[];
}

export interface RoleInput {
  name: string;
  description?: string;
  canManageModel: boolean;
  canManageUsers: boolean;
  canViewAudit: boolean;
  permissions: Role['permissions'];
}

export const adminApi = {
  users: () => api<User[]>('/admin/users'),
  createUser: (u: UserInput) => api<User>('/admin/users', { method: 'POST', body: u }),
  updateUser: (id: number, u: UserInput) => api<User>(`/admin/users/${id}`, { method: 'PUT', body: u }),
  roles: () => api<Role[]>('/admin/roles'),
  createRole: (r: RoleInput) => api<Role>('/admin/roles', { method: 'POST', body: r }),
  updateRole: (id: number, r: RoleInput) => api<Role>(`/admin/roles/${id}`, { method: 'PUT', body: r }),
  modules: () => api<ModuleInfo[]>('/admin/modules'),
  previewModule: (id: string) => api<ModulePreview>(`/admin/modules/${enc(id)}/preview`, { method: 'POST' }),
  installModule: (id: string) => api<{ changeSetId: number }>(`/admin/modules/${enc(id)}/install`, { method: 'POST' }),
};

export const auditApi = {
  list: (q: { entity?: string; page: number; pageSize: number }) =>
    api<Paged<AuditEntry>>('/audit', { query: q }),
};
