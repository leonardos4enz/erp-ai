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
}

export interface Workflow { initial: string; states: WorkflowState[]; transitions: WorkflowTransitionDef[] }

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
}

export type Operation =
  | { op: 'createEntity'; name: string; label: string; labelPlural: string; icon?: string; description?: string; displayField?: string; group?: string; fields: FieldDef[] }
  | { op: 'updateEntity'; entity: string; label?: string; labelPlural?: string; icon?: string; description?: string; displayField?: string; group?: string }
  | { op: 'archiveEntity'; entity: string }
  | { op: 'addField'; entity: string; field: FieldDef }
  | { op: 'updateField'; entity: string; field: string; label?: string; required?: boolean; showInList?: boolean; options?: string[]; requiredForRoles?: string[]; displayOrder?: number; defaultValue?: string; formula?: string }
  | { op: 'archiveField'; entity: string; field: string }
  | { op: 'setWorkflow'; entity: string; workflow: Workflow }
  | { op: 'setRules'; entity: string; rules: Rule[] };

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
