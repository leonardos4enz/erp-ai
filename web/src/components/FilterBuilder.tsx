import { useEffect, useState } from 'react';
import { Plus, Trash2, X } from 'lucide-react';
import { usersApi } from '../api/endpoints';
import type { EntityDef, FieldDef, FilterOp, ViewFilter } from '../api/types';
import { activeFields } from '../lib/entity';
import { formatDate } from '../lib/format';
import { Dialog } from './Dialog';
import { ReferenceSelect } from './ReferenceSelect';
import { Button, Input, Select } from './ui';

export type Kind = 'text' | 'longtext' | 'number' | 'date' | 'boolean' | 'id' | 'choice';

/** Igual que el servidor (FilterSql): qué operadores admite cada clase de campo. */
export function kindOf(f: FieldDef): Kind {
  const t = f.type === 'formula' ? f.resultType ?? 'decimal' : f.type;
  if (t === 'choice' || t === 'state') return 'choice';
  if (t === 'longtext') return 'longtext';
  if (t === 'integer' || t === 'decimal') return 'number';
  if (t === 'date' || t === 'datetime') return 'date';
  if (t === 'boolean') return 'boolean';
  if (t === 'reference' || t === 'user' || t === 'parent') return 'id';
  return 'text';
}

export const OPS: Record<Kind, FilterOp[]> = {
  text: ['contains', 'eq', 'ne', 'blank', 'notBlank'],
  longtext: ['contains', 'blank', 'notBlank'],
  choice: ['eq', 'ne', 'in', 'blank', 'notBlank'],
  number: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'blank', 'notBlank'],
  date: ['eq', 'gte', 'lte', 'gt', 'lt', 'blank', 'notBlank'],
  boolean: ['eq'],
  id: ['eq', 'ne', 'blank', 'notBlank'],
};

export const OP_LABELS: Record<FilterOp, string> = {
  eq: 'es igual a', ne: 'es distinto de', gt: 'mayor que', gte: 'mayor o igual a', lt: 'menor que', lte: 'menor o igual a',
  contains: 'contiene', in: 'es uno de', blank: 'está vacío', notBlank: 'no está vacío',
};
const DATE_OPS: Partial<Record<FilterOp, string>> = { gte: 'desde', lte: 'hasta', gt: 'después de', lt: 'antes de' };

/** Campos por los que la persona puede filtrar (los ocultos por la 006 ya no vienen en el catálogo). */
export function filterableFields(def: EntityDef): FieldDef[] {
  return activeFields(def).filter((f) => f.type !== 'lines' && f.type !== 'parent');
}

function options(def: EntityDef, f: FieldDef): { value: string; label: string }[] {
  if (f.type === 'state') return (def.workflow?.states ?? []).map((s) => ({ value: s.name, label: s.label }));
  return (f.options ?? []).map((o) => ({ value: o, label: o }));
}

/** Texto de un filtro para su chip: «Estado es uno de Borrador, Enviada». */
export function describeFilter(def: EntityDef, flt: ViewFilter): string {
  const f = def.fields.find((x) => x.name.toLowerCase() === flt.field.toLowerCase());
  const label = f?.label ?? flt.field;
  const op = f && kindOf(f) === 'date' ? DATE_OPS[flt.op] ?? OP_LABELS[flt.op] : OP_LABELS[flt.op];
  if (flt.op === 'blank' || flt.op === 'notBlank') return `${label} ${op}`;
  const show = (v: unknown) => {
    if (v === '$me') return 'yo';
    if (v === '$today') return 'hoy';
    if (f && options(def, f).length) return options(def, f).find((o) => o.value === v)?.label ?? String(v);
    if (f && kindOf(f) === 'boolean') return v ? 'Sí' : 'No';
    if (f && kindOf(f) === 'date') return formatDate(v);
    return String(v);
  };
  const value = flt.label ?? (Array.isArray(flt.value) ? flt.value.map(show).join(', ') : show(flt.value));
  return `${label} ${op} ${value}`;
}

function isComplete(flt: Draft): boolean {
  if (!flt.field) return false;
  if (flt.op === 'blank' || flt.op === 'notBlank') return true;
  if (Array.isArray(flt.value)) return flt.value.length > 0;
  return flt.value !== undefined && flt.value !== null && flt.value !== '';
}

type Draft = ViewFilter & { key: number };
let seq = 0;

/** Chips de los filtros activos, cada uno con su botón para quitarlo. */
export function FilterChips({ def, filters, onRemove }: { def: EntityDef; filters: ViewFilter[]; onRemove: (i: number) => void }) {
  if (!filters.length) return null;
  return (
    <ul className="filter-chips" aria-label="Filtros aplicados" style={{ listStyle: 'none', padding: 0 }}>
      {filters.map((f, i) => {
        const text = describeFilter(def, f);
        return (
          <li key={i} className="filter-chip">
            <span title={text}>{text}</span>
            <Button size="sm" variant="ghost" iconOnly icon={X} aria-label={`Quitar filtro: ${text}`} onClick={() => onRemove(i)} />
          </li>
        );
      })}
    </ul>
  );
}

