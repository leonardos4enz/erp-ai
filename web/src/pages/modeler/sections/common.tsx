import { useId, useState, type ReactNode } from 'react';
import { ArrowDown, ArrowUp, Braces, ChevronDown, ChevronRight, Package, Plus, Trash2, TriangleAlert, type LucideIcon } from 'lucide-react';
import type { ElementOrigin, EntityDef, FieldDef, OriginKind } from '../../../api/types';
import type { ExprScope } from '../../../components/ExprInput';
import { Alert, Badge, Button, EmptyState, Textarea } from '../../../components/ui';
import { clean, sameValue } from '../../../lib/canonical';

/* ------------------------------------------------------------------------- */
/* Contexto que comparten los editores de sección                             */

export interface SectionCtx {
  /** Identificador del catálogo que se edita (puede ser nuevo). */
  entity: string;
  /** Campos activos del borrador con identificador válido. */
  fields: FieldDef[];
  entities: EntityDef[];
  roleNames: string[] | null;
  states: { name: string; label: string }[];
  transitions: { name: string; label: string }[];
  /** Spec 026 (H2): origen de cada elemento guardado, por `kind:nombre`. */
  origin: (kind: OriginKind, name: string) => ElementOrigin | undefined;
  /** Elemento guardado con ese nombre (para saber si el borrador lo cambió). */
  saved: (kind: OriginKind, name: string) => unknown;
  /** Errores del compilador (vista previa en vivo) de esta sección. */
  errors: string[];
}

export const selfScope = (c: SectionCtx): ExprScope => ({ entity: c.entity, fields: c.fields, draft: true, states: c.states.map((s) => s.name) });

export function entityByName(c: SectionCtx, name?: string): EntityDef | undefined {
  if (!name) return undefined;
  return c.entities.find((e) => e.name.toLowerCase() === name.toLowerCase());
}

/** Ámbito de expresión de otro catálogo (o del propio, con el borrador). */
export function scopeOf(c: SectionCtx, name?: string): ExprScope | null {
  if (!name) return null;
  if (name.toLowerCase() === c.entity.toLowerCase()) return selfScope(c);
  const e = entityByName(c, name);
  return e ? { entity: e.name, fields: e.fields.filter((f) => !f.isArchived), states: e.workflow?.states.map((s) => s.name) } : null;
}

/** Opciones de catálogos; si dos se llaman igual, se distingue por su identificador. */
export function entityOptions(entities: EntityDef[]) {
  const count = new Map<string, number>();
  entities.forEach((e) => count.set(e.labelPlural, (count.get(e.labelPlural) ?? 0) + 1));
  return entities.map((e) => <option key={e.name} value={e.name}>{e.labelPlural}{(count.get(e.labelPlural) ?? 0) > 1 ? ` (${e.name})` : ''}</option>);
}

export const activeFields =(e?: { fields: FieldDef[] }) => (e?.fields ?? []).filter((f) => !f.isArchived);
export const isNumeric = (f: FieldDef) => f.type === 'integer' || f.type === 'decimal' || (f.type === 'formula' && (f.resultType === 'integer' || f.resultType === 'decimal'));
/** Campos que una persona o una acción puede asignar. */
export const writable = (f: FieldDef) => !f.isArchived && !f.readOnly && !['formula', 'autonumber', 'lines', 'state'].includes(f.type);

export function fieldOptions(fields: FieldDef[], empty?: string) {
  return (
    <>
      {empty !== undefined && <option value="">{empty}</option>}
      {fields.map((f) => <option key={f.name} value={f.name}>{f.label || f.name} ({f.name})</option>)}
    </>
  );
}

export function uniqueName(base: string, taken: string[]): string {
  const lower = taken.map((t) => t.toLowerCase());
  if (!lower.includes(base.toLowerCase())) return base;
  for (let i = 2; ; i++) if (!lower.includes(`${base}${i}`.toLowerCase())) return `${base}${i}`;
}

// La comparación canónica (026 F-03) vive en lib/canonical.ts (spec 030: se prueba con Vitest).
export { clean, sameValue };

/* ------------------------------------------------------------------------- */
/* Origen (spec 026 · H2)                                                     */

export function originText(o?: ElementOrigin): string {
  if (!o || !o.package) return 'Local';
  return `${o.packageName ?? o.package}${o.step && o.step !== 'base' ? ` · v${o.step}` : ''}`;
}

export function OriginBadge({ origin }: { origin?: ElementOrigin }) {
  if (!origin) return null;
  return (
    <span className="origin">
      <span className={`origin-chip${origin.package ? '' : ' origin-local'}`} title={origin.package
        ? `Instalado por el paquete ${origin.packageName}${origin.step === 'base' ? '' : ` (actualización ${origin.step})`}`
        : 'Creado en este entorno (modelador, asistente o paquete de Entornos)'}>
        {origin.package && <Package size={11} aria-hidden />}
        <span className="sr-only">Origen: </span>{originText(origin)}
      </span>
      {origin.modified && <Badge tone="warning">Modificado localmente</Badge>}
    </span>
  );
}

