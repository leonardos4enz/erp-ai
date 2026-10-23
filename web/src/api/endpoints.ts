import { api } from './client';
import type {
  AuditEntry, ChangePlan, ChangeSetSummary, EntityDef, LookupItem, Me, Paged,
  PlanPreview, Role, Row, User,
} from './types';

const enc = encodeURIComponent;

export const authApi = {
  login: (username: string, password: string) =>
    api<Me>('/auth/login', { method: 'POST', body: { username, password }, skipAuthRedirect: true }),
  logout: () => api<void>('/auth/logout', { method: 'POST', skipAuthRedirect: true }),
  me: () => api<Me>('/auth/me', { skipAuthRedirect: true }),
};

export const modelApi = {
  entities: (includeArchived = false) =>
    api<EntityDef[]>('/model/entities', { query: { includeArchived: includeArchived || undefined } }),
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
}

export const dataApi = {
  list: (entity: string, q: ListQuery, signal?: AbortSignal) =>
    api<Paged<Row>>(`/data/${enc(entity)}`, { query: { ...q }, signal }),
  get: (entity: string, id: number | string) => api<Row>(`/data/${enc(entity)}/${enc(String(id))}`),
  lookup: (entity: string, search: string, signal?: AbortSignal) =>
    api<LookupItem[]>(`/data/${enc(entity)}/lookup`, { query: { search }, signal }),
  create: (entity: string, values: Record<string, unknown>) =>
    api<Row>(`/data/${enc(entity)}`, { method: 'POST', body: values }),
  update: (entity: string, id: number | string, values: Record<string, unknown>) =>
    api<Row>(`/data/${enc(entity)}/${enc(String(id))}`, { method: 'PUT', body: values }),
  remove: (entity: string, id: number | string) =>
    api<void>(`/data/${enc(entity)}/${enc(String(id))}`, { method: 'DELETE' }),
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
};

export const auditApi = {
  list: (q: { entity?: string; page: number; pageSize: number }) =>
    api<Paged<AuditEntry>>('/audit', { query: q }),
};
