import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { RotateCcw, WifiOff } from 'lucide-react';
import { ApiError } from '../../api/client';
import { companionApi } from '../../api/endpoints';
import type { CompanionAction, CompanionConversation, CompanionScreen, CompanionStatus } from '../../api/types';
import { AVATAR_TEXT, DATA_TOOLS, TOOL_LABELS, avatarState, latestAction, type AvatarState } from '../../lib/companion';
import { groupTurns, isActive, lastUserText, liveLabel } from '../../lib/studio';
import { LiveArea, Notice, TurnEnd, UserBubble, problemText, useNow } from '../../pages/studio/ChatView';
import { Composer, type ComposerHandle } from '../../pages/studio/Composer';
import { CopyButton, Markdown } from '../../pages/studio/Markdown';
import { ToolGroup } from '../../pages/studio/ToolGroup';
import { useAgentChat, type ChatApi } from '../../pages/studio/useAgentChat';
import { Alert, Button, LoadingBlock } from '../ui';
import { ActionCard } from './ActionCard';

/**
 * Spec 047 (H2, H5): una conversación del Acompañante en el panel. El flujo, el compositor, el texto en vivo, el Markdown
 * seguro y el grupo de herramientas son los de Studio (037, CLARIFY 2); lo propio son las tarjetas de las acciones, los
 * nombres para personas y el contexto de la pantalla en cada mensaje (H6).
 */
