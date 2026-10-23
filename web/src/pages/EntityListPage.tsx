import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowDown, ArrowUp, ArrowUpDown, BookmarkPlus, ChevronLeft, ChevronRight, Columns3, Filter, Inbox, Plus, Search, SearchX, Trash2 } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { dataApi, viewsApi } from '../api/endpoints';
import type { EntityDef, RowList, SavedView, StateCount, ViewFilter, ViewList } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog, Dialog } from '../components/Dialog';
import { resolveIcon } from '../components/DynamicIcon';
import { FilterChips, FilterDialog, filterableFields } from '../components/FilterBuilder';
import { useToast } from '../components/Toast';
import { Alert, Button, Checkbox, EmptyState, Input, LoadingBlock, PageHeader, Select, Spinner } from '../components/ui';
import { activeFields, CellValue, isNumericField, stateTone, useEntityDef } from '../lib/entity';
import { useAsync, useDebounced } from '../lib/hooks';
import { formatDateTime } from '../lib/format';
import { ForbiddenPage, NotFoundPage } from './StatusPages';

const PAGE_SIZES = [10, 25, 50, 100];
/** Valor de `view` que significa «sin vista» (evita que se vuelva a aplicar la predeterminada). */
const NO_VIEW = 'todos';

function parseFilters(raw: string | null): ViewFilter[] {
  if (!raw) return [];
  try {
    const v: unknown = JSON.parse(raw);
    return Array.isArray(v) ? (v as ViewFilter[]) : [];
  } catch {
    return [];
  }
}

