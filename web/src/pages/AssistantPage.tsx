import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { AlertTriangle, Braces, MessageSquarePlus, MessagesSquare, Rocket, Send, Sparkles, Trash2, KeyRound, Save } from 'lucide-react';
import { ApiError } from '../api/client';
import { assistantApi } from '../api/endpoints';
import type { AssistantStatus, Conversation, Proposal, ProposalStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog, Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, Input, LoadingBlock, PageHeader, Spinner, Textarea } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';
import { PlanPreviewView } from './modeler/PlanReviewDialog';

const statusLabel: Record<ProposalStatus, string> = {
  proposed: 'Propuesta lista', invalid: 'Con errores', answer: 'Respuesta', failed: 'Sin respuesta',
  applied: 'Aplicada', discarded: 'Descartada', superseded: 'Reemplazada',
};
const statusTone: Record<ProposalStatus, 'accent' | 'danger' | 'neutral' | 'success' | 'warning'> = {
  proposed: 'accent', invalid: 'danger', answer: 'neutral', failed: 'warning', applied: 'success', discarded: 'neutral', superseded: 'neutral',
};

const EXAMPLES = [
  'Agrega un catálogo de Vehículos con placa única, marca, modelo y año, en un grupo «Flotilla».',
  'En Productos, agrega un campo «Peso (kg)» decimal y una regla que no permita pesos negativos.',
  'Crea un reporte de órdenes de compra por mes con el total en moneda base.',
];

function problemText(e: ApiError) {
  const items = Object.values(e.fieldErrors).flat();
  return items.length ? items.join(' ') : e.title;
}

