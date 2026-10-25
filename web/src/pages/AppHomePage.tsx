import { Link, useParams } from 'react-router-dom';
import { ArrowRight, BarChart3, Plus } from 'lucide-react';
import { dashboardsApi, reportsApi } from '../api/endpoints';
import type { EntityDef } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DynamicIcon, resolveIcon } from '../components/DynamicIcon';
import { LoadingBlock, PageHeader } from '../components/ui';
import { useApps } from '../lib/apps';
import { useAsync } from '../lib/hooks';
import { NotFoundPage } from './StatusPages';

function EntityCards({ title, id, list }: { title: string; id: string; list: EntityDef[] }) {
  if (!list.length) return null;
  return (
    <section className="stack" style={{ marginBottom: 'var(--space-8)' }} aria-labelledby={id}>
      <h2 id={id} className="section-title">{title}</h2>
      <div className="grid-cards">
        {list.map((e) => (
          <Link key={e.name} to={`/c/${encodeURIComponent(e.name)}`} className="card entity-card">
            <div className="entity-card-icon"><DynamicIcon name={e.icon} size={18} /></div>
            <div>
              <div className="entity-card-title">{e.labelPlural}</div>
              {e.description && <div className="entity-card-desc">{e.description}</div>}
            </div>
            <div className="entity-card-foot"><span className="spacer" /><ArrowRight size={15} aria-hidden /></div>
          </Link>
        ))}
      </div>
    </section>
  );
}

/**
 * Spec 024 (H1 CA2, H4 CA1): página de un módulo. Documentos y catálogos que el usuario puede leer, y su análisis
 * (reportes de sus catálogos y tableros con alguna tarjeta de ellos). Un módulo que el usuario no ve no existe para él.
 */
export function AppHomePage() {
  const { app: name } = useParams();
  const { entities, me } = useAuth();
  const { find, loading, appOfGroup } = useApps();
  const canModel = !!(me?.canManageModel || me?.isSuperuser);
  const app = find(name);
  const { data: boards } = useAsync((s) => dashboardsApi.list(s), [entities]);
  const { data: reports } = useAsync((s) => reportsApi.list(s), [entities]);

  if (!app) return loading ? <LoadingBlock /> : <NotFoundPage />;
  const own = app.entities.map((n) => entities.find((e) => e.name.toLowerCase() === n.toLowerCase())).filter((e): e is EntityDef => !!e);
  const docs = own.filter((e) => e.workflow);
  const catalogs = own.filter((e) => !e.workflow);
  const myBoards = (boards ?? []).filter((d) => d.apps?.some((a) => a.toLowerCase() === app.name.toLowerCase()));
  const myReports = (reports ?? []).filter((r) => appOfGroup(r.group)?.name === app.name);

  return (
    <div className="page">
      <PageHeader icon={resolveIcon(app.icon)} title={app.label} subtitle={app.description ?? undefined}
        actions={canModel && (
          <Link className="btn btn-secondary" to={`/admin/model?new=1&app=${encodeURIComponent(app.name)}`}><Plus size={16} aria-hidden /> Nuevo catálogo</Link>
        )} />
      <EntityCards title="Documentos" id="app-docs" list={docs} />
      <EntityCards title="Catálogos" id="app-catalogs" list={catalogs} />
      {(myBoards.length > 0 || myReports.length > 0) && (
        <section className="stack" aria-labelledby="app-analysis">
          <h2 id="app-analysis" className="section-title">Análisis</h2>
          <div className="grid-cards">
            {myBoards.map((d) => (
              <Link key={`d-${d.name}`} to={`/dashboards/${encodeURIComponent(d.name)}`} className="card entity-card">
                <div className="entity-card-icon"><DynamicIcon name={d.icon} size={18} /></div>
                <div>
                  <div className="entity-card-title">{d.label}</div>
                  <div className="entity-card-desc">{d.description ?? 'Tablero'}</div>
                </div>
              </Link>
            ))}
            {myReports.map((r) => (
              <Link key={`r-${r.entity}.${r.name}`} to={`/reports/${encodeURIComponent(r.entity)}/${encodeURIComponent(r.name)}`} className="card entity-card">
                <div className="entity-card-icon"><BarChart3 size={18} aria-hidden /></div>
                <div>
                  <div className="entity-card-title">{r.label}</div>
                  {r.description && <div className="entity-card-desc">{r.description}</div>}
                </div>
                <div className="entity-card-foot"><span>{r.entityLabel}</span></div>
              </Link>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
