import { ArrowDown, ArrowUp, BarChart3, ListFilter, Plus, Trash2 } from 'lucide-react';
import type { FieldDef, FilterOp, ReportDef, ViewDef, ViewFilter } from '../../../api/types';
import { ExprInput } from '../../../components/ExprInput';
import { kindOf, OP_LABELS, OPS } from '../../../components/FilterBuilder';
import { Badge, Button, Input, Select, Switch } from '../../../components/ui';
import { clean, entityByName, fieldOptions, isNumeric, ItemList, MultiPick, roleOptions, selfScope, uniqueName, type SectionCtx } from './common';

/** Columnas en el orden elegido: agregar de una lista, subir, bajar y quitar. */
function OrderedPick({ legend, value, onChange, fields, hint }: { legend: string; value: string[]; onChange: (v: string[]) => void; fields: FieldDef[]; hint?: string }) {
  const label = (n: string) => fields.find((f) => f.name === n)?.label ?? `${n} (no existe)`;
  const move = (i: number, d: -1 | 1) => { const n = value.slice(); [n[i], n[i + d]] = [n[i + d], n[i]]; onChange(n); };
  const free = fields.filter((f) => !value.includes(f.name));
  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">{legend}</legend>
      {hint && <p className="field-hint">{hint}</p>}
      {value.length > 0 && (
        <ol className="ordered-pick">
          {value.map((n, i) => (
            <li key={n}>
              <span className="grow">{label(n)}</span>
              <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} aria-label={`Subir ${label(n)}`} disabled={i === 0} onClick={() => move(i, -1)} />
              <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} aria-label={`Bajar ${label(n)}`} disabled={i === value.length - 1} onClick={() => move(i, 1)} />
              <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar ${label(n)}`} onClick={() => onChange(value.filter((x) => x !== n))} />
            </li>
          ))}
        </ol>
      )}
      {free.length > 0 && (
        <Select aria-label={`${legend}: agregar`} value="" onChange={(e) => e.target.value && onChange([...value, e.target.value])}>
          {fieldOptions(free, 'Agregar columna…')}
        </Select>
      )}
    </fieldset>
  );
}

function FilterValue({ f, flt, onChange, states }: { f?: FieldDef; flt: ViewFilter; onChange: (v: unknown) => void; states: { name: string; label: string }[] }) {
  if (flt.op === 'blank' || flt.op === 'notBlank') return <span className="field-hint">Sin valor</span>;
  const kind = f ? kindOf(f) : 'text';
  const label = `Valor de ${f?.label ?? flt.field}`;
  const opts = f?.type === 'state' ? states.map((s) => ({ value: s.name, label: s.label })) : (f?.options ?? []).map((o) => ({ value: o, label: o }));
  if (kind === 'choice' && flt.op === 'in') {
    const v = Array.isArray(flt.value) ? (flt.value as string[]) : [];
    return <MultiPick legend={label} options={opts} value={v} onChange={onChange} />;
  }
  if (kind === 'choice') {
    return <Select aria-label={label} value={String(flt.value ?? '')} onChange={(e) => onChange(e.target.value)}>
      <option value="">Elige…</option>{opts.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </Select>;
  }
  if (kind === 'boolean') {
    return <Select aria-label={label} value={flt.value === false ? 'false' : 'true'} onChange={(e) => onChange(e.target.value === 'true')}>
      <option value="true">Sí</option><option value="false">No</option>
    </Select>;
  }
  if (kind === 'number') return <Input aria-label={label} type="number" value={flt.value === undefined ? '' : String(flt.value)} onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))} />;
  const hint = kind === 'date' ? '$today = hoy' : f?.type === 'user' ? '$me = quien consulta' : kind === 'id' ? 'Id del registro' : undefined;
  return <Input aria-label={label} value={String(flt.value ?? '')} placeholder={hint} title={hint}
    onChange={(e) => onChange(kind === 'id' && /^\d+$/.test(e.target.value) ? Number(e.target.value) : e.target.value)} />;
}

function FilterList({ value, onChange, fields, states }: { value: ViewFilter[]; onChange: (v: ViewFilter[]) => void; fields: FieldDef[]; states: { name: string; label: string }[] }) {
  const usable = fields.filter((f) => f.type !== 'lines' && f.type !== 'parent');
  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">Filtros</legend>
      {value.map((flt, i) => {
        const f = usable.find((x) => x.name === flt.field);
        const ops = f ? OPS[kindOf(f)] : (['eq'] as FilterOp[]);
        const patch = (p: Partial<ViewFilter>) => onChange(value.map((x, k) => (k === i ? ({ ...x, ...p }) : x)));
        return (
          <div className="filter-row-editor" key={i}>
            <Select aria-label={`Filtro ${i + 1}: campo`} value={flt.field ?? ''} onChange={(e) => {
              const nf = usable.find((x) => x.name === e.target.value);
              onChange(value.map((x, k) => (k === i ? { field: e.target.value, op: nf ? OPS[kindOf(nf)][0] : 'eq' } : x)));
            }}>
              {fieldOptions(usable, 'Campo…')}
            </Select>
            <Select aria-label={`Filtro ${i + 1}: operador`} value={flt.op} onChange={(e) => patch({ op: e.target.value as FilterOp, value: undefined })}>
              {ops.map((o) => <option key={o} value={o}>{OP_LABELS[o]}</option>)}
            </Select>
            <FilterValue f={f} flt={flt} states={states} onChange={(v) => patch({ value: v })} />
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar filtro ${i + 1}`} onClick={() => onChange(value.filter((_, k) => k !== i))} />
          </div>
        );
      })}
      <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => onChange([...value, { field: '', op: 'eq' }])}>Agregar filtro</Button></div>
    </fieldset>
  );
}

