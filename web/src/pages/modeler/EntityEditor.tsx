import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  AlertCircle, Archive, ArchiveRestore, ArrowDown, ArrowUp, BookOpen, CheckCircle2, ChevronDown, ChevronUp, Eye, KeyRound, LayoutTemplate, ListFilter, Loader2, Package, Plus,
  Printer, ShieldCheck, Table2, Trash2, Webhook, Workflow as WorkflowIcon, Zap,
} from 'lucide-react';
import { ApiError } from '../../api/client';
import { adminApi, modelApi } from '../../api/endpoints';
import { GENERAL_APP } from '../../lib/apps';
import type {
  Automation, ChangePlan, ElementOrigin, EntityDef, EntityOrigin, FieldDef, FieldType, FormulaResultType, LedgerDef, Layout, Operation, OriginKind, PlanPreview, PrintTemplate, ReportDef, Rule,
  ScheduleDef, SecurityPolicy, ViewDef, WebhookDef, Workflow,
} from '../../api/types';
import { ConfirmDialog } from '../../components/Dialog';
import { IconPicker } from '../../components/IconPicker';
import { TagInput } from '../../components/TagInput';
import { ExprInput, type ExprScope } from '../../components/ExprInput';
import { Alert, Button, Field, Input, Select, Switch, Tabs, Textarea, type TabItem } from '../../components/ui';
import { FIELD_TYPE_LABELS, MODELER_FIELD_TYPES, supportsDefault } from '../../lib/entity';
import { IDENTIFIER_RE, pluralize, toIdentifier } from '../../lib/format';
import { DesignOrJson, type JsonResult } from './DesignOrJson';
import { LayoutDesigner } from './LayoutDesigner';
import { WorkflowDesigner } from './WorkflowDesigner';
import { AutomationsEditor } from './sections/AutomationsEditor';
import { CompilerErrors, InvalidJson, JsonAdvanced, OriginBadge, PackageNotice, SectionShell, selfScope, type SectionCtx } from './sections/common';
import { SchedulesEditor, WebhooksEditor } from './sections/IntegrationsEditor';
import { LedgerEditor } from './sections/LedgerEditor';
import { PrintTemplatesEditor } from './sections/PrintTemplatesEditor';
import { RulesEditor } from './sections/RulesEditor';
import { SecurityEditor } from './sections/SecurityEditor';
import { ReportsEditor, ViewsEditor } from './sections/ViewsReportsEditor';

const RESULT_TYPE_LABELS: Record<FormulaResultType, string> = {
  decimal: 'Número decimal', integer: 'Número entero', text: 'Texto', boolean: 'Sí / No', date: 'Fecha',
};

/** Tipos cuyo valor no captura la persona: no aplican "obligatorio" ni "único". */
const COMPUTED_TYPES: FieldType[] = ['formula', 'autonumber', 'lines', 'state'];

/** Vacío = sin definición. Valida forma mínima; el servidor valida el contenido en la vista previa. */
function parseJson<T>(text: string, kind: 'array' | 'object'): JsonResult<T> {
  if (!text.trim()) return { ok: true, value: null };
  try {
    const v: unknown = JSON.parse(text);
    if (kind === 'array' && !Array.isArray(v)) return { ok: false, error: 'Debe ser una lista JSON: [ … ]' };
    if (kind === 'object' && (typeof v !== 'object' || v === null || Array.isArray(v))) return { ok: false, error: 'Debe ser un objeto JSON: { … }' };
    return { ok: true, value: v as T };
  } catch (e) {
    return { ok: false, error: `JSON inválido: ${(e as Error).message}` };
  }
}

/** Sin propiedades nulas: el servidor las omite al leer, así que no cambian el significado y solo estorban. */
const withoutNulls = (_k: string, v: unknown) => (v === null ? undefined : v);
/**
 * Para comparar con lo guardado, las claves van ordenadas: el diseñador (019) puede reescribir un objeto con sus
 * propiedades en otro orden y eso no es un cambio.
 */
const sortedKeys = (_k: string, v: unknown) => (v === null ? undefined : v && typeof v === 'object' && !Array.isArray(v)
  ? Object.fromEntries(Object.keys(v).sort().map((k) => [k, (v as Record<string, unknown>)[k]]))
  : v);
export const canonical = (v: unknown) => JSON.stringify(v ?? null, sortedKeys);
const pretty = (v: unknown) => JSON.stringify(v, withoutNulls, 2);

interface DraftField extends FieldDef {
  _key: string;
  _isNew: boolean;
  _nameTouched: boolean;
  _original?: FieldDef;
}

interface Draft {
  name: string;
  label: string;
  labelPlural: string;
  icon: string;
  description: string;
  displayField: string;
  group: string;
  /** Spec 010: un registro por empresa. */
  perCompany: boolean;
  fields: DraftField[];
  rulesJson: string;
  workflowJson: string;
  automationsJson: string;
  securityJson: string;
  layoutJson: string;
  viewsJson: string;
  reportsJson: string;
  webhooksJson: string;
  schedulesJson: string;
  ledgerJson: string;
  /** Spec 027. */
  printTemplatesJson: string;
}

let keySeq = 0;
const newKey = () => `k${++keySeq}`;

function blankField(order: number): DraftField {
  return {
    _key: newKey(), _isNew: true, _nameTouched: false,
    name: '', label: '', type: 'text', required: false, unique: false, showInList: true,
    maxLength: 200, options: [], requiredForRoles: [], displayOrder: order,
  };
}

function draftFrom(def?: EntityDef): Draft {
  if (!def) {
    const f = blankField(0);
    f.label = 'Nombre'; f.name = 'Nombre'; f.required = true; f.maxLength = 150;
    return { name: '', label: '', labelPlural: '', icon: 'package', description: '', displayField: '', group: '', perCompany: false, fields: [f], rulesJson: '', workflowJson: '', automationsJson: '', securityJson: '', layoutJson: '', viewsJson: '', reportsJson: '', webhooksJson: '', schedulesJson: '', ledgerJson: '', printTemplatesJson: '' };
  }
  const fields = def.fields
    .slice()
    .sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0))
    .map((f): DraftField => ({ ...f, options: f.options ?? [], requiredForRoles: f.requiredForRoles ?? [], _key: newKey(), _isNew: false, _nameTouched: true, _original: f }));
  return {
    name: def.name, label: def.label, labelPlural: def.labelPlural, icon: def.icon || 'database',
    description: def.description ?? '', displayField: def.displayField ?? '', group: def.group ?? '', perCompany: !!def.perCompany, fields,
    rulesJson: def.rules?.length ? pretty(def.rules) : '',
    workflowJson: def.workflow ? pretty(def.workflow) : '',
    automationsJson: def.automations?.length ? pretty(def.automations) : '',
    securityJson: def.security ? pretty(def.security) : '',
    layoutJson: def.layout ? pretty(def.layout) : '',
    viewsJson: def.views?.length ? pretty(def.views) : '',
    reportsJson: def.reports?.length ? pretty(def.reports) : '',
    webhooksJson: def.webhooks?.length ? pretty(def.webhooks) : '',
    schedulesJson: def.schedules?.length ? pretty(def.schedules) : '',
    ledgerJson: def.ledger ? pretty(def.ledger) : '',
    printTemplatesJson: def.printTemplates?.length ? pretty(def.printTemplates) : '',
  };
}

function sameList(a?: string[], b?: string[]) {
  const x = a ?? []; const y = b ?? [];
  return x.length === y.length && x.every((v, i) => v === y[i]);
}

