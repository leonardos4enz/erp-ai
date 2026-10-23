import { useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { Search, X } from 'lucide-react';
import { dataApi } from '../api/endpoints';
import type { LookupItem } from '../api/types';
import { useDebounced } from '../lib/hooks';
import { Spinner } from './ui';

export interface ReferenceSelectProps {
  entity: string;
  value: number | null;
  label: string | null;
  onChange: (id: number | null, label: string | null) => void;
  id?: string;
  invalid?: boolean;
  disabled?: boolean;
  describedBy?: string;
  /** Nombre accesible cuando no hay <label> visible (p. ej. celdas de partidas). */
  ariaLabel?: string;
  /** Tamaño reducido para tablas. */
  compact?: boolean;
  /** Fuente alternativa de opciones (p. ej. usuarios). Por defecto: GET /data/{entity}/lookup. */
  lookup?: (search: string, signal: AbortSignal) => Promise<LookupItem[]>;
  /** Spec 005: solo ofrece registros con `field` = `value` (p. ej. partidas de la orden elegida). */
  filter?: { field: string; value: unknown; sourceLabel: string };
}

const LIST_MAX_HEIGHT = 240;
const LIST_MIN_WIDTH = 280;

/**
 * Posición de la lista en coordenadas de la ventana. La lista se dibuja en un portal (fuera del
 * formulario) para que ningún contenedor con scroll la recorte, p. ej. la tabla de partidas
 * (hallazgo F-20). Se abre hacia arriba si no cabe abajo.
 */
function listPosition(anchor: HTMLElement): CSSProperties {
  const r = anchor.getBoundingClientRect();
  const width = Math.max(r.width, LIST_MIN_WIDTH);
  const left = Math.max(8, Math.min(r.left, window.innerWidth - width - 8));
  const below = window.innerHeight - r.bottom - 8;
  const above = r.top - 8;
  const up = below < Math.min(LIST_MAX_HEIGHT, 160) && above > below;
  return {
    position: 'fixed', left, width,
    maxHeight: Math.min(LIST_MAX_HEIGHT, Math.max(up ? above : below, 120)),
    ...(up ? { bottom: window.innerHeight - r.top + 4 } : { top: r.bottom + 4 }),
  };
}

/** Accessible async combobox backed by GET /data/{entity}/lookup. */
export function ReferenceSelect({ entity, value, label, onChange, id, invalid, disabled, describedBy, ariaLabel, compact, lookup, filter }: ReferenceSelectProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<LookupItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [active, setActive] = useState(0);
  const [pos, setPos] = useState<CSSProperties>({});
  const debounced = useDebounced(query, 250);
  const listId = useId();
  const wrapRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  const filterValue = filter?.value ?? null;
  const waitingSource = !!filter && (filterValue === null || filterValue === '');

  useEffect(() => {
    if (!open) return;
    if (waitingSource) { setItems([]); setLoading(false); return; }
    const ctrl = new AbortController();
    setLoading(true);
    (lookup ? lookup(debounced, ctrl.signal) : dataApi.lookup(entity, debounced, ctrl.signal, filter ? { field: filter.field, value: filterValue } : undefined))
      .then((r) => { setItems(r); setActive(0); })
      .catch(() => { if (!ctrl.signal.aborted) setItems([]); })
      .finally(() => { if (!ctrl.signal.aborted) setLoading(false); });
    return () => ctrl.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entity, debounced, open, filter?.field, filterValue]);

  // Sigue al campo si la página, la tabla de partidas o la ventana se desplazan o cambian de tamaño.
  useLayoutEffect(() => {
    if (!open || !wrapRef.current) return;
    const update = () => wrapRef.current && setPos(listPosition(wrapRef.current));
    update();
    window.addEventListener('scroll', update, true);
    window.addEventListener('resize', update);
    return () => { window.removeEventListener('scroll', update, true); window.removeEventListener('resize', update); };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function onDoc(e: MouseEvent) {
      const t = e.target as Node;
      if (!wrapRef.current?.contains(t) && !listRef.current?.contains(t)) setOpen(false);
    }
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  function choose(item: LookupItem) {
    onChange(item.id, item.label);
    setOpen(false);
    setQuery('');
  }

  const current = label ?? (value != null ? `#${value}` : '');
  const display = open ? query : current;

  return (
    <div className="combo" ref={wrapRef}>
      <div className="input-wrap">
        <Search size={15} aria-hidden />
        <input
          id={id}
          className={compact ? 'input input-sm' : 'input'}
          aria-label={ariaLabel}
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-invalid={invalid || undefined}
          aria-describedby={describedBy}
          aria-activedescendant={open && items[active] ? `${listId}-${items[active].id}` : undefined}
          // Al abrir para buscar, el valor actual queda visible como guía (antes parecía que se perdía).
          placeholder={disabled ? '—' : open && current ? current : 'Buscar…'}
          title={current || undefined}
          value={display}
          disabled={disabled}
          style={{ paddingRight: value != null ? 36 : undefined }}
          onFocus={() => setOpen(true)}
          // Tras elegir una opción el foco se queda aquí: un clic nuevo también debe abrir la lista.
          onClick={() => setOpen(true)}
          onChange={(e) => { setQuery(e.target.value); setOpen(true); }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive((a) => Math.min(a + 1, items.length - 1)); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
            else if (e.key === 'Enter' && open && items[active]) { e.preventDefault(); choose(items[active]); }
            else if (e.key === 'Escape' && open) { e.stopPropagation(); setOpen(false); }
            else if (e.key === 'Tab') setOpen(false);
          }}
        />
      </div>
      {value != null && !disabled && (
        <button type="button" className="btn btn-ghost btn-sm btn-icon combo-clear" aria-label="Quitar selección" onClick={() => onChange(null, null)}>
          <X size={14} aria-hidden />
        </button>
      )}
      {open && createPortal(
        <ul className="combo-list combo-list-floating" id={listId} role="listbox" ref={listRef} style={pos}>
          {loading && <li className="combo-empty"><Spinner size={14} /></li>}
          {!loading && items.length === 0 && (
            <li className="combo-empty">{waitingSource ? `Elige primero ${filter!.sourceLabel.toLowerCase()}` : 'Sin resultados'}</li>
          )}
          {!loading && items.map((it, i) => (
            <li
              key={it.id}
              id={`${listId}-${it.id}`}
              role="option"
              aria-selected={i === active}
              className="combo-option"
              onMouseEnter={() => setActive(i)}
              onMouseDown={(e) => { e.preventDefault(); choose(it); }}
            >
              {it.label}
            </li>
          ))}
        </ul>,
        document.body,
      )}
    </div>
  );
}
