import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Archive, CheckCircle2, Download, Eye, Fingerprint, GitCompareArrows, History, PackageCheck, PackagePlus, Rocket, ScanSearch, ShieldAlert,
  ShieldCheck, Upload, XCircle,
} from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { environmentsApi } from '../api/endpoints';
import type { EnvironmentInfo, ExportPreview, ExportRequest, PackageImport, PackageRole, PackageStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, Checkbox, EmptyState, Input, LoadingBlock, PageHeader, Tabs, Textarea } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';
import { PlanPreviewView } from './modeler/PlanReviewDialog';

type Tab = 'received' | 'export';

const statusTone: Record<PackageStatus, 'warning' | 'accent' | 'danger' | 'success'> = {
  pending: 'warning', approved: 'accent', rejected: 'danger', applied: 'success',
};
const statusLabel: Record<PackageStatus, string> = {
  pending: 'Pendiente', approved: 'Aprobado', rejected: 'Rechazado', applied: 'Aplicado',
};

const shortHash = (h: string) => h.slice(0, 12);

/** 1.4.2 → 1.5.0 (la misma regla que el servidor). */
function nextMinor(last?: string) {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(last ?? '');
  return m ? `${m[1]}.${Number(m[2]) + 1}.0` : '1.0.0';
}

function ProblemAlert({ error }: { error: ApiError }) {
  const items = Object.values(error.fieldErrors).flat();
  return (
    <Alert tone="danger" title={error.title}>
      {items.length > 1 && <ul className="import-errors">{items.map((m, i) => <li key={i}>{m}</li>)}</ul>}
    </Alert>
  );
}

function permissionText(p: PackageRole['permissions'][number]) {
  const verbs = [p.canRead && 'leer', p.canCreate && 'crear', p.canUpdate && 'editar', p.canDelete && 'eliminar'].filter(Boolean);
  return `${p.label} (${verbs.join(', ') || 'sin acceso'})`;
}

