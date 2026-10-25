import { Plus, Trash2 } from 'lucide-react';
import type { FieldDef } from '../api/types';
import {
  defaultValue, fieldAt, kindOfField, OP_TEXT, OPS_FOR, type Cond, type CondGroup, type CondOp, type CondValue, type FieldKind,
} from '../lib/conditionBuilder';
import { fieldsAtPath, MAX_HOPS, type CompletionScope } from '../lib/exprComplete';
import { Button, Input, Select } from './ui';

const hop = (f: FieldDef) => f.type === 'reference' || f.type === 'parent';
const usable = (fs: FieldDef[]) => fs.filter((f) => !f.isArchived && f.type !== 'lines');

/** Campo por pasos: el propio o, si es una referencia, uno de su catálogo (hasta tres saltos, spec 023). */
function PathPicker({ path, onChange, scope, label }: { path: string; onChange: (p: string) => void; scope: CompletionScope; label: string }) {
  const segs = path ? path.split('.') : [];
  const levels: { fields: FieldDef[]; value: string }[] = [];
  let fields = usable(scope.fields);
  for (let i = 0; i <= segs.length && i <= MAX_HOPS; i++) {
    levels.push({ fields, value: segs[i] ?? '' });
    const f = fields.find((x) => x.name === segs[i]);
    if (!f || !hop(f) || i === MAX_HOPS) break;
    const next = fieldsAtPath(scope, segs.slice(0, i + 1));
    if (!next) break;
    fields = usable(next.fields);
  }
  return (
    <div className="cond-path">
      {levels.map((lv, i) => (
        <Select key={i} aria-label={i === 0 ? `${label}: campo` : `${label}: campo de ${segs.slice(0, i).join('.')}`} value={lv.value}
          onChange={(e) => onChange([...segs.slice(0, i), e.target.value].filter(Boolean).join('.'))}>
          <option value="">{i === 0 ? 'Campo…' : '(el campo mismo)'}</option>
          {lv.fields.map((f) => <option key={f.name} value={f.name}>{f.label || f.name}{hop(f) && i < MAX_HOPS ? ' ›' : ''}</option>)}
        </Select>
      ))}
    </div>
  );
}

const SOURCES: Record<FieldKind, { v: CondValue['kind']; l: string }[]> = {
  text: [{ v: 'text', l: 'un texto' }, { v: 'field', l: 'otro campo' }],
  number: [{ v: 'number', l: 'un número' }, { v: 'field', l: 'otro campo' }],
  date: [{ v: 'today', l: 'hoy (± días)' }, { v: 'field', l: 'otro campo' }],
  boolean: [{ v: 'bool', l: 'sí o no' }],
  choice: [{ v: 'text', l: 'una opción' }, { v: 'field', l: 'otro campo' }],
  user: [{ v: 'me', l: 'quien usa el sistema' }, { v: 'field', l: 'otro campo' }],
  reference: [{ v: 'field', l: 'otro campo' }],
};

function ValueEditor({ c, f, kind, scope, label, onChange }: { c: Cond; f?: FieldDef; kind: FieldKind; scope: CompletionScope; label: string; onChange: (v: CondValue) => void }) {
  const v = c.value;
  const options = f?.type === 'state' ? scope.states ?? [] : f?.options ?? [];
  if (c.op === 'in') {
    const chosen = v?.kind === 'list' ? v.values : [];
    return (
      <fieldset className="cond-in">
        <legend className="sr-only">{`${label}: valores`}</legend>
        {options.map((o) => (
          <label key={o} className="checkbox">
            <input type="checkbox" checked={chosen.includes(o)}
              onChange={(e) => onChange({ kind: 'list', values: e.target.checked ? options.filter((x) => x === o || chosen.includes(x)) : chosen.filter((x) => x !== o) })} />
            <span>{o}</span>
          </label>
        ))}
      </fieldset>
    );
  }
  const sources = SOURCES[kind];
  const sourceSelect = sources.length > 1 && (
    <Select aria-label={`${label}: comparar con`} value={v?.kind ?? sources[0].v}
      onChange={(e) => onChange(e.target.value === 'field' ? { kind: 'field', value: '' } : defaultValue(kind, f, scope.states)!)}>
      {sources.map((s) => <option key={s.v} value={s.v}>{s.l}</option>)}
    </Select>
  );
  let control = null;
  if (!v || v.kind === 'text') {
    control = kind === 'choice'
      ? <Select aria-label={`${label}: valor`} value={v?.kind === 'text' ? v.value : ''} onChange={(e) => onChange({ kind: 'text', value: e.target.value })}>
          {options.map((o) => <option key={o} value={o}>{o}</option>)}
        </Select>
      : <Input aria-label={`${label}: valor`} value={v?.kind === 'text' ? v.value : ''} onChange={(e) => onChange({ kind: 'text', value: e.target.value })} />;
  } else if (v.kind === 'number') {
    control = <Input aria-label={`${label}: valor`} type="number" value={v.value} onChange={(e) => onChange({ kind: 'number', value: e.target.value })} />;
  } else if (v.kind === 'bool') {
    control = <Select aria-label={`${label}: valor`} value={v.value ? 'true' : 'false'} onChange={(e) => onChange({ kind: 'bool', value: e.target.value === 'true' })}>
      <option value="true">Sí</option><option value="false">No</option>
    </Select>;
  } else if (v.kind === 'today') {
    control = <Input aria-label={`${label}: días desde hoy`} type="number" value={String(v.days)} title="0 = hoy; negativo = días atrás"
      onChange={(e) => onChange({ kind: 'today', days: Number(e.target.value) || 0 })} />;
  } else if (v.kind === 'field') {
    // Otro campo del mismo tipo (sin el propio).
    const same = usable(scope.fields).filter((x) => x.name !== c.path && kindOfField(x) === kind && (kind !== 'reference' || x.referenceEntity === f?.referenceEntity));
    control = <Select aria-label={`${label}: otro campo`} value={v.value} onChange={(e) => onChange({ kind: 'field', value: e.target.value })}>
      <option value="">Campo…</option>
      {same.map((x) => <option key={x.name} value={x.name}>{x.label || x.name}</option>)}
    </Select>;
  }
  return <div className="cond-value">{sourceSelect}{control}</div>;
}