/** H2 CA3: se avisa junto al elemento cuando el borrador cambia algo que instaló un paquete. */
export function PackageNotice({ origin }: { origin?: ElementOrigin }) {
  if (!origin?.package) return null;
  return (
    <Alert tone="warning" icon={TriangleAlert} title={`Lo instaló ${origin.packageName}`}>
      Tu cambio queda como modificación local. Si una actualización de {origin.packageName} vuelve a definir este elemento,
      la actualización lo reemplaza; su vista previa te avisará antes de aplicarla.
    </Alert>
  );
}

/* ------------------------------------------------------------------------- */
/* Errores del compilador junto a la sección                                  */

export function CompilerErrors({ errors, title = 'El compilador rechaza esta sección' }: { errors: string[]; title?: string }) {
  if (!errors.length) return null;
  return (
    <Alert tone="danger" title={title}>
      <ul className="compiler-errors">{errors.map((e) => <li key={e}>{e}</li>)}</ul>
    </Alert>
  );
}

/** Errores que nombran al elemento (`… regla FechaValida …`, `'FechaValida'`). */
export const errorsFor = (errors: string[], name: string) =>
  name ? errors.filter((e) => new RegExp(`(^|[\\s'«(›])${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}([\\s'»):›,.]|$)`).test(e)) : [];

/* ------------------------------------------------------------------------- */
/* JSON avanzado (H4 CA10)                                                    */

export function JsonAdvanced({ label, text, onText, error, hint }: { label: string; text: string; onText: (t: string) => void; error?: string; hint: string }) {
  // Un JSON inválido no se esconde: la sección se abre sola para corregirlo.
  const [open, setOpen] = useState(!!error);
  return (
    <details className="json-advanced" open={open || !!error} onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
      <summary><Braces size={14} aria-hidden /> Ver como JSON <span className="subtle">· avanzado</span></summary>
      <Textarea className="mono json-editor" label={label} rows={12} spellCheck={false} hint={hint} error={error} value={text} onChange={(e) => onText(e.target.value)} />
    </details>
  );
}

/** El editor visual necesita un JSON válido; si no lo es, lo dice y no pisa el texto. */
export function InvalidJson({ error }: { error: string }) {
  return (
    <Alert tone="danger" title="El JSON de esta sección no es válido">
      {error} Corrígelo en «Ver como JSON» (abajo) para volver al editor visual; no se perdió nada.
    </Alert>
  );
}

/* ------------------------------------------------------------------------- */
/* Lista de elementos con agregar, mover, quitar y plegar                     */

export interface ItemListProps<T> {
  items: T[];
  onChange: (items: T[]) => void;
  kind: OriginKind;
  ctx: SectionCtx;
  icon: LucideIcon;
  /** «regla», «automatización»… */
  noun: string;
  emptyTitle: string;
  emptyDescription: ReactNode;
  addLabel: string;
  create: (items: T[]) => T;
  name: (t: T) => string;
  title: (t: T) => string;
  summary?: (t: T) => ReactNode;
  render: (t: T, patch: (p: Partial<T>) => void, i: number) => ReactNode;
}

