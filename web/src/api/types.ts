export type FieldType =
  | 'text' | 'longtext' | 'integer' | 'decimal' | 'boolean'
  | 'date' | 'datetime' | 'choice' | 'reference'
  | 'autonumber' | 'formula' | 'lines' | 'parent' | 'state' | 'user';

export type FormulaResultType = 'decimal' | 'integer' | 'text' | 'boolean' | 'date';

export interface FieldDef {
  name: string;
  label: string;
  type: FieldType;
  required?: boolean;
  unique?: boolean;
  maxLength?: number;
  precision?: number;
  scale?: number;
  options?: string[];
  referenceEntity?: string;
  showInList?: boolean;
  requiredForRoles?: string[];
  displayOrder?: number;
  isArchived?: boolean;
  /** Expresión que se aplica cuando el campo está vacío. */
  defaultValue?: string;
  /** Solo para type 'formula'. */
  formula?: string;
  resultType?: FormulaResultType;
  /** Solo para type 'autonumber'. */
  prefix?: string;
  padding?: number;
  /** Solo para type 'lines': entidad de las partidas. */
  childEntity?: string;
  /** Lo calcula el servidor (formula, autonumber, state…). */
  readOnly?: boolean;
  /** false: lo asigna el sistema (al crear o en transiciones), nunca el usuario. */
  editable?: boolean;
  /** Spec 005: el buscador solo ofrece registros con `field` = valor de `source` (campo propio o `CampoPadre.Campo`). */
  lookupFilter?: { field: string; source: string } | null;
  /** Spec 006, solo salida: acceso del usuario actual por rol. */
  access?: FieldAccessLevel;
  /** Spec 006, solo salida: estados en que se captura ([] = solo en las transiciones que lo piden). */
  editableIn?: string[] | null;
  /** Spec 015, solo salida: el campo dueño acepta equipos (`teamOwners`). */
  acceptsTeams?: boolean;
  /** Solo en la UI: bloqueado por el estado del registro (ver `withState`). */
  lockedByState?: boolean;
}

export type FieldAccessLevel = 'hidden' | 'read' | 'edit';

/** Spec 006: quién ve y captura cada campo, y qué registros ve o modifica cada rol. */
export interface FieldSecurity { field: string; read?: string[]; edit?: string[]; editableIn?: string[] }
export interface RecordGrant { roles?: string[]; scope?: 'all' | 'unitTree' | 'unit' | 'own'; where?: string; access?: 'read' | 'write' }
export interface SecurityPolicy {
  fields?: FieldSecurity[];
  owner?: string;
  records?: RecordGrant[];
  systemManaged?: boolean;
  /** Spec 015: el campo dueño acepta un equipo (requiere `owner`). */
  teamOwners?: boolean;
}

/* ---------- Spec 007: metadatos de interfaz ---------- */

export type LayoutItem = string | { field: string; visibleWhen?: string; wide?: boolean };
export interface LayoutSection {
  name: string;
  label: string;
  description?: string;
  tab?: string;
  columns?: 1 | 2;
  visibleWhen?: string;
  fields: LayoutItem[];
}
export interface Layout { sections: LayoutSection[] }
/** Qué oculta el layout para el borrador actual (lo calcula el servidor). */
export interface LayoutState { hiddenSections: string[]; hiddenFields: string[] }

export type FilterOp = 'eq' | 'ne' | 'gt' | 'gte' | 'lt' | 'lte' | 'contains' | 'in' | 'blank' | 'notBlank';
export interface ViewFilter { field: string; op: FilterOp; value?: unknown; /** Solo presentación (p. ej. nombre del proveedor elegido). */ label?: string }
export interface ViewDef {
  name: string;
  label: string;
  roles?: string[];
  columns?: string[];
  filters?: ViewFilter[];
  sort?: string;
  dir?: 'asc' | 'desc';
  state?: string;
  default?: boolean;
}
export interface SavedView extends ViewDef { id?: number | null; personal: boolean }
export interface ViewList { shared: SavedView[]; personal: SavedView[] }

