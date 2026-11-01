import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Compass, Copy, FileSpreadsheet, FileText, Filter, Plus, Save, Share2, Trash2 } from 'lucide-react';
import { ApiError } from '../api/client';
import { myReportsApi, reportsApi } from '../api/endpoints';
import type { ChartKind, ChartOptions, PersonalReportDef, ReportResult, ReportSourceField, ViewFilter } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog, Dialog } from '../components/Dialog';
import { FilterChips, FilterDialog } from '../components/FilterBuilder';
import { ReportChart } from '../components/ReportChart';
import { ReportTable } from '../components/ReportTable';
import { ShareDialog } from '../components/ShareDialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, Checkbox, EmptyState, Input, LoadingBlock, PageHeader, Select, Spinner, Switch } from '../components/ui';
import { useAsync, useDebounced } from '../lib/hooks';
import { clean, EXPLORE_DELAY_MS, missingPart } from '../lib/personalReport';
import { NotFoundPage } from './StatusPages';

type Measure = PersonalReportDef['measures'][number];
type Group = NonNullable<PersonalReportDef['groupBy']>[number];

const MEASURES: Record<Measure['fn'], string> = { count: 'Conteo', sum: 'Suma', avg: 'Promedio', min: 'Mínimo', max: 'Máximo', calc: 'Calculada' };
const PERIODS: Record<string, string> = { day: 'Por día', week: 'Por semana', month: 'Por mes', quarter: 'Por trimestre', year: 'Por año' };
const CHARTS: Record<ChartKind, string> = {
  column: 'Columnas', bar: 'Barras horizontales', line: 'Línea', area: 'Área', pie: 'Pastel', donut: 'Dona', combo: 'Combinada (columnas y línea)', kpi: 'Indicador',
};
/**
 * Spec 041: explorar al vuelo y reportes personales. Una sola pantalla: el diseño a la izquierda y el resultado, que se
 * recalcula sin guardar, a la derecha. `/explore` (nuevo o `?from=Catalogo/Reporte`) y `/explore/:id` (uno guardado): una
 * sola ruta con el id opcional, para que guardar por primera vez no vuelva a montar la pantalla (el foco sigue en «Guardar»).
 */
