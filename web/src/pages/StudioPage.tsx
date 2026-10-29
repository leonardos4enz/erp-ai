import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Bot, KeyRound, MessagesSquare, Plus, ShieldAlert, X } from 'lucide-react';
import { studioApi } from '../api/endpoints';
import type { StudioScope, StudioStatusInfo, StudioSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Alert, Button, EmptyState, LoadingBlock, Switch } from '../components/ui';
import { useAsync } from '../lib/hooks';
import { isActive } from '../lib/studio';
import { ChatView, problemText } from './studio/ChatView';
import { Composer, type ComposerHandle } from './studio/Composer';

/** H1 CA2: pedidos de ejemplo de ERP (rellenan el compositor; no se envían solos). */
const SUGGESTIONS = [
  'Agrega un campo «Peso (kg)» a Productos con una regla que no permita pesos negativos, y pruébala.',
  'Explícame cómo se calcula el costo promedio en Inventario.',
  'Revisa qué catálogos no tienen ninguna regla de validación.',
  'Propón una vista de órdenes de compra pendientes de recibir.',
];

/**
 * Spec 037: ERP AI Studio como chat. Barra de conversaciones a la izquierda (panel en móvil), bienvenida con los alcances
 * y la conversación con su flujo en vivo. Reemplaza la pantalla de un pedido de la 035.
 */
export function StudioPage() {
  const [params, setParams] = useSearchParams();
  const sessionId = Number(params.get('s')) || undefined;
  const [tick, setTick] = useState(0);
  const status = useAsync((s) => studioApi.status(s), [tick]);
  const sessions = useAsync((s) => studioApi.sessions(s), [tick]);
  // Herramientas del alcance «datos»: su salida se borra al terminar cada turno (R-033-4).
  const tools = useAsync((s) => studioApi.tools(s), []);
  const dataTools = new Set((tools.data ?? []).filter((t) => t.scope === 'datos').map((t) => t.name));
  const st = status.data;
  const opened = useRef(false);
  const [barOpen, setBarOpen] = useState(false);
  const opener = useRef<HTMLElement | null>(null);

  // H1 CA3: con una conversación trabajando, la pantalla abre esa (una vez, al entrar).
  useEffect(() => {
    if (opened.current || !st) return;
    opened.current = true;
    if (!sessionId && st.active) setParams({ s: String(st.active) }, { replace: true });
  }, [st, sessionId, setParams]);

  const open = (id?: number) => { setParams(id ? { s: String(id) } : {}); closeBar(); };
  const refresh = () => setTick((t) => t + 1);
  const openBar = () => { opener.current = document.activeElement as HTMLElement | null; setBarOpen(true); };
  function closeBar() {
    setBarOpen((was) => {
      if (was) window.setTimeout(() => opener.current?.focus(), 0);
      return false;
    });
  }

  if (status.loading && !st) return <LoadingBlock />;
  if (status.error || !st) {
    return (
      <div className="page">
        <Alert tone="danger" title="No se pudo cargar ERP AI Studio">{problemText(status.error, 'Recarga la página e intenta de nuevo.')}</Alert>
      </div>
    );
  }
  if (!st.configured) {
    return (
      <div className="page">
        <h1 className="page-title" style={{ marginBottom: 'var(--space-6)' }}>ERP AI Studio</h1>
        <div className="card">
          <EmptyState
            icon={KeyRound}
            title="ERP AI Studio no está configurado"
            description={<>Usa la clave de Anthropic del asistente. Un administrador la guarda en <Link to="/admin/assistant">Configuración › Asistente IA</Link>; el resto del ERP funciona igual.</>}
          />
        </div>
      </div>
    );
  }

  return (
    <div className={`chat-page${barOpen ? ' bar-open' : ''}`}>
      <ConversationBar items={sessions.data ?? null} current={sessionId} onOpen={open} onClose={closeBar} open={barOpen} />
      {barOpen && <div className="chat-bar-backdrop" aria-hidden onClick={closeBar} />}
      {sessionId ? (
        <ChatView key={sessionId} id={sessionId} status={st} dataTools={dataTools} onListChanged={refresh} onOpenBar={openBar} />
      ) : (
        <Welcome status={st} onStarted={(id) => { refresh(); open(id); }} onOpen={open} onOpenBar={openBar} />
      )}
    </div>
  );
}

/* ---------------- Barra de conversaciones ---------------- */

