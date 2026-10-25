import { Link } from 'react-router-dom';
import { ArrowRight, BarChart3, Gauge, LayoutGrid, Plus, Workflow } from 'lucide-react';
import { dashboardsApi, reportsApi } from '../api/endpoints';
import { useAuth } from '../auth/AuthContext';
import { DynamicIcon } from '../components/DynamicIcon';
import { EmptyState, LoadingBlock, PageHeader } from '../components/ui';
import { useApps } from '../lib/apps';
import { useAsync } from '../lib/hooks';

function greeting(): string {
  const h = new Date().getHours();
  return h < 12 ? 'Buenos días' : h < 19 ? 'Buenas tardes' : 'Buenas noches';
}

const count = (n: number) => `${n} ${n === 1 ? 'catálogo' : 'catálogos'}`;

/** Spec 024 (H1 CA1): selector de módulos. Las tarjetas salen de los módulos que el servidor ya filtró para el usuario. */
export function HomePage() {
  const { me, entities } = useAuth();
  const { apps, loading } = useApps();
  const canModel = !!(me?.canManageModel || me?.isSuperuser);
  const firstName = (me?.displayName || me?.username || '').split(' ')[0];
  // H4 CA2: la vista general de análisis solo se ofrece si hay algo que ver (o si quien entra puede crear tableros).
  const { data: boards } = useAsync((s) => dashboardsApi.list(s), [me?.id, entities]);
  const { data: reports } = useAsync((s) => reportsApi.list(s), [me?.id, entities]);

  return (
    <div className="page">
      <PageHeader
        title={`${greeting()}, ${firstName}`}
        subtitle="Elige el módulo en el que vas a trabajar."
        actions={canModel && (
          <Link className="btn btn-secondary" to="/admin/model"><Workflow size={16} aria-hidden /> Abrir modelador</Link>
        )}
      />
      {loading && apps.length === 0 ? (
        <LoadingBlock />
      ) : apps.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={LayoutGrid}
            title="Todavía no hay catálogos"
            description={canModel
              ? 'Crea tu primer catálogo desde el modelador. Aparecerá aquí, dentro de su módulo, al instante.'
              : 'Cuando un modelador cree catálogos y te dé permiso, aparecerán aquí.'}
            action={canModel && <Link className="btn btn-primary" to="/admin/model?new=1"><Plus size={16} aria-hidden /> Nuevo catálogo</Link>}
          />
        </div>
      ) : (
        <section aria-labelledby="apps-title">
          <h2 id="apps-title" className="sr-only">Módulos</h2>
          <ul className="grid-cards app-grid" role="list">
            {apps.map((a) => (
              <li key={a.name}>
                <Link to={`/m/${encodeURIComponent(a.name)}`} className="card entity-card app-card">
                  <div className="app-card-icon"><DynamicIcon name={a.icon} size={22} /></div>
                  <div>
                    <div className="entity-card-title">{a.label}</div>
                    {a.description && <div className="entity-card-desc">{a.description}</div>}
                  </div>
                  <div className="entity-card-foot">
                    <span>{count(a.entities.length)}</span>
                    <span className="spacer" />
                    <ArrowRight size={15} aria-hidden />
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}
      {(!!boards?.length || !!reports?.length) && (
        <section className="stack" style={{ marginTop: 'var(--space-8)' }} aria-labelledby="general-analysis">
          <h2 id="general-analysis" className="section-title">Análisis general</h2>
          <div className="grid-cards">
            {!!boards?.length && (
              <Link to="/dashboards" className="card entity-card">
                <div className="entity-card-icon"><Gauge size={18} aria-hidden /></div>
                <div>
                  <div className="entity-card-title">Tableros</div>
                  <div className="entity-card-desc">Indicadores y gráficas de todos tus módulos.</div>
                </div>
              </Link>
            )}
            {!!reports?.length && (
              <Link to="/reports" className="card entity-card">
                <div className="entity-card-icon"><BarChart3 size={18} aria-hidden /></div>
                <div>
                  <div className="entity-card-title">Reportes</div>
                  <div className="entity-card-desc">Agrupaciones y totales de todos tus módulos.</div>
                </div>
              </Link>
            )}
          </div>
        </section>
      )}
    </div>
  );
}
