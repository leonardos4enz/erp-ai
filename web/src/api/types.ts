export type FieldType =
  | 'text' | 'longtext' | 'integer' | 'decimal' | 'boolean'
  | 'date' | 'datetime' | 'choice' | 'reference'
  | 'autonumber' | 'formula' | 'lines' | 'parent' | 'state' | 'user'
  // Spec 050: varios registros de un catálogo o varias personas.
  | 'multireference' | 'multiuser';

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
  /** Spec 045: expresión del documento que debe cumplirse para eliminarlo, y el mensaje si no. */
  deletableWhen?: string;
  deleteMessage?: string;
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
  groupBy?: { field: string; period?: ReportPeriod; label?: string }[];
  /** Spec 039: `calc` lleva `expr` sobre los `name` de otras medidas y `scale`. */
  measures: { fn: 'count' | 'sum' | 'avg' | 'min' | 'max' | 'calc'; field?: string; label?: string; name?: string; expr?: string; scale?: number }[];
  filter?: string;
  orderBy?: 'group' | 'measure';
  dir?: 'asc' | 'desc';
  limit?: number;
  chart?: ChartKind;
  chartOptions?: ChartOptions;
}
/** Spec 039: periodos de una agrupación por fecha. */
export type ReportPeriod = 'day' | 'week' | 'month' | 'quarter' | 'year';
/** Spec 039: tipos de gráfica (`kpi`: indicador, con comparación si el reporte va por periodo). */
export type ChartKind = 'column' | 'bar' | 'line' | 'area' | 'pie' | 'donut' | 'combo' | 'kpi';
/** Spec 039: cómo se dibuja la gráfica de un reporte (ver plan 039 §1). */
export interface ChartOptions { measures?: number[]; lines?: number[]; stacked?: boolean; colors?: string[]; values?: boolean; legend?: boolean | null }
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
  /** Spec 023: avisos por fila (la fila se importa igual). */
  warnings?: { row: number; column?: string | null; field?: string | null; message: string }[];
}

