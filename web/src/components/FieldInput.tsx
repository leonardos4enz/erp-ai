import { usersApi } from '../api/endpoints';
import type { EntityDef, FieldDef } from '../api/types';
import { formatFormula, isNumericField, StateBadge } from '../lib/entity';
import { Field, Input, Select, Switch } from './ui';
import { ReferenceSelect, type ReferenceSelectProps } from './ReferenceSelect';

export interface FieldControlProps {
  field: FieldDef;
  /** Definición de la entidad dueña del campo (para etiquetas de estado). */
  def?: EntityDef;
  value: unknown;
  /** Etiqueta mostrada para referencias / usuarios. */
  refLabel?: string | null;
  onChange: (value: unknown, label?: string | null) => void;
  id: string;
  disabled?: boolean;
  invalid?: boolean;
  describedBy?: string;
  required?: boolean;
  /** Sin <label> visible: nombre accesible explícito (celdas de partidas). */
  ariaLabel?: string;
  compact?: boolean;
  /** Spec 005: filtro del buscador ya resuelto con los valores del formulario. */
  filter?: ReferenceSelectProps['filter'];
  /** Registro nuevo (los folios aún no existen). */
  isNew?: boolean;
}

const lookupUsers = (search: string, signal: AbortSignal) => usersApi.lookup(search, signal);
// Spec 015: el campo dueño con teamOwners ofrece personas y equipos.
const lookupPrincipals = (search: string, signal: AbortSignal) => usersApi.lookup(search, signal, true);

/** Solo el control (sin etiqueta ni mensajes), según el tipo del campo. */
export function FieldControl(p: FieldControlProps) {
  const { field: f, value: v, id, disabled, invalid, describedBy, ariaLabel, compact } = p;
  const aria = { 'aria-invalid': invalid || undefined, 'aria-describedby': describedBy, 'aria-label': ariaLabel };
  const size = compact ? 'sm' : undefined;

  switch (f.type) {
    case 'formula': {
      const text = formatFormula(f, v);
      return (
        <output id={id} className={`readonly-value${isNumericField(f) ? ' num' : ''}${compact ? ' compact' : ''}`} aria-label={ariaLabel} aria-live="polite">
          {text || <span className="subtle">—</span>}
        </output>
      );
    }
    case 'autonumber':
      return (
        <output id={id} className={`readonly-value mono${compact ? ' compact' : ''}`} aria-label={ariaLabel}>
          {v ? String(v) : <span className="subtle">{p.isNew ? 'Se asigna al guardar' : '—'}</span>}
        </output>
      );
    case 'state': {
      const s = p.def?.workflow?.states.find((x) => x.name === v);
      return <span id={id}>{v ? <StateBadge label={s?.label ?? String(v)} color={s?.color} /> : <span className="subtle">—</span>}</span>;
    }
    case 'longtext':
      return (
        <textarea
          id={id} {...aria} className={`textarea${compact ? ' textarea-compact' : ''}`} disabled={disabled} required={p.required}
          rows={compact ? 1 : undefined} value={String(v ?? '')} onChange={(e) => p.onChange(e.target.value)}
        />
      );
    case 'boolean':
      return (
        <div className={compact ? 'cell-switch' : 'field-switch'}>
          <Switch id={id} disabled={disabled} checked={!!v} onChange={(c) => p.onChange(c)} label={compact ? undefined : (v ? 'Sí' : 'No')} aria-label={ariaLabel} />
        </div>
      );
    case 'integer':
    case 'decimal':
      return (
        <Input
          id={id} {...aria} inputSize={size} disabled={disabled} required={p.required}
          className={compact ? 'num' : undefined}
          type="number" inputMode={f.type === 'integer' ? 'numeric' : 'decimal'} step={f.type === 'integer' ? 1 : 'any'}
          value={String(v ?? '')} onChange={(e) => p.onChange(e.target.value)}
        />
      );
    case 'date':
      return <Input id={id} {...aria} inputSize={size} disabled={disabled} required={p.required} type="date" value={String(v ?? '')} onChange={(e) => p.onChange(e.target.value)} />;
    case 'datetime':
      return <Input id={id} {...aria} inputSize={size} disabled={disabled} required={p.required} type="datetime-local" value={String(v ?? '')} onChange={(e) => p.onChange(e.target.value)} />;
    case 'choice':
      return (
        <Select id={id} {...aria} selectSize={size} disabled={disabled} required={p.required} value={String(v ?? '')} onChange={(e) => p.onChange(e.target.value)}>
          <option value="">{p.required ? 'Selecciona…' : '— Ninguno —'}</option>
          {(f.options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
        </Select>
      );
    case 'reference':
    case 'user':
      return (
        <ReferenceSelect
          id={id}
          entity={f.referenceEntity ?? ''}
          lookup={f.type === 'user' ? (f.acceptsTeams ? lookupPrincipals : lookupUsers) : undefined}
          filter={f.type === 'reference' ? p.filter : undefined}
          value={(v as number | null) ?? null}
          label={p.refLabel ?? null}
          invalid={invalid}
          disabled={disabled}
          describedBy={describedBy}
          ariaLabel={ariaLabel}
          compact={compact}
          onChange={(nid, nlabel) => p.onChange(nid, nlabel)}
        />
      );
    case 'parent':
    case 'lines':
      return null;
    default:
      return <Input id={id} {...aria} inputSize={size} disabled={disabled} required={p.required} value={String(v ?? '')} maxLength={f.maxLength} onChange={(e) => p.onChange(e.target.value)} />;
  }
}

export interface FieldInputProps extends FieldControlProps {
  error?: string;
  hint?: string;
  className?: string;
}

/** Control con etiqueta, ayuda y error (formulario y diálogos). */
export function FieldInput({ error, hint, className, ...p }: FieldInputProps) {
  const errId = `${p.id}-err`;
  const wide = p.field.type === 'longtext';
  return (
    <Field
      label={p.field.label}
      required={p.required && p.field.type !== 'boolean'}
      error={error}
      hint={hint}
      htmlFor={p.id}
      className={[wide ? 'span-2' : undefined, className].filter(Boolean).join(' ') || undefined}
    >
      <FieldControl {...p} invalid={!!error} describedBy={error ? errId : undefined} />
    </Field>
  );
}