export function CompanionChat({ id, status, screen, contextNote, entityLabel, onState, onListChanged, onGone }: {
  id: number; status: CompanionStatus; screen: CompanionScreen | null; contextNote: ReactNode;
  entityLabel: (entity: string, fallback: string) => string;
  onState: (s: AvatarState, text: string) => void; onListChanged: () => void;
  /** La conversación ya no existe (se borró, caducó o es de otra empresa): el panel vuelve a la bienvenida. */
  onGone: () => void;
}) {
  // H6 CA1: el contexto se lee al enviar (la persona pudo navegar desde que abrió el panel).
  const screenRef = useRef(screen);
  screenRef.current = screen;
  const chatApi = useMemo<ChatApi<CompanionConversation>>(() => ({
    load: (cid) => companionApi.conversation(cid),
    send: (cid, text) => companionApi.send(cid, text, screenRef.current),
    cancel: (cid) => companionApi.cancel(cid),
    streamUrl: (cid, after) => companionApi.streamUrl(cid, after),
    // La respuesta 202 no trae pasos: se conserva lo que ya se ve.
    sent: (s, res) => ({ ...s, ...res, trail: s.trail, actions: s.actions }),
  }), []);
  const chat = useAgentChat(id, chatApi, onListChanged, DATA_TOOLS);
  const { session: s, live, conn } = chat;
  const pending = chat.pending && !s?.trail.some((x) => x.kind === 'user' && x.seq > chat.pending!.after) ? chat.pending : null;
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [atEnd, setAtEnd] = useState(true);
  const composer = useRef<ComposerHandle>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const now = useNow(30_000);

  const active = !!s && isActive(s.status);
  const turns = useMemo(() => (s ? groupTurns(s.trail, s.title) : []), [s]);

  // H5: un paso `action` llega por el flujo antes que su acción (que solo trae la lectura completa): se relee una vez por id.
  const asked = useRef(new Set<number>());
  useEffect(() => {
    if (!s) return;
    const missing = s.trail
      .map((x) => (x.kind === 'action' ? (x.output as { accion?: unknown } | undefined)?.accion : null))
      .filter((x): x is number => typeof x === 'number' && !s.actions.some((a) => a.id === x) && !asked.current.has(x));
    if (!missing.length) return;
    for (const m of missing) asked.current.add(m);
    void chat.full().catch(() => undefined);
  }, [s, chat.full]);

  // H3: el estado del avatar y su texto.
  const avatar = avatarState(s, live, { failed: !!chat.loadError, sending: !!pending });
  const avatarText = avatar === 'tool' || avatar === 'thinking' ? (conn === 'reconnecting' ? 'Reconectando…' : liveLabel(live, s?.status ?? 'queued', TOOL_LABELS)) : AVATAR_TEXT[avatar];
  useEffect(() => { onState(avatar, avatarText); }, [avatar, avatarText, onState]);
  useEffect(() => { if (chat.loadError instanceof ApiError && chat.loadError.status === 404) onGone(); }, [chat.loadError, onGone]);
  // Revisión adversarial 047 (F-06): en reposo no hay flujo; si otra pestaña decidió una propuesta, se ve al volver a esta.
  const fullRef = useRef(chat.full);
  fullRef.current = chat.full;
  useEffect(() => {
    const onVisible = () => { if (document.visibilityState === 'visible') void fullRef.current().catch(() => undefined); };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, []);

  /* ---------- Desplazamiento: si la persona está al final, la respuesta la sigue ---------- */
  const toEnd = useCallback(() => {
    const sc = scroller.current;
    if (sc) sc.scrollTop = sc.scrollHeight;
  }, []);
  useLayoutEffect(() => { if (atEnd) toEnd(); }, [s, live, pending, atEnd, toEnd]);
  const onScroll = () => {
    const sc = scroller.current;
    if (sc) setAtEnd(sc.scrollHeight - sc.scrollTop - sc.clientHeight < 48);
  };
  useEffect(() => { if (s && window.matchMedia?.('(pointer: fine)').matches) composer.current?.focus(); }, [s === null]);

  async function send(text: string, restore?: () => void) {
    const t = text.trim();
    if (!t || sending) return;
    setSending(true);
    setSendError(null);
    setAtEnd(true);
    composer.current?.focus();
    const err = await chat.send(t);
    setSending(false);
    if (err) {
      setSendError(problemText(err, 'No se pudo enviar el mensaje. Revisa tu conexión.'));
      restore?.();
    }
    window.setTimeout(() => composer.current?.focus(), 0);
  }

  async function stop() {
    composer.current?.focus();
    setStopping(true);
    try { await chat.stop(); } catch (e) { setSendError(problemText(e, 'No se pudo detener la respuesta.')); } finally { setStopping(false); }
  }

  const decided = useRef(new Map<number, CompanionAction>());
  const onActionChanged = useCallback((a?: CompanionAction) => {
    if (a && a.status !== 'proposed') decided.current.set(a.id, a);
    if (a) chat.setSession((x) => ({ ...x, actions: x.actions.map((y) => (y.id === a.id ? a : y)) }));
    void chat.full().catch(() => undefined);
    onListChanged();
  }, [chat.setSession, chat.full, onListChanged]);

  if (chat.loadError) {
    return (
      <div className="cmp-msg">
        {/* Revisión adversarial 047 (F-11): una falla pasajera al releer no es definitiva. */}
        <Alert tone="danger" title="No se pudo abrir la conversación" action={<Button size="sm" icon={RotateCcw} onClick={() => window.location.reload()}>Intentar de nuevo</Button>}>
          {problemText(chat.loadError, 'Abre otra o empieza una nueva.')}
        </Alert>
      </div>
    );
  }
  if (!s) return <LoadingBlock />;

  const lastIdx = turns.length - 1;
  const finished = !active && !pending;
  const retry = () => { const text = lastUserText(turns); if (text) void send(text); };

  return (
    <div className="chat-pane">
      <div className="chat-scroll" ref={scroller} onScroll={onScroll}>
        <div className="chat-column" role="log" aria-live="off" aria-label="Conversación con el Acompañante">
          {turns.map((t, i) => {
            const last = i === lastIdx && !pending;
            return (
              <section key={t.turn} className="chat-turn" aria-label={`Mensaje ${t.turn}`}>
                <UserBubble text={t.user.text} at={t.user.at} now={now} />
                <div className="chat-assistant">
                  {t.segments.map((seg) => {
                    if (seg.kind === 'text') return <Markdown key={seg.seq} text={seg.text} />;
                    if (seg.kind === 'tools') return <ToolGroup key={seg.steps[0].seq} steps={seg.steps} dataTools={DATA_TOOLS} labels={TOOL_LABELS} />;
                    if (seg.kind === 'action') {
                      const action = latestAction(s.actions.find((a) => a.id === seg.action), seg.action === null ? undefined : decided.current.get(seg.action));
                      return <ActionCard key={seg.seq} action={action} entityLabel={entityLabel} onChanged={onActionChanged} />;
                    }
                    if (seg.kind === 'notice') return <Notice key={seg.step.seq} step={seg.step} />;
                    return null;
                  })}
                  {last && active && <LiveArea live={live} status={s.status} toolLabels={TOOL_LABELS} />}
                  {last && finished && <TurnEnd s={s} onRetry={s.canSend ? retry : undefined} />}
                  {finished && t.segments.some((x) => x.kind === 'text') && (
                    <div className={`chat-msg-actions${last ? ' is-visible' : ''}`}>
                      <CopyButton text={t.segments.map((x) => (x.kind === 'text' ? x.text : '')).filter(Boolean).join('\n\n')} label="Copiar la respuesta" />
                    </div>
                  )}
                </div>
              </section>
            );
          })}
          {pending && (
            <section className="chat-turn" aria-label="Mensaje enviado">
              <UserBubble text={pending.text} at={null} now={now} />
              <div className="chat-assistant"><LiveArea live={live} status="queued" toolLabels={TOOL_LABELS} /></div>
            </section>
          )}
        </div>
      </div>

      <div className="chat-dock">
        {conn === 'offline' && active && (
          <div className="chat-notice is-error" role="alert">
            <WifiOff size={16} aria-hidden />
            <span className="grow">Sin conexión con el avance. El Acompañante sigue trabajando en el servidor.</span>
            <Button size="sm" icon={RotateCcw} onClick={chat.reconnect}>Intentar de nuevo</Button>
          </div>
        )}
        {conn === 'reconnecting' && active && <p className="chat-reconnecting"><span className="chat-dot" aria-hidden /> Reconectando…</p>}
        {sendError && <div className="chat-send-error"><Alert tone="danger" title="No se envió el mensaje">{sendError}</Alert></div>}
        <Composer
          ref={composer}
          label="Mensaje para el Acompañante"
          value={draft}
          onChange={(v) => { setDraft(v); if (sendError) setSendError(null); }}
          onSubmit={() => { const text = draft; setDraft(''); void send(text, () => setDraft(text)); }}
          onStop={() => void stop()}
          working={active}
          stopping={stopping}
          sending={sending}
          blocked={!active && !s.canSend ? s.sendBlockedReason ?? 'No se pueden mandar más mensajes en esta conversación.' : null}
          maxChars={status.limits.maxPromptChars}
          placeholder="Pregunta o pide algo…"
          footer={contextNote}
        />
      </div>
    </div>
  );
}
