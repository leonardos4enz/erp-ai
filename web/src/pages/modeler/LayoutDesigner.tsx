import { useEffect, useRef, useState, type DragEvent, type KeyboardEvent } from 'react';
import {
  AlertTriangle, ArrowDown, ArrowUp, EyeOff, GripVertical, LayoutTemplate, MoveHorizontal, Plus, Settings2, Trash2, X,
} from 'lucide-react';
import type { FieldDef, Layout, LayoutSection } from '../../api/types';
import { Dialog } from '../../components/Dialog';
import { Alert, Badge, Button, EmptyState, Input, Select, Switch } from '../../components/ui';
import { IDENTIFIER_RE, toIdentifier } from '../../lib/format';
import {
  PALETTE, addSection, addTab, dropField, dropSection, findField, initialLayout, itemField, moveField, patchItem,
  patchSection, removeSection, removeTab, renameTab, setTabs, stepField, stepSection, stepTab, tabsOf, uniqueName, usesTabs,
} from '../../lib/layoutEdit';
import { LayoutPreview } from './LayoutPreview';

/** Tipos que el formulario coloca en su propio lugar (007): no van en el layout. */
const NOT_PLACEABLE = new Set(['lines', 'parent', 'state']);
const FIELD_MIME = 'application/x-erp-field';
const SECTION_MIME = 'application/x-erp-section';

export interface LayoutDesignerProps {
  layout: Layout | null;
  onChange: (layout: Layout | null) => void;
  /** Campos activos del borrador (los nuevos incluidos). */
  fields: FieldDef[];
  isDocument: boolean;
}