function ConversationBar({ items, current, open, onOpen, onClose }: {
  items: StudioSummary[] | null; current?: number; open: boolean; onOpen: (id?: number) => void; onClose: () => void;
}) {
  const ref = useRef<HTMLElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  // En móvil (panel): al abrir, el foco entra al panel; Esc lo cierra (y el foco vuelve a quien lo abrió).
  useEffect(() => {
    if (!open) return;
    ref.current?.querySelector<HTMLElement>('button')?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') close.current(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open]);
  return (
    <nav ref={ref} className="chat-bar" aria-label="Conversaciones">
      <div className="chat-bar-head">
        <Bot size={17} aria-hidden />
        <span className="grow">Studio</span>
        <button type="button" className="chat-icon-btn chat-bar-close" aria-label="Cerrar conversaciones" onClick={onClose}><X size={17} aria-hidden /></button>
      </div>
      <button type="button" className="chat-bar-new" aria-current={current ? undefined : 'page'} onClick={() => onOpen(undefined)}>
        <Plus size={16} aria-hidden /> Nueva conversación
      </button>
      <h2 className="chat-bar-title">Recientes</h2>
      {items === null ? null : items.length ? (
        <ul className="chat-bar-list">
          {items.map((x) => (
            <li key={x.id}>
              <button type="button" className="chat-bar-item" aria-current={x.id === current ? 'page' : undefined} title={x.title} onClick={() => onOpen(x.id)}>
                <span className="chat-bar-item-title">{x.title}</span>
                {isActive(x.status) && <span className="chat-dot" role="img" aria-label="Trabajando" />}
                {x.status === 'proposed' && <span className="chat-bar-tag">Propuesta</span>}
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="chat-bar-empty">Aún no hay conversaciones.</p>
      )}
    </nav>
  );
}

/* ---------------- Bienvenida ---------------- */

function Welcome({ status, onStarted, onOpen, onOpenBar }: {
  status: StudioStatusInfo; onStarted: (id: number) => void; onOpen: (id: number) => void; onOpenBar: () => void;
}) {
  const { me } = useAuth();
  const [prompt, setPrompt] = useState('');
  // H1 CA2: «datos» siempre empieza apagado; «aislado», encendido si el servidor lo permite.
  const [datos, setDatos] = useState(false);
  const [aislado, setAislado] = useState(status.sandboxAvailable);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const composer = useRef<ComposerHandle>(null);
  useEffect(() => { composer.current?.focus(); }, []);
  const name = me?.displayName?.split(/\s+/)[0] ?? '';

  async function submit() {
    const text = prompt.trim();
    if (!text || sending) return;
    setSending(true);
    setError(null);
    try {
      const scopes: StudioScope[] = [...(datos ? ['datos' as const] : []), ...(aislado && status.sandboxAvailable ? ['aislado' as const] : [])];
      const s = await studioApi.start(text, scopes);
      onStarted(s.id);
    } catch (err) {
      setError(problemText(err, 'No se pudo iniciar la conversación. Revisa tu conexión.'));
      composer.current?.focus();
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="chat-pane">
      <header className="chat-header">
        <button type="button" className="chat-icon-btn chat-bar-open" aria-label="Conversaciones" onClick={onOpenBar}><MessagesSquare size={17} aria-hidden /></button>
        <h1 className="chat-brand">ERP AI Studio</h1>
      </header>
      <div className="chat-welcome-scroll">
        <div className="chat-welcome">
          <h2 className="chat-greeting"><Bot size={26} aria-hidden /> {name ? `Hola, ${name}` : 'Hola'}</h2>
          <p className="chat-greeting-sub">¿Qué desarrollo necesitas? El agente investiga el modelo con tus permisos, lo prueba en una copia y te entrega una propuesta. Nada se aplica sin ti.</p>
          {status.active !== null && (
            <Alert tone="info" title="Ya tienes una conversación en curso" action={<Button size="sm" onClick={() => onOpen(status.active!)}>Ver la conversación</Button>}>
              Espera a que termine o detenla para empezar otra.
            </Alert>
          )}
          {error && <Alert tone="danger" title="No se inició la conversación">{error}</Alert>}
          <Composer
            ref={composer}
            welcome
            value={prompt}
            onChange={setPrompt}
            onSubmit={() => void submit()}
            sending={sending}
            maxChars={status.limits.maxPromptChars}
            placeholder="Por ejemplo: en Productos, agrega «Peso (kg)» con una regla que no permita pesos negativos, y pruébala."
            footer={
              <fieldset className="chat-scopes">
                <legend>Qué puede usar el agente en esta conversación</legend>
                <div className="chat-scope">
                  <Switch checked disabled onChange={() => undefined} label="Modelo del ERP" />
                  <p>Siempre: catálogos, campos, procesos, reglas, rutas y riesgos, con tus permisos. Sin registros.</p>
                </div>
                <div className="chat-scope">
                  <Switch checked={datos} onChange={setDatos} label="Leer registros (datos)" />
                  <p>Apagado por omisión. Lee solo lo que tú puedes ver, 20 registros por consulta.</p>
                </div>
                {datos && (
                  <Alert tone="warning" icon={ShieldAlert} title="Los registros que el agente lea viajan a Anthropic">
                    Sus valores se envían al proveedor del modelo como parte de la conversación. Lo que devuelven las herramientas de datos se borra al terminar cada turno; lo que el agente escriba o copie se conserva.
                  </Alert>
                )}
                <div className="chat-scope">
                  <Switch checked={aislado && status.sandboxAvailable} disabled={!status.sandboxAvailable} onChange={setAislado} label="Probar en una copia aislada" />
                  <p>
                    {status.sandboxAvailable
                      ? 'Una base aparte con el modelo y sin datos reales: el agente aplica su plan ahí y crea registros de prueba.'
                      : 'No disponible en este servidor (Studio:Sandbox o permiso para crear bases): el agente entregará sin probar en una copia.'}
                  </p>
                </div>
                <p className="chat-composer-note">
                  Se envía a Anthropic con la estructura del modelo{status.model ? ` (${status.model})` : ''}. No escribas contraseñas ni datos personales.
                  Hoy: {status.sessionsToday} de {status.limits.sessionsPerDay} conversaciones.
                </p>
              </fieldset>
            }
          />
          <ul className="chat-suggestions" aria-label="Sugerencias">
            {SUGGESTIONS.map((x) => (
              <li key={x}>
                <button type="button" className="chat-suggestion" onClick={() => { setPrompt(x); composer.current?.focus(); }}>{x}</button>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}
