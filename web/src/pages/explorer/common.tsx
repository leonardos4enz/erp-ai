import { Link } from 'react-router-dom';
import { ExternalLink, Network, PencilRuler } from 'lucide-react';
import type { AppRef, RelField } from '../../api/explorer';
import type { FieldType } from '../../api/types';
import { Badge } from '../../components/ui';
import { FIELD_TYPE_LABELS } from '../../lib/entity';

export type ExplorerView = 'bd' | 'relaciones' | 'ficha' | 'usos' | 'automatizaciones' | 'seguridad';

/** Índice de color (0–7) de cada módulo, en el orden del selector de módulos. */
export function appColors(apps: AppRef[]): Map<string, number> {
  return new Map(apps.map((a, i) => [a.name.toLowerCase(), i % 8]));
}
export const colorOf = (colors: Map<string, number>, app?: string | null) => (app ? colors.get(app.toLowerCase()) : undefined);

export function AppLegend({ apps, colors }: { apps: AppRef[]; colors: Map<string, number> }) {
  if (apps.length === 0) return null;
  return (
    <ul className="ex-legend" aria-label="Colores por módulo">
      {apps.map((a) => (
        <li key={a.name}><span className={`ex-swatch bg-c${colorOf(colors, a.name)}`} aria-hidden />{a.label}</li>
      ))}
    </ul>
  );
}

export const typeLabel = (type: string, resultType?: string | null) =>
  type === 'formula' && resultType ? `Fórmula (${FIELD_TYPE_LABELS[resultType as FieldType] ?? resultType})` : FIELD_TYPE_LABELS[type as FieldType] ?? type;

export function FieldBadges({ f }: { f: RelField }) {
  return (
    <span className="ex-badges">
      {f.required && <Badge tone="accent">Obligatorio</Badge>}
      {f.unique && <Badge tone="accent">Único</Badge>}
      {f.formula && <Badge>Fórmula</Badge>}
      {f.autonumber && <Badge>Folio</Badge>}
      {f.hasDefault && <Badge>Por defecto</Badge>}
      {f.state && <Badge tone="success">Estado</Badge>}
    </span>
  );
}

export const explorerHref = (view: ExplorerView, entity?: string, field?: string) => {
  const p = new URLSearchParams({ vista: view });
  if (entity) p.set('catalogo', entity);
  if (field) p.set('campo', field);
  return `/admin/explorer?${p.toString()}`;
};

export const modelerHref = (entity: string, tab?: string) =>
  `/admin/model?edit=${encodeURIComponent(entity)}${tab && tab !== 'catalogo' ? `&tab=${tab}` : ''}`;

/** Enlaces que llevan fuera del explorador (las ediciones siguen por el modelador). */
export function EntityLinks({ entity, label, isChild, card = true }: { entity: string; label: string; isChild?: boolean; card?: boolean }) {
  return (
    <div className="ex-links">
      {card && <Link className="btn btn-secondary btn-sm" to={explorerHref('ficha', entity)}><Network size={15} aria-hidden />Ver ficha<span className="sr-only"> de {label}</span></Link>}
      <Link className="btn btn-secondary btn-sm" to={modelerHref(entity)}><PencilRuler size={15} aria-hidden />Abrir en el modelador<span className="sr-only"> ({label})</span></Link>
      {!isChild && <Link className="btn btn-secondary btn-sm" to={`/c/${encodeURIComponent(entity)}`}><ExternalLink size={15} aria-hidden />Ver pantalla<span className="sr-only"> de {label}</span></Link>}
    </div>
  );
}