export function LayoutDesigner({ layout, onChange, fields, isDocument }: LayoutDesignerProps) {
  const placeable = fields.filter((f) => !NOT_PLACEABLE.has(f.type));
  const byName = new Map(placeable.map((f) => [f.name.toLowerCase(), f]));
  const [announce, setAnnounce] = useState('');
  const [grabbed, setGrabbed] = useState<{ field: string; origin: Layout } | null>(null);
  const [options, setOptions] = useState<string | null>(null);
  const [drag, setDrag] = useState<{ kind: 'field'; field: string } | { kind: 'section'; index: number } | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  /** Secciones creadas aquí: su identificador sigue a la etiqueta hasta que se edite a mano. */
  const autoNames = useRef(new Set<string>());
  const focusAfter = useRef<string | null>(null);
  const root = useRef<HTMLDivElement>(null);

  // Al mover con teclado el chip cambia de lugar (y a veces de sección): el foco lo sigue.
  useEffect(() => {
    if (!focusAfter.current) return;
    const sel = focusAfter.current;
    focusAfter.current = null;
    const el = root.current?.querySelector<HTMLElement>(sel);
    el?.focus();
    // El título de una sección nueva queda seleccionado: se escribe encima de «Nueva sección».
    if (el instanceof HTMLInputElement) el.select();
  });

  if (!layout) {
    return (
      <div className="ld">
        <div className="card card-pad ld-empty">
          <EmptyState
            icon={LayoutTemplate}
            title="El formulario muestra los campos en el orden de la lista"
            description="Crea un layout para organizarlo en secciones y pestañas, con campos de ancho completo y partes que aparecen solo cuando aplican."
            action={<Button variant="primary" icon={Plus} onClick={() => { onChange(initialLayout(placeable.map((f) => f.name))); setAnnounce('Layout creado con una sección y todos los campos.'); }}>Crear layout</Button>}
          />
        </div>
        <LayoutPreview layout={null} fields={placeable} isDocument={isDocument} />
      </div>
    );
  }
  const l = layout;
  const tabsOn = usesTabs(l);
  const placedNames = new Set(l.sections.flatMap((s) => s.fields.map((i) => itemField(i).toLowerCase())));
  const unplaced = placeable.filter((f) => !placedNames.has(f.name.toLowerCase()));
  const labelOf = (name: string) => byName.get(name.toLowerCase())?.label || name;
  const sectionLabel = (i: number) => l.sections[i]?.label || l.sections[i]?.name || `Sección ${i + 1}`;

  function describe(name: string, lay: Layout): string {
    const pos = findField(lay, name);
    if (!pos) return `${labelOf(name)}: sin colocar.`;
    const s = lay.sections[pos.section];
    return `${labelOf(name)}: sección ${s.label || s.name}${s.tab ? `, pestaña ${s.tab}` : ''}, posición ${pos.index + 1} de ${s.fields.length}.`;
  }

  const chipSel = (name: string) => `[data-ld-chip="${CSS.escape(name.toLowerCase())}"]`;

  function onChipKey(e: KeyboardEvent<HTMLButtonElement>, name: string) {
    if (e.key === ' ' || e.key === 'Enter') {
      e.preventDefault();
      if (grabbed?.field === name) {
        setGrabbed(null);
        setAnnounce(`Soltado. ${describe(name, l)}`);
      } else {
        setGrabbed({ field: name, origin: l });
        setAnnounce(`${labelOf(name)} tomado. Usa las flechas arriba y abajo para moverlo, Espacio para soltar y Escape para cancelar. ${describe(name, l)}`);
      }
      return;
    }
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && grabbed?.field === name) {
      e.preventDefault();
      const r = stepField(l, name, e.key === 'ArrowDown' ? 1 : -1);
      if (r.layout !== l) { onChange(r.layout); focusAfter.current = chipSel(name); }
      setAnnounce(describe(name, r.layout));
      return;
    }
    if (e.key === 'Escape' && grabbed?.field === name) {
      e.preventDefault();
      e.stopPropagation();
      onChange(grabbed.origin);
      setGrabbed(null);
      focusAfter.current = chipSel(name);
      setAnnounce(`Movimiento cancelado. ${describe(name, grabbed.origin)}`);
      return;
    }
    if ((e.key === 'Delete' || e.key === 'Backspace') && placedNames.has(name.toLowerCase())) {
      e.preventDefault();
      setGrabbed(null);
      onChange(moveField(l, name, PALETTE));
      focusAfter.current = chipSel(name);
      setAnnounce(`${labelOf(name)} quitado del formulario: aparecerá en «Otros datos».`);
    }
  }

  /* ---------- Arrastrar y soltar (ratón) ---------- */
  function startFieldDrag(e: DragEvent, name: string) {
    e.dataTransfer.setData(FIELD_MIME, name);
    e.dataTransfer.effectAllowed = 'move';
    setDrag({ kind: 'field', field: name });
  }
  function startSectionDrag(e: DragEvent, index: number) {
    e.dataTransfer.setData(SECTION_MIME, String(index));
    e.dataTransfer.effectAllowed = 'move';
    setDrag({ kind: 'section', index });
  }
  function endDrag() { setDrag(null); setDropTarget(null); }
  const accepts = (kind: 'field' | 'section') => (e: DragEvent, target: string) => {
    if (drag?.kind !== kind) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'move';
    if (dropTarget !== target) setDropTarget(target);
  };
  const overField = accepts('field');
  const overSection = accepts('section');
  function dropFieldAt(e: DragEvent, section: number | typeof PALETTE, before: string | null) {
    if (drag?.kind !== 'field') return;
    e.preventDefault();
    e.stopPropagation();
    const name = drag.field;
    const next = section === PALETTE ? moveField(l, name, PALETTE) : dropField(l, name, section, before);
    onChange(next);
    setAnnounce(section === PALETTE ? `${labelOf(name)} quitado del formulario.` : `Colocado. ${describe(name, next)}`);
    endDrag();
  }
  function dropSectionAt(e: DragEvent, target: { before: number } | { tab: string } | { end: true }) {
    if (drag?.kind !== 'section') return;
    e.preventDefault();
    e.stopPropagation();
    const moving = l.sections[drag.index];
    onChange(dropSection(l, drag.index, target));
    setAnnounce(`Sección ${moving.label || moving.name} movida.`);
    endDrag();
  }

  /* ---------- Secciones ---------- */
  function patchSec(i: number, patch: Partial<Omit<LayoutSection, 'fields'>>) {
    const s = l.sections[i];
    if (patch.label !== undefined && autoNames.current.has(s.name)) {
      const name = uniqueName(toIdentifier(patch.label) || 'Seccion', l.sections.filter((_, j) => j !== i).map((x) => x.name));
      autoNames.current.delete(s.name);
      autoNames.current.add(name);
      patch = { ...patch, name };
    } else if (patch.name !== undefined) autoNames.current.delete(s.name);
    const renamed = patch.name;
    if (renamed !== undefined && expanded.has(s.name)) setExpanded((x) => { const n = new Set(x); n.delete(s.name); n.add(renamed); return n; });
    onChange(patchSection(l, i, patch));
  }
  function moveSec(i: number, dir: -1 | 1) {
    const r = stepSection(l, i, dir);
    onChange(r.layout);
    const s = r.layout.sections[r.index];
    focusAfter.current = `[data-ld-section="${CSS.escape(s.name)}"] [data-ld-move="${dir}"]`;
    setAnnounce(`Sección ${s.label || s.name}: posición ${r.index + 1} de ${r.layout.sections.length}${s.tab ? `, pestaña ${s.tab}` : ''}.`);
  }
  function addSec(tab?: string) {
    const r = addSection(l, tab);
    autoNames.current.add(r.layout.sections[r.index].name);
    onChange(r.layout);
    focusAfter.current = `[data-ld-section="${CSS.escape(r.layout.sections[r.index].name)}"] input`;
    setAnnounce('Sección agregada. Arrastra campos a ella.');
  }

  function renderChip(name: string, sectionIndex: number | null) {
    const f = byName.get(name.toLowerCase());
    const pos = sectionIndex === null ? null : findField(l, name);
    const item = pos ? l.sections[pos.section].fields[pos.index] : name;
    const cond = typeof item !== 'string' ? item.visibleWhen : undefined;
    const wide = typeof item !== 'string' && !!item.wide;
    if (!f) {
      // H1 CA8: el layout menciona un campo que ya no está activo en el borrador.
      const field = fields.find((x) => x.name.toLowerCase() === name.toLowerCase());
      const why = field ? 'tiene su propio lugar en el formulario (partidas, documento o estado)' : 'no existe o está archivado';
      return (
        <li key={name} className="ld-chip ld-chip-error">
          <AlertTriangle size={14} aria-hidden />
          <span className="grow ld-chip-text"><span className="mono">{name}</span> <span className="subtle">{why}</span></span>
          <Button variant="ghost" size="sm" iconOnly icon={X} aria-label={`Quitar ${name} del formulario`} onClick={() => onChange(moveField(l, name, PALETTE))} />
        </li>
      );
    }
    const isGrabbed = grabbed?.field === name;
    return (
      <li
        key={name}
        className={`ld-chip${isGrabbed ? ' grabbed' : ''}${wide && sectionIndex !== null ? ' span-2' : ''}${dropTarget === `chip:${name}` ? ' drop-before' : ''}`}
        draggable
        onDragStart={(e) => startFieldDrag(e, name)}
        onDragEnd={endDrag}
        onDragOver={sectionIndex === null ? undefined : (e) => overField(e, `chip:${name}`)}
        onDrop={sectionIndex === null ? undefined : (e) => dropFieldAt(e, sectionIndex, name)}
      >
        <button
          type="button"
          className="ld-chip-handle"
          data-ld-chip={name.toLowerCase()}
          aria-pressed={isGrabbed}
          aria-describedby="ld-help"
          aria-label={`${f.label || f.name}${sectionIndex === null ? ', sin colocar' : ''}${wide ? ', ancho completo' : ''}${cond ? ', condicional' : ''}`}
          onKeyDown={(e) => onChipKey(e, name)}
          onBlur={() => {
            // Al moverse con teclado el chip puede volver a montarse (pierde el foco y el efecto se lo devuelve): solo se
            // suelta si el foco de verdad se fue a otro lado.
            if (!isGrabbed) return;
            setTimeout(() => {
              if (document.activeElement?.getAttribute('data-ld-chip') === name.toLowerCase()) return;
              setGrabbed((g) => (g?.field === name ? null : g));
            }, 0);
          }}
        >
          <GripVertical size={14} aria-hidden className="subtle" />
          <span className="ld-chip-text">{f.label || f.name}</span>
          {wide && sectionIndex !== null && <MoveHorizontal size={13} aria-hidden className="subtle" />}
          {cond && <EyeOff size={13} aria-hidden className="ld-cond-icon" />}
        </button>
        <Button variant="ghost" size="sm" iconOnly icon={Settings2} aria-label={`Opciones de ${f.label || f.name}`} title="Opciones" onClick={() => setOptions(name)} />
      </li>
    );
  }

  function renderSection(s: LayoutSection, i: number) {
    const open = expanded.has(s.name);
    const nameErr = !IDENTIFIER_RE.test(s.name) ? 'Solo letras, números y guion bajo; inicia con letra' : l.sections.some((x, j) => j !== i && x.name.toLowerCase() === s.name.toLowerCase()) ? 'Repetido' : undefined;
    const firstOfTab = i === 0 || l.sections[i - 1].tab !== s.tab;
    const lastOfTab = i === l.sections.length - 1 || l.sections[i + 1].tab !== s.tab;
    return (
      <section
        key={i}
        className={`ld-section${dropTarget === `sec:${i}` ? ' drop-before' : ''}${drag?.kind === 'section' && drag.index === i ? ' dragging' : ''}`}
        aria-label={`Sección ${s.label || s.name}`}
        data-ld-section={s.name}
        onDragOver={(e) => overSection(e, `sec:${i}`)}
        onDrop={(e) => dropSectionAt(e, { before: i })}
      >
        <div className="ld-section-head">
          <span className="ld-grip" draggable onDragStart={(e) => startSectionDrag(e, i)} onDragEnd={endDrag} title="Arrastra para mover la sección" aria-hidden>
            <GripVertical size={16} />
          </span>
          <Input inputSize="sm" wrapClassName="grow" aria-label={`Título de la sección ${i + 1}`} value={s.label} maxLength={100}
            error={s.label.trim() ? undefined : 'Escribe un título'} onChange={(e) => patchSec(i, { label: e.target.value })} />
          <Select selectSize="sm" aria-label={`Columnas de la sección ${s.label || s.name}`} value={String(s.columns ?? 2)} onChange={(e) => patchSec(i, { columns: Number(e.target.value) as 1 | 2 })}>
            <option value="2">2 columnas</option>
            <option value="1">1 columna</option>
          </Select>
          <div className="row ld-tools">
            <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} data-ld-move="-1" aria-label={`Subir sección ${s.label || s.name}`}
              title={tabsOn && firstOfTab && i > 0 ? 'Pasar a la pestaña anterior' : 'Subir'} disabled={i === 0} onClick={() => moveSec(i, -1)} />
            <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} data-ld-move="1" aria-label={`Bajar sección ${s.label || s.name}`}
              title={tabsOn && lastOfTab && i < l.sections.length - 1 ? 'Pasar a la pestaña siguiente' : 'Bajar'} disabled={i === l.sections.length - 1} onClick={() => moveSec(i, 1)} />
            <Button variant="ghost" size="sm" iconOnly icon={Settings2} aria-label={`Propiedades de la sección ${s.label || s.name}`} aria-expanded={open}
              onClick={() => setExpanded((x) => { const n = new Set(x); if (n.has(s.name)) n.delete(s.name); else n.add(s.name); return n; })} />
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar sección ${s.label || s.name}`} title="Quitar sección (sus campos vuelven a «Sin colocar»)"
              onClick={() => { onChange(removeSection(l, i)); setAnnounce(`Sección ${s.label || s.name} quitada; sus campos quedaron sin colocar.`); }} />
          </div>
        </div>
        {(open || nameErr) && (
          <div className="ld-section-props form-grid">
            <Input inputSize="sm" label="Identificador" className="mono" value={s.name} maxLength={50} error={nameErr}
              onChange={(e) => patchSec(i, { name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
            <Input inputSize="sm" label="Descripción" value={s.description ?? ''} maxLength={300} onChange={(e) => patchSec(i, { description: e.target.value })} />
            <Input inputSize="sm" wrapClassName="span-2" label="Visible solo si" className="mono" maxLength={1000} placeholder="Siempre visible"
              hint="Expresión, p. ej. Moneda.Clave <> 'MXN'. Se evalúa en el servidor mientras se captura." value={s.visibleWhen ?? ''}
              onChange={(e) => patchSec(i, { visibleWhen: e.target.value })} />
          </div>
        )}
        {s.visibleWhen && !open && <p className="subtle ld-cond"><EyeOff size={13} aria-hidden /> Visible solo si <code>{s.visibleWhen}</code></p>}
        <ul
          className={`ld-grid${s.columns === 1 ? ' cols-1' : ''}${dropTarget === `end:${i}` ? ' drop-end' : ''}`}
          aria-label={`Campos de ${s.label || s.name}`}
          onDragOver={(e) => overField(e, `end:${i}`)}
          onDrop={(e) => dropFieldAt(e, i, null)}
        >
          {s.fields.map((it) => renderChip(itemField(it), i))}
          {s.fields.length === 0 && <li className="ld-drop-hint">Arrastra campos aquí. Una sección necesita al menos un campo.</li>}
        </ul>
      </section>
    );
  }

  function renderTabGroup(tab: string, idx: number, tabs: string[]) {
    const items = l.sections.map((s, i) => ({ s, i })).filter((x) => x.s.tab === tab);
    return (
      <div key={idx} className="ld-tab" role="group" aria-label={`Pestaña ${tab}`}>
        <div
          className={`ld-tab-head${dropTarget === `tab:${tab}` ? ' drop-end' : ''}`}
          onDragOver={(e) => overSection(e, `tab:${tab}`)}
          onDrop={(e) => dropSectionAt(e, { tab })}
        >
          <TabNameInput index={idx} value={tab} others={tabs.filter((t) => t !== tab)}
            onCommit={(v) => { onChange(renameTab(l, tab, v)); setAnnounce(`Pestaña renombrada: ${v}.`); }} />
          <div className="row ld-tools">
            <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} aria-label={`Mover pestaña ${tab} antes`} disabled={idx === 0}
              onClick={() => { onChange(stepTab(l, tab, -1)); setAnnounce(`Pestaña ${tab}: posición ${idx} de ${tabs.length}.`); }} />
            <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} aria-label={`Mover pestaña ${tab} después`} disabled={idx === tabs.length - 1}
              onClick={() => { onChange(stepTab(l, tab, 1)); setAnnounce(`Pestaña ${tab}: posición ${idx + 2} de ${tabs.length}.`); }} />
            <Button variant="ghost" size="sm" icon={Plus} onClick={() => addSec(tab)}>Sección</Button>
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar pestaña ${tab}`} title="Quitar la pestaña y sus secciones (sus campos vuelven a «Sin colocar»)"
              disabled={tabs.length === 1} onClick={() => { onChange(removeTab(l, tab)); setAnnounce(`Pestaña ${tab} quitada.`); }} />
          </div>
        </div>
        {items.map(({ s, i }) => renderSection(s, i))}
      </div>
    );
  }

  const tabs = tabsOf(l);
  const optField = options ? byName.get(options.toLowerCase()) : undefined;
  const optPos = options ? findField(l, options) : null;
  const optItem = optPos ? l.sections[optPos.section].fields[optPos.index] : undefined;

  return (
    <div className="ld" ref={root}>
      <p id="ld-help" className="sr-only">Espacio para tomar el campo; flechas arriba y abajo para moverlo entre secciones; Espacio para soltar; Escape para cancelar; Suprimir para quitarlo del formulario.</p>
      <div className="sr-only" aria-live="assertive" aria-atomic="true">{announce}</div>

      <aside
        className={`ld-palette${dropTarget === 'palette' ? ' drop-end' : ''}`}
        aria-labelledby="ld-palette-title"
        onDragOver={(e) => overField(e, 'palette')}
        onDrop={(e) => dropFieldAt(e, PALETTE, null)}
      >
        <h3 id="ld-palette-title" className="ld-pane-title">Sin colocar <Badge>{unplaced.length}</Badge></h3>
        <p className="subtle ld-pane-hint">
          Los campos sin colocar aparecen al final, en «Otros datos»{isDocument ? ' (las fórmulas, en «Totales»)' : ''}. Arrástralos a una sección.
        </p>
        {unplaced.length ? (
          <ul className="ld-grid cols-1" aria-label="Campos sin colocar">{unplaced.map((f) => renderChip(f.name, null))}</ul>
        ) : <p className="subtle ld-drop-hint">Todos los campos están en el formulario.</p>}
      </aside>

      <div className="ld-canvas">
        <div className="row ld-toolbar">
          <Switch checked={tabsOn} onChange={(v) => { onChange(setTabs(l, v)); setAnnounce(v ? 'Secciones organizadas en la pestaña General.' : 'Pestañas quitadas.'); }} label="Organizar en pestañas" />
          <span className="grow" />
          {tabsOn
            ? <Button size="sm" icon={Plus} onClick={() => { const r = addTab(l); autoNames.current.add(r.layout.sections[r.index].name); onChange(r.layout); setAnnounce(`Pestaña ${r.tab} agregada con una sección.`); }}>Agregar pestaña</Button>
            : <Button size="sm" icon={Plus} onClick={() => addSec()}>Agregar sección</Button>}
          <Button size="sm" variant="ghost" icon={Trash2} onClick={() => { setGrabbed(null); onChange(null); setAnnounce('Layout quitado: el formulario mostrará los campos en orden.'); }}>Quitar layout</Button>
        </div>
        <p className="subtle ld-pane-hint">Arrastra campos y secciones, o con el teclado: Espacio toma un campo, las flechas lo mueven y Espacio lo suelta.</p>
        {l.sections.length === 0 && <Alert tone="warning" title="El layout no tiene secciones">Agrega una sección o quita el layout.</Alert>}
        {tabsOn ? tabs.map((t, i) => renderTabGroup(t, i, tabs)) : l.sections.map(renderSection)}
        {drag?.kind === 'section' && !tabsOn && (
          <div className={`ld-end-zone${dropTarget === 'end' ? ' drop-end' : ''}`} onDragOver={(e) => overSection(e, 'end')} onDrop={(e) => dropSectionAt(e, { end: true })}>
            Soltar aquí para moverla al final
          </div>
        )}
      </div>

      <LayoutPreview layout={l} fields={placeable} isDocument={isDocument} />

      <Dialog
        open={!!optField}
        onClose={() => setOptions(null)}
        title={optField ? `Campo «${optField.label || optField.name}»` : ''}
        description="Dónde va y cuándo se muestra. Los cambios se ven en la vista previa."
        footer={
          <>
            {optPos && <Button variant="ghost" icon={X} onClick={() => { onChange(moveField(l, options!, PALETTE)); setOptions(null); }}>Quitar del formulario</Button>}
            <Button variant="primary" onClick={() => setOptions(null)}>Listo</Button>
          </>
        }
      >
        {optField && (
          <div className="stack">
            <Select label="Sección" value={optPos ? String(optPos.section) : ''} data-autofocus
              onChange={(e) => {
                const v = e.target.value;
                onChange(v === '' ? moveField(l, options!, PALETTE) : dropField(l, options!, Number(v), null));
              }}>
              <option value="">Sin colocar (va en «Otros datos»)</option>
              {l.sections.map((s, i) => <option key={i} value={i}>{s.tab ? `${s.tab} › ` : ''}{sectionLabel(i)}</option>)}
            </Select>
            <Switch
              checked={typeof optItem === 'object' && !!optItem.wide}
              disabled={!optPos || l.sections[optPos.section].columns === 1}
              onChange={(v) => onChange(patchItem(l, options!, { wide: v }))}
              label="Ancho completo (ocupa las dos columnas)"
            />
            <Input
              label="Visible solo si"
              className="mono"
              maxLength={1000}
              placeholder="Siempre visible"
              disabled={!optPos}
              hint={optPos ? "Expresión, p. ej. Estado = 'Rechazada'. Un campo obligatorio oculto por su condición no se exige." : 'Coloca el campo en una sección para darle una condición.'}
              value={typeof optItem === 'object' ? optItem.visibleWhen ?? '' : ''}
              onChange={(e) => onChange(patchItem(l, options!, { visibleWhen: e.target.value }))}
            />
          </div>
        )}
      </Dialog>
    </div>
  );
}

/**
 * Nombre de una pestaña: se confirma al salir del campo o con Enter. Escribirlo letra por letra no puede chocar con otra
 * pestaña a medio camino (dos pestañas con el mismo nombre se juntarían en una sola).
 */
function TabNameInput({ index, value, others, onCommit }: { index: number; value: string; others: string[]; onCommit: (v: string) => void }) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  const v = text.trim();
  const error = !v ? 'Escribe un nombre' : others.includes(v) ? 'Ya hay una pestaña con ese nombre' : undefined;
  const commit = () => { if (!error && v !== value) onCommit(v); else if (error) setText(value); };
  return (
    <Input inputSize="sm" wrapClassName="grow" aria-label={`Nombre de la pestaña ${index + 1}`} value={text} maxLength={60} error={error}
      onChange={(e) => setText(e.target.value)} onBlur={commit}
      onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); commit(); } if (e.key === 'Escape') setText(value); }} />
  );
}
