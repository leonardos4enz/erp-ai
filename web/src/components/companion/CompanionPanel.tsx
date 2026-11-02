import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { Eye, History, KeyRound, PanelRightClose, RotateCcw, SquarePen, Trash2 } from 'lucide-react';
import { ApiError } from '../../api/client';
import { companionApi } from '../../api/endpoints';
import type { CompanionScreen, CompanionStatus } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { AVATAR_TEXT, screenContext, suggestions, type AvatarState } from '../../lib/companion';
import { useAsync } from '../../lib/hooks';
import { isActive, relativeTime } from '../../lib/studio';
import { problemText, useNow } from '../../pages/studio/ChatView';
import { Composer, type ComposerHandle } from '../../pages/studio/Composer';
import { ConfirmDialog } from '../Dialog';
import { Alert, Button, EmptyState, LoadingBlock } from '../ui';
import { CompanionAvatar } from './CompanionAvatar';
import { CompanionChat } from './CompanionChat';

type View = 'chat' | 'history';

/**
 * Spec 047 (H1): el panel del Acompañante, a la derecha de toda la app. Vive en el `Shell` (fuera del `Outlet`): la
 * conversación abierta se conserva al navegar. Estados: cargando, error, no disponible, bienvenida, historial y conversación.
 */
export function CompanionPanel({ id, conversation, onConversation, onClose }: {
  id: string; conversation: number | null; onConversation: (id: number | null) => void; onClose: () => void;
}) {
  const [tick, setTick] = useState(0);
  const status = useAsync((s) => companionApi.status(s), [tick]);
  const [view, setView] = useState<View>('chat');
  const [avatar, setAvatar] = useState<{ state: AvatarState; text: string }>({ state: 'idle', text: AVATAR_TEXT.idle });
  const { entities } = useAuth();
  const { pathname } = useLocation();
  const screen = screenContext(pathname);
  const opened = useRef(false);
  const st = status.data;

  // Con una conversación trabajando, el panel abre esa (una vez, al cargar).
  useEffect(() => {
    if (opened.current || !st) return;
    opened.current = true;
    if (conversation === null && st.activeId) onConversation(st.activeId);
  }, [st, conversation, onConversation]);

  const onState = useCallback((state: AvatarState, text: string) => setAvatar((a) => (a.state === state && a.text === text ? a : { state, text })), []);
  const refresh = useCallback(() => setTick((t) => t + 1), []);
  useEffect(() => { if (conversation === null || view === 'history') onState(status.error ? 'error' : 'idle', status.error ? AVATAR_TEXT.error : AVATAR_TEXT.idle); }, [conversation, view, status.error, onState]);

  const entityLabel = useCallback((entity: string, fallback: string) =>
    entities.find((e) => e.name.toLowerCase() === entity.toLowerCase())?.label ?? fallback, [entities]);
  const def = screen ? entities.find((e) => e.name.toLowerCase() === screen.entity.toLowerCase()) : undefined;
  // H6 CA1: solo se nombra lo que la persona puede ver (el servidor ignora el resto).
  const contextNote = (
    <p className="cmp-context">
      {def && screen ? (
        <><Eye size={13} aria-hidden /><span>Viendo: <strong>{screen.id ? `${def.label} · n.º ${screen.id}` : def.labelPlural}</strong></span></>
      ) : (
        <span>Lee con tus permisos. Nada cambia sin tu confirmación.</span>
      )}
    </p>
  );

  const open = (cid: number | null) => { onConversation(cid); setView('chat'); };
  const gone = useCallback(() => onConversation(null), [onConversation]);

  let body: ReactNode;
  if (status.loading && !st) body = <LoadingBlock />;
  // Revisión adversarial 047 (F-02): el estado se relee tras cada cambio; una falla pasajera con datos ya cargados no
  // desmonta la conversación (se perdían el borrador, los avisos y el flujo).
  else if (!st) {
    body = (
      <div className="cmp-msg">
        <Alert tone="danger" title="No se pudo cargar el Acompañante" action={<Button size="sm" icon={RotateCcw} onClick={refresh}>Intentar de nuevo</Button>}>
          {problemText(status.error, 'Revisa tu conexión.')}
        </Alert>
      </div>
    );
  } else if (!st.available) {
    body = (
      <div className="cmp-msg">
        <EmptyState icon={KeyRound} title="El Acompañante no está disponible" description={st.reason ?? 'Pídele a un administrador que lo active.'} />
      </div>
    );
  } else if (view === 'history') {
    body = <HistoryView current={conversation} onOpen={open} onDeleted={(cid) => { if (cid === conversation) onConversation(null); refresh(); }} tick={tick} />;
  } else if (conversation !== null) {
    body = (
      <CompanionChat key={conversation} id={conversation} status={st} screen={screen} contextNote={contextNote} entityLabel={entityLabel}
        onState={onState} onListChanged={refresh} onGone={gone} />
    );
  } else {
    body = <Welcome status={st} screen={screen} contextNote={contextNote} onStarted={(cid) => { refresh(); open(cid); }} onOpen={open} onState={onState} />;
  }

  return (
    <aside id={id} className="cmp-panel" aria-label="Acompañante">
      <header className="cmp-head">
        <CompanionAvatar state={avatar.state} size={44} />
        <h2 className="cmp-head-title">
          <span className="cmp-head-name">Acompañante</span>
          <span className="cmp-head-state">{avatar.text}</span>
        </h2>
        {st?.available && (
          <>
            <button type="button" className="chat-icon-btn" aria-label="Conversaciones" title="Conversaciones" aria-pressed={view === 'history'}
              onClick={() => setView((v) => (v === 'history' ? 'chat' : 'history'))}>
              <History size={17} aria-hidden />
            </button>
            <button type="button" className="chat-icon-btn" aria-label="Nueva conversación" title="Nueva conversación" onClick={() => open(null)}>
              <SquarePen size={17} aria-hidden />
            </button>
          </>
        )}
        <button type="button" className="chat-icon-btn" aria-label="Cerrar el Acompañante" title="Cerrar" onClick={onClose}>
          <PanelRightClose size={17} aria-hidden />
        </button>
      </header>
      <div className="cmp-body">{body}</div>
      {/* H3 CA2: el estado del avatar en texto (el avatar es decorativo). */}
      <div className="sr-only" role="status" aria-live="polite">{avatar.text}</div>
    </aside>
  );
}

