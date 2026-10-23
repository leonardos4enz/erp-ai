import { api } from './client';
import type {
  AuditEntry, ChangePlan, Company, ExchangeRate, ChangeSetSummary, DashboardResult, DashboardSummary, EntityDef, HistoryEntry, LayoutState, LookupItem, Me,
  ApiKey, AssistantStatus, Conversation, ConversationSummary, Proposal, EnvironmentInfo, ExportableCatalogs, ExportPreview, ExportRequest, ImportResult, ModuleInfo, PackageImport, ModulePreview, WebhookDelivery, WebhookSummary, Paged, PlanPreview, ReportResult, ReportSummary, Role, Row, RowList, SavedView, User, ViewFilter, ViewList,
} from './types';

const enc = encodeURIComponent;

export const authApi = {
  login: (username: string, password: string) =>
    api<Me>('/auth/login', { method: 'POST', body: { username, password }, skipAuthRedirect: true }),
  logout: () => api<void>('/auth/logout', { method: 'POST', skipAuthRedirect: true }),
  me: () => api<Me>('/auth/me', { skipAuthRedirect: true }),
  /** Spec 010: cambia la empresa actual. */
  switchCompany: (companyId: number) => api<Me>('/auth/company', { method: 'POST', body: { companyId } }),
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
  /** Spec 007: filtros por campo (se envían como JSON). */
  filters?: ViewFilter[];
}

const filtersParam = (f?: ViewFilter[]) => (f && f.length ? JSON.stringify(f) : undefined);

export interface TransitionInput {
  transition: string;
  comment?: string;
  values?: Record<string, unknown>;
  _rowVersion?: string;
}

export const dataApi = {
  list: (entity: string, q: ListQuery, signal?: AbortSignal) =>
    api<RowList>(`/data/${enc(entity)}`, { query: { ...q, filters: filtersParam(q.filters) }, signal }),
  get: (entity: string, id: number | string) => api<Row>(`/data/${enc(entity)}/${enc(String(id))}`),
  lookup: (entity: string, search: string, signal?: AbortSignal, filter?: { field: string; value: unknown }) =>
    api<LookupItem[]>(`/data/${enc(entity)}/lookup`, {
      query: { search, filterField: filter?.field, filterValue: filter ? String(filter.value ?? '') : undefined }, signal,
    }),
  create: (entity: string, values: Record<string, unknown>) =>
    api<Row>(`/data/${enc(entity)}`, { method: 'POST', body: values }),
  update: (entity: string, id: number | string, values: Record<string, unknown>) =>
    api<Row>(`/data/${enc(entity)}/${enc(String(id))}`, { method: 'PUT', body: values }),
  remove: (entity: string, id: number | string) =>
    api<void>(`/data/${enc(entity)}/${enc(String(id))}`, { method: 'DELETE' }),
  /** Calcula valores por defecto y fórmulas de un borrador sin guardarlo. */
  compute: (entity: string, values: Record<string, unknown>, id?: number | string, signal?: AbortSignal) =>
    api<{ values: Record<string, unknown>; layout?: LayoutState | null }>(`/data/${enc(entity)}/compute`, {
      method: 'POST', body: { id: id != null ? Number(id) : undefined, values }, signal,
    }),
  transition: (entity: string, id: number | string, input: TransitionInput) =>
    api<Row>(`/data/${enc(entity)}/${enc(String(id))}/transition`, { method: 'POST', body: input }),
  history: (entity: string, id: number | string, signal?: AbortSignal) =>
    api<HistoryEntry[]>(`/data/${enc(entity)}/${enc(String(id))}/history`, { signal }),
  /** Spec 008: vista previa (dryRun) o importación de un CSV/XLSX; todo o nada. */
  importFile: (entity: string, file: Blob, dryRun: boolean) =>
    api<ImportResult>(`/data/${enc(entity)}/import`, { method: 'POST', rawBody: file, query: { dryRun } }),
  /** Spec 008: URL de descarga del listado como se ve (GET: misma sesión por cookie, sin cabecera CSRF). */
  exportUrl: (entity: string, format: 'csv' | 'xlsx', q: Omit<ListQuery, 'page' | 'pageSize'>, columns: string[]) => {
    const qs = new URLSearchParams({ format });
    if (columns.length) qs.set('columns', columns.join(','));
    if (q.search) qs.set('search', q.search);
    if (q.sort) qs.set('sort', q.sort);
    if (q.dir) qs.set('dir', q.dir);
    if (q.state) qs.set('state', q.state);
    const f = filtersParam(q.filters);
    if (f) qs.set('filters', f);
    return `/api/data/${enc(entity)}/export?${qs.toString()}`;
  },
};

