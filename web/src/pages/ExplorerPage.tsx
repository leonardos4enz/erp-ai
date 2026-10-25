import { useSearchParams } from 'react-router-dom';
import { Database, IdCard, Network, SearchCode, ShieldCheck, Waypoints, Zap } from 'lucide-react';
import { PageHeader, Tabs, type TabItem } from '../components/ui';
import type { ExplorerView } from './explorer/common';
import { DatabaseView } from './explorer/DatabaseView';
import { RelationsView } from './explorer/RelationsView';
import { EntityCardView } from './explorer/EntityCardView';
import { UsagesView } from './explorer/UsagesView';
import { AutomationsView } from './explorer/AutomationsView';
import { SecurityView } from './explorer/SecurityView';

const VIEWS: TabItem<ExplorerView>[] = [
  { id: 'bd', label: 'Base de datos', icon: Database },
  { id: 'relaciones', label: 'Relaciones', icon: Waypoints },
  { id: 'ficha', label: 'Ficha', icon: IdCard },
  { id: 'usos', label: '¿Dónde se usa?', icon: SearchCode },
  { id: 'automatizaciones', label: 'Automatizaciones', icon: Zap },
  { id: 'seguridad', label: 'Seguridad y navegación', icon: ShieldCheck },
];

/**
 * Spec 025: explorador del modelo y de la base de datos. Solo lectura: las ediciones siguen por el modelador y el Plan de
 * Cambios. La vista, el catálogo y el campo viajan en la URL para que cada vista sea un enlace guardable.
 */
export function ExplorerPage() {
  const [params, setParams] = useSearchParams();
  const view = (VIEWS.find((v) => v.id === params.get('vista'))?.id ?? 'bd') as ExplorerView;
  const entity = params.get('catalogo') ?? undefined;
  const field = params.get('campo') ?? undefined;

  const go = (v: ExplorerView, e?: string, f?: string) => {
    const p = new URLSearchParams({ vista: v });
    if (e) p.set('catalogo', e);
    if (f) p.set('campo', f);
    setParams(p);
  };

  return (
    <div className="page ex-page">
      <PageHeader icon={Network} title="Explorador del modelo"
        subtitle="Cómo está construido el sistema: tablas reales, relaciones, usos, automatizaciones y seguridad. Solo lectura; los cambios se hacen en el modelador." />
      <Tabs label="Vistas del explorador" items={VIEWS} value={view} onChange={(v) => go(v, entity)} />
      {view === 'bd' && <DatabaseView entity={entity} onEntity={(e) => go('bd', e)} onOpen={go} />}
      {view === 'relaciones' && <RelationsView entity={entity} onEntity={(e) => go('relaciones', e)} onOpen={go} />}
      {view === 'ficha' && <EntityCardView entity={entity} onEntity={(e) => go('ficha', e)} onOpen={go} />}
      {view === 'usos' && <UsagesView entity={entity} field={field} onChange={(e, f) => go('usos', e, f)} />}
      {view === 'automatizaciones' && <AutomationsView entity={entity} onEntity={(e) => go('automatizaciones', e)} onOpen={go} />}
      {view === 'seguridad' && <SecurityView />}
    </div>
  );
}
