import { useEffect, useState } from 'react';
import { Copy, Fingerprint, KeyRound, Pencil, Plus, RefreshCw, RotateCcw, Send, Settings2, ShieldOff, Webhook } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { adminApi, integrationsApi } from '../api/endpoints';
import { isPending, type ApiKey, type DeliveryStatus, type OAuthClient, type OAuthScope, type Role, type WebhookEvent, type WebhookSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog, Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, Checkbox, EmptyState, Input, LoadingBlock, PageHeader, Select, Switch, Tabs } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';

type Tab = 'webhooks' | 'keys' | 'clients';

const deliveryTone: Record<DeliveryStatus, 'success' | 'warning' | 'danger' | 'neutral'> = {
  delivered: 'success', pending: 'warning', failed: 'danger', canceled: 'neutral',
};
const deliveryLabel: Record<DeliveryStatus, string> = {
  delivered: 'Entregada', pending: 'Pendiente', failed: 'Fallida', canceled: 'Cancelada',
};

/** Un secreto o clave se muestra una sola vez: el servidor solo guarda su hash (claves) o lo cifra (webhooks). */
function CopyBox({ value, label }: { value: string; label: string }) {
  const toast = useToast();
  return (
    <div className="secret-box">
      <code aria-label={label}>{value}</code>
      <Button
        icon={Copy}
        aria-label={label === 'client_id' ? 'Copiar client_id' : undefined}
        onClick={async () => {
          try { await navigator.clipboard.writeText(value); toast.success('Copiado'); } catch { toast.error('No se pudo copiar; selecciónalo y cópialo a mano.'); }
        }}
      >
        Copiar
      </Button>
    </div>
  );
}

/** Spec 018: un cliente OAuth muestra también su client_id (no es secreto, pero va junto al secreto). */
function SecretDialog({ secret, title, onClose, pending, clientId }: { secret: string | null; title: string; onClose: () => void; pending?: boolean; clientId?: string }) {
  return (
    <Dialog
      open={!!secret}
      onClose={onClose}
      title={title}
      description="Cópialo ahora y guárdalo en el sistema que lo usará: no se volverá a mostrar."
      footer={<Button variant="primary" onClick={onClose}>Ya lo guardé</Button>}
    >
      <div className="stack">
        {pending && (
          <Alert tone="warning" title="Pendiente de aprobación">
            {clientId
              ? 'Este entorno exige que otro administrador apruebe los clientes nuevos (Seguridad › Solicitudes). El cliente obtendrá tokens cuando lo apruebe.'
              : 'Este entorno exige que otro administrador apruebe las claves nuevas (Seguridad › Solicitudes). La clave funcionará cuando la apruebe.'}
          </Alert>
        )}
        {clientId && (
          <div className="field">
            <span className="field-label">client_id</span>
            <CopyBox value={clientId} label="client_id" />
          </div>
        )}
        <div className="field">
          {clientId && <span className="field-label">client_secret</span>}
          <CopyBox value={secret ?? ''} label="Valor secreto" />
        </div>
      </div>
    </Dialog>
  );
}

/* ----------------------------- Webhooks ----------------------------- */

function eventText(ev: WebhookEvent, labelOf: (kind: 'state' | 'transition', name: string) => string) {
  switch (ev.on) {
    case 'create': return 'Al crear';
    case 'update': return 'Al editar';
    case 'enterState': return `Al entrar a «${labelOf('state', ev.state ?? '')}»`;
    case 'transition': return `Al ejecutar «${labelOf('transition', ev.transition ?? '')}»`;
  }
}