export interface ReportDef {
  name: string;
  label: string;
  description?: string;
  roles?: string[];
  groupBy?: { field: string; period?: 'day' | 'month' | 'year'; label?: string }[];
  measures: { fn: 'count' | 'sum' | 'avg' | 'min' | 'max'; field?: string; label?: string }[];
  filter?: string;
  orderBy?: 'group' | 'measure';
  dir?: 'asc' | 'desc';
  limit?: number;
  chart?: 'bar' | 'line' | 'pie';
}
// ---------- Spec 008: integraciones ----------
export interface WebhookEvent { on: 'create' | 'update' | 'enterState' | 'transition'; state?: string | null; transition?: string | null }
export interface WebhookDef {
  name: string;
  label: string;
  description?: string;
  events: WebhookEvent[];
  when?: string;
  role: string;
  fields?: string[];
}
export interface WebhookSummary {
  entity: string;
  entityLabel: string;
  name: string;
  label: string;
  description?: string | null;
  events: WebhookEvent[];
  role: string;
  fields?: string[] | null;
  configured: boolean;
  active: boolean;
  destination?: string | null;
  secretCreatedAt?: string | null;
  lastStatus?: DeliveryStatus | null;
  lastAt?: string | null;
}
export type DeliveryStatus = 'pending' | 'delivered' | 'failed' | 'canceled';

// ---------- Spec 016: automatizaciones asíncronas y programadas ----------
export interface ScheduleDef {
  name: string;
  label: string;
  description?: string;
  every: 'minutes' | 'hour' | 'day' | 'week' | 'month';
  interval?: number;
  minute?: number;
  at?: string;
  weekdays?: number[];
  day?: number;
  where: string;
  role: string;
  batchSize?: number;
  actions: AutomationAction[];
}
export type RunStatus = 'pending' | 'running' | 'succeeded' | 'partial' | 'failed' | 'canceled';
export interface ScheduleSummary {
  entity: string;
  entityLabel: string;
  name: string;
  label: string;
  description?: string | null;
  calendar: string;
  role: string;
  batchSize: number;
  active: boolean;
  nextRunAt?: string | null;
  lastRun?: { id: number; status: RunStatus; finishedAt?: string | null; processed: number; succeeded: number; failed: number; more: boolean; error?: string | null } | null;
}
export interface AutomationRun {
  id: number;
  kind: 'async' | 'schedule';
  entity: string;
  entityLabel: string;
  name: string;
  label: string;
  recordId?: number | null;
  event?: string | null;
  eventKey?: string | null;
  runAs: string;
  manual: boolean;
  status: RunStatus;
  attempts: number;
  processed: number;
  succeeded: number;
  failed: number;
  skipped: number;
  more: boolean;
  createdAt: string;
  startedAt?: string | null;
  finishedAt?: string | null;
  nextAttemptAt?: string | null;
  lastError?: string | null;
}
export interface AutomationRunDetail { run: AutomationRun; errors: { recordId: number; message: string }[] }
export interface WebhookDelivery {
  id: number;
  deliveryId: string;
  entity: string;
  webhook: string;
  event: string;
  eventKey?: string | null;
  recordId?: number | null;
  status: DeliveryStatus;
  attempts: number;
  lastStatusCode?: number | null;
  lastError?: string | null;
  createdAt: string;
  nextAttemptAt?: string | null;
  deliveredAt?: string | null;
}
export interface ApiKey {
  id: number;
  name: string;
  prefix: string;
  roleIds: number[];
  username: string;
  createdAt: string;
  createdBy: string;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  rotatedAt?: string | null;
  previousExpiresAt?: string | null;
  revokedAt?: string | null;
  status: 'active' | 'expired' | 'revoked';
}
/** Spec 018: cliente OAuth (client credentials). El secreto solo se ve al crearlo o rotarlo. */
export type OAuthScope = 'odata.read' | 'data.read' | 'data.write';
export interface OAuthClient {
  id: number;
  name: string;
  clientId: string;
  roleIds: number[];
  username: string;
  scopes: OAuthScope[];
  tokenMinutes: number;
  createdAt: string;
  createdBy: string;
  rotatedAt?: string | null;
  previousExpiresAt?: string | null;
  revokedAt?: string | null;
  lastTokenAt?: string | null;
  activeTokens: number;
  status: 'active' | 'revoked';
}
export interface ImportColumn { column: string; field?: string | null; label?: string | null; status: 'ok' | 'ignored' | 'error'; message?: string | null }
export interface ImportResult {
  dryRun: boolean;
  format: 'csv' | 'xlsx';
  total: number;
  imported: number;
  valid: boolean;
  columns: ImportColumn[];
  errors: { row: number; column?: string | null; field?: string | null; message: string }[];
  truncatedErrors: boolean;
}