function cleanField(f: DraftField, order: number): FieldDef {
  const computed = COMPUTED_TYPES.includes(f.type);
  const out: FieldDef = {
    name: f.name.trim(), label: f.label.trim(), type: f.type,
    required: !computed && !!f.required, unique: !computed && !!f.unique, showInList: !!f.showInList, displayOrder: order,
  };
  if (!computed && f.requiredForRoles?.length) out.requiredForRoles = f.requiredForRoles;
  if (!computed && f.editable === false) out.editable = false;
  if (f.type === 'text' && f.maxLength) out.maxLength = f.maxLength;
  if (f.type === 'decimal') { out.precision = f.precision ?? 18; out.scale = f.scale ?? 2; }
  if (f.type === 'choice') out.options = f.options ?? [];
  if (f.type === 'reference' || f.type === 'parent') out.referenceEntity = f.referenceEntity;
  if (f.type === 'autonumber') { out.prefix = f.prefix?.trim() ?? ''; out.padding = f.padding ?? 6; }
  if (f.type === 'formula') { out.formula = f.formula?.trim() ?? ''; out.resultType = f.resultType ?? 'decimal'; }
  if (f.type === 'lines') out.childEntity = f.childEntity;
  if (supportsDefault(f.type) && f.defaultValue?.trim()) out.defaultValue = f.defaultValue.trim();
  return out;
}

/** Operaciones setRules / setWorkflow / setAutomations cuando el JSON cambió respecto al original. */
function behaviourOperations(entity: string, original: EntityDef | undefined, d: Draft): Operation[] {
  const ops: Operation[] = [];
  const rules = parseJson<Rule[]>(d.rulesJson, 'array');
  const workflow = parseJson<Workflow>(d.workflowJson, 'object');
  if (rules.ok && canonical(rules.value ?? []) !== canonical(original?.rules ?? [])) {
    ops.push({ op: 'setRules', entity, rules: rules.value ?? [] });
  }
  if (workflow.ok && workflow.value && canonical(workflow.value) !== canonical(original?.workflow ?? null)) {
    ops.push({ op: 'setWorkflow', entity, workflow: workflow.value });
  }
  // Va al final: las automatizaciones pueden usar estados y transiciones del proceso recién definido.
  const automations = parseJson<Automation[]>(d.automationsJson, 'array');
  if (automations.ok && canonical(automations.value ?? []) !== canonical(original?.automations ?? [])) {
    ops.push({ op: 'setAutomations', entity, automations: automations.value ?? [] });
  }
  // Spec 006: vacío quita la política.
  const security = parseJson<SecurityPolicy>(d.securityJson, 'object');
  if (security.ok && canonical(security.value) !== canonical(original?.security ?? null)) {
    ops.push({ op: 'setSecurity', entity, security: security.value });
  }
  // Spec 007: metadatos de interfaz. Van al final: usan campos, estados y roles ya definidos.
  const layout = parseJson<Layout>(d.layoutJson, 'object');
  if (layout.ok && canonical(layout.value) !== canonical(original?.layout ?? null)) {
    ops.push({ op: 'setLayout', entity, layout: layout.value });
  }
  const views = parseJson<ViewDef[]>(d.viewsJson, 'array');
  if (views.ok && canonical(views.value ?? []) !== canonical(original?.views ?? [])) {
    ops.push({ op: 'setViews', entity, views: views.value ?? [] });
  }
  const reports = parseJson<ReportDef[]>(d.reportsJson, 'array');
  if (reports.ok && canonical(reports.value ?? []) !== canonical(original?.reports ?? [])) {
    ops.push({ op: 'setReports', entity, reports: reports.value ?? [] });
  }
  // Spec 008: el destino y el secreto no son metadatos (se configuran en Integraciones).
  const webhooks = parseJson<WebhookDef[]>(d.webhooksJson, 'array');
  if (webhooks.ok && canonical(webhooks.value ?? []) !== canonical(original?.webhooks ?? [])) {
    ops.push({ op: 'setWebhooks', entity, webhooks: webhooks.value ?? [] });
  }
  // Spec 016: si la tarea corre en este entorno se decide en Configuración › Automatizaciones (llega en pausa).
  const schedules = parseJson<ScheduleDef[]>(d.schedulesJson, 'array');
  if (schedules.ok && canonical(schedules.value ?? []) !== canonical(original?.schedules ?? [])) {
    ops.push({ op: 'setSchedules', entity, schedules: schedules.value ?? [] });
  }
  // Spec 020: vacío quita el libro.
  const ledger = parseJson<LedgerDef>(d.ledgerJson, 'object');
  if (ledger.ok && canonical(ledger.value) !== canonical(original?.ledger ?? null)) {
    ops.push({ op: 'setLedger', entity, ledger: ledger.value });
  }
  // Spec 027: el editor escribe la lista completa (sin merge).
  const templates = parseJson<PrintTemplate[]>(d.printTemplatesJson, 'array');
  if (templates.ok && canonical(templates.value ?? []) !== canonical(original?.printTemplates ?? [])) {
    ops.push({ op: 'setPrintTemplates', entity, templates: templates.value ?? [] });
  }
  return ops;
}

/**
 * Posición nueva de cada campo existente, solo si el orden relativo cambió. Regresión (revisión exploratoria 006,
 * F-03): se comparaba el índice en pantalla con el `displayOrder` guardado, que no tiene por qué ser consecutivo
 * (huecos, estado en -1), y se ofrecía «Revisar cambios» sin cambios.
 */
export function reorderedPositions(original: EntityDef, fields: { _isNew: boolean; _original?: FieldDef }[]): Map<string, number> {
  const before = original.fields.slice().sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0)).map((f) => f.name);
  const after = fields.filter((f) => !f._isNew && f._original).map((f) => f._original!.name);
  const out = new Map<string, number>();
  if (before.length === after.length && before.every((n, i) => n === after[i])) return out;
  fields.forEach((f, i) => {
    if (f._isNew || !f._original) return;
    // Al reordenar se renumera de forma consecutiva; un campo que ya tiene su número no se envía.
    if ((f._original.displayOrder ?? 0) !== i) out.set(f._original.name, i);
  });
  return out;
}

export function buildOperations(original: EntityDef | undefined, d: Draft): Operation[] {
  const active = d.fields.filter((f) => !(f._isNew && f.isArchived));
  if (!original) {
    return [{
      op: 'createEntity', name: d.name.trim(), label: d.label.trim(), labelPlural: d.labelPlural.trim(),
      icon: d.icon || undefined, description: d.description.trim() || undefined,
      displayField: d.displayField || undefined,
      group: d.group.trim() || undefined,
      perCompany: d.perCompany || undefined,
      fields: active.filter((f) => !f.isArchived).map((f, i) => cleanField(f, i)),
    }, ...behaviourOperations(d.name.trim(), undefined, d)];
  }
  const ops: Operation[] = [];
  const meta: Extract<Operation, { op: 'updateEntity' }> = { op: 'updateEntity', entity: original.name };
  let metaChanged = false;
  if (d.label.trim() !== original.label) { meta.label = d.label.trim(); metaChanged = true; }
  if (d.labelPlural.trim() !== original.labelPlural) { meta.labelPlural = d.labelPlural.trim(); metaChanged = true; }
  if (d.icon !== (original.icon || 'database')) { meta.icon = d.icon; metaChanged = true; }
  if (d.description.trim() !== (original.description ?? '')) { meta.description = d.description.trim(); metaChanged = true; }
  if (d.displayField !== (original.displayField ?? '')) { meta.displayField = d.displayField; metaChanged = true; }
  if (d.group.trim() !== (original.group ?? '')) { meta.group = d.group.trim(); metaChanged = true; }
  if (d.perCompany && !original.perCompany) { meta.perCompany = true; metaChanged = true; }
  if (metaChanged) ops.push(meta);

  const moved = reorderedPositions(original, d.fields);
  d.fields.forEach((f, i) => {
    if (f._isNew) {
      if (!f.isArchived) ops.push({ op: 'addField', entity: original.name, field: cleanField(f, i) });
      return;
    }
    const o = f._original!;
    if (f.isArchived && !o.isArchived) { ops.push({ op: 'archiveField', entity: original.name, field: o.name }); return; }
    if (f.isArchived) return;
    // Spec 017 (H4): reactivar un campo archivado; los cambios que se le hagan van después, ya activo.
    if (o.isArchived) ops.push({ op: 'restoreField', entity: original.name, field: o.name });
    const u: Extract<Operation, { op: 'updateField' }> = { op: 'updateField', entity: original.name, field: o.name };
    let changed = false;
    if (f.label.trim() !== o.label) { u.label = f.label.trim(); changed = true; }
    if (!!f.required !== !!o.required) { u.required = !!f.required; changed = true; }
    if (!!f.showInList !== !!o.showInList) { u.showInList = !!f.showInList; changed = true; }
    if ((f.editable !== false) !== (o.editable !== false)) { u.editable = f.editable !== false; changed = true; }
    if (f.type === 'choice' && !sameList(f.options, o.options)) { u.options = f.options ?? []; changed = true; }
    if (!sameList(f.requiredForRoles, o.requiredForRoles)) { u.requiredForRoles = f.requiredForRoles ?? []; changed = true; }
    if (moved.has(o.name)) { u.displayOrder = moved.get(o.name)!; changed = true; }
    if (supportsDefault(f.type) && (f.defaultValue ?? '').trim() !== (o.defaultValue ?? '')) { u.defaultValue = (f.defaultValue ?? '').trim(); changed = true; }
    if (f.type === 'formula' && (f.formula ?? '').trim() !== (o.formula ?? '')) { u.formula = (f.formula ?? '').trim(); changed = true; }
    if (changed) ops.push(u);
  });
  return [...ops, ...behaviourOperations(original.name, original, d)];
}