/** Roles que el paquete crea o a los que da permisos: lo primero que revisa quien aprueba (H5). */
function RolesSummary({ roles }: { roles: PackageRole[] }) {
  const relevant = roles.filter((r) => r.isNew || r.permissions.length > 0);
  if (relevant.length === 0) return null;
  return (
    <section aria-labelledby="pkg-roles">
      <h3 id="pkg-roles" className="section-title">Roles y permisos</h3>
      <ul className="env-roles">
        {relevant.map((r) => (
          <li key={r.name}>
            <ShieldCheck size={15} aria-hidden />
            <div className="grow">
              <div className="row" style={{ flexWrap: 'wrap' }}>
                <strong>{r.name}</strong>
                <Badge tone={r.isNew ? 'accent' : 'neutral'}>{r.isNew ? 'Rol nuevo' : 'Ya existe'}</Badge>
                {r.canViewAudit && <Badge tone="warning">Ve la auditoría</Badge>}
              </div>
              <div className="subtle">{r.permissions.length ? r.permissions.map(permissionText).join(' · ') : 'Sin permisos sobre catálogos'}</div>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

function Notes({ tone, title, items }: { tone: 'danger' | 'warning' | 'info'; title: string; items: string[] }) {
  if (items.length === 0) return null;
  return (
    <Alert tone={tone} title={title}>
      <ul className="import-errors">{items.map((m, i) => <li key={i}>{m}</li>)}</ul>
    </Alert>
  );
}

/* ----------------------------- Paquetes recibidos ----------------------------- */

function ReviewDialog({ id, env, onClose, onChanged }: { id: number | null; env: EnvironmentInfo | undefined; onClose: () => void; onChanged: () => void }) {
  const toast = useToast();
  const { refresh } = useAuth();
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync<PackageImport | undefined>((s) => (id ? environmentsApi.detail(id, s) : Promise.resolve(undefined)), [id, tick]);
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState<'approve' | 'reject' | 'apply' | null>(null);
  const [actionError, setActionError] = useState<ApiError | null>(null);
  // Spec 014 (H4): archivar campos que solo existen aquí se confirma aparte; si el diff cambia, se vuelve a pedir.
  const [confirmArchive, setConfirmArchive] = useState(false);

  useEffect(() => { setComment(''); setActionError(null); }, [id]);
  useEffect(() => { setConfirmArchive(false); }, [id, data?.diff?.diffHash]);

  async function act(kind: 'approve' | 'reject' | 'apply') {
    if (!data) return;
    setBusy(kind);
    setActionError(null);
    try {
      if (kind === 'approve') {
        await environmentsApi.approve(data.id, data.diff!.diffHash, comment.trim() || undefined, confirmArchive);
        toast.success('Paquete aprobado', 'Ya se puede aplicar.');
      } else if (kind === 'reject') {
        await environmentsApi.reject(data.id, comment.trim());
        toast.success('Paquete rechazado');
      } else {
        const r = await environmentsApi.apply(data.id, data.diff!.diffHash, confirmArchive);
        toast.success('Paquete aplicado', `${data.name} v${data.version}${r.changeSetId ? ` · conjunto de cambios #${r.changeSetId}` : ''}`);
        await refresh();
      }
      setComment('');
      setTick((t) => t + 1);
      onChanged();
    } catch (e) {
      setActionError(e instanceof ApiError ? e : new ApiError(0, {}));
      // El diff pudo cambiar (409): se vuelve a cargar para mostrar el actual.
      if (e instanceof ApiError && e.status === 409) setTick((t) => t + 1);
    } finally {
      setBusy(null);
    }
  }

  const open = data && (data.status === 'pending' || data.status === 'approved');
  const diff = data?.diff;
  const needsArchive = !!open && (diff?.localArchives.length ?? 0) > 0 && !confirmArchive;
  const err = actionError ?? (error instanceof ApiError ? error : null);
  return (
    <Dialog
      open={id !== null}
      onClose={() => { if (!busy) onClose(); }}
      size="lg"
      title={data ? `${data.name} v${data.version}` : 'Paquete'}
      description={data?.description || 'Revisa el diff contra este entorno antes de aplicarlo.'}
      footer={
        <>
          <Button onClick={onClose} disabled={!!busy}>Cerrar</Button>
          {open && (
            <Button variant="danger" icon={XCircle} loading={busy === 'reject'} disabled={!!busy} onClick={() => void act('reject')}
              title="Escribe el motivo en el comentario">Rechazar</Button>
          )}
          {data?.canApprove && (
            <Button icon={CheckCircle2} loading={busy === 'approve'} disabled={!!busy || needsArchive} onClick={() => void act('approve')}
              title={needsArchive ? 'Confirma primero los campos que se archivarán' : undefined}>Aprobar</Button>
          )}
          {open && (
            <Button variant="primary" icon={Rocket} loading={busy === 'apply'} disabled={!!busy || !data?.canApply || needsArchive} onClick={() => void act('apply')}
              title={data?.canApply && needsArchive ? 'Confirma primero los campos que se archivarán' : undefined}>
              Aplicar paquete
            </Button>
          )}
        </>
      }
    >
      {loading && !data && <LoadingBlock />}
      {err && <ProblemAlert error={err} />}
      {data && (
        <div className="stack">
          <div className="module-summary">
            <div className="module-summary-item">
              <PackageCheck size={16} aria-hidden />
              <div><div className="subtle">Versión</div><div>{data.status === 'applied' ? `${data.previousVersion ?? 'Sin instalar'} → ${data.version}` : `${data.installedVersion ?? 'Sin instalar'} → ${data.version}`}</div></div>
            </div>
            <div className="module-summary-item">
              <GitCompareArrows size={16} aria-hidden />
              <div><div className="subtle">Origen</div><div>{data.sourceEnvironment ?? 'Desconocido'}{data.exportedAt && <span className="subtle"> · {formatDateTime(data.exportedAt)}</span>}</div></div>
            </div>
            <div className="module-summary-item">
              <ShieldCheck size={16} aria-hidden />
              <div><div className="subtle">Firma</div><div>{data.signed ? (env?.signing ? 'Firmado y verificado' : 'Firmado (este entorno no tiene clave para verificarlo)') : 'Sin firma'}</div></div>
            </div>
            <div className="module-summary-item">
              <Fingerprint size={16} aria-hidden />
              <div className="grow" style={{ minWidth: 0 }}><div className="subtle">Huella (SHA-256)</div><div className="mono env-hash" aria-label="Huella del paquete">{data.hash}</div></div>
            </div>
          </div>

          {/* Quién hizo qué (H4 CA3): la misma información queda en la auditoría. */}
          <ul className="env-trail subtle">
            <li>Subido por {data.uploadedBy} · {formatDateTime(data.uploadedAt)}</li>
            {data.reviewedBy && data.status !== 'rejected' && (
              <li>Aprobado por {data.reviewedBy} · {formatDateTime(data.reviewedAt)}{data.reviewComment && <> · «{data.reviewComment}»</>}</li>
            )}
            {data.appliedBy && (
              <li>Aplicado por {data.appliedBy} · {formatDateTime(data.appliedAt)}{data.changeSetId ? ` · conjunto de cambios #${data.changeSetId}` : ''}</li>
            )}
          </ul>
          {data.status === 'rejected' && (
            <Alert tone="danger" title={`Rechazado por ${data.reviewedBy ?? '—'}`}>{data.reviewComment}</Alert>
          )}
          {data.status === 'applied' && <Alert tone="info" icon={CheckCircle2} title="Paquete aplicado">El modelo de este entorno ya tiene esta versión.</Alert>}

          {diff && (
            <>
              <Notes tone="danger" title="Conflictos: el plan no puede expresar estos cambios" items={diff.conflicts} />
              <Notes tone="warning" title="Advertencias" items={diff.warnings} />
              {diff.localArchives.length > 0 && (
                <section className="env-archives" aria-labelledby="pkg-archives">
                  <Alert tone="warning" icon={Archive} title={<span id="pkg-archives">Campos de este entorno que se archivarán</span>}>
                    <p>Existen solo en este entorno: el paquete no los trae. Al aplicarlo se archivan; dejan de verse y sus datos se conservan.</p>
                    <ul className="import-errors">{diff.localArchives.map((m, i) => <li key={i}>{m}</li>)}</ul>
                    {/* Regresión (verificación 014, F-02): solo quien puede aprobar o aplicar confirma. */}
                    {open && (data.canApprove || data.canApply) && (
                      <Checkbox checked={confirmArchive} onChange={setConfirmArchive} disabled={!!busy}
                        label={`Entiendo que ${diff.localArchives.length === 1 ? 'este campo se archivará' : `estos ${diff.localArchives.length} campos se archivarán`}`} />
                    )}
                  </Alert>
                </section>
              )}
              <RolesSummary roles={diff.roles} />
              {diff.empty ? (
                <Alert tone="info" title="Sin cambios en el modelo">Este entorno ya coincide con el paquete: al aplicarlo solo se registra la versión.</Alert>
              ) : (diff.steps.length > 0 || diff.errors.length > 0) && (
                <section aria-labelledby="pkg-diff">
                  <h3 id="pkg-diff" className="section-title">Cambios en este entorno</h3>
                  <PlanPreviewView preview={diff}
                    validText="Se aplica en una sola transacción, con auditoría. Ningún dato se borra: los campos que salen se archivan." />
                </section>
              )}
              {!data.canApply && data.blockedReason && <Alert tone="warning" title={data.blockedReason} />}
              {open && (
                <Textarea label="Comentario" rows={2} maxLength={500} value={comment} onChange={(e) => setComment(e.target.value)}
                  hint={data.canApprove ? 'Opcional al aprobar; obligatorio al rechazar.' : 'Obligatorio al rechazar.'}
                  error={fieldError(actionError, 'comment')} />
              )}
            </>
          )}
        </div>
      )}
    </Dialog>
  );
}

function ReceivedTab({ env }: { env: EnvironmentInfo | undefined }) {
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync((s) => environmentsApi.imports(s), [tick]);
  const [reviewing, setReviewing] = useState<number | null>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<ApiError | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  async function upload(file: File) {
    setUploading(true);
    setUploadError(null);
    try {
      const r = await environmentsApi.upload(file);
      toast.success('Paquete recibido', `${r.name} v${r.version}: revisa el diff.`);
      setTick((t) => t + 1);
      setReviewing(r.id);
    } catch (e) {
      if (e instanceof ApiError) setUploadError(e); else toast.fromError(e);
    } finally {
      setUploading(false);
      if (fileRef.current) fileRef.current.value = '';
    }
  }

  return (
    <div className="stack">
      <div className="row env-toolbar">
        <p className="muted grow" style={{ margin: 0 }}>
          Sube el archivo que exportó otro entorno. Verás qué cambiaría aquí antes de aplicarlo{env?.requireApproval ? ', y otro administrador debe aprobarlo' : ''}.
        </p>
        {/* El input nativo queda oculto pero con nombre accesible; el botón lo abre. */}
        <span className="env-file">
          <input ref={fileRef} className="sr-only" type="file" accept=".json,application/json" aria-label="Archivo del paquete" tabIndex={-1}
            onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); }} />
        </span>
        <Button variant="primary" icon={Upload} loading={uploading} onClick={() => fileRef.current?.click()}>Subir paquete</Button>
      </div>
      {uploadError && <ProblemAlert error={uploadError} />}
      {loading && !data ? <LoadingBlock /> : error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los paquetes'} />
      ) : (
        <div className="table-wrap">
          {!data?.length ? (
            <EmptyState icon={PackageCheck} title="Sin paquetes recibidos" description="Cuando subas un paquete aparecerá aquí con su estado, su huella y quién lo revisó." />
          ) : (
            <div className="table-scroll">
              <table className="table">
                <thead><tr><th>Paquete</th><th>Versión</th><th>Estado</th><th>Huella</th><th>Subido</th><th><span className="sr-only">Acciones</span></th></tr></thead>
                <tbody>
                  {data.map((p) => (
                    <tr key={p.id}>
                      <td><div style={{ fontWeight: 500 }}>{p.name}</div><div className="subtle mono" style={{ fontSize: 'var(--text-xs)' }}>{p.packageId}</div></td>
                      <td className="mono" style={{ whiteSpace: 'nowrap' }}>{p.previousVersion ? `${p.previousVersion} → ` : ''}{p.version}</td>
                      <td><Badge tone={statusTone[p.status]}>{statusLabel[p.status]}</Badge></td>
                      <td className="mono" style={{ fontSize: 'var(--text-sm)' }} title={p.hash}>{shortHash(p.hash)}</td>
                      <td>{p.uploadedBy}<div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{formatDateTime(p.uploadedAt)}</div></td>
                      <td style={{ textAlign: 'right' }}>
                        <Button size="sm" variant="ghost" icon={Eye} onClick={() => setReviewing(p.id)} aria-label={`Revisar ${p.name} v${p.version}`}>Revisar</Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
      <ReviewDialog id={reviewing} env={env} onClose={() => setReviewing(null)} onChanged={() => setTick((t) => t + 1)} />
    </div>
  );
}

/* ----------------------------- Exportar ----------------------------- */

function ExportTab() {
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync((s) => environmentsApi.exportable(s), [tick]);
  const [form, setForm] = useState({ id: '', name: '', version: '1.0.0', description: '' });
  const [versionTouched, setVersionTouched] = useState(false);
  const [entities, setEntities] = useState<string[]>([]);
  const [dashboards, setDashboards] = useState<string[]>([]);
  const [preview, setPreview] = useState<ExportPreview | null>(null);
  const [busy, setBusy] = useState<'preview' | 'download' | null>(null);
  const [err, setErr] = useState<ApiError | null>(null);

  // Versión sugerida: la siguiente menor de la última exportación con ese identificador.
  const lastVersion = useMemo(() => data?.exports.find((x) => x.packageId === form.id.trim())?.version, [data, form.id]);
  useEffect(() => { if (!versionTouched) setForm((f) => ({ ...f, version: lastVersion ? nextMinor(lastVersion) : '1.0.0' })); }, [lastVersion, versionTouched]);

  const groups = useMemo(() => {
    const map = new Map<string, NonNullable<typeof data>['entities']>();
    for (const e of data?.entities ?? []) {
      const g = e.group ?? 'Catálogos';
      map.set(g, [...(map.get(g) ?? []), e]);
    }
    return [...map.entries()];
  }, [data]);

  const request: ExportRequest = { id: form.id.trim(), name: form.name.trim(), version: form.version.trim(), description: form.description.trim() || undefined, entities, dashboards };
  const set = (patch: Partial<typeof form>) => { setForm((f) => ({ ...f, ...patch })); setPreview(null); };
  const toggle = (list: string[], setList: (v: string[]) => void, name: string, on: boolean) => {
    setList(on ? [...list, name] : list.filter((x) => x !== name));
    setPreview(null);
  };

  async function review() {
    setBusy('preview');
    setErr(null);
    try { setPreview(await environmentsApi.previewExport(request)); }
    catch (e) { setPreview(null); if (e instanceof ApiError) setErr(e); else toast.fromError(e); }
    finally { setBusy(null); }
  }

  async function download() {
    setBusy('download');
    setErr(null);
    try {
      const blob = await environmentsApi.exportFile(request);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${request.id}-${request.version}.json`;
      a.click();
      URL.revokeObjectURL(url);
      toast.success('Paquete exportado', `${request.id}-${request.version}.json`);
      setTick((t) => t + 1);
    } catch (e) {
      if (e instanceof ApiError) setErr(e); else toast.fromError(e);
    } finally {
      setBusy(null);
    }
  }

  if (loading && !data) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar el modelo'} />;

  return (
    <div className="stack">
      <div className="card env-card stack">
        <p className="muted" style={{ margin: 0 }}>
          El paquete lleva solo metadatos: catálogos con sus campos, procesos, reglas, automatizaciones, seguridad, formularios, vistas,
          reportes y webhooks (sin destino ni secreto), tableros y los roles que usan. Nunca lleva registros, usuarios ni claves.
        </p>
        <div className="form-grid">
          <Input label="Identificador" required value={form.id} maxLength={41} placeholder="compras-ajustes" onChange={(e) => set({ id: e.target.value.toLowerCase() })}
            hint="Minúsculas, números y guiones. Identifica el paquete en todos los entornos." data-autofocus />
          <Input label="Nombre" required value={form.name} maxLength={100} placeholder="Ajustes de compras" onChange={(e) => set({ name: e.target.value })} />
          <Input label="Versión" required value={form.version} maxLength={17} onChange={(e) => { setVersionTouched(true); set({ version: e.target.value }); }}
            hint={lastVersion ? `Última exportada: ${lastVersion}` : 'Forma x.y.z'} />
          <Textarea label="Descripción" rows={2} maxLength={500} value={form.description} onChange={(e) => set({ description: e.target.value })} />
        </div>

        <fieldset className="env-pick">
          <legend className="field-label">Catálogos</legend>
          {groups.length === 0 ? <span className="muted">No hay catálogos activos.</span> : groups.map(([group, list]) => (
            <div key={group} className="env-pick-group">
              <div className="subtle env-pick-title">{group}</div>
              {list.map((e) => (
                <div key={e.name}>
                  <Checkbox label={e.labelPlural} checked={entities.includes(e.name)} onChange={(c) => toggle(entities, setEntities, e.name, c)} />
                  {e.children.length > 0 && <div className="field-hint env-pick-hint">Con sus partidas: {e.children.join(', ')}</div>}
                </div>
              ))}
            </div>
          ))}
        </fieldset>
        {!!data?.dashboards.length && (
          <fieldset className="env-pick">
            <legend className="field-label">Tableros</legend>
            <div className="env-pick-group">
              {data.dashboards.map((d) => (
                <Checkbox key={d.name} label={d.label} checked={dashboards.includes(d.name)} onChange={(c) => toggle(dashboards, setDashboards, d.name, c)} />
              ))}
            </div>
          </fieldset>
        )}
        <div className="row" style={{ justifyContent: 'flex-end', flexWrap: 'wrap' }}>
          <Button icon={ScanSearch} loading={busy === 'preview'} disabled={!!busy} onClick={() => void review()}>Revisar paquete</Button>
          <Button variant="primary" icon={Download} loading={busy === 'download'} disabled={!!busy || !preview?.valid} onClick={() => void download()}
            title={preview?.valid ? undefined : 'Primero revisa el paquete'}>
            Descargar paquete
          </Button>
        </div>
      </div>

      {err && <ProblemAlert error={err} />}
      {preview && (
        <section className="card env-card stack" aria-labelledby="export-preview">
          <h2 id="export-preview" className="section-title">Vista previa del paquete</h2>
          <div className="module-summary">
            <div className="module-summary-item"><PackagePlus size={16} aria-hidden />
              <div><div className="subtle">Contenido</div><div>{preview.entities.length} catálogo(s) · {preview.operations} operación(es)</div></div></div>
            <div className="module-summary-item"><ShieldCheck size={16} aria-hidden />
              <div><div className="subtle">Firma</div><div>{preview.signed ? 'Se firma con la clave de este entorno' : 'Sin firma (este entorno no tiene clave)'}</div></div></div>
            <div className="module-summary-item"><Fingerprint size={16} aria-hidden />
              <div className="grow" style={{ minWidth: 0 }}><div className="subtle">Huella (SHA-256)</div><div className="mono env-hash">{preview.hash}</div></div></div>
          </div>
          <Notes tone="info" title="Depende de catálogos que no van en el paquete: el destino debe tenerlos" items={preview.dependencies} />
          <Notes tone="warning" title="Advertencias" items={preview.warnings} />
          <RolesSummary roles={preview.roles} />
          <PlanPreviewView preview={preview} validText="El paquete crea esto en un entorno vacío; en uno que ya lo tiene, solo se aplica la diferencia." />
        </section>
      )}

      {!!data?.exports.length && (
        <section aria-labelledby="export-history">
          <h2 id="export-history" className="section-title"><History size={15} aria-hidden /> Exportaciones recientes</h2>
          <div className="table-wrap">
            <div className="table-scroll">
              <table className="table">
                <thead><tr><th>Paquete</th><th>Versión</th><th>Huella</th><th>Exportó</th></tr></thead>
                <tbody>
                  {data.exports.map((x, i) => (
                    <tr key={i}>
                      <td><div style={{ fontWeight: 500 }}>{x.name}</div><div className="subtle mono" style={{ fontSize: 'var(--text-xs)' }}>{x.packageId}</div></td>
                      <td className="mono">{x.version}</td>
                      <td className="mono" style={{ fontSize: 'var(--text-sm)' }} title={x.hash}>{shortHash(x.hash)}</td>
                      <td>{x.exportedBy}<div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{formatDateTime(x.exportedAt)}</div></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </section>
      )}
    </div>
  );
}

export function EnvironmentsPage() {
  const [tab, setTab] = useState<Tab>('received');
  const { data: env } = useAsync((s) => environmentsApi.info(s), []);
  return (
    <div className="page">
      <PageHeader icon={GitCompareArrows} title="Entornos"
        subtitle="Lleva lo construido en el modelador de un entorno a otro como paquete versionado: con diff, aprobación y auditoría." />
      {env && (
        <section className="card env-card env-current" aria-label="Este entorno">
          <div className="grow">
            <div className="subtle">Este entorno</div>
            <div className="env-name">{env.name}</div>
          </div>
          <div className="row" style={{ flexWrap: 'wrap' }}>
            <Badge tone={env.requireApproval ? 'warning' : 'neutral'}>{env.requireApproval ? 'Requiere aprobación de otro administrador' : 'Aplicación directa'}</Badge>
            <Badge tone={env.signing ? 'success' : 'warning'}>{env.signing ? 'Firma obligatoria' : 'Sin clave de firma'}</Badge>
          </div>
        </section>
      )}
      {env?.directChangesOverride && (
        <Alert tone="warning" icon={ShieldAlert} title="Este entorno exige aprobación, pero permite cambios directos">
          El servidor tiene <span className="mono">Environments:AllowDirectChanges</span>: el modelador, los módulos y el asistente
          cambian el modelo sin un paquete aprobado por otro administrador. Úsalo solo en entornos de prueba.
        </Alert>
      )}
      <Tabs label="Entornos" value={tab} onChange={setTab}
        items={[{ id: 'received', label: 'Paquetes recibidos', icon: PackageCheck }, { id: 'export', label: 'Exportar', icon: PackagePlus }]} />
      <div role="tabpanel" aria-label={tab === 'received' ? 'Paquetes recibidos' : 'Exportar'}>
        {tab === 'received' ? <ReceivedTab env={env} /> : <ExportTab />}
      </div>
    </div>
  );
}
