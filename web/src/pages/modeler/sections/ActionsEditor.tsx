import { ArrowDown, ArrowUp, Plus, Trash2, Zap } from 'lucide-react';
import type { AutomationAction, EntityDef, FieldDef } from '../../../api/types';
import { ExprInput, type ExprScope } from '../../../components/ExprInput';
import { Button, Checkbox, Select } from '../../../components/ui';
import { activeFields, clean, entityByName, entityOptions, fieldOptions, isNumeric, scopeOf, writable, type SectionCtx } from './common';

type Assignment = { field: string; value: string };
type Action = AutomationAction;

export const ACTION_LABELS: Record<Action['type'], string> = {
  update: 'Asignar campos', transition: 'Ejecutar una transición', create: 'Crear un registro', rollup: 'Acumular una suma', cost: 'Valuar con capas de costo',
};

/** Ámbito de una acción: el registro o, con `lines`, la partida (igual que `AutomationRules.Scope`). */
function scopeEntity(ctx: SectionCtx, a: Action): { name: string; fields: FieldDef[] } | null {
  if (!a.lines) return { name: ctx.entity, fields: ctx.fields };
  const lf = ctx.fields.find((f) => f.name === a.lines && f.type === 'lines');
  const child = entityByName(ctx, lf?.childEntity);
  return child ? { name: child.name, fields: activeFields(child) } : null;
}

/** Catálogo destino (como `AutomationRules.Target`). */
function targetEntity(ctx: SectionCtx, scope: { name: string; fields: FieldDef[] }, a: Action, isChild: boolean): EntityDef | { name: string; fields: FieldDef[]; workflow?: EntityDef['workflow'] } | undefined {
  if (a.type === 'create' || a.type === 'cost' || (a.type === 'rollup' && !a.target)) return entityByName(ctx, a.entity);
  if (!a.target && !a.lines && (a.type === 'update' || a.type === 'transition') && !isChild) {
    return scope.name === ctx.entity ? { name: ctx.entity, fields: ctx.fields, workflow: { initial: '', states: [], transitions: ctx.transitions.map((t) => ({ ...t, from: [], to: '' })) } } : entityByName(ctx, scope.name);
  }
  const ref = scope.fields.find((f) => f.name === a.target && f.type === 'reference');
  return entityByName(ctx, ref?.referenceEntity);
}

function AssignList({ label, value, onChange, fields, scope, ctx, hint, addLabel = 'Agregar campo' }: {
  label: string; value: Assignment[]; onChange: (v: Assignment[]) => void; fields: FieldDef[]; scope: ExprScope | null; ctx: SectionCtx; hint?: string; addLabel?: string;
}) {
  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">{label}</legend>
      {hint && <p className="field-hint">{hint}</p>}
      {value.map((s, i) => (
        <div className="assign-row" key={i}>
          <Select aria-label={`${label}: campo ${i + 1}`} value={s.field ?? ''} onChange={(e) => onChange(value.map((x, k) => (k === i ? { ...x, field: e.target.value } : x)))}>
            {fieldOptions(fields, 'Campo…')}
            {s.field && !fields.some((f) => f.name === s.field) && <option value={s.field}>{s.field} (no existe)</option>}
          </Select>
          <ExprInput label={<span className="sr-only">{`${label}: valor de ${s.field || `campo ${i + 1}`}`}</span>} value={s.value ?? ''} scope={scope} entities={ctx.entities}
            placeholder="Valor (expresión)" onChange={(v) => onChange(value.map((x, k) => (k === i ? { ...x, value: v } : x)))} />
          <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar ${s.field || `campo ${i + 1}`}`} onClick={() => onChange(value.filter((_, k) => k !== i))} />
        </div>
      ))}
      <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => onChange([...value, { field: '', value: '' }])}>{addLabel}</Button></div>
    </fieldset>
  );
}

function MatchList({ value, onChange, target, scope }: { value: { field: string; source: string }[]; onChange: (v: { field: string; source: string }[]) => void; target: FieldDef[]; scope: FieldDef[] }) {
  const stored = (fs: FieldDef[]) => fs.filter((f) => !['lines', 'formula'].includes(f.type));
  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">Cómo se encuentra el destino (llave)</legend>
      <p className="field-hint">Cada par: campo del destino = campo de este registro, del mismo tipo.</p>
      {value.map((m, i) => (
        <div className="assign-row" key={i}>
          <Select aria-label={`Llave ${i + 1}: campo del destino`} value={m.field ?? ''} onChange={(e) => onChange(value.map((x, k) => (k === i ? { ...x, field: e.target.value } : x)))}>
            {fieldOptions(stored(target), 'Campo del destino…')}
          </Select>
          <Select aria-label={`Llave ${i + 1}: campo de este registro`} value={m.source ?? ''} onChange={(e) => onChange(value.map((x, k) => (k === i ? { ...x, source: e.target.value } : x)))}>
            {fieldOptions(stored(scope), 'Campo de este registro…')}
          </Select>
          <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar llave ${i + 1}`} onClick={() => onChange(value.filter((_, k) => k !== i))} />
        </div>
      ))}
      <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => onChange([...value, { field: '', source: '' }])}>Agregar llave</Button></div>
    </fieldset>
  );
}

