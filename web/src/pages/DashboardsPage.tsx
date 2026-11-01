import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowRight, Code2, Inbox, LayoutDashboard, Plus, Users } from 'lucide-react';
import { ApiError } from '../api/client';
import { dashboardsApi, myDashboardsApi } from '../api/endpoints';
import type { ChangePlan, DashboardDef, DashboardResult, DashboardSummary, PersonalDashboardList, ReportResult } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { DynamicIcon } from '../components/DynamicIcon';
import { formatReportValue, ReportChart } from '../components/ReportChart';
import { ReportTable } from '../components/ReportTable';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader, Textarea } from '../components/ui';
import { useAsync } from '../lib/hooks';
import { useApps } from '../lib/apps';
import { PlanReviewDialog } from './modeler/PlanReviewDialog';
import { NotFoundPage } from './StatusPages';

const EXAMPLE: DashboardDef = {
  name: 'Ventas', label: 'Ventas', icon: 'layout-dashboard',
  widgets: [{ type: 'kpi', entity: 'Catalogo', report: 'Total' }, { type: 'chart', entity: 'Catalogo', report: 'PorMes', wide: true }],
};

/** Spec 007: tableros disponibles. El modelador crea uno nuevo como JSON validado en la vista previa del plan. */
export function DashboardsPage() {
  const { me } = useAuth();
  const canModel = !!(me?.canManageModel || me?.isSuperuser);
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync<DashboardSummary[]>((s) => dashboardsApi.list(s), [tick]);
  // Spec 024 (H4): `?app=` acota los tableros a los que tienen tarjetas de ese módulo.
  const [params] = useSearchParams();
  const { find } = useApps();
  const only = find(params.get('app'));
  const list = (data ?? []).filter((d) => !only || d.apps?.some((a) => a.toLowerCase() === only.name.toLowerCase()));
  const [editing, setEditing] = useState(false);
  const navigate = useNavigate();
  // Spec 042: mis tableros y los compartidos conmigo (sin `?app=`, como «Mis reportes»).
  const personal = useAsync<PersonalDashboardList>((s) => myDashboardsApi.list(s), []);

  return (
    <div className="page">
      <PageHeader icon={LayoutDashboard} title={only ? `Tableros de ${only.label}` : 'Tableros'} subtitle="Indicadores y gráficas con los registros que puedes ver."
        actions={<>
          {only && <Link className="btn btn-secondary" to="/dashboards">Ver todos los tableros</Link>}
          {canModel && <Button icon={Code2} onClick={() => setEditing(true)}>Nuevo tablero del modelo</Button>}
          <Link className="btn btn-primary" to="/my-dashboards"><Plus size={16} aria-hidden /> Nuevo tablero</Link>
        </>} />
      {!only && !!personal.data?.mine.length && (
        <section className="stack" style={{ marginBottom: 'var(--space-6)' }} aria-labelledby="db-mine">
          <h2 id="db-mine" className="section-title">Mis tableros</h2>
          <div className="grid-cards">
            {personal.data.mine.map((d) => (
              <Link key={d.id} to={`/my-dashboards/${d.id}`} className="card entity-card">
                <div className="entity-card-icon"><LayoutDashboard size={18} aria-hidden /></div>
                <div>
                  <div className="entity-card-title">{d.label}</div>
                  {d.description && <div className="entity-card-desc">{d.description}</div>}
                </div>
                <div className="entity-card-foot">{d.shared && <Badge icon={Users}>Compartido</Badge>}<span className="spacer" /><ArrowRight size={15} aria-hidden /></div>
              </Link>
            ))}
          </div>
        </section>
      )}
      {!only && !!personal.data?.shared.length && (
        <section className="stack" style={{ marginBottom: 'var(--space-6)' }} aria-labelledby="db-shared">
          <h2 id="db-shared" className="section-title">Compartidos conmigo</h2>
          <div className="grid-cards">
            {personal.data.shared.map((d) => (
              <Link key={d.id} to={`/my-dashboards/${d.id}`} className="card entity-card">
                <div className="entity-card-icon"><Users size={18} aria-hidden /></div>
                <div>
                  <div className="entity-card-title">{d.label}</div>
                  <div className="entity-card-desc">De {d.owner}{d.description ? ` · ${d.description}` : ''}</div>
                </div>
                <div className="entity-card-foot"><span className="spacer" /><ArrowRight size={15} aria-hidden /></div>
              </Link>
            ))}
          </div>
        </section>
      )}
      {!only && !!(personal.data?.mine.length || personal.data?.shared.length) && <h2 className="section-title">Del modelo</h2>}
      {loading && !data ? <LoadingBlock />
        : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los tableros'} />
        : !list.length ? (
          <div className="card"><EmptyState icon={Inbox} title="No hay tableros del modelo" description="Cuando un modelador defina tableros que puedas ver, aparecerán aquí. Mientras, arma el tuyo con «Nuevo tablero»." /></div>
        ) : (
          <div className="grid-cards">
            {list.map((d) => (
              <Link key={d.name} to={`/dashboards/${encodeURIComponent(d.name)}`} className="card entity-card">
                <div className="entity-card-icon"><DynamicIcon name={d.icon} size={18} /></div>
                <div>
                  <div className="entity-card-title">{d.label}</div>
                  {d.description && <div className="entity-card-desc">{d.description}</div>}
                </div>
                <div className="entity-card-foot"><span className="spacer" /><ArrowRight size={15} aria-hidden /></div>
              </Link>
            ))}
          </div>
        )}
      <DashboardEditor open={editing} onClose={() => setEditing(false)} onApplied={(name) => { setEditing(false); setTick((t) => t + 1); navigate(`/dashboards/${encodeURIComponent(name)}`); }} />
    </div>
  );
}