function ConfigureDialog({ hook, onClose, onSaved }: { hook: WebhookSummary | null; onClose: () => void; onSaved: (secret?: string | null) => void }) {
  const toast = useToast();
  const [url, setUrl] = useState('');
  const [active, setActive] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);

  useEffect(() => {
    if (!hook) return;
    setUrl('');
    setActive(hook.configured ? hook.active : true);
    setError(null);
  }, [hook]);

  async function save() {
    if (!hook) return;
    setSaving(true);
    setError(null);
    try {
      const r = await integrationsApi.configure(hook.entity, hook.name, { url: url.trim() || undefined, active });
      toast.success('Destino guardado');
      onSaved(r.secret);
    } catch (e) {
      if (e instanceof ApiError) setError(e); else toast.fromError(e);
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      <Dialog
        open={!!hook && !confirmRemove}
        onClose={onClose}
        title={`Destino de «${hook?.label}»`}
        description="La URL y el secreto son de este entorno: no forman parte del modelo ni de los módulos, y se guardan cifrados."
        footer={
          <>
            {hook?.configured && <Button variant="danger" onClick={() => setConfirmRemove(true)} style={{ marginRight: 'auto' }}>Quitar destino</Button>}
            <Button onClick={onClose} disabled={saving}>Cancelar</Button>
            <Button variant="primary" loading={saving} onClick={() => void save()}>Guardar</Button>
          </>
        }
      >
        <div className="stack">
          {error && Object.keys(error.fieldErrors).length === 0 && <Alert tone="danger" title={error.title} />}
          <Input
            label="URL de destino"
            type="url"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            placeholder="https://"
            required={!hook?.configured}
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            error={fieldError(error, 'url')}
            hint={hook?.configured ? `Destino actual: ${hook.destination}. Déjalo vacío para conservarlo.` : 'Solo https y direcciones públicas. Se envía un POST firmado con HMAC-SHA256.'}
            maxLength={2000}
            data-autofocus
          />
          <Switch checked={active} onChange={setActive} label="Activo: enviar los eventos a este destino" />
        </div>
      </Dialog>
      <ConfirmDialog
        open={confirmRemove}
        danger
        title="Quitar destino"
        message={<>«{hook?.label}» dejará de enviarse y su secreto se borrará. Las entregas pendientes se cancelan.</>}
        confirmLabel="Quitar destino"
        onCancel={() => setConfirmRemove(false)}
        onConfirm={async () => {
          if (!hook) return;
          try {
            await integrationsApi.removeDestination(hook.entity, hook.name);
            toast.success('Destino quitado');
            setConfirmRemove(false);
            onSaved(null);
          } catch (e) { toast.fromError(e); setConfirmRemove(false); }
        }}
      />
    </>
  );
}

