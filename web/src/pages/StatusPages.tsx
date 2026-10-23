import { Link } from 'react-router-dom';
import { Compass, ShieldOff } from 'lucide-react';
import { EmptyState } from '../components/ui';

export function NotFoundPage() {
  return (
    <div className="page">
      <EmptyState
        icon={Compass}
        title="Página no encontrada"
        description="La dirección no existe o el catálogo fue archivado."
        action={<Link className="btn btn-secondary" to="/">Ir al inicio</Link>}
      />
    </div>
  );
}

export function ForbiddenPage() {
  return (
    <div className="page">
      <EmptyState
        icon={ShieldOff}
        title="Sin acceso"
        description="Tu rol no tiene permiso para ver esta sección. Pide acceso a un administrador."
        action={<Link className="btn btn-secondary" to="/">Ir al inicio</Link>}
      />
    </div>
  );
}