/** Parámetros de la URL que representan una vista (columnas, filtros, orden y estado). */
function viewParams(v: SavedView): Record<string, string | undefined> {
  return {
    view: v.name,
    cols: v.columns?.length ? v.columns.join(',') : undefined,
    filters: v.filters?.length ? JSON.stringify(v.filters) : undefined,
    sort: v.sort ?? undefined,
    dir: v.sort ? v.dir ?? 'asc' : undefined,
    state: v.state ?? undefined,
    page: undefined,
    search: undefined,
  };
}

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
  const state = params.get('state') ?? undefined;
  const filtersRaw = params.get('filters');
  const filters = useMemo(() => parseFilters(filtersRaw), [filtersRaw]);
  const viewName = params.get('view') ?? undefined;
  const colsParam = params.get('cols');
  const [search, setSearch] = useState(params.get('search') ?? '');
  const toast = useToast();
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const [confirmDeleteView, setConfirmDeleteView] = useState(false);
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
  const { data, loading, error } = useAsync<RowList | undefined>(
    (signal) => (def && p.read ? dataApi.list(def.name, { page, pageSize, search: params.get('search') ?? undefined, sort, dir, state, filters }, signal) : Promise.resolve(undefined)),
    [def?.name, page, pageSize, params.get('search'), sort, dir, state, filtersRaw, p.read],
  );

  // Spec 007: vistas compartidas (por rol) y personales.
  const [viewsTick, setViewsTick] = useState(0);
  const { data: views } = useAsync<ViewList | undefined>(
    (signal) => (def && p.read && !def.isChild ? viewsApi.list(def.name, signal) : Promise.resolve(undefined)),
    [def?.name, p.read, viewsTick],
  );
  const allViews = useMemo(() => [...(views?.shared ?? []), ...(views?.personal ?? [])], [views]);
  const currentView = allViews.find((v) => v.name === viewName);
  // La vista predeterminada se aplica una vez al abrir el listado sin parámetros.
  const defaultApplied = useRef<string | null>(null);
  useEffect(() => {
    if (!views || !def || defaultApplied.current === def.name) return;
    defaultApplied.current = def.name;
    const dv = views.shared.find((v) => v.default);
    if (dv && [...params.keys()].length === 0) update(viewParams(dv));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [views, def?.name]);
  // Los conteos por estado se conservan mientras carga para que los chips no parpadeen.
  const [counts, setCounts] = useState<StateCount[] | undefined>();
  useEffect(() => { if (data?.stateCounts) setCounts(data.stateCounts); }, [data]);
  useEffect(() => { setCounts(undefined); }, [entity]);

  const columns = useMemo(() => {
    if (!def) return [];
    // Partidas y documento padre no tienen sentido como columna de listado.
    const fs = activeFields(def).filter((f) => f.type !== 'lines' && f.type !== 'parent');
    // Spec 007: columnas elegidas (las que el usuario no ve simplemente no están en `fs`).
    if (colsParam) {
      const chosen = colsParam.split(',').map((c) => fs.find((f) => f.name.toLowerCase() === c.toLowerCase())).filter((f): f is NonNullable<typeof f> => !!f);
      if (chosen.length) return chosen;
    }
    const shown = fs.filter((f) => f.showInList);
    return shown.length ? shown : fs.slice(0, 5);
  }, [def, colsParam]);

  if (defLoading) return <LoadingBlock />;
  if (defError instanceof ApiError && defError.status === 403) return <ForbiddenPage />;
  if (!def) return <NotFoundPage />;
  if (!p.read) return <ForbiddenPage />;

  const Icon = resolveIcon(def.icon);
  const total = data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const rows = data?.items ?? [];
  const searching = !!params.get('search') || filters.length > 0;
  const customized = !!(colsParam || filtersRaw || sort || state);

  function chooseView(name: string) {
    const v = allViews.find((x) => x.name === name);
    if (v) update(viewParams(v));
    else update({ view: NO_VIEW, cols: undefined, filters: undefined, sort: undefined, dir: undefined, state: undefined, page: undefined, search: undefined });
  }

  function setFilters(next: ViewFilter[]) {
    update({ filters: next.length ? JSON.stringify(next) : undefined, page: undefined });
  }

  function toggleSort(field: string) {
    if (sort !== field) update({ sort: field, dir: 'asc', page: undefined });
    else if (dir === 'asc') update({ dir: 'desc', page: undefined });
    else update({ sort: undefined, dir: undefined, page: undefined });
  }

  // Spec 006: un catálogo que administra el sistema no ofrece altas a mano, ni al administrador.
  const canCreate = p.create && !def?.systemManaged;
  const newBtn = canCreate && (
    <Button variant="primary" icon={Plus} onClick={() => navigate(`/c/${encodeURIComponent(def.name)}/new`)}>Nuevo</Button>
  );

  return (
    <div className="page">
      <PageHeader icon={Icon} title={def.labelPlural} subtitle={def.description} actions={newBtn} />

      {def.workflow && (
        <StateFilter
          states={def.workflow.states.map((s) => ({ state: s.name, label: s.label, color: s.color, count: counts?.find((c) => c.state === s.name)?.count ?? 0 }))}
          known={!!counts}
          value={state}
          onChange={(v) => update({ state: v, page: undefined })}
        />
      )}

      {!def.isChild && (
        <div className="toolbar view-bar" role="group" aria-label="Vista del listado">
          <Select selectSize="sm" aria-label="Vista" value={currentView?.name ?? NO_VIEW} onChange={(e) => chooseView(e.target.value)}>
            <option value={NO_VIEW}>Todos los registros</option>
            {!!views?.shared.length && (
              <optgroup label="Compartidas">
                {views.shared.map((v) => <option key={v.name} value={v.name}>{v.label}</option>)}
              </optgroup>
            )}
            {!!views?.personal.length && (
              <optgroup label="Mis vistas">
                {views.personal.map((v) => <option key={v.name} value={v.name}>{v.label}</option>)}
              </optgroup>
            )}
          </Select>
          <Button size="sm" icon={Filter} onClick={() => setFiltersOpen(true)} aria-haspopup="dialog">
            Filtros{filters.length ? ` (${filters.length})` : ''}
          </Button>
          <Button size="sm" icon={Columns3} onClick={() => setColumnsOpen(true)} aria-haspopup="dialog">Columnas</Button>
          <Button size="sm" icon={BookmarkPlus} onClick={() => setSaveOpen(true)} aria-haspopup="dialog" disabled={!customized && !currentView?.personal}>Guardar vista</Button>
          {currentView?.personal && (
            <Button size="sm" variant="ghost" icon={Trash2} onClick={() => setConfirmDeleteView(true)}>Eliminar vista</Button>
          )}
        </div>
      )}
      <FilterChips def={def} filters={filters} onRemove={(i) => setFilters(filters.filter((_, j) => j !== i))} />

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
              <EmptyState icon={SearchX} title="Sin coincidencias" description={filters.length ? 'Ningún registro cumple los filtros.' : 'Prueba con otros términos de búsqueda.'}
                action={<Button onClick={() => { setSearch(''); if (filters.length) setFilters([]); }}>{filters.length ? 'Quitar filtros' : 'Limpiar búsqueda'}</Button>} />
            ) : (
              <EmptyState icon={Inbox} title={`No hay ${def.labelPlural.toLowerCase()}`} description={canCreate ? 'Crea el primer registro para empezar.' : def.systemManaged ? 'Los registra el sistema automáticamente.' : 'Todavía no hay registros capturados.'} action={newBtn || undefined} />
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
                          <th key={c.name} className={isNumericField(c) ? 'td-num' : undefined} aria-sort={active ? (dir === 'asc' ? 'ascending' : 'descending') : undefined}>
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
                            <td key={c.name} className={isNumericField(c) ? 'td-num' : 'td-truncate'}>
                              <CellValue field={c} row={r} def={def} />
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

      <FilterDialog open={filtersOpen} def={def} filters={filters} onClose={() => setFiltersOpen(false)}
        onApply={(f) => { setFiltersOpen(false); setFilters(f); }} />
      <ColumnsDialog open={columnsOpen} def={def} current={columns.map((c) => c.name)} onClose={() => setColumnsOpen(false)}
        onApply={(cols) => { setColumnsOpen(false); update({ cols: cols?.join(',') }); }} />
      <SaveViewDialog
        open={saveOpen}
        def={def}
        current={currentView?.personal ? currentView : undefined}
        query={{ columns: colsParam ? columns.map((c) => c.name) : undefined, filters, sort, dir: sort ? dir : undefined, state }}
        onClose={() => setSaveOpen(false)}
        onSaved={(v) => { setSaveOpen(false); toast.success('Vista guardada'); setViewsTick((t) => t + 1); update({ view: v.name }); }}
      />
      <ConfirmDialog
        open={confirmDeleteView}
        danger
        title="Eliminar vista"
        message={<>La vista <strong>{currentView?.label}</strong> dejará de aparecer en tus vistas.</>}
        confirmLabel="Eliminar"
        onCancel={() => setConfirmDeleteView(false)}
        onConfirm={async () => {
          if (!currentView?.id) return;
          try {
            await viewsApi.remove(def.name, currentView.id);
            toast.success('Vista eliminada');
            setViewsTick((t) => t + 1);
            chooseView(NO_VIEW);
          } catch (e) {
            toast.fromError(e);
          } finally {
            setConfirmDeleteView(false);
          }
        }}
      />
    </div>
  );
}