/** Spec 011: el asistente propone Planes de Cambios; la persona los revisa con la misma vista previa del modelador y decide. */
export function AssistantPage() {
  const toast = useToast();
  const { reloadEntities } = useAuth();
  const [params, setParams] = useSearchParams();
  const conversationId = Number(params.get('c')) || undefined;
  const [tick, setTick] = useState(0);
  const status = useAsync((s) => assistantApi.status(s), [tick]);
  const conversations = useAsync((s) => assistantApi.conversations(s), [tick]);
  const current = useAsync<Conversation | null>(
    (s) => (conversationId ? assistantApi.conversation(conversationId, s) : Promise.resolve(null)),
    [conversationId, tick],
  );

  const [prompt, setPrompt] = useState('');
  const [pending, setPending] = useState<string | null>(null);
  const [sendError, setSendError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<Proposal | null>(null);
  const [busy, setBusy] = useState(false);
  const [keyDialog, setKeyDialog] = useState(false);
  const input = useRef<HTMLTextAreaElement>(null);
  const threadEnd = useRef<HTMLDivElement>(null);

  const st = status.data;
  const turns = current.data?.turns ?? [];
  const max = st?.maxPromptChars ?? 4000;
  const disabled = !st?.configured || st.remaining <= 0 || pending !== null;

  useEffect(() => { threadEnd.current?.scrollIntoView({ block: 'nearest' }); }, [turns.length, pending]);

  const openConversation = (id?: number) => {
    setSendError(null);
    setParams(id ? { c: String(id) } : {});
  };

  async function send(e?: FormEvent) {
    e?.preventDefault();
    const text = prompt.trim();
    if (!text || disabled) return;
    setPending(text);
    setSendError(null);
    try {
      const p = await assistantApi.request(text, conversationId);
      setPrompt('');
      if (p.conversationId !== conversationId) setParams({ c: String(p.conversationId) });
    } catch (err) {
      setSendError(err instanceof ApiError ? problemText(err) : 'No se pudo enviar la solicitud. Revisa tu conexión.');
    } finally {
      setPending(null);
      setTick((t) => t + 1);
      input.current?.focus();
    }
  }

  async function apply(p: Proposal) {
    setBusy(true);
    try {
      const r = await assistantApi.apply(p.id);
      setConfirm(null);
      toast.success('Cambios aplicados', `Conjunto de cambios #${r.changeSetId}`);
      await reloadEntities();
    } catch (err) {
      setConfirm(null);
      toast.fromError(err, 'No se pudo aplicar la propuesta.');
    } finally {
      setBusy(false);
      setTick((t) => t + 1);
    }
  }

  async function discard(p: Proposal) {
    try {
      await assistantApi.discard(p.id);
      toast.info('Propuesta descartada');
    } catch (err) {
      toast.fromError(err);
    } finally {
      setTick((t) => t + 1);
    }
  }

  if (status.loading && !st) return <LoadingBlock />;
  if (status.error) return <div className="page"><Alert tone="danger" title="No se pudo cargar el asistente">{status.error instanceof ApiError ? status.error.title : "Recarga la página e intenta de nuevo."}</Alert></div>;

  return (
    <div className="page">
      <PageHeader
        icon={Sparkles}
        title="Asistente IA"
        subtitle="Describe el cambio en tus palabras. El asistente propone un plan de cambios; tú lo revisas y decides si se aplica."
        actions={st?.configured && (
          <>
            {st.canConfigure && <Button icon={KeyRound} onClick={() => setKeyDialog(true)}>Clave de Anthropic</Button>}
            <Button icon={MessageSquarePlus} onClick={() => { openConversation(undefined); setPrompt(''); input.current?.focus(); }}>Nueva conversación</Button>
          </>
        )}
      />

      {!st?.configured ? (
        st?.canConfigure ? (
          <div className="card card-pad assistant-setup">
            <h2 className="section-title"><KeyRound size={15} aria-hidden /> Conecta el asistente con Anthropic</h2>
            <p className="muted" style={{ margin: 0 }}>
              El asistente usa Claude, de Anthropic. Crea una clave en console.anthropic.com (API Keys) y pégala aquí. Se prueba
              antes de guardarse, se guarda cifrada en el servidor y no se vuelve a mostrar.
            </p>
            <KeyForm onSaved={() => { toast.success('Clave guardada', 'El asistente ya está disponible.'); setTick((t) => t + 1); }} />
          </div>
        ) : (
          <div className="card">
            <EmptyState
              icon={KeyRound}
              title="El asistente no está configurado"
              description="Pide a un administrador que guarde la clave de Anthropic en Configuración › Asistente IA. El resto del ERP funciona igual."
            />
          </div>
        )
      ) : (
        <div className="assistant-layout">
          <nav className="card assistant-history" aria-label="Conversaciones">
            <h2 className="section-title"><MessagesSquare size={15} aria-hidden /> Conversaciones</h2>
            {conversations.data?.length ? (
              <ul>
                {conversations.data.map((c) => (
                  <li key={c.id}>
                    <button
                      type="button"
                      className="assistant-history-item"
                      aria-current={c.id === conversationId ? 'true' : undefined}
                      onClick={() => openConversation(c.id)}
                    >
                      <span className="assistant-history-title">{c.title}</span>
                      <span className="subtle">{formatDateTime(c.updatedAt)} · {statusLabel[c.lastStatus]}</span>
                    </button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="subtle">Aún no hay conversaciones.</p>
            )}
          </nav>

          <section className="assistant-main" aria-label="Conversación actual">
            {!!current.error && <Alert tone="danger" title="No se pudo abrir la conversación">{current.error instanceof ApiError ? current.error.title : "Recarga la página e intenta de nuevo."}</Alert>}
            {!conversationId && !pending && (
              <div className="card">
                <EmptyState
                  icon={Sparkles}
                  title="¿Qué necesitas en el modelo?"
                  description="Catálogos, campos, procesos, reglas, seguridad, layouts, reportes, tableros… Nada se aplica sin tu confirmación."
                  action={
                    <div className="assistant-examples">
                      {EXAMPLES.map((x) => (
                        <button key={x} type="button" className="assistant-example" onClick={() => { setPrompt(x); input.current?.focus(); }}>{x}</button>
                      ))}
                    </div>
                  }
                />
              </div>
            )}
            {current.loading && !current.data && conversationId && <LoadingBlock />}
            <ol className="assistant-thread">
              {turns.map((p) => (
                <Turn key={p.id} p={p} onApply={() => setConfirm(p)} onDiscard={() => void discard(p)} />
              ))}
              {pending && (
                <li className="assistant-turn">
                  <div className="assistant-prompt"><span className="sr-only">Tu solicitud: </span>{pending}</div>
                  <div className="card card-pad assistant-response" aria-live="polite">
                    <span className="row"><Spinner size={16} label="Preparando la propuesta" /> El asistente está preparando la propuesta…</span>
                  </div>
                </li>
              )}
            </ol>
            <div ref={threadEnd} />

            <form className="card card-pad assistant-composer" onSubmit={send}>
              {sendError && <Alert tone="danger" title="No se obtuvo una propuesta">{sendError}</Alert>}
              <Textarea
                ref={input}
                label={conversationId ? 'Ajusta la propuesta o pide otro cambio' : 'Describe el cambio'}
                value={prompt}
                maxLength={max}
                rows={3}
                disabled={!st.configured || pending !== null}
                onChange={(e) => setPrompt(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) void send(); }}
                hint={
                  <>
                    Se envía a Anthropic junto con la estructura del modelo (nunca tus registros) y queda en la auditoría: no escribas
                    datos personales ni contraseñas. {prompt.length}/{max} · Ctrl+Enter para enviar · te quedan {st.remaining} de {st.requestsPerHour} solicitudes esta hora.
                  </>
                }
              />
              <div className="row" style={{ justifyContent: 'flex-end' }}>
                <Button type="submit" variant="primary" icon={Send} loading={pending !== null} disabled={disabled || !prompt.trim()}>
                  Pedir propuesta
                </Button>
              </div>
            </form>
          </section>
        </div>
      )}

      {st?.canConfigure && st.configured && (
        <KeyDialog
          open={keyDialog}
          status={st}
          onClose={() => setKeyDialog(false)}
          onChanged={(msg) => { setKeyDialog(false); toast.success(msg); setTick((t) => t + 1); }}
        />
      )}

      <Dialog
        open={!!confirm}
        onClose={() => { if (!busy) setConfirm(null); }}
        title="Aplicar la propuesta"
        description={confirm?.plan?.description}
        footer={
          <>
            <Button onClick={() => setConfirm(null)} disabled={busy} data-autofocus>Cancelar</Button>
            <Button variant="primary" icon={Rocket} loading={busy} onClick={() => confirm && void apply(confirm)}>Aplicar cambios</Button>
          </>
        }
      >
        {confirm && (
          <div className="stack">
            <p className="muted" style={{ margin: 0 }}>
              Se aplicarán {confirm.plan?.operations.length ?? 0} operaciones al modelo en una sola transacción, con tus permisos y en la
              auditoría. Si algo ya no es válido, no se aplica nada.
            </p>
            {confirm.warnings.length > 0 && (
              <Alert tone="warning" icon={AlertTriangle} title="Revisa con atención">
                <ul className="import-errors">{confirm.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
              </Alert>
            )}
          </div>
        )}
      </Dialog>
    </div>
  );
}

function Turn({ p, onApply, onDiscard }: { p: Proposal; onApply: () => void; onDiscard: () => void }) {
  const live = p.status === 'proposed' || p.status === 'invalid';
  return (
    <li className="assistant-turn">
      <div className="assistant-prompt"><span className="sr-only">Tu solicitud: </span>{p.prompt}</div>
      <article className="card card-pad assistant-response stack" aria-label={`Respuesta del asistente: ${statusLabel[p.status]}`}>
        <div className="row" style={{ flexWrap: 'wrap' }}>
          <Sparkles size={15} aria-hidden />
          <strong>Asistente</strong>
          <Badge tone={statusTone[p.status]}>{statusLabel[p.status]}</Badge>
          {p.changeSetId && <Badge tone="success">Conjunto de cambios #{p.changeSetId}</Badge>}
          <span className="subtle grow" style={{ textAlign: 'right' }}>{formatDateTime(p.createdAt)}</span>
        </div>
        {p.explanation && <p className="assistant-explanation">{p.explanation}</p>}
        {p.error && <Alert tone="warning" title="El asistente no respondió">{p.error}</Alert>}
        {p.errors.length > 0 && (
          <Alert tone="danger" title="La respuesta no es un plan de cambios válido">
            <ul className="import-errors">{p.errors.map((m, i) => <li key={i}>{m}</li>)}</ul>
          </Alert>
        )}
        {p.warnings.length > 0 && live && (
          <Alert tone="warning" icon={AlertTriangle} title="Revisa con atención">
            <ul className="import-errors">{p.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
          </Alert>
        )}
        {p.preview && live && (
          <PlanPreviewView
            preview={p.preview}
            validText="Si lo apruebas, se aplica en una sola transacción. Si un paso falla, no se aplica ninguno."
          />
        )}
        {p.status === 'invalid' && p.attempts > 1 && (
          <p className="subtle" style={{ margin: 0 }}>El asistente intentó corregirlo {p.attempts - 1} {p.attempts === 2 ? 'vez' : 'veces'}. Pide un ajuste o simplifica la solicitud.</p>
        )}
        {p.plan && (
          <details className="assistant-json">
            <summary><Braces size={14} aria-hidden /> Ver plan en JSON ({p.plan.operations.length} operaciones)</summary>
            <pre className="code-block" tabIndex={0} aria-label="Plan de cambios en JSON">{JSON.stringify(p.plan, null, 2)}</pre>
          </details>
        )}
        {live && (
          <div className="row" style={{ justifyContent: 'flex-end', flexWrap: 'wrap' }}>
            <Button icon={Trash2} onClick={onDiscard}>Descartar</Button>
            {p.status === 'proposed' && (
              <Button variant="primary" icon={Rocket} disabled={!p.canApply} onClick={onApply}>Aplicar…</Button>
            )}
          </div>
        )}
      </article>
    </li>
  );
}

/** Captura de la clave: campo de contraseña, nunca se rellena con la guardada. */
function KeyForm({ onSaved, label = 'Clave de API de Anthropic' }: { onSaved: () => void; label?: string }) {
  const [key, setKey] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await assistantApi.setKey(key.trim());
      setKey('');
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? problemText(err) : 'No se pudo guardar la clave. Revisa tu conexión.');
    } finally {
      setSaving(false);
    }
  }
  return (
    <form className="assistant-key-form" onSubmit={save}>
      <Input
        label={label}
        type="password"
        autoComplete="off"
        spellCheck={false}
        placeholder="sk-ant-…"
        value={key}
        error={error ?? undefined}
        onChange={(e) => setKey(e.target.value)}
      />
      <Button type="submit" variant="primary" icon={Save} loading={saving} disabled={!key.trim()}>Guardar y probar</Button>
    </form>
  );
}

function KeyDialog({ open, status, onClose, onChanged }: { open: boolean; status: AssistantStatus; onClose: () => void; onChanged: (msg: string) => void }) {
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearing, setClearing] = useState(false);
  const toast = useToast();
  async function clear() {
    setClearing(true);
    try {
      await assistantApi.clearKey();
      setConfirmClear(false);
      onChanged('Clave quitada: el asistente quedó desactivado.');
    } catch (err) {
      toast.fromError(err);
    } finally {
      setClearing(false);
    }
  }
  return (
    <>
      <Dialog
        open={open && !confirmClear}
        onClose={onClose}
        title="Clave de Anthropic"
        description={status.keyUpdatedAt ? `Guardada por ${status.keyUpdatedBy} el ${formatDateTime(status.keyUpdatedAt)}.` : undefined}
        footer={
          <>
            <Button variant="danger" icon={Trash2} onClick={() => setConfirmClear(true)}>Quitar clave</Button>
            <Button onClick={onClose} data-autofocus>Cerrar</Button>
          </>
        }
      >
        <div className="stack">
          <p className="muted" style={{ margin: 0 }}>Por seguridad la clave guardada no se muestra. Para cambiarla, pega una nueva: se prueba antes de reemplazar la actual.</p>
          <KeyForm label="Nueva clave de API" onSaved={() => onChanged('Clave reemplazada')} />
        </div>
      </Dialog>
      <ConfirmDialog
        open={confirmClear}
        danger
        title="Quitar la clave de Anthropic"
        message="El asistente dejará de estar disponible para todos hasta que se guarde otra clave. Las conversaciones se conservan."
        confirmLabel="Quitar clave"
        loading={clearing}
        onConfirm={() => void clear()}
        onCancel={() => setConfirmClear(false)}
      />
    </>
  );
}