export function ReportExplorerPage() {
  const { id: idParam } = useParams();
  const id = idParam ? Number(idParam) : undefined;
  const [params] = useSearchParams();
  const from = params.get('from');
  const navigate = useNavigate();
  const toast = useToast();
  const { entities } = useAuth();

  const [def, setDef] = useState<PersonalReportDef | null>(null);
  const [label, setLabel] = useState('');
  const [saved, setSaved] = useState<{ id: number; version: number; snapshot: string } | null>(null);
  const [notice, setNotice] = useState<{ fixedFilter?: boolean; problems?: string[] } | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [loaded, setLoaded] = useState(!id && !from);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [naming, setNaming] = useState<'save' | 'copy' | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [busy, setBusy] = useState(false);
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  const saveButton = useRef<HTMLButtonElement>(null);
  // Spec 042: con qué roles está compartido (solo el dueño) y, si me lo compartieron, quién.
  const [sharing, setSharing] = useState<{ roles: string[]; shareable: string[] } | null>(null);
  const [sharedBy, setSharedBy] = useState<string | null>(null);
  const [shareOpen, setShareOpen] = useState(false);
  const shareButton = useRef<HTMLButtonElement>(null);

  // Abrir uno guardado o la definición de un reporte del modelo.
  useEffect(() => {
    const ctrl = new AbortController();
    setLoadError(null);
    setSharing(null);
    setSharedBy(null);
    if (id) {
      myReportsApi.get(id, ctrl.signal).then((r) => {
        setDef(r.definition);
        setLabel(r.label);
        // Spec 042 (H2 CA2): uno compartido conmigo se abre sin «Guardar» ni «Borrar» (no es mío): «Guardar como mío» lo copia.
        if (r.owned) {
          setSaved({ id: r.id, version: r.version, snapshot: JSON.stringify([r.label, r.definition]) });
          setSharing({ roles: r.roles ?? [], shareable: r.shareableRoles ?? [] });
        } else {
          setSaved(null);
          setSharedBy(r.owner ?? '');
        }
        setNotice(r.available ? null : { problems: r.problems });
        setLoaded(true);
      }).catch((e) => { if (!ctrl.signal.aborted) { setLoadError(e); setLoaded(true); } });
    } else if (from) {
      const [entity, name] = from.split('/');
      // Regresión (revisión adversarial 041, F-G): la ruta es una sola (`explore/:id?`) y la pantalla no se vuelve a montar;
      // sin esto, «Guardar» seguía apuntando al reporte guardado de antes y lo sobrescribía con el del modelo.
      setSaved(null);
      reportsApi.run(entity ?? '', name ?? '', [], ctrl.signal).then((r) => {
        if (r.definition) setDef(r.definition);
        setLabel(r.label);
        setNotice(r.fixedFilter ? { fixedFilter: true } : null);
        setLoaded(true);
      }).catch((e) => { if (!ctrl.signal.aborted) { setLoadError(e); setLoaded(true); } });
    } else {
      setDef(null); setLabel(''); setSaved(null); setNotice(null); setLoaded(true);
    }
    return () => ctrl.abort();
  }, [id, from]);

  const sources = useAsync((s) => myReportsApi.sources(s), []);
  const entity = def?.entity;
  const fields = useAsync((s) => (entity ? myReportsApi.fields(entity, s) : Promise.resolve(undefined)), [entity]);
  const paths = fields.data?.fields ?? [];
  const byPath = useMemo(() => new Map(paths.map((f) => [f.path, f])), [paths]);
  // Los filtros usan el diálogo del listado: solo en catálogos con listado (las partidas se filtran desde su documento).
  const listDef = entity ? entities.find((e) => e.name.toLowerCase() === entity.toLowerCase()) : undefined;

  // Resultado en vivo: espera a que la persona deje de cambiar y descarta la respuesta de una definición anterior.
  const key = def ? JSON.stringify([def, label.trim()]) : '';
  const debounced = useDebounced(key, EXPLORE_DELAY_MS);
  const [result, setResult] = useState<ReportResult | null>(null);
  const [runError, setRunError] = useState<unknown>(null);
  const [running, setRunning] = useState(false);
  const missing = def ? missingPart(def) : null;
  useEffect(() => {
    if (!debounced) { setResult(null); return; }
    const [d, l] = JSON.parse(debounced) as [PersonalReportDef, string];
    if (missingPart(d)) return;
    const ctrl = new AbortController();
    setRunning(true);
    myReportsApi.explore(d, l || undefined, ctrl.signal)
      .then((r) => { if (!ctrl.signal.aborted) { setResult(r); setRunError(null); } })
      .catch((e) => { if (!ctrl.signal.aborted) { setRunError(e); setResult(null); } })
      .finally(() => { if (!ctrl.signal.aborted) setRunning(false); });
    return () => ctrl.abort();
  }, [debounced]);

  const dirty = !!def && (!saved || saved.snapshot !== JSON.stringify([label, def]));
  const patch = (p: Partial<PersonalReportDef>) => setDef((d) => (d ? clean({ ...d, ...p }) as PersonalReportDef : d));

  if (!loaded) return <LoadingBlock />;
  if (loadError instanceof ApiError && loadError.status === 404) return <NotFoundPage />;
  if (loadError) return <div className="page"><Alert tone="danger" title={loadError instanceof ApiError ? loadError.title : 'No se pudo abrir el reporte'} /></div>;

  async function save(asNew: boolean, name: string) {
    if (!def) return;
    setBusy(true);
    setSaveError(null);
    try {
      const r = !asNew && saved ? await myReportsApi.update(saved.id, name, def, saved.version) : await myReportsApi.create(name, def);
      setLabel(r.label);
      setSaved({ id: r.id, version: r.version, snapshot: JSON.stringify([r.label, def]) });
      setNotice(null);
      setNaming(null);
      // Revisión exploratoria 041 (F-05): el diálogo devolvía el foco a «Guardar como mío», que al guardar se vuelve
      // «Guardar», y quedaba en el contenido principal; sigue en «Guardar».
      window.setTimeout(() => saveButton.current?.focus(), 0);
      toast.success(asNew || !saved ? 'Reporte guardado' : 'Cambios guardados', r.label);
      if (r.id !== id) navigate(`/explore/${r.id}`, { replace: !asNew });
    } catch (e) {
      if (e instanceof ApiError) setSaveError(e); else toast.fromError(e);
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!saved) return;
    setBusy(true);
    try {
      await myReportsApi.remove(saved.id, saved.version);
      toast.success('Reporte borrado', label);
      navigate('/reports', { replace: true });
    } catch (e) {
      setDeleting(false);
      if (e instanceof ApiError) setSaveError(e); else toast.fromError(e);
    } finally {
      setBusy(false);
    }
  }

  async function download(format: 'csv' | 'xlsx') {
    if (!def) return;
    try {
      const blob = await myReportsApi.exportFile(def, label.trim() || undefined, format);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${def.entity}-${format === 'csv' ? 'reporte.csv' : 'reporte.xlsx'}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      if (e instanceof ApiError) setRunError(e); else toast.fromError(e);
    }
  }

  const title = saved || sharedBy !== null ? label : from ? `Explorar: ${label}` : 'Nuevo reporte';
  const runMessages = runError instanceof ApiError ? Object.values(runError.fieldErrors).flat() : [];
  const subtitle = sharedBy !== null
    ? `Compartido por ${sharedBy}: corre con los registros y campos que tú puedes ver. Puedes explorarlo y guardar tu copia.`
    : saved
      ? sharing?.roles.length
        ? `Reporte personal compartido con ${sharing.roles.join(', ')}: cada quien lo ve con sus propios registros y campos.`
        : 'Reporte personal: solo tú lo ves, siempre con los registros y campos que puedes ver.'
      : 'Cambia la agrupación, las medidas o la gráfica: el resultado se calcula sin guardar nada.';
  return (
    <div className="page">
      <PageHeader icon={Compass} title={title} subtitle={subtitle}
        actions={(def || saved) && (
          <div className="report-actions">
            {dirty && saved && <Badge tone="warning">Cambios sin guardar</Badge>}
            {saved
              ? <Button ref={saveButton} variant="primary" icon={Save} loading={busy} disabled={!!missing} onClick={() => save(false, label)}>Guardar</Button>
              : <Button ref={saveButton} variant="primary" icon={Save} disabled={!!missing} onClick={() => { setSaveError(null); setNaming('save'); }}>Guardar como mío</Button>}
            {saved && <Button icon={Copy} disabled={!!missing} onClick={() => { setSaveError(null); setNaming('copy'); }}>Guardar como</Button>}
            <Button icon={FileText} disabled={!!missing} onClick={() => download('csv')}>CSV</Button>
            <Button icon={FileSpreadsheet} disabled={!!missing} onClick={() => download('xlsx')}>Excel</Button>
            {saved && sharing && (
              <Button ref={shareButton} icon={Share2} onClick={() => setShareOpen(true)} aria-haspopup="dialog">
                Compartir{sharing.roles.length ? ` (${sharing.roles.length})` : ''}
              </Button>
            )}
            {saved && <Button variant="ghost" iconOnly icon={Trash2} aria-label="Borrar el reporte" onClick={() => setDeleting(true)} />}
          </div>
        )} />
      {saveError && !naming && (
        <Alert tone="danger" title={saveError.title}
          action={saveError.status === 409 && saved ? <Button size="sm" onClick={() => window.location.reload()}>Volver a abrir</Button> : undefined}>
          {Object.values(saveError.fieldErrors).flat().join(' ') || undefined}
        </Alert>
      )}
      {notice?.fixedFilter && (
        <Alert tone="info" title="El filtro fijo del reporte original no se aplica al explorar">Usa «Filtros» para acotar los registros; el reporte del modelo no cambia.</Alert>
      )}
      {notice?.problems && sharedBy !== null && (
        <Alert tone="warning" title="Este reporte no está disponible para ti">Usa catálogos o campos que tu rol no puede ver; pídele a {sharedBy} que lo ajuste.</Alert>
      )}
      {notice?.problems && sharedBy === null && (
        <Alert tone="warning" title="Este reporte usa algo que ya no puedes ver">{notice.problems.join(' ')} Cámbialo para que vuelva a correr.</Alert>
      )}
      <div className="explorer-layout">
        <section className="card card-pad explorer-design" aria-label="Diseño del reporte">
          <Select label="Catálogo" value={entity ?? ''} onChange={(e) => {
            const next = e.target.value;
            setDef(next ? { entity: next, measures: [{ fn: 'count' }] } : null);
            setResult(null);
          }}>
            <option value="">Elige un catálogo…</option>
            {(sources.data ?? []).map((s) => <option key={s.entity} value={s.entity}>{s.parent ? `${s.label} (de ${s.parent})` : s.label}</option>)}
          </Select>
          {def && fields.data && <Designer def={def} paths={paths} byPath={byPath} patch={patch} />}
          {def && listDef && (
            <div className="stack-sm">
              <Button size="sm" icon={Filter} onClick={() => setFiltersOpen(true)} aria-haspopup="dialog">Filtros{def.filters?.length ? ` (${def.filters.length})` : ''}</Button>
              <FilterChips def={listDef} filters={def.filters ?? []} onRemove={(i) => patch({ filters: (def.filters ?? []).filter((_, j) => j !== i) })} />
            </div>
          )}
        </section>
        <section className="explorer-result" aria-label="Resultado">
          <div className="sr-only" aria-live="polite">{running ? 'Calculando…' : result ? 'Resultado actualizado' : ''}</div>
          {!def ? (
            <div className="card"><EmptyState icon={Compass} title="Elige un catálogo" description="Solo aparecen los catálogos y campos que puedes ver; el resultado usa los registros de tu alcance y tu empresa." /></div>
          ) : missing ? (
            <div className="card card-pad"><p className="subtle">{missing}</p></div>
          ) : runError ? (
            <Alert tone="danger" title={runError instanceof ApiError ? runError.title : 'No se pudo calcular el reporte'}>
              {runMessages.length ? <ul className="plain-list">{runMessages.map((m) => <li key={m}>{m}</li>)}</ul> : undefined}
            </Alert>
          ) : !result ? <LoadingBlock /> : (
            <div className="stack">
              {running && <Spinner size={16} label="Calculando" />}
              {result.truncated && <Alert tone="warning" title="Se muestran solo los primeros grupos">Agrega filtros o baja el límite para acotarlo.</Alert>}
              {result.chart && result.rows.length > 0 && <section className="card card-pad" aria-label="Gráfica"><ReportChart result={result} kind={result.chart} /></section>}
              {result.rows.length === 0 && result.columns.some((c) => c.kind === 'group')
                ? <div className="card"><EmptyState icon={Compass} title="Sin datos" description="Ningún registro que puedas ver cumple los filtros." /></div>
                : <section className="card" aria-label="Datos del reporte"><ReportTable result={result} caption={result.label} /></section>}
            </div>
          )}
        </section>
      </div>
      {def && listDef && (
        <FilterDialog open={filtersOpen} def={listDef} filters={def.filters ?? []} onClose={() => setFiltersOpen(false)}
          onApply={(f: ViewFilter[]) => { setFiltersOpen(false); patch({ filters: f }); }} />
      )}
      <NameDialog open={!!naming} copy={naming === 'copy'} initial={naming === 'copy' ? `${label} (copia)` : label} busy={busy}
        error={saveError} onClose={() => setNaming(null)} onSave={(n) => save(naming === 'copy', n)} />
      <ConfirmDialog open={deleting} danger title="Borrar el reporte" message={`¿Borrar «${label}»? No se puede deshacer.`} confirmLabel="Borrar"
        loading={busy} onCancel={() => setDeleting(false)} onConfirm={remove} />
      {saved && sharing && (
        <ShareDialog open={shareOpen} what="el reporte" shareable={sharing.shareable} current={sharing.roles}
          onClose={() => { setShareOpen(false); window.setTimeout(() => shareButton.current?.focus(), 0); }}
          onSave={async (roles) => {
            const r = await myReportsApi.share(saved.id, roles, saved.version);
            setSaved({ ...saved, version: r.version });
            setSharing({ ...sharing, roles: r.roles });
            setShareOpen(false);
            window.setTimeout(() => shareButton.current?.focus(), 0);
            toast.success(r.roles.length ? 'Reporte compartido' : 'Reporte sin compartir', r.roles.join(', ') || label);
            return r;
          }} />
      )}
      {!saved && <p className="subtle explorer-foot"><Link to="/reports">Volver a Reportes</Link></p>}
    </div>
  );
}

