export type FieldType =
  | 'text' | 'longtext' | 'integer' | 'decimal' | 'boolean'
  | 'date' | 'datetime' | 'choice' | 'reference';

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
}

export interface EntityDef {
  name: string;
  label: string;
  labelPlural: string;
  icon: string;
  description?: string;
  displayField?: string;
  isArchived: boolean;
  fields: FieldDef[];
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
  | { op: 'createEntity'; name: string; label: string; labelPlural: string; icon?: string; description?: string; displayField?: string; fields: FieldDef[] }
  | { op: 'updateEntity'; entity: string; label?: string; labelPlural?: string; icon?: string; description?: string; displayField?: string }
  | { op: 'archiveEntity'; entity: string }
  | { op: 'addField'; entity: string; field: FieldDef }
  | { op: 'updateField'; entity: string; field: string; label?: string; required?: boolean; showInList?: boolean; options?: string[]; requiredForRoles?: string[]; displayOrder?: number }
  | { op: 'archiveField'; entity: string; field: string };

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