/**
 * Spec 026 (H3 CA5): condición por renglones. El texto que resulta es el mismo que se escribiría a mano; quien lo
 * necesite más complejo (fórmulas, paréntesis, Y con O) cambia a «Texto».
 */
export function ConditionBuilder({ group, onChange, scope, label }: { group: CondGroup; onChange: (g: CondGroup) => void; scope: CompletionScope; label: string }) {
  const set = (i: number, c: Cond) => onChange({ ...group, items: group.items.map((x, k) => (k === i ? c : x)) });
  return (
    <div className="cond">
      {group.items.length > 1 && (
        <Select aria-label={`${label}: cómo se combinan`} value={group.join} onChange={(e) => onChange({ ...group, join: e.target.value === 'OR' ? 'OR' : 'AND' })}>
          <option value="AND">Se cumplen todas (Y)</option>
          <option value="OR">Se cumple alguna (O)</option>
        </Select>
      )}
      {group.items.length === 0 && <p className="field-hint">Sin condiciones todavía.</p>}
      {group.items.map((c, i) => {
        const rowLabel = `Condición ${i + 1}`;
        const f = c.path ? fieldAt(scope, c.path) : undefined;
        const kind = kindOfField(f);
        const ops = OPS_FOR[kind];
        return (
          <div className="cond-row" key={i} role="group" aria-label={`${label}: ${rowLabel.toLowerCase()}`}>
            {i > 0 && <span className="cond-join" aria-hidden>{group.join === 'AND' ? 'y' : 'o'}</span>}
            <PathPicker path={c.path} scope={scope} label={rowLabel}
              onChange={(path) => {
                const nf = path ? fieldAt(scope, path) : undefined;
                const nk = kindOfField(nf);
                const op = OPS_FOR[nk].includes(c.op) ? c.op : OPS_FOR[nk][0];
                set(i, { path, op, value: defaultValue(nk, nf, scope.states) });
              }} />
            {c.path && (
              <>
                <Select aria-label={`${rowLabel}: operador`} value={c.op}
                  onChange={(e) => {
                    const op = e.target.value as CondOp;
                    set(i, { ...c, op, value: op === 'in' ? { kind: 'list', values: [] } : c.op === 'in' ? defaultValue(kind, f, scope.states) : c.value });
                  }}>
                  {ops.map((o) => <option key={o} value={o}>{OP_TEXT[o]}</option>)}
                </Select>
                {c.op !== 'blank' && c.op !== 'notBlank' && (
                  <ValueEditor c={c} f={f} kind={kind} scope={scope} label={rowLabel} onChange={(value) => set(i, { ...c, value })} />
                )}
              </>
            )}
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar condición ${i + 1}`}
              onClick={() => onChange({ ...group, items: group.items.filter((_, k) => k !== i) })} />
          </div>
        );
      })}
      <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => onChange({ ...group, items: [...group.items, { path: '', op: 'eq' }] })}>Agregar condición</Button></div>
    </div>
  );
}
