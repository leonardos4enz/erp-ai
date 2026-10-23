import { Fragment, useState } from 'react';
import { ChevronDown, ChevronLeft, ChevronRight, History } from 'lucide-react';
import { ApiError } from '../api/client';
import { auditApi, modelApi } from '../api/endpoints';
import type { AuditEntry, EntityDef, Paged } from '../api/types';
import { Alert, Badge, Button, EmptyState, LoadingBlock, PageHeader, Select } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useAsync } from '../lib/hooks';

const PAGE_SIZE = 50;

function asObject(v: unknown): Record<string, unknown> | null {
  if (v == null) return null;
  if (typeof v === 'string') {
    try { const parsed = JSON.parse(v); return typeof parsed === 'object' && parsed ? parsed : { valor: v }; } catch { return { valor: v }; }
  }
  return typeof v === 'object' ? (v as Record<string, unknown>) : { valor: v };
}

function show(v: unknown): string {
  if (v === undefined) return '';
  if (v === null) return 'null';
  return typeof v === 'object' ? JSON.stringify(v) : String(v);
}

function Diff({ before, after }: { before: unknown; after: unknown }) {
  const b = asObject(before);
  const a = asObject(after);
  const keys = Array.from(new Set([...Object.keys(b ?? {}), ...Object.keys(a ?? {})]));
  if (keys.length === 0) return <span className="subtle">Sin detalle.</span>;
  return (
    <div className="diff" role="table" aria-label="Comparación antes y después">
      <div className="diff-head" role="columnheader">Campo</div>
      <div className="diff-head" role="columnheader">Antes</div>
      <div className="diff-head" role="columnheader">Después</div>
      {keys.map((k) => {
        const bv = b?.[k];
        const av = a?.[k];
        const changed = show(bv) !== show(av);
        const cls = changed ? 'changed' : '';
        return (
          <Fragment key={k}>
            <div className={`diff-key ${cls}`} role="cell">{k}</div>
            <div className={cls} role="cell">{bv === undefined ? <span className="diff-cell-empty">—</span> : show(bv)}</div>
            <div className={cls} role="cell">{av === undefined ? <span className="diff-cell-empty">—</span> : show(av)}</div>
          </Fragment>
        );
      })}
    </div>
  );
}

function actionTone(action: string): 'success' | 'danger' | 'warning' | 'accent' | 'neutral' {
  const a = action.toLowerCase();
  if (a.includes('create') || a.includes('insert')) return 'success';
  if (a.includes('delete') || a.includes('archive')) return 'danger';
  if (a.includes('update')) return 'warning';
  if (a.includes('login') || a.includes('model')) return 'accent';
  return 'neutral';
}

export function AuditPage() {
  const [entity, setEntity] = useState('');
  const [page, setPage] = useState(1);
  const [expanded, setExpanded] = useState<number | null>(null);
  const { data: entities } = useAsync<EntityDef[]>(() => modelApi.entities(true), []);
  const { data, loading, error } = useAsync<Paged<AuditEntry>>(
    () => auditApi.list({ entity: entity || undefined, page, pageSize: PAGE_SIZE }),
    [entity, page],
  );
  const pages = Math.max(1, Math.ceil((data?.total ?? 0) / PAGE_SIZE));

  return (
    <div className="page">
      <PageHeader icon={History} title="Auditoría" subtitle="Registro inmutable de cambios en datos y en el modelo." />
      <div className="toolbar">
        <Select aria-label="Filtrar por catálogo" value={entity} onChange={(e) => { setEntity(e.target.value); setPage(1); setExpanded(null); }} style={{ width: 240 }}>
          <option value="">Todos los catálogos</option>
          {(entities ?? []).map((e) => <option key={e.name} value={e.name}>{e.labelPlural}</option>)}
        </Select>
        <span className="spacer" />
        {data && <span className="muted" style={{ fontSize: 'var(--text-sm)' }}>{data.total.toLocaleString('es-MX')} eventos</span>}
      </div>
      {error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar la auditoría'} />
      ) : (
        <div className="table-wrap">
          {loading && !data ? <LoadingBlock /> : !data?.items.length ? (
            <EmptyState icon={History} title="Sin eventos" description="Aquí aparecerán las altas, cambios y bajas." />
          ) : (
            <>
              <div className="table-scroll">
                <table className="table">
                  <thead><tr><th style={{ width: 36 }}><span className="sr-only">Expandir</span></th><th>Fecha</th><th>Usuario</th><th>Acción</th><th>Catálogo</th><th>Registro</th></tr></thead>
                  <tbody>
                    {data.items.map((a) => {
                      const isOpen = expanded === a.id;
                      return (
                        <Fragment key={a.id}>
                          <tr className="clickable" onClick={() => setExpanded(isOpen ? null : a.id)}>
                            <td>
                              <Button
                                variant="ghost" size="sm" iconOnly icon={isOpen ? ChevronDown : ChevronRight}
                                aria-label={isOpen ? 'Ocultar detalle' : 'Ver detalle'} aria-expanded={isOpen}
                                onClick={(e) => { e.stopPropagation(); setExpanded(isOpen ? null : a.id); }}
                              />
                            </td>
                            <td style={{ whiteSpace: 'nowrap' }}>{formatDateTime(a.at)}</td>
                            <td>{a.user}</td>
                            <td><Badge tone={actionTone(a.action)}>{a.action}</Badge></td>
                            <td>{a.entityName}</td>
                            <td className="mono subtle">{a.recordId ?? '—'}</td>
                          </tr>
                          {isOpen && (
                            <tr className="expand-row">
                              <td colSpan={6}><Diff before={a.before} after={a.after} /></td>
                            </tr>
                          )}
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <div className="pagination">
                <span className="spacer" />
                <span>Página {page} de {pages}</span>
                <Button size="sm" iconOnly icon={ChevronLeft} aria-label="Página anterior" disabled={page <= 1} onClick={() => setPage(page - 1)} />
                <Button size="sm" iconOnly icon={ChevronRight} aria-label="Página siguiente" disabled={page >= pages} onClick={() => setPage(page + 1)} />
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
