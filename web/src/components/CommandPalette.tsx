import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { BarChart3, FileText, Gauge, Search, type LucideIcon } from 'lucide-react';
import { dashboardsApi, reportsApi, searchApi } from '../api/endpoints';
import type { DashboardSummary, ReportSummary, SearchHit } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { adminGroupsFor } from '../lib/adminNav';
import { useApps } from '../lib/apps';
import { Dialog } from './Dialog';
import { DynamicIcon } from './DynamicIcon';
import { Spinner } from './ui';

interface Item { id: string; group: string; label: string; hint?: string; to: string; icon?: LucideIcon; iconName?: string; keywords?: string }

const MIN_RECORD_SEARCH = 2;
const MAX_RECORD_SEARCH = 50;
const MAX_PER_GROUP = 8;

/** Sin acentos ni mayúsculas: «almacen» encuentra «Almacén». */
export const normalize = (s: string) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();

function matches(item: Item, terms: string[]) {
  const text = normalize(`${item.label} ${item.hint ?? ''} ${item.keywords ?? ''}`);
  return terms.every((t) => text.includes(t));
}

/**
 * Spec 024 (H5): paleta de comandos. Módulos, catálogos, análisis y configuración se filtran aquí sobre listas que el
 * servidor ya acotó al usuario; los documentos por folio se buscan en el servidor con su seguridad. Patrón de cuadro
 * combinado con lista (`aria-activedescendant`): el foco se queda en el campo y las flechas mueven la opción activa.
 */