export interface ReportSummary { entity: string; entityLabel: string; group?: string | null; name: string; label: string; description?: string | null; chart?: string | null }
export interface ReportColumn { key: string; label: string; kind: 'group' | 'measure'; type: 'text' | 'number' | 'date' | 'boolean'; scale?: number | null; period?: ReportPeriod | null; fn?: string | null }
/** Spec 039 (H4): último periodo con datos y el inmediato anterior (sin registros: conteo y suma en 0). */
export interface ReportComparison {
  current: { label: string; values: Record<string, number | string | null> };
  previous?: { label: string; values: Record<string, number | string | null> } | null;
}
export interface ReportResult {
  entity: string;
  name: string;
  label: string;
  description?: string | null;
  chart?: ChartKind | null;
  chartOptions?: ChartOptions | null;
  comparison?: ReportComparison | null;
  columns: ReportColumn[];
  rows: Record<string, string | number | boolean | null>[];
  totals: Record<string, number | string | null>;
  truncated: boolean;
  /** Spec 041 (H1): solo en la pantalla de un reporte del modelo, su definición para explorarlo. */
  definition?: PersonalReportDef | null;
  /** Spec 041 (H1 CA2): el reporte del modelo tiene un filtro fijo que el explorador no copia. */
  fixedFilter?: boolean;
}
/** Spec 041: definición de un reporte personal o explorado (la de `ReportDef` con filtros del listado, sin roles ni filtro fijo). */
export interface PersonalReportDef {
  entity: string;
  groupBy?: ReportDef['groupBy'];
  measures: ReportDef['measures'];
  filters?: ViewFilter[];
  orderBy?: 'group' | 'measure';
  dir?: 'asc' | 'desc';
  limit?: number;
  chart?: ChartKind;
  chartOptions?: ChartOptions;
}
export interface ReportSource { entity: string; label: string; group?: string | null; parent?: string | null }
export interface ReportSourceField { path: string; label: string; type: string; kind: 'text' | 'longtext' | 'number' | 'date' | 'boolean' | 'id' }
export interface ReportSourceFields { entity: string; label: string; fields: ReportSourceField[] }
export interface PersonalReportSummary { id: number; label: string; entity: string; entityLabel: string; chart?: ChartKind | null; version: number; available: boolean; updatedAt: string; shared: boolean }
/** Spec 042: `definition` nula si es compartido y no corre para quien lo abre; `roles` y `shareableRoles`, solo al dueño. */
export interface PersonalReport {
  id: number; label: string; version: number; definition: PersonalReportDef | null; available: boolean; problems: string[];
  owned: boolean; owner?: string | null; roles?: string[] | null; shareableRoles?: string[] | null;
}
/** Spec 042: reporte personal que otro compartió con uno de mis roles (catálogo solo si corre para mí). */
export interface SharedReportSummary { id: number; label: string; owner: string; entity?: string | null; entityLabel?: string | null; chart?: ChartKind | null; available: boolean; updatedAt: string }
export interface ShareResult { id: number; version: number; roles: string[] }
/** Spec 042: tarjeta de un tablero personal: un reporte del modelo (`entity` + `report`) o uno personal (`personalReport`). */
export interface PersonalWidget { type: 'kpi' | 'chart' | 'table'; label?: string | null; entity?: string | null; report?: string | null; personalReport?: number | null; wide?: boolean }
export interface PersonalDashboardSummary { id: number; label: string; description?: string | null; version: number; shared: boolean; updatedAt: string }
export interface SharedDashboardSummary { id: number; label: string; description?: string | null; owner: string; updatedAt: string }
export interface PersonalDashboardList { mine: PersonalDashboardSummary[]; shared: SharedDashboardSummary[] }
export interface PersonalCard {
  type: 'kpi' | 'chart' | 'table'; label: string; wide: boolean; entity?: string | null; report?: string | null; personalReport?: number | null;
  link?: string | null; available: boolean; problems: string[]; result?: ReportResult | null;
}
export interface PersonalDashboardResult {
  id: number; label: string; description?: string | null; version: number; owned: boolean; owner: string;
  roles?: string[] | null; shareableRoles?: string[] | null; widgets?: PersonalWidget[] | null; cards: PersonalCard[];
}
export interface DashboardWidgetDef { type: 'kpi' | 'chart' | 'table'; label?: string; entity: string; report: string; measure?: number; wide?: boolean }
export interface DashboardDef { name: string; label: string; icon?: string; description?: string; roles?: string[]; widgets: DashboardWidgetDef[] }
/** Spec 024: `apps`, módulos de los catálogos de sus tarjetas visibles. */
export interface DashboardSummary { name: string; label: string; icon: string; description?: string | null; apps?: string[] }
/** Spec 024: módulo de navegación tal como lo ve el usuario (`isArchived` solo en la lista del modelador). */
export interface AppSummary { name: string; label: string; icon: string; description?: string | null; order: number; declared: boolean; entities: string[]; isArchived?: boolean | null }
export interface AppDef { name: string; label: string; icon?: string; description?: string; order?: number }
/** Spec 024: documento encontrado por folio. */
export interface SearchHit { entity: string; entityLabel: string; id: number; label: string }
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
  /** Spec 023: si al ejecutarla quedan avisos: confirmar (omitido), bloquear o continuar. */
  onWarning?: 'confirm' | 'block' | 'allow';
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
  createLines?: { field: string; when?: string; set: { field: string; value: string }[]; from?: string }[];
  /** Spec 020: valuación con capas (type: cost). */
  quantity?: string;
  method?: string;
  unitCost?: string;
  fallbackCost?: string;
  costField?: string;
  unitCostField?: string;
  /** Spec 022: políticas de valuación, costo total exacto y cantidad costeada en el saldo (type: cost). */
  uncostedEntry?: 'average' | 'fallback' | 'require';
  methodChange?: 'merge' | 'keep';
  totalCost?: string;
  quantityField?: string;
  /** Spec 022: valores del registro creado que se guardan en el que disparó la acción (type: create). */
  capture?: { field: string; value: string }[];
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

