import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Archive, Boxes, History, Pencil, Plus, Workflow } from 'lucide-react';
import { ApiError } from '../api/client';
import { modelApi } from '../api/endpoints';
import type { ChangePlan, ChangeSetSummary, EntityDef } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DynamicIcon } from '../components/DynamicIcon';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader, Tabs } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';
import { EntityEditor } from './modeler/EntityEditor';
import { PlanReviewDialog } from './modeler/PlanReviewDialog';

type Tab = 'entities' | 'history';

function HistoryTab() {
  const { data, loading, error } = useAsync<ChangeSetSummary[]>(() => modelApi.changes(), []);
  if (loading) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar el historial'} />;
  if (!data?.length) return <div className="card"><EmptyState icon={History} title="Sin cambios registrados" description="Cada plan aplicado al modelo aparecerá aquí." /></div>;
  return (
    <div className="table-wrap">
      <div className="table-scroll">
        <table className="table">
          <thead><tr><th>#</th><th>Descripción</th><th className="td-num">Operaciones</th><th>Aplicado por</th><th>Fecha</th></tr></thead>
          <tbody>
            {data.map((c) => (
              <tr key={c.id}>
                <td className="subtle mono">{c.id}</td>
                <td>{c.description}</td>
                <td className="td-num">{c.operationCount}</td>
                <td>{c.appliedBy}</td>
                <td className="subtle" style={{ whiteSpace: 'nowrap' }}>{formatDateTime(c.appliedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function ModelerPage() {
  const toast = useToast();
  const { reloadEntities } = useAuth();
  const [params, setParams] = useSearchParams();
  const [tab, setTab] = useState<Tab>('entities');
  const { data: entities, loading, error, reload } = useAsync<EntityDef[]>(() => modelApi.entities(true), []);
  const [plan, setPlan] = useState<ChangePlan | null>(null);

  const editing = params.get('edit');
  const creating = params.get('new') === '1';
  const original = editing ? entities?.find((e) => e.name === editing) : undefined;
  const editorOpen = creating || !!original;

  function closeEditor() {
    const p = new URLSearchParams(params);
    p.delete('new'); p.delete('edit');
    setParams(p, { replace: true });
  }

  async function applied(id: number) {
    setPlan(null);
    toast.success('Cambios aplicados', `Conjunto de cambios #${id}`);
    closeEditor();
    reload();
    await reloadEntities();
  }

  if (editorOpen && entities) {
    return (
      <div className="page page-narrow">
        <PageHeader
          icon={Workflow}
          title={original ? `Editar ${original.labelPlural}` : 'Nuevo catálogo'}
          subtitle={original ? 'Los cambios se revisan antes de aplicarse. Los campos archivados conservan sus datos.' : 'Define los datos que quieres administrar. No necesitas programar.'}
        />
        <EntityEditor
          key={original?.name ?? 'new'}
          original={original}
          entities={entities}
          onCancel={closeEditor}
          onReview={setPlan}
        />
        <PlanReviewDialog plan={plan} onClose={() => setPlan(null)} onApplied={applied} />
      </div>
    );
  }

  const list = entities ?? [];
  return (
    <div className="page">
      <PageHeader
        icon={Workflow}
        title="Modelador"
        subtitle="Crea y modifica catálogos sin programar. Cada cambio se valida, se previsualiza y queda registrado."
        actions={<Button variant="primary" icon={Plus} onClick={() => setParams({ new: '1' })}>Nuevo catálogo</Button>}
      />
      <Tabs<Tab>
        label="Secciones del modelador"
        value={tab}
        onChange={setTab}
        items={[{ id: 'entities', label: 'Catálogos', icon: Boxes }, { id: 'history', label: 'Historial', icon: History }]}
      />
      {tab === 'history' ? <HistoryTab /> : loading ? <LoadingBlock /> : error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar el modelo'} />
      ) : list.length === 0 ? (
        <div className="card">
          <EmptyState icon={Boxes} title="Aún no hay catálogos" description="Empieza creando uno, por ejemplo Clientes o Empaques." action={<Button variant="primary" icon={Plus} onClick={() => setParams({ new: '1' })}>Nuevo catálogo</Button>} />
        </div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Catálogo</th><th>Identificador</th><th className="td-num">Campos</th><th>Estado</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {list.map((e) => (
                  <tr key={e.name}>
                    <td>
                      <div className="row" style={{ gap: 'var(--space-3)' }}>
                        <span className="entity-card-icon" style={{ width: 30, height: 30 }}><DynamicIcon name={e.icon} size={15} /></span>
                        <div>
                          <div style={{ fontWeight: 500 }}>{e.labelPlural}</div>
                          {e.description && <div className="subtle td-truncate" style={{ fontSize: 'var(--text-sm)' }}>{e.description}</div>}
                        </div>
                      </div>
                    </td>
                    <td className="mono muted">{e.name}</td>
                    <td className="td-num">{e.fields.filter((f) => !f.isArchived).length}</td>
                    <td>{e.isArchived ? <Badge tone="warning">Archivado</Badge> : <Badge tone="success">Activo</Badge>}</td>
                    <td className="td-actions">
                      {!e.isArchived && (
                        <div className="row" style={{ justifyContent: 'flex-end', gap: 2 }}>
                          <Button variant="ghost" size="sm" icon={Pencil} onClick={() => setParams({ edit: e.name })}>Editar</Button>
                          <Button variant="ghost" size="sm" iconOnly icon={Archive} aria-label={`Archivar ${e.labelPlural}`} title="Archivar" onClick={() => setPlan({ description: `Archivar catálogo ${e.labelPlural}`, operations: [{ op: 'archiveEntity', entity: e.name }] })} />
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <PlanReviewDialog plan={plan} onClose={() => setPlan(null)} onApplied={applied} />
    </div>
  );
}
