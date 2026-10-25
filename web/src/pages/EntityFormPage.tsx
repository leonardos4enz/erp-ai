import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, RefreshCw, Save, Trash2 } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { dataApi, type Warnings } from '../api/endpoints';
import type { EntityDef, FieldDef, LayoutState, RecordAccess, RecordTransition, RecordWorkflow, Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog } from '../components/Dialog';
import { resolveIcon } from '../components/DynamicIcon';
import { FieldInput } from '../components/FieldInput';
import { LinesEditor } from '../components/LinesEditor';
import { useToast } from '../components/Toast';
import { WarningsAlert } from '../components/Warnings';
import { HistoryPanel, stateLabel, TransitionDialog, WorkflowBar } from '../components/Workflow';
import { Alert, Button, LoadingBlock, PageHeader, Tabs } from '../components/ui';
import { formSections, formTabs, hasLayoutConditions, placedFields, type FormSection } from '../lib/layout';
import {
  blankLine, buildPayload, draftFromRow, isEmptyValue, lineErrorKey, lineFields, lookupFilterFor, mergeComputed, type Draft,
} from '../lib/draft';
import { activeFields, formatFormula, isEditableField, isRequiredFor, lockHint, StateBadge, useChildDefs, useEntityDef, withState } from '../lib/entity';
import { formatDateTime } from '../lib/format';
import { ForbiddenPage, NotFoundPage } from './StatusPages';

const COMPUTE_DEBOUNCE_MS = 350;
const EMPTY_DRAFT: Draft = { values: {}, touched: {}, lines: {} };

/** ¿La entidad (o sus partidas) tiene algo que calcule el servidor? */
function needsCompute(def: EntityDef, children: Record<string, EntityDef>): boolean {
  const calc = (f: FieldDef) => f.type === 'formula' || f.type === 'autonumber' || !!f.defaultValue;
  return activeFields(def).some((f) => calc(f) || f.type === 'lines')
    || Object.values(children).some((c) => activeFields(c).some(calc))
    // Spec 007: el servidor dice qué secciones y campos ocultar mientras se captura.
    || hasLayoutConditions(def)
    // Spec 023: y qué avisos de reglas quedan con lo capturado.
    || [def, ...Object.values(children)].some((d) => d.rules?.some((r) => r.severity === 'warning'));
}

/** Validación local: obligatorios (incluye "obligatorio para rol"), números y longitudes. */
function validateFields(fields: FieldDef[], values: Record<string, unknown>, roles: string[], keyOf: (f: FieldDef) => string, out: Record<string, string>) {
  for (const f of fields) {
    if (!isEditableField(f)) continue;
    const v = values[f.name];
    const key = keyOf(f);
    if (isRequiredFor(f, roles) && isEmptyValue(f, v)) out[key] = 'Este campo es obligatorio';
    else if ((f.type === 'integer' || f.type === 'decimal') && v !== '' && v != null && Number.isNaN(Number(v))) out[key] = 'Debe ser un número';
    else if (f.maxLength && typeof v === 'string' && v.length > f.maxLength) out[key] = `Máximo ${f.maxLength} caracteres`;
  }
}