/* ---------- Spec 027: plantillas de impresión ---------- */
export type PrintBlockType = 'header' | 'text' | 'fields' | 'table' | 'totals' | 'image' | 'separator' | 'signatures' | 'pageBreak' | 'footer';
export type PrintFormat = 'text' | 'number' | 'integer' | 'money' | 'percent' | 'date' | 'dateLong' | 'datetime';
export type PrintAlign = 'left' | 'center' | 'right';
/** Un valor impreso: `value` es una expresión en el ámbito del bloque (la partida en una tabla). */
export interface PrintValue { label: string; value: string; format?: PrintFormat; decimals?: number; currency?: string; width?: number; align?: PrintAlign; bold?: boolean }
export interface PrintSignature { label: string; name?: string }
export interface PrintBlock {
  type: PrintBlockType;
  visibleWhen?: string;
  logo?: boolean; company?: boolean; title?: string; subtitle?: string;
  text?: string; size?: number; bold?: boolean; italic?: boolean; align?: PrintAlign; pageNumbers?: boolean;
  items?: PrintValue[]; perRow?: number;
  lines?: string; columns?: PrintValue[];
  file?: string; width?: number;
  signatures?: PrintSignature[];
}
export interface PrintPage { size?: 'letter' | 'a4'; orientation?: 'portrait' | 'landscape'; margin?: number }
export interface PrintTemplate {
  name: string; label: string; description?: string; roles?: string[]; companies?: string[]; when?: string; default?: boolean;
  page?: PrintPage; blocks: PrintBlock[];
}
export interface PrintChoice { name: string; label: string; description?: string | null; default: boolean }
/** Spec 027: imagen de plantilla (dato del entorno, por clave). */
export interface StoredImage { id: number; key: string; contentType: string; size: number; width: number; height: number; sha256: string; createdBy: string; createdAt: string; url: string }

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

/** Spec 023: severity 'warning' = aviso (no impide guardar); omitido = error. */
export interface Rule { name: string; message: string; assert: string; when?: string; field?: string; severity?: 'error' | 'warning' }

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
  /** Spec 027: plantillas de impresión (solo la reciben los modeladores). */
  printTemplates?: PrintTemplate[] | null;
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
export interface Company extends CompanyInfo { isDefault: boolean; isActive: boolean; users: number; taxId?: string | null; address?: string | null; logoUrl?: string | null }
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
  | { op: 'setRules'; entity: string; rules: Rule[]; merge?: boolean }
  | { op: 'setAutomations'; entity: string; automations: Automation[]; merge?: boolean }
  | { op: 'setSecurity'; entity: string; security: SecurityPolicy | null }
  | { op: 'setLayout'; entity: string; layout: Layout | null }
  | { op: 'setViews'; entity: string; views: ViewDef[]; merge?: boolean }
  | { op: 'setReports'; entity: string; reports: ReportDef[]; merge?: boolean }
  | { op: 'setWebhooks'; entity: string; webhooks: WebhookDef[]; merge?: boolean }
  | { op: 'setSchedules'; entity: string; schedules: ScheduleDef[]; merge?: boolean }
  | { op: 'setLedger'; entity: string; ledger: LedgerDef | null }
  | { op: 'setPrintTemplates'; entity: string; templates: PrintTemplate[]; merge?: boolean }
  | { op: 'setDashboard'; dashboard: DashboardDef }
  | { op: 'archiveDashboard'; name: string }
  | { op: 'setApp'; app: AppDef }
  | { op: 'archiveApp'; name: string };

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

export type ModulePreview = PlanPreview & { roles: string[]; seedRecords: number; conflicts?: UpgradeConflict[] };

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