export function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { me, entities } = useAuth();
  const { apps, appOf } = useApps();
  const navigate = useNavigate();
  const [q, setQ] = useState('');
  // La opción activa se recuerda por su id: los folios llegan después y no deben mover la selección (F-02 de la 024).
  const [activeId, setActiveId] = useState<string | null>(null);
  const [hits, setHits] = useState<SearchHit[]>([]);
  const [searching, setSearching] = useState(false);
  const [boards, setBoards] = useState<DashboardSummary[]>([]);
  const [reports, setReports] = useState<ReportSummary[]>([]);
  const listId = useId();
  const listRef = useRef<HTMLDivElement>(null);

  // Al abrir: vacía la búsqueda y trae tableros y reportes (pueden haber cambiado).
  useEffect(() => {
    if (!open) return;
    setQ(''); setActiveId(null); setHits([]);
    const ac = new AbortController();
    dashboardsApi.list(ac.signal).then(setBoards).catch(() => setBoards([]));
    reportsApi.list(ac.signal).then(setReports).catch(() => setReports([]));
    return () => ac.abort();
  }, [open]);

  // Documentos por folio: espera a que se deje de escribir y cancela la búsqueda anterior.
  const term = q.trim();
  useEffect(() => {
    if (!open || term.length < MIN_RECORD_SEARCH || term.length > MAX_RECORD_SEARCH) { setHits([]); setSearching(false); return; }
    const ac = new AbortController();
    setSearching(true);
    const t = window.setTimeout(() => {
      searchApi.records(term, ac.signal)
        .then((r) => { setHits(r); setSearching(false); })
        .catch(() => { if (!ac.signal.aborted) { setHits([]); setSearching(false); } });
    }, 250);
    return () => { window.clearTimeout(t); ac.abort(); };
  }, [open, term]);

  const all = useMemo<Item[]>(() => [
    ...apps.map((a) => ({ id: `app-${a.name}`, group: 'Módulos', label: a.label, hint: a.description ?? undefined, to: `/m/${encodeURIComponent(a.name)}`, iconName: a.icon })),
    ...entities.map((e) => ({
      id: `c-${e.name}`, group: 'Catálogos', label: e.labelPlural, hint: appOf(e.name)?.label, to: `/c/${encodeURIComponent(e.name)}`,
      iconName: e.icon, keywords: e.label,
    })),
    ...boards.map((d) => ({ id: `d-${d.name}`, group: 'Análisis', label: d.label, hint: 'Tablero', to: `/dashboards/${encodeURIComponent(d.name)}`, icon: Gauge })),
    ...reports.map((r) => ({
      id: `r-${r.entity}.${r.name}`, group: 'Análisis', label: r.label, hint: `Reporte · ${r.entityLabel}`,
      to: `/reports/${encodeURIComponent(r.entity)}/${encodeURIComponent(r.name)}`, icon: BarChart3,
    })),
    ...(boards.length ? [{ id: 'all-boards', group: 'Análisis', label: 'Todos los tableros', to: '/dashboards', icon: Gauge }] : []),
    ...(reports.length ? [{ id: 'all-reports', group: 'Análisis', label: 'Todos los reportes', to: '/reports', icon: BarChart3 }] : []),
    ...adminGroupsFor(me).flatMap((g) => g.items.map((i) => ({
      id: `a-${i.to}`, group: 'Configuración', label: i.label, hint: g.label, to: i.to, icon: i.icon, keywords: i.description,
    }))),
  ], [apps, entities, boards, reports, me, appOf]);

  const items = useMemo(() => {
    const terms = normalize(term).split(/\s+/).filter(Boolean);
    // Lo que coincide por su nombre va antes que lo que coincide por su descripción (y su grupo con él).
    const byLabel = (i: Item) => terms.every((t) => normalize(i.label).includes(t));
    const found = terms.length
      ? all.filter((i) => matches(i, terms)).sort((a, b) => Number(byLabel(b)) - Number(byLabel(a)))
      : all.filter((i) => i.group === 'Módulos' || i.group === 'Catálogos');
    const byGroup = new Map<string, Item[]>();
    for (const i of found) {
      const list = byGroup.get(i.group) ?? [];
      if (list.length < MAX_PER_GROUP) byGroup.set(i.group, [...list, i]);
    }
    const records = hits.map((h) => ({
      id: `rec-${h.entity}-${h.id}`, group: 'Documentos', label: h.label, hint: h.entityLabel,
      to: `/c/${encodeURIComponent(h.entity)}/${h.id}`, icon: FileText,
    }));
    return [...(records.length ? [records] : []), ...byGroup.values()].flat();
  }, [all, term, hits]);

  useEffect(() => { setActiveId(null); }, [term]);
  const active = Math.max(0, items.findIndex((i) => i.id === activeId));
  const setActive = (i: number) => setActiveId(items[i]?.id ?? null);
  useEffect(() => {
    listRef.current?.querySelector(`[data-index="${active}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [active]);

  const go = (i: Item | undefined) => {
    if (!i) return;
    onClose();
    navigate(i.to);
  };

  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(items.length ? (active + 1) % items.length : 0); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(items.length ? (active - 1 + items.length) % items.length : 0); }
    else if (e.key === 'Home' && e.ctrlKey) { e.preventDefault(); setActive(0); }
    else if (e.key === 'End' && e.ctrlKey) { e.preventDefault(); setActive(Math.max(items.length - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); go(items[active]); }
  };

  const groups = items.reduce<{ group: string; items: { item: Item; index: number }[] }[]>((acc, item, index) => {
    const last = acc[acc.length - 1];
    if (last?.group === item.group) last.items.push({ item, index });
    else acc.push({ group: item.group, items: [{ item, index }] });
    return acc;
  }, []);
  const optionId = (i: number) => `${listId}-o${i}`;
  const status = items.length === 0
    ? (searching ? 'Buscando…' : 'Sin resultados')
    : `${items.length} ${items.length === 1 ? 'resultado' : 'resultados'}${searching ? ', buscando documentos…' : ''}`;

  return (
    <Dialog open={open} onClose={onClose} title="Buscar" size="lg">
      <div className="cp">
        <div className="input-wrap cp-input">
          <Search size={16} aria-hidden className="input-icon" />
          <input
            data-autofocus
            className="input"
            role="combobox"
            aria-label="Buscar módulos, catálogos, folios o configuración"
            aria-expanded={items.length > 0}
            aria-controls={listId}
            aria-autocomplete="list"
            aria-activedescendant={items.length ? optionId(active) : undefined}
            placeholder="Módulo, catálogo, folio o página de configuración"
            value={q}
            maxLength={100}
            autoComplete="off"
            spellCheck={false}
            onChange={(e) => setQ(e.target.value)}
            onKeyDown={onKey}
          />
          {searching && <span className="cp-spinner"><Spinner size={14} label="Buscando documentos" /></span>}
        </div>
        <div className="sr-only" role="status" aria-live="polite">{status}</div>
        <div ref={listRef} id={listId} role="listbox" aria-label="Resultados" className="cp-list">
          {groups.map((g, gi) => (
            <div key={g.group} role="group" aria-labelledby={`${listId}-g${gi}`}>
              <div id={`${listId}-g${gi}`} role="presentation" className="cp-group">{g.group}</div>
              {g.items.map(({ item, index }) => (
                <div
                  key={item.id}
                  id={optionId(index)}
                  data-index={index}
                  role="option"
                  aria-selected={index === active}
                  className={`cp-option${index === active ? ' active' : ''}`}
                  onMouseMove={() => { if (index !== active) setActive(index); }}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => go(item)}
                >
                  <span className="cp-icon">{item.icon ? <item.icon size={16} aria-hidden /> : <DynamicIcon name={item.iconName} size={16} />}</span>
                  <span className="cp-label">{item.label}</span>
                  {item.hint && <span className="cp-hint">{item.hint}</span>}
                </div>
              ))}
            </div>
          ))}
          {items.length === 0 && !searching && (
            <div className="cp-empty">
              {term.length > 0 && term.length < MIN_RECORD_SEARCH ? 'Escribe al menos 2 caracteres para buscar folios.' : 'Nada coincide con la búsqueda.'}
            </div>
          )}
        </div>
        <div className="cp-foot subtle" aria-hidden>
          <span><kbd>↑</kbd> <kbd>↓</kbd> moverse</span><span><kbd>Enter</kbd> abrir</span><span><kbd>Esc</kbd> cerrar</span>
        </div>
      </div>
    </Dialog>
  );
}