export interface ReportSummary { entity: string; entityLabel: string; group?: string | null; name: string; label: string; description?: string | null; chart?: string | null }
export interface ReportColumn { key: string; label: string; kind: 'group' | 'measure'; type: 'text' | 'number' | 'date' | 'boolean'; scale?: number | null }
export interface ReportResult {
  entity: string;
  name: string;
  label: string;
  description?: string | null;
  chart?: 'bar' | 'line' | 'pie' | null;
  columns: ReportColumn[];
  rows: Record<string, string | number | boolean | null>[];
  totals: Record<string, number | string | null>;
  truncated: boolean;
}
export interface DashboardWidgetDef { type: 'kpi' | 'chart' | 'table'; label?: string; entity: string; report: string; measure?: number; wide?: boolean }
export interface DashboardDef { name: string; label: string; icon?: string; description?: string; roles?: string[]; widgets: DashboardWidgetDef[] }
export interface DashboardSummary { name: string; label: string; icon: string; description?: string | null }
export interface DashboardResult {
  name: string;
  label: string;
  icon: string;
  description?: string | null;
  widgets: { type: 'kpi' | 'chart' | 'table'; label: string; wide: boolean; entity: string; report: string; measure?: number | null; result: ReportResult }[];
  definition?: DashboardDef | null;
}

export type StateColor = 'neutral' | 'accent' | 'success' | 'warning' | 'danger';

export interface WorkflowState { name: string; label: string; color?: StateColor; editable?: boolean }

export interface WorkflowTransitionDef {
  name: string;
  label: string;
  from: string[];
  to: string;
  roles?: string[];
  when?: string;
  whenMessage?: string;
  requireComment?: boolean;
  requiredFields?: string[];
  set?: { field: string; value: string }[];
  icon?: string;
  variant?: 'primary' | 'secondary' | 'danger';
  /** Spec 005: solo la ejecuta una automatización (no aparece como botón). */
  system?: boolean;
}

export interface Workflow { initial: string; states: WorkflowState[]; transitions: WorkflowTransitionDef[] }

export type AutomationEvent = 'create' | 'update' | 'enterState' | 'transition';

export interface AutomationAction {
  type: 'update' | 'transition' | 'create' | 'rollup' | 'cost';
  lines?: string;
  when?: string;
  target?: string;
  set?: { field: string; value: string }[];
  transition?: string;
  entity?: string;
  match?: { field: string; source: string }[];
  createMissing?: boolean;
  field?: string;
  sum?: string;
  filter?: string;
  /** Spec 020: partidas del documento que crea una acción create. */
  createLines?: { field: string; when?: string; set: { field: string; value: string }[] }[];
  /** Spec 020: valuación con capas (type: cost). */
  quantity?: string;
  method?: string;
  unitCost?: string;
  fallbackCost?: string;
  costField?: string;
  unitCostField?: string;
}

/** Spec 020: libro de partida doble (cuadre y periodos los garantiza el motor). */
export interface LedgerDef {
  lines: string;
  debit: string;
  credit: string;
  date?: string;
  states?: string[];
  period?: { entity: string; start: string; end: string; open: string };
}

/** Spec 005: «cuando pase X en este catálogo, haz Y en otros registros». */
export interface Automation {
  name: string;
  label: string;
  on: AutomationEvent;
  state?: string;
  transition?: string;
  when?: string;
  actions: AutomationAction[];
  /** Spec 016: async = las acciones corren después, desde la bandeja. */
  mode?: 'sync' | 'async';
}

export interface Rule { name: string; message: string; assert: string; when?: string; field?: string }