// ---------- Spec 035: ERP AI Studio (contrato de la 033) ----------
export interface StudioLimits {
  maxSteps: number; maxToolCalls: number; maxTokens: number; maxMinutes: number; maxCostUsd: number; costPerDayUsd: number;
  sessionsPerDay: number; maxPromptChars: number;
  /** Spec 036: mensajes de la persona por conversación. */
  maxTurns: number;
}
export interface StudioStatusInfo {
  configured: boolean; model: string | null; sandboxAvailable: boolean; limits: StudioLimits; sessionsToday: number;
  costTodayUsd: number; active: number | null; directChanges: boolean;
}
export type StudioSessionStatus = 'queued' | 'running' | 'proposed' | 'answered' | 'failed' | 'cancelled' | 'interrupted' | 'applied' | 'discarded';
export type StudioScope = 'modelo' | 'datos' | 'aislado';
export interface StudioSummary {
  id: number; title: string; status: StudioSessionStatus; createdAt: string; finishedAt?: string; steps: number; costUsd: number;
  /** Spec 036: la lista viene ordenada por `updatedAt` (última actividad). */
  turns: number; updatedAt: string;
  /** Spec 044: `mcp` si la entregó un cliente MCP. */
  origin?: 'studio' | 'mcp';
}
export type StudioPlanStatus = 'proposed' | 'applied' | 'discarded';
/** Un paso del agente: texto del modelo, herramienta, entrega o tope. Entrada y salida son datos no confiables. */
export interface StudioStep {
  /** Spec 036: `user` es un mensaje de la persona (`output.text`); `turn`, el turno de la conversación. Spec 046: `action`, una
   *  propuesta del Acompañante (`output.accion`, su id). */
  seq: number; kind: 'user' | 'answer' | 'text' | 'tool' | 'deliver' | 'action' | 'limit' | 'error'; tool?: string | null; status: 'ok' | 'error' | 'denied';
  summary: string; input?: unknown; output?: unknown; durationMs: number; at: string; turn?: number;
}
export interface StudioTest { description: string; ok: boolean; detail?: string | null }
export interface StudioReport { tested: StudioTest[]; failures: string[]; risks: string[] }
export interface StudioSession {
  id: number; prompt: string; scopes: StudioScope[]; status: StudioSessionStatus; error?: string | null; model: string;
  steps: number; toolCalls: number; inputTokens: number; outputTokens: number; costUsd: number;
  createdAt: string; startedAt?: string | null; finishedAt?: string | null; summary?: string | null; report?: StudioReport | null;
  /** Solo en la lectura completa (sin `after`): con `after` no se compila (spec 035, R-033-10). */
  plan?: ChangePlan | null; preview?: PlanPreview | null; warnings: string[]; reviewHash?: string | null;
  canApply: boolean; blockedReason?: string | null; appliedAt?: string | null; changeSetId?: number | null; trail: StudioStep[];
  /** Spec 036: mensajes de la persona; la propuesta vigente (o aplicada/descartada); si se puede mandar otro mensaje y por qué no. */
  turns: number; planStatus?: StudioPlanStatus | null; canSend: boolean; sendBlockedReason?: string | null;
  /** Spec 044: de dónde llegó (el agente de Studio o un cliente MCP) y el nombre del cliente. */
  origin?: 'studio' | 'mcp'; client?: string | null;
}
/** Spec 036: eventos del flujo SSE (`…/stream`). */
export interface StudioLive { kind: 'waiting' | 'text' | 'tool' | 'none'; text?: string; tool?: string; input?: unknown }
export interface StudioStreamSession {
  status: StudioSessionStatus; turns: number; steps: number; toolCalls: number; inputTokens: number; outputTokens: number; costUsd: number;
  planStatus?: StudioPlanStatus | null; error?: string | null;
}
export type StudioEndReason = 'idle' | 'timeout' | 'identity' | 'gone';

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

/* ---------- Spec 026: origen de cada elemento y editor de expresiones ---------- */
export type OriginKind = 'entity' | 'field' | 'rule' | 'automation' | 'view' | 'report' | 'webhook' | 'schedule' | 'workflow' | 'layout' | 'security' | 'ledger' | 'printTemplate';
export interface PackageStamp { id: string; name: string; version: string }
/** `package` nulo = local; `step` = «base» o la versión de la actualización que lo dejó como está. */
export interface ElementOrigin { kind: OriginKind; name: string; package: string | null; packageName: string | null; step: string | null; modified: boolean }
export interface EntityOrigin { entity: string; installedBy: PackageStamp | null; packages: PackageStamp[]; elements: ElementOrigin[] }
export interface ExprFunction { name: string; min: number; max: number }
/** Spec 026 (H2 CA4): lo local o modificado que una actualización de módulo reemplaza o quita. */
export interface UpgradeConflict { entity: string; entityLabel: string; kind: OriginKind; name: string; reason: 'local' | 'modified'; effect: 'replaced' | 'removed' }

