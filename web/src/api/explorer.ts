import { api } from './client';
import type { EntityDef } from './types';

/** Spec 025: explorador del modelo y de la base de datos (solo lectura; exige administrar el modelo). */

export interface AppRef { name: string; label: string; icon: string; order: number }

export type ColumnStatus = 'ok' | 'system' | 'archived' | 'orphan' | 'mismatch';
export interface DbColumn {
  name: string; type: string; nullable: boolean; identity: boolean; default?: { name?: string; definition: string } | null;
  computed?: string | null; system: boolean; field?: { name: string; label: string; type: string; isArchived: boolean } | null;
  status: ColumnStatus; expectedType?: string | null;
}
export interface DbForeignKey { name: string; columns: string[]; refSchema: string; refTable: string; refColumns: string[]; nullable: boolean; unique: boolean; onDelete: string }
export interface DbIndex { name: string; unique: boolean; primaryKey: boolean; uniqueConstraint: boolean; filter?: string | null; columns: string[]; included: string[] }
export interface DbTable {
  schema: string; name: string;
  entity?: { name: string; label: string; app: string; appLabel: string; isArchived: boolean; isChild: boolean } | null;
  columns: DbColumn[]; primaryKey?: { name: string; columns: string[] } | null; foreignKeys: DbForeignKey[]; indexes: DbIndex[]; driftCount: number;
}
export type DriftKind = 'orphanTable' | 'archivedTable' | 'missingTable' | 'orphanColumn' | 'archivedColumn' | 'missingColumn' | 'typeMismatch';
export interface DbDrift { kind: DriftKind; table: string; column?: string | null; message: string }
export interface DbSchema { schema: string; tables: DbTable[]; external: { schema: string; name: string }[]; drift: DbDrift[]; apps: AppRef[] }

export interface RelField {
  name: string; label: string; type: string; resultType?: string | null; target?: string | null;
  required: boolean; unique: boolean; formula: boolean; autonumber: boolean; hasDefault: boolean; state: boolean;
}
export interface RelNode { name: string; label: string; labelPlural: string; icon: string; app: string; isChild: boolean; hasWorkflow: boolean; perCompany: boolean; fields: RelField[] }
export interface RelEdge { from: string; field: string; to: string; kind: 'reference' | 'lines' | 'parent' | 'user' }
export interface RelGraph { apps: AppRef[]; nodes: RelNode[]; edges: RelEdge[] }

export interface ScopeGrant { access: 'read' | 'write'; scope: string; where?: string | null }
export interface RoleAccess {
  role: string; entity: string; read: boolean; create: boolean; update: boolean; delete: boolean; inheritedFrom: string[];
  scopes: ScopeGrant[]; hidden: string[]; readOnly: string[];
}
export interface PackageRef { id: string; name: string; created: boolean; versions: string[]; installedVersion?: string | null }
export interface EntityCard {
  entity: EntityDef; app: AppRef; table: string; tableExists: boolean; packages: PackageRef[];
  dashboards: { name: string; label: string }[]; referencedBy: RelEdge[]; roles: RoleAccess[];
}

export type UsageKind = 'formula' | 'default' | 'rule' | 'transition' | 'layout' | 'automation' | 'schedule' | 'webhook' | 'access'
  | 'view' | 'report' | 'dashboard' | 'lookup' | 'ledger' | 'display' | 'reference';
export interface Usage { entity: string; kind: UsageKind; location: string; expression?: string | null; target: { entity: string; field?: string | null }; tab: 'catalogo' | 'formulario' | 'proceso' }

export interface FlowNode { name: string; label: string; app: string }
export interface FlowEdge {
  from: string; to: string; owner: string; name: string; label: string; trigger: string; mode: 'sync' | 'async' | 'schedule';
  action: 'update' | 'transition' | 'create' | 'rollup' | 'cost' | 'capture'; detail: string; triggers: boolean; cycle: boolean;
}
export interface FlowGraph { apps: AppRef[]; nodes: FlowNode[]; edges: FlowEdge[] }

export interface SecurityMatrix {
  roles: { name: string; description?: string | null; isSuperuser: boolean; inherits: string[] }[];
  entities: { name: string; label: string; app: string; appLabel: string }[];
  cells: RoleAccess[];
}
export interface NavApp {
  name: string; label: string; icon: string;
  entities: { name: string; label: string; labelPlural: string; icon: string; hasWorkflow: boolean; layout: boolean;
    sections: { name: string; label: string; tab?: string | null; fields: { name: string; label: string; type: string }[] }[]; lines: string[] }[];
}

const enc = encodeURIComponent;

export const explorerApi = {
  database: (q: { schema?: string; app?: string; q?: string }, signal?: AbortSignal) => api<DbSchema>('/explorer/database', { query: q, signal }),
  ddl: (q: { schema?: string; app?: string; q?: string; tables?: string[] }) =>
    api<Blob>('/explorer/database/ddl', { query: { ...q, tables: q.tables?.join(',') }, asBlob: true }),
  relations: (q: { app?: string; q?: string }, signal?: AbortSignal) => api<RelGraph>('/explorer/relations', { query: q, signal }),
  entity: (name: string, signal?: AbortSignal) => api<EntityCard>(`/explorer/entities/${enc(name)}`, { signal }),
  usages: (entity: string, field?: string, signal?: AbortSignal) => api<Usage[]>('/explorer/usages', { query: { entity, field }, signal }),
  automations: (signal?: AbortSignal) => api<FlowGraph>('/explorer/automations', { signal }),
  security: (signal?: AbortSignal) => api<SecurityMatrix>('/explorer/security', { signal }),
  navigation: (signal?: AbortSignal) => api<NavApp[]>('/explorer/navigation', { signal }),
};