export interface EntityDef {
  /** Spec 021: versión de la definición (solo para modeladores); el editor la manda con su plan. */
  version?: string;
  name: string;
  label: string;
  labelPlural: string;
  icon: string;
  description?: string;
  displayField?: string;
  /** Sección del menú (módulo). */
  group?: string;
  isArchived: boolean;
  fields: FieldDef[];
  workflow?: Workflow | null;
  rules?: Rule[] | null;
  automations?: Automation[] | null;
  /** Spec 006: solo la reciben los modeladores. */
  security?: SecurityPolicy | null;
  /** Spec 006: las personas solo consultan; lo escribe el motor. */
  systemManaged?: boolean;
  /** Spec 007: secciones y pestañas del formulario. */
  layout?: Layout | null;
  /** Spec 007: solo la reciben los modeladores. */
  views?: ViewDef[] | null;
  reports?: ReportDef[] | null;
  /** Spec 008: solo la reciben los modeladores. */
  webhooks?: WebhookDef[] | null;
  /** Spec 016: solo la reciben los modeladores. */
  schedules?: ScheduleDef[] | null;
  /** Spec 020: libro de partida doble. */
  ledger?: LedgerDef | null;
  /** Spec 010: un registro por empresa (las partidas siguen a su documento). */
  perCompany?: boolean;
  isChild?: boolean;
  parentEntity?: string;
}

export interface EntityPerms { read: boolean; create: boolean; update: boolean; delete: boolean }

export interface Me {
  id: number;
  username: string;
  displayName: string;
  roles: string[];
  isSuperuser: boolean;
  canManageModel: boolean;
  canManageUsers: boolean;
  canViewAudit: boolean;
  entities: Record<string, EntityPerms>;
  unit?: string | null;
  /** Spec 010: empresa en la que trabaja y las que puede elegir. */
  company?: CompanyInfo | null;
  companies: CompanyInfo[];
}

/** Spec 010: empresa del entorno (dato, no metadato). */
export interface CompanyInfo { id: number; code: string; name: string; baseCurrency: string }
export interface Company extends CompanyInfo { isDefault: boolean; isActive: boolean; users: number }
export interface ExchangeRate { id: number; from: string; to: string; date: string; rate: number; updatedBy: string; updatedAt: string }

export type Operation =
  | { op: 'createEntity'; name: string; label: string; labelPlural: string; icon?: string; description?: string; displayField?: string; group?: string; perCompany?: boolean; fields: FieldDef[] }
  | { op: 'updateEntity'; entity: string; label?: string; labelPlural?: string; icon?: string; description?: string; displayField?: string; group?: string; perCompany?: boolean }
  | { op: 'archiveEntity'; entity: string }
  | { op: 'addField'; entity: string; field: FieldDef }
  | { op: 'updateField'; entity: string; field: string; label?: string; required?: boolean; showInList?: boolean; options?: string[]; requiredForRoles?: string[]; displayOrder?: number; defaultValue?: string; formula?: string; editable?: boolean; lookupFilter?: { field: string; source: string } }
  | { op: 'archiveField'; entity: string; field: string }
  | { op: 'restoreEntity'; entity: string }
  | { op: 'restoreField'; entity: string; field: string }
  | { op: 'setWorkflow'; entity: string; workflow: Workflow }
  | { op: 'setRules'; entity: string; rules: Rule[] }
  | { op: 'setAutomations'; entity: string; automations: Automation[]; merge?: boolean }
  | { op: 'setSecurity'; entity: string; security: SecurityPolicy | null }
  | { op: 'setLayout'; entity: string; layout: Layout | null }
  | { op: 'setViews'; entity: string; views: ViewDef[] }
  | { op: 'setReports'; entity: string; reports: ReportDef[] }
  | { op: 'setWebhooks'; entity: string; webhooks: WebhookDef[] }
  | { op: 'setSchedules'; entity: string; schedules: ScheduleDef[] }
  | { op: 'setLedger'; entity: string; ledger: LedgerDef | null }
  | { op: 'setDashboard'; dashboard: DashboardDef }
  | { op: 'archiveDashboard'; name: string };

/** Spec 021: `baseVersions` = versión de cada catálogo al abrirlo; `baseHash` = huella de la vista previa revisada. */
export interface ChangePlan { description: string; operations: Operation[]; baseVersions?: Record<string, string>; baseHash?: string }

export interface PlanPreview {
  valid: boolean;
  errors: { operation: number; message: string }[];
  steps: { operation: number; description: string; sql?: string }[];
  /** Spec 021: huella de lo que el plan toca tal como estaba; aplicar la exige. */
  baseHash?: string | null;
}

export interface ChangeSetSummary {
  id: number;
  description: string;
  appliedAt: string;
  appliedBy: string;
  operationCount: number;
}

export type RowValue = string | number | boolean | null;
export interface Row {
  id: number;
  _rowVersion: string;
  createdAt: string;
  createdBy: string | number | null;
  updatedAt: string | null;
  updatedBy: string | number | null;
  [field: string]: unknown;
}