function validate(d: Draft, isNew: boolean, existingNames: string[]): Record<string, string> {
  const e: Record<string, string> = {};
  if (!d.label.trim()) e.label = 'Escribe un nombre visible';
  if (!d.labelPlural.trim()) e.labelPlural = 'Escribe el nombre en plural';
  if (isNew) {
    if (!IDENTIFIER_RE.test(d.name)) e.name = 'Solo letras, números y guion bajo; inicia con letra (2 a 50 caracteres)';
    else if (existingNames.some((n) => n.toLowerCase() === d.name.toLowerCase())) e.name = 'Ya existe un catálogo con ese identificador';
  }
  const seen = new Set<string>();
  d.fields.forEach((f) => {
    if (f.isArchived && f._isNew) return;
    if (!f.label.trim()) e[`${f._key}.label`] = 'Requerido';
    if (!IDENTIFIER_RE.test(f.name)) e[`${f._key}.name`] = 'Identificador inválido';
    else if (seen.has(f.name.toLowerCase())) e[`${f._key}.name`] = 'Repetido';
    seen.add(f.name.toLowerCase());
    if (f.type === 'choice' && !(f.options?.length)) e[`${f._key}.options`] = 'Agrega al menos una opción';
    if ((f.type === 'reference' || f.type === 'parent') && !f.referenceEntity) e[`${f._key}.referenceEntity`] = 'Selecciona el catálogo';
    if (f.type === 'formula' && !f.formula?.trim()) e[`${f._key}.formula`] = 'Escribe la fórmula';
    if (f.type === 'lines' && !f.childEntity) e[`${f._key}.childEntity`] = 'Selecciona el catálogo de partidas';
  });
  if (d.fields.filter((f) => !f.isArchived).length === 0) e.fields = 'Agrega al menos un campo';
  const rules = parseJson<Rule[]>(d.rulesJson, 'array');
  if (!rules.ok) e.rulesJson = rules.error;
  const workflow = parseJson<Workflow>(d.workflowJson, 'object');
  if (!workflow.ok) e.workflowJson = workflow.error;
  const automations = parseJson<Automation[]>(d.automationsJson, 'array');
  if (!automations.ok) e.automationsJson = automations.error;
  const security = parseJson<SecurityPolicy>(d.securityJson, 'object');
  if (!security.ok) e.securityJson = security.error;
  const layout = parseJson<Layout>(d.layoutJson, 'object');
  if (!layout.ok) e.layoutJson = layout.error;
  const views = parseJson<ViewDef[]>(d.viewsJson, 'array');
  if (!views.ok) e.viewsJson = views.error;
  const reports = parseJson<ReportDef[]>(d.reportsJson, 'array');
  if (!reports.ok) e.reportsJson = reports.error;
  const webhooks = parseJson<WebhookDef[]>(d.webhooksJson, 'array');
  if (!webhooks.ok) e.webhooksJson = webhooks.error;
  const schedules = parseJson<ScheduleDef[]>(d.schedulesJson, 'array');
  if (!schedules.ok) e.schedulesJson = schedules.error;
  const ledger = parseJson<LedgerDef>(d.ledgerJson, 'object');
  if (!ledger.ok) e.ledgerJson = ledger.error;
  const templates = parseJson<PrintTemplate[]>(d.printTemplatesJson, 'array');
  if (!templates.ok) e.printTemplatesJson = templates.error;
  return e;
}

/* ------------------------------------------------------------------------- */

interface FieldRowProps {
  f: DraftField;
  index: number;
  count: number;
  errors: Record<string, string>;
  entities: EntityDef[];
  allEntities: EntityDef[];
  roleNames: string[] | null;
  origin?: ElementOrigin;
  scope: ExprScope;
  onChange: (patch: Partial<DraftField>) => void;
  onMove: (dir: -1 | 1) => void;
  onRemove: () => void;
}

/** Spec 026 (H2 CA3): el borrador cambió un campo guardado (lo que `buildOperations` mandaría como updateField o archivo). */
function fieldChanged(f: DraftField): boolean {
  const o = f._original;
  if (!o) return false;
  return f.label.trim() !== o.label || !!f.required !== !!o.required || !!f.showInList !== !!o.showInList || (f.editable !== false) !== (o.editable !== false)
    || !!f.isArchived !== !!o.isArchived || !sameList(f.options, o.options) || !sameList(f.requiredForRoles, o.requiredForRoles)
    || (f.defaultValue ?? '').trim() !== (o.defaultValue ?? '') || (f.formula ?? '').trim() !== (o.formula ?? '');
}