/* ---------------- Bienvenida (H1 CA2) ---------------- */

function Welcome({ status, screen, contextNote, onStarted, onOpen, onState }: {
  status: CompanionStatus; screen: CompanionScreen | null; contextNote: ReactNode;
  onStarted: (id: number) => void; onOpen: (id: number) => void; onState: (s: AvatarState, text: string) => void;
}) {
  const { me } = useAuth();
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const composer = useRef<ComposerHandle>(null);
  useEffect(() => { if (window.matchMedia?.('(pointer: fine)').matches) composer.current?.focus(); }, []);
  const name = me?.displayName?.split(/\s+/)[0] ?? '';

  async function submit() {
    const t = text.trim();
    if (!t || sending) return;
    setSending(true);
    setError(null);
    onState('thinking', AVATAR_TEXT.thinking);
    try {
      const c = await companionApi.start(t, screen);
      onStarted(c.id);
    } catch (err) {
      onState('error', AVATAR_TEXT.error);
      setError(problemText(err, 'No se pudo empezar la conversación. Revisa tu conexión.'));
      composer.current?.focus();
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="chat-pane">
      <div className="cmp-welcome">
        <div className="cmp-hello">
          <h2>{name ? `Hola, ${name}` : 'Hola'}</h2>
          <p>Pregúntame cómo se hace algo, por qué no puedes avanzar un registro o qué tienes pendiente. Leo con tus permisos y no cambio nada sin que lo confirmes.</p>
        </div>
        {status.activeId ? (
          <Alert tone="info" title="Tienes una conversación en curso" action={<Button size="sm" onClick={() => onOpen(status.activeId!)}>Verla</Button>}>
            Espera a que termine o detenla para empezar otra.
          </Alert>
        ) : null}
        {error && <Alert tone="danger" title="No se empezó la conversación">{error}</Alert>}
        <ul className="cmp-suggestions" aria-label="Sugerencias">
          {suggestions(screen).map((x) => (
            <li key={x}>
              <button type="button" className="chat-suggestion" onClick={() => { setText(x); composer.current?.focus(); }}>{x}</button>
            </li>
          ))}
        </ul>
      </div>
      <div className="chat-dock">
        <Composer
          ref={composer}
          label="Mensaje para el Acompañante"
          value={text}
          onChange={setText}
          onSubmit={() => void submit()}
          sending={sending}
          maxChars={status.limits.maxPromptChars}
          placeholder="Pregunta o pide algo…"
          footer={contextNote}
        />
      </div>
    </div>
  );
}

/* ---------------- Historial (H4) ---------------- */

function HistoryView({ current, onOpen, onDeleted, tick }: {
  current: number | null; onOpen: (id: number | null) => void; onDeleted: (id: number) => void; tick: number;
}) {
  const list = useAsync((s) => companionApi.conversations(s), [tick]);
  const [removing, setRemoving] = useState<{ id: number; title: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const now = useNow(30_000);
  const first = useRef<HTMLButtonElement>(null);
  useEffect(() => { first.current?.focus(); }, [list.data === null]);

  async function remove() {
    if (!removing) return;
    setBusy(true);
    setError(null);
    try {
      await companionApi.remove(removing.id);
      onDeleted(removing.id);
    } catch (e) {
      setError(e instanceof ApiError && e.status === 409
        ? 'Esa conversación tiene una respuesta en curso: detenla o espera a que termine para borrarla.'
        : problemText(e, 'No se pudo borrar la conversación.'));
    } finally {
      setBusy(false);
      setRemoving(null);
    }
  }

  return (
    <nav className="cmp-history" aria-label="Conversaciones con el Acompañante">
      <button ref={first} type="button" className="chat-bar-new" onClick={() => onOpen(null)}><SquarePen size={16} aria-hidden /> Nueva conversación</button>
      {error && <Alert tone="danger" title="No se borró">{error}</Alert>}
      <h3 className="cmp-history-title">Recientes</h3>
      {list.loading && !list.data ? <LoadingBlock />
        : list.error ? <Alert tone="danger" title="No se pudo cargar la lista">{problemText(list.error, 'Intenta de nuevo.')}</Alert>
          : list.data && list.data.length ? (
            <ul className="cmp-history-list">
              {list.data.map((c) => (
                <li key={c.id} className="cmp-history-item">
                  <button type="button" className="cmp-history-open" aria-current={c.id === current || undefined} title={c.title} onClick={() => onOpen(c.id)}>
                    <span className="cmp-history-name">{c.title}</span>
                    <span className="cmp-history-meta">
                      <time dateTime={c.updatedAt} title={new Date(c.updatedAt).toLocaleString('es-MX')}>{relativeTime(c.updatedAt, now)}</time>
                      {isActive(c.status) && <><span className="chat-dot" aria-hidden /> Trabajando</>}
                      {c.pendingActions > 0 && <span className="chat-bar-tag">{c.pendingActions === 1 ? '1 propuesta' : `${c.pendingActions} propuestas`}</span>}
                    </span>
                  </button>
                  <button type="button" className="chat-icon-btn" aria-label={`Borrar «${c.title}»`} title="Borrar" onClick={() => setRemoving({ id: c.id, title: c.title })}>
                    <Trash2 size={15} aria-hidden />
                  </button>
                </li>
              ))}
            </ul>
          ) : <p className="chat-bar-empty">Aún no tienes conversaciones.</p>}
      <ConfirmDialog
        open={removing !== null}
        danger
        title="Borrar la conversación"
        message={<>Se borra «{removing?.title}» con sus mensajes y propuestas. Los cambios que ya confirmaste se quedan, con su bitácora.</>}
        confirmLabel="Borrar"
        loading={busy}
        onConfirm={() => void remove()}
        onCancel={() => setRemoving(null)}
      />
    </nav>
  );
}