const METHODS = [{ v: "'average'", l: 'Promedio ponderado' }, { v: "'fifo'", l: 'Primeras entradas, primeras salidas (FIFO)' }];

function ActionForm({ a, patch, ctx, isChild, schedule }: { a: Action; patch: (p: Partial<Action>) => void; ctx: SectionCtx; isChild: boolean; schedule: boolean }) {
  const scope = scopeEntity(ctx, a);
  const linesFields = ctx.fields.filter((f) => f.type === 'lines');
  const exprScope = scope ? scopeOf(ctx, scope.name) : null;
  const target = scope ? targetEntity(ctx, scope, a, isChild) : undefined;
  const targetFields = activeFields(target);
  const refFields = (scope?.fields ?? []).filter((f) => f.type === 'reference');
  const rootEntities = ctx.entities.filter((e) => !e.isArchived && !e.isChild);
  const canSelf = !a.lines && !isChild && (a.type === 'update' || a.type === 'transition');
  const targetSelect = (
    <Select label={a.type === 'transition' ? 'Registro donde se ejecuta' : 'Registro que se modifica'} value={a.target ?? ''}
      onChange={(e) => patch({ target: e.target.value || undefined })}
      hint={canSelf ? 'El propio registro o el que apunta una referencia.' : 'El registro al que apunta una referencia.'}>
      {canSelf ? <option value="">{schedule ? 'El propio registro' : 'El propio registro'}</option> : <option value="">Elige una referencia…</option>}
      {refFields.map((f) => <option key={f.name} value={f.name}>{f.label} → {entityByName(ctx, f.referenceEntity)?.label ?? f.referenceEntity}</option>)}
    </Select>
  );
  const entitySelect = (label: string) => (
    <Select label={label} value={a.entity ?? ''} onChange={(e) => patch({ entity: e.target.value || undefined })}>
      <option value="">Elige un catálogo…</option>
      {entityOptions(rootEntities)}
    </Select>
  );
  const transitions = target?.workflow?.transitions ?? [];
  const createdScope = a.type === 'create' ? scopeOf(ctx, a.entity) : null;

  return (
    <div className="item-grid">
      <Select label="Tipo de acción" value={a.type} onChange={(e) => patch({ type: e.target.value as Action['type'] })}>
        {(Object.keys(ACTION_LABELS) as Action['type'][]).map((t) => <option key={t} value={t}>{ACTION_LABELS[t]}</option>)}
      </Select>
      {a.type !== 'cost' && linesFields.length > 0 && (
        <Select label="Para cada" value={a.lines ?? ''} onChange={(e) => patch({ lines: e.target.value || undefined })}
          hint="Con partidas, la acción se repite por cada partida y sus expresiones se leen en la partida.">
          <option value="">El registro (una vez)</option>
          {linesFields.map((f) => <option key={f.name} value={f.name}>Partida de {f.label}</option>)}
        </Select>
      )}
      {!scope && <p className="field-error span-all">El campo de partidas «{a.lines}» ya no existe.</p>}
      <ExprInput condition className="span-all" label="Solo si (opcional)" value={a.when ?? ''} onChange={(v) => patch({ when: v })} scope={exprScope} entities={ctx.entities} hint="Vacío: siempre." />

      {a.type === 'update' && (
        <>
          {targetSelect}
          <AssignList label="Campos que se asignan" value={a.set ?? []} onChange={(set) => patch({ set })} fields={targetFields.filter(writable)} scope={exprScope} ctx={ctx}
            hint="Los valores se calculan en este registro (o en la partida)." />
        </>
      )}

      {a.type === 'transition' && (
        <>
          {targetSelect}
          <Select label="Transición" value={a.transition ?? ''} onChange={(e) => patch({ transition: e.target.value || undefined })}
            hint={target && !transitions.length ? 'El catálogo destino no tiene proceso.' : undefined}>
            <option value="">Elige una transición…</option>
            {transitions.map((t) => <option key={t.name} value={t.name}>{t.label} ({t.name})</option>)}
          </Select>
        </>
      )}

      {a.type === 'create' && (
        <>
          {entitySelect('Catálogo que se crea')}
          <Select label="Transición al crearlo (opcional)" value={a.transition ?? ''} onChange={(e) => patch({ transition: e.target.value || undefined })}>
            <option value="">Ninguna</option>
            {transitions.map((t) => <option key={t.name} value={t.name}>{t.label} ({t.name})</option>)}
          </Select>
          <AssignList label="Campos del registro nuevo" value={a.set ?? []} onChange={(set) => patch({ set })} fields={targetFields.filter(writable)} scope={exprScope} ctx={ctx}
            hint="Los obligatorios sin valor por defecto deben asignarse." />
          <CreateLines a={a} patch={patch} ctx={ctx} target={target} exprScope={exprScope} />
          <AssignList label="Capturar en este registro (opcional)" value={a.capture ?? []} onChange={(capture) => patch({ capture })}
            fields={(scope?.fields ?? []).filter(writable)} scope={createdScope} ctx={ctx} addLabel="Agregar captura"
            hint="Valores del registro creado que se guardan en el que disparó la acción (p. ej. su costo real)." />
        </>
      )}

      {a.type === 'rollup' && (
        <>
          <Select label="Destino de la suma" value={a.target !== undefined ? 'ref' : 'match'}
            onChange={(e) => patch(e.target.value === 'ref' ? { target: refFields[0]?.name ?? '', entity: undefined, match: undefined, createMissing: undefined } : { target: undefined })}>
            <option value="ref">El registro al que apunta una referencia</option>
            <option value="match">Un registro encontrado por llave</option>
          </Select>
          {a.target !== undefined ? (
            <Select label="Referencia" value={a.target} onChange={(e) => patch({ target: e.target.value })}>
              {fieldOptions(refFields, 'Elige una referencia…')}
            </Select>
          ) : (
            <>
              {entitySelect('Catálogo destino')}
              <MatchList value={a.match ?? []} onChange={(match) => patch({ match })} target={targetFields} scope={scope?.fields ?? []} />
              <div className="span-all"><Checkbox checked={!!a.createMissing} onChange={(v) => patch({ createMissing: v || undefined })} label="Crear el destino si no existe" /></div>
            </>
          )}
          <Select label="Campo que recibe la suma" value={a.field ?? ''} onChange={(e) => patch({ field: e.target.value || undefined })}>
            {fieldOptions(targetFields.filter((f) => writable(f) && isNumeric(f)), 'Campo numérico del destino…')}
          </Select>
          <Select label="Campo que se suma" value={a.sum ?? ''} onChange={(e) => patch({ sum: e.target.value || undefined })}>
            {fieldOptions((scope?.fields ?? []).filter(isNumeric), 'Campo numérico de este registro…')}
          </Select>
          <ExprInput condition className="span-all" label="Filtro de lo que se suma (opcional)" value={a.filter ?? ''} onChange={(v) => patch({ filter: v })} scope={exprScope} entities={ctx.entities} />
        </>
      )}

      {a.type === 'cost' && (
        <>
          {entitySelect('Catálogo del saldo valuado')}
          <MatchList value={a.match ?? []} onChange={(match) => patch({ match })} target={targetFields} scope={ctx.fields} />
          <Select label="Campo del saldo que recibe el valor" value={a.field ?? ''} onChange={(e) => patch({ field: e.target.value || undefined })}>
            {fieldOptions(targetFields.filter((f) => writable(f) && isNumeric(f)), 'Campo numérico…')}
          </Select>
          <Select label="Campo del saldo con la cantidad costeada (opcional)" value={a.quantityField ?? ''} onChange={(e) => patch({ quantityField: e.target.value || undefined })}>
            {fieldOptions(targetFields.filter((f) => writable(f) && isNumeric(f)), 'Ninguno')}
          </Select>
          <Select label="Cantidad (con signo)" value={a.quantity ?? ''} onChange={(e) => patch({ quantity: e.target.value || undefined })}>
            {fieldOptions(ctx.fields.filter(isNumeric), 'Campo numérico…')}
          </Select>
          <Select label="Método" value={METHODS.some((m) => m.v === a.method) ? a.method : a.method ? 'expr' : ''}
            onChange={(e) => patch({ method: e.target.value === 'expr' ? (METHODS.some((m) => m.v === a.method) ? '' : a.method) || "IF(TRUE, 'average', 'fifo')" : e.target.value || undefined })}>
            <option value="">Elige…</option>
            {METHODS.map((m) => <option key={m.v} value={m.v}>{m.l}</option>)}
            <option value="expr">Según una expresión</option>
          </Select>
          {a.method && !METHODS.some((m) => m.v === a.method) && (
            <ExprInput className="span-all" label="Expresión del método" value={a.method} onChange={(v) => patch({ method: v })} scope={exprScope} entities={ctx.entities}
              hint="Debe dar 'average' o 'fifo', p. ej. según el producto." />
          )}
          <ExprInput className="span-all" label="Costo unitario de una entrada (opcional)" value={a.unitCost ?? ''} onChange={(v) => patch({ unitCost: v })} scope={exprScope} entities={ctx.entities} />
          <ExprInput className="span-all" label="Costo total exacto de una entrada (opcional)" value={a.totalCost ?? ''} onChange={(v) => patch({ totalCost: v })} scope={exprScope} entities={ctx.entities}
            hint="Para traspasos: la entrada vale exactamente lo que costó la salida." />
          <ExprInput className="span-all" label="Costo de respaldo (opcional)" value={a.fallbackCost ?? ''} onChange={(v) => patch({ fallbackCost: v })} scope={exprScope} entities={ctx.entities}
            hint="Para salidas sin capas suficientes o entradas sin costo, según la política." />
          <Select label="Campo de este registro que recibe el costo" value={a.costField ?? ''} onChange={(e) => patch({ costField: e.target.value || undefined })}>
            {fieldOptions(ctx.fields.filter((f) => writable(f) && isNumeric(f)), 'Campo numérico…')}
          </Select>
          <Select label="Campo que recibe el costo unitario (opcional)" value={a.unitCostField ?? ''} onChange={(e) => patch({ unitCostField: e.target.value || undefined })}>
            {fieldOptions(ctx.fields.filter((f) => writable(f) && isNumeric(f)), 'Ninguno')}
          </Select>
          <Select label="Entrada sin costo" value={a.uncostedEntry ?? ''} onChange={(e) => patch({ uncostedEntry: (e.target.value || undefined) as Action['uncostedEntry'] })}>
            <option value="">Al costo promedio vigente (predeterminado)</option>
            <option value="average">Al costo promedio vigente</option>
            <option value="fallback">Al costo de respaldo</option>
            <option value="require">No se permite</option>
          </Select>
          <Select label="Al cambiar de método" value={a.methodChange ?? ''} onChange={(e) => patch({ methodChange: (e.target.value || undefined) as Action['methodChange'] })}>
            <option value="">Fundir las capas (predeterminado)</option>
            <option value="merge">Fundir las capas</option>
            <option value="keep">Conservar las capas</option>
          </Select>
        </>
      )}
    </div>
  );
}

