import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { CalendarClock, FileSearch, History, Play, RefreshCw, RotateCcw } from 'lucide-react';
import { ApiError } from '../api/client';
import { automationsApi } from '../api/endpoints';
import type { AutomationRun, AutomationRunDetail, RunStatus, ScheduleSummary } from '../api/types';
import { Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader, Select, Switch, Tabs } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';

/* Spec 016: Configuración › Automatizaciones. Tareas programadas por entorno y bitácora de automatizaciones (solo superusuario). */

const runTone: Record<RunStatus, 'success' | 'warning' | 'danger' | 'neutral' | 'accent'> = {
  pending: 'warning', running: 'accent', succeeded: 'success', partial: 'warning', failed: 'danger', canceled: 'neutral',
};
const runLabel: Record<RunStatus, string> = {
  pending: 'Pendiente', running: 'En curso', succeeded: 'Correcta', partial: 'Con errores', failed: 'Fallida', canceled: 'Cancelada',
};
const eventLabel: Record<string, string> = { create: 'Al crear', update: 'Al editar', enterState: 'Al entrar a', transition: 'Al ejecutar' };

const small = { fontSize: 'var(--text-sm)' } as const;

function RunBadge({ status }: { status: RunStatus }) {
  return <Badge tone={runTone[status]}>{runLabel[status]}</Badge>;
}

/** «12 procesados · 11 correctos · 1 con error», con lo que quedó para la siguiente ocurrencia. */
function Counts({ processed, succeeded, failed, skipped, more }: { processed: number; succeeded: number; failed: number; skipped?: number; more: boolean }) {
  const parts = [`${processed} procesado${processed === 1 ? '' : 's'}`];
  if (processed) parts.push(`${succeeded} correcto${succeeded === 1 ? '' : 's'}`);
  if (failed) parts.push(`${failed} con error`);
  if (skipped) parts.push(`${skipped} ya no cumplía${skipped === 1 ? '' : 'n'}`);
  return (
    <span>
      {parts.join(' · ')}
      {more && <span className="subtle" style={{ display: 'block', ...small }}>Quedaron registros para la siguiente ocurrencia</span>}
    </span>
  );
}

/* ----------------------------- Tareas programadas ----------------------------- */