/** Spec 026 (H4 CA4): vistas compartidas del listado (007). */
export function ViewsEditor({ views, onChange, ctx }: { views: ViewDef[]; onChange: (v: ViewDef[]) => void; ctx: SectionCtx }) {
  const listFields = ctx.fields.filter((f) => f.type !== 'lines' && f.type !== 'parent');
  return (
    <ItemList<ViewDef>
      items={views} onChange={onChange} kind="view" ctx={ctx} icon={ListFilter} noun="vista"
      emptyTitle="Este catálogo no tiene vistas compartidas"
      emptyDescription="Una vista guarda columnas, filtros y orden del listado para los roles que elijas."
      addLabel="Agregar vista"
      create={(items) => ({ name: uniqueName('Vista', items.map((v) => v.name)), label: 'Nueva vista' })}
      name={(v) => v.name} title={(v) => v.label}
      summary={(v) => <>{v.default && <Badge tone="accent">Predeterminada</Badge>}<span>{(v.filters ?? []).length} filtro(s) · {(v.columns ?? []).length || 'todas las'} columna(s)</span></>}
      render={(v, patch) => (
        <div className="item-grid">
          <Input label="Nombre visible" required value={v.label ?? ''} maxLength={100} onChange={(e) => patch({ label: e.target.value })} />
          <Input label="Identificador" required className="mono" value={v.name ?? ''} maxLength={60} onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
          <OrderedPick legend="Columnas" value={v.columns ?? []} onChange={(columns) => patch({ columns })} fields={listFields} hint="Ninguna: las del listado." />
          <FilterList value={v.filters ?? []} onChange={(filters) => patch({ filters })} fields={ctx.fields} states={ctx.states} />
          <Select label="Ordenar por" value={v.sort ?? ''} onChange={(e) => patch({ sort: e.target.value || undefined })}>
            {fieldOptions(listFields, 'Predeterminado')}
          </Select>
          <Select label="Dirección" value={v.dir ?? 'asc'} onChange={(e) => patch({ dir: e.target.value === 'desc' ? 'desc' : undefined })}>
            <option value="asc">Ascendente</option><option value="desc">Descendente</option>
          </Select>
          {ctx.states.length > 0 && (
            <Select label="Estado" value={v.state ?? ''} onChange={(e) => patch({ state: e.target.value || undefined })}>
              <option value="">Todos</option>
              {ctx.states.map((s) => <option key={s.name} value={s.name}>{s.label}</option>)}
            </Select>
          )}
          <div className="field"><span className="field-label">Al abrir el listado</span>
            <Switch checked={!!v.default} onChange={(d) => patch({ default: d || undefined })} label="Vista predeterminada" /></div>
          <MultiPick legend="Roles que la ven" options={roleOptions(ctx)} value={v.roles ?? []} onChange={(roles) => patch({ roles })} hint="Ninguno: todos los que ven el catálogo." />
          <p className="field-hint span-all">La etiqueta de presentación de un filtro (`label`) solo se edita en «Ver como JSON».</p>
        </div>
      )}
    />
  );
}

