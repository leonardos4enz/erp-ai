import { Link } from 'react-router-dom';
import { Settings } from 'lucide-react';
import { useAuth } from '../auth/AuthContext';
import { PageHeader } from '../components/ui';
import { adminGroupsFor } from '../lib/adminNav';
import { ForbiddenPage } from './StatusPages';

/** Spec 024 (H3): entrada al área de configuración, con las mismas páginas y capacidades que su menú lateral. */
export function AdminHomePage() {
  const { me } = useAuth();
  const groups = adminGroupsFor(me);
  if (groups.length === 0) return <ForbiddenPage />;
  return (
    <div className="page">
      <PageHeader icon={Settings} title="Configuración" subtitle="Modelo, seguridad, automatización e integraciones, y auditoría de este entorno." />
      {groups.map((g, gi) => (
        <section key={g.label} className="stack" style={{ marginBottom: 'var(--space-8)' }} aria-labelledby={`admin-g-${gi}`}>
          <h2 id={`admin-g-${gi}`} className="section-title">{g.label}</h2>
          <div className="grid-cards">
            {g.items.map((i) => (
              <Link key={i.to} to={i.to} className="card entity-card">
                <div className="entity-card-icon"><i.icon size={18} aria-hidden /></div>
                <div>
                  <div className="entity-card-title">{i.label}</div>
                  <div className="entity-card-desc">{i.description}</div>
                </div>
              </Link>
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}
