import { Check, Minus } from 'lucide-react';
import { modelApi } from '../api/endpoints';
import type { EntityDef, FieldDef, FieldType, Row } from '../api/types';
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
};

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

export function isRequiredFor(field: FieldDef, roles: string[]): boolean {
  if (field.required) return true;
  if (!field.requiredForRoles?.length) return false;
  const mine = roles.map((r) => r.toLowerCase());
  return field.requiredForRoles.some((r) => mine.includes(r.toLowerCase()));
}

export function CellValue({ field, row }: { field: FieldDef; row: Row }) {
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
    case 'choice':
      return <Badge>{String(v)}</Badge>;
    case 'reference': {
      const label = row[`${field.name}__label`];
      return <>{label != null && label !== '' ? String(label) : `#${String(v)}`}</>;
    }
    default:
      return <>{String(v)}</>;
  }
}