/** Campos para agrupar: propios y un salto por referencia (`Proveedor.Pais`); más saltos, en el JSON. */
function groupOptions(ctx: SectionCtx) {
  const own = ctx.fields.filter((f) => f.type !== 'lines' && f.type !== 'parent');
  const hops = own.filter((f) => f.type === 'reference').flatMap((f) => (entityByName(ctx, f.referenceEntity)?.fields ?? [])
    .filter((x) => !x.isArchived && x.type !== 'lines').map((x) => ({ value: `${f.name}.${x.name}`, label: `${f.label} › ${x.label}`, type: x.type })));
  return [...own.map((f) => ({ value: f.name, label: f.label, type: f.type })), ...hops];
}

const MEASURES = { count: 'Conteo', sum: 'Suma', avg: 'Promedio', min: 'Mínimo', max: 'Máximo' } as const;
type Measure = ReportDef['measures'][number];
type Group = NonNullable<ReportDef['groupBy']>[number];

/** Spec 026 (H4 CA5): reportes agrupados (007). */
export function ReportsEditor({ reports, onChange, ctx }: { reports: ReportDef[]; onChange: (r: ReportDef[]) => void; ctx: SectionCtx }) {
  const groups = groupOptions(ctx);
  const numeric = ctx.fields.filter(isNumeric);
  return (
    <ItemList<ReportDef>
      items={reports} onChange={onChange} kind="report" ctx={ctx} icon={BarChart3} noun="reporte"
      emptyTitle="Este catálogo no tiene reportes"
      emptyDescription="Un reporte agrupa los registros y calcula totales; se consulta en Análisis y se exporta."
      addLabel="Agregar reporte"
      create={(items) => ({ name: uniqueName('Reporte', items.map((r) => r.name)), label: 'Nuevo reporte', measures: [{ fn: 'count' }] })}
      name={(r) => r.name} title={(r) => r.label}
      summary={(r) => <span>{(r.groupBy ?? []).map((g) => g.field).join(' y ') || 'sin agrupar'} · {(r.measures ?? []).map((m) => MEASURES[m.fn]).join(', ')}</span>}
      render={(r, patch) => {
        const gb = r.groupBy ?? [];
        const ms = r.measures ?? [];
        const setGroup = (i: number, p: Partial<Group>) => patch({ groupBy: gb.map((g, k) => (k === i ? clean({ ...g, ...p }) as Group : g)) });
        const setMeasure = (i: number, p: Partial<Measure>) => patch({ measures: ms.map((m, k) => (k === i ? clean({ ...m, ...p }) as Measure : m)) });
        return (
          <div className="item-grid">
            <Input label="Nombre visible" required value={r.label ?? ''} maxLength={100} onChange={(e) => patch({ label: e.target.value })} />
            <Input label="Identificador" required className="mono" value={r.name ?? ''} maxLength={60} onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
            <Input wrapClassName="span-all" label="Descripción (opcional)" value={r.description ?? ''} maxLength={300} onChange={(e) => patch({ description: e.target.value })} />
            <fieldset className="subgroup span-all">
              <legend className="field-label">Agrupar por (hasta 2)</legend>
              {gb.map((g, i) => {
                const t = groups.find((x) => x.value === g.field)?.type;
                return (
                  <div className="assign-row" key={i}>
                    <Select aria-label={`Agrupación ${i + 1}: campo`} value={g.field ?? ''} onChange={(e) => setGroup(i, { field: e.target.value, period: undefined })}>
                      <option value="">Campo…</option>
                      {groups.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                      {g.field && !groups.some((o) => o.value === g.field) && <option value={g.field}>{g.field} (ruta del JSON)</option>}
                    </Select>
                    {t === 'date' || t === 'datetime' ? (
                      <Select aria-label={`Agrupación ${i + 1}: periodo`} value={g.period ?? ''} onChange={(e) => setGroup(i, { period: (e.target.value || undefined) as Group['period'] })}>
                        <option value="">Fecha exacta</option><option value="day">Por día</option><option value="month">Por mes</option><option value="year">Por año</option>
                      </Select>
                    ) : <span />}
                    <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar agrupación ${i + 1}`} onClick={() => patch({ groupBy: gb.filter((_, k) => k !== i) })} />
                  </div>
                );
              })}
              {gb.length < 2 && <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ groupBy: [...gb, { field: '' }] })}>Agregar agrupación</Button></div>}
              <p className="field-hint">Se ofrecen campos propios y un salto por referencia; rutas más largas, en «Ver como JSON».</p>
            </fieldset>
            <fieldset className="subgroup span-all">
              <legend className="field-label">Medidas</legend>
              {ms.map((m, i) => (
                <div className="assign-row" key={i}>
                  <Select aria-label={`Medida ${i + 1}: cálculo`} value={m.fn} onChange={(e) => setMeasure(i, { fn: e.target.value as Measure['fn'], field: e.target.value === 'count' ? undefined : m.field })}>
                    {(Object.keys(MEASURES) as Measure['fn'][]).map((k) => <option key={k} value={k}>{MEASURES[k]}</option>)}
                  </Select>
                  {m.fn === 'count' ? <span className="field-hint">De registros</span> : (
                    <Select aria-label={`Medida ${i + 1}: campo`} value={m.field ?? ''} onChange={(e) => setMeasure(i, { field: e.target.value || undefined })}>
                      {fieldOptions(numeric, 'Campo numérico…')}
                    </Select>
                  )}
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar medida ${i + 1}`} disabled={ms.length === 1} onClick={() => patch({ measures: ms.filter((_, k) => k !== i) })} />
                </div>
              ))}
              <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ measures: [...ms, { fn: 'sum' }] })}>Agregar medida</Button></div>
            </fieldset>
            <ExprInput condition className="span-all" label="Filtro (opcional)" value={r.filter ?? ''} onChange={(v) => patch({ filter: v })} scope={selfScope(ctx)} entities={ctx.entities} />
            <Select label="Ordenar por" value={r.orderBy ?? 'group'} onChange={(e) => patch({ orderBy: e.target.value === 'measure' ? 'measure' : undefined })}>
              <option value="group">La agrupación</option><option value="measure">La primera medida</option>
            </Select>
            <Select label="Dirección" value={r.dir ?? 'asc'} onChange={(e) => patch({ dir: e.target.value === 'desc' ? 'desc' : undefined })}>
              <option value="asc">Ascendente</option><option value="desc">Descendente</option>
            </Select>
            <Input label="Límite de filas (opcional)" type="number" min={1} max={1000} value={r.limit ?? ''} onChange={(e) => patch({ limit: e.target.value ? Number(e.target.value) : undefined })} />
            <Select label="Gráfica" value={r.chart ?? ''} onChange={(e) => patch({ chart: (e.target.value || undefined) as ReportDef['chart'] })}>
              <option value="">Sin gráfica</option><option value="bar">Barras</option><option value="line">Línea</option><option value="pie">Pastel</option>
            </Select>
            <MultiPick legend="Roles que lo consultan" options={roleOptions(ctx)} value={r.roles ?? []} onChange={(roles) => patch({ roles })} hint="Ninguno: todos los que ven el catálogo." />
          </div>
        );
      }}
    />
  );
}
