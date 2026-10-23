import { useState } from 'react';
import { ArrowUpCircle, Blocks, CheckCircle2, Database, Download, ShieldCheck } from 'lucide-react';
import { ApiError } from '../api/client';
import { adminApi } from '../api/endpoints';
import type { ModuleInfo, ModulePreview } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { DynamicIcon } from '../components/DynamicIcon';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader } from '../components/ui';
import { useAsync } from '../lib/hooks';
import { PlanPreviewView } from './modeler/PlanReviewDialog';

type Mode = 'install' | 'upgrade';

export function ModulesPage() {
  const { data, loading, error, reload } = useAsync<ModuleInfo[]>(() => adminApi.modules(), []);
  const [selected, setSelected] = useState<{ module: ModuleInfo; mode: Mode } | null>(null);

  return (
    <div className="page">
      <PageHeader
        icon={Blocks}
        title="Módulos"
        subtitle="Paquetes listos para instalar: catálogos, documentos, procesos, roles y datos iniciales. Todo es metadato, sin código."
      />
      {loading ? <LoadingBlock /> : error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los módulos'} />
      ) : !data?.length ? (
        <div className="card"><EmptyState icon={Blocks} title="No hay módulos disponibles" description="Los paquetes se leen de la carpeta modules del servidor." /></div>
      ) : (
        <div className="grid-cards modules-grid">
          {data.map((m) => (
            <article key={m.id} className="card module-card" aria-labelledby={`mod-${m.id}`}>
              <div className="row module-card-head">
                <span className="entity-card-icon"><DynamicIcon name={m.icon ?? 'blocks'} size={18} /></span>
                <div className="grow">
                  <h2 id={`mod-${m.id}`} className="entity-card-title">{m.name}</h2>
                  <span className="subtle mono module-version">
                    {m.installed && m.installedVersion && m.installedVersion !== m.version ? `v${m.installedVersion} instalada · v${m.version} disponible` : `v${m.version}`}
                  </span>
                </div>
                {m.installed && <Badge tone="success" icon={CheckCircle2}>Instalado</Badge>}
              </div>
              {m.description && <p className="muted module-desc">{m.description}</p>}
              {!m.installed && !!m.missingRequirements?.length && (
                <Alert tone="warning" title={`Requiere ${m.missingRequirements.join(' y ')}`}>Instala primero {m.missingRequirements.length === 1 ? 'ese módulo' : 'esos módulos'}.</Alert>
              )}
              {!!m.requires?.length && !m.missingRequirements?.length && (
                <p className="subtle module-requires">Usa {m.requires.join(', ')}</p>
              )}
              {m.upgradeAvailable && !!m.pendingUpgrades?.length && (
                <ul className="module-upgrades">
                  {m.pendingUpgrades.map((u) => <li key={u}>{u}</li>)}
                </ul>
              )}
              <div className="entity-card-foot">
                <Database size={14} aria-hidden />
                {m.entities.length} {m.entities.length === 1 ? 'catálogo' : 'catálogos'}
                <span className="spacer" />
                {!m.installed && (
                  <Button size="sm" variant="primary" icon={Download} disabled={!!m.missingRequirements?.length}
                    onClick={() => setSelected({ module: m, mode: 'install' })} aria-label={`Instalar ${m.name}`}>Instalar</Button>
                )}
                {m.installed && m.upgradeAvailable && (
                  <Button size="sm" variant="primary" icon={ArrowUpCircle} onClick={() => setSelected({ module: m, mode: 'upgrade' })} aria-label={`Actualizar ${m.name}`}>
                    Actualizar a v{m.version}
                  </Button>
                )}
              </div>
            </article>
          ))}
        </div>
      )}
      <ModuleDialog
        module={selected?.module ?? null}
        mode={selected?.mode ?? 'install'}
        onClose={() => setSelected(null)}
        onDone={() => { setSelected(null); reload(); }}
      />
    </div>
  );
}

function ModuleDialog({ module: m, mode, onClose, onDone }: { module: ModuleInfo | null; mode: Mode; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const { refresh } = useAuth();
  const upgrade = mode === 'upgrade';
  const { data: preview, loading, error } = useAsync<ModulePreview | undefined>(
    () => (m ? (upgrade ? adminApi.previewUpgrade(m.id) : adminApi.previewModule(m.id)) : Promise.resolve(undefined)),
    [m?.id, mode],
  );
  const [busy, setBusy] = useState(false);
  const [applyError, setApplyError] = useState<ApiError | null>(null);

  async function apply() {
    if (!m) return;
    setBusy(true);
    setApplyError(null);
    try {
      const r = upgrade ? await adminApi.upgradeModule(m.id) : await adminApi.installModule(m.id);
      toast.success(upgrade ? 'Módulo actualizado' : 'Módulo instalado', `${m.name} v${m.version} · conjunto de cambios #${r.changeSetId}`);
      await refresh();
      onDone();
    } catch (e) {
      setApplyError(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setBusy(false);
    }
  }

  const err = applyError ?? (error instanceof ApiError ? error : null);
  return (
    <Dialog
      open={!!m}
      onClose={() => { if (!busy) { setApplyError(null); onClose(); } }}
      size="lg"
      title={m ? `${upgrade ? 'Actualizar' : 'Instalar'} ${m.name}` : ''}
      description={upgrade
        ? `De v${m?.installedVersion ?? '?'} a v${m?.version ?? '?'}. Los registros existentes se conservan; todo se aplica en una sola transacción.`
        : 'Revisa lo que se va a crear. La instalación se aplica en una sola transacción.'}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>Cancelar</Button>
          <Button variant="primary" icon={upgrade ? ArrowUpCircle : Download} loading={busy} disabled={!preview?.valid || loading} onClick={apply}>
            {upgrade ? 'Actualizar módulo' : 'Instalar módulo'}
          </Button>
        </>
      }
    >
      {loading && <LoadingBlock />}
      {err && (
        <Alert tone="danger" title={err.title}>
          {err.detail}
          {Object.entries(err.fieldErrors).map(([k, v]) => <div key={k}>{k}: {v.join(', ')}</div>)}
        </Alert>
      )}
      {preview && (
        <div className="stack">
          {!upgrade && (
            <div className="module-summary">
              <div className="module-summary-item">
                <ShieldCheck size={16} aria-hidden />
                <div>
                  <div className="subtle">Roles que se crearán</div>
                  <div>{preview.roles.length ? preview.roles.join(', ') : 'Ninguno'}</div>
                </div>
              </div>
              <div className="module-summary-item">
                <Database size={16} aria-hidden />
                <div>
                  <div className="subtle">Datos iniciales</div>
                  <div>{preview.seedRecords.toLocaleString('es-MX')} {preview.seedRecords === 1 ? 'registro' : 'registros'}</div>
                </div>
              </div>
            </div>
          )}
          <PlanPreviewView
            preview={preview}
            validText={upgrade
              ? 'Los cambios se aplican en una sola transacción y no se borra ningún dato. Si algo falla, el módulo queda como estaba.'
              : 'Modelo, roles, permisos y datos iniciales se aplican en una sola transacción. Si algo falla, no se instala nada.'}
          />
        </div>
      )}
    </Dialog>
  );
}
