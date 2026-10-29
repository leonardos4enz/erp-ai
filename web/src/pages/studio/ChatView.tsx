import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type Ref } from 'react';
import { ArrowDown, Gauge, Info, MessagesSquare, Pencil, RotateCcw, Rocket, StopCircle, WifiOff, XCircle } from 'lucide-react';
import { ApiError } from '../../api/client';
import { studioApi } from '../../api/endpoints';
import type { StudioSession, StudioStatusInfo, StudioStep } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Dialog } from '../../components/Dialog';
import { useToast } from '../../components/Toast';
import { Alert, Badge, Button, LoadingBlock } from '../../components/ui';
import { download } from '../../lib/svgExport';
import {
  STATUS_LABEL, STATUS_TONE, consumption, currentProposalSeq, groupTurns, isActive, lastUserText, liveLabel, planFile, relativeTime,
  type Turn,
} from '../../lib/studio';
import { Composer, type ComposerHandle } from './Composer';
import { LiveText, reducedMotion } from './LiveText';
import { CopyButton, Markdown } from './Markdown';
import { ProposalCard, ReportView } from './ProposalCard';
import { ToolGroup } from './ToolGroup';
import { useStudioChat } from './useStudioChat';

export function problemText(e: unknown, fallback: string) {
  if (!(e instanceof ApiError)) return fallback;
  const items = Object.values(e.fieldErrors).flat();
  return items.length ? items.join(' ') : e.detail ?? e.title;
}

/** Texto de la respuesta de un turno (para «Copiar»): sus textos y el resumen de su entrega, tal cual. */
function answerText(t: Turn): string {
  return t.segments.map((x) => (x.kind === 'text' ? x.text : x.kind === 'deliver' ? x.delivery.summary : '')).filter(Boolean).join('\n\n');
}

function useNow(ms: number) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), ms); return () => window.clearInterval(t); }, [ms]);
  return now;
}

/**
 * Spec 037: una conversación de ERP AI Studio como chat (análisis de Claude web, aplicado con la identidad de ERP AI). El
 * mensaje enviado sube al borde de arriba y la respuesta crece debajo sin mover la vista; todo lo que viene del agente se
 * pinta como texto o con el Markdown seguro.
 */