export function EntityFormPage() {
  const { entity = '', id } = useParams();
  const isNew = !id;
  const navigate = useNavigate();
  const toast = useToast();
  const { me, perms } = useAuth();
  const { def: baseDef, loading: defLoading } = useEntityDef(entity);
  const { children: baseChildren, loading: childrenLoading } = useChildDefs(baseDef);
  const p = perms(entity);
  const roles = me?.roles ?? [];

  const [row, setRow] = useState<Row | undefined>();
  // Spec 006: campos editables solo en ciertos estados (encabezado y partidas usan el estado del documento).
  const docState = isNew ? baseDef?.workflow?.initial : (row?.Estado as string | undefined);
  const def = useMemo(() => (baseDef ? withState(baseDef, docState) : undefined), [baseDef, docState]);
  const children = useMemo(
    () => Object.fromEntries(Object.entries(baseChildren).map(([k, c]) => [k, withState(c, docState)])),
    [baseChildren, docState],
  );
  const fields = useMemo(() => (def ? activeFields(def) : []), [def]);
  const [draft, setDraft] = useState<Draft>(EMPTY_DRAFT);
  const [loadError, setLoadError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(!isNew);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});
  const [conflict, setConflict] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [transition, setTransition] = useState<RecordTransition | null>(null);
  const [dirty, setDirty] = useState(false);
  const [editTick, setEditTick] = useState(0);
  // Spec 007: visibilidad condicional del layout (la calcula el servidor) y pestaña activa.
  const [layoutState, setLayoutState] = useState<LayoutState | null>(null);
  // Spec 023: avisos de reglas (los calcula el servidor al abrir, al guardar y mientras se captura).
  const [warnings, setWarnings] = useState<Warnings | null>(null);
  const [tab, setTab] = useState('');

  const draftRef = useRef(draft);
  draftRef.current = draft;
  const computeSeq = useRef(0);
  const computeAbort = useRef<AbortController | null>(null);
  const ready = !!def && !childrenLoading;
  const calc = ready && needsCompute(def!, children);

  /** Pide al servidor valores sugeridos y fórmulas del borrador actual. Ignora respuestas viejas. */
  const runCompute = useCallback(async () => {
    if (!def || !calc) return;
    const seq = ++computeSeq.current;
    computeAbort.current?.abort();
    const ctrl = new AbortController();
    computeAbort.current = ctrl;
    try {
      const r = await dataApi.compute(def.name, buildPayload(def, children, draftRef.current, isNew), id, ctrl.signal);
      if (seq !== computeSeq.current || !r?.values) return;
      setDraft((d) => mergeComputed(def, children, d, r.values));
      if (r.layout !== undefined) setLayoutState(r.layout ?? null);
      if (r.warnings !== undefined) setWarnings(r.warnings ?? null);
    } catch {
      /* el cálculo en vivo es opcional: el servidor recalcula al guardar */
    }
  }, [def, children, calc, isNew, id]);

  const applyRow = useCallback((r: Row) => {
    if (!def) return;
    setRow(r);
    setDraft(draftFromRow(def, children, r));
    setLayoutState((r._layout as LayoutState | undefined) ?? null);
    setWarnings((r._warnings as Warnings | undefined) ?? null);
    setDirty(false);
    setConflict(false);
    setError(null);
    setClientErrors({});
  }, [def, children]);

  async function load() {
    if (!def || isNew || !id) return;
    setLoading(true);
    setLoadError(null);
    try {
      applyRow(await dataApi.get(def.name, id));
    } catch (e) {
      setLoadError(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (!ready) return;
    if (isNew) {
      setRow(undefined);
      setLayoutState(null);
      setWarnings(null);
      setDraft(draftFromRow(def!, children));
      setDirty(false);
      setLoading(false);
      // Valores por defecto del registro nuevo (fecha de hoy, moneda, folio…).
      if (calc) queueMicrotask(() => void runCompute());
    } else void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready, def?.name, id]);

  useEffect(() => {
    if (!editTick || !calc) return;
    const t = window.setTimeout(() => void runCompute(), COMPUTE_DEBOUNCE_MS);
    return () => window.clearTimeout(t);
  }, [editTick, calc, runCompute]);

  useEffect(() => () => computeAbort.current?.abort(), []);

  if (defLoading || (def && childrenLoading)) return <LoadingBlock />;
  if (!def) return <NotFoundPage />;
  if (!p.read || (isNew && (!p.create || def.systemManaged))) return <ForbiddenPage />;
  if (loading) return <LoadingBlock />;
  if (loadError) return loadError.status === 404 ? <NotFoundPage /> : loadError.status === 403 ? <ForbiddenPage /> : (
    <div className="page page-narrow"><Alert tone="danger" title={loadError.title}>{loadError.detail}</Alert></div>
  );

  const wf = (!isNew ? (row?._workflow as RecordWorkflow | undefined) : undefined) ?? undefined;
  const lockedByState = !!wf && wf.editable === false;
  // Spec 013: el servidor dice si este registro está en el alcance de escritura del usuario.
  const access = !isNew ? (row?._access as RecordAccess | undefined) : undefined;
  const outOfScope = !!access?.reason;
  const readOnly = !isNew && (!p.update || lockedByState || !!def.systemManaged || access?.update === false);
  const listPath = `/c/${encodeURIComponent(def.name)}`;
  const Icon = resolveIcon(def.icon);
  const linesFields = fields.filter((f) => f.type === 'lines');
  const isDocument = linesFields.length > 0;
  const headerFields = fields.filter((f) => f.type !== 'lines' && f.type !== 'parent' && !(f.type === 'state' && def.workflow));
  // Spec 007: con layout, las fórmulas que menciona van en su sección; las demás siguen en los totales del documento.
  const placed = placedFields(def);
  const isTotal = (f: FieldDef) => isDocument && f.type === 'formula' && !placed.has(f.name.toLowerCase());
  const sections = formSections(def, headerFields, layoutState, isTotal);
  const totalFields = headerFields.filter(isTotal);
  // Con layout, solo lo visible: un campo oculto por condición no se valida (el servidor tampoco lo exige).
  const mainFields = sections ? sections.flatMap((s) => s.fields.map((x) => x.field)) : headerFields.filter((f) => !totalFields.includes(f));
  const tabs = sections ? formTabs(sections) : [];
  const activeTab = tabs.includes(tab) ? tab : tabs[0];
  const tabOf = (name: string) => sections?.find((s) => s.fields.some((x) => x.field.name.toLowerCase() === name.toLowerCase()))?.tab;
  const folioField = fields.find((f) => f.type === 'autonumber');
  const folio = folioField && row?.[folioField.name] ? String(row[folioField.name]) : null;

  /** Toda edición invalida cálculos en curso y programa uno nuevo. */
  function edited() {
    computeSeq.current++;
    computeAbort.current?.abort();
    setDirty(true);
    setEditTick((t) => t + 1);
  }

  function clearError(key: string) {
    if (clientErrors[key]) setClientErrors((e) => { const n = { ...e }; delete n[key]; return n; });
  }

  function setField(name: string, v: unknown, label?: string | null) {
    setDraft((d) => ({
      ...d,
      values: { ...d.values, [name]: v, ...(label !== undefined ? { [`${name}__label`]: label } : {}) },
      touched: { ...d.touched, [name]: true },
    }));
    clearError(name);
    edited();
  }

  function setLineField(lf: string, index: number, name: string, v: unknown, label?: string | null) {
    setDraft((d) => ({
      ...d,
      lines: {
        ...d.lines,
        [lf]: (d.lines[lf] ?? []).map((l, i) => (i !== index ? l : {
          ...l,
          values: { ...l.values, [name]: v, ...(label !== undefined ? { [`${name}__label`]: label } : {}) },
          touched: { ...l.touched, [name]: true },
        })),
      },
    }));
    clearError(lineErrorKey(lf, index, name));
    edited();
  }

  function addLine(lf: FieldDef) {
    const child = lf.childEntity ? children[lf.childEntity] : undefined;
    if (!child) return;
    setDraft((d) => ({ ...d, lines: { ...d.lines, [lf.name]: [...(d.lines[lf.name] ?? []), blankLine(child)] } }));
    edited();
    // Enfoca la primera celda editable de la nueva partida.
    const index = draftRef.current.lines[lf.name]?.length ?? 0;
    const first = lineFields(child).find((f) => isEditableField(f) && f.showInList !== false);
    if (first) window.setTimeout(() => document.getElementById(`f-${lf.name}-${index}-${first.name}`)?.focus(), 0);
  }

  function removeLine(lf: string, index: number) {
    setDraft((d) => ({ ...d, lines: { ...d.lines, [lf]: (d.lines[lf] ?? []).filter((_, i) => i !== index) } }));
    // Los errores por índice ya no corresponden a las mismas partidas.
    setClientErrors((e) => Object.fromEntries(Object.entries(e).filter(([k]) => !k.startsWith(`${lf}[`))));
    edited();
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (!def || readOnly) return;
    const errs: Record<string, string> = {};
    validateFields(mainFields, draft.values, roles, (f) => f.name, errs);
    for (const lf of linesFields) {
      const child = lf.childEntity ? children[lf.childEntity] : undefined;
      if (!child) continue;
      (draft.lines[lf.name] ?? []).forEach((l, i) => validateFields(lineFields(child), l.values, roles, (f) => lineErrorKey(lf.name, i, f.name), errs));
    }
    setClientErrors(errs);
    const firstKey = Object.keys(errs)[0];
    if (firstKey) {
      const m = /^(\w+)\[(\d+)\]\.(\w+)$/.exec(firstKey);
      const t = m ? undefined : tabOf(firstKey);
      // La pestaña con el error se abre sola antes de enfocar el campo.
      if (t && t !== activeTab) setTab(t);
      window.setTimeout(() => document.getElementById(m ? `f-${m[1]}-${m[2]}-${m[3]}` : `f-${firstKey}`)?.focus(), 0);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const payload = buildPayload(def, children, draft, isNew);
      if (isNew) {
        const created = await dataApi.create(def.name, payload);
        toast.success('Registro creado');
        setDirty(false);
        navigate(`${listPath}/${created.id}`, { replace: true });
      } else {
        const updated = await dataApi.update(def.name, id!, { ...payload, _rowVersion: row?._rowVersion });
        applyRow(updated);
        toast.success('Cambios guardados');
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) setConflict(true);
      else if (err instanceof ApiError) {
        setError(err);
        const t = Object.keys(err.fieldErrors).map(tabOf).find(Boolean);
        if (t) setTab(t);
      } else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  async function onDelete() {
    if (!def || !id) return;
    setDeleting(true);
    try {
      await dataApi.remove(def.name, id);
      toast.success('Registro eliminado');
      navigate(listPath, { replace: true });
    } catch (err) {
      toast.fromError(err);
      setDeleting(false);
      setConfirmDelete(false);
    }
  }

  function renderField(f: FieldDef, wide = false) {
    const fid = `f-${f.name}`;
    // Spec 013 (F-02): en un formulario de solo lectura no hay obligatorios ni pistas de edición; el aviso general explica.
    const required = !readOnly && isRequiredFor(f, roles);
    const roleHint = readOnly ? undefined : lockHint(f, def) ?? (!f.required && required && isEditableField(f) ? 'Obligatorio para tu rol' : undefined);
    return (
      <FieldInput
        key={f.name}
        field={f}
        def={def}
        id={fid}
        value={draft.values[f.name]}
        refLabel={draft.values[`${f.name}__label`] as string | null}
        onChange={(v, label) => setField(f.name, v, label)}
        disabled={readOnly || saving || !isEditableField(f)}
        required={required && isEditableField(f)}
        error={clientErrors[f.name] ?? fieldError(error, f.name)}
        hint={roleHint}
        isNew={isNew}
        filter={def ? lookupFilterFor(f, { def, values: draft.values }) : undefined}
        className={wide ? 'span-2' : undefined}
      />
    );
  }

  function renderSection(s: FormSection) {
    // Sin aria-labelledby: una región por sección sería ruido para el lector de pantalla y su nombre
    // («Fechas») competiría con la etiqueta de un campo («Fecha»). El encabezado basta para navegar.
    return (
      <section key={s.name} className="form-section">
        <h2 className="form-section-title">{s.label}</h2>
        {s.description && <p className="subtle form-section-desc">{s.description}</p>}
        <div className={`form-grid${s.columns === 1 ? ' form-grid-1' : ''}`}>{s.fields.map((x) => renderField(x.field, x.wide))}</div>
      </section>
    );
  }

  /** Campos del encabezado: rejilla simple, secciones o pestañas (spec 007). */
  function renderBody() {
    if (!sections) return mainFields.length === 0 && !isDocument
      ? <p className="muted">Este catálogo no tiene campos activos.</p>
      : <div className="form-grid">{mainFields.map((f) => renderField(f))}</div>;
    if (!tabs.length) return <div className="form-sections">{sections.map(renderSection)}</div>;
    return (
      <div>
        <Tabs items={tabs.map((t) => ({ id: t, label: t }))} value={activeTab!} onChange={setTab} label={`Secciones de ${def!.label.toLowerCase()}`} />
        {tabs.map((t) => (
          <div key={t} role="tabpanel" aria-label={t} hidden={t !== activeTab} className="form-sections">
            {sections.filter((s) => s.tab === t).map(renderSection)}
          </div>
        ))}
      </div>
    );
  }

  const generalError = error && Object.keys(error.fieldErrors).length === 0 ? error : null;
  const subtitle = !isNew && row
    ? `Creado ${formatDateTime(row.createdAt)}${row.createdBy ? ` por ${row.createdBy}` : ''}${row.updatedAt ? ` · Actualizado ${formatDateTime(row.updatedAt)}${row.updatedBy ? ` por ${row.updatedBy}` : ''}` : ''}`
    : def.description;
  const canDelete = !isNew && p.delete && !lockedByState && !def.systemManaged && access?.delete !== false;

  const alerts: ReactNode = (
    <>
      {conflict && (
        <Alert
          tone="warning"
          title="Otro usuario modificó este registro"
          action={<Button size="sm" icon={RefreshCw} onClick={() => void load()}>Recargar</Button>}
        >
          Tus cambios no se guardaron. Recarga para ver la versión actual y vuelve a aplicar tus cambios.
        </Alert>
      )}
      {generalError && <Alert tone="danger" title={generalError.title}>{generalError.detail}</Alert>}
      <WarningsAlert warnings={warnings} def={def} childDefs={children} />
      {error && Object.keys(error.fieldErrors).length > 0 && (
        <Alert tone="danger" title="Revisa los campos marcados" />
      )}
      {lockedByState && wf && (
        <Alert tone="info" title="Documento cerrado a edición">
          Este documento está en estado {wf.stateLabel} y ya no se puede editar.
        </Alert>
      )}
      {!lockedByState && readOnly && (def.systemManaged
        ? <Alert tone="info" title="Lo administra el sistema">Estos registros los crea y actualiza el sistema; solo se consultan.</Alert>
        : p.update && outOfScope
          ? <Alert tone="info" title="Solo lectura">{access!.reason}</Alert>
          : <Alert tone="info" title="Solo lectura">Tu rol no tiene permiso para editar este catálogo.</Alert>)}
    </>
  );

  const actions = !readOnly && (
    <div className="dialog-footer">
      {/* Regresión (revisión 023, F-01): --danger sobre el pie gris no llega a AA (4.3:1); --danger-text sí. */}
      {canDelete && (
        <Button variant="ghost" icon={Trash2} onClick={() => setConfirmDelete(true)} style={{ marginRight: 'auto', color: 'var(--danger-text)' }}>Eliminar</Button>
      )}
      <Button onClick={() => navigate(listPath)} disabled={saving}>Cancelar</Button>
      <Button type="submit" variant="primary" icon={Save} loading={saving}>{isNew ? 'Crear' : 'Guardar cambios'}</Button>
    </div>
  );

  const header = (
    <PageHeader
      icon={Icon}
      // "Registrar …" evita errores de género ("Nuevo orden", "Nueva almacén") con etiquetas definidas por el usuario.
      title={isNew ? `Registrar ${def.label.toLowerCase()}` : folio ? `${def.label} ${folio}` : `${def.label} #${id}`}
      subtitle={subtitle}
      actions={<Button variant="ghost" icon={ArrowLeft} onClick={() => navigate(listPath)}>Volver</Button>}
    />
  );

  const workflowArea = wf && row ? (
    <WorkflowBar workflow={wf} blockedReason={dirty ? 'Guarda los cambios antes de cambiar el estado.' : undefined} onTransition={setTransition} />
  ) : isNew && def.workflow ? (
    <div className="workflow-bar" role="group" aria-label="Proceso del documento">
      <div className="row workflow-state">
        <span className="subtle">Se creará en estado</span>
        <StateBadge label={stateLabel(def, def.workflow.initial)} color={def.workflow.states.find((s) => s.name === def.workflow!.initial)?.color} />
      </div>
    </div>
  ) : null;

  const dialogs = (
    <>
      <ConfirmDialog
        open={confirmDelete}
        danger
        title="Eliminar registro"
        message={<>El registro <strong>#{id}</strong> dejará de aparecer en {def.labelPlural.toLowerCase()}. La acción queda registrada en la auditoría.</>}
        confirmLabel="Eliminar"
        loading={deleting}
        onConfirm={onDelete}
        onCancel={() => setConfirmDelete(false)}
      />
      {row && transition && (
        <TransitionDialog
          def={def}
          row={row}
          transition={transition}
          onClose={() => setTransition(null)}
          onDone={(r) => { setTransition(null); applyRow(r); }}
          onConflict={() => setConflict(true)}
        />
      )}
    </>
  );

  // Catálogo simple: mismo diseño de siempre (una tarjeta con el formulario).
  if (!isDocument) {
    return (
      <div className="page page-narrow">
        {header}
        {workflowArea}
        <form className="card" onSubmit={onSubmit} noValidate>
          <div className="card-pad stack">
            {alerts}
            {renderBody()}
          </div>
          {actions}
        </form>
        {wf && row && <div className="doc-section"><HistoryPanel entity={def.name} id={row.id} version={row._rowVersion} /></div>}
        {dialogs}
      </div>
    );
  }

  // Documento: encabezado, partidas, totales e historial.
  return (
    <div className="page">
      {header}
      {workflowArea}
      <form className="doc-form" onSubmit={onSubmit} noValidate>
        <section className="card card-pad stack" aria-label="Encabezado">
          {alerts}
          {renderBody()}
        </section>

        {linesFields.map((lf) => {
          const child = lf.childEntity ? children[lf.childEntity] : undefined;
          if (!child) return <Alert key={lf.name} tone="warning" title={`No se encontró la definición de ${lf.label}`} />;
          return (
            <LinesEditor
              key={lf.name}
              field={lf}
              child={child}
              lines={draft.lines[lf.name] ?? []}
              roles={roles}
              readOnly={readOnly || saving || lf.access === 'read' || !!lf.lockedByState}
              parent={{ def, values: draft.values }}
              serverError={error}
              clientErrors={clientErrors}
              onAdd={() => addLine(lf)}
              onRemove={(i) => removeLine(lf.name, i)}
              onChange={(i, name, v, label) => setLineField(lf.name, i, name, v, label)}
            />
          );
        })}

        {totalFields.length > 0 && (
          <section className="card card-pad totals-card" aria-label="Totales">
            <h2 className="section-title">Totales</h2>
            <dl className="totals">
              {totalFields.map((f, i) => (
                <div key={f.name} className={`totals-row${i === totalFields.length - 1 ? ' totals-grand' : ''}`}>
                  <dt>{f.label}</dt>
                  <dd><output id={`f-${f.name}`} aria-label={f.label} aria-live="polite">{formatFormula(f, draft.values[f.name]) || '—'}</output></dd>
                </div>
              ))}
            </dl>
          </section>
        )}

        {actions && <div className="card doc-actions">{actions}</div>}
      </form>
      {wf && row && <div className="doc-section"><HistoryPanel entity={def.name} id={row.id} version={row._rowVersion} /></div>}
      {dialogs}
    </div>
  );
}