export interface ViewInput {
  label: string;
  columns?: string[];
  filters?: ViewFilter[];
  sort?: string;
  dir?: 'asc' | 'desc';
  state?: string;
}

export const viewsApi = {
  list: (entity: string, signal?: AbortSignal) => api<ViewList>(`/views/${enc(entity)}`, { signal }),
  create: (entity: string, v: ViewInput) => api<SavedView>(`/views/${enc(entity)}`, { method: 'POST', body: v }),
  update: (entity: string, id: number, v: ViewInput) => api<SavedView>(`/views/${enc(entity)}/${id}`, { method: 'PUT', body: v }),
  remove: (entity: string, id: number) => api<void>(`/views/${enc(entity)}/${id}`, { method: 'DELETE' }),
};

export const reportsApi = {
  list: (signal?: AbortSignal) => api<ReportSummary[]>('/reports', { signal }),
  run: (entity: string, name: string, filters?: ViewFilter[], signal?: AbortSignal) =>
    api<ReportResult>(`/reports/${enc(entity)}/${enc(name)}`, { query: { filters: filtersParam(filters) }, signal }),
  /** URL de descarga (misma sesión por cookie; GET no necesita cabecera CSRF). */
  exportUrl: (entity: string, name: string, format: 'csv' | 'xlsx', filters?: ViewFilter[]) => {
    const qs = new URLSearchParams({ format });
    const f = filtersParam(filters);
    if (f) qs.set('filters', f);
    return `/api/reports/${enc(entity)}/${enc(name)}/export?${qs.toString()}`;
  },
};

export const dashboardsApi = {
  list: (signal?: AbortSignal) => api<DashboardSummary[]>('/dashboards', { signal }),
  get: (name: string, signal?: AbortSignal) => api<DashboardResult>(`/dashboards/${enc(name)}`, { signal }),
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
  /** Spec 006: vacío la quita. */
  unit?: string | null;
  /** Spec 010: empresas que puede elegir. */
  companyIds?: number[];
}

export interface CompanyInput { code?: string; name: string; baseCurrency: string; isActive: boolean }
export interface RateInput { from: string; to: string; date: string; rate: number }

/** Spec 010: empresas y tipos de cambio (datos del entorno). */
export const companiesApi = {
  list: (signal?: AbortSignal) => api<Company[]>('/admin/companies', { signal }),
  create: (c: CompanyInput) => api<Company>('/admin/companies', { method: 'POST', body: c }),
  update: (id: number, c: CompanyInput) => api<Company>(`/admin/companies/${id}`, { method: 'PUT', body: c }),
  rates: (q: { from?: string; to?: string }, signal?: AbortSignal) =>
    api<ExchangeRate[]>('/admin/exchange-rates', { query: { from: q.from || undefined, to: q.to || undefined }, signal }),
  saveRate: (r: RateInput) => api<ExchangeRate>('/admin/exchange-rates', { method: 'PUT', body: r }),
  removeRate: (id: number) => api<void>(`/admin/exchange-rates/${id}`, { method: 'DELETE' }),
};

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
  units: () => api<string[]>('/admin/units'),
  createUser: (u: UserInput) => api<User>('/admin/users', { method: 'POST', body: u }),
  updateUser: (id: number, u: UserInput) => api<User>(`/admin/users/${id}`, { method: 'PUT', body: u }),
  roles: () => api<Role[]>('/admin/roles'),
  createRole: (r: RoleInput) => api<Role>('/admin/roles', { method: 'POST', body: r }),
  updateRole: (id: number, r: RoleInput) => api<Role>(`/admin/roles/${id}`, { method: 'PUT', body: r }),
  modules: () => api<ModuleInfo[]>('/admin/modules'),
  previewModule: (id: string) => api<ModulePreview>(`/admin/modules/${enc(id)}/preview`, { method: 'POST' }),
  installModule: (id: string) => api<{ changeSetId: number }>(`/admin/modules/${enc(id)}/install`, { method: 'POST' }),
  previewUpgrade: (id: string) => api<ModulePreview>(`/admin/modules/${enc(id)}/upgrade/preview`, { method: 'POST' }),
  upgradeModule: (id: string) => api<{ changeSetId: number }>(`/admin/modules/${enc(id)}/upgrade`, { method: 'POST' }),
};