function NameDialog({ open, copy, initial, busy, error, onClose, onSave }: {
  open: boolean; copy: boolean; initial: string; busy: boolean; error: ApiError | null; onClose: () => void; onSave: (name: string) => void;
}) {
  const [name, setName] = useState(initial);
  useEffect(() => { if (open) setName(initial); }, [open, initial]);
  const message = error ? [...(error.fieldErrors.label ?? []), ...(error.fieldErrors.report ?? [])].join(' ') || error.title : undefined;
  return (
    <Dialog open={open} onClose={onClose} title={copy ? 'Guardar una copia' : 'Guardar como mío'}
      description="Solo tú verás este reporte en «Mis reportes». Corre siempre con los registros y campos que puedas ver."
      footer={<><Button onClick={onClose}>Cancelar</Button><Button variant="primary" loading={busy} onClick={() => onSave(name.trim())}>Guardar</Button></>}>
      <form onSubmit={(e) => { e.preventDefault(); onSave(name.trim()); }}>
        <Input label="Nombre del reporte" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} error={message} data-autofocus />
        {/* Revisión exploratoria 041 (F-06): el diálogo enfocaba «Cerrar»; el nombre es lo que se escribe. */}
      </form>
    </Dialog>
  );
}

/** Agrupaciones, medidas, gráfica y orden. Solo ofrece los campos que el servidor dice que la persona puede usar. */
function Designer({ def, paths, byPath, patch }: {
  def: PersonalReportDef; paths: ReportSourceField[]; byPath: Map<string, ReportSourceField>; patch: (p: Partial<PersonalReportDef>) => void;
}) {
  const gb = def.groupBy ?? [];
  const ms = def.measures;
  const o: ChartOptions = def.chartOptions ?? {};
  const groupable = paths.filter((p) => p.kind !== 'longtext');
  const numeric = paths.filter((p) => p.kind === 'number');
  const isDate = (f?: string) => byPath.get(f ?? '')?.kind === 'date';
  const setGroup = (i: number, p: Partial<Group>) => patch({ groupBy: gb.map((g, k) => (k === i ? clean({ ...g, ...p }) as Group : g)) });
  const setMeasure = (i: number, p: Partial<Measure>) => patch({ measures: ms.map((m, k) => (k === i ? clean({ ...m, ...p }) as Measure : m)) });
  const setOptions = (p: Partial<ChartOptions>) => {
    const next = clean({ ...o, ...p });
    patch({ chartOptions: Object.keys(next).length ? next : undefined });
  };
  const removeMeasure = (i: number) => {
    const shift = (list?: number[]) => list?.filter((x) => x !== i).map((x) => (x > i ? x - 1 : x));
    const next = clean({ ...o, measures: shift(o.measures), lines: shift(o.lines) });
    patch({ measures: ms.filter((_, k) => k !== i), chartOptions: def.chart && Object.keys(next).length ? next : undefined });
  };
  const measureLabel = (i: number) => {
    const m = ms[i];
    if (!m) return `Medida ${i + 1}`;
    const f = m.fn === 'calc' ? m.label || m.name || m.expr : byPath.get(m.field ?? '')?.label ?? m.field;
    return `${i + 1}. ${MEASURES[m.fn]}${f ? ` · ${f}` : ''}`;
  };
  const plottable = ms.map((m, i) => ({ m, i })).filter(({ m }) => (m.fn !== 'min' && m.fn !== 'max') || byPath.get(m.field ?? '')?.kind === 'number');
  const plotted = o.measures?.length ? o.measures : [0];
  const chart = def.chart;
  const single = chart === 'pie' || chart === 'donut' || chart === 'kpi' || gb.length === 2;
  const names = ms.filter((m) => m.fn !== 'calc' && m.name).map((m) => m.name!);

  return (
    <>
      <fieldset className="subgroup">
        <legend className="field-label">Agrupar por (hasta 2)</legend>
        {gb.map((g, i) => (
          // Revisión exploratoria 041 (F-01): sin periodo, el campo ocupa el ancho del periodo (antes quedaba en un tercio).
          <div className={`explorer-row${isDate(g.field) ? '' : ' explorer-row-wide'}`} key={i}>
            <Select aria-label={`Agrupación ${i + 1}: campo`} value={g.field ?? ''} onChange={(e) => setGroup(i, { field: e.target.value, period: isDate(e.target.value) ? 'month' : undefined })}>
              <option value="">Campo…</option>
              {groupable.map((p) => <option key={p.path} value={p.path}>{p.label}</option>)}
              {g.field && !byPath.has(g.field) && <option value={g.field}>{g.field} (no disponible)</option>}
            </Select>
            {isDate(g.field) ? (
              <Select aria-label={`Agrupación ${i + 1}: periodo`} value={g.period ?? 'day'} onChange={(e) => setGroup(i, { period: e.target.value as Group['period'] })}>
                {Object.entries(PERIODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </Select>
            ) : null}
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar agrupación ${i + 1}`} onClick={() => patch({ groupBy: gb.filter((_, k) => k !== i) })} />
          </div>
        ))}
        {gb.length < 2 && <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ groupBy: [...gb, { field: '' }] })}>Agregar agrupación</Button></div>}
      </fieldset>

      <fieldset className="subgroup">
        <legend className="field-label">Medidas (hasta 6)</legend>
        {ms.map((m, i) => (
          <div className="measure-row" key={i}>
            <div className="explorer-row">
              <Select aria-label={`Medida ${i + 1}: cálculo`} value={m.fn} onChange={(e) => {
                const fn = e.target.value as Measure['fn'];
                setMeasure(i, fn === 'calc' ? { fn, field: undefined, expr: m.expr ?? '' } : { fn, field: fn === 'count' ? undefined : m.field, expr: undefined, scale: undefined });
              }}>
                {(Object.keys(MEASURES) as Measure['fn'][]).map((k) => <option key={k} value={k}>{MEASURES[k]}</option>)}
              </Select>
              {m.fn === 'count' ? <span className="field-hint">De registros</span> : m.fn === 'calc' ? (
                <Input aria-label={`Medida ${i + 1}: expresión`} className="mono" value={m.expr ?? ''} maxLength={500} placeholder="ROUND(Ventas / Pedidos, 2)"
                  onChange={(e) => setMeasure(i, { expr: e.target.value })} />
              ) : (
                <Select aria-label={`Medida ${i + 1}: campo`} value={m.field ?? ''} onChange={(e) => setMeasure(i, { field: e.target.value || undefined })}>
                  <option value="">Campo…</option>
                  {(m.fn === 'min' || m.fn === 'max' ? paths.filter((p) => p.kind === 'number' || p.kind === 'date') : numeric)
                    .map((p) => <option key={p.path} value={p.path}>{p.label}</option>)}
                  {m.field && !byPath.has(m.field) && <option value={m.field}>{m.field} (no disponible)</option>}
                </Select>
              )}
              <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar medida ${i + 1}`} disabled={ms.length === 1} onClick={() => removeMeasure(i)} />
            </div>
            <div className="explorer-row explorer-row-extra">
              {/* Revisión exploratoria 041 (F-02): «Nombre para calcular» no cabía; la ayuda de abajo dice para qué es. */}
              <Input aria-label={`Medida ${i + 1}: nombre para calcular`} placeholder="Nombre" className="mono" value={m.name ?? ''} maxLength={60}
                onChange={(e) => setMeasure(i, { name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') || undefined })} />
              <Input aria-label={`Medida ${i + 1}: etiqueta`} placeholder="Etiqueta (opcional)" value={m.label ?? ''} maxLength={100} onChange={(e) => setMeasure(i, { label: e.target.value || undefined })} />
            </div>
          </div>
        ))}
        {ms.length < 6 && <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ measures: [...ms, { fn: 'sum' }] })}>Agregar medida</Button></div>}
        <p className="field-hint">
          Una calculada usa el nombre de otras medidas{names.length ? ` (${names.join(', ')})` : ' (dale un nombre a las que uses)'} con + − * /, IF, ROUND, ABS, MIN, MAX, COALESCE e ISBLANK.
        </p>
      </fieldset>

      <fieldset className="subgroup">
        <legend className="field-label">Gráfica</legend>
        <Select label="Tipo de gráfica" value={chart ?? ''} onChange={(e) => {
          const kind = (e.target.value || undefined) as ChartKind | undefined;
          patch(kind ? { chart: kind, chartOptions: o.colors ? { colors: o.colors } : undefined } : { chart: undefined, chartOptions: undefined });
        }}>
          <option value="">Sin gráfica</option>
          {(Object.keys(CHARTS) as ChartKind[]).map((k) => <option key={k} value={k}>{CHARTS[k]}</option>)}
        </Select>
        {chart && single && plottable.length > 1 && (
          <Select label="Medida de la gráfica" value={String(plotted[0] ?? 0)} onChange={(e) => setOptions({ measures: e.target.value === '0' ? undefined : [Number(e.target.value)], lines: undefined })}>
            {plottable.map(({ i }) => <option key={i} value={i}>{measureLabel(i)}</option>)}
          </Select>
        )}
        {chart && !single && plottable.length > 1 && (
          <div role="group" aria-label="Medidas que se dibujan" className="stack-sm">
            <span className="field-label">Medidas que se dibujan</span>
            {plottable.map(({ i }) => (
              <Checkbox key={i} label={measureLabel(i)} checked={plotted.includes(i)} onChange={(on) => {
                const list = on ? [...plotted, i].sort((a, b) => a - b) : plotted.filter((x) => x !== i);
                setOptions({ measures: list.length && !(list.length === 1 && list[0] === 0) ? list : undefined, lines: o.lines?.filter((x) => list.includes(x)) });
              }} />
            ))}
          </div>
        )}
        {chart === 'combo' && (
          <div role="group" aria-label="Como línea" className="stack-sm">
            <span className="field-label">Como línea</span>
            {plotted.map((i) => (
              <Checkbox key={i} label={measureLabel(i)} checked={(o.lines ?? []).includes(i)}
                onChange={(on) => setOptions({ lines: on ? [...(o.lines ?? []), i] : (o.lines ?? []).filter((x) => x !== i) })} />
            ))}
          </div>
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
      </fieldset>

      <fieldset className="subgroup">
        <legend className="field-label">Orden y límite</legend>
        <div className="explorer-row explorer-row-extra">
          <Select aria-label="Ordenar por" value={def.orderBy ?? 'group'} onChange={(e) => patch({ orderBy: e.target.value === 'measure' ? 'measure' : undefined })}>
            <option value="group">Por la agrupación</option><option value="measure">Por la primera medida</option>
          </Select>
          <Select aria-label="Dirección" value={def.dir ?? 'asc'} onChange={(e) => patch({ dir: e.target.value === 'desc' ? 'desc' : undefined })}>
            <option value="asc">Ascendente</option><option value="desc">Descendente</option>
          </Select>
        </div>
        <Input label="Límite de filas (opcional)" type="number" min={1} max={1000} value={def.limit ?? ''}
          onChange={(e) => patch({ limit: e.target.value ? Number(e.target.value) : undefined })} />
      </fieldset>
    </>
  );
}