function CreateLines({ a, patch, ctx, target, exprScope }: { a: Action; patch: (p: Partial<Action>) => void; ctx: SectionCtx; target?: { fields: FieldDef[] }; exprScope: ExprScope | null }) {
  const lines = activeFields(target).filter((f) => f.type === 'lines');
  const list = a.createLines ?? [];
  if (!lines.length && !list.length) return null;
  const set = (next: typeof list) => patch({ createLines: next });
  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">Partidas del registro nuevo (opcional)</legend>
      {list.map((lt, i) => {
        const child = entityByName(ctx, lines.find((f) => f.name === lt.field)?.childEntity);
        return (
          <div className="subitem" key={i}>
            <div className="item-grid">
              <Select label={`Partida ${i + 1}: campo de partidas`} value={lt.field ?? ''} onChange={(e) => set(list.map((x, k) => (k === i ? { ...x, field: e.target.value } : x)))}>
                {fieldOptions(lines, 'Elige…')}
              </Select>
              <ExprInput condition label={`Partida ${i + 1}: solo si (opcional)`} value={lt.when ?? ''} scope={exprScope} entities={ctx.entities}
                onChange={(v) => set(list.map((x, k) => (k === i ? clean({ ...x, when: v }) : x)))} />
              <AssignList label={`Partida ${i + 1}: campos`} value={lt.set ?? []} onChange={(s) => set(list.map((x, k) => (k === i ? { ...x, set: s } : x)))}
                fields={activeFields(child).filter(writable)} scope={exprScope} ctx={ctx} />
            </div>
            <Button size="sm" variant="ghost" icon={Trash2} onClick={() => set(list.filter((_, k) => k !== i))}>Quitar partida {i + 1}</Button>
          </div>
        );
      })}
      <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => set([...list, { field: lines[0]?.name ?? '', set: [] }])}>Agregar partida</Button></div>
    </fieldset>
  );
}

