import { useState } from 'react';
import { Blocks, CheckCircle2, Database, Download, ShieldCheck } from 'lucide-react';
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

export function ModulesPage() {
  const { data, loading, error, reload } = useAsync<ModuleInfo[]>(() => adminApi.modules(), []);
  const [selected, setSelected] = useState<ModuleInfo | null>(null);

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
                  <span className="subtle mono module-version">v{m.version}</span>
                </div>
                {m.installed && <Badge tone="success" icon={CheckCircle2}>Instalado</Badge>}
              </div>
              {m.description && <p className="muted module-desc">{m.description}</p>}
              <div className="entity-card-foot">
                <Database size={14} aria-hidden />
                {m.entities.length} {m.entities.length === 1 ? 'catálogo' : 'catálogos'}
                <span className="spacer" />
                {!m.installed && (
                  <Button size="sm" variant="primary" icon={Download} onClick={() => setSelected(m)} aria-label={`Instalar ${m.name}`}>Instalar</Button>
                )}
              </div>
            </article>
          ))}
        </div>
      )}
      <InstallDialog module={selected} onClose={() => setSelected(null)} onInstalled={() => { setSelected(null); reload(); }} />
    </div>
  );
}

function InstallDialog({ module: m, onClose, onInstalled }: { module: ModuleInfo | null; onClose: () => void; onInstalled: () => void }) {
  const toast = useToast();
  const { refresh } = useAuth();
  const { data: preview, loading, error } = useAsync<ModulePreview | undefined>(
    () => (m ? adminApi.previewModule(m.id) : Promise.resolve(undefined)),
    [m?.id],
  );
  const [installing, setInstalling] = useState(false);
  const [installError, setInstallError] = useState<ApiError | null>(null);

  async function install() {
    if (!m) return;
    setInstalling(true);
    setInstallError(null);
    try {
      const r = await adminApi.installModule(m.id);
      toast.success('Módulo instalado', `${m.name} · conjunto de cambios #${r.changeSetId}`);
      await refresh();
      onInstalled();
    } catch (e) {
      setInstallError(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setInstalling(false);
    }
  }

  const err = installError ?? (error instanceof ApiError ? error : null);
  return (
    <Dialog
      open={!!m}
      onClose={() => { if (!installing) { setInstallError(null); onClose(); } }}
      size="lg"
      title={m ? `Instalar ${m.name}` : ''}
      description="Revisa lo que se va a crear. La instalación se aplica en una sola transacción."
      footer={
        <>
          <Button onClick={onClose} disabled={installing}>Cancelar</Button>
          <Button variant="primary" icon={Download} loading={installing} disabled={!preview?.valid || loading} onClick={install}>Instalar módulo</Button>
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
          <PlanPreviewView preview={preview} validText="Modelo, roles, permisos y datos iniciales se aplican en una sola transacción. Si algo falla, no se instala nada." />
        </div>
      )}
    </Dialog>
  );
}
