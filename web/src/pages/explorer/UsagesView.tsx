import { useMemo } from 'react';
import { Link } from 'react-router-dom';
import { PencilRuler, SearchCode } from 'lucide-react';
import { ApiError } from '../../api/client';
import { explorerApi, type RelGraph, type Usage, type UsageKind } from '../../api/explorer';
import { Alert, Badge, EmptyState, LoadingBlock, Select } from '../../components/ui';
import { useAsync } from '../../lib/hooks';
import { modelerHref } from './common';

const KINDS: Record<UsageKind, string> = {
  formula: 'Fórmula', default: 'Valor por defecto', rule: 'Regla', transition: 'Transición', layout: 'Formulario',
  automation: 'Automatización', schedule: 'Tarea programada', webhook: 'Webhook', access: 'Seguridad', view: 'Vista',
  report: 'Reporte', dashboard: 'Tablero', lookup: 'Filtro del buscador', ledger: 'Contabilidad', display: 'Campo que lo representa',
  reference: 'Referencia', print: 'Plantilla de impresión',
};

interface Props { entity?: string; field?: string; onChange: (entity?: string, field?: string) => void }

export function UsagesView({ entity, field, onChange }: Props) {
  const list = useAsync<RelGraph>((s) => explorerApi.relations({}, s), []);
  const node = list.data?.nodes.find((n) => n.name.toLowerCase() === entity?.toLowerCase());
  const usages = useAsync<Usage[] | null>((s) => (entity ? explorerApi.usages(entity, field, s) : Promise.resolve(null)), [entity, field]);
  const label = (n: string) => list.data?.nodes.find((x) => x.name === n)?.labelPlural ?? n;
  const fieldLabel = (e: string, f?: string | null) => (f ? list.data?.nodes.find((x) => x.name === e)?.fields.find((x) => x.name === f)?.label ?? f : null);

  const groups = useMemo(() => {
    const m = new Map<UsageKind, Usage[]>();
    for (const u of usages.data ?? []) m.set(u.kind, [...(m.get(u.kind) ?? []), u]);
    return [...m.entries()].sort((a, b) => KINDS[a[0]].localeCompare(KINDS[b[0]]));
  }, [usages.data]);

  const n = usages.data?.length ?? 0;
  const subject = node ? (field ? `el campo ${fieldLabel(node.name, field)} de ${node.labelPlural}` : `${node.labelPlural} o alguno de sus campos`) : '';
  const summary = n === 0 ? `Nada menciona ${subject}.` : `${n} ${n === 1 ? 'mención' : 'menciones'}: ${subject}.`;
  return (
    <div className="stack">
      <div className="ex-filters">
        <Select label="Catálogo" selectSize="sm" value={node?.name ?? ''} onChange={(e) => onChange(e.target.value || undefined)}>
          <option value="">Elige un catálogo…</option>
          {list.data?.nodes.map((x) => <option key={x.name} value={x.name}>{x.labelPlural}{x.isChild ? ' (partida)' : ''}</option>)}
        </Select>
        <Select label="Campo" selectSize="sm" value={field ?? ''} disabled={!node} onChange={(e) => onChange(node?.name, e.target.value || undefined)}>
          <option value="">Todo el catálogo</option>
          {node?.fields.map((f) => <option key={f.name} value={f.name}>{f.label} ({f.name})</option>)}
        </Select>
      </div>
      {!entity && <div className="card"><EmptyState icon={SearchCode} title="¿Qué quieres cambiar?" description="Elige un catálogo o un campo para ver cada fórmula, regla, transición, automatización, reporte, vista, tablero, webhook y regla de acceso que lo menciona, antes de cambiarlo." /></div>}
      {entity && usages.loading && <LoadingBlock />}
      {!!usages.error && <Alert tone="danger" title={usages.error instanceof ApiError ? usages.error.title : 'No se pudieron calcular los usos'} />}
      {usages.data && (
        <p aria-live="polite" className="ex-count">
          {summary}
        </p>
      )}
      {groups.map(([kind, items]) => (
        <section key={kind} className="card card-pad stack" aria-labelledby={`ex-u-${kind}`}>
          <h3 className="ex-h3" id={`ex-u-${kind}`}>{KINDS[kind]} <span className="subtle">({items.length})</span></h3>
          <ul className="ex-list">
            {items.map((u, i) => (
              <li key={i} className="ex-usage">
                <div className="row ex-wrap">
                  <strong>{label(u.entity)}</strong>
                  <span>{u.location}</span>
                  {u.target.field && (u.target.entity !== entity || !field) && <Badge>{label(u.target.entity)}.{fieldLabel(u.target.entity, u.target.field)}</Badge>}
                  <span className="grow" />
                  {u.kind !== 'dashboard' && (
                    <Link className="btn btn-ghost btn-sm" to={modelerHref(u.entity, u.tab)}>
                      <PencilRuler size={15} aria-hidden />Abrir en el modelador<span className="sr-only">: {label(u.entity)}, {u.location}</span>
                    </Link>
                  )}
                </div>
                {u.expression && <code className="ex-expr ex-code">{u.expression}</code>}
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
