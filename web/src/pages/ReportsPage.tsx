import { useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { BarChart3, FileSpreadsheet, FileText, Filter, Inbox } from 'lucide-react';
import { ApiError } from '../api/client';
import { reportsApi } from '../api/endpoints';
import type { ReportSummary, ViewFilter } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { FilterChips, FilterDialog } from '../components/FilterBuilder';
import { ReportChart } from '../components/ReportChart';
import { ReportTable } from '../components/ReportTable';
import { Alert, Button, EmptyState, LoadingBlock, PageHeader, Spinner } from '../components/ui';
import { useAsync } from '../lib/hooks';
import { useEntityDef } from '../lib/entity';
import { useApps } from '../lib/apps';
import { NotFoundPage } from './StatusPages';

/** Spec 007: reportes disponibles para el usuario, agrupados por módulo. Spec 024 (H4): `?app=` los acota a un módulo. */
export function ReportsPage() {
  const { data, loading, error } = useAsync<ReportSummary[]>((s) => reportsApi.list(s), []);
  const [params] = useSearchParams();
  const { appOfGroup, find } = useApps();
  const only = find(params.get('app'));
  const groups = useMemo(() => {
    const out = new Map<string, ReportSummary[]>();
    for (const r of data ?? []) {
      const app = appOfGroup(r.group);
      if (only && app?.name !== only.name) continue;
      const g = app?.label ?? (r.group || 'General');
      out.set(g, [...(out.get(g) ?? []), r]);
    }
    return [...out.entries()];
  }, [data, appOfGroup, only]);

  return (
    <div className="page">
      <PageHeader icon={BarChart3} title={only ? `Reportes de ${only.label}` : 'Reportes'}
        subtitle="Agrupaciones y totales calculados con los registros que puedes ver."
        actions={only && <Link className="btn btn-secondary" to="/reports">Ver todos los reportes</Link>} />
      {loading && !data ? <LoadingBlock />
        : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los reportes'} />
        : !groups.length ? (
          <div className="card"><EmptyState icon={Inbox} title="No hay reportes" description="Cuando un modelador defina reportes para los catálogos que puedes consultar, aparecerán aquí." /></div>
        ) : groups.map(([g, list], gi) => (
          <section key={g} className="stack" style={{ marginBottom: 'var(--space-6)' }} aria-labelledby={`rg-${gi}`}>
            <h2 id={`rg-${gi}`} className="section-title">{g}</h2>
            <div className="report-grid">
              {list.map((r) => (
                <Link key={`${r.entity}.${r.name}`} to={`/reports/${encodeURIComponent(r.entity)}/${encodeURIComponent(r.name)}`} className="card entity-card">
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
        ))}
    </div>
  );
}

function parseFilters(raw: string | null): ViewFilter[] {
  try { const v: unknown = raw ? JSON.parse(raw) : []; return Array.isArray(v) ? (v as ViewFilter[]) : []; } catch { return []; }
}

/** Un reporte: filtros, gráfica, tabla con total y exportación a CSV/Excel. */
export function ReportPage() {
  const { entity = '', name = '' } = useParams();
  const [params, setParams] = useSearchParams();
  const filtersRaw = params.get('filters');
  const filters = useMemo(() => parseFilters(filtersRaw), [filtersRaw]);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const { entities } = useAuth();
  // Las partidas no están en el menú: el filtro se ofrece solo en reportes de catálogos con listado.
  const { def } = useEntityDef(entities.some((e) => e.name.toLowerCase() === entity.toLowerCase()) ? entity : undefined);
  const { data, loading, error } = useAsync((s) => reportsApi.run(entity, name, filters, s), [entity, name, filtersRaw]);

  const setFilters = (f: ViewFilter[]) => {
    const p = new URLSearchParams(params);
    if (f.length) p.set('filters', JSON.stringify(f)); else p.delete('filters');
    setParams(p, { replace: true });
  };

  if (error instanceof ApiError && error.status === 404) return <NotFoundPage />;
  if (loading && !data) return <LoadingBlock />;
  if (error || !data) return <div className="page"><Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar el reporte'}>{error instanceof ApiError ? error.detail : undefined}</Alert></div>;

  return (
    <div className="page">
      <PageHeader
        icon={BarChart3}
        title={data.label}
        subtitle={data.description ?? undefined}
        actions={
          <div className="report-actions">
            {def && <Button icon={Filter} onClick={() => setFiltersOpen(true)} aria-haspopup="dialog">Filtros{filters.length ? ` (${filters.length})` : ''}</Button>}
            <a className="btn btn-secondary" href={reportsApi.exportUrl(entity, name, 'csv', filters)} download><FileText size={16} aria-hidden /> CSV</a>
            <a className="btn btn-secondary" href={reportsApi.exportUrl(entity, name, 'xlsx', filters)} download><FileSpreadsheet size={16} aria-hidden /> Excel</a>
          </div>
        }
      />
      {def && <FilterChips def={def} filters={filters} onRemove={(i) => setFilters(filters.filter((_, j) => j !== i))} />}
      {loading && <Spinner size={16} />}
      {data.truncated && <Alert tone="warning" title="Se muestran solo los primeros grupos">El reporte tiene más resultados de los que se muestran; agrega filtros para acotarlo.</Alert>}
      {data.rows.length === 0 && data.columns.some((c) => c.kind === 'group') ? (
        <div className="card"><EmptyState icon={Inbox} title="Sin datos" description={filters.length ? 'Ningún registro cumple los filtros.' : 'Todavía no hay registros que sumar.'} /></div>
      ) : (
        <div className="stack">
          {data.chart && <section className="card card-pad" aria-label="Gráfica"><ReportChart result={data} kind={data.chart} /></section>}
          <section className="card" aria-label="Datos del reporte"><ReportTable result={data} caption={data.label} /></section>
        </div>
      )}
      {def && <FilterDialog open={filtersOpen} def={def} filters={filters} onClose={() => setFiltersOpen(false)} onApply={(f) => { setFiltersOpen(false); setFilters(f); }} />}
    </div>
  );
}
