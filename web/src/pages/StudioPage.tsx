import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  AlertTriangle, Ban, Bot, Braces, CheckCircle2, CircleSlash, Download, FlaskConical, History, KeyRound, ListChecks, MessageSquareText,
  PackageCheck, Plus, Rocket, Send, ShieldAlert, Square, Trash2, Wrench, XCircle, type LucideIcon,
} from 'lucide-react';
import { ApiError } from '../api/client';
import { studioApi } from '../api/endpoints';
import type { StudioScope, StudioSession, StudioStatusInfo, StudioStep } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader, Spinner, Switch, Textarea } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';
import {
  STATUS_LABEL, STATUS_TONE, asText, consumption, formatDuration, formatUsd, isActive, lastSeq, mergeTrail, planFile, reportNotes,
} from '../lib/studio';
import { download } from '../lib/svgExport';
import { PlanPreviewView } from './modeler/PlanReviewDialog';

/** Cada cuánto se piden los pasos nuevos de una sesión en curso (uno a la vez, con `after`). */
const POLL_MS = 1500;

function problemText(e: unknown, fallback: string) {
  if (!(e instanceof ApiError)) return fallback;
  const items = Object.values(e.fieldErrors).flat();
  return items.length ? items.join(' ') : e.detail ?? e.title;
}

/**
 * Spec 035: pantalla de ERP AI Studio sobre el motor de la 033. El agente trabaja en segundo plano con los permisos de la
 * persona; la pantalla muestra su avance (pidiendo solo los pasos nuevos), lo que propone y lo que dice haber probado, y
 * la persona aplica con la huella de lo que revisó. Todo lo que viene del agente se pinta como texto.
 */