function SchedulesTab({ onShowRuns }: { onShowRuns: () => void }) {
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const { data: schedules, loading, error } = useAsync((s) => automationsApi.schedules(s), [tick]);
  const [busy, setBusy] = useState<string | null>(null);

  // Mientras la última ejecución de alguna tarea esté pendiente o en curso, la tabla se actualiza sola (F-04).
  const waiting = schedules?.some((s) => s.lastRun?.status === 'pending' || s.lastRun?.status === 'running');
  useEffect(() => {
    if (!waiting) return;
    const t = window.setTimeout(() => setTick((x) => x + 1), 2000);
    return () => window.clearTimeout(t);
  }, [waiting, schedules]);

  async function toggle(s: ScheduleSummary, active: boolean) {
    setBusy(`${s.entity}.${s.name}`);
    try {
      await automationsApi.setActive(s.entity, s.name, active);
      toast.success(active ? `«${s.label}» activada` : `«${s.label}» en pausa`);
      setTick((t) => t + 1);
    } catch (e) { toast.fromError(e); } finally { setBusy(null); }
  }

  async function runNow(s: ScheduleSummary) {
    setBusy(`run:${s.entity}.${s.name}`);
    try {
      await automationsApi.runNow(s.entity, s.name);
      toast.success(`«${s.label}» en cola: el resultado aparece en la bitácora`);
      setTick((t) => t + 1);
    } catch (e) { toast.fromError(e); } finally { setBusy(null); }
  }

  if (loading && !schedules) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar las tareas'} />;

  return (
    <div className="stack">
      <Alert tone="info" title="Cómo funcionan">
        Una tarea se declara en el modelador (Tareas programadas en JSON) o la trae un módulo, y llega en pausa. Al activarla corre
        en la hora local del servidor, con los permisos de su rol de servicio; lo que cambia queda a nombre de «Tareas programadas».
      </Alert>
      <div className="table-wrap">
        {!schedules?.length ? (
          <EmptyState icon={CalendarClock} title="Sin tareas programadas"
            description="Declara una tarea en el modelador: qué registros procesa, cuándo y con qué rol. Aquí se decide si corre en este entorno." />
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Tarea</th><th>Activa</th><th>Próxima</th><th>Última ejecución</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {schedules.map((s) => (
                  <tr key={`${s.entity}.${s.name}`}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{s.label}</div>
                      <div className="subtle" style={small}>{s.entityLabel}{s.description ? ` · ${s.description}` : ''}</div>
                      {/* Cuándo y con qué rol, en la misma celda: con columnas aparte los controles quedaban fuera de la vista (F-02). */}
                      <div style={small}>{s.calendar} · rol {s.role} · hasta {s.batchSize} registros</div>
                    </td>
                    <td>
                      <Switch checked={s.active} disabled={busy === `${s.entity}.${s.name}`} onChange={(v) => void toggle(s, v)}
                        aria-label={`Activa: ${s.label}`} />
                    </td>
                    <td style={{ whiteSpace: 'nowrap' }}>{s.active && s.nextRunAt ? formatDateTime(s.nextRunAt) : <span className="subtle">En pausa</span>}</td>
                    <td>
                      {s.lastRun ? (
                        <div className="stack" style={{ gap: 4 }}>
                          <span><RunBadge status={s.lastRun.status} /></span>
                          {/* Una ocurrencia que no llegó a procesar nada (p. ej. el rol perdió la lectura) dice por qué, no «0 procesados». */}
                          {(s.lastRun.processed > 0 || s.lastRun.status === 'succeeded') && <span style={small}><Counts {...s.lastRun} /></span>}
                          {s.lastRun.error && s.lastRun.processed === 0 && <span className="subtle" style={{ ...small, overflowWrap: 'anywhere' }}>{s.lastRun.error}</span>}
                          {s.lastRun.finishedAt && <span className="subtle" style={small}>{formatDateTime(s.lastRun.finishedAt)}</span>}
                        </div>
                      ) : '—'}
                    </td>
                    <td>
                      <Button size="sm" icon={Play} loading={busy === `run:${s.entity}.${s.name}`} onClick={() => void runNow(s)}
                        aria-label={`Ejecutar ahora ${s.label}`}>
                        Ejecutar ahora
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
        <Button size="sm" variant="ghost" icon={RefreshCw} onClick={() => setTick((t) => t + 1)}>Actualizar</Button>
        <Button size="sm" variant="ghost" icon={History} onClick={onShowRuns}>Ver bitácora</Button>
      </div>
    </div>
  );
}

/* ----------------------------- Bitácora ----------------------------- */

function RunDetailDialog({ id, onClose }: { id: number | null; onClose: () => void }) {
  const { data, loading, error } = useAsync<AutomationRunDetail | null>((s) => (id ? automationsApi.run(id, s) : Promise.resolve(null)), [id]);
  const r = data?.run;
  return (
    <Dialog open={id !== null} onClose={onClose} size="lg" title={r ? `${r.label} · ejecución ${r.id}` : 'Ejecución'}
      description={r ? `${r.entityLabel} · ${r.kind === 'async' ? 'Automatización asíncrona' : 'Tarea programada'}` : undefined}
      footer={<Button variant="primary" onClick={onClose}>Cerrar</Button>}>
      {loading && !data ? <LoadingBlock /> : error ? <Alert tone="danger" title="No se pudo cargar la ejecución" /> : r && (
        <div className="stack">
          <dl className="detail-list">
            <dt>Estado</dt><dd><RunBadge status={r.status} /></dd>
            <dt>A nombre de</dt><dd>{r.runAs}</dd>
            {r.recordId != null && <><dt>Registro</dt><dd><Link to={`/c/${encodeURIComponent(r.entity)}/${r.recordId}`}>#{r.recordId}</Link></dd></>}
            <dt>Intentos</dt><dd className="mono">{r.attempts}</dd>
            {r.kind === 'schedule' && <><dt>Registros</dt><dd><Counts {...r} /></dd></>}
            <dt>Encolada</dt><dd>{formatDateTime(r.createdAt)}{r.manual ? ' (a mano)' : ''}</dd>
            {r.startedAt && <><dt>Inicio</dt><dd>{formatDateTime(r.startedAt)}</dd></>}
            {r.finishedAt && <><dt>Fin</dt><dd>{formatDateTime(r.finishedAt)}</dd></>}
            {r.nextAttemptAt && r.attempts > 0 && <><dt>Siguiente intento</dt><dd>{formatDateTime(r.nextAttemptAt)}</dd></>}
          </dl>
          {/* Con errores por registro, la tabla ya los dice: el resumen solo los repetiría. */}
          {r.lastError && !data.errors.length && <Alert tone={r.status === 'canceled' ? 'info' : 'danger'} title={r.status === 'canceled' ? 'Motivo' : 'Error'}>{r.lastError}</Alert>}
          {!!data.errors.length && (
            <div className="table-wrap">
              <div className="table-scroll">
                <table className="table">
                  <caption className="sr-only">Errores por registro</caption>
                  <thead><tr><th>Registro</th><th>Error</th></tr></thead>
                  <tbody>
                    {data.errors.map((e) => (
                      <tr key={e.recordId}>
                        <td><Link to={`/c/${encodeURIComponent(r.entity)}/${e.recordId}`}>#{e.recordId}</Link></td>
                        <td>{e.message}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}
    </Dialog>
  );
}

function RunsTab() {
  const toast = useToast();
  const [kind, setKind] = useState('');
  const [status, setStatus] = useState('');
  const [tick, setTick] = useState(0);
  const [detail, setDetail] = useState<number | null>(null);
  const { data: runs, loading, error } = useAsync((s) => automationsApi.runs({ kind: kind || undefined, status: status || undefined }, s), [kind, status, tick]);

  // Mientras algo esté pendiente o en curso, la bitácora se actualiza sola.
  const active = runs?.some((r) => r.status === 'pending' || r.status === 'running');
  useEffect(() => {
    if (!active) return;
    const t = window.setTimeout(() => setTick((x) => x + 1), 3000);
    return () => window.clearTimeout(t);
  }, [active, runs]);

  async function retry(r: AutomationRun) {
    try { await automationsApi.retry(r.id); toast.success('Ejecución en cola'); setTick((t) => t + 1); }
    catch (e) { toast.fromError(e); }
  }

  return (
    <div className="stack">
      <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
        <Select label="Tipo" selectSize="sm" value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="">Todas</option>
          <option value="async">Automatizaciones asíncronas</option>
          <option value="schedule">Tareas programadas</option>
        </Select>
        <Select label="Estado" selectSize="sm" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Todos</option>
          {(Object.keys(runLabel) as RunStatus[]).map((s) => <option key={s} value={s}>{runLabel[s]}</option>)}
        </Select>
        <Button size="sm" variant="ghost" icon={RefreshCw} onClick={() => setTick((t) => t + 1)}>Actualizar</Button>
      </div>
      <div className="table-wrap">
        {loading && !runs ? <LoadingBlock /> : error ? <Alert tone="danger" title="No se pudo cargar la bitácora" /> : !runs?.length ? (
          <EmptyState icon={History} title="Sin ejecuciones"
            description="Cada automatización asíncrona y cada ocurrencia de una tarea programada aparecen aquí con su resultado." />
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Fecha</th><th>Automatización</th><th>Estado</th><th>Resultado</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {runs.map((r) => (
                  <tr key={r.id}>
                    <td style={{ whiteSpace: 'nowrap' }}>{formatDateTime(r.createdAt)}</td>
                    <td>
                      <div style={{ fontWeight: 500 }}>{r.label}</div>
                      <div className="subtle" style={small}>
                        {r.entityLabel} · {r.kind === 'async' ? `Asíncrona${r.event ? ` · ${eventLabel[r.event] ?? r.event}${r.eventKey ? ` ${r.eventKey}` : ''}` : ''}` : `Programada${r.manual ? ' · a mano' : ''}`}
                      </div>
                      <div className="subtle" style={small}>{r.recordId != null ? `Registro #${r.recordId} · ` : ''}{r.runAs}</div>
                    </td>
                    <td>
                      <RunBadge status={r.status} />
                      {r.attempts > 1 && <div className="subtle" style={small}>{r.attempts} intentos</div>}
                    </td>
                    <td style={{ minWidth: 160 }}>
                      {r.kind === 'schedule' && (r.processed > 0 || r.status === 'succeeded') && <div style={small}><Counts {...r} /></div>}
                      {r.lastError && <div className="subtle" style={{ ...small, overflowWrap: 'anywhere' }}>{r.lastError}</div>}
                      {r.kind === 'async' && !r.lastError && (r.status === 'pending' && r.nextAttemptAt ? <span className="subtle" style={small}>En cola</span> : '—')}
                    </td>
                    <td>
                      <div className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                        <Button size="sm" variant="ghost" icon={FileSearch} onClick={() => setDetail(r.id)} aria-label={`Detalle de la ejecución ${r.id}`}>Detalle</Button>
                        {r.kind === 'async' && (r.status === 'failed' || r.status === 'canceled') && (
                          <Button size="sm" variant="ghost" icon={RotateCcw} onClick={() => void retry(r)} aria-label={`Reintentar ejecución ${r.id}`}>Reintentar</Button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <RunDetailDialog id={detail} onClose={() => setDetail(null)} />
    </div>
  );
}

type Tab = 'schedules' | 'runs';
const tabLabels: Record<Tab, string> = { schedules: 'Tareas programadas', runs: 'Bitácora' };

export function AutomationsPage() {
  const [tab, setTab] = useState<Tab>('schedules');
  return (
    <div className="page">
      <PageHeader icon={CalendarClock} title="Automatizaciones"
        subtitle="Qué tareas corren solas en este entorno y qué hicieron las automatizaciones en segundo plano." />
      <Tabs label="Automatizaciones" value={tab} onChange={setTab}
        items={[{ id: 'schedules', label: tabLabels.schedules, icon: CalendarClock }, { id: 'runs', label: tabLabels.runs, icon: History }]} />
      <div role="tabpanel" aria-label={tabLabels[tab]}>
        {tab === 'schedules' ? <SchedulesTab onShowRuns={() => setTab('runs')} /> : <RunsTab />}
      </div>
    </div>
  );
}
