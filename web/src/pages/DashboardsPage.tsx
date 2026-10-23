import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ArrowRight, Code2, Inbox, LayoutDashboard, Plus } from 'lucide-react';
import { ApiError } from '../api/client';
import { dashboardsApi } from '../api/endpoints';
import type { ChangePlan, DashboardDef, DashboardResult, DashboardSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { DynamicIcon } from '../components/DynamicIcon';
import { formatReportValue, ReportChart } from '../components/ReportChart';
import { ReportTable } from '../components/ReportTable';
import { Alert, Button, EmptyState, LoadingBlock, PageHeader, Textarea } from '../components/ui';
import { useAsync } from '../lib/hooks';
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
  const [editing, setEditing] = useState(false);
  const navigate = useNavigate();

  return (
    <div className="page">
      <PageHeader icon={LayoutDashboard} title="Tableros" subtitle="Indicadores y gráficas con los registros que puedes ver."
        actions={canModel && <Button icon={Plus} onClick={() => setEditing(true)}>Nuevo tablero</Button>} />
      {loading && !data ? <LoadingBlock />
        : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los tableros'} />
        : !data?.length ? (
          <div className="card"><EmptyState icon={Inbox} title="No hay tableros" description="Cuando haya tableros con indicadores que puedas ver, aparecerán aquí." /></div>
        ) : (
          <div className="grid-cards">
            {data.map((d) => (
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
          const measures = w.result.columns.filter((c) => c.kind === 'measure');
          const m = measures[w.measure ?? 0] ?? measures[0];
          const titleId = `w-${i}`;
          return (
            <section key={i} className={`card widget${w.wide ? ' wide' : ''}`} aria-labelledby={titleId}>
              <h2 id={titleId} className="widget-title">
                <Link to={`/reports/${encodeURIComponent(w.entity)}/${encodeURIComponent(w.report)}`}>{w.label}</Link>
              </h2>
              {w.type === 'kpi' ? (
                <div className="kpi-value">{m ? formatReportValue(m, w.result.totals[m.key]) : '—'}</div>
              ) : w.result.rows.length === 0 ? (
                <p className="subtle">Sin datos todavía.</p>
              ) : w.type === 'chart' ? (
                <ReportChart result={w.result} kind={w.result.chart ?? 'bar'} measure={w.measure ?? 0} />
              ) : (
                <ReportTable result={w.result} caption={w.label} />
              )}
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
        description="Tarjetas kpi (reporte sin agrupación), chart o table (con agrupación), cada una con entity y report. Se valida en la vista previa."
        footer={<><Button onClick={onClose}>Cancelar</Button><Button variant="primary" onClick={review}>Revisar cambios</Button></>}>
        <Textarea className="mono json-editor" label="Tablero (JSON)" rows={16} spellCheck={false} value={text} error={error}
          onChange={(e) => { setText(e.target.value); setError(undefined); }} />
      </Dialog>
      <PlanReviewDialog plan={plan} onClose={() => setPlan(null)} onApplied={() => { setPlan(null); onApplied(name); }} />
    </>
  );
}