export interface Paged<T> { items: T[]; total: number }

export interface StateCount { state: string; label: string; color?: StateColor; count: number }

export interface RowList extends Paged<Row> { stateCounts?: StateCount[] }

/** Transición disponible para el registro y el usuario actual (calculada por el servidor). */
export interface RecordTransition {
  name: string;
  label: string;
  icon?: string;
  variant?: 'primary' | 'secondary' | 'danger';
  to: string;
  allowed: boolean;
  reason?: string;
  requireComment: boolean;
  requiredFields: string[];
}

/** Spec 013: lo que el usuario puede hacer con este registro (rol, alcance de escritura y catálogo del sistema). */
export interface RecordAccess {
  update: boolean;
  delete: boolean;
  reason: string | null;
}

export interface RecordWorkflow {
  state: string;
  stateLabel: string;
  color?: StateColor;
  editable: boolean;
  transitions: RecordTransition[];
}

export interface HistoryEntry {
  at: string;
  user: string;
  from: string | null;
  fromLabel: string | null;
  to: string;
  toLabel: string;
  transition: string;
  transitionLabel: string;
  comment?: string | null;
}

export interface ModuleInfo {
  id: string;
  name: string;
  description?: string;
  icon?: string;
  version: string;
  installed: boolean;
  entities: string[];
  installedVersion?: string | null;
  upgradeAvailable?: boolean;
  pendingUpgrades?: string[];
  requires?: string[];
  missingRequirements?: string[];
}

export type ModulePreview = PlanPreview & { roles: string[]; seedRecords: number };

export interface LookupItem { id: number; label: string }

export interface EntityPermission {
  entity: string;
  canRead: boolean;
  canCreate: boolean;
  canUpdate: boolean;
  canDelete: boolean;
}

export interface Role {
  id: number;
  name: string;
  description?: string;
  isSuperuser?: boolean;
  canManageModel: boolean;
  canManageUsers: boolean;
  canViewAudit: boolean;
  permissions: EntityPermission[];
  /** Spec 015: roles de los que hereda. */
  inheritsRoleIds: number[];
}

export interface User {
  id: number;
  username: string;
  displayName: string;
  isActive: boolean;
  roleIds: number[];
  roles?: string[];
  /** Spec 006: unidad (área o sucursal) para el alcance «de mi unidad». */
  unit?: string | null;
  lockoutEnd?: string | null;
  /** Spec 010: empresas que puede elegir. */
  companyIds: number[];
}

export interface AuditEntry {
  id: number;
  at: string;
  user: string;
  action: string;
  entityName: string;
  recordId: string | number | null;
  before: unknown;
  after: unknown;
}

/* ---------------- Spec 009: entornos ---------------- */

export interface EnvironmentInfo {
  name: string;
  requireApproval: boolean;
  signing: boolean;
  /** Spec 014: el servidor exige aprobación pero permite cambios directos (Environments:AllowDirectChanges). */
  directChangesOverride: boolean;
  packages: { id: string; version: string; installedAt: string }[];
}

export interface ExportableCatalogs {
  entities: { name: string; label: string; labelPlural: string; group?: string | null; children: string[] }[];
  dashboards: { name: string; label: string }[];
  exports: { packageId: string; name: string; version: string; hash: string; exportedAt: string; exportedBy: string }[];
}

export interface ExportRequest {
  id: string;
  name: string;
  version: string;
  description?: string;
  entities: string[];
  dashboards: string[];
}

export interface PackageRole {
  name: string;
  isNew: boolean;
  canViewAudit: boolean;
  permissions: { entity: string; label: string; canRead: boolean; canCreate: boolean; canUpdate: boolean; canDelete: boolean }[];
}

export interface ExportPreview extends PlanPreview {
  operations: number;
  entities: string[];
  roles: PackageRole[];
  dependencies: string[];
  warnings: string[];
  hash: string;
  signed: boolean;
}

export type PackageStatus = 'pending' | 'approved' | 'rejected' | 'applied' | 'reverted';