function FieldRow({ f, index, count, errors, entities, allEntities, roleNames, origin, scope, onChange, onMove, onRemove }: FieldRowProps) {
  const [expanded, setExpanded] = useState(f._isNew);
  const locked = !f._isNew;
  const computed = COMPUTED_TYPES.includes(f.type);
  const err = (k: string) => errors[`${f._key}.${k}`];
  return (
    <div className={`field-row${f.isArchived ? ' archived' : ''}`}>
      <div className="field-row-head">
        <div className="field-row-tools">
          <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} aria-label="Subir campo" disabled={index === 0} onClick={() => onMove(-1)} />
          <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} aria-label="Bajar campo" disabled={index === count - 1} onClick={() => onMove(1)} />
        </div>
        <Input
          inputSize="sm"
          aria-label="Etiqueta del campo"
          placeholder="Etiqueta del campo"
          value={f.label}
          error={err('label')}
          disabled={f.isArchived}
          onChange={(e) => {
            const label = e.target.value;
            onChange(f._isNew && !f._nameTouched ? { label, name: toIdentifier(label) } : { label });
          }}
        />
        <Input
          inputSize="sm"
          wrapClassName="hide-sm"
          className="mono"
          aria-label="Identificador del campo"
          placeholder="Identificador"
          value={f.name}
          error={err('name')}
          disabled={locked || f.isArchived}
          title={locked ? 'El identificador no se puede cambiar una vez creado' : undefined}
          onChange={(e) => onChange({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, ''), _nameTouched: true })}
        />
        <Select
          selectSize="sm"
          wrapClassName="hide-sm"
          aria-label="Tipo de campo"
          value={f.type}
          disabled={locked || f.isArchived}
          onChange={(e) => onChange({ type: e.target.value as FieldType })}
        >
          {(f.type === 'state' ? [...MODELER_FIELD_TYPES, 'state' as FieldType] : MODELER_FIELD_TYPES).map((t) => <option key={t} value={t}>{FIELD_TYPE_LABELS[t]}</option>)}
        </Select>
        <div className="hide-sm field-row-origin">{f._isNew ? <span className="origin-chip origin-local">Nuevo</span> : <OriginBadge origin={origin} />}</div>
        <div className="field-row-tools">
          {!f.isArchived && (
            <Button variant="ghost" size="sm" iconOnly icon={expanded ? ChevronUp : ChevronDown} aria-label={expanded ? `Ocultar opciones de ${f.label || 'campo'}` : `Mostrar opciones de ${f.label || 'campo'}`} aria-expanded={expanded} onClick={() => setExpanded((x) => !x)} />
          )}
          {f._isNew ? (
            <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label="Quitar campo" onClick={onRemove} />
          ) : f.isArchived ? (
            <Button
              variant="ghost" size="sm" iconOnly icon={ArchiveRestore}
              aria-label={f._original?.isArchived ? `Reactivar campo ${f.label}` : 'Deshacer archivado'}
              title={f._original?.isArchived ? 'Reactivar (vuelve con los datos que conservó)' : 'Deshacer archivado'}
              onClick={() => onChange({ isArchived: false })}
            />
          ) : (
            <Button variant="ghost" size="sm" iconOnly icon={Archive} aria-label="Archivar campo" title="Archivar (los datos se conservan)" onClick={() => onChange({ isArchived: true })} />
          )}
        </div>
      </div>
      {fieldChanged(f) && origin?.package && <div className="field-row-notice"><PackageNotice origin={origin} /></div>}
      {expanded && !f.isArchived && (
        <div className="field-row-body">
          {!f._isNew && <div className="span-all field-row-origin-sm"><OriginBadge origin={origin} /></div>}
          {!computed && <Switch checked={!!f.required} onChange={(v) => onChange({ required: v })} label="Obligatorio" />}
          {!computed && f.type !== 'parent' && (
            <Switch checked={f.editable !== false} onChange={(v) => onChange({ editable: v })} label="Editable por el usuario" />
          )}
          <Switch checked={!!f.showInList} onChange={(v) => onChange({ showInList: v })} label="Mostrar en listado" />
          {!computed && f.type !== 'parent' && (
            <Switch checked={!!f.unique} disabled={locked || f.type === 'boolean' || f.type === 'longtext' || f.type === 'user'} onChange={(v) => onChange({ unique: v })} label="Valor único" />
          )}
          {f.type === 'autonumber' && (
            <>
              <Input inputSize="sm" label="Prefijo" className="mono" maxLength={10} disabled={locked} value={f.prefix ?? ''} onChange={(e) => onChange({ prefix: e.target.value })} />
              <Input inputSize="sm" type="number" min={1} max={12} label="Dígitos" disabled={locked} value={f.padding ?? 6} onChange={(e) => onChange({ padding: Number(e.target.value) || undefined })} hint={`Ejemplo: ${(f.prefix ?? '')}${'1'.padStart(f.padding ?? 6, '0')}`} />
            </>
          )}
          {f.type === 'formula' && (
            <>
              <ExprInput
                className="span-all"
                label="Fórmula"
                multiline
                required
                value={f.formula ?? ''}
                error={err('formula')}
                scope={scope}
                entities={allEntities}
                hint="Usa nombres de campos y funciones como ROUND, IF, SUM(Partidas.Importe)."
                onChange={(v) => onChange({ formula: v })}
              />
              <Select selectSize="sm" label="Tipo de resultado" disabled={locked} value={f.resultType ?? 'decimal'} onChange={(e) => onChange({ resultType: e.target.value as FormulaResultType })}>
                {(Object.keys(RESULT_TYPE_LABELS) as FormulaResultType[]).map((t) => <option key={t} value={t}>{RESULT_TYPE_LABELS[t]}</option>)}
              </Select>
            </>
          )}
          {f.type === 'lines' && (
            <Select selectSize="sm" label="Catálogo de partidas" disabled={locked} error={err('childEntity')} value={f.childEntity ?? ''} onChange={(e) => onChange({ childEntity: e.target.value || undefined })}
              hint="Debe tener un campo de tipo Documento padre que apunte a este catálogo.">
              <option value="">Selecciona…</option>
              {entities.filter((x) => !x.isArchived).map((x) => <option key={x.name} value={x.name}>{x.labelPlural}</option>)}
            </Select>
          )}
          {f.type === 'parent' && (
            <Select selectSize="sm" label="Documento padre" disabled={locked} error={err('referenceEntity')} value={f.referenceEntity ?? ''} onChange={(e) => onChange({ referenceEntity: e.target.value || undefined })}>
              <option value="">Selecciona…</option>
              {entities.filter((x) => !x.isArchived && !x.isChild).map((x) => <option key={x.name} value={x.name}>{x.labelPlural}</option>)}
            </Select>
          )}
          {supportsDefault(f.type) && (
            <ExprInput
              className="span-all"
              label="Valor por defecto"
              value={f.defaultValue ?? ''}
              scope={scope}
              entities={allEntities}
              hint="Expresión, p. ej. TODAY() o Producto.CostoUltimo. Vacío: sin valor por defecto."
              onChange={(v) => onChange({ defaultValue: v })}
            />
          )}
          {f.type === 'text' && (
            <Input inputSize="sm" type="number" min={1} max={4000} label="Longitud máxima" disabled={locked} value={f.maxLength ?? ''} onChange={(e) => onChange({ maxLength: e.target.value ? Number(e.target.value) : undefined })} />
          )}
          {f.type === 'decimal' && (
            <>
              <Input inputSize="sm" type="number" min={1} max={38} label="Precisión" disabled={locked} value={f.precision ?? 18} onChange={(e) => onChange({ precision: Number(e.target.value) })} />
              <Input inputSize="sm" type="number" min={0} max={18} label="Decimales" disabled={locked} value={f.scale ?? 2} onChange={(e) => onChange({ scale: Number(e.target.value) })} />
            </>
          )}
          {f.type === 'reference' && (
            <Select selectSize="sm" label="Catálogo referenciado" disabled={locked} error={err('referenceEntity')} value={f.referenceEntity ?? ''} onChange={(e) => onChange({ referenceEntity: e.target.value || undefined })}>
              <option value="">Selecciona…</option>
              {entities.filter((x) => !x.isArchived).map((x) => <option key={x.name} value={x.name}>{x.labelPlural}</option>)}
            </Select>
          )}
          {/* Spec 026 (fuera del editor visual): el filtro de búsqueda de la 005 se ve, pero se cambia por plan o paquete. */}
          {f.lookupFilter && (
            <p className="field-hint span-all">
              El buscador solo ofrece registros con <span className="mono">{f.lookupFilter.field} = {f.lookupFilter.source}</span>.
              Este filtro se cambia con un paquete o con el asistente; aquí no tiene control.
            </p>
          )}
          {f.type === 'choice' && (
            <Field label="Opciones" error={err('options')} className="span-all">
              <TagInput value={f.options ?? []} onChange={(v) => onChange({ options: v })} aria-label="Opciones de la lista" invalid={!!err('options')} />
            </Field>
          )}
          {!computed && (
            <Field label="Obligatorio solo para roles" hint={roleNames ? 'El campo será obligatorio cuando lo capture alguien con estos roles' : 'Escribe el nombre exacto del rol'} className="span-all">
              <TagInput value={f.requiredForRoles ?? []} onChange={(v) => onChange({ requiredForRoles: v })} suggestions={roleNames ?? undefined} placeholder="Agregar rol…" aria-label="Roles para los que es obligatorio" />
            </Field>
          )}
        </div>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------------- */

export type EditorTab = 'catalogo' | 'formulario' | 'proceso' | 'reglas' | 'automatizaciones' | 'seguridad' | 'vistas' | 'integraciones' | 'contabilidad' | 'impresion';
const TABS: EditorTab[] = ['catalogo', 'formulario', 'proceso', 'reglas', 'automatizaciones', 'seguridad', 'vistas', 'integraciones', 'contabilidad', 'impresion'];
export const editorTab = (v: string | null): EditorTab => (TABS.includes(v as EditorTab) ? (v as EditorTab) : 'catalogo');
/** Spec 019: el diseñador del formulario y el diagrama del proceso usan el ancho completo; spec 027: también la impresión (vista previa). */
export const wideTab = (t: EditorTab) => t === 'formulario' || t === 'proceso' || t === 'impresion';

/** Sección de cada error de validación del cliente (clave de `validate`). */
const JSON_TAB: Record<string, EditorTab> = {
  layoutJson: 'formulario', workflowJson: 'proceso', rulesJson: 'reglas', automationsJson: 'automatizaciones', securityJson: 'seguridad',
  viewsJson: 'vistas', reportsJson: 'vistas', webhooksJson: 'integraciones', schedulesJson: 'integraciones', ledgerJson: 'contabilidad',
  printTemplatesJson: 'impresion',
};
const tabOfKey = (k: string): EditorTab => JSON_TAB[k] ?? 'catalogo';

type Section = 'catalog' | 'layout' | 'workflow' | 'rules' | 'automations' | 'security' | 'views' | 'reports' | 'webhooks' | 'schedules' | 'ledger' | 'print';
const SECTION_TAB: Record<Section, EditorTab> = {
  catalog: 'catalogo', layout: 'formulario', workflow: 'proceso', rules: 'reglas', automations: 'automatizaciones', security: 'seguridad',
  views: 'vistas', reports: 'vistas', webhooks: 'integraciones', schedules: 'integraciones', ledger: 'contabilidad', print: 'impresion',
};
const OP_SECTION: Partial<Record<Operation['op'], Section>> = {
  setRules: 'rules', setAutomations: 'automations', setSecurity: 'security', setViews: 'views', setReports: 'reports',
  setWebhooks: 'webhooks', setSchedules: 'schedules', setLedger: 'ledger', setLayout: 'layout', setWorkflow: 'workflow', setPrintTemplates: 'print',
};

/**
 * Spec 026 (H4 CA11): a qué sección pertenece un error del compilador. Primero por el texto (la pasada final del compilador
 * atribuye sus errores a la última operación del catálogo, no a la que los causó) y, si no, por la operación.
 */
export function sectionOfError(message: string, op?: Operation['op']): Section {
  const m = message.toLowerCase();
  // Spec 027: va primero; una plantilla menciona campos, reglas o reportes en sus textos.
  if (m.includes('plantilla \'')) return 'print';
  if (m.includes('› tarea ') || m.includes(' tarea \'')) return 'schedules';
  if (m.includes('webhook')) return 'webhooks';
  if (m.includes('› seguridad') || m.includes('seguridad del catálogo')) return 'security';
  if (m.includes('› libro')) return 'ledger';
  if (m.includes('› layout')) return 'layout';
  if (m.includes('reporte \'')) return 'reports';
  if (m.includes('vista \'')) return 'views';
  if (/\bregla\b/.test(m)) return 'rules';
  if (m.includes('automatizaci')) return 'automations';
  if (op && OP_SECTION[op]) return OP_SECTION[op]!;
  if (m.includes('transición') || m.includes('proceso') || m.includes('estado')) return 'workflow';
  return 'catalog';
}

interface JsonBackedProps<T> {
  kind: 'array' | 'object';
  text: string;
  onText: (t: string) => void;
  title: string;
  description: ReactNode;
  jsonLabel: string;
  hint: string;
  errors: string[];
  clientError?: string;
  children: (value: T | null, write: (v: T | null) => void) => ReactNode;
}

/** Spec 026 (H4 CA10): editor visual y JSON avanzado sobre el mismo texto del borrador (patrón de `DesignOrJson`). */
function JsonBacked<T>({ kind, text, onText, title, description, jsonLabel, hint, errors, clientError, children }: JsonBackedProps<T>) {
  const parsed = parseJson<T>(text, kind);
  const write = (v: T | null) => onText(v === null || (Array.isArray(v) && v.length === 0) ? '' : pretty(v));
  return (
    <SectionShell title={title} description={description} errors={errors}
      json={<JsonAdvanced label={jsonLabel} text={text} onText={onText} hint={hint} error={clientError ?? (parsed.ok ? undefined : parsed.error)} />}>
      {parsed.ok ? children(parsed.value, write) : <InvalidJson error={parsed.error} />}
    </SectionShell>
  );
}

export interface EntityEditorProps {
  original?: EntityDef;
  entities: EntityDef[];
  onCancel: () => void;
  onReview: (plan: ChangePlan) => void;
}

export function EntityEditor({ original, entities, onCancel, onReview }: EntityEditorProps) {
  const isNew = !original;
  const [params, setParams] = useSearchParams();
  // Spec 024 (H2 CA2): al crear desde un módulo (`?app=`), el catálogo nuevo se propone en ese módulo.
  const [d, setD] = useState<Draft>(() => {
    const draft = draftFrom(original);
    const app = params.get('app')?.trim();
    return isNew && app && app.toLowerCase() !== GENERAL_APP.toLowerCase() ? { ...draft, group: app.slice(0, 60) } : draft;
  });
  const [appNames, setAppNames] = useState<string[]>([]);
  const [nameTouched, setNameTouched] = useState(!isNew);
  const [pluralTouched, setPluralTouched] = useState(!isNew);
  const [showErrors, setShowErrors] = useState(false);
  const [roleNames, setRoleNames] = useState<string[] | null>(null);
  const [provenance, setProvenance] = useState<EntityOrigin | null>(null);
  // Spec 019: pestañas del editor en la URL; el borrador es uno solo para todas.
  const tab = editorTab(params.get('tab'));
  const setTab = (t: EditorTab) => {
    const p = new URLSearchParams(params);
    if (t === 'catalogo') p.delete('tab'); else p.set('tab', t);
    setParams(p, { replace: true });
  };

  useEffect(() => {
    modelApi.apps(true).then((a) => setAppNames(a.filter((x) => !x.isArchived && x.name !== GENERAL_APP).map((x) => x.name))).catch(() => setAppNames([]));
  }, []);

  useEffect(() => {
    adminApi.roles().then((r) => setRoleNames(r.map((x) => x.name))).catch((e) => { if (e instanceof ApiError) setRoleNames(null); });
  }, []);

  // Spec 026 (H2): origen de cada elemento del catálogo guardado.
  useEffect(() => {
    if (!original) return;
    const ctl = new AbortController();
    modelApi.provenance(ctl.signal).then((p) => setProvenance(p.entities.find((e) => e.entity === original.name) ?? null)).catch(() => setProvenance(null));
    return () => ctl.abort();
  }, [original]);

  const errors = useMemo(() => validate(d, isNew, entities.map((e) => e.name)), [d, isNew, entities]);
  const visibleErrors = showErrors ? errors : {};
  const ops = useMemo(() => buildOperations(original, d), [original, d]);
  // Spec 021 (H8 CA4): con cambios sin aplicar, cerrar o recargar la pestaña pregunta, y «Cancelar» pide confirmación.
  // Un catálogo nuevo siempre produce operaciones: ahí cuenta si el borrador se tocó.
  const [pristine] = useState(d);
  const dirty = isNew ? d !== pristine : ops.length > 0;
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);
  const textFields = d.fields.filter((f) => !f.isArchived && (f.type === 'text') && IDENTIFIER_RE.test(f.name));

  // Spec 026 (H4 CA11): vista previa en vivo con el mismo compilador; sus errores se ven junto a cada sección.
  const baseVersions = original?.version ? { [original.name]: original.version } : undefined;
  const clientOk = Object.keys(errors).length === 0;
  const opsKey = JSON.stringify(ops);
  const [live, setLive] = useState<{ state: 'idle' | 'checking' | 'done' | 'failed'; errors: PlanPreview['errors'] }>({ state: 'idle', errors: [] });
  useEffect(() => {
    if (!ops.length || !clientOk) { setLive({ state: 'idle', errors: [] }); return; }
    const ctl = new AbortController();
    const t = window.setTimeout(() => {
      setLive((s) => ({ ...s, state: 'checking' }));
      modelApi.preview({ description: 'Vista previa en vivo del modelador', operations: ops, baseVersions }, ctl.signal)
        .then((p) => setLive({ state: 'done', errors: p.errors }))
        .catch(() => { if (!ctl.signal.aborted) setLive({ state: 'failed', errors: [] }); });
    }, 800);
    return () => { window.clearTimeout(t); ctl.abort(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opsKey, clientOk]);
  const liveBySection = useMemo(() => {
    const m: Partial<Record<Section, string[]>> = {};
    for (const e of live.errors) {
      const s = sectionOfError(e.message, ops[e.operation]?.op);
      (m[s] ??= []).push(e.message);
    }
    return m;
  }, [live.errors, ops]);
  const sectionErrors = (s: Section) => liveBySection[s] ?? [];

  function patchField(key: string, patch: Partial<DraftField>) {
    setD((s) => ({ ...s, fields: s.fields.map((f) => (f._key === key ? { ...f, ...patch } : f)) }));
  }
  function move(i: number, dir: -1 | 1) {
    setD((s) => {
      const fields = s.fields.slice();
      const j = i + dir;
      if (j < 0 || j >= fields.length) return s;
      [fields[i], fields[j]] = [fields[j], fields[i]];
      return { ...s, fields };
    });
  }

  function review() {
    setShowErrors(true);
    const keys = Object.keys(errors);
    if (keys.length) {
      // El error puede estar en otra pestaña: se abre la primera que tenga uno.
      const tabs = keys.map(tabOfKey);
      const target = TABS.find((t) => tabs.includes(t))!;
      if (target !== tab) setTab(target);
      return;
    }
    const description = isNew ? `Crear catálogo ${d.labelPlural.trim()}` : `Modificar catálogo ${original!.labelPlural}`;
    // Spec 021 (H2 CA1): la versión con la que se abrió; si otra persona aplicó un cambio después, la vista previa lo dice.
    onReview({ description, operations: ops, baseVersions });
  }

  const parsedLayout = parseJson<Layout>(d.layoutJson, 'object');
  const parsedWorkflow = parseJson<Workflow>(d.workflowJson, 'object');
  // Campos activos del borrador con identificador válido: los nuevos también se pueden colocar en el formulario.
  const designFields: FieldDef[] = d.fields.filter((f) => !f.isArchived && IDENTIFIER_RE.test(f.name));
  const isChild = d.fields.some((f) => !f.isArchived && f.type === 'parent');
  const isDocument = d.fields.some((f) => !f.isArchived && f.type === 'lines');
  const tabHasErrors = (t: EditorTab) =>
    (showErrors && Object.keys(errors).some((k) => tabOfKey(k) === t))
    || (Object.keys(liveBySection) as Section[]).some((s) => SECTION_TAB[s] === t && (liveBySection[s]?.length ?? 0) > 0);
  const childNotice = (
    <Alert tone="info" title="Es un catálogo de partidas">
      Sus registros se capturan como tabla dentro de su documento: el formulario y el proceso se definen en el documento.
    </Alert>
  );

  const workflow = parsedWorkflow.ok ? parsedWorkflow.value : original?.workflow ?? null;
  const originMap = useMemo(() => new Map((provenance?.elements ?? []).map((e) => [`${e.kind}:${e.name.toLowerCase()}`, e])), [provenance]);
  const savedList = (kind: OriginKind): { name: string }[] | null | undefined => ({
    rule: original?.rules, automation: original?.automations, view: original?.views, report: original?.reports, webhook: original?.webhooks, schedule: original?.schedules,
    printTemplate: original?.printTemplates,
  } as Partial<Record<OriginKind, { name: string }[] | null | undefined>>)[kind];
  const ctx: SectionCtx = {
    // Un catálogo nuevo sin identificador todavía se valida como «Borrador» (no existe: se arma con los campos del borrador).
    entity: IDENTIFIER_RE.test(d.name) ? d.name : 'Borrador',
    fields: designFields,
    entities,
    roleNames,
    states: (workflow?.states ?? []).map((s) => ({ name: s.name, label: s.label || s.name })),
    transitions: (workflow?.transitions ?? []).map((t) => ({ name: t.name, label: t.label || t.name })),
    origin: (kind, name) => originMap.get(`${kind}:${(name ?? '').toLowerCase()}`),
    saved: (kind, name) => {
      if (kind === 'security') return original?.security ?? undefined;
      if (kind === 'ledger') return original?.ledger ?? undefined;
      return savedList(kind)?.find((x) => x.name.toLowerCase() === (name ?? '').toLowerCase());
    },
    errors: [],
  };
  const sectionCtx = (s: Section): SectionCtx => ({ ...ctx, errors: sectionErrors(s) });
  const scope = selfScope(ctx);
  const entityOrigin = ctx.origin('entity', '');
  const tabItems: TabItem<EditorTab>[] = [
    { id: 'catalogo', label: 'Catálogo', icon: Table2 },
    { id: 'formulario', label: 'Formulario', icon: LayoutTemplate },
    { id: 'proceso', label: 'Proceso', icon: WorkflowIcon },
    { id: 'reglas', label: 'Reglas', icon: ShieldCheck },
    { id: 'automatizaciones', label: 'Automatizaciones', icon: Zap },
    { id: 'seguridad', label: 'Seguridad', icon: KeyRound },
    { id: 'vistas', label: 'Vistas y reportes', icon: ListFilter },
    { id: 'integraciones', label: 'Integraciones', icon: Webhook },
    { id: 'contabilidad', label: 'Contabilidad', icon: BookOpen },
    { id: 'impresion', label: 'Impresión', icon: Printer },
  ].map((t) => ({ ...t, label: tabHasErrors(t.id as EditorTab) ? `${t.label} (con errores)` : t.label })) as TabItem<EditorTab>[];

  return (
    <div className="stack" style={{ gap: 'var(--space-6)' }}>
      {provenance && (
        <p className="origin-line subtle">
          <Package size={14} aria-hidden />
          {provenance.installedBy ? <>Instalado por <strong>{provenance.installedBy.name} {provenance.installedBy.version}</strong></> : <>Creado en este entorno</>}
          {provenance.packages.filter((p) => p.id !== provenance.installedBy?.id).length > 0 && (
            <> · También lo extiende: {provenance.packages.filter((p) => p.id !== provenance.installedBy?.id).map((p) => `${p.name} ${p.version}`).join(', ')}</>
          )}
        </p>
      )}
      <div className="editor-tabs">
        <Tabs<EditorTab> label="Partes del catálogo" value={tab} onChange={setTab} items={tabItems} />
      </div>
      {tab === 'formulario' && (
        <div role="tabpanel" aria-label="Formulario">
          {isChild ? childNotice : (
            <DesignOrJson<Layout>
              title="Formulario"
              jsonLabel="Layout del formulario (JSON)"
              text={d.layoutJson}
              parsed={parsedLayout}
              onText={(layoutJson) => setD((s) => ({ ...s, layoutJson }))}
              placeholder=""
              hint="Secciones con name, label, fields y opcionalmente tab, columns (1 o 2), description y visibleWhen (también por campo). Los campos no mencionados van en «Otros datos». Vacío: sin layout."
              error={visibleErrors.layoutJson}
              designer={(layout) => (
                <>
                  <CompilerErrors errors={sectionErrors('layout')} />
                  <LayoutDesigner layout={layout} isDocument={isDocument} fields={designFields}
                    onChange={(next) => setD((s) => ({ ...s, layoutJson: next ? pretty(next) : '' }))} />
                </>
              )}
            />
          )}
        </div>
      )}
      {tab === 'proceso' && (
        <div role="tabpanel" aria-label="Proceso">
          {isChild ? childNotice : (
            <DesignOrJson<Workflow>
              title="Proceso"
              jsonLabel="Proceso (JSON)"
              text={d.workflowJson}
              parsed={parsedWorkflow}
              onText={(workflowJson) => setD((s) => ({ ...s, workflowJson }))}
              placeholder=""
              hint="Estados y transiciones del documento. Déjalo vacío si el catálogo no tiene proceso (un proceso guardado no se quita)."
              error={visibleErrors.workflowJson}
              designer={(wf) => (
                <>
                  <CompilerErrors errors={sectionErrors('workflow')} />
                  <WorkflowDesigner workflow={wf} fields={designFields} roleNames={roleNames}
                    savedStates={new Set(original?.workflow?.states.map((x) => x.name) ?? [])}
                    savedTransitions={new Set(original?.workflow?.transitions.map((x) => x.name) ?? [])}
                    onChange={(next) => setD((s) => ({ ...s, workflowJson: pretty(next) }))} />
                </>
              )}
            />
          )}
        </div>
      )}
      {tab === 'reglas' && (
        <div role="tabpanel" aria-label="Reglas" className="stack">
          <JsonBacked<Rule[]> kind="array" text={d.rulesJson} onText={(rulesJson) => setD((s) => ({ ...s, rulesJson }))}
            title="Reglas" description="Validaciones de negocio: impiden guardar (error) o avisan sin impedirlo (aviso)."
            jsonLabel="Reglas (JSON)" errors={sectionErrors('rules')} clientError={visibleErrors.rulesJson}
            hint="Lista de reglas: name, assert, message y opcionalmente when, field y severity («warning»). Se validan en la vista previa.">
            {(v, write) => <RulesEditor rules={v ?? []} onChange={write} ctx={sectionCtx('rules')} />}
          </JsonBacked>
        </div>
      )}
      {tab === 'automatizaciones' && (
        <div role="tabpanel" aria-label="Automatizaciones" className="stack">
          <JsonBacked<Automation[]> kind="array" text={d.automationsJson} onText={(automationsJson) => setD((s) => ({ ...s, automationsJson }))}
            title="Automatizaciones" description="Cuando pasa algo en este catálogo, hacer algo en este u otros registros, en la misma transacción o después."
            jsonLabel="Automatizaciones (JSON)" errors={sectionErrors('automations')} clientError={visibleErrors.automationsJson}
            hint="on (create, update, enterState, transition), when, mode (sync o async) y acciones update, transition, create (createLines, capture), rollup o cost.">
            {(v, write) => <AutomationsEditor automations={v ?? []} onChange={write} ctx={sectionCtx('automations')} />}
          </JsonBacked>
        </div>
      )}
      {tab === 'seguridad' && (
        <div role="tabpanel" aria-label="Seguridad" className="stack">
          <JsonBacked<SecurityPolicy> kind="object" text={d.securityJson} onText={(securityJson) => setD((s) => ({ ...s, securityJson }))}
            title="Seguridad" description="Quién ve o modifica qué registros y campos. Se aplica en el servidor, también en listados, reportes y API."
            jsonLabel="Seguridad (JSON)" errors={sectionErrors('security')} clientError={visibleErrors.securityJson}
            hint="fields (field, read, edit, editableIn), owner, teamOwners, systemManaged y records (roles, scope all/unitTree/unit/own, where, access read/write).">
            {(v, write) => <SecurityEditor policy={v} onChange={write} ctx={sectionCtx('security')} />}
          </JsonBacked>
        </div>
      )}
      {tab === 'vistas' && (
        <div role="tabpanel" aria-label="Vistas y reportes" className="stack" style={{ gap: 'var(--space-6)' }}>
          <JsonBacked<ViewDef[]> kind="array" text={d.viewsJson} onText={(viewsJson) => setD((s) => ({ ...s, viewsJson }))}
            title="Vistas compartidas" description="Columnas, filtros y orden guardados del listado, por rol."
            jsonLabel="Vistas compartidas (JSON)" errors={sectionErrors('views')} clientError={visibleErrors.viewsJson}
            hint="name, label y opcionalmente roles, columns, filters (field, op, value; $me y $today), sort, dir, state y default.">
            {(v, write) => <ViewsEditor views={v ?? []} onChange={write} ctx={sectionCtx('views')} />}
          </JsonBacked>
          <JsonBacked<ReportDef[]> kind="array" text={d.reportsJson} onText={(reportsJson) => setD((s) => ({ ...s, reportsJson }))}
            title="Reportes" description="Agrupaciones y totales que se consultan en Análisis y se exportan."
            jsonLabel="Reportes (JSON)" errors={sectionErrors('reports')} clientError={visibleErrors.reportsJson}
            hint="groupBy (hasta 2; period en fechas), measures (count, sum, avg, min, max), filter, orderBy, dir, limit, chart y roles.">
            {(v, write) => <ReportsEditor reports={v ?? []} onChange={write} ctx={sectionCtx('reports')} />}
          </JsonBacked>
        </div>
      )}
      {tab === 'integraciones' && (
        <div role="tabpanel" aria-label="Integraciones" className="stack" style={{ gap: 'var(--space-6)' }}>
          <JsonBacked<WebhookDef[]> kind="array" text={d.webhooksJson} onText={(webhooksJson) => setD((s) => ({ ...s, webhooksJson }))}
            title="Webhooks" description="Avisos a otros sistemas. El destino y el secreto se configuran en Configuración › Integraciones."
            jsonLabel="Webhooks (JSON)" errors={sectionErrors('webhooks')} clientError={visibleErrors.webhooksJson}
            hint="events (on: create, update, enterState con state, transition con transition), role (rol de servicio), fields y when.">
            {(v, write) => <WebhooksEditor webhooks={v ?? []} onChange={write} ctx={sectionCtx('webhooks')} />}
          </JsonBacked>
          <JsonBacked<ScheduleDef[]> kind="array" text={d.schedulesJson} onText={(schedulesJson) => setD((s) => ({ ...s, schedulesJson }))}
            title="Tareas programadas" description="Revisan registros con un calendario y les aplican acciones. Llegan en pausa: se activan en Configuración › Automatizaciones."
            jsonLabel="Tareas programadas (JSON)" errors={sectionErrors('schedules')} clientError={visibleErrors.schedulesJson}
            hint="every (minutes con interval, hour con minute, day, week con weekdays, month con day) y at; where; role; acciones; batchSize (máx. 1000).">
            {(v, write) => <SchedulesEditor schedules={v ?? []} onChange={write} ctx={sectionCtx('schedules')} />}
          </JsonBacked>
        </div>
      )}
      {tab === 'contabilidad' && (
        <div role="tabpanel" aria-label="Contabilidad" className="stack">
          <JsonBacked<LedgerDef> kind="object" text={d.ledgerJson} onText={(ledgerJson) => setD((s) => ({ ...s, ledgerJson }))}
            title="Libro de partida doble" description="Cargos y abonos de las partidas cuadran en los estados contabilizados, y la fecha cae en un periodo abierto."
            jsonLabel="Libro de partida doble (JSON)" errors={sectionErrors('ledger')} clientError={visibleErrors.ledgerJson}
            hint="lines, debit, credit, date, states y period (entity, start, end, open). Vacío: no es un libro.">
            {(v, write) => <LedgerEditor ledger={v} onChange={write} ctx={sectionCtx('ledger')} />}
          </JsonBacked>
        </div>
      )}
      {tab === 'impresion' && (
        <div role="tabpanel" aria-label="Impresión" className="stack">
          {isChild ? (
            <Alert tone="info" title="Es un catálogo de partidas">Sus partidas se imprimen en la tabla de la plantilla de su documento.</Alert>
          ) : (
            <JsonBacked<PrintTemplate[]> kind="array" text={d.printTemplatesJson} onText={(printTemplatesJson) => setD((s) => ({ ...s, printTemplatesJson }))}
              title="Plantillas de impresión" description="El PDF de cada registro: encabezado con logo, campos, partidas, totales y firmas. Quien consulta el registro descarga el que le aplica, con su propia seguridad."
              jsonLabel="Plantillas de impresión (JSON)" errors={sectionErrors('print')} clientError={visibleErrors.printTemplatesJson}
              hint="name, label, roles, companies, when, default, page (size letter|a4, orientation, margin) y blocks (header, text, fields, table, totals, image, separator, signatures, pageBreak, footer).">
              {(v, write) => <PrintTemplatesEditor templates={v ?? []} onChange={write} ctx={sectionCtx('print')} saved={!isNew} />}
            </JsonBacked>
          )}
        </div>
      )}
      {tab === 'catalogo' && <div role="tabpanel" aria-label="Catálogo" className="stack" style={{ gap: 'var(--space-6)' }}>
      <CompilerErrors errors={sectionErrors('catalog')} />
      <section className="card card-pad stack">
        <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
          <h2 style={{ fontSize: 'var(--text-md)', fontWeight: 600 }} className="grow">General</h2>
          <OriginBadge origin={entityOrigin} />
        </div>
        <div className="form-grid">
          <Input
            label="Nombre (singular)"
            required
            value={d.label}
            error={visibleErrors.label}
            onChange={(e) => {
              const label = e.target.value;
              setD((s) => ({
                ...s,
                label,
                name: isNew && !nameTouched ? toIdentifier(label) : s.name,
                labelPlural: !pluralTouched ? pluralize(label) : s.labelPlural,
              }));
            }}
          />
          <Input label="Nombre (plural)" required value={d.labelPlural} error={visibleErrors.labelPlural} onChange={(e) => { setPluralTouched(true); setD((s) => ({ ...s, labelPlural: e.target.value })); }} />
          <Input
            label="Identificador"
            required
            className="mono"
            value={d.name}
            disabled={!isNew}
            error={visibleErrors.name}
            hint={isNew ? 'Nombre técnico de la tabla. No se puede cambiar después.' : 'No se puede cambiar.'}
            onChange={(e) => { setNameTouched(true); setD((s) => ({ ...s, name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })); }}
          />
          <IconPicker value={d.icon} onChange={(icon) => setD((s) => ({ ...s, icon: icon ?? 'package' }))} />
          <Textarea wrapClassName="span-2" label="Descripción" hint="Para qué sirve este catálogo." rows={2} style={{ minHeight: 64 }} value={d.description} onChange={(e) => setD((s) => ({ ...s, description: e.target.value }))} maxLength={500} />
          <Input label="Módulo" list="modeler-apps" autoComplete="off" hint="Módulo del menú, p. ej. Compras. Vacío: General." maxLength={60} value={d.group} onChange={(e) => setD((s) => ({ ...s, group: e.target.value }))} />
          <datalist id="modeler-apps">{appNames.map((n) => <option key={n} value={n} />)}</datalist>
          <Select label="Campo para mostrar en referencias" hint="Se usa cuando otro catálogo apunta a este" value={d.displayField} onChange={(e) => setD((s) => ({ ...s, displayField: e.target.value }))}>
            <option value="">Automático</option>
            {textFields.map((f) => <option key={f._key} value={f.name}>{f.label || f.name}</option>)}
          </Select>
          {/* Spec 010: catálogo compartido o por empresa. Pasar a por empresa no se puede deshacer. */}
          <Field
            label="Empresas"
            className="span-2"
            hint={d.fields.some((f) => f.type === 'parent')
              ? 'Las partidas siguen la empresa de su documento.'
              : original?.perCompany
                ? 'Cada empresa ve y captura solo sus registros. Un catálogo por empresa no vuelve a ser compartido.'
                : 'Compartido: todas las empresas ven los mismos registros. Por empresa: cada una ve y captura solo los suyos (los existentes quedan en la empresa predeterminada). No se puede deshacer.'}
          >
            <Switch
              checked={d.perCompany}
              disabled={!!original?.perCompany || d.fields.some((f) => f.type === 'parent')}
              onChange={(v) => setD((s) => ({ ...s, perCompany: v }))}
              label="Un registro por empresa"
            />
          </Field>
        </div>
      </section>

      <section className="card card-pad stack">
        <div className="row">
          <h2 style={{ fontSize: 'var(--text-md)', fontWeight: 600 }} className="grow">Campos</h2>
          <Button size="sm" icon={Plus} onClick={() => setD((s) => ({ ...s, fields: [...s.fields, blankField(s.fields.length)] }))}>Agregar campo</Button>
        </div>
        {visibleErrors.fields && <Alert tone="danger" title={visibleErrors.fields} />}
        <div className="fields-editor">
          {d.fields.map((f, i) => (
            <FieldRow
              key={f._key}
              f={f}
              index={i}
              count={d.fields.length}
              errors={visibleErrors}
              entities={entities.filter((x) => x.name !== original?.name)}
              allEntities={entities}
              roleNames={roleNames}
              origin={f._original ? ctx.origin('field', f._original.name) : undefined}
              scope={scope}
              onChange={(p) => patchField(f._key, p)}
              onMove={(dir) => move(i, dir)}
              onRemove={() => setD((s) => ({ ...s, fields: s.fields.filter((x) => x._key !== f._key) }))}
            />
          ))}
        </div>
      </section>
      </div>}

      <div className="row editor-footer">
        <span className="live-status grow" aria-live="polite">
          {showErrors && !clientOk ? <span className="field-error">Corrige los campos marcados antes de continuar.</span>
            : live.state === 'checking' ? <span className="subtle"><Loader2 size={13} className="spin" aria-hidden /> Validando con el compilador…</span>
            : live.state === 'done' && live.errors.length ? <span className="field-error"><AlertCircle size={13} aria-hidden /> El compilador encontró {live.errors.length} {live.errors.length === 1 ? 'error' : 'errores'}: revisa las pestañas marcadas.</span>
            : live.state === 'done' ? <span className="subtle"><CheckCircle2 size={13} aria-hidden /> El compilador no encontró errores.</span>
            : null}
        </span>
        <Button onClick={() => (dirty ? setConfirmDiscard(true) : onCancel())}>Cancelar</Button>
        <Button variant="primary" icon={Eye} disabled={!isNew && ops.length === 0} onClick={review}>
          {!isNew && ops.length === 0 ? 'Sin cambios' : 'Revisar cambios'}
        </Button>
      </div>
      <ConfirmDialog
        open={confirmDiscard}
        danger
        title="¿Descartar los cambios?"
        message="Los cambios de este catálogo aún no se aplican. Si sales ahora, se pierden."
        confirmLabel="Descartar cambios"
        cancelLabel="Seguir editando"
        onCancel={() => setConfirmDiscard(false)}
        onConfirm={() => { setConfirmDiscard(false); onCancel(); }}
      />
    </div>
  );
}