/** Spec 026 (H4 CA2/CA7): acciones de una automatización o de una tarea programada (mismas reglas en el servidor). */
export function ActionsEditor({ actions, onChange, ctx, schedule = false }: { actions: Action[]; onChange: (a: Action[]) => void; ctx: SectionCtx; schedule?: boolean }) {
  const isChild = ctx.fields.some((f) => f.type === 'parent');
  const move = (i: number, d: -1 | 1) => { const n = actions.slice(); [n[i], n[i + d]] = [n[i + d], n[i]]; onChange(n); };
  return (
    <fieldset className="subgroup span-all actions">
      <legend className="field-label">Hacer</legend>
      {actions.length === 0 && <p className="field-hint">Sin acciones todavía: agrega al menos una.</p>}
      <ol className="action-list">
        {actions.map((a, i) => (
          <li key={i} className="action">
            <div className="action-head">
              <Zap size={14} aria-hidden />
              <span className="grow">Acción {i + 1}: {ACTION_LABELS[a.type] ?? a.type}</span>
              <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} aria-label={`Subir acción ${i + 1}`} disabled={i === 0} onClick={() => move(i, -1)} />
              <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} aria-label={`Bajar acción ${i + 1}`} disabled={i === actions.length - 1} onClick={() => move(i, 1)} />
              <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar acción ${i + 1}`} onClick={() => onChange(actions.filter((_, k) => k !== i))} />
            </div>
            <ActionForm a={a} ctx={ctx} isChild={isChild} schedule={schedule}
              patch={(p) => onChange(actions.map((x, k) => (k === i ? clean(typeChange(x, p)) : x)))} />
          </li>
        ))}
      </ol>
      <div><Button size="sm" icon={Plus} onClick={() => onChange([...actions, { type: schedule ? 'transition' : 'update' }])}>Agregar acción</Button></div>
    </fieldset>
  );
}

/** Al cambiar el tipo se quitan las propiedades que el nuevo tipo no admite (el servidor las rechazaría). */
function typeChange(a: Action, p: Partial<Action>): Action {
  if (!p.type || p.type === a.type) return { ...a, ...p };
  return { type: p.type, lines: p.type === 'cost' ? undefined : a.lines, when: a.when };
}
