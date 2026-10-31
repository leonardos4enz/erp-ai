import { ArrowDown, ArrowUp, BarChart3, ListFilter, Plus, Trash2 } from 'lucide-react';
import type { ChartKind, ChartOptions, FieldDef, FilterOp, ReportDef, ViewDef, ViewFilter } from '../../../api/types';
import { PALETTE, PALETTE_LABELS, seriesColor } from '../../../lib/reportChart';
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

interface PathOption { value: string; label: string; type: string; field: FieldDef }

/**
 * Campos para agrupar y medir: propios y (spec 039) a uno o dos saltos por referencia o por el documento padre
 * (`Producto.Categoria.Nombre`, `OrdenCompra.Proveedor.Nombre`).
 */
export function pathOptions(ctx: SectionCtx, maxHops = 2): PathOption[] {
  const out: PathOption[] = [];
  const walk = (fields: FieldDef[], prefix: string, labelPrefix: string, depth: number) => {
    for (const f of fields) {
      if (f.isArchived || f.type === 'lines') continue;
      const value = prefix + f.name;
      const label = labelPrefix + (f.label || f.name);
      if (f.type !== 'parent') out.push({ value, label, type: f.type, field: f });
      if (depth < maxHops && (f.type === 'reference' || f.type === 'parent')) {
        const target = entityByName(ctx, f.referenceEntity);
        if (target) walk(target.fields ?? [], `${value}.`, `${label} › `, depth + 1);
      }
    }
  };
  walk(ctx.fields, '', '', 0);
  return out;
}

const MEASURES = { count: 'Conteo', sum: 'Suma', avg: 'Promedio', min: 'Mínimo', max: 'Máximo', calc: 'Calculada' } as const;
const PERIODS: Record<string, string> = { day: 'Por día', week: 'Por semana', month: 'Por mes', quarter: 'Por trimestre', year: 'Por año' };
const CHARTS: Record<ChartKind, string> = {
  column: 'Columnas', bar: 'Barras horizontales', line: 'Línea', area: 'Área', pie: 'Pastel', donut: 'Dona', combo: 'Combinada (columnas y línea)', kpi: 'Indicador',
};
type Measure = ReportDef['measures'][number];
type Group = NonNullable<ReportDef['groupBy']>[number];

/**
 * Paleta cerrada: «Automático» deja el color de su lugar fijo, y la muestra lo enseña (revisión exploratoria 039, F-03: con
 * «Automático» la muestra salía vacía y no se sabía qué color tocaría).
 */
function ColorPick({ label, slot, value, onChange }: { label: string; slot: number; value?: string; onChange: (v?: string) => void }) {
  return (
    <div className="color-pick">
      <span className="chart-swatch" data-color={value ?? PALETTE[slot % PALETTE.length]} aria-hidden style={{ background: seriesColor(slot, value ? [...Array(slot).fill(''), value] : null) }} />
      <Select aria-label={label} value={value ?? ''} onChange={(e) => onChange(e.target.value || undefined)}>
        <option value="">Automático</option>
        {PALETTE.map((p) => <option key={p} value={p}>{PALETTE_LABELS[p]}</option>)}
      </Select>
    </div>
  );
}

/** Colores en el orden de las series; los que quedan sin elegir antes de uno elegido toman su lugar fijo. */
function setColor(colors: string[] | undefined, k: number, c: string | undefined): string[] | undefined {
  const list = [...(colors ?? [])];
  while (list.length <= k) list.push(PALETTE[list.length]);
  list[k] = c ?? PALETTE[k];
  while (list.length && list[list.length - 1] === PALETTE[list.length - 1]) list.pop(); // el lugar fijo no hace falta escribirlo
  return list.length ? list : undefined;
}

