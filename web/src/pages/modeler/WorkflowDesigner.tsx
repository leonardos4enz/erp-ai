import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { Bot, Flag, Lock, Plus, Spline, Trash2, Workflow as WorkflowIcon, X } from 'lucide-react';
import type { FieldDef, StateColor, Workflow, WorkflowState, WorkflowTransitionDef } from '../../api/types';
import { ConfirmDialog } from '../../components/Dialog';
import { IconPicker } from '../../components/IconPicker';
import { TagInput } from '../../components/TagInput';
import { Badge, Button, Checkbox, EmptyState, Field, Input, Select, Switch } from '../../components/ui';
import { IDENTIFIER_RE } from '../../lib/format';
import {
  LABEL_H, NODE_H, NODE_W, addState, clip, addTransition, layoutDiagram, newWorkflow, patchObject, removeState, renameState, stateImpact, uniqueName,
} from '../../lib/workflowEdit';

const COLORS: { value: StateColor; label: string }[] = [
  { value: 'neutral', label: 'Neutro' }, { value: 'accent', label: 'Acento' }, { value: 'success', label: 'Éxito' },
  { value: 'warning', label: 'Advertencia' }, { value: 'danger', label: 'Peligro' },
];
const COMPUTED = new Set(['formula', 'autonumber', 'lines', 'state', 'parent']);

type Selection = { kind: 'state' | 'transition'; name: string } | null;

export interface WorkflowDesignerProps {
  workflow: Workflow | null;
  onChange: (w: Workflow) => void;
  /** Estados y transiciones ya guardados: su identificador no se cambia (registros, historial y automatizaciones lo usan). */
  savedStates: Set<string>;
  savedTransitions: Set<string>;
  fields: FieldDef[];
  roleNames: string[] | null;
}