function WebhooksTab() {
  const toast = useToast();
  const { entities } = useAuth();
  const [tick, setTick] = useState(0);
  const { data: hooks, loading, error } = useAsync((s) => integrationsApi.webhooks(s), [tick]);
  const { data: deliveries } = useAsync((s) => integrationsApi.deliveries({}, s), [tick]);
  const [editing, setEditing] = useState<WebhookSummary | null>(null);
  const [rotating, setRotating] = useState<WebhookSummary | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const labelOf = (entity: string) => (kind: 'state' | 'transition', name: string) => {
    const wf = entities.find((e) => e.name === entity)?.workflow;
    const found = kind === 'state' ? wf?.states.find((s) => s.name === name) : wf?.transitions.find((t) => t.name === name);
    return found?.label ?? name;
  };
  const hookLabel = (entity: string, name: string) => hooks?.find((h) => h.entity === entity && h.name === name)?.label ?? name;

  async function test(h: WebhookSummary) {
    setBusy(`${h.entity}.${h.name}`);
    try {
      const r = await integrationsApi.test(h.entity, h.name);
      if (r.ok) toast.success(`Prueba entregada (respuesta ${r.status})`);
      else toast.error(`La prueba falló: ${r.error ?? 'sin respuesta'}`);
      setTick((t) => t + 1);
    } catch (e) { toast.fromError(e); } finally { setBusy(null); }
  }

  if (loading && !hooks) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los webhooks'} />;

  return (
    <div className="stack">
      {!hooks?.length ? (
        <div className="table-wrap">
          <EmptyState icon={Webhook} title="Sin webhooks declarados"
            description="Un webhook se declara en el modelador (Webhooks en JSON) o lo trae un módulo; aquí se elige a dónde se envía." />
        </div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Webhook</th><th>Eventos</th><th>Rol de servicio</th><th>Destino</th><th>Último envío</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {hooks.map((h) => (
                  <tr key={`${h.entity}.${h.name}`}>
                    <td><div style={{ fontWeight: 500 }}>{h.label}</div><div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{h.entityLabel}</div></td>
                    <td>{h.events.map((ev) => eventText(ev, labelOf(h.entity))).join(' · ')}</td>
                    <td>{h.role}</td>
                    <td>
                      {!h.configured ? <Badge>Sin configurar</Badge> : (
                        <div className="stack" style={{ gap: 4 }}>
                          <span className="mono" style={{ fontSize: 'var(--text-sm)', overflowWrap: 'anywhere' }}>{h.destination}</span>
                          <span><Badge tone={h.active ? 'success' : 'neutral'}>{h.active ? 'Activo' : 'Inactivo'}</Badge></span>
                        </div>
                      )}
                    </td>
                    <td>{h.lastStatus ? <><Badge tone={deliveryTone[h.lastStatus]}>{deliveryLabel[h.lastStatus]}</Badge><div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{formatDateTime(h.lastAt ?? '')}</div></> : '—'}</td>
                    <td>
                      <div className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                        <Button size="sm" icon={Settings2} onClick={() => setEditing(h)} aria-label={`Configurar ${h.label}`}>Configurar</Button>
                        {h.configured && <Button size="sm" icon={Send} loading={busy === `${h.entity}.${h.name}`} onClick={() => void test(h)} aria-label={`Probar ${h.label}`}>Probar</Button>}
                        {h.configured && <Button size="sm" variant="ghost" icon={RotateCcw} onClick={() => setRotating(h)} aria-label={`Rotar secreto de ${h.label}`}>Rotar secreto</Button>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
        <h2 className="section-title" style={{ fontSize: 'var(--text-md, 1rem)', margin: 0 }}>Entregas recientes</h2>
        <Button size="sm" variant="ghost" icon={RefreshCw} onClick={() => setTick((t) => t + 1)}>Actualizar</Button>
      </div>
      <div className="table-wrap">
        {!deliveries?.length ? (
          <EmptyState icon={Send} title="Sin entregas todavía" description="Cada evento enviado (o por enviar) aparece aquí con su estado, intentos y respuesta." />
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Fecha</th><th>Webhook</th><th>Evento</th><th>Registro</th><th>Estado</th><th>Intentos</th><th>Respuesta</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {deliveries.map((d) => (
                  <tr key={d.id}>
                    <td style={{ whiteSpace: 'nowrap' }}>{formatDateTime(d.createdAt)}</td>
                    <td>{hookLabel(d.entity, d.webhook)}</td>
                    <td>{d.event === 'ping' ? 'Prueba' : d.eventKey ? `${d.event} · ${d.eventKey}` : d.event}</td>
                    <td className="mono subtle">{d.recordId ?? '—'}</td>
                    <td><Badge tone={deliveryTone[d.status]}>{deliveryLabel[d.status]}</Badge></td>
                    <td className="mono">{d.attempts}</td>
                    <td>{d.lastStatusCode ? <span className="mono">{d.lastStatusCode}</span> : null}{d.lastError && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{d.lastError}</div>}{!d.lastStatusCode && !d.lastError && '—'}</td>
                    <td>
                      {(d.status === 'failed' || d.status === 'canceled') && d.event !== 'ping' && (
                        <Button size="sm" variant="ghost" icon={RotateCcw} aria-label={`Reintentar entrega ${d.id}`}
                          onClick={async () => { try { await integrationsApi.retry(d.id); toast.success('Entrega en cola'); setTick((t) => t + 1); } catch (e) { toast.fromError(e); } }}>
                          Reintentar
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <ConfigureDialog hook={editing} onClose={() => setEditing(null)} onSaved={(s) => { setEditing(null); setTick((t) => t + 1); if (s) setSecret(s); }} />
      <ConfirmDialog
        open={!!rotating}
        title="Rotar secreto"
        message={<>Se genera un secreto nuevo para «{rotating?.label}» y el anterior deja de firmar de inmediato. Actualízalo en el sistema que recibe.</>}
        confirmLabel="Rotar secreto"
        onCancel={() => setRotating(null)}
        onConfirm={async () => {
          if (!rotating) return;
          try { const r = await integrationsApi.rotate(rotating.entity, rotating.name); setSecret(r.secret); setTick((t) => t + 1); }
          catch (e) { toast.fromError(e); }
          finally { setRotating(null); }
        }}
      />
      <SecretDialog secret={secret} title="Secreto del webhook" onClose={() => setSecret(null)} />
    </div>
  );
}

/* ----------------------------- Claves de API ----------------------------- */

const isAdminRole = (r: Role) => r.isSuperuser || r.canManageModel || r.canManageUsers;

function KeyDialog({ open, apiKey, roles, onClose, onSaved }: {
  open: boolean; apiKey: ApiKey | null; roles: Role[]; onClose: () => void; onSaved: (secret?: string, pending?: boolean) => void;
}) {
  const toast = useToast();
  const isNew = !apiKey;
  const [name, setName] = useState('');
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [expires, setExpires] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    if (!open) return;
    setName(apiKey?.name ?? '');
    setRoleIds(apiKey?.roleIds ?? []);
    setExpires('');
    setError(null);
  }, [open, apiKey]);

  async function save() {
    setSaving(true);
    setError(null);
    try {
      if (isNew) {
        const r = await integrationsApi.createKey({ name: name.trim(), roleIds, expiresInDays: expires ? Number(expires) : undefined });
        onSaved(r.secret, isPending(r));
      } else {
        const r = await integrationsApi.updateKey(apiKey.id, { name: name.trim(), roleIds });
        // Spec 015: agregar roles en un entorno con aprobación de seguridad queda como solicitud.
        if (isPending(r)) toast.success('Solicitud enviada: otro administrador debe aprobar los roles nuevos');
        else toast.success('Clave actualizada');
        onSaved();
      }
    } catch (e) {
      if (e instanceof ApiError) setError(e); else toast.fromError(e);
    } finally {
      setSaving(false);
    }
  }

  const available = roles.filter((r) => !isAdminRole(r));
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isNew ? 'Nueva clave de API' : `Editar ${apiKey?.name}`}
      description="La clave actúa como una cuenta de servicio con los roles que elijas: misma seguridad por catálogo, campo y registro que una persona con esos roles."
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={() => void save()}>{isNew ? 'Crear clave' : 'Guardar'}</Button></>}
    >
      <div className="stack">
        {error && Object.keys(error.fieldErrors).length === 0 && <Alert tone="danger" title={error.title} />}
        <Input label="Nombre" required value={name} maxLength={100} placeholder="Tienda en línea" onChange={(e) => setName(e.target.value)} error={fieldError(error, 'name')} data-autofocus />
        <fieldset className="field" style={{ border: 0, padding: 0, margin: 0 }}>
          <legend className="field-label">Roles</legend>
          {available.length === 0 ? <span className="muted">No hay roles sin privilegios de administración.</span> : (
            <div className="stack" style={{ gap: 'var(--space-2)' }}>
              {available.map((r) => (
                <Checkbox key={r.id} label={r.name} checked={roleIds.includes(r.id)}
                  onChange={(c) => setRoleIds((ids) => (c ? [...ids, r.id] : ids.filter((x) => x !== r.id)))} />
              ))}
            </div>
          )}
          {fieldError(error, 'roleIds') && <span className="field-error" role="alert">{fieldError(error, 'roleIds')}</span>}
          <span className="field-hint">Los roles de administración (todo, modelo o usuarios) no se pueden dar a una clave.</span>
        </fieldset>
        {isNew && (
          <Select label="Vencimiento" value={expires} onChange={(e) => setExpires(e.target.value)} error={fieldError(error, 'expiresInDays')}>
            <option value="">Sin vencimiento</option>
            <option value="30">30 días</option>
            <option value="90">90 días</option>
            <option value="365">1 año</option>
          </Select>
        )}
      </div>
    </Dialog>
  );
}

function RotateKeyDialog({ apiKey, onClose, onRotated }: { apiKey: ApiKey | null; onClose: () => void; onRotated: (secret: string) => void }) {
  const toast = useToast();
  const [grace, setGrace] = useState<'0' | '24'>('0');
  const [saving, setSaving] = useState(false);
  useEffect(() => { if (apiKey) setGrace('0'); }, [apiKey]);
  return (
    <Dialog
      open={!!apiKey}
      onClose={onClose}
      title={`Rotar ${apiKey?.name}`}
      description="Se emite una clave nueva con el mismo prefijo, roles y vencimiento."
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={async () => {
        if (!apiKey) return;
        setSaving(true);
        try { const r = await integrationsApi.rotateKey(apiKey.id, grace === '24' ? 24 : 0); onRotated(r.secret); }
        catch (e) { toast.fromError(e); } finally { setSaving(false); }
      }}>Rotar clave</Button></>}
    >
      <Select label="La clave anterior" value={grace} onChange={(e) => setGrace(e.target.value as '0' | '24')}>
        <option value="0">Deja de servir de inmediato</option>
        <option value="24">Sigue sirviendo 24 horas (para cambiarla sin cortes)</option>
      </Select>
    </Dialog>
  );
}

function KeysTab() {
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const { data: keys, loading, error } = useAsync((s) => integrationsApi.keys(s), [tick]);
  const { data: roles } = useAsync(() => adminApi.roles(), []);
  const [editing, setEditing] = useState<ApiKey | null>(null);
  const [creating, setCreating] = useState(false);
  const [rotating, setRotating] = useState<ApiKey | null>(null);
  const [revoking, setRevoking] = useState<ApiKey | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [secretPending, setSecretPending] = useState(false);
  const roleName = (id: number) => roles?.find((r) => r.id === id)?.name ?? `#${id}`;

  if (loading && !keys) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar las claves'} />;

  return (
    <div className="stack">
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <Button variant="primary" icon={Plus} onClick={() => setCreating(true)}>Nueva clave</Button>
      </div>
      <div className="table-wrap">
        {!keys?.length ? (
          <EmptyState icon={KeyRound} title="Sin claves de API" description="Una clave permite a otro sistema usar la API de datos y reportes con permisos acotados." />
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Nombre</th><th>Prefijo</th><th>Roles</th><th>Último uso</th><th>Vence</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {keys.map((k) => (
                  <tr key={k.id}>
                    <td style={{ fontWeight: 500 }}>{k.name}</td>
                    <td className="mono" style={{ fontSize: 'var(--text-sm)' }}>{k.prefix}…</td>
                    <td>{k.roleIds.map(roleName).join(', ') || '—'}</td>
                    <td>{k.lastUsedAt ? formatDateTime(k.lastUsedAt) : 'Nunca'}</td>
                    <td>{k.expiresAt ? formatDateTime(k.expiresAt) : '—'}{k.previousExpiresAt && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>La anterior sirve hasta {formatDateTime(k.previousExpiresAt)}</div>}</td>
                    <td><Badge tone={k.status === 'active' ? 'success' : k.status === 'expired' ? 'warning' : 'danger'}>{k.status === 'active' ? 'Vigente' : k.status === 'expired' ? 'Vencida' : 'Revocada'}</Badge></td>
                    <td>
                      {k.status !== 'revoked' && (
                        <div className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                          <Button size="sm" variant="ghost" icon={Pencil} onClick={() => setEditing(k)} aria-label={`Editar ${k.name}`}>Editar</Button>
                          {k.status === 'active' && <Button size="sm" variant="ghost" icon={RotateCcw} onClick={() => setRotating(k)} aria-label={`Rotar ${k.name}`}>Rotar</Button>}
                          <Button size="sm" variant="ghost" icon={ShieldOff} onClick={() => setRevoking(k)} aria-label={`Revocar ${k.name}`}>Revocar</Button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <Alert tone="info" title="Cómo se usa">
        Envía la clave en la cabecera <code>Authorization: Bearer &lt;clave&gt;</code> a <code>/api/data</code> o <code>/api/reports</code>. No necesita
        la cabecera anti-CSRF. Lo que haga queda en la auditoría a nombre de la clave.
      </Alert>

      <KeyDialog open={creating || !!editing} apiKey={editing} roles={roles ?? []}
        onClose={() => { setCreating(false); setEditing(null); }}
        onSaved={(s, pending) => { setCreating(false); setEditing(null); setTick((t) => t + 1); setSecretPending(!!pending); if (s) setSecret(s); }} />
      <RotateKeyDialog apiKey={rotating} onClose={() => setRotating(null)} onRotated={(s) => { setRotating(null); setTick((t) => t + 1); setSecretPending(false); setSecret(s); }} />
      <ConfirmDialog
        open={!!revoking}
        danger
        title="Revocar clave"
        message={<>«{revoking?.name}» dejará de funcionar para siempre (también una clave anterior en periodo de gracia). No se puede deshacer.</>}
        confirmLabel="Revocar"
        onCancel={() => setRevoking(null)}
        onConfirm={async () => {
          if (!revoking) return;
          try { await integrationsApi.revokeKey(revoking.id); toast.success('Clave revocada'); setTick((t) => t + 1); }
          catch (e) { toast.fromError(e); }
          finally { setRevoking(null); }
        }}
      />
      <SecretDialog secret={secret} pending={secretPending} title="Clave de API" onClose={() => setSecret(null)} />
    </div>
  );
}

/* ----------------------------- Clientes OAuth (spec 018) ----------------------------- */

const scopeInfo: { id: OAuthScope; label: string; hint: string }[] = [
  { id: 'odata.read', label: 'Consultas OData (odata.read)', hint: 'Lectura con $filter, $select, $orderby y $expand en /odata.' },
  { id: 'data.read', label: 'Leer datos y reportes (data.read)', hint: 'Lecturas de /api/data y /api/reports.' },
  { id: 'data.write', label: 'Escribir datos (data.write)', hint: 'Altas, cambios, transiciones, bajas e importación en /api/data.' },
];
const scopeShort: Record<OAuthScope, string> = { 'odata.read': 'OData', 'data.read': 'Leer datos', 'data.write': 'Escribir datos' };

function ClientDialog({ open, client, roles, onClose, onSaved }: {
  open: boolean; client: OAuthClient | null; roles: Role[]; onClose: () => void; onSaved: (created?: { clientId: string; secret: string; pending: boolean }) => void;
}) {
  const toast = useToast();
  const isNew = !client;
  const [name, setName] = useState('');
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [scopes, setScopes] = useState<OAuthScope[]>([]);
  const [minutes, setMinutes] = useState('15');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    if (!open) return;
    setName(client?.name ?? '');
    setRoleIds(client?.roleIds ?? []);
    setScopes(client?.scopes ?? ['odata.read']);
    setMinutes(String(client?.tokenMinutes ?? 15));
    setError(null);
  }, [open, client]);

  async function save() {
    setSaving(true);
    setError(null);
    const input = { name: name.trim(), roleIds, scopes, tokenMinutes: Number(minutes) };
    try {
      if (isNew) {
        const r = await integrationsApi.createClient(input);
        // Pendiente de aprobación: el client_id va en la solicitud (no hay cliente todavía).
        const clientId = isPending(r) ? String((r.securityChange.after as { clientId?: string }).clientId ?? '') : r.client.clientId;
        onSaved({ clientId, secret: r.secret, pending: isPending(r) });
      } else {
        const r = await integrationsApi.updateClient(client.id, input);
        if (isPending(r)) toast.success('Solicitud enviada: otro administrador debe aprobar los roles o alcances nuevos');
        else toast.success('Cliente actualizado');
        onSaved();
      }
    } catch (e) {
      if (e instanceof ApiError) {
        setError(e);
        // El primer error puede quedar fuera de la vista si se desplazó el diálogo (lista de roles larga).
        requestAnimationFrame(() => document.querySelector('[role="dialog"] .field-error')?.scrollIntoView({ block: 'nearest' }));
      } else toast.fromError(e);
    } finally {
      setSaving(false);
    }
  }

  const available = roles.filter((r) => !isAdminRole(r));
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isNew ? 'Nuevo cliente OAuth' : `Editar ${client?.name}`}
      description="El cliente cambia su client_id y secreto por un token corto. Sus roles deciden qué ve; los alcances, qué partes de la API puede usar."
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={() => void save()}>{isNew ? 'Crear cliente' : 'Guardar'}</Button></>}
    >
      <div className="stack">
        {error && Object.keys(error.fieldErrors).length === 0 && <Alert tone="danger" title={error.title} />}
        <Input label="Nombre" required value={name} maxLength={100} placeholder="Tablero de ventas" onChange={(e) => setName(e.target.value)} error={fieldError(error, 'name')} data-autofocus />
        <fieldset className="field" style={{ border: 0, padding: 0, margin: 0 }}>
          <legend className="field-label">Roles</legend>
          {/* Regresión (revisión exploratoria 018, F-01): con muchos roles, el error al final de la lista quedaba fuera de la vista. */}
          {fieldError(error, 'roleIds') && <span className="field-error" role="alert">{fieldError(error, 'roleIds')}</span>}
          {available.length === 0 ? <span className="muted">No hay roles sin privilegios de administración.</span> : (
            <div className="stack" style={{ gap: 'var(--space-2)' }}>
              {available.map((r) => (
                <Checkbox key={r.id} label={r.name} checked={roleIds.includes(r.id)}
                  onChange={(c) => setRoleIds((ids) => (c ? [...ids, r.id] : ids.filter((x) => x !== r.id)))} />
              ))}
            </div>
          )}
          <span className="field-hint">Los roles de administración (todo, modelo o usuarios) no se pueden dar a un cliente.</span>
        </fieldset>
        <fieldset className="field" style={{ border: 0, padding: 0, margin: 0 }}>
          <legend className="field-label">Alcances</legend>
          <div className="stack" style={{ gap: 'var(--space-2)' }}>
            {scopeInfo.map((s) => (
              <div key={s.id}>
                <Checkbox label={s.label} checked={scopes.includes(s.id)}
                  onChange={(c) => setScopes((list) => (c ? [...list, s.id] : list.filter((x) => x !== s.id)))} />
                <span className="field-hint" style={{ display: 'block', marginInlineStart: 'calc(16px + var(--space-2))' }}>{s.hint}</span>
              </div>
            ))}
          </div>
          {fieldError(error, 'scopes') && <span className="field-error" role="alert">{fieldError(error, 'scopes')}</span>}
          <span className="field-hint">Un alcance limita, nunca agrega: dentro de él manda lo que permiten los roles.</span>
        </fieldset>
        <Select label="Vida del token" value={minutes} onChange={(e) => setMinutes(e.target.value)} error={fieldError(error, 'tokenMinutes')}>
          <option value="5">5 minutos</option>
          <option value="15">15 minutos</option>
          <option value="30">30 minutos</option>
          <option value="60">60 minutos</option>
        </Select>
      </div>
    </Dialog>
  );
}

function RotateClientDialog({ client, onClose, onRotated }: { client: OAuthClient | null; onClose: () => void; onRotated: (clientId: string, secret: string) => void }) {
  const toast = useToast();
  const [grace, setGrace] = useState<'0' | '24'>('0');
  const [saving, setSaving] = useState(false);
  useEffect(() => { if (client) setGrace('0'); }, [client]);
  return (
    <Dialog
      open={!!client}
      onClose={onClose}
      title={`Rotar el secreto de ${client?.name}`}
      description="Se emite un secreto nuevo; el client_id, los roles y los alcances no cambian."
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={async () => {
        if (!client) return;
        setSaving(true);
        try { const r = await integrationsApi.rotateClient(client.id, grace === '24' ? 24 : 0); onRotated(r.client.clientId, r.secret); }
        catch (e) { toast.fromError(e); } finally { setSaving(false); }
      }}>Rotar secreto</Button></>}
    >
      <Select label="El secreto anterior" value={grace} onChange={(e) => setGrace(e.target.value as '0' | '24')}>
        <option value="0">Deja de servir de inmediato y se revocan sus tokens</option>
        <option value="24">Sigue sirviendo 24 horas (para cambiarlo sin cortes)</option>
      </Select>
    </Dialog>
  );
}

function ClientsTab() {
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const { data: clients, loading, error } = useAsync((s) => integrationsApi.clients(s), [tick]);
  const { data: urls } = useAsync((s) => integrationsApi.clientEndpoints(s), []);
  const { data: roles } = useAsync(() => adminApi.roles(), []);
  const [editing, setEditing] = useState<OAuthClient | null>(null);
  const [creating, setCreating] = useState(false);
  const [rotating, setRotating] = useState<OAuthClient | null>(null);
  const [revoking, setRevoking] = useState<OAuthClient | null>(null);
  const [shown, setShown] = useState<{ clientId: string; secret: string; pending: boolean } | null>(null);
  const roleName = (id: number) => roles?.find((r) => r.id === id)?.name ?? `#${id}`;

  if (loading && !clients) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los clientes'} />;

  return (
    <div className="stack">
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <Button variant="primary" icon={Plus} onClick={() => setCreating(true)}>Nuevo cliente</Button>
      </div>
      <div className="table-wrap">
        {!clients?.length ? (
          <EmptyState icon={Fingerprint} title="Sin clientes OAuth" description="Un cliente obtiene tokens de vida corta para consultar por OData o usar la API de datos con alcances acotados." />
        ) : (
          // Con todos los clientes revocados no queda nada enfocable: la región misma recibe el foco para desplazarla con el teclado.
          <div className="table-scroll" tabIndex={0} role="region" aria-label="Clientes OAuth: tabla desplazable">
            <table className="table">
              <thead><tr><th>Nombre</th><th>client_id</th><th>Roles</th><th>Alcances</th><th>Último token</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {clients.map((c) => (
                  <tr key={c.id}>
                    <td style={{ fontWeight: 500 }}>{c.name}</td>
                    <td className="mono" style={{ fontSize: 'var(--text-sm)' }}>{c.clientId}</td>
                    <td>{c.roleIds.map(roleName).join(', ') || '—'}</td>
                    <td>
                      <div className="row" style={{ gap: 4, flexWrap: 'wrap' }}>{c.scopes.map((s) => <Badge key={s} tone="neutral">{scopeShort[s]}</Badge>)}</div>
                      <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>Token de {c.tokenMinutes} min</div>
                    </td>
                    <td>
                      {c.lastTokenAt ? formatDateTime(c.lastTokenAt) : 'Nunca'}
                      {c.activeTokens > 0 && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{c.activeTokens === 1 ? '1 token activo' : `${c.activeTokens} tokens activos`}</div>}
                      {c.previousExpiresAt && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>El secreto anterior sirve hasta {formatDateTime(c.previousExpiresAt)}</div>}
                    </td>
                    <td><Badge tone={c.status === 'active' ? 'success' : 'danger'}>{c.status === 'active' ? 'Vigente' : 'Revocado'}</Badge></td>
                    <td>
                      {c.status === 'active' && (
                        <div className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                          <Button size="sm" variant="ghost" icon={Pencil} onClick={() => setEditing(c)} aria-label={`Editar ${c.name}`}>Editar</Button>
                          <Button size="sm" variant="ghost" icon={RotateCcw} onClick={() => setRotating(c)} aria-label={`Rotar el secreto de ${c.name}`}>Rotar</Button>
                          <Button size="sm" variant="ghost" icon={ShieldOff} onClick={() => setRevoking(c)} aria-label={`Revocar ${c.name}`}>Revocar</Button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <Alert tone="info" title="Cómo se usa">
        <div className="stack" style={{ gap: 'var(--space-2)' }}>
          <span>
            Pide un token con <code>POST</code> a la URL del token, <code>grant_type=client_credentials</code> y el client_id y secreto en
            <code> Authorization: Basic</code>. Envía el token como <code>Authorization: Bearer &lt;token&gt;</code>; al vencer, pide otro.
          </span>
          {urls && (
            <dl className="url-list">
              <dt>URL del token</dt><dd><code>{urls.token}</code></dd>
              <dt>API OData</dt><dd><code>{urls.odata}</code> (descubrimiento en <code>$metadata</code>)</dd>
            </dl>
          )}
        </div>
      </Alert>

      <ClientDialog open={creating || !!editing} client={editing} roles={roles ?? []}
        onClose={() => { setCreating(false); setEditing(null); }}
        onSaved={(created) => { setCreating(false); setEditing(null); setTick((t) => t + 1); if (created) setShown(created); }} />
      <RotateClientDialog client={rotating} onClose={() => setRotating(null)}
        onRotated={(clientId, secret) => { setRotating(null); setTick((t) => t + 1); setShown({ clientId, secret, pending: false }); }} />
      <ConfirmDialog
        open={!!revoking}
        danger
        title="Revocar cliente"
        message={<>«{revoking?.name}» dejará de obtener tokens y los que tiene dejan de servir ya. No se puede deshacer.</>}
        confirmLabel="Revocar"
        onCancel={() => setRevoking(null)}
        onConfirm={async () => {
          if (!revoking) return;
          try { await integrationsApi.revokeClient(revoking.id); toast.success('Cliente revocado'); setTick((t) => t + 1); }
          catch (e) { toast.fromError(e); }
          finally { setRevoking(null); }
        }}
      />
      <SecretDialog secret={shown?.secret ?? null} clientId={shown?.clientId} pending={shown?.pending} title="Credenciales del cliente" onClose={() => setShown(null)} />
    </div>
  );
}

const tabLabel: Record<Tab, string> = { webhooks: 'Webhooks', keys: 'Claves de API', clients: 'Clientes OAuth' };

export function IntegrationsPage() {
  const [tab, setTab] = useState<Tab>('webhooks');
  return (
    <div className="page">
      <PageHeader icon={Webhook} title="Integraciones" subtitle="A dónde se avisan los eventos (webhooks) y qué servicios pueden usar la API (claves y clientes OAuth)." />
      <Tabs label="Integraciones" value={tab} onChange={setTab}
        items={[{ id: 'webhooks', label: tabLabel.webhooks, icon: Webhook }, { id: 'keys', label: tabLabel.keys, icon: KeyRound },
          { id: 'clients', label: tabLabel.clients, icon: Fingerprint }]} />
      <div role="tabpanel" aria-label={tabLabel[tab]}>
        {tab === 'webhooks' ? <WebhooksTab /> : tab === 'keys' ? <KeysTab /> : <ClientsTab />}
      </div>
    </div>
  );
}