export interface PackageImport {
  id: number;
  packageId: string;
  name: string;
  version: string;
  previousVersion?: string | null;
  status: PackageStatus;
  hash: string;
  signed: boolean;
  sourceEnvironment?: string | null;
  exportedAt?: string | null;
  uploadedBy: string;
  uploadedAt: string;
  reviewedBy?: string | null;
  reviewedAt?: string | null;
  reviewComment?: string | null;
  appliedBy?: string | null;
  appliedAt?: string | null;
  changeSetId?: number | null;
  description?: string | null;
  installedVersion?: string | null;
  /** Spec 014: `localArchives` = campos que solo existen aquí y se archivarán (aprobar y aplicar piden confirmarlo). */
  /** Spec 021: `securityChanges` = lo que una reversión cambia de seguridad (aprobar y aplicar piden confirmarlo). */
  diff?: (PlanPreview & { conflicts: string[]; warnings: string[]; roles: PackageRole[]; diffHash: string; empty: boolean; localArchives: string[];
    securityChanges: string[] }) | null;
  canApprove: boolean;
  canApply: boolean;
  blockedReason?: string | null;
  /** Spec 017: `revert` = solicitud de reversión de `revertOf` (deja `targetVersion`; nula = sin instalar). */
  kind: 'package' | 'revert';
  revertOf?: number | null;
  targetVersion?: string | null;
  /** En un paquete revertido: la solicitud que lo deshizo. */
  revertedBy?: number | null;
  canRevert: boolean;
  revertBlockedReason?: string | null;
  snapshot?: { beforeHash: string; afterHash: string } | null;
}

// ---------- Spec 011: asistente IA ----------
export interface AssistantStatus {
  configured: boolean; model: string | null; maxPromptChars: number; requestsPerHour: number; remaining: number;
  /** server: user-secrets o variable de entorno; app: guardada por un administrador desde esta pantalla. */
  keySource: 'server' | 'app' | null; keyUpdatedAt?: string; keyUpdatedBy?: string;
  /** Espacio de trabajo de Anthropic (solo con claves de organización). */
  workspaceId?: string; canConfigure: boolean;
  /** Spec 012: falso en un entorno con aprobación (los cambios llegan como paquete). */
  directChanges: boolean;
}
/** Spec 012: «pending» es una solicitud que espera al proveedor (reserva su lugar en el límite por hora). */
export type ProposalStatus = 'proposed' | 'invalid' | 'answer' | 'failed' | 'applied' | 'discarded' | 'superseded' | 'pending';
export interface Proposal {
  id: number;
  conversationId: number;
  prompt: string;
  status: ProposalStatus;
  explanation?: string;
  plan?: ChangePlan;
  preview?: PlanPreview;
  warnings: string[];
  errors: string[];
  attempts: number;
  model: string;
  createdAt: string;
  appliedAt?: string;
  changeSetId?: number;
  error?: string;
  canApply: boolean;
  /** Spec 012: huella de lo revisado (plan, pasos y advertencias); aplicar la envía. */
  reviewHash?: string;
  /** Spec 012: por qué no se aplica aquí aunque sea válida (entorno con aprobación). */
  blockedReason?: string;
}
export interface ConversationSummary { id: number; title: string; turns: number; updatedAt: string; lastStatus: ProposalStatus }
export interface Conversation { id: number; turns: Proposal[] }

/* ---------- Spec 015: unidades, equipos y solicitudes de cambios de seguridad ---------- */

export interface BusinessUnit { id: number; name: string; parentId: number | null; depth: number; users: number; teams: number }
export interface Team { id: number; name: string; unit?: string | null; isActive: boolean; memberIds: number[] }

export type SecurityChangeKind = 'user.create' | 'user.update' | 'role.create' | 'role.update' | 'team.create' | 'team.update' | 'unit.move' | 'apikey.create' | 'apikey.update' | 'oauthclient.create' | 'oauthclient.update';

export interface SecurityChange {
  id: number;
  kind: SecurityChangeKind;
  targetId: number | null;
  summary: string;
  status: 'pending' | 'applying' | 'applied' | 'rejected';
  before: Record<string, unknown> | null;
  after: Record<string, unknown>;
  hash: string;
  requestedBy: string;
  requestedAt: string;
  reviewedBy?: string | null;
  reviewedAt?: string | null;
  reviewComment?: string | null;
  canApprove: boolean;
  canReject: boolean;
}

/** Respuesta 202: el cambio amplía acceso y espera la aprobación de otro administrador. */
export interface PendingChange { securityChange: SecurityChange; secret?: string }

export const isPending = (x: unknown): x is PendingChange => typeof x === 'object' && x !== null && 'securityChange' in x;