export function WorkflowDesigner({ workflow, onChange, savedStates, savedTransitions, fields, roleNames }: WorkflowDesignerProps) {
  const [sel, setSel] = useState<Selection>(null);
  const [announce, setAnnounce] = useState('');
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [link, setLink] = useState<{ from: string; x: number; y: number; active: boolean } | null>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const suppressClick = useRef(false);
  const focusAfter = useRef<string | null>(null);
  const root = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLDivElement>(null);
  const [canvasWidth, setCanvasWidth] = useState(0);

  // El diagrama se reduce para caber en su recuadro (hasta 70 %); más grande que eso, se desplaza.
  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setCanvasWidth(el.clientWidth));
    ro.observe(el);
    setCanvasWidth(el.clientWidth);
    return () => ro.disconnect();
  }, [workflow === null]);

  useEffect(() => {
    if (!focusAfter.current) return;
    const s = focusAfter.current;
    focusAfter.current = null;
    root.current?.querySelector<HTMLElement>(s)?.focus();
  });

  if (!workflow) {
    return (
      <div className="card card-pad">
        <EmptyState
          icon={WorkflowIcon}
          title="Este catálogo no tiene proceso"
          description="Un proceso define los estados de un documento (Borrador, Autorizada…) y las transiciones entre ellos, con roles, condiciones y campos obligatorios."
          action={<Button variant="primary" icon={Plus} onClick={() => { const w = newWorkflow(); onChange(w); setSel({ kind: 'state', name: w.initial }); }}>Crear proceso</Button>}
        />
      </div>
    );
  }
  const w = workflow;
  const d = layoutDiagram(w);
  const stateOf = (n: string) => w.states.find((s) => s.name.toLowerCase() === n.toLowerCase());
  const labelOf = (n: string) => stateOf(n)?.label || n;
  const outgoing = (n: string) => w.transitions.filter((t) => t.from.some((f) => f.toLowerCase() === n.toLowerCase()));
  const selState = sel?.kind === 'state' ? stateOf(sel.name) : undefined;
  const selTransition = sel?.kind === 'transition' ? w.transitions.find((t) => t.name === sel.name) : undefined;
  const nodeSel = (n: string) => `[data-wf-state="${CSS.escape(n)}"]`;

  function select(kind: 'state' | 'transition', name: string) {
    setSel({ kind, name });
  }
  function onItemKey(e: KeyboardEvent, kind: 'state' | 'transition', name: string) {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(kind, name); }
  }

  /* ---------- Conectar arrastrando de un estado a otro ---------- */
  function svgPoint(clientX: number, clientY: number) {
    const r = svgRef.current!.getBoundingClientRect();
    return { x: ((clientX - r.left) * d.width) / r.width, y: ((clientY - r.top) * d.height) / r.height };
  }
  function onNodePointerDown(e: ReactPointerEvent, from: string) {
    if (e.button !== 0) return;
    const start = { x: e.clientX, y: e.clientY };
    let active = false;
    const move = (ev: PointerEvent) => {
      active = active || Math.hypot(ev.clientX - start.x, ev.clientY - start.y) > 6;
      if (!active) return;
      const p = svgPoint(ev.clientX, ev.clientY);
      setLink({ from, x: p.x, y: p.y, active });
    };
    const up = (ev: PointerEvent) => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      setLink(null);
      if (!active) return;
      suppressClick.current = true;
      // El clic que sigue a soltar cae en otro elemento: se descarta en el siguiente ciclo.
      setTimeout(() => { suppressClick.current = false; }, 0);
      const target = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('[data-wf-state]')?.getAttribute('data-wf-state');
      if (target) connect(from, target);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  }
  function connect(from: string, to: string) {
    const r = addTransition(w, from, to);
    onChange(r.workflow);
    setSel({ kind: 'transition', name: r.name });
    setAnnounce(`Transición creada de ${labelOf(from)} a ${labelOf(to)}. Edita sus propiedades en el panel.`);
  }

  /* ---------- Edición ---------- */
  function patchState(name: string, patch: Partial<WorkflowState>) {
    onChange({ ...w, states: w.states.map((s) => (s.name === name ? patchObject(s, patch, ['editable']) : s)) });
  }
  function patchTransition(name: string, patch: Partial<WorkflowTransitionDef>) {
    onChange({ ...w, transitions: w.transitions.map((t) => (t.name === name ? patchObject(t, patch, ['from']) : t)) });
  }
  function doDelete(name: string) {
    onChange(removeState(w, name));
    setSel(null);
    setConfirmDelete(null);
    setAnnounce(`Estado ${labelOf(name)} eliminado.`);
    focusAfter.current = nodeSel(w.initial);
  }

  const capturable = fields.filter((f) => !COMPUTED.has(f.type) && f.editable !== false).map((f) => f.name);
  const assignable = fields.filter((f) => !COMPUTED.has(f.type));

  function renderStatePanel(s: WorkflowState) {
    const saved = savedStates.has(s.name);
    const isInitial = s.name === w.initial;
    const nameErr = !IDENTIFIER_RE.test(s.name) ? 'Solo letras, números y guion bajo; inicia con letra' : w.states.filter((x) => x.name.toLowerCase() === s.name.toLowerCase()).length > 1 ? 'Repetido' : undefined;
    const out = outgoing(s.name);
    return (
      <>
        <h3 className="ld-pane-title">Estado</h3>
        <Input label="Etiqueta" required value={s.label} maxLength={100} error={s.label.trim() ? undefined : 'Escribe una etiqueta'} data-autofocus
          onChange={(e) => {
            const label = e.target.value;
            // Un estado nuevo toma su identificador de la etiqueta, como los campos.
            if (!saved && s.name === uniqueName(s.label, w.states.filter((x) => x !== s).map((x) => x.name))) {
              const name = uniqueName(label || 'Estado', w.states.filter((x) => x !== s).map((x) => x.name));
              const renamed = renameState(w, s.name, name);
              onChange({ ...renamed, states: renamed.states.map((x) => (x.name === name ? { ...x, label } : x)) });
              setSel({ kind: 'state', name });
            } else patchState(s.name, { label });
          }} />
        <Input label="Identificador" className="mono" value={s.name} disabled={saved} error={saved ? undefined : nameErr} maxLength={50}
          hint={saved ? 'Ya está guardado: los registros y su historial lo usan.' : 'Se usa en reglas y automatizaciones.'}
          onChange={(e) => { const name = e.target.value.replace(/[^A-Za-z0-9_]/g, ''); onChange(renameState(w, s.name, name)); setSel({ kind: 'state', name }); }} />
        <Select label="Color" value={s.color ?? 'neutral'} onChange={(e) => patchState(s.name, { color: e.target.value as StateColor })}>
          {COLORS.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
        </Select>
        <Switch checked={s.editable !== false} onChange={(v) => patchState(s.name, { editable: v })} label="Se puede editar el registro en este estado" />
        {isInitial
          ? <div><Badge tone="accent" icon={Flag}>Estado inicial</Badge></div>
          : <div><Button size="sm" icon={Flag} onClick={() => { onChange({ ...w, initial: s.name }); setAnnounce(`${s.label} es ahora el estado inicial.`); }}>Marcar como inicial</Button></div>}
        <Field label="Transiciones que salen">
          {out.length ? (
            <ul className="wf-out-list">
              {out.map((t) => <li key={t.name}><button type="button" className="link-btn" onClick={() => select('transition', t.name)}>{t.label} → {labelOf(t.to)}</button></li>)}
            </ul>
          ) : <p className="subtle">Ninguna: es un estado final.</p>}
        </Field>
        <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
          <Button size="sm" icon={Spline} onClick={() => {
            const to = w.states.find((x) => x.name !== s.name)?.name ?? s.name;
            const r = addTransition(w, s.name, to);
            onChange(r.workflow);
            setSel({ kind: 'transition', name: r.name });
            setAnnounce(`Transición agregada desde ${s.label}. Elige su destino en el panel.`);
          }}>Agregar transición desde aquí</Button>
          <Button size="sm" variant="danger" icon={Trash2} disabled={isInitial || w.states.length === 1}
            title={isInitial ? 'Marca otro estado como inicial antes de eliminarlo' : undefined} onClick={() => setConfirmDelete(s.name)}>Eliminar estado</Button>
        </div>
        {isInitial && <p className="subtle">El estado inicial no se puede eliminar: marca otro como inicial primero.</p>}
      </>
    );
  }

  function renderTransitionPanel(t: WorkflowTransitionDef) {
    const saved = savedTransitions.has(t.name);
    const nameErr = !IDENTIFIER_RE.test(t.name) ? 'Solo letras, números y guion bajo; inicia con letra' : w.transitions.filter((x) => x.name.toLowerCase() === t.name.toLowerCase()).length > 1 ? 'Repetido' : undefined;
    const rename = (name: string) => { patchTransition(t.name, { name }); setSel({ kind: 'transition', name }); };
    return (
      <>
        <h3 className="ld-pane-title">Transición</h3>
        <Input label="Etiqueta del botón" required value={t.label} maxLength={100} error={t.label.trim() ? undefined : 'Escribe una etiqueta'} data-autofocus
          onChange={(e) => {
            const label = e.target.value;
            if (!saved && t.name === uniqueName(t.label, w.transitions.filter((x) => x !== t).map((x) => x.name))) {
              const name = uniqueName(label || 'Transicion', w.transitions.filter((x) => x !== t).map((x) => x.name));
              onChange({ ...w, transitions: w.transitions.map((x) => (x === t ? { ...x, label, name } : x)) });
              setSel({ kind: 'transition', name });
            } else patchTransition(t.name, { label });
          }} />
        <Input label="Identificador" className="mono" value={t.name} disabled={saved} error={saved ? undefined : nameErr} maxLength={50}
          hint={saved ? 'Ya está guardada: el historial y las automatizaciones la nombran.' : undefined}
          onChange={(e) => rename(e.target.value.replace(/[^A-Za-z0-9_]/g, ''))} />
        <fieldset className="wf-fieldset">
          <legend className="field-label">Desde</legend>
          {w.states.map((s) => (
            <Checkbox key={s.name} label={s.label || s.name} checked={t.from.includes(s.name)}
              onChange={(v) => patchTransition(t.name, { from: v ? [...t.from, s.name] : t.from.filter((f) => f !== s.name) })} />
          ))}
          {t.from.length === 0 && <span className="field-error" role="alert">Elige al menos un estado de origen.</span>}
        </fieldset>
        <Select label="Hacia" value={t.to} onChange={(e) => patchTransition(t.name, { to: e.target.value })}>
          {!stateOf(t.to) && <option value={t.to}>{t.to} (no existe)</option>}
          {w.states.map((s) => <option key={s.name} value={s.name}>{s.label || s.name}</option>)}
        </Select>
        <Field label="Roles que la pueden ejecutar" hint="Vacío: quien tenga el alcance de escritura del registro.">
          <TagInput value={t.roles ?? []} onChange={(v) => patchTransition(t.name, { roles: v })} suggestions={roleNames ?? undefined} placeholder="Agregar rol…" aria-label="Roles de la transición" />
        </Field>
        <Switch checked={!!t.requireComment} onChange={(v) => patchTransition(t.name, { requireComment: v })} label="Pedir un comentario" />
        <Field label="Campos obligatorios al ejecutarla" hint="Se piden en el diálogo de la transición.">
          <TagInput value={t.requiredFields ?? []} onChange={(v) => patchTransition(t.name, { requiredFields: v })} suggestions={capturable} placeholder="Agregar campo…" aria-label="Campos obligatorios de la transición" />
        </Field>
        <Input label="Condición" className="mono" maxLength={1000} placeholder="Siempre disponible" value={t.when ?? ''}
          hint="Expresión, p. ej. Total > 0. Si no se cumple, el botón se muestra bloqueado." onChange={(e) => patchTransition(t.name, { when: e.target.value })} />
        <Input label="Mensaje si no se cumple" maxLength={300} value={t.whenMessage ?? ''} disabled={!t.when} onChange={(e) => patchTransition(t.name, { whenMessage: e.target.value })} />
        <Field label="Asignaciones al ejecutarla" hint="Campo = expresión, p. ej. FechaAutorizacion = TODAY().">
          <div className="stack" style={{ gap: 'var(--space-2)' }}>
            {(t.set ?? []).map((a, i) => (
              <div key={i} className="row wf-assign">
                <Select selectSize="sm" aria-label={`Campo de la asignación ${i + 1}`} value={a.field}
                  onChange={(e) => patchTransition(t.name, { set: (t.set ?? []).map((x, j) => (j === i ? { ...x, field: e.target.value } : x)) })}>
                  {!assignable.some((f) => f.name === a.field) && <option value={a.field}>{a.field || 'Elige…'}</option>}
                  {assignable.map((f) => <option key={f.name} value={f.name}>{f.label || f.name}</option>)}
                </Select>
                <Input inputSize="sm" wrapClassName="grow" className="mono" aria-label={`Valor de la asignación ${i + 1}`} value={a.value} placeholder="TODAY()"
                  onChange={(e) => patchTransition(t.name, { set: (t.set ?? []).map((x, j) => (j === i ? { ...x, value: e.target.value } : x)) })} />
                <Button variant="ghost" size="sm" iconOnly icon={X} aria-label={`Quitar la asignación ${i + 1}`} onClick={() => patchTransition(t.name, { set: (t.set ?? []).filter((_, j) => j !== i) })} />
              </div>
            ))}
            <div><Button size="sm" icon={Plus} disabled={!assignable.length} onClick={() => patchTransition(t.name, { set: [...(t.set ?? []), { field: assignable[0]?.name ?? '', value: '' }] })}>Agregar asignación</Button></div>
          </div>
        </Field>
        <IconPicker label="Icono del botón" allowNone value={t.icon} onChange={(icon) => patchTransition(t.name, { icon })} />
        <Select label="Estilo del botón" value={t.variant ?? ''} onChange={(e) => patchTransition(t.name, { variant: (e.target.value || undefined) as WorkflowTransitionDef['variant'] })}>
          <option value="">Normal</option>
          <option value="primary">Principal</option>
          <option value="secondary">Secundario</option>
          <option value="danger">Peligro</option>
        </Select>
        <Switch checked={!!t.system} onChange={(v) => patchTransition(t.name, { system: v })} label="Solo la ejecuta una automatización (no aparece como botón)" />
        <div>
          <Button size="sm" variant="danger" icon={Trash2} onClick={() => {
            onChange({ ...w, transitions: w.transitions.filter((x) => x !== t) });
            setSel(null);
            setAnnounce(`Transición ${t.label} eliminada.`);
          }}>Eliminar transición</Button>
        </div>
      </>
    );
  }

  const impact = confirmDelete ? stateImpact(w, confirmDelete) : null;
  const scale = canvasWidth ? Math.min(1, Math.max(0.7, (canvasWidth - 2) / d.width)) : 1;
  const linkFrom = link?.active ? d.nodes.find((n) => n.name === link.from) : undefined;

  return (
    <div className="wf" ref={root}>
      <div className="sr-only" aria-live="assertive" aria-atomic="true">{announce}</div>
      <div className="wf-main">
        <div className="row ld-toolbar">
          <Button size="sm" icon={Plus} onClick={() => {
            const r = addState(w);
            onChange(r.workflow);
            setSel({ kind: 'state', name: r.name });
            setAnnounce('Estado agregado. Escribe su etiqueta en el panel.');
          }}>Agregar estado</Button>
          <Button size="sm" icon={Spline} onClick={() => {
            const from = selState?.name ?? w.initial;
            const to = w.states.find((x) => x.name !== from)?.name ?? from;
            const r = addTransition(w, from, to);
            onChange(r.workflow);
            setSel({ kind: 'transition', name: r.name });
            setAnnounce(`Transición agregada desde ${labelOf(from)}. Elige su destino en el panel.`);
          }}>Agregar transición</Button>
          <span className="grow" />
          <span className="subtle wf-legend"><Lock size={13} aria-hidden /> no editable · <Bot size={13} aria-hidden /> solo automatización</span>
        </div>
        <p className="subtle ld-pane-hint">Arrastra de un estado a otro para conectarlos. Con el teclado: Tab recorre estados y transiciones y Enter abre sus propiedades.</p>
        <div ref={canvasRef} className="wf-canvas" tabIndex={0} role="region" aria-label="Diagrama del proceso (se desplaza con las flechas)">
          <svg ref={svgRef} width={Math.round(d.width * scale)} height={Math.round(d.height * scale)} viewBox={`0 0 ${d.width} ${d.height}`} role="group" aria-label={`Proceso: ${w.states.length} estados y ${w.transitions.length} transiciones`}>
            <defs>
              <marker id="wf-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
                <path d="M 0 0 L 10 5 L 0 10 z" className="wf-arrow" />
              </marker>
              <marker id="wf-arrow-sel" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
                <path d="M 0 0 L 10 5 L 0 10 z" className="wf-arrow-sel" />
              </marker>
            </defs>
            {d.edges.map((e) => {
              const t = w.transitions.find((x) => x.name === e.transition)!;
              const on = sel?.kind === 'transition' && sel.name === e.transition;
              return <path key={e.key} d={e.path} className={`wf-edge${on ? ' selected' : ''}${t.system ? ' system' : ''}`} markerEnd={`url(#${on ? 'wf-arrow-sel' : 'wf-arrow'})`} aria-hidden />;
            })}
            {linkFrom && <line x1={linkFrom.x + NODE_W / 2} y1={linkFrom.y + NODE_H / 2} x2={link!.x} y2={link!.y} className="wf-edge linking" aria-hidden />}
            {d.nodes.map((n) => {
              const s = stateOf(n.name)!;
              const isInitial = s.name === w.initial;
              const out = outgoing(s.name).length;
              const on = sel?.kind === 'state' && sel.name === s.name;
              const name = `Estado ${s.label || s.name}${isInitial ? ', inicial' : ''}${s.editable === false ? ', no editable' : ', editable'}, ${out === 1 ? '1 transición sale' : `${out} transiciones salen`}`;
              return (
                <g
                  key={n.name}
                  role="button"
                  tabIndex={0}
                  aria-pressed={on}
                  aria-label={name}
                  data-wf-state={s.name}
                  className={`wf-node wf-tone-${s.color ?? 'neutral'}${on ? ' selected' : ''}`}
                  transform={`translate(${n.x} ${n.y})`}
                  onPointerDown={(e) => onNodePointerDown(e, s.name)}
                  onClick={() => { if (!suppressClick.current) select('state', s.name); }}
                  onKeyDown={(e) => onItemKey(e, 'state', s.name)}
                >
                  {isInitial && <><circle cx={-22} cy={NODE_H / 2} r={5} className="wf-start" /><line x1={-17} y1={NODE_H / 2} x2={-3} y2={NODE_H / 2} className="wf-edge" markerEnd="url(#wf-arrow)" /></>}
                  <rect className="wf-node-box" width={NODE_W} height={NODE_H} rx={10} />
                  <text x={12} y={21} className="wf-node-label">{clip(s.label || s.name, 18)}</text>
                  <text x={12} y={38} className="wf-node-sub">{clip(s.name, 22)}</text>
                  {s.editable === false && <Lock x={NODE_W - 24} y={8} size={14} className="wf-node-icon" aria-hidden />}
                </g>
              );
            })}
            {d.edges.map((e) => {
              const t = w.transitions.find((x) => x.name === e.transition)!;
              const on = sel?.kind === 'transition' && sel.name === e.transition;
              const text = e.labelText;
              const width = e.labelWidth;
              return (
                <g
                  key={`l-${e.key}`}
                  role="button"
                  tabIndex={0}
                  aria-pressed={on}
                  aria-label={`Transición ${t.label || t.name}: de ${labelOf(e.from)} a ${labelOf(e.to)}${t.system ? ' (solo automatización)' : ''}`}
                  data-wf-transition={t.name}
                  className={`wf-edge-label${on ? ' selected' : ''}`}
                  transform={`translate(${e.labelX - width / 2} ${e.labelY - LABEL_H / 2})`}
                  onClick={() => select('transition', t.name)}
                  onKeyDown={(ev) => onItemKey(ev, 'transition', t.name)}
                >
                  <rect width={width} height={LABEL_H} rx={LABEL_H / 2} />
                  {t.system && <Bot x={8} y={4} size={14} className="wf-node-icon" aria-hidden />}
                  <text x={t.system ? 26 : 9} y={15}>{text}</text>
                </g>
              );
            })}
          </svg>
        </div>
      </div>

      <aside className="wf-panel card card-pad stack" aria-label="Propiedades">
        {selState ? renderStatePanel(selState) : selTransition ? renderTransitionPanel(selTransition) : (
          <>
            <h3 className="ld-pane-title">Propiedades</h3>
            <p className="subtle">Selecciona un estado o una transición del diagrama para editarlo.</p>
            <p className="subtle">{w.states.length} estados · {w.transitions.length} transiciones · inicial: {labelOf(w.initial)}</p>
          </>
        )}
      </aside>

      <ConfirmDialog
        open={!!confirmDelete}
        danger
        title={`Eliminar el estado «${confirmDelete ? labelOf(confirmDelete) : ''}»`}
        confirmLabel="Eliminar estado"
        onCancel={() => setConfirmDelete(null)}
        onConfirm={() => doDelete(confirmDelete!)}
        message={impact && (
          <div className="stack" style={{ gap: 'var(--space-2)' }}>
            {impact.removed.length > 0 && <div>Se quitan las transiciones: {impact.removed.map((t) => t.label || t.name).join(', ')}.</div>}
            {impact.trimmed.length > 0 && <div>Dejan de salir de este estado: {impact.trimmed.map((t) => t.label || t.name).join(', ')}.</div>}
            {impact.removed.length + impact.trimmed.length === 0 && <div>Ninguna transición lo usa.</div>}
            {confirmDelete && savedStates.has(confirmDelete) && (
              <div className="field-error">Ya está guardado: los registros que estén en este estado se quedan con él, sin transiciones disponibles.</div>
            )}
            <div className="subtle">El cambio se aplica hasta revisar y aplicar el plan.</div>
          </div>
        )}
      />
    </div>
  );
}