export function DashboardPage() {
  const { name = '' } = useParams();
  const { me } = useAuth();
  const canModel = !!(me?.canManageModel || me?.isSuperuser);
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync<DashboardResult>((s) => dashboardsApi.get(name, s), [name, tick]);
  const [editing, setEditing] = useState(false);

  if (error instanceof ApiError && error.status === 404) return <NotFoundPage />;
  if (loading && !data) return <LoadingBlock />;
  if (error || !data) return <div className="page"><Alert tone="danger" title="No se pudo cargar el tablero" /></div>;

  return (
    <div className="page">
      <PageHeader icon={LayoutDashboard} title={data.label} subtitle={data.description ?? undefined}
        actions={canModel && data.definition && <Button icon={Code2} onClick={() => setEditing(true)}>Editar (JSON)</Button>} />
      <div className="dashboard-grid">
        {data.widgets.map((w, i) => {
          const titleId = `w-${i}`;
          return (
            <section key={i} className={`card widget${w.wide ? ' wide' : ''}`} aria-labelledby={titleId}>
              <h2 id={titleId} className="widget-title">
                <Link to={`/reports/${encodeURIComponent(w.entity)}/${encodeURIComponent(w.report)}`}>{w.label}</Link>
              </h2>
              <WidgetBody type={w.type} label={w.label} result={w.result} measure={w.measure} />
            </section>
          );
        })}
      </div>
      {data.definition && (
        <DashboardEditor open={editing} initial={data.definition} onClose={() => setEditing(false)} onApplied={() => { setEditing(false); setTick((t) => t + 1); }} />
      )}
    </div>
  );
}

/** El cuerpo de una tarjeta (spec 007; spec 042 la comparte con el tablero personal). */
export function WidgetBody({ type, label, result, measure }: { type: 'kpi' | 'chart' | 'table'; label: string; result: ReportResult; measure?: number | null }) {
  const measures = result.columns.filter((c) => c.kind === 'measure');
  const m = measures[measure ?? 0] ?? measures[0];
  return type === 'kpi' ? (
    result.comparison
      ? <ReportChart result={result} kind="kpi" measure={measure ?? 0} /> // spec 039 (H4): último periodo contra el anterior
      : <div className="kpi-value">{m ? formatReportValue(m, result.totals[m.key]) : '—'}</div>
  ) : result.rows.length === 0 ? (
    <p className="subtle">Sin datos todavía.</p>
  ) : type === 'chart' ? (
    <ReportChart result={result} kind={result.chart ?? 'bar'} measure={measure ?? undefined} />
  ) : (
    <ReportTable result={result} caption={label} />
  );
}

/** Editor JSON del tablero: pasa por la misma vista previa y aplicación que cualquier plan de cambios. */
function DashboardEditor({ open, initial, onClose, onApplied }: { open: boolean; initial?: DashboardDef; onClose: () => void; onApplied: (name: string) => void }) {
  const [text, setText] = useState('');
  const [error, setError] = useState<string>();
  const [plan, setPlan] = useState<ChangePlan | null>(null);
  const [name, setName] = useState('');
  useEffect(() => { if (open) { setText(JSON.stringify(initial ?? EXAMPLE, (_k, v) => (v === null ? undefined : v), 2)); setError(undefined); } }, [open, initial]);

  function review() {
    try {
      const d = JSON.parse(text) as DashboardDef;
      if (typeof d !== 'object' || d === null || Array.isArray(d)) throw new Error('Debe ser un objeto JSON: { … }');
      setName(d.name);
      setPlan({ description: `${initial ? 'Modificar' : 'Crear'} tablero ${d.label ?? d.name}`, operations: [{ op: 'setDashboard', dashboard: d }] });
    } catch (e) {
      setError(`JSON inválido: ${(e as Error).message}`);
    }
  }

  return (
    <>
      <Dialog open={open && !plan} onClose={onClose} size="lg" title={initial ? `Editar ${initial.label}` : 'Nuevo tablero'}
        description="Tarjetas kpi (reporte sin agrupación, o por una fecha con periodo para compararlo con el anterior), chart o table (con agrupación), cada una con entity y report. Se valida en la vista previa."
        footer={<><Button onClick={onClose}>Cancelar</Button><Button variant="primary" onClick={review}>Revisar cambios</Button></>}>
        <Textarea className="mono json-editor" label="Tablero (JSON)" rows={16} spellCheck={false} value={text} error={error}
          onChange={(e) => { setText(e.target.value); setError(undefined); }} />
      </Dialog>
      <PlanReviewDialog plan={plan} onClose={() => setPlan(null)} onApplied={() => { setPlan(null); onApplied(name); }} />
    </>
  );
}
