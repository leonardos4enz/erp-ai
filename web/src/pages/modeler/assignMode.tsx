import type { FieldDef } from '../../api/types';
import { Select } from '../../components/ui';
import { isMultiField } from '../../lib/entity';

type Mode = 'add' | 'remove';

/**
 * Spec 051: una asignación a un campo de varios valores agrega o quita el id que da su expresión (`mode`); a un campo simple, no
 * lleva modo. Al cambiar el campo se ajusta: «agregar» por omisión en una lista y sin modo en lo demás.
 */
export function withMode<T extends { field: string; mode?: Mode }>(a: T, fields: FieldDef[]): T {
  const f = fields.find((x) => x.name === a.field);
  if (f && isMultiField(f)) return { ...a, mode: a.mode ?? 'add' };
  const { mode: _omit, ...rest } = a;
  return rest as T;
}

export function AssignMode({ label, value, onChange, size }: { label: string; value?: Mode; onChange: (m: Mode) => void; size?: 'sm' }) {
  return (
    <Select selectSize={size} aria-label={label} value={value ?? 'add'} onChange={(e) => onChange(e.target.value === 'remove' ? 'remove' : 'add')}>
      <option value="add">Agregar a la lista</option>
      <option value="remove">Quitar de la lista</option>
    </Select>
  );
}