/** Elegir columnas del listado (solo campos que la persona puede ver). */
function ColumnsDialog({ open, def, current, onClose, onApply }: {
  open: boolean; def: EntityDef; current: string[]; onClose: () => void; onApply: (cols: string[] | undefined) => void;
}) {
  const fields = filterableFields(def);
  const [chosen, setChosen] = useState<string[]>(current);
  useEffect(() => { if (open) setChosen(current); }, [open, current]);
  return (
    <Dialog open={open} onClose={onClose} title="Columnas" description="Elige qué campos se muestran en el listado, en el orden del catálogo."
      footer={
        <>
          <Button onClick={() => onApply(undefined)} style={{ marginRight: 'auto' }}>Restablecer</Button>
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" disabled={chosen.length === 0} onClick={() => onApply(fields.filter((f) => chosen.includes(f.name)).map((f) => f.name))}>Aplicar</Button>
        </>
      }
    >
      <div className="column-list">
        {fields.map((f) => (
          <Checkbox key={f.name} label={f.label} checked={chosen.includes(f.name)}
            onChange={(c) => setChosen((s) => (c ? [...s, f.name] : s.filter((x) => x !== f.name)))} />
        ))}
      </div>
    </Dialog>
  );
}

/** Guarda la consulta actual como vista personal (o actualiza la vista personal elegida si conserva su nombre). */
function SaveViewDialog({ open, def, current, query, onClose, onSaved }: {
  open: boolean; def: EntityDef; current?: SavedView;
  query: { columns?: string[]; filters: ViewFilter[]; sort?: string; dir?: 'asc' | 'desc'; state?: string };
  onClose: () => void; onSaved: (v: SavedView) => void;
}) {
  const [label, setLabel] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  useEffect(() => { if (open) { setLabel(current?.label ?? ''); setError(null); } }, [open, current]);
  const replacing = !!current && label.trim() === current.label;

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const input = { label: label.trim(), ...query, filters: query.filters.length ? query.filters : undefined };
      const v = replacing && current?.id ? await viewsApi.update(def.name, current.id, input) : await viewsApi.create(def.name, input);
      onSaved(v);
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setSaving(false);
    }
  }

  const general = error && !fieldError(error, 'label') ? (fieldError(error, 'view') ?? error.detail ?? error.title) : undefined;
  return (
    <Dialog open={open} onClose={onClose} title="Guardar vista" description="Solo tú verás esta vista. Guarda las columnas, los filtros y el orden actuales."
      footer={
        <>
          <Button onClick={onClose} disabled={saving}>Cancelar</Button>
          <Button variant="primary" loading={saving} disabled={!label.trim()} onClick={() => void save()}>{replacing ? 'Actualizar vista' : 'Guardar'}</Button>
        </>
      }
    >
      <div className="stack">
        {general && <Alert tone="danger" title="No se guardó la vista">{general}</Alert>}
        <Input label="Nombre de la vista" required maxLength={100} value={label} error={fieldError(error, 'label')}
          onChange={(e) => setLabel(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' && label.trim()) void save(); }} />
      </div>
    </Dialog>
  );
}

/** Filtro por estado del proceso: un grupo de opciones con conteos ("Todos" primero). */
function StateFilter({ states, known, value, onChange }: {
  states: StateCount[];
  known: boolean;
  value: string | undefined;
  onChange: (state: string | undefined) => void;
}) {
  const total = states.reduce((n, s) => n + s.count, 0);
  const items: { state?: string; label: string; count: number; tone: ReturnType<typeof stateTone> }[] = [
    { state: undefined, label: 'Todos', count: total, tone: 'neutral' },
    ...states.map((s) => ({ state: s.state, label: s.label, count: s.count, tone: stateTone(s.color) })),
  ];
  return (
    <div className="state-filter" role="radiogroup" aria-label="Filtrar por estado">
      {items.map((it) => {
        const checked = (value ?? undefined) === it.state;
        return (
          <button
            key={it.state ?? '__all'}
            type="button"
            role="radio"
            aria-checked={checked}
            className={`state-chip tone-${it.tone}${checked ? ' checked' : ''}`}
            onClick={() => onChange(it.state)}
          >
            <span className="state-chip-dot" aria-hidden />
            {it.label}
            {known && <span className="state-chip-count">{it.count.toLocaleString('es-MX')}</span>}
          </button>
        );
      })}
    </div>
  );
}