export function ItemList<T extends object>({ items, onChange, kind, ctx, icon, noun, emptyTitle, emptyDescription, addLabel, create, name, title, summary, render }: ItemListProps<T>) {
  const [open, setOpen] = useState<Set<number>>(() => new Set());
  const baseId = useId();
  const toggle = (i: number) => setOpen((s) => { const n = new Set(s); if (n.has(i)) n.delete(i); else n.add(i); return n; });
  const swap = (s: Set<number>, a: number, b: number) => { const n = new Set<number>(); s.forEach((x) => n.add(x === a ? b : x === b ? a : x)); return n; };
  function add() {
    onChange([...items, create(items)]);
    setOpen((s) => new Set(s).add(items.length));
    // El foco va al primer control del elemento nuevo.
    requestAnimationFrame(() => document.getElementById(`${baseId}-${items.length}`)?.querySelector<HTMLElement>('input, select, textarea')?.focus());
  }
  function move(i: number, d: -1 | 1) {
    const j = i + d;
    const next = items.slice();
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
    setOpen((s) => swap(s, i, j));
  }
  function remove(i: number) {
    onChange(items.filter((_, k) => k !== i));
    setOpen((s) => { const n = new Set<number>(); s.forEach((x) => { if (x < i) n.add(x); else if (x > i) n.add(x - 1); }); return n; });
  }
  const addButton = <Button size="sm" icon={Plus} onClick={add}>{addLabel}</Button>;
  if (!items.length) {
    return <div className="section-empty"><EmptyState icon={icon} title={emptyTitle} description={emptyDescription} action={addButton} /></div>;
  }
  return (
    <div className="stack" style={{ gap: 'var(--space-3)' }}>
      <ul className="item-list">
        {items.map((t, i) => {
          const n = name(t);
          const o = ctx.origin(kind, n);
          const saved = ctx.saved(kind, n);
          const changed = !!o?.package && saved !== undefined && !sameValue(saved, t);
          const errs = errorsFor(ctx.errors, n);
          const expanded = open.has(i);
          const label = title(t) || n || `${noun} sin nombre`;
          return (
            <li key={i} className={`item${errs.length ? ' item-error' : ''}`} id={`${baseId}-${i}`}>
              <div className="item-head">
                <button type="button" className="item-toggle" aria-expanded={expanded} aria-controls={`${baseId}-${i}-body`} onClick={() => toggle(i)}>
                  {expanded ? <ChevronDown size={16} aria-hidden /> : <ChevronRight size={16} aria-hidden />}
                  <span className="item-title">{label}</span>
                  {n && n !== label && <span className="item-name mono">{n}</span>}
                </button>
                <OriginBadge origin={o} />
                {errs.length > 0 && <Badge tone="danger">Con errores</Badge>}
                <div className="item-tools">
                  <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} aria-label={`Subir ${noun} ${label}`} disabled={i === 0} onClick={() => move(i, -1)} />
                  <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} aria-label={`Bajar ${noun} ${label}`} disabled={i === items.length - 1} onClick={() => move(i, 1)} />
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar ${noun} ${label}`} onClick={() => remove(i)} />
                </div>
              </div>
              {!expanded && summary && <div className="item-summary">{summary(t)}</div>}
              {expanded && (
                <div className="item-body" id={`${baseId}-${i}-body`}>
                  {changed && <PackageNotice origin={o} />}
                  {errs.length > 0 && <CompilerErrors errors={errs} title="Errores del compilador" />}
                  {render(t, (p) => onChange(items.map((x, k) => (k === i ? clean({ ...x, ...p }) : x))), i)}
                </div>
              )}
            </li>
          );
        })}
      </ul>
      <div>{addButton}</div>
    </div>
  );
}

/* ------------------------------------------------------------------------- */
/* Selección múltiple con casillas                                            */

export function MultiPick({ legend, options, value, onChange, hint, empty }: {
  legend: string;
  options: { value: string; label: string }[];
  value: string[];
  onChange: (v: string[]) => void;
  hint?: ReactNode;
  empty?: string;
}) {
  // Lo guardado que ya no existe se sigue mostrando (y se puede quitar): no se pierde en silencio.
  const extra = value.filter((v) => !options.some((o) => o.value.toLowerCase() === v.toLowerCase())).map((v) => ({ value: v, label: `${v} (no existe)` }));
  const all = [...options, ...extra];
  const toggle = (v: string, on: boolean) => {
    if (on) {
      // Respeta el orden de las opciones (p. ej. el de los campos).
      const order = all.map((o) => o.value);
      onChange([...value, v].sort((a, b) => order.indexOf(a) - order.indexOf(b)));
    } else onChange(value.filter((x) => x !== v));
  };
  return (
    <fieldset className="multipick span-all">
      <legend className="field-label">{legend}</legend>
      {all.length === 0 ? <span className="field-hint">{empty ?? 'No hay opciones.'}</span> : (
        <div className="multipick-grid">
          {all.map((o) => (
            <label key={o.value} className="checkbox">
              <input type="checkbox" checked={value.includes(o.value)} onChange={(e) => toggle(o.value, e.target.checked)} />
              <span>{o.label}</span>
            </label>
          ))}
        </div>
      )}
      {hint && <span className="field-hint">{hint}</span>}
    </fieldset>
  );
}

export const roleOptions = (c: SectionCtx) => (c.roleNames ?? []).map((r) => ({ value: r, label: r }));
export const fieldPickOptions = (fields: FieldDef[]) => fields.map((f) => ({ value: f.name, label: f.label || f.name }));

/** Sección con su título, errores del compilador y el JSON avanzado al final. */
export function SectionShell({ title, description, errors, children, json }: { title: string; description: ReactNode; errors: string[]; children: ReactNode; json: ReactNode }) {
  return (
    <section className="card card-pad stack" aria-label={title}>
      <div>
        <h2 className="section-heading">{title}</h2>
        <p className="subtle section-desc">{description}</p>
      </div>
      <CompilerErrors errors={errors} />
      {children}
      {json}
    </section>
  );
}