export function ChatView({ id, status, dataTools, onListChanged, onOpenBar }: {
  id: number; status: StudioStatusInfo; dataTools: ReadonlySet<string>; onListChanged: () => void; onOpenBar: () => void;
}) {
  const toast = useToast();
  const { reloadEntities } = useAuth();
  const chat = useStudioChat(id, onListChanged, dataTools);
  const { session: s, live, conn } = chat;
  // El mensaje enviado se ve como pendiente hasta que su paso `user` llega (nunca los dos a la vez).
  const pending = chat.pending && !chat.session?.trail.some((x) => x.kind === 'user' && x.seq > chat.pending!.after) ? chat.pending : null;
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState<'discard' | 'apply' | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [refreshed, setRefreshed] = useState(false);
  const [meters, setMeters] = useState(false);
  const [atEnd, setAtEnd] = useState(true);
  const [viewH, setViewH] = useState(0);
  const composer = useRef<ComposerHandle>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const endMark = useRef<HTMLDivElement>(null);
  const lastBubble = useRef<HTMLDivElement>(null);
  const placed = useRef(false);
  const metersId = useId();
  const now = useNow(30_000);

  const active = !!s && isActive(s.status);
  const turns = useMemo(() => (s ? groupTurns(s.trail, s.prompt) : []), [s]);
  const proposalSeq = s ? currentProposalSeq(s.trail, s.planStatus) : null;

  /* ---------- Scroll (análisis §3) ---------- */
  const behavior = (): ScrollBehavior => (reducedMotion() ? 'auto' : 'smooth');
  const bubbleToTop = useCallback((how: ScrollBehavior) => {
    const sc = scroller.current;
    const b = lastBubble.current;
    if (!sc || !b) return;
    const top = b.getBoundingClientRect().top - sc.getBoundingClientRect().top + sc.scrollTop - 16;
    sc.scrollTo({ top: Math.max(0, top), behavior: how });
  }, []);
  const toEnd = useCallback((how: ScrollBehavior) => {
    const sc = scroller.current;
    const m = endMark.current;
    if (!sc || !m) return;
    const top = m.getBoundingClientRect().bottom - sc.getBoundingClientRect().top + sc.scrollTop - sc.clientHeight + 24;
    sc.scrollTo({ top: Math.max(0, top), behavior: how });
  }, []);

  // Al abrir: una conversación trabajando muestra el último mensaje arriba; una terminada, el final.
  useLayoutEffect(() => {
    if (!s || placed.current || viewH === 0) return;
    placed.current = true;
    if (active) bubbleToTop('auto'); else toEnd('auto');
    // H2 CA2: el primer mensaje abre la conversación; el foco sigue en el compositor (como al abrirla desde la barra). En
    // pantallas táctiles no: abriría el teclado al solo leer.
    if (window.matchMedia?.('(pointer: fine)').matches) composer.current?.focus();
  }, [s, active, viewH, bubbleToTop, toEnd]);

  // H2 CA3: el mensaje recién enviado sube al borde superior; la respuesta crece debajo sin mover la vista.
  useLayoutEffect(() => { if (pending) bubbleToTop(behavior()); }, [pending, bubbleToTop]);

  useEffect(() => {
    const sc = scroller.current;
    if (!sc) return;
    const ro = new ResizeObserver(() => setViewH(sc.clientHeight));
    ro.observe(sc);
    return () => ro.disconnect();
  }, [s === null]);

  useEffect(() => {
    const sc = scroller.current;
    const m = endMark.current;
    if (!sc || !m) return;
    const io = new IntersectionObserver(([e]) => {
      setAtEnd(e.isIntersecting || (e.rootBounds ? e.boundingClientRect.top <= e.rootBounds.bottom : true));
    }, { root: sc, threshold: 0 });
    io.observe(m);
    return () => io.disconnect();
  });

  /* ---------- Acciones ---------- */
  const focusComposer = () => window.setTimeout(() => composer.current?.focus(), 0);

  async function send(text: string, restore?: () => void) {
    const t = text.trim();
    if (!t || sending) return;
    setSending(true);
    setSendError(null);
    // Revisión adversarial 037 (F-07): el botón pulsado (Reintentar, Enviar de la edición) desaparece al enviar: el foco
    // pasa al compositor antes de esperar al servidor, no a body.
    composer.current?.focus();
    const err = await chat.send(t);
    setSending(false);
    if (err) {
      setSendError(problemText(err, 'No se pudo enviar el mensaje. Revisa tu conexión.'));
      restore?.();
    }
    focusComposer();
  }

  function submitDraft() {
    const text = draft;
    setDraft('');
    void send(text, () => setDraft(text));
  }

  async function stop() {
    // Como F-07: «Detener» se deshabilita mientras espera al servidor; el foco pasa antes al compositor, no a body.
    composer.current?.focus();
    setStopping(true);
    try { await chat.stop(); } catch (e) { toast.fromError(e, 'No se pudo detener la respuesta.'); } finally { setStopping(false); focusComposer(); }
  }

  function retry() {
    const text = lastUserText(turns);
    if (text) void send(text);
  }

  function startEdit() {
    const text = lastUserText(turns);
    if (text !== null) setEditing(text);
  }
  function cancelEdit() { setEditing(null); focusComposer(); }
  function sendEdit() {
    const text = editing ?? '';
    setEditing(null);
    void send(text, () => setEditing(text));
  }

  async function discard() {
    setBusy('discard');
    try {
      await studioApi.discard(id);
      toast.info('Propuesta descartada');
      await chat.full();
      onListChanged();
      focusComposer();
    } catch (err) {
      toast.fromError(err);
      void chat.full().catch(() => undefined);
    } finally { setBusy(null); }
  }

  async function apply() {
    if (!s?.reviewHash) return;
    setBusy('apply');
    try {
      const r = await studioApi.apply(id, s.reviewHash);
      setConfirm(false);
      setRefreshed(false);
      chat.setSession(r.session);
      toast.success('Cambios aplicados', `Conjunto de cambios #${r.changeSetId}`);
      onListChanged();
      focusComposer();
      await reloadEntities();
    } catch (err) {
      setConfirm(false);
      toast.fromError(err, 'No se pudo aplicar la propuesta.');
      // H5 CA2: si el modelo cambió (409), la propuesta se recarga con su vista previa y su huella nuevas antes de ofrecerla.
      if (err instanceof ApiError && err.status === 409) {
        try { await chat.full(); setRefreshed(true); } catch { /* el aviso ya se mostró */ }
      } else void chat.full().catch(() => undefined);
    } finally { setBusy(null); }
  }

  if (chat.loadError) {
    return (
      <div className="chat-pane-msg">
        <Alert tone="danger" title="No se pudo abrir la conversación">{problemText(chat.loadError, 'Recarga la página e intenta de nuevo.')}</Alert>
      </div>
    );
  }
  if (!s) return <LoadingBlock />;

  const finished = !active && !pending;
  const lastIdx = turns.length - 1;
  const canEdit = finished && s.canSend && editing === null;
  const file = planFile(s);
  const announce = active
    ? (conn === 'reconnecting' ? 'Reconectando…' : liveLabel(live, s.status))
    // Una región aria-live no lee lo que ya tiene al montarse: el estado final solo se anuncia al cambiar.
    : s.status === 'cancelled' ? 'Detuviste la respuesta.'
        : s.status === 'failed' || s.status === 'interrupted' ? 'La respuesta no terminó.' : 'Respuesta lista.';
  const minH = viewH > 0 ? Math.max(0, viewH - 24) : undefined;

  return (
    <div className="chat-pane">
      <header className="chat-header">
        <button type="button" className="chat-icon-btn chat-bar-open" aria-label="Conversaciones" onClick={onOpenBar}><MessagesSquare size={17} aria-hidden /></button>
        <h1 className="chat-brand">ERP AI Studio</h1>
        <p className="chat-header-title" title={s.prompt}>{s.prompt}</p>
        <div className="chat-header-meta">
          {s.scopes.map((x) => <Badge key={x}>{x}</Badge>)}
          <Badge tone={STATUS_TONE[s.status]}>{STATUS_LABEL[s.status]}</Badge>
          <button type="button" className="chat-icon-btn" aria-label="Consumo de la conversación" aria-expanded={meters} aria-controls={metersId} onClick={() => setMeters((m) => !m)}>
            <Gauge size={17} aria-hidden />
          </button>
        </div>
      </header>
      {meters && (
        <dl id={metersId} className="chat-meters" aria-label="Consumo de la conversación">
          {consumption(s, status.limits).map((m) => (
            <div key={m.key} className={`chat-meter chat-meter-${m.key}`}>
              <dt>{m.label}</dt>
              <dd>
                <span className="chat-meter-text">{m.text}</span>
                <span className={`chat-meter-bar${m.max > 0 && m.value / m.max >= 0.8 ? ' is-high' : ''}`} role="meter" aria-label={m.label}
                  aria-valuemin={0} aria-valuemax={m.max} aria-valuenow={m.value} aria-valuetext={m.text}>
                  <span style={{ width: `${m.max > 0 ? Math.round((m.value / m.max) * 100) : 0}%` }} />
                </span>
              </dd>
            </div>
          ))}
        </dl>
      )}

      <div className="chat-scroll" ref={scroller}>
        <div className="chat-column" role="log" aria-live="off" aria-label="Conversación">
          {turns.map((t, i) => {
            const last = i === lastIdx && !pending;
            return (
              <section key={t.turn} className="chat-turn" aria-label={`Mensaje ${t.turn}`} style={last ? { minHeight: minH } : undefined}>
                {last && editing !== null ? (
                  <EditBox value={editing} original={t.user.text} onChange={setEditing} onCancel={cancelEdit} onSend={sendEdit} />
                ) : (
                  <UserBubble text={t.user.text} at={t.user.at} now={now} innerRef={last ? lastBubble : undefined}
                    onEdit={last && canEdit ? startEdit : undefined} />
                )}
                <div className="chat-assistant">
                  {t.segments.map((seg) => {
                    if (seg.kind === 'text') return <Markdown key={seg.seq} text={seg.text} />;
                    if (seg.kind === 'tools') return <ToolGroup key={seg.steps[0].seq} steps={seg.steps} dataTools={dataTools} />;
                    if (seg.kind === 'notice') return <Notice key={seg.step.seq} step={seg.step} />;
                    const isProposal = seg.seq === proposalSeq || seg.delivery.hasPlan;
                    return (
                      <div key={seg.seq} className="chat-deliver">
                        {seg.delivery.summary && <Markdown text={seg.delivery.summary} />}
                        {seg.delivery.report && <ReportView report={seg.delivery.report} steps={t.steps} id={`informe-${seg.seq}`} />}
                        {isProposal && (
                          <ProposalCard s={s} replaced={seg.seq !== proposalSeq} working={active} refreshed={refreshed} busy={busy}
                            onApply={() => setConfirm(true)} onDiscard={() => void discard()}
                            onDownload={file ? () => download(new Blob([file.content], { type: 'application/json' }), file.name) : undefined} />
                        )}
                      </div>
                    );
                  })}
                  {last && active && <LiveArea live={live} status={s.status} />}
                  {last && finished && <TurnEnd s={s} onRetry={s.canSend ? retry : undefined} onEdit={canEdit ? startEdit : undefined} />}
                  {last && finished && answerText(t) && (
                    <div className="chat-msg-actions is-visible">
                      <CopyButton text={answerText(t)} label="Copiar la respuesta" />
                      {s.canSend && <button type="button" className="chat-icon-btn" aria-label="Reintentar" title="Reintentar" onClick={retry}><RotateCcw size={15} aria-hidden /></button>}
                    </div>
                  )}
                  {!last && answerText(t) && (
                    <div className="chat-msg-actions"><CopyButton text={answerText(t)} label="Copiar la respuesta" /></div>
                  )}
                  {last && <div ref={endMark} className="chat-end" aria-hidden />}
                </div>
              </section>
            );
          })}
          {pending && (
            <section className="chat-turn" aria-label="Mensaje enviado" style={{ minHeight: minH }}>
              <UserBubble text={pending.text} at={null} now={now} innerRef={lastBubble} />
              <div className="chat-assistant">
                <LiveArea live={live} status="queued" />
                <div ref={endMark} className="chat-end" aria-hidden />
              </div>
            </section>
          )}
        </div>
      </div>

      <div className="chat-dock">
        {!atEnd && (
          <button type="button" className="chat-to-end" aria-label="Ir al final" title="Ir al final" onClick={() => toEnd(behavior())}>
            <ArrowDown size={17} aria-hidden />
          </button>
        )}
        {conn === 'offline' && active && (
          <div className="chat-notice is-error" role="alert">
            <WifiOff size={16} aria-hidden />
            <span className="grow">Sin conexión con el avance de la conversación. El agente sigue trabajando en el servidor.</span>
            <Button size="sm" icon={RotateCcw} onClick={chat.reconnect}>Intentar de nuevo</Button>
          </div>
        )}
        {conn === 'reconnecting' && active && <p className="chat-reconnecting"><span className="chat-dot" aria-hidden /> Reconectando…</p>}
        {sendError && (
          <div className="chat-send-error">
            <Alert tone="danger" title="No se envió el mensaje">{sendError}</Alert>
          </div>
        )}
        <Composer
          ref={composer}
          value={draft}
          onChange={(v) => { setDraft(v); if (sendError) setSendError(null); }}
          onSubmit={submitDraft}
          onStop={() => void stop()}
          working={active}
          stopping={stopping}
          sending={sending}
          blocked={!active && !s.canSend ? s.sendBlockedReason ?? 'No se pueden mandar más mensajes en esta conversación.' : null}
          maxChars={status.limits.maxPromptChars}
          placeholder="Responde o pide un ajuste…"
          footer={<p className="chat-composer-note">Studio trabaja con tus permisos. Nada se aplica sin que lo revises.</p>}
        />
      </div>
      <div className="sr-only" role="status" aria-live="polite">{announce}</div>

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
            <Alert tone="warning" title="Revisa con atención">
              <ul className="import-errors">{s.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
            </Alert>
          )}
        </div>
      </Dialog>
    </div>
  );
}

