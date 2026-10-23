import { useEffect, useId, useRef, useState } from 'react';
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
}

/** Accessible async combobox backed by GET /data/{entity}/lookup. */
export function ReferenceSelect({ entity, value, label, onChange, id, invalid, disabled, describedBy, ariaLabel, compact, lookup }: ReferenceSelectProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<LookupItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [active, setActive] = useState(0);
  const debounced = useDebounced(query, 250);
  const listId = useId();
  const wrapRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const ctrl = new AbortController();
    setLoading(true);
    (lookup ? lookup(debounced, ctrl.signal) : dataApi.lookup(entity, debounced, ctrl.signal))
      .then((r) => { setItems(r); setActive(0); })
      .catch(() => { if (!ctrl.signal.aborted) setItems([]); })
      .finally(() => { if (!ctrl.signal.aborted) setLoading(false); });
    return () => ctrl.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entity, debounced, open]);

  useEffect(() => {
    if (!open) return;
    function onDoc(e: MouseEvent) {
      if (!wrapRef.current?.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  function choose(item: LookupItem) {
    onChange(item.id, item.label);
    setOpen(false);
    setQuery('');
  }

  const display = open ? query : (label ?? (value != null ? `#${value}` : ''));

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
          placeholder={disabled ? '—' : 'Buscar…'}
          value={display}
          disabled={disabled}
          style={{ paddingRight: value != null ? 36 : undefined }}
          onFocus={() => setOpen(true)}
          onChange={(e) => { setQuery(e.target.value); setOpen(true); }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive((a) => Math.min(a + 1, items.length - 1)); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
            else if (e.key === 'Enter' && open && items[active]) { e.preventDefault(); choose(items[active]); }
            else if (e.key === 'Escape' && open) { e.stopPropagation(); setOpen(false); }
          }}
        />
      </div>
      {value != null && !disabled && (
        <button type="button" className="btn btn-ghost btn-sm btn-icon combo-clear" aria-label="Quitar selección" onClick={() => onChange(null, null)}>
          <X size={14} aria-hidden />
        </button>
      )}
      {open && (
        <ul className="combo-list" id={listId} role="listbox">
          {loading && <li className="combo-empty"><Spinner size={14} /></li>}
          {!loading && items.length === 0 && <li className="combo-empty">Sin resultados</li>}
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
        </ul>
      )}
    </div>
  );
}
