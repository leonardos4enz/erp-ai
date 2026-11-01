import { useState } from 'react';
import { Copy, FlaskConical, Plug, Trash2, Unplug } from 'lucide-react';
import { ApiError } from '../api/client';
import { mcpApi } from '../api/endpoints';
import type { McpConnection } from '../api/types';
import { ConfirmDialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';

const SCOPE_LABEL: Record<string, string> = { modelo: 'Modelo', datos: 'Datos', aislado: 'Copia aislada', proponer: 'Proponer' };

/**
 * Spec 038 (H6): la persona ve qué aplicaciones (su Claude) tienen acceso a su cuenta por MCP y las desconecta. También da la
 * URL del servidor para agregarlo como conector.
 */
export function ConnectionsPage() {
  const toast = useToast();
  const state = useAsync((signal) => mcpApi.connections(signal), []);
  const [confirm, setConfirm] = useState<McpConnection | null>(null);
  // Spec 044 (H6 CA2): descartar la copia aislada de una conexión.
  const [discard, setDiscard] = useState<McpConnection | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function copy(value: string) {
    try {
      await navigator.clipboard.writeText(value);
      toast.success('URL copiada');
    } catch {
      toast.error('No se pudo copiar; selecciónala y cópiala a mano.');
    }
  }

  async function disconnect() {
    if (!confirm) return;
    setBusy(true);
    setError(null);
    try {
      await mcpApi.disconnect(confirm.id);
      toast.success(`${confirm.client} se desconectó`);
      setConfirm(null);
      state.reload();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'No se pudo desconectar.');
      setConfirm(null);
      state.reload();
    } finally {
      setBusy(false);
    }
  }

  async function discardSandbox() {
    if (!discard) return;
    setBusy(true);
    setError(null);
    try {
      await mcpApi.discardSandbox(discard.id);
      toast.success(`Se descartó la copia aislada de ${discard.client}`);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'No se pudo descartar la copia.');
    } finally {
      setDiscard(null);
      setBusy(false);
      state.reload();
    }
  }

  const data = state.data;
  return (
    <div className="page">
      <PageHeader icon={Plug} title="Conexiones de Claude" subtitle="Aplicaciones con MCP que usan el ERP con tu cuenta y tus permisos: leen, prueban en una copia aislada y te proponen cambios." />
      {state.loading && !data && <LoadingBlock />}
      {state.error != null && (
        <Alert tone="danger" title="No se pudieron cargar tus conexiones">
          {state.error instanceof ApiError ? state.error.message : 'Inténtalo de nuevo.'}
        </Alert>
      )}
      {error && <Alert tone="danger" title="No se pudo completar">{error}</Alert>}
      {data && (
        <>
          <section className="card card-pad connections-setup" aria-labelledby="mcp-setup">
            <h2 id="mcp-setup" className="section-title">Conectar Claude</h2>
            <ol className="connections-steps">
              <li>En Claude, abre <em>Configuración › Conectores</em> y agrega un conector personalizado.</li>
              <li>Pega la URL del servidor MCP de este ERP:</li>
            </ol>
            <div className="secret-box">
              <code aria-label="URL del servidor MCP">{data.serverUrl}</code>
              <Button icon={Copy} onClick={() => copy(data.serverUrl)}>Copiar</Button>
            </div>
            <p className="subtle">
              Claude te traerá aquí para que autorices. Podrá hacer, como mucho, lo que tú puedes (sin escribir registros reales ni cambiar
              el modelo) y verás la conexión abajo.
            </p>
          </section>

          <section aria-labelledby="mcp-list">
            <h2 id="mcp-list" className="section-title">Tus conexiones</h2>
            {data.connections.length === 0 ? (
              <EmptyState icon={Plug} title="Sin conexiones" description="Cuando autorices a una aplicación, aparecerá aquí." />
            ) : (
              <ul className="connections-list">
                {data.connections.map((c) => (
                  <li key={c.id} className="card connection" aria-label={c.client}>
                    <div className="connection-main">
                      <div className="connection-title">
                        <strong>{c.client}</strong>
                        <span className="connection-host">{c.redirectHost}</span>
                        {c.scopes.map((s) => <Badge key={s} tone={s === 'datos' ? 'warning' : 'neutral'}>{SCOPE_LABEL[s] ?? s}</Badge>)}
                      </div>
                      <div className="subtle connection-dates">
                        Conectada el {formatDateTime(c.createdAt)} · {c.lastUsedAt ? `último uso el ${formatDateTime(c.lastUsedAt)}` : 'sin uso todavía'}
                      </div>
                      {c.sandbox && (
                        <div className="subtle connection-dates">
                          <FlaskConical size={13} aria-hidden /> {c.sandbox.status === 'ready'
                            ? `Copia aislada desde el ${formatDateTime(c.sandbox.createdAt)} · último uso el ${formatDateTime(c.sandbox.lastUsedAt)}`
                            : 'Copia aislada preparándose'}
                        </div>
                      )}
                    </div>
                    <div className="connection-actions">
                      {c.sandbox && (
                        <Button size="sm" icon={Trash2} onClick={() => setDiscard(c)} aria-label={`Descartar la copia aislada de ${c.client}`}>Descartar copia</Button>
                      )}
                      <Button size="sm" icon={Unplug} onClick={() => setConfirm(c)} aria-label={`Desconectar ${c.client}`}>Desconectar</Button>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
      <ConfirmDialog
        open={!!confirm}
        danger
        title="Desconectar aplicación"
        message={confirm && <>¿Desconectar <strong>{confirm.client}</strong> ({confirm.redirectHost})? Su acceso deja de servir de inmediato; para volver a usarla tendrás que autorizarla otra vez.</>}
        confirmLabel="Desconectar"
        loading={busy}
        onConfirm={disconnect}
        onCancel={() => setConfirm(null)}
      />
      <ConfirmDialog
        open={!!discard}
        danger
        title="Descartar copia aislada"
        message={discard && <>¿Descartar la copia aislada de <strong>{discard.client}</strong>? Se borran sus pruebas; la conexión sigue y la próxima prueba prepara otra copia.</>}
        confirmLabel="Descartar copia"
        loading={busy}
        onConfirm={discardSandbox}
        onCancel={() => setDiscard(null)}
      />
    </div>
  );
}
