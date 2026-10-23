import { Check, Minus } from 'lucide-react';
import { modelApi } from '../api/endpoints';
import type { EntityDef, FieldDef, FieldType, Row, StateColor } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge } from '../components/ui';
import { formatDate, formatDateTime, formatNumber } from './format';
import { useAsync } from './hooks';

export const FIELD_TYPE_LABELS: Record<FieldType, string> = {
  text: 'Texto',
  longtext: 'Texto largo',
  integer: 'Entero',
  decimal: 'Decimal',
  boolean: 'Sí / No',
  date: 'Fecha',
  datetime: 'Fecha y hora',
  choice: 'Lista de opciones',
  reference: 'Referencia',
  user: 'Usuario',
  autonumber: 'Folio automático',
  formula: 'Fórmula',
  lines: 'Partidas (detalle)',
  parent: 'Documento padre',
  state: 'Estado del proceso',
};

/** Tipos que el modelador puede elegir ('state' lo crea el motor al definir un proceso). */
export const MODELER_FIELD_TYPES: FieldType[] = [
  'text', 'longtext', 'integer', 'decimal', 'boolean', 'date', 'datetime', 'choice', 'reference',
  'user', 'autonumber', 'formula', 'lines', 'parent',
];

/** Campos cuyo valor calcula o asigna el servidor; nunca se envían al guardar. */
export function isComputedField(f: FieldDef): boolean {
  return f.readOnly === true || f.editable === false || f.type === 'formula' || f.type === 'autonumber' || f.type === 'state' || f.type === 'parent';
}

/** Campos que la persona captura directamente en el formulario. */
export function isEditableField(f: FieldDef): boolean {
  return !isComputedField(f) && f.type !== 'lines';
}

/** Tipos que admiten valor por defecto en el modelador. */
export function supportsDefault(type: FieldType): boolean {
  return !['formula', 'autonumber', 'lines', 'parent', 'state'].includes(type);
}

export function activeFields(def: EntityDef): FieldDef[] {
  return def.fields
    .filter((f) => !f.isArchived)
    .slice()
    .sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0));
}

/** Loads an entity definition, preferring the cached copy from the auth context. */
export function useEntityDef(name: string | undefined) {
  const { entities } = useAuth();
  const cached = name ? entities.find((e) => e.name.toLowerCase() === name.toLowerCase()) : undefined;
  const state = useAsync<EntityDef | undefined>(
    async () => (cached ?? (name ? await modelApi.entity(name) : undefined)),
    [name, cached],
  );
  return { def: cached ?? state.data, loading: !cached && state.loading, error: cached ? null : state.error };
}

/**
 * Definiciones de las entidades hijas (partidas) de un documento. Solo vienen en
 * GET /model/entities?includeChildren=true, por eso no están en la caché del menú.
 */
export function useChildDefs(def: EntityDef | undefined): { children: Record<string, EntityDef>; loading: boolean } {
  const names = def ? activeFields(def).filter((f) => f.type === 'lines' && f.childEntity).map((f) => f.childEntity!) : [];
  const key = names.join(',');
  const state = useAsync<Record<string, EntityDef>>(async () => {
    if (!names.length) return {};
    const all = await modelApi.entities(false, true);
    const out: Record<string, EntityDef> = {};
    for (const n of names) {
      const found = all.find((e) => e.name.toLowerCase() === n.toLowerCase());
      if (found) out[n] = found;
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return { children: state.data ?? {}, loading: names.length > 0 && state.loading };
}

export function isRequiredFor(field: FieldDef, roles: string[]): boolean {
  if (field.required) return true;
  if (!field.requiredForRoles?.length) return false;
  const mine = roles.map((r) => r.toLowerCase());
  return field.requiredForRoles.some((r) => mine.includes(r.toLowerCase()));
}

export function stateTone(color?: StateColor): 'neutral' | 'accent' | 'success' | 'warning' | 'danger' {
  return color ?? 'neutral';
}

export function StateBadge({ label, color }: { label: string; color?: StateColor }) {
  return <Badge tone={stateTone(color)}>{label}</Badge>;
}

/** Formato de un valor calculado según su tipo de resultado. */
export function formatFormula(field: FieldDef, v: unknown): string {
  if (v === null || v === undefined || v === '') return '';
  switch (field.resultType) {
    case 'integer': return formatNumber(v);
    case 'boolean': return v ? 'Sí' : 'No';
    case 'date': return formatDate(v);
    case 'text': return String(v);
    default: {
      const n = typeof v === 'number' ? v : Number(v);
      if (Number.isNaN(n)) return String(v);
      return n.toLocaleString('es-MX', { minimumFractionDigits: 2, maximumFractionDigits: Math.max(2, field.scale ?? 2) });
    }
  }
}

export function isNumericField(f: FieldDef): boolean {
  return f.type === 'integer' || f.type === 'decimal' || (f.type === 'formula' && (f.resultType ?? 'decimal') !== 'text' && f.resultType !== 'boolean' && f.resultType !== 'date');
}

export function CellValue({ field, row, def }: { field: FieldDef; row: Row; def?: EntityDef }) {
  const v = row[field.name];
  if (v === null || v === undefined || v === '') return <span className="subtle" aria-label="vacío">—</span>;
  switch (field.type) {
    case 'boolean':
      return v
        ? <Check size={16} color="var(--success)" aria-label="Sí" />
        : <Minus size={16} color="var(--text-subtle)" aria-label="No" />;
    case 'date':
      return <>{formatDate(v)}</>;
    case 'datetime':
      return <>{formatDateTime(v)}</>;
    case 'integer':
      return <>{formatNumber(v)}</>;
    case 'decimal':
      return <>{formatNumber(v, true)}</>;
    case 'formula':
      return <>{formatFormula(field, v)}</>;
    case 'autonumber':
      return <span className="mono">{String(v)}</span>;
    case 'choice':
      return <Badge>{String(v)}</Badge>;
    case 'state': {
      const s = def?.workflow?.states.find((x) => x.name === v);
      return <StateBadge label={s?.label ?? String(v)} color={s?.color} />;
    }
    case 'reference':
    case 'user':
    case 'parent': {
      const label = row[`${field.name}__label`];
      return <>{label != null && label !== '' ? String(label) : `#${String(v)}`}</>;
    }
    default:
      return <>{String(v)}</>;
  }
}
