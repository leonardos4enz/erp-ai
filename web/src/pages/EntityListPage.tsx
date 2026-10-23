import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Inbox, Plus, Search, SearchX } from 'lucide-react';
import { ApiError } from '../api/client';
import { dataApi } from '../api/endpoints';
import type { Paged, Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { resolveIcon } from '../components/DynamicIcon';
import { Alert, Button, EmptyState, Input, LoadingBlock, PageHeader, Select, Spinner } from '../components/ui';
import { activeFields, CellValue, useEntityDef } from '../lib/entity';
import { useAsync, useDebounced } from '../lib/hooks';
import { formatDateTime } from '../lib/format';
import { ForbiddenPage, NotFoundPage } from './StatusPages';

const PAGE_SIZES = [10, 25, 50, 100];

export function EntityListPage() {
  const { entity = '' } = useParams();
  const navigate = useNavigate();
  const { perms } = useAuth();
  const { def, loading: defLoading, error: defError } = useEntityDef(entity);
  const [params, setParams] = useSearchParams();

  const page = Math.max(1, Number(params.get('page')) || 1);
  const pageSize = PAGE_SIZES.includes(Number(params.get('pageSize'))) ? Number(params.get('pageSize')) : 25;
  const sort = params.get('sort') ?? undefined;
  const dir = (params.get('dir') === 'desc' ? 'desc' : 'asc') as 'asc' | 'desc';
  const [search, setSearch] = useState(params.get('search') ?? '');
  const debouncedSearch = useDebounced(search, 300);

  function update(next: Record<string, string | undefined>) {
    const p = new URLSearchParams(params);
    for (const [k, v] of Object.entries(next)) {
      if (v === undefined || v === '') p.delete(k); else p.set(k, v);
    }
    setParams(p, { replace: true });
  }

  useEffect(() => {
    if ((params.get('search') ?? '') !== debouncedSearch) update({ search: debouncedSearch || undefined, page: undefined });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debouncedSearch]);

  useEffect(() => { setSearch(params.get('search') ?? ''); /* reset on entity change */ // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entity]);

  const p = perms(entity);
  const { data, loading, error } = useAsync<Paged<Row> | undefined>(
    (signal) => (def && p.read ? dataApi.list(def.name, { page, pageSize, search: params.get('search') ?? undefined, sort, dir }, signal) : Promise.resolve(undefined)),
    [def?.name, page, pageSize, params.get('search'), sort, dir, p.read],
  );

  const columns = useMemo(() => {
    if (!def) return [];
    const fs = activeFields(def);
    const shown = fs.filter((f) => f.showInList);
    return shown.length ? shown : fs.slice(0, 5);
  }, [def]);

  if (defLoading) return <LoadingBlock />;
  if (defError instanceof ApiError && defError.status === 403) return <ForbiddenPage />;
  if (!def) return <NotFoundPage />;
  if (!p.read) return <ForbiddenPage />;

  const Icon = resolveIcon(def.icon);
  const total = data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const rows = data?.items ?? [];
  const searching = !!params.get('search');

  function toggleSort(field: string) {
    if (sort !== field) update({ sort: field, dir: 'asc', page: undefined });
    else if (dir === 'asc') update({ dir: 'desc', page: undefined });
    else update({ sort: undefined, dir: undefined, page: undefined });
  }

  const newBtn = p.create && (
    <Button variant="primary" icon={Plus} onClick={() => navigate(`/c/${encodeURIComponent(def.name)}/new`)}>Nuevo</Button>
  );

  return (
    <div className="page">
      <PageHeader icon={Icon} title={def.labelPlural} subtitle={def.description} actions={newBtn} />

      <div className="toolbar">
        <Input
          icon={Search}
          type="search"
          placeholder={`Buscar en ${def.labelPlural.toLowerCase()}…`}
          aria-label="Buscar"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          maxLength={200}
        />
        {loading && data && <Spinner size={16} />}
        <span className="spacer" />
        {data && <span className="muted" style={{ fontSize: 'var(--text-sm)' }}>{total.toLocaleString('es-MX')} {total === 1 ? 'registro' : 'registros'}</span>}
      </div>

      {error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los registros'}>
          {error instanceof ApiError ? error.detail : undefined}
        </Alert>
      ) : (
        <div className="table-wrap">
          {loading && !data ? (
            <LoadingBlock />
          ) : rows.length === 0 ? (
            searching ? (
              <EmptyState icon={SearchX} title="Sin coincidencias" description="Prueba con otros términos de búsqueda." action={<Button onClick={() => setSearch('')}>Limpiar búsqueda</Button>} />
            ) : (
              <EmptyState icon={Inbox} title={`No hay ${def.labelPlural.toLowerCase()}`} description={p.create ? 'Crea el primer registro para empezar.' : 'Todavía no hay registros capturados.'} action={newBtn || undefined} />
            )
          ) : (
            <>
              <div className="table-scroll">
                <table className="table">
                  <thead>
                    <tr>
                      {columns.map((c) => {
                        const active = sort === c.name;
                        const SortIcon = active ? (dir === 'asc' ? ArrowUp : ArrowDown) : ArrowUpDown;
                        return (
                          <th key={c.name} className={c.type === 'integer' || c.type === 'decimal' ? 'td-num' : undefined} aria-sort={active ? (dir === 'asc' ? 'ascending' : 'descending') : undefined}>
                            <button className={`th-sort${active ? ' active' : ''}`} onClick={() => toggleSort(c.name)}>
                              {c.label}
                              <SortIcon size={12} aria-hidden style={{ opacity: active ? 1 : 0.5 }} />
                            </button>
                          </th>
                        );
                      })}
                      <th>Actualizado</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => {
                      const open = () => navigate(`/c/${encodeURIComponent(def.name)}/${r.id}`);
                      return (
                        <tr
                          key={r.id}
                          className="clickable"
                          tabIndex={0}
                          onClick={open}
                          onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } }}
                          aria-label={`Abrir registro ${r.id}`}
                        >
                          {columns.map((c) => (
                            <td key={c.name} className={c.type === 'integer' || c.type === 'decimal' ? 'td-num' : 'td-truncate'}>
                              <CellValue field={c} row={r} />
                            </td>
                          ))}
                          <td className="subtle" style={{ whiteSpace: 'nowrap', fontSize: 'var(--text-sm)' }}>
                            {formatDateTime(r.updatedAt ?? r.createdAt)}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <div className="pagination">
                <span>Filas por página</span>
                <Select
                  selectSize="sm"
                  aria-label="Filas por página"
                  value={pageSize}
                  onChange={(e) => update({ pageSize: e.target.value, page: undefined })}
                  style={{ width: 76 }}
                >
                  {PAGE_SIZES.map((s) => <option key={s} value={s}>{s}</option>)}
                </Select>
                <span className="spacer" />
                <span>Página {page} de {pages}</span>
                <Button size="sm" iconOnly icon={ChevronLeft} aria-label="Página anterior" disabled={page <= 1} onClick={() => update({ page: String(page - 1) })} />
                <Button size="sm" iconOnly icon={ChevronRight} aria-label="Página siguiente" disabled={page >= pages} onClick={() => update({ page: String(page + 1) })} />
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
