import { useEffect, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import type { EntityDef, FieldDef, LookupFilter, LookupOp } from '../api/types';
import {
  fromRows, kindOf, lookupSignature, needsValue, opLabel, opsFor, sourcePaths, targetFields, toRows, type LookupRow,
} from '../lib/lookupFilter';
import { ReferenceSelect } from './ReferenceSelect';
import { Button, Input, Select } from './ui';

interface Props {
  value?: LookupFilter | null;
  onChange: (lf: LookupFilter) => void;
  /** Campos del registro que se captura (los del borrador del catálogo que se edita). */
  ownerFields: FieldDef[];
  entities: EntityDef[];
  /** Catálogo referenciado. */
  target?: EntityDef;
  disabled?: boolean;
}

/** Lista escrita «a, b, c» → valores (números si el campo lo es). */
function parseList(text: string, numeric: boolean): unknown[] {
  return text.split(',').map((x) => x.trim()).filter(Boolean).map((x) => (numeric && x !== '' && !Number.isNaN(Number(x)) ? Number(x) : x));
}

/** Valor fijo según el tipo del campo del destino. */
function ConstantEditor({ f, row, label, onChange, disabled }: { f: FieldDef; row: LookupRow; label: string; onChange: (v: unknown) => void; disabled?: boolean }) {
  const kind = kindOf(f);
  const v = row.value;
  if (row.op === 'in') {
    if (f.type === 'choice') {
      const chosen = Array.isArray(v) ? (v as string[]) : [];
      return (
        <fieldset className="cond-in">
          <legend className="sr-only">{`${label}: valores`}</legend>
          {(f.options ?? []).map((o) => (
            <label key={o} className="checkbox">
              <input type="checkbox" disabled={disabled} checked={chosen.includes(o)}
                onChange={(e) => onChange(e.target.checked ? [...chosen, o] : chosen.filter((x) => x !== o))} />
              <span>{o}</span>
            </label>
          ))}
        </fieldset>
      );
    }
    const numeric = kind !== 'text';
    return <Input aria-label={`${label}: valores separados por coma`} disabled={disabled} placeholder={numeric ? 'p. ej. 3, 7' : 'p. ej. A, B'}
      value={Array.isArray(v) ? v.join(', ') : ''} onChange={(e) => onChange(parseList(e.target.value, numeric))} />;
  }
  if (kind === 'boolean')
    return (
      <Select aria-label={`${label}: valor`} disabled={disabled} value={v === false ? 'false' : 'true'} onChange={(e) => onChange(e.target.value === 'true')}>
        <option value="true">Sí</option><option value="false">No</option>
      </Select>
    );
  if (f.type === 'choice')
    return (
      <Select aria-label={`${label}: valor`} disabled={disabled} value={String(v ?? '')} onChange={(e) => onChange(e.target.value)}>
        <option value="">Opción…</option>
        {(f.options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
      </Select>
    );
  if (f.type === 'user' || f.type === 'multiuser')
    return (
      <Select aria-label={`${label}: valor`} disabled={disabled} value="$me" onChange={() => onChange('$me')}>
        <option value="$me">quien captura</option>
      </Select>
    );
  if (f.type === 'reference' || f.type === 'parent' || f.type === 'multireference')
    return (
      <div className="lookup-filter-ref">
        <ReferenceSelect entity={f.referenceEntity ?? ''} value={typeof v === 'number' ? v : null} label={null} ariaLabel={`${label}: registro`}
          disabled={disabled} compact onChange={(id) => onChange(id)} />
      </div>
    );
  if (kind === 'number')
    return <Input aria-label={`${label}: valor`} disabled={disabled} type="number" value={v == null ? '' : String(v)}
      onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} />;
  if (kind === 'date')
    return <Input aria-label={`${label}: valor`} disabled={disabled} placeholder="aaaa-mm-dd o $today" value={String(v ?? '')} onChange={(e) => onChange(e.target.value)} />;
  return <Input aria-label={`${label}: valor`} disabled={disabled} value={String(v ?? '')} onChange={(e) => onChange(e.target.value)} />;
}

/**
 * Spec 052 (H4): filtro del buscador de una referencia, en renglones «campo del catálogo · operador · valor fijo o del registro»
 * unidos por Y. El JSON que resulta lo valida el plan (los mismos mensajes que un paquete o el asistente).
 */
export function LookupFilterEditor({ value, onChange, ownerFields, entities, target, disabled }: Props) {
  // Los renglones a medio llenar (sin campo) viven aquí; al borrador solo llegan los que tienen campo.
  const [rows, setRows] = useState<LookupRow[]>(() => toRows(value));
  const signature = lookupSignature(value);
  useEffect(() => {
    setRows((cur) => (lookupSignature(fromRows(cur)) === signature ? cur : toRows(value)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);
  const fields = target ? targetFields(target) : [];
  const set = (next: LookupRow[]) => { setRows(next); onChange(fromRows(next)); };
  const update = (i: number, row: LookupRow) => set(rows.map((r, k) => (k === i ? row : r)));
  const name = target?.labelPlural ?? 'el catálogo';
  return (
    <fieldset className="cond lookup-filter span-all" disabled={disabled}>
      <legend className="field-label">Filtro del buscador</legend>
      <p className="field-hint">
        {rows.length === 0
          ? `El buscador ofrece todos los registros de ${name} a su alcance.`
          : `El buscador ofrece y el servidor acepta al guardar solo los registros de ${name} que cumplen todas las condiciones.`}
      </p>
      {rows.map((r, i) => {
        const rowLabel = `Filtro, condición ${i + 1}`;
        const f = fields.find((x) => x.name.toLowerCase() === r.field.toLowerCase());
        const ops = f ? opsFor(f) : [];
        const paths = f && needsValue(r.op) ? sourcePaths(ownerFields, entities, f, r.op) : [];
        return (
          <div className="cond-row" key={i} role="group" aria-label={rowLabel}>
            {i > 0 && <span className="cond-join" aria-hidden>y</span>}
            <Select aria-label={`${rowLabel}: campo de ${name}`} value={f?.name ?? r.field}
              onChange={(e) => {
                const nf = fields.find((x) => x.name === e.target.value);
                const op = nf && opsFor(nf).includes(r.op) ? r.op : nf ? opsFor(nf)[0] : 'eq';
                update(i, { field: e.target.value, op, mode: 'value', value: nf && kindOf(nf) === 'boolean' ? true : undefined });
              }}>
              <option value="">Campo…</option>
              {fields.map((x) => <option key={x.name} value={x.name}>{x.label || x.name}</option>)}
            </Select>
            {f && (
              <>
                <Select aria-label={`${rowLabel}: operador`} value={r.op}
                  onChange={(e) => {
                    const op = e.target.value as LookupOp;
                    update(i, { ...r, op, value: op === 'in' || r.op === 'in' ? undefined : r.value });
                  }}>
                  {ops.map((o) => <option key={o} value={o}>{opLabel(f, o)}</option>)}
                </Select>
                {needsValue(r.op) && (
                  <div className="cond-value">
                    <Select aria-label={`${rowLabel}: comparar con`} value={r.mode}
                      onChange={(e) => update(i, { ...r, mode: e.target.value === 'source' ? 'source' : 'value', value: undefined, source: undefined })}>
                      <option value="value">un valor fijo</option>
                      <option value="source">un dato del registro</option>
                    </Select>
                    {r.mode === 'source' ? (
                      <Select aria-label={`${rowLabel}: dato del registro`} value={r.source ?? ''} onChange={(e) => update(i, { ...r, source: e.target.value })}>
                        <option value="">{paths.length ? 'Dato…' : 'Ningún dato compatible'}</option>
                        {r.source && !paths.some((p) => p.path === r.source) && <option value={r.source}>{r.source}</option>}
                        {paths.map((p) => <option key={p.path} value={p.path}>{p.label}</option>)}
                      </Select>
                    ) : (
                      <ConstantEditor f={f} row={r} label={rowLabel} disabled={disabled} onChange={(v) => update(i, { ...r, value: v })} />
                    )}
                  </div>
                )}
              </>
            )}
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar condición ${i + 1} del filtro`}
              onClick={() => set(rows.filter((_, k) => k !== i))} />
          </div>
        );
      })}
      <div>
        <Button size="sm" variant="ghost" icon={Plus} disabled={!target || rows.length >= 10}
          onClick={() => set([...rows, { field: '', op: 'eq', mode: 'value' }])}>Agregar condición al filtro</Button>
      </div>
    </fieldset>
  );
}