export const integrationsApi = {
  webhooks: (signal?: AbortSignal) => api<WebhookSummary[]>('/admin/webhooks', { signal }),
  configure: (entity: string, name: string, input: { url?: string; active: boolean }) =>
    api<{ secret?: string | null }>(`/admin/webhooks/${enc(entity)}/${enc(name)}`, { method: 'PUT', body: input }),
  rotate: (entity: string, name: string) => api<{ secret: string }>(`/admin/webhooks/${enc(entity)}/${enc(name)}/rotate`, { method: 'POST' }),
  test: (entity: string, name: string) =>
    api<{ ok: boolean; status?: number | null; error?: string | null; deliveryId: string }>(`/admin/webhooks/${enc(entity)}/${enc(name)}/test`, { method: 'POST' }),
  removeDestination: (entity: string, name: string) => api<void>(`/admin/webhooks/${enc(entity)}/${enc(name)}`, { method: 'DELETE' }),
  deliveries: (q: { entity?: string; name?: string; status?: string }, signal?: AbortSignal) =>
    api<WebhookDelivery[]>('/admin/webhooks/deliveries', { query: q, signal }),
  retry: (id: number) => api<void>(`/admin/webhooks/deliveries/${id}/retry`, { method: 'POST' }),
  keys: (signal?: AbortSignal) => api<ApiKey[]>('/admin/api-keys', { signal }),
  createKey: (input: { name: string; roleIds: number[]; expiresInDays?: number }) =>
    api<{ key: ApiKey; secret: string }>('/admin/api-keys', { method: 'POST', body: input }),
  updateKey: (id: number, input: { name: string; roleIds: number[] }) => api<ApiKey>(`/admin/api-keys/${id}`, { method: 'PUT', body: input }),
  rotateKey: (id: number, graceHours: 0 | 24) =>
    api<{ key: ApiKey; secret: string }>(`/admin/api-keys/${id}/rotate`, { method: 'POST', body: { graceHours } }),
  revokeKey: (id: number) => api<void>(`/admin/api-keys/${id}/revoke`, { method: 'POST' }),
};

export const auditApi = {
  list: (q: { entity?: string; page: number; pageSize: number }) =>
    api<Paged<AuditEntry>>('/audit', { query: q }),
};

/** Spec 009: paquetes de metadatos entre entornos (solo superusuario). */
export const environmentsApi = {
  info: (signal?: AbortSignal) => api<EnvironmentInfo>('/admin/environments', { signal }),
  exportable: (signal?: AbortSignal) => api<ExportableCatalogs>('/admin/environments/exportable', { signal }),
  previewExport: (input: ExportRequest) => api<ExportPreview>('/admin/environments/export/preview', { method: 'POST', body: input }),
  /** El archivo tal cual lo firmó el servidor (no se vuelve a serializar en el navegador). */
  exportFile: (input: ExportRequest) => api<Blob>('/admin/environments/export', { method: 'POST', body: input, asBlob: true }),
  imports: (signal?: AbortSignal) => api<PackageImport[]>('/admin/environments/imports', { signal }),
  upload: (file: Blob) => api<PackageImport>('/admin/environments/imports', { method: 'POST', rawBody: file }),
  detail: (id: number, signal?: AbortSignal) => api<PackageImport>(`/admin/environments/imports/${id}`, { signal }),
  approve: (id: number, diffHash: string, comment?: string) =>
    api<PackageImport>(`/admin/environments/imports/${id}/approve`, { method: 'POST', body: { diffHash, comment } }),
  reject: (id: number, comment: string) => api<PackageImport>(`/admin/environments/imports/${id}/reject`, { method: 'POST', body: { comment } }),
  apply: (id: number, diffHash: string) =>
    api<{ changeSetId: number | null; import: PackageImport }>(`/admin/environments/imports/${id}/apply`, { method: 'POST', body: { diffHash } }),
};

/** Spec 011: el asistente propone planes; aplicar usa el plan guardado en el servidor. */
export const assistantApi = {
  status: (signal?: AbortSignal) => api<AssistantStatus>('/assistant', { signal }),
  /** El servidor la prueba con Anthropic antes de guardarla; nunca la devuelve. */
  setKey: (apiKey: string) => api<AssistantStatus>('/assistant/key', { method: 'PUT', body: { apiKey } }),
  clearKey: () => api<AssistantStatus>('/assistant/key', { method: 'DELETE' }),
  conversations: (signal?: AbortSignal) => api<ConversationSummary[]>('/assistant/conversations', { signal }),
  conversation: (id: number, signal?: AbortSignal) => api<Conversation>(`/assistant/conversations/${id}`, { signal }),
  request: (prompt: string, conversationId?: number) =>
    api<Proposal>('/assistant/requests', { method: 'POST', body: { prompt, conversationId } }),
  apply: (id: number) => api<{ changeSetId: number; proposal: Proposal }>(`/assistant/requests/${id}/apply`, { method: 'POST' }),
  discard: (id: number) => api<Proposal>(`/assistant/requests/${id}/discard`, { method: 'POST' }),
};