/** Diálogo para editar los filtros (se aplican al presionar «Aplicar»). */
export function FilterDialog({ open, def, filters, onClose, onApply }: {
  open: boolean; def: EntityDef; filters: ViewFilter[]; onClose: () => void; onApply: (f: ViewFilter[]) => void;
}) {
  const fields = filterableFields(def);
  const [rows, setRows] = useState<Draft[]>([]);
  useEffect(() => {
    if (open) setRows(filters.length ? filters.map((f) => ({ ...f, key: ++seq })) : [{ field: '', op: 'eq', key: ++seq }]);
  }, [open, filters]);

  const patch = (key: number, p: Partial<Draft>) => setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...p } : r)));
  const apply = () => onApply(rows.filter(isComplete).map(({ key: _k, ...f }) => f));

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Filtros"
      description="Solo se muestran los registros que cumplen todas las condiciones."
      size="lg"
      footer={
        <>
          <Button onClick={() => setRows([])} style={{ marginRight: 'auto' }}>Quitar todos</Button>
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" onClick={apply}>Aplicar</Button>
        </>
      }
    >
      <div className="stack">
        {rows.map((r, i) => {
          const f = fields.find((x) => x.name === r.field);
          const kind = f ? kindOf(f) : 'text';
          const ops = OPS[kind];
          const n = i + 1;
          return (
            <div key={r.key} className="filter-row">
              <Select label={`Campo ${n}`} value={r.field} onChange={(e) => {
                const nf = fields.find((x) => x.name === e.target.value);
                const k = nf ? kindOf(nf) : 'text';
                patch(r.key, { field: e.target.value, op: OPS[k][0], value: k === 'boolean' ? true : undefined, label: undefined });
              }}>
                <option value="">Elige un campo…</option>
                {fields.map((x) => <option key={x.name} value={x.name}>{x.label}</option>)}
              </Select>
              <Select label={`Condición ${n}`} value={r.op} disabled={!f} onChange={(e) => patch(r.key, { op: e.target.value as FilterOp, value: undefined, label: undefined })}>
                {ops.map((o) => <option key={o} value={o}>{kind === 'date' ? DATE_OPS[o] ?? OP_LABELS[o] : OP_LABELS[o]}</option>)}
              </Select>
              <ValueInput def={def} field={f} filter={r} n={n} onChange={(value, label) => patch(r.key, { value, label: label ?? undefined })} />
              <Button variant="ghost" iconOnly icon={Trash2} aria-label={`Quitar condición ${n}`} onClick={() => setRows((rs) => rs.filter((x) => x.key !== r.key))} />
            </div>
          );
        })}
        <div>
          <Button size="sm" icon={Plus} onClick={() => setRows((rs) => [...rs, { field: '', op: 'eq', key: ++seq }])}>Agregar condición</Button>
        </div>
      </div>
    </Dialog>
  );
}

const lookupUsers = (search: string, signal: AbortSignal) => usersApi.lookup(search, signal);

function ValueInput({ def, field: f, filter, n, onChange }: {
  def: EntityDef; field?: FieldDef; filter: Draft; n: number; onChange: (v: unknown, label?: string | null) => void;
}) {
  const label = `Valor ${n}`;
  if (!f || filter.op === 'blank' || filter.op === 'notBlank') return <Input label={label} disabled value="" readOnly />;
  const kind = kindOf(f);
  if (kind === 'choice') {
    const opts = options(def, f);
    if (filter.op === 'in') {
      const selected = Array.isArray(filter.value) ? (filter.value as string[]) : [];
      return (
        <Select label={label} multiple value={selected} style={{ height: 'auto', minHeight: 36 }} size={Math.min(5, opts.length)}
          onChange={(e) => onChange([...e.target.selectedOptions].map((o) => o.value))}>
          {opts.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </Select>
      );
    }
    return (
      <Select label={label} value={String(filter.value ?? '')} onChange={(e) => onChange(e.target.value)}>
        <option value="">Elige…</option>
        {opts.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </Select>
    );
  }
  if (kind === 'boolean') {
    return (
      <Select label={label} value={filter.value === false ? 'false' : 'true'} onChange={(e) => onChange(e.target.value === 'true')}>
        <option value="true">Sí</option>
        <option value="false">No</option>
      </Select>
    );
  }
  if (kind === 'id') {
    const id = `flt-${filter.key}`;
    return (
      <div className="field">
        <label className="field-label" htmlFor={id}>{label}</label>
        <ReferenceSelect
          id={id}
          entity={f.referenceEntity ?? ''}
          lookup={f.type === 'user' ? lookupUsers : undefined}
          value={typeof filter.value === 'number' ? filter.value : null}
          label={filter.label ?? null}
          onChange={(v, l) => onChange(v, l)}
        />
      </div>
    );
  }
  if (kind === 'date') return <Input label={label} type="date" value={filter.value === '$today' ? new Date().toISOString().slice(0, 10) : String(filter.value ?? '')} onChange={(e) => onChange(e.target.value)} />;
  if (kind === 'number') return <Input label={label} type="number" step="any" value={String(filter.value ?? '')} onChange={(e) => onChange(e.target.value === '' ? '' : Number(e.target.value))} />;
  return <Input label={label} value={String(filter.value ?? '')} maxLength={200} onChange={(e) => onChange(e.target.value)} />;
}
