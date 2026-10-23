import { Link } from 'react-router-dom';
import { ArrowRight, LayoutGrid, Plus, Workflow } from 'lucide-react';
import { useAuth } from '../auth/AuthContext';
import { DynamicIcon } from '../components/DynamicIcon';
import { EmptyState, LoadingBlock, PageHeader } from '../components/ui';

function greeting(): string {
  const h = new Date().getHours();
  return h < 12 ? 'Buenos días' : h < 19 ? 'Buenas tardes' : 'Buenas noches';
}

export function HomePage() {
  const { me, entities, entitiesLoading } = useAuth();
  const canModel = !!(me?.canManageModel || me?.isSuperuser);
  const firstName = (me?.displayName || me?.username || '').split(' ')[0];

  return (
    <div className="page">
      <PageHeader
        title={`${greeting()}, ${firstName}`}
        subtitle="Estos son los catálogos a los que tienes acceso."
        actions={canModel && (
          <Link className="btn btn-secondary" to="/admin/model"><Workflow size={16} aria-hidden /> Abrir modelador</Link>
        )}
      />
      {entitiesLoading && entities.length === 0 ? (
        <LoadingBlock />
      ) : entities.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={LayoutGrid}
            title="Todavía no hay catálogos"
            description={canModel
              ? 'Crea tu primer catálogo desde el modelador. Aparecerá aquí y en el menú lateral al instante.'
              : 'Cuando un modelador cree catálogos y te dé permiso, aparecerán aquí.'}
            action={canModel && <Link className="btn btn-primary" to="/admin/model?new=1"><Plus size={16} aria-hidden /> Nuevo catálogo</Link>}
          />
        </div>
      ) : (
        <div className="grid-cards">
          {entities.map((e) => (
            <Link key={e.name} to={`/c/${encodeURIComponent(e.name)}`} className="card entity-card">
              <div className="entity-card-icon"><DynamicIcon name={e.icon} size={18} /></div>
              <div>
                <div className="entity-card-title">{e.labelPlural}</div>
                {e.description && <div className="entity-card-desc">{e.description}</div>}
              </div>
              <div className="entity-card-foot">
                <span>{e.fields.filter((f) => !f.isArchived).length} campos</span>
                <span className="spacer" />
                <ArrowRight size={15} aria-hidden />
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