/** Spec 026 (H4 CA5): reportes agrupados (007); spec 039: gráficas, calculadas, periodos y rutas a dos saltos. */
export function ReportsEditor({ reports, onChange, ctx }: { reports: ReportDef[]; onChange: (r: ReportDef[]) => void; ctx: SectionCtx }) {
  const paths = pathOptions(ctx);
  const numeric = paths.filter((o) => isNumeric(o.field));
  const isDate = (field?: string) => ['date', 'datetime'].includes(paths.find((x) => x.value === field)?.type ?? '');
  return (
    <ItemList<ReportDef>
      items={reports} onChange={onChange} kind="report" ctx={ctx} icon={BarChart3} noun="reporte"
      emptyTitle="Este catálogo no tiene reportes"
      emptyDescription="Un reporte agrupa los registros y calcula totales; se consulta en Análisis, se grafica y se exporta."
      addLabel="Agregar reporte"
      create={(items) => ({ name: uniqueName('Reporte', items.map((r) => r.name)), label: 'Nuevo reporte', measures: [{ fn: 'count' }] })}
      name={(r) => r.name} title={(r) => r.label}
      summary={(r) => <span>{(r.groupBy ?? []).map((g) => g.field).join(' y ') || 'sin agrupar'} · {(r.measures ?? []).map((m) => MEASURES[m.fn]).join(', ')}{r.chart ? ` · ${CHARTS[r.chart]}` : ''}</span>}
      render={(r, patch) => {
        const gb = r.groupBy ?? [];
        const ms = r.measures ?? [];
        const o = r.chartOptions ?? {};
        const setGroup = (i: number, p: Partial<Group>) => patch({ groupBy: gb.map((g, k) => (k === i ? clean({ ...g, ...p }) as Group : g)) });
        const setMeasure = (i: number, p: Partial<Measure>) => patch({ measures: ms.map((m, k) => (k === i ? clean({ ...m, ...p }) as Measure : m)) });
        const setOptions = (p: Partial<ChartOptions>) => {
          const next = clean({ ...o, ...p });
          patch({ chartOptions: Object.keys(next).length ? next : undefined });
        };
        // Quitar una medida corrige los índices de la gráfica que la seguían.
        const removeMeasure = (i: number) => {
          const shift = (list?: number[]) => list?.filter((x) => x !== i).map((x) => (x > i ? x - 1 : x));
          const next = clean({ ...o, measures: shift(o.measures), lines: shift(o.lines) });
          patch({ measures: ms.filter((_, k) => k !== i), chartOptions: r.chart && Object.keys(next).length ? next : undefined });
        };
        const measureLabel = (i: number) => {
          const m = ms[i];
          if (!m) return `Medida ${i + 1}`;
          const f = m.fn === 'calc' ? m.label || m.name || m.expr : paths.find((x) => x.value === m.field)?.label ?? m.field;
          return `${i + 1}. ${MEASURES[m.fn]}${f ? ` · ${f}` : ''}`;
        };
        // Un mínimo o un máximo de una fecha no se grafica.
        const plottable = ms.map((m, i) => ({ m, i })).filter(({ m }) => (m.fn !== 'min' && m.fn !== 'max') || numeric.some((p) => p.value === m.field));
        const plotted = o.measures?.length ? o.measures : [0];
        const chart = r.chart;
        const bySeriesGroup = gb.length === 2 || chart === 'pie' || chart === 'donut';
        const single = chart === 'pie' || chart === 'donut' || chart === 'kpi' || gb.length === 2;
        const seriesCount = bySeriesGroup ? Math.min(12, (o.colors?.length ?? 0) + 1) : plotted.length;
        const names = ms.filter((m) => m.fn !== 'calc' && m.name).map((m) => m.name!);
        return (
          <div className="item-grid">
            <Input label="Nombre visible" required value={r.label ?? ''} maxLength={100} onChange={(e) => patch({ label: e.target.value })} />
            <Input label="Identificador" required className="mono" value={r.name ?? ''} maxLength={60} onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
            <Input wrapClassName="span-all" label="Descripción (opcional)" value={r.description ?? ''} maxLength={300} onChange={(e) => patch({ description: e.target.value })} />
            <fieldset className="subgroup span-all">
              <legend className="field-label">Agrupar por (hasta 2)</legend>
              {gb.map((g, i) => (
                <div className="assign-row" key={i}>
                  <Select aria-label={`Agrupación ${i + 1}: campo`} value={g.field ?? ''} onChange={(e) => setGroup(i, { field: e.target.value, period: isDate(e.target.value) ? 'month' : undefined })}>
                    <option value="">Campo…</option>
                    {paths.map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
                    {g.field && !paths.some((p) => p.value === g.field) && <option value={g.field}>{g.field} (ruta del JSON)</option>}
                  </Select>
                  {isDate(g.field) ? (
                    <Select aria-label={`Agrupación ${i + 1}: periodo`} value={g.period ?? 'day'} onChange={(e) => setGroup(i, { period: e.target.value as Group['period'] })}>
                      {Object.entries(PERIODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                    </Select>
                  ) : <span />}
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar agrupación ${i + 1}`} onClick={() => patch({ groupBy: gb.filter((_, k) => k !== i) })} />
                </div>
              ))}
              {gb.length < 2 && <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ groupBy: [...gb, { field: '' }] })}>Agregar agrupación</Button></div>}
              <p className="field-hint">Campos propios y a uno o dos saltos (por una referencia o por el documento). Con dos agrupaciones, la segunda da las series de la gráfica.</p>
            </fieldset>
            <fieldset className="subgroup span-all">
              <legend className="field-label">Medidas</legend>
              {ms.map((m, i) => (
                <div className="measure-row" key={i}>
                  <div className="assign-row">
                    <Select aria-label={`Medida ${i + 1}: cálculo`} value={m.fn} onChange={(e) => {
                      const fn = e.target.value as Measure['fn'];
                      setMeasure(i, fn === 'calc' ? { fn, field: undefined, expr: m.expr ?? '' } : { fn, field: fn === 'count' ? undefined : m.field, expr: undefined, scale: undefined });
                    }}>
                      {(Object.keys(MEASURES) as Measure['fn'][]).map((k) => <option key={k} value={k}>{MEASURES[k]}</option>)}
                    </Select>
                    {m.fn === 'count' ? <span className="field-hint">De registros</span> : m.fn === 'calc' ? (
                      <Input aria-label={`Medida ${i + 1}: expresión`} className="mono" value={m.expr ?? ''} maxLength={500} placeholder="ROUND((Ventas - Costo) / Ventas * 100, 1)"
                        onChange={(e) => setMeasure(i, { expr: e.target.value })} />
                    ) : (
                      <Select aria-label={`Medida ${i + 1}: campo`} value={m.field ?? ''} onChange={(e) => setMeasure(i, { field: e.target.value || undefined })}>
                        <option value="">Campo numérico…</option>
                        {(m.fn === 'min' || m.fn === 'max' ? paths.filter((p) => isNumeric(p.field) || p.type === 'date' || p.type === 'datetime') : numeric)
                          .map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
                        {m.field && !paths.some((p) => p.value === m.field) && <option value={m.field}>{m.field} (ruta del JSON)</option>}
                      </Select>
                    )}
                    <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar medida ${i + 1}`} disabled={ms.length === 1} onClick={() => removeMeasure(i)} />
                  </div>
                  <div className="measure-extra">
                    <Input label={`Medida ${i + 1}: nombre para calcular (opcional)`} className="mono" value={m.name ?? ''} maxLength={60}
                      onChange={(e) => setMeasure(i, { name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') || undefined })} />
                    <Input label={`Medida ${i + 1}: etiqueta (opcional)`} value={m.label ?? ''} maxLength={100} onChange={(e) => setMeasure(i, { label: e.target.value || undefined })} />
                    {m.fn === 'calc' && (
                      <Select label={`Medida ${i + 1}: decimales`} value={String(m.scale ?? 2)} onChange={(e) => setMeasure(i, { scale: Number(e.target.value) })}>
                        {[0, 1, 2, 3, 4, 5, 6].map((n) => <option key={n} value={n}>{n}</option>)}
                      </Select>
                    )}
                  </div>
                </div>
              ))}
              <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ measures: [...ms, { fn: 'sum' }] })}>Agregar medida</Button></div>
              <p className="field-hint">
                Una medida calculada usa el nombre de otras medidas{names.length ? ` (${names.join(', ')})` : ' (dale un nombre a las que uses)'} con
                + − * /, IF, ROUND, ABS, MIN, MAX, COALESCE e ISBLANK. Si un valor está vacío o se divide entre cero, queda vacía; COALESCE(x, 0) lo toma como cero.
              </p>
            </fieldset>
            <ExprInput condition className="span-all" label="Filtro (opcional)" value={r.filter ?? ''} onChange={(v) => patch({ filter: v })} scope={selfScope(ctx)} entities={ctx.entities} />
            <Select label="Ordenar por" value={r.orderBy ?? 'group'} onChange={(e) => patch({ orderBy: e.target.value === 'measure' ? 'measure' : undefined })}>
              <option value="group">La agrupación</option><option value="measure">La primera medida</option>
            </Select>
            <Select label="Dirección" value={r.dir ?? 'asc'} onChange={(e) => patch({ dir: e.target.value === 'desc' ? 'desc' : undefined })}>
              <option value="asc">Ascendente</option><option value="desc">Descendente</option>
            </Select>
            <Input label="Límite de filas (opcional)" type="number" min={1} max={1000} value={r.limit ?? ''} onChange={(e) => patch({ limit: e.target.value ? Number(e.target.value) : undefined })} />
            <MultiPick legend="Roles que lo consultan" options={roleOptions(ctx)} value={r.roles ?? []} onChange={(roles) => patch({ roles })} hint="Ninguno: todos los que ven el catálogo." />
            <fieldset className="subgroup span-all">
              <legend className="field-label">Gráfica</legend>
              <div className="item-grid">
                <Select label="Tipo de gráfica" value={chart ?? ''} onChange={(e) => {
                  const kind = (e.target.value || undefined) as ChartKind | undefined;
                  patch(kind ? { chart: kind } : { chart: undefined, chartOptions: undefined });
                }}>
                  <option value="">Sin gráfica</option>
                  {(Object.keys(CHARTS) as ChartKind[]).map((k) => <option key={k} value={k}>{CHARTS[k]}</option>)}
                </Select>
                {chart && single && (
                  <Select label="Medida de la gráfica" value={String(plotted[0] ?? 0)} onChange={(e) => setOptions({ measures: e.target.value === '0' ? undefined : [Number(e.target.value)], lines: undefined })}>
                    {plottable.map(({ i }) => <option key={i} value={i}>{measureLabel(i)}</option>)}
                  </Select>
                )}
                {chart && !single && (
                  <MultiPick legend="Medidas que se dibujan" options={plottable.map(({ i }) => ({ value: String(i), label: measureLabel(i) }))}
                    value={plotted.map(String)} onChange={(v) => {
                      const list = v.map(Number);
                      setOptions({ measures: list.length && !(list.length === 1 && list[0] === 0) ? list : undefined, lines: o.lines?.filter((x) => list.includes(x)) });
                    }} />
                )}
                {chart === 'combo' && (
                  <MultiPick legend="Como línea" options={plotted.map((i) => ({ value: String(i), label: measureLabel(i) }))}
                    value={(o.lines ?? []).map(String)} onChange={(v) => setOptions({ lines: v.length ? v.map(Number) : undefined })}
                    hint="Las demás van como columnas, sobre el mismo eje." />
                )}
                {(chart === 'column' || chart === 'bar' || chart === 'area') && (
                  <Switch label="Apilar las series" checked={!!o.stacked} onChange={(v) => setOptions({ stacked: v || undefined })} />
                )}
                {chart && chart !== 'kpi' && (
                  <>
                    <Switch label="Mostrar los valores" checked={!!o.values} onChange={(v) => setOptions({ values: v || undefined })} />
                    <Select label="Leyenda" value={o.legend === true ? 'yes' : o.legend === false ? 'no' : ''}
                      onChange={(e) => setOptions({ legend: e.target.value === 'yes' ? true : e.target.value === 'no' ? false : undefined })}>
                      <option value="">Si hay más de una serie</option><option value="yes">Siempre</option><option value="no">Nunca</option>
                    </Select>
                  </>
                )}
              </div>
              {chart && chart !== 'kpi' && (
                <div className="color-grid" role="group" aria-label="Colores de las series">
                  {Array.from({ length: seriesCount }, (_, k) => (
                    <ColorPick key={k} slot={k} label={bySeriesGroup ? `Color de la serie ${k + 1}` : `Color de ${measureLabel(plotted[k])}`}
                      value={o.colors?.[k]} onChange={(c) => setOptions({ colors: setColor(o.colors, k, c) })} />
                  ))}
                </div>
              )}
              <p className="field-hint">
                {chart === 'kpi' ? 'Un indicador muestra el total, o el último periodo contra el anterior si el reporte agrupa solo por una fecha con periodo.'
                  : 'Los colores salen de la paleta del sistema: se distinguen entre sí y se ven bien en claro y en oscuro.'}
              </p>
            </fieldset>
          </div>
        );
      }}
    />
  );
}
