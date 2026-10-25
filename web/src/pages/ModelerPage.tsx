import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Archive, ArchiveRestore, Boxes, History, LayoutGrid, Package, Pencil, Plus, Search, Workflow, X } from 'lucide-react';
import { ApiError } from '../api/client';
import { modelApi } from '../api/endpoints';
import type { AppSummary, ChangePlan, ChangeSetSummary, EntityDef, EntityOrigin } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DynamicIcon } from '../components/DynamicIcon';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, Input, LoadingBlock, PageHeader, Select, Tabs } from '../components/ui';
import { GENERAL_APP } from '../lib/apps';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';
import { EntityEditor, editorTab, wideTab } from './modeler/EntityEditor';
import { PlanReviewDialog } from './modeler/PlanReviewDialog';
import { AppsTab } from './modeler/AppsTab';

type Tab = 'entities' | 'apps' | 'history';

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

/** Módulo de navegación de un catálogo: su `group` (024); una partida va con su documento. */
function appOf(e: EntityDef, all: EntityDef[]): string {
  const root = e.isChild ? all.find((x) => x.name === e.parentEntity) ?? e : e;
  return root.group?.trim() || GENERAL_APP;
}

interface CatalogsProps {
  list: EntityDef[];
  apps: AppSummary[];
  origins: Map<string, EntityOrigin> | null;
  onEdit: (name: string) => void;
  onPlan: (p: ChangePlan) => void;
  onNew: () => void;
}