/** Spec 038: lo que muestra la pantalla Conectar (o a dónde volver si la petición del cliente ya trae un error). */
export interface McpAuthorizeInfo {
  redirect?: string;
  /** Revisión adversarial F-03: el error que vuelve al cliente; la persona decide volver (no se redirige sola). */
  error?: string;
  errorDescription?: string;
  redirectHost?: string;
  client?: { name: string; redirectHost: string; trusted: boolean };
  user?: { username: string; displayName: string };
  /** Spec 044: `available` y `reason` (aislado y proponer solo para quien modela; aislado, si el servidor tiene copia). */
  scopes?: { name: McpScope; required: boolean; requested: boolean; available: boolean; reason?: string | null }[];
}

/** Spec 038: una conexión MCP vigente de la persona. */
export type McpScope = 'modelo' | 'datos' | 'aislado' | 'proponer';
/** Spec 044: la copia aislada viva de una conexión. */
export interface McpSandboxInfo { status: 'creating' | 'ready'; createdAt: string; lastUsedAt: string }
export interface McpConnection {
  id: number; client: string; redirectHost: string; scopes: string[]; createdAt: string; lastUsedAt?: string | null; sandbox?: McpSandboxInfo | null;
}
export interface McpConnections { serverUrl: string; connections: McpConnection[] }

// ---------- Spec 046: Acompañante (contrato del motor; la pantalla es de la 047) ----------
export interface CompanionLimits { maxPromptChars: number; maxTurns: number; maxPendingActions: number; conversationsPerDay: number; costPerDayUsd: number }
export interface CompanionStatus {
  available: boolean; reason?: string | null; limits: CompanionLimits; conversationsToday: number; costTodayUsd: number; activeId?: number | null;
}
/** Spec 048: `waiting` = el turno se detuvo en una propuesta y espera tu respuesta. */
export type CompanionConversationStatus = 'queued' | 'running' | 'waiting' | 'answered' | 'failed' | 'cancelled' | 'interrupted';
/** Lo que la persona está viendo (el servidor lo revalida con su alcance). */
export interface CompanionScreen { entity: string; id?: number | null }
export interface CompanionSummary {
  id: number; title: string; status: CompanionConversationStatus; turns: number; createdAt: string; updatedAt: string; pendingActions: number;
}
export type CompanionActionKind = 'create' | 'update' | 'transition';
export type CompanionActionStatus = 'proposed' | 'applying' | 'applied' | 'failed' | 'discarded' | 'expired' | 'unknown';
export interface CompanionChange { campo: string; antes?: unknown; despues?: unknown }
/** La vista previa la arma el servidor con etiquetas; existe mientras la acción está pendiente. */
export interface CompanionPreview {
  tipo: CompanionActionKind; catalogo: string; registro?: number | null;
  /** Revisión adversarial 046 (F-03): lo que escribió el modelo; el panel lo muestra aparte y marcado como suyo. */
  segunElAcompanante?: string | null; cambios?: CompanionChange[];
  transicion?: string; desde?: string; hacia?: string; comentario?: string | null;
}
export interface CompanionAction {
  id: number; kind: CompanionActionKind; entity: string; entityLabel: string; recordId?: number | null; preview?: CompanionPreview | null;
  hash: string; status: CompanionActionStatus; error?: string | null; result?: { recordId?: number | null } | null; createdAt: string; decidedAt?: string | null;
  /** Spec 048: lo que la persona le dijo al decir que no. */
  reason?: string | null;
}
export interface CompanionConversation {
  id: number; title: string; status: CompanionConversationStatus; error?: string | null; turns: number; steps: number; costUsd: number;
  createdAt: string; updatedAt: string; canSend: boolean; sendBlockedReason?: string | null; trail: StudioStep[]; actions: CompanionAction[];
}
export interface CompanionStreamSession { status: CompanionConversationStatus; turns: number; steps: number; pendingActions: number; error?: string | null }
