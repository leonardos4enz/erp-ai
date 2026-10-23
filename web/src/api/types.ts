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
  /** Solo en la UI: bloqueado por el estado del registro (ver `withState`). */
  lockedByState?: boolean;
}

export type FieldAccessLevel = 'hidden' | 'read' | 'edit';

/** Spec 006: quién ve y captura cada campo, y qué registros ve o modifica cada rol. */
export interface FieldSecurity { field: string; read?: string[]; edit?: string[]; editableIn?: string[] }
export interface RecordGrant { roles?: string[]; scope?: 'all' | 'unit' | 'own'; where?: string; access?: 'read' | 'write' }
export interface SecurityPolicy {
  fields?: FieldSecurity[];
  owner?: string;
  records?: RecordGrant[];
  systemManaged?: boolean;
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
  type: 'update' | 'transition' | 'create' | 'rollup';
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
}

export interface Rule { name: string; message: string; assert: string; when?: string; field?: string }

export interface EntityDef {
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
}

export type Operation =
  | { op: 'createEntity'; name: string; label: string; labelPlural: string; icon?: string; description?: string; displayField?: string; group?: string; fields: FieldDef[] }
  | { op: 'updateEntity'; entity: string; label?: string; labelPlural?: string; icon?: string; description?: string; displayField?: string; group?: string }
  | { op: 'archiveEntity'; entity: string }
  | { op: 'addField'; entity: string; field: FieldDef }
  | { op: 'updateField'; entity: string; field: string; label?: string; required?: boolean; showInList?: boolean; options?: string[]; requiredForRoles?: string[]; displayOrder?: number; defaultValue?: string; formula?: string; editable?: boolean; lookupFilter?: { field: string; source: string } }
  | { op: 'archiveField'; entity: string; field: string }
  | { op: 'setWorkflow'; entity: string; workflow: Workflow }
  | { op: 'setRules'; entity: string; rules: Rule[] }
  | { op: 'setAutomations'; entity: string; automations: Automation[]; merge?: boolean }
  | { op: 'setSecurity'; entity: string; security: SecurityPolicy | null };

export interface ChangePlan { description: string; operations: Operation[] }

export interface PlanPreview {
  valid: boolean;
  errors: { operation: number; message: string }[];
  steps: { operation: number; description: string; sql?: string }[];
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