/** Spec 026 (H1): catálogos agrupados por módulo, con filtro y búsqueda en la URL y quién instaló cada uno. */
function CatalogsTab({ list, apps, origins, onEdit, onPlan, onNew }: CatalogsProps) {
  const [params, setParams] = useSearchParams();
  const app = params.get('modulo') ?? '';
  const q = params.get('q') ?? '';
  const setParam = (k: 'modulo' | 'q', v: string) => {
    const p = new URLSearchParams(params);
    if (v) p.set(k, v); else p.delete(k);
    setParams(p, { replace: true });
  };

  const groups = useMemo(() => {
    const order = new Map(apps.map((a, i) => [a.name.toLowerCase(), { i: a.order * 1000 + i, label: a.label }]));
    const byApp = new Map<string, EntityDef[]>();
    for (const e of list) {
      const a = appOf(e, list);
      if (!byApp.has(a)) byApp.set(a, []);
      byApp.get(a)!.push(e);
    }
    return [...byApp.entries()]
      .map(([name, items]) => ({ name, label: order.get(name.toLowerCase())?.label ?? name, items: items.sort((x, y) => x.labelPlural.localeCompare(y.labelPlural, 'es')) }))
      // General va al final; los demás, en el orden de los módulos.
      .sort((a, b) => (a.name === GENERAL_APP ? 1 : 0) - (b.name === GENERAL_APP ? 1 : 0)
        || (order.get(a.name.toLowerCase())?.i ?? 1e9) - (order.get(b.name.toLowerCase())?.i ?? 1e9) || a.label.localeCompare(b.label, 'es'));
  }, [list, apps]);

  const needle = q.trim().toLowerCase();
  const visible = groups
    .filter((g) => !app || g.name.toLowerCase() === app.toLowerCase())
    .map((g) => ({ ...g, items: g.items.filter((e) => !needle || e.labelPlural.toLowerCase().includes(needle) || e.label.toLowerCase().includes(needle) || e.name.toLowerCase().includes(needle)) }))
    .filter((g) => g.items.length > 0);
  const shown = visible.reduce((n, g) => n + g.items.length, 0);

  if (list.length === 0) {
    return (
      <div className="card">
        <EmptyState icon={Boxes} title="Aún no hay catálogos" description="Empieza creando uno, por ejemplo Clientes o Empaques." action={<Button variant="primary" icon={Plus} onClick={onNew}>Nuevo catálogo</Button>} />
      </div>
    );
  }

  return (
    <div className="stack">
      <div className="catalog-filters" role="search" aria-label="Filtrar catálogos">
        <Select aria-label="Módulo" value={app} onChange={(e) => setParam('modulo', e.target.value)}>
          <option value="">Todos los módulos</option>
          {groups.map((g) => <option key={g.name} value={g.name}>{g.label} ({g.items.length})</option>)}
        </Select>
        <Input aria-label="Buscar catálogo" icon={Search} type="search" placeholder="Buscar por nombre o identificador" value={q} onChange={(e) => setParam('q', e.target.value)} />
        <span className="subtle catalog-count" aria-live="polite">{shown} {shown === 1 ? 'catálogo' : 'catálogos'}</span>
      </div>
      {visible.length === 0 ? (
        <div className="card">
          <EmptyState icon={Search} title="Ningún catálogo coincide" description="Prueba con otro texto o con otro módulo."
            action={<Button icon={X} onClick={() => setParams(new URLSearchParams(), { replace: true })}>Quitar filtros</Button>} />
        </div>
      ) : visible.map((g) => (
        <section key={g.name} className="catalog-group" aria-labelledby={`grp-${g.name}`}>
          <h2 className="catalog-group-title" id={`grp-${g.name}`}>{g.label} <span className="subtle">· {g.items.length} {g.items.length === 1 ? 'catálogo' : 'catálogos'}</span></h2>
          <div className="table-wrap">
            <div className="table-scroll">
              <table className="table">
                <thead><tr><th>Catálogo</th><th>Identificador</th><th>Origen</th><th className="td-num">Campos</th><th>Estado</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
                <tbody>
                  {g.items.map((e) => {
                    const o = origins?.get(e.name);
                    const others = (o?.packages ?? []).filter((p) => p.id !== o?.installedBy?.id);
                    return (
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
                        <td>
                          {!origins ? <span className="subtle">…</span> : (
                            <div className="catalog-origin">
                              {o?.installedBy
                                ? <span className="origin-chip"><Package size={11} aria-hidden />{o.installedBy.name} {o.installedBy.version}</span>
                                : <span className="origin-chip origin-local" title="Creado en este entorno (modelador, asistente o paquete de Entornos)">Local</span>}
                              {others.length > 0 && <span className="subtle catalog-also">También: {others.map((p) => p.name).join(', ')}</span>}
                            </div>
                          )}
                        </td>
                        <td className="td-num">{e.fields.filter((f) => !f.isArchived).length}</td>
                        <td>
                          <div className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                            {e.isArchived ? <Badge tone="warning">Archivado</Badge> : <Badge tone="success">Activo</Badge>}
                            {e.isChild && <Badge>Detalle de {list.find((x) => x.name === e.parentEntity)?.label ?? e.parentEntity}</Badge>}
                            {e.workflow && <Badge tone="accent">Con proceso</Badge>}
                          </div>
                        </td>
                        <td className="td-actions">
                          {e.isArchived ? (
                            <div className="row" style={{ justifyContent: 'flex-end', gap: 2 }}>
                              {/* Spec 017 (H4 CA3): vuelve con los registros que conservó su tabla. */}
                              <Button variant="ghost" size="sm" icon={ArchiveRestore} aria-label={`Reactivar ${e.labelPlural}`}
                                onClick={() => onPlan({ description: `Reactivar catálogo ${e.labelPlural}`, operations: [{ op: 'restoreEntity', entity: e.name }] })}>Reactivar</Button>
                            </div>
                          ) : (
                            <div className="row" style={{ justifyContent: 'flex-end', gap: 2 }}>
                              <Button variant="ghost" size="sm" icon={Pencil} aria-label={`Editar ${e.labelPlural}`} onClick={() => onEdit(e.name)}>Editar</Button>
                              <Button variant="ghost" size="sm" iconOnly icon={Archive} aria-label={`Archivar ${e.labelPlural}`} title="Archivar" onClick={() => onPlan({ description: `Archivar catálogo ${e.labelPlural}`, operations: [{ op: 'archiveEntity', entity: e.name }] })} />
                            </div>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        </section>
      ))}
    </div>
  );
}

export function ModelerPage() {
  const toast = useToast();
  const { reloadEntities } = useAuth();
  const [params, setParams] = useSearchParams();
  // Spec 024: `?section=apps` abre la pestaña de módulos (enlace desde el área de configuración).
  const [tab, setTab] = useState<Tab>(() => (params.get('section') === 'apps' ? 'apps' : 'entities'));
  const [appsVersion, setAppsVersion] = useState(0);
  const { data: entities, loading, error, reload } = useAsync<EntityDef[]>(() => modelApi.entities(true, true), []);
  const { data: apps } = useAsync<AppSummary[]>((signal) => modelApi.apps(true, signal), [appsVersion]);
  const { data: provenance } = useAsync<{ entities: EntityOrigin[] }>((signal) => modelApi.provenance(signal), [appsVersion]);
  const origins = useMemo(() => (provenance ? new Map(provenance.entities.map((o) => [o.entity, o])) : null), [provenance]);
  const [plan, setPlan] = useState<ChangePlan | null>(null);

  const editing = params.get('edit');
  const creating = params.get('new') === '1';
  const original = editing ? entities?.find((e) => e.name === editing) : undefined;
  const editorOpen = creating || !!original;

  function closeEditor() {
    const p = new URLSearchParams(params);
    p.delete('new'); p.delete('edit'); p.delete('tab'); p.delete('app');
    setParams(p, { replace: true });
  }

  async function applied(id: number) {
    setPlan(null);
    toast.success('Cambios aplicados', `Conjunto de cambios #${id}`);
    closeEditor();
    reload();
    setAppsVersion((v) => v + 1);
    await reloadEntities();
  }

  if (editorOpen && entities) {
    const wide = wideTab(editorTab(params.get('tab')));
    return (
      <div className={wide ? 'page' : 'page page-narrow'}>
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
        items={[{ id: 'entities', label: 'Catálogos', icon: Boxes }, { id: 'apps', label: 'Módulos', icon: LayoutGrid }, { id: 'history', label: 'Historial', icon: History }]}
      />
      {tab === 'history' ? <HistoryTab /> : tab === 'apps' ? <AppsTab version={appsVersion} onReview={setPlan} /> : loading ? <LoadingBlock /> : error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar el modelo'} />
      ) : (
        <CatalogsTab list={entities ?? []} apps={apps ?? []} origins={origins} onEdit={(name) => { const p = new URLSearchParams(params); p.set('edit', name); setParams(p); }} onPlan={setPlan} onNew={() => setParams({ new: '1' })} />
      )}
      <PlanReviewDialog plan={plan} onClose={() => setPlan(null)} onApplied={applied} />
    </div>
  );
}