function UserBubble({ text, at, now, innerRef, onEdit }: {
  text: string; at: string | null; now: number; innerRef?: Ref<HTMLDivElement>; onEdit?: () => void;
}) {
  return (
    <div className="chat-user" ref={innerRef}>
      <div className="chat-bubble"><span className="sr-only">Tú: </span>{text}</div>
      <div className="chat-msg-actions">
        {at && <time className="chat-time studio-date" dateTime={at} title={new Date(at).toLocaleString('es-MX')}>{relativeTime(at, now)}</time>}
        {onEdit && <button type="button" className="chat-icon-btn" aria-label="Editar el mensaje" title="Editar" onClick={onEdit}><Pencil size={15} aria-hidden /></button>}
        <CopyButton text={text} label="Copiar el mensaje" />
      </div>
    </div>
  );
}

/** H4 CA3: editar el último mensaje en su lugar. Esc cancela; enviar solo si cambió. */
function EditBox({ value, original, onChange, onCancel, onSend }: {
  value: string; original: string; onChange: (v: string) => void; onCancel: () => void; onSend: () => void;
}) {
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => { const el = ref.current; if (el) { el.focus(); el.setSelectionRange(el.value.length, el.value.length); } }, []);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 384)}px`;
  }, [value]);
  const changed = value.trim() !== '' && value.trim() !== original.trim();
  return (
    <div className="chat-edit">
      <textarea ref={ref} className="chat-edit-input" aria-label="Editar el mensaje" value={value} onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') { e.preventDefault(); onCancel(); }
          else if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); if (changed) onSend(); }
        }} />
      <div className="chat-edit-foot">
        <span className="chat-edit-note"><Info size={14} aria-hidden /> Se envía como un mensaje nuevo; el anterior y su respuesta quedan en la conversación.</span>
        <Button size="sm" onClick={onCancel}>Cancelar</Button>
        <Button size="sm" variant="primary" disabled={!changed} onClick={onSend}>Enviar</Button>
      </div>
    </div>
  );
}

/** H3 CA1: mientras el turno corre: el punto que late, el texto en vivo o la herramienta en curso. */
function LiveArea({ live, status }: { live: ReturnType<typeof useStudioChat>['live']; status: StudioSession['status'] }) {
  const writing = live?.kind === 'text' && !!live.text;
  return (
    <div className="chat-live">
      {writing && <LiveText text={live.text!} />}
      {!writing && (
        <p className="chat-status-line" key={live?.kind === 'tool' ? `tool-${live.tool}` : live?.kind ?? 'w'}>
          <span className="chat-dot" aria-hidden />
          <span>{liveLabel(live, status)}</span>
        </p>
      )}
    </div>
  );
}

/** Tope o error registrado como paso. */
function Notice({ step }: { step: StudioStep }) {
  return (
    <div className="chat-notice is-warning">
      <XCircle size={16} aria-hidden />
      <span>{step.summary}</span>
    </div>
  );
}

/** H4 CA1 y CA2: cómo terminó el último turno si no fue con una respuesta (detenido, falló, interrumpido). */
function TurnEnd({ s, onRetry, onEdit }: { s: StudioSession; onRetry?: () => void; onEdit?: () => void }) {
  const stopped = s.status === 'cancelled';
  const failed = s.status === 'failed' || s.status === 'interrupted';
  if (!stopped && !failed) return null;
  return (
    <div className={`chat-notice ${stopped ? 'is-info' : 'is-error'}`}>
      {stopped ? <StopCircle size={16} aria-hidden /> : <XCircle size={16} aria-hidden />}
      <span className="grow">{stopped ? 'Detuviste la respuesta.' : s.error ?? 'La respuesta no terminó.'}</span>
      {onEdit && <Button size="sm" icon={Pencil} onClick={onEdit}>Editar el mensaje</Button>}
      {onRetry && <Button size="sm" icon={RotateCcw} onClick={onRetry}>Intentar de nuevo</Button>}
    </div>
  );
}