export function StudioPage() {
  const [params, setParams] = useSearchParams();
  const sessionId = Number(params.get('s')) || undefined;
  const [tick, setTick] = useState(0);
  const status = useAsync((s) => studioApi.status(s), [tick]);
  const sessions = useAsync((s) => studioApi.sessions(s), [tick]);
  // Herramientas del alcance «datos»: su salida se borra al cerrar la sesión (R-033-4; revisión adversarial F-04).
  const tools = useAsync((s) => studioApi.tools(s), []);
  const dataTools = new Set((tools.data ?? []).filter((t) => t.scope === 'datos').map((t) => t.name));
  const st = status.data;
  const opened = useRef(false);

  // H1 CA3: con una sesión en curso, la pantalla abre esa (una vez, al entrar).
  useEffect(() => {
    if (opened.current || !st) return;
    opened.current = true;
    if (!sessionId && st.active) setParams({ s: String(st.active) }, { replace: true });
  }, [st, sessionId, setParams]);

  const open = (id?: number) => setParams(id ? { s: String(id) } : {});
  const refresh = () => setTick((t) => t + 1);

  if (status.loading && !st) return <LoadingBlock />;
  if (status.error || !st) {
    return (
      <div className="page">
        <Alert tone="danger" title="No se pudo cargar ERP AI Studio">{problemText(status.error, 'Recarga la página e intenta de nuevo.')}</Alert>
      </div>
    );
  }

  return (
    <div className="page">
      <PageHeader
        icon={Bot}
        title="ERP AI Studio"
        subtitle="Pide un desarrollo: el agente investiga el modelo con tus permisos, lo prueba en una copia y te entrega una propuesta. Nada se aplica sin ti."
        actions={st.configured && sessionId ? <Button icon={Plus} onClick={() => open(undefined)}>Nuevo pedido</Button> : undefined}
      />
      {!st.configured ? (
        <div className="card">
          <EmptyState
            icon={KeyRound}
            title="ERP AI Studio no está configurado"
            description={<>Usa la clave de Anthropic del asistente. Un administrador la guarda en <Link to="/admin/assistant">Configuración › Asistente IA</Link>; el resto del ERP funciona igual.</>}
          />
        </div>
      ) : (
        <div className="studio-layout">
          <nav className="card studio-history" aria-label="Sesiones">
            <h2 className="section-title"><History size={15} aria-hidden /> Sesiones</h2>
            {sessions.data?.length ? (
              <ul>
                {sessions.data.map((x) => (
                  <li key={x.id}>
                    <button type="button" className="studio-history-item" aria-current={x.id === sessionId ? 'true' : undefined} onClick={() => open(x.id)}>
                      <span className="studio-history-title">{x.title}</span>
                      <span className="studio-history-meta">
                        <Badge tone={STATUS_TONE[x.status]}>{STATUS_LABEL[x.status]}</Badge>
                        <span className="subtle studio-date">{formatDateTime(x.createdAt)}</span>
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="subtle" style={{ margin: 0 }}>Aún no hay sesiones.</p>
            )}
          </nav>
          <section className="studio-main" aria-label={sessionId ? 'Sesión' : 'Nuevo pedido'}>
            {sessionId ? (
              <SessionView key={sessionId} id={sessionId} status={st} dataTools={dataTools} onChanged={refresh} />
            ) : (
              <Composer status={st} onStarted={(id) => { refresh(); open(id); }} onOpen={open} />
            )}
          </section>
        </div>
      )}
    </div>
  );
}

/* ---------------- Pedido ---------------- */

function Composer({ status, onStarted, onOpen }: { status: StudioStatusInfo; onStarted: (id: number) => void; onOpen: (id: number) => void }) {
  const [prompt, setPrompt] = useState('');
  // H1 CA2: «datos» siempre empieza apagado; «aislado», encendido si el servidor lo permite.
  const [datos, setDatos] = useState(false);
  const [aislado, setAislado] = useState(status.sandboxAvailable);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const max = status.limits.maxPromptChars;
  const blocked = status.active !== null;

  async function submit(e: FormEvent) {
    e.preventDefault();
    const text = prompt.trim();
    if (!text || sending || blocked) return;
    setSending(true);
    setError(null);
    try {
      const scopes: StudioScope[] = [...(datos ? ['datos' as const] : []), ...(aislado && status.sandboxAvailable ? ['aislado' as const] : [])];
      const s = await studioApi.start(text, scopes);
      onStarted(s.id);
    } catch (err) {
      setError(problemText(err, 'No se pudo iniciar la sesión. Revisa tu conexión.'));
    } finally {
      setSending(false);
    }
  }

  return (
    <form className="card card-pad studio-composer" onSubmit={submit} aria-label="Nuevo pedido a ERP AI Studio">
      <h2 className="section-title"><Send size={15} aria-hidden /> Nuevo pedido</h2>
      {blocked && (
        <Alert tone="info" title="Ya tienes una sesión en curso" action={<Button size="sm" onClick={() => onOpen(status.active!)}>Ver la sesión</Button>}>
          Espera a que termine o cancélala para pedir otra.
        </Alert>
      )}
      <Textarea
        label="¿Qué desarrollo necesitas?"
        value={prompt}
        maxLength={max}
        rows={5}
        disabled={sending}
        placeholder="Por ejemplo: en Productos, agrega «Peso (kg)» con una regla que no permita pesos negativos, y pruébala."
        onChange={(e) => setPrompt(e.target.value)}
        hint={<>Se envía a Anthropic con la estructura del modelo{status.model ? ` (${status.model})` : ''}. No escribas contraseñas ni datos personales. {prompt.length}/{max}</>}
      />
      <fieldset className="studio-scopes">
        <legend>Qué puede usar el agente</legend>
        <div className="studio-scope">
          <Switch checked disabled onChange={() => undefined} label="Modelo del ERP" />
          <p className="subtle">Siempre: catálogos, campos, procesos, reglas, rutas y riesgos, con tus permisos. Sin registros.</p>
        </div>
        <div className="studio-scope">
          <Switch checked={datos} onChange={setDatos} label="Leer registros (datos)" />
          <p className="subtle">Apagado por omisión. Lee solo lo que tú puedes ver, 20 registros por consulta.</p>
          {datos && (
            <Alert tone="warning" icon={ShieldAlert} title="Los registros que el agente lea viajan a Anthropic">
              Sus valores se envían al proveedor del modelo como parte de la conversación. Las respuestas de las herramientas de datos se borran de esta sesión al terminar; lo que el agente escriba o copie en sus pasos se conserva.
            </Alert>
          )}
        </div>
        <div className="studio-scope">
          <Switch checked={aislado && status.sandboxAvailable} disabled={!status.sandboxAvailable} onChange={setAislado} label="Probar en una copia aislada" />
          <p className="subtle">
            {status.sandboxAvailable
              ? 'Una base aparte con el modelo y sin datos reales: el agente aplica su plan ahí, crea registros de prueba y la borra al terminar.'
              : 'No disponible en este servidor (Studio:Sandbox o permiso para crear bases): el agente entregará sin probar en una copia.'}
          </p>
        </div>
      </fieldset>
      {error && <Alert tone="danger" title="No se inició la sesión">{error}</Alert>}
      <div className="studio-composer-foot">
        <span className="subtle">
          Hoy: {status.sessionsToday} de {status.limits.sessionsPerDay} sesiones · {formatUsd(status.costTodayUsd)} de {formatUsd(status.limits.costPerDayUsd)}.
          Por sesión: hasta {status.limits.maxSteps} pasos, {status.limits.maxMinutes} minutos y {formatUsd(status.limits.maxCostUsd)}.
        </span>
        <Button type="submit" variant="primary" icon={Send} loading={sending} disabled={!prompt.trim() || blocked}>Pedir a Studio</Button>
      </div>
    </form>
  );
}

/* ---------------- Sesión ---------------- */

function useNow(active: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [active]);
  return now;
}

function SessionView({ id, status, dataTools, onChanged }: { id: number; status: StudioStatusInfo; dataTools: ReadonlySet<string>; onChanged: () => void }) {
  const toast = useToast();
  const { reloadEntities } = useAuth();
  const [s, setS] = useState<StudioSession | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [pollFailed, setPollFailed] = useState(false);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState<'cancel' | 'discard' | 'apply' | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [refreshed, setRefreshed] = useState(false);
  const head = useRef<HTMLElement>(null);
  const onChangedRef = useRef(onChanged);
  onChangedRef.current = onChanged;
  const active = !!s && isActive(s.status);
  const now = useNow(active);

  // H2 CA1: lectura completa al abrir; mientras está activa, solo los pasos nuevos (`after`, sin compilar: R-033-10); al
  // terminar, otra lectura completa (la propuesta con su vista previa y su huella).
  useEffect(() => {
    let stop = false;
    let timer: number | undefined;
    const ctrl = new AbortController();
    let current: StudioSession | null = null;
    const full = async () => {
      current = await studioApi.session(id, undefined, ctrl.signal);
      if (!stop) setS(current);
      return current;
    };
    const poll = async () => {
      if (stop || !current) return;
      try {
        const x = await studioApi.session(id, lastSeq(current.trail), ctrl.signal);
        if (stop) return;
        setPollFailed(false);
        if (!isActive(x.status)) {
          await full();
          onChangedRef.current();
          return;
        }
        // Revisión exploratoria 035 (F-09): el historial seguía «En cola» con la sesión ya trabajando.
        if (x.status !== current.status) onChangedRef.current();
        current = { ...x, trail: mergeTrail(current.trail, x.trail) };
        setS(current);
      } catch {
        if (stop) return;
        setPollFailed(true);
      }
      timer = window.setTimeout(poll, POLL_MS);
    };
    setLoadError(null);
    full()
      .then((x) => { if (!stop && isActive(x.status)) timer = window.setTimeout(poll, POLL_MS); })
      .catch((e) => { if (!stop) setLoadError(e); });
    return () => { stop = true; ctrl.abort(); window.clearTimeout(timer); };
  }, [id, reload]);

  // El botón que se pulsó (Cancelar, Descartar, Revisar y aplicar) desaparece: el foco pasa al pedido, no a body.
  const focusHead = () => window.setTimeout(() => head.current?.focus(), 50);

  async function act(kind: 'cancel' | 'discard') {
    setBusy(kind);
    try {
      await (kind === 'cancel' ? studioApi.cancel(id) : studioApi.discard(id));
      toast.info(kind === 'cancel' ? 'Cancelando la sesión' : 'Propuesta descartada');
      onChanged();
      setReload((r) => r + 1);
      focusHead();
    } catch (err) {
      toast.fromError(err);
      setReload((r) => r + 1);
    } finally {
      setBusy(null);
    }
  }

  async function apply() {
    if (!s?.reviewHash) return;
    setBusy('apply');
    try {
      const r = await studioApi.apply(id, s.reviewHash);
      setConfirm(false);
      setRefreshed(false);
      setS(r.session);
      toast.success('Cambios aplicados', `Conjunto de cambios #${r.changeSetId}`);
      onChanged();
      focusHead();
      await reloadEntities();
    } catch (err) {
      setConfirm(false);
      toast.fromError(err, 'No se pudo aplicar la propuesta.');
      // H3 CA2: si el modelo cambió (409), la propuesta se recarga con su vista previa y su huella nuevas. Revisión
      // exploratoria 035 (F-08): el aviso y el botón esperan a la lectura nueva; antes podía aplicarse con la huella vieja.
      if (err instanceof ApiError && err.status === 409) {
        setBusy('apply');
        try {
          setS(await studioApi.session(id));
          setRefreshed(true);
        } catch {
          setReload((r) => r + 1);
        }
      } else setReload((r) => r + 1);
    } finally {
      setBusy(null);
    }
  }

  if (loadError) return <Alert tone="danger" title="No se pudo abrir la sesión">{problemText(loadError, 'Recarga la página e intenta de nuevo.')}</Alert>;
  if (!s) return <LoadingBlock />;

  const meters = consumption(s, status.limits, now);
  // Spec 036: el pedido llega como paso «user»; esta pantalla ya lo muestra arriba (la 037 la reemplaza por el chat).
  const steps = s.trail.filter((x) => x.kind !== 'user');
  const last = steps[steps.length - 1];
  const file = planFile(s);
  const hasOutcome = s.status === 'proposed' || s.status === 'answered' || s.status === 'applied' || s.status === 'discarded';

  return (
    <div className="stack studio-session">
      <article className="card card-pad stack" aria-label="Pedido" ref={head} tabIndex={-1}>
        <div className="row studio-session-head">
          <Badge tone={STATUS_TONE[s.status]}>{STATUS_LABEL[s.status]}</Badge>
          {s.scopes.map((x) => <Badge key={x}>{x}</Badge>)}
          {s.changeSetId && <Badge tone="success" icon={PackageCheck}>Conjunto de cambios #{s.changeSetId}</Badge>}
          <span className="subtle grow studio-date" style={{ textAlign: 'right' }}>{formatDateTime(s.createdAt)}</span>
        </div>
        <p className="studio-prompt"><span className="sr-only">Tu pedido: </span>{s.prompt}</p>
        {active && (
          <div className="row studio-working">
            <Spinner size={16} label="El agente está trabajando" />
            <span className="grow">{s.status === 'queued' ? 'En cola: empieza en unos segundos.' : 'El agente está trabajando…'}</span>
            <Button icon={Square} loading={busy === 'cancel'} onClick={() => void act('cancel')}>Cancelar</Button>
          </div>
        )}
        {pollFailed && active && <Alert tone="warning" title="No se pudo actualizar el avance">Se reintenta solo en unos segundos.</Alert>}
        {s.error && <Alert tone="warning" title="La sesión no terminó con una propuesta">{s.error}</Alert>}
        <dl className="studio-meters" aria-label="Consumo de la sesión">
          {meters.map((m) => (
            <div key={m.key} className={`studio-meter studio-meter-${m.key}`}>
              <dt>{m.label}</dt>
              <dd>
                <span className="studio-meter-text">{m.text}</span>
                <span
                  className={`studio-meter-bar${m.max > 0 && m.value / m.max >= 0.8 ? ' is-high' : ''}`}
                  role="meter"
                  aria-label={m.label}
                  aria-valuemin={0}
                  aria-valuemax={m.max}
                  aria-valuenow={m.value}
                  aria-valuetext={m.text}
                >
                  <span style={{ width: `${m.max > 0 ? Math.round((m.value / m.max) * 100) : 0}%` }} />
                </span>
              </dd>
            </div>
          ))}
        </dl>
      </article>

      <section className="card card-pad stack" aria-labelledby={`pasos-${s.id}`}>
        <h2 className="section-title" id={`pasos-${s.id}`}><ListChecks size={15} aria-hidden /> Pasos del agente ({steps.length})</h2>
        {/* Anuncia el último paso mientras trabaja (H2 CA1). */}
        <div className="sr-only" aria-live="polite">{active && last ? `Paso ${last.seq}: ${stepTitle(last)}. ${last.summary}` : ''}</div>
        {steps.length === 0 ? (
          <p className="subtle" style={{ margin: 0 }}>{active ? 'Todavía no hay pasos.' : 'La sesión no registró pasos.'}</p>
        ) : (
          <ol className="studio-steps">
            {steps.map((step) => <Step key={step.seq} step={step} dataTools={dataTools} />)}
          </ol>
        )}
      </section>

      {hasOutcome && !active && (
        <Outcome
          s={s}
          refreshed={refreshed}
          busy={busy}
          onApply={() => setConfirm(true)}
          onDiscard={() => void act('discard')}
          onDownload={file ? () => download(new Blob([file.content], { type: 'application/json' }), file.name) : undefined}
        />
      )}

      <Dialog
        open={confirm}
        onClose={() => { if (busy !== 'apply') setConfirm(false); }}
        title="Aplicar la propuesta de ERP AI Studio"
        description={s.plan?.description}
        footer={
          <>
            <Button onClick={() => setConfirm(false)} disabled={busy === 'apply'} data-autofocus>Cancelar</Button>
            <Button variant="primary" icon={Rocket} loading={busy === 'apply'} onClick={() => void apply()}>Aplicar cambios</Button>
          </>
        }
      >
        <div className="stack">
          <p className="muted" style={{ margin: 0 }}>
            Se aplicarán {s.plan?.operations.length ?? 0} operaciones al modelo real en una sola transacción, con tus permisos y en la
            auditoría. Es exactamente lo que revisaste: si el modelo cambió desde entonces, no se aplica nada.
          </p>
          {s.warnings.length > 0 && (
            <Alert tone="warning" icon={AlertTriangle} title="Revisa con atención">
              <ul className="import-errors">{s.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
            </Alert>
          )}
        </div>
      </Dialog>
    </div>
  );
}

const KIND_ICON: Record<StudioStep['kind'], LucideIcon> = { user: MessageSquareText, text: MessageSquareText, tool: Wrench, deliver: PackageCheck, limit: CircleSlash, error: XCircle };

function stepTitle(step: StudioStep) {
  if (step.kind === 'text') return 'El agente escribe';
  if (step.kind === 'deliver') return 'Entrega';
  if (step.kind === 'limit') return 'Tope alcanzado';
  return step.tool ?? 'Herramienta';
}

const STEP_STATUS: Record<StudioStep['status'], { label: string; tone: 'success' | 'danger' | 'warning' }> = {
  ok: { label: 'Correcto', tone: 'success' }, error: { label: 'Error', tone: 'danger' }, denied: { label: 'Negado', tone: 'warning' },
};

/** H2 CA1 y CA4: un paso, plegado; entrada y salida como texto (nunca como marcado ni enlaces). */
function Step({ step, dataTools }: { step: StudioStep; dataTools: ReadonlySet<string> }) {
  const Icon = KIND_ICON[step.kind] ?? Wrench;
  const st = STEP_STATUS[step.status] ?? STEP_STATUS.error;
  const text = step.kind === 'text' ? asText((step.output as { text?: unknown } | undefined)?.text ?? step.summary) : null;
  const input = asText(step.input);
  const output = asText(step.output);
  return (
    <li className={`studio-step studio-step-${step.kind}`}>
      <details>
        <summary>
          <Icon size={15} aria-hidden className="studio-step-icon" />
          <span className="studio-step-name">{stepTitle(step)}</span>
          <span className="studio-step-summary">{step.summary}</span>
          <span className="studio-step-meta">
            {step.kind !== 'text' && <Badge tone={st.tone}>{st.label}</Badge>}
            {step.kind === 'tool' && <span className="subtle studio-duration">{formatDuration(step.durationMs)}</span>}
          </span>
        </summary>
        <div className="studio-step-body">
          {text !== null ? (
            <p className="studio-text">{text}</p>
          ) : (
            <>
              {input && (
                <div>
                  <div className="studio-io-label">Entrada</div>
                  <pre className="code-block studio-io" tabIndex={0} aria-label={`Entrada del paso ${step.seq}`}>{input}</pre>
                </div>
              )}
              <div>
                <div className="studio-io-label">Salida</div>
                {output ? (
                  <pre className="code-block studio-io" tabIndex={0} aria-label={`Salida del paso ${step.seq}`}>{output}</pre>
                ) : (
                  <p className="subtle" style={{ margin: 0 }}>
                    {step.tool && dataTools.has(step.tool)
                      ? 'Se borró al terminar la sesión: contenía registros.'
                      : 'Sin salida guardada.'}
                  </p>
                )}
              </div>
            </>
          )}
        </div>
      </details>
    </li>
  );
}

/* ---------------- Propuesta ---------------- */

function Outcome({ s, refreshed, busy, onApply, onDiscard, onDownload }: {
  s: StudioSession; refreshed: boolean; busy: string | null; onApply: () => void; onDiscard: () => void; onDownload?: () => void;
}) {
  const proposed = s.status === 'proposed';
  const notes = reportNotes(s.report, s.trail);
  const report = s.report;
  return (
    <article className="card card-pad stack" aria-labelledby={`propuesta-${s.id}`}>
      <h2 className="section-title" id={`propuesta-${s.id}`}>
        <PackageCheck size={15} aria-hidden /> {s.plan ? 'Propuesta' : 'Respuesta del agente'}
      </h2>
      {s.summary && <p className="studio-text">{s.summary}</p>}

      {report && (report.tested.length > 0 || report.failures.length > 0 || report.risks.length > 0) && (
        <section className="studio-report" aria-labelledby={`informe-${s.id}`}>
          <h3 id={`informe-${s.id}`} className="studio-report-title"><FlaskConical size={14} aria-hidden /> Lo que el agente informa</h3>
          <p className="subtle" style={{ margin: 0 }}>Es una afirmación del agente: compárala con los pasos de arriba.</p>
          {notes.map((n) => <Alert key={n} tone="warning" icon={AlertTriangle} title={n} />)}
          {report.tested.length > 0 && (
            <>
              <h4 className="studio-report-sub">Pruebas</h4>
              <ul className="studio-tests">
                {report.tested.map((t, i) => (
                  <li key={i} className={t.ok ? 'is-ok' : 'is-fail'}>
                    {t.ok ? <CheckCircle2 size={15} aria-hidden /> : <XCircle size={15} aria-hidden />}
                    <span className="sr-only">{t.ok ? 'Superada: ' : 'No superada: '}</span>
                    <span>
                      {t.description}
                      {t.detail && <span className="subtle studio-test-detail">{t.detail}</span>}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          )}
          {report.failures.length > 0 && (
            <>
              <h4 className="studio-report-sub">Fallas</h4>
              <ul className="studio-list">{report.failures.map((f, i) => <li key={i}>{f}</li>)}</ul>
            </>
          )}
          {report.risks.length > 0 && (
            <>
              <h4 className="studio-report-sub">Riesgos</h4>
              <ul className="studio-list">{report.risks.map((r, i) => <li key={i}>{r}</li>)}</ul>
            </>
          )}
        </section>
      )}

      {s.warnings.length > 0 && proposed && (
        <Alert tone="warning" icon={AlertTriangle} title="Revisa con atención">
          <ul className="import-errors">{s.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
        </Alert>
      )}
      {refreshed && proposed && (
        <Alert tone="info" title="La propuesta se actualizó">El modelo cambió desde tu revisión: revisa la vista previa de nuevo antes de aplicarla.</Alert>
      )}
      {s.preview && proposed && (
        <PlanPreviewView preview={s.preview} validText="Si la apruebas, se aplica en una sola transacción. Si un paso falla, no se aplica ninguno." />
      )}
      {s.plan && (
        <details className="assistant-json">
          <summary><Braces size={14} aria-hidden /> Ver plan en JSON ({s.plan.operations.length} operaciones)</summary>
          <pre className="code-block" tabIndex={0} aria-label="Plan de cambios en JSON">{JSON.stringify(s.plan, null, 2)}</pre>
        </details>
      )}
      {s.blockedReason && <Alert tone="warning" icon={Ban} title="No se aplica en este entorno">{s.blockedReason} Descarga el plan para aplicarlo en un entorno de desarrollo y exportarlo como paquete.</Alert>}
      {s.status === 'applied' && <Alert tone="info" icon={CheckCircle2} title="Propuesta aplicada">{s.appliedAt ? `El ${formatDateTime(s.appliedAt)}.` : null}</Alert>}
      {(proposed || onDownload) && (
        <div className="row studio-actions">
          {onDownload && <Button icon={Download} onClick={onDownload}>Descargar plan</Button>}
          {proposed && <Button icon={Trash2} loading={busy === 'discard'} onClick={onDiscard}>Descartar</Button>}
          {proposed && s.canApply && <Button variant="primary" icon={Rocket} loading={busy === 'apply'} onClick={onApply}>Revisar y aplicar…</Button>}
        </div>
      )}
    </article>
  );
}
