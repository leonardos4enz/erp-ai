import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  Archive, ArchiveRestore, ArrowDown, ArrowUp, ChevronDown, ChevronUp, Eye, LayoutTemplate, Plus, Table2, Trash2, Workflow as WorkflowIcon,
} from 'lucide-react';
import { ApiError } from '../../api/client';
import { adminApi } from '../../api/endpoints';
import type { Automation, ChangePlan, EntityDef, FieldDef, FieldType, FormulaResultType, LedgerDef, Layout, Operation, ReportDef, Rule, ScheduleDef, SecurityPolicy, ViewDef, WebhookDef, Workflow } from '../../api/types';
import { ConfirmDialog } from '../../components/Dialog';
import { IconPicker } from '../../components/IconPicker';
import { TagInput } from '../../components/TagInput';
import { Alert, Button, Field, Input, Select, Switch, Tabs, Textarea } from '../../components/ui';
import { FIELD_TYPE_LABELS, MODELER_FIELD_TYPES, supportsDefault } from '../../lib/entity';
import { IDENTIFIER_RE, pluralize, toIdentifier } from '../../lib/format';
import { DesignOrJson, type JsonResult } from './DesignOrJson';
import { LayoutDesigner } from './LayoutDesigner';
import { WorkflowDesigner } from './WorkflowDesigner';

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

function parseJsonError(text: string, kind: 'array' | 'object'): string | undefined {
  const r = parseJson<unknown>(text, kind);
  return r.ok ? undefined : r.error;
}

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
    return { name: '', label: '', labelPlural: '', icon: 'package', description: '', displayField: '', group: '', perCompany: false, fields: [f], rulesJson: '', workflowJson: '', automationsJson: '', securityJson: '', layoutJson: '', viewsJson: '', reportsJson: '', webhooksJson: '', schedulesJson: '', ledgerJson: '' };
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
  return e;
}

/* ------------------------------------------------------------------------- */

interface FieldRowProps {
  f: DraftField;
  index: number;
  count: number;
  errors: Record<string, string>;
  entities: EntityDef[];
  roleNames: string[] | null;
  onChange: (patch: Partial<DraftField>) => void;
  onMove: (dir: -1 | 1) => void;
  onRemove: () => void;
}

function FieldRow({ f, index, count, errors, entities, roleNames, onChange, onMove, onRemove }: FieldRowProps) {
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
          placeholder="Etiqueta (p. ej. Nombre)"
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
        <div className="field-row-tools">
          {!f.isArchived && (
            <Button variant="ghost" size="sm" iconOnly icon={expanded ? ChevronUp : ChevronDown} aria-label={expanded ? 'Ocultar opciones' : 'Mostrar opciones'} aria-expanded={expanded} onClick={() => setExpanded((x) => !x)} />
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
      {expanded && !f.isArchived && (
        <div className="field-row-body">
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
              <Input inputSize="sm" label="Prefijo" className="mono" placeholder="OC-" maxLength={10} disabled={locked} value={f.prefix ?? ''} onChange={(e) => onChange({ prefix: e.target.value })} />
              <Input inputSize="sm" type="number" min={1} max={12} label="Dígitos" disabled={locked} value={f.padding ?? 6} onChange={(e) => onChange({ padding: Number(e.target.value) || undefined })} hint={`Ejemplo: ${(f.prefix ?? '')}${'1'.padStart(f.padding ?? 6, '0')}`} />
            </>
          )}
          {f.type === 'formula' && (
            <>
              <Textarea
                wrapClassName="span-all"
                className="mono"
                label="Fórmula"
                rows={2}
                maxLength={1000}
                placeholder="Cantidad * PrecioUnitario"
                hint="Usa nombres de campos y funciones como ROUND, IF, SUM(Partidas.Importe)."
                error={err('formula')}
                value={f.formula ?? ''}
                onChange={(e) => onChange({ formula: e.target.value })}
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
            <Input
              inputSize="sm"
              wrapClassName="span-all"
              className="mono"
              label="Valor por defecto"
              placeholder="Sin valor por defecto"
              maxLength={1000}
              hint="Expresión, p. ej. TODAY() o Producto.CostoUltimo"
              value={f.defaultValue ?? ''}
              onChange={(e) => onChange({ defaultValue: e.target.value })}
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

export type EditorTab = 'catalogo' | 'formulario' | 'proceso';
export const editorTab = (v: string | null): EditorTab => (v === 'formulario' || v === 'proceso' ? v : 'catalogo');

export interface EntityEditorProps {
  original?: EntityDef;
  entities: EntityDef[];
  onCancel: () => void;
  onReview: (plan: ChangePlan) => void;
}

export function EntityEditor({ original, entities, onCancel, onReview }: EntityEditorProps) {
  const isNew = !original;
  const [d, setD] = useState<Draft>(() => draftFrom(original));
  const [nameTouched, setNameTouched] = useState(!isNew);
  const [pluralTouched, setPluralTouched] = useState(!isNew);
  const [showErrors, setShowErrors] = useState(false);
  const [roleNames, setRoleNames] = useState<string[] | null>(null);
  // Spec 019: pestañas del editor en la URL; el borrador es uno solo para las tres.
  const [params, setParams] = useSearchParams();
  const tab = editorTab(params.get('tab'));
  const setTab = (t: EditorTab) => {
    const p = new URLSearchParams(params);
    if (t === 'catalogo') p.delete('tab'); else p.set('tab', t);
    setParams(p, { replace: true });
  };

  useEffect(() => {
    adminApi.roles().then((r) => setRoleNames(r.map((x) => x.name))).catch((e) => { if (e instanceof ApiError) setRoleNames(null); });
  }, []);

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
      const target: EditorTab = keys.some((k) => k !== 'layoutJson' && k !== 'workflowJson') ? 'catalogo' : keys.includes('layoutJson') ? 'formulario' : 'proceso';
      if (target !== tab) setTab(target);
      return;
    }
    const description = isNew ? `Crear catálogo ${d.labelPlural.trim()}` : `Modificar catálogo ${original!.labelPlural}`;
    // Spec 021 (H2 CA1): la versión con la que se abrió; si otra persona aplicó un cambio después, la vista previa lo dice.
    onReview({ description, operations: ops, baseVersions: original?.version ? { [original.name]: original.version } : undefined });
  }

  const parsedLayout = parseJson<Layout>(d.layoutJson, 'object');
  const parsedWorkflow = parseJson<Workflow>(d.workflowJson, 'object');
  // Campos activos del borrador con identificador válido: los nuevos también se pueden colocar en el formulario.
  const designFields: FieldDef[] = d.fields.filter((f) => !f.isArchived && IDENTIFIER_RE.test(f.name));
  const isChild = d.fields.some((f) => !f.isArchived && f.type === 'parent');
  const isDocument = d.fields.some((f) => !f.isArchived && f.type === 'lines');
  const tabErrors = (t: EditorTab) => showErrors && Object.keys(errors).some((k) => (t === 'formulario' ? k === 'layoutJson' : t === 'proceso' ? k === 'workflowJson' : k !== 'layoutJson' && k !== 'workflowJson'));
  const childNotice = (
    <Alert tone="info" title="Es un catálogo de partidas">
      Sus registros se capturan como tabla dentro de su documento: el formulario y el proceso se definen en el documento.
    </Alert>
  );

  return (
    <div className="stack" style={{ gap: 'var(--space-6)' }}>
      <div className="editor-tabs">
      <Tabs<EditorTab>
        label="Partes del catálogo"
        value={tab}
        onChange={setTab}
        items={[
          { id: 'catalogo', label: tabErrors('catalogo') ? 'Catálogo (con errores)' : 'Catálogo', icon: Table2 },
          { id: 'formulario', label: tabErrors('formulario') ? 'Formulario (con errores)' : 'Formulario', icon: LayoutTemplate },
          { id: 'proceso', label: tabErrors('proceso') ? 'Proceso (con errores)' : 'Proceso', icon: WorkflowIcon },
        ]}
      />
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
              placeholder={'{\n  "sections": [\n    { "name": "General", "label": "Datos generales", "tab": "General", "fields": ["Nombre", "Fecha"] },\n    { "name": "Pago", "label": "Pago", "tab": "General", "fields": ["Moneda", { "field": "TipoCambio", "visibleWhen": "Moneda.Clave <> \'MXN\'" }] }\n  ]\n}'}
              hint="Secciones con name, label, fields y opcionalmente tab, columns (1 o 2), description y visibleWhen (también por campo). Los campos no mencionados van en «Otros datos». Vacío: sin layout."
              error={visibleErrors.layoutJson}
              designer={(layout) => (
                <LayoutDesigner layout={layout} isDocument={isDocument} fields={designFields}
                  onChange={(next) => setD((s) => ({ ...s, layoutJson: next ? pretty(next) : '' }))} />
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
              placeholder={'{\n  "initial": "Borrador",\n  "states": [{ "name": "Borrador", "label": "Borrador", "editable": true }],\n  "transitions": []\n}'}
              hint="Estados y transiciones del documento. Déjalo vacío si el catálogo no tiene proceso (un proceso guardado no se quita)."
              error={visibleErrors.workflowJson}
              designer={(workflow) => (
                <WorkflowDesigner workflow={workflow} fields={designFields} roleNames={roleNames}
                  savedStates={new Set(original?.workflow?.states.map((x) => x.name) ?? [])}
                  savedTransitions={new Set(original?.workflow?.transitions.map((x) => x.name) ?? [])}
                  onChange={(next) => setD((s) => ({ ...s, workflowJson: pretty(next) }))} />
              )}
            />
          )}
        </div>
      )}
      {tab === 'catalogo' && <div role="tabpanel" aria-label="Catálogo" className="stack" style={{ gap: 'var(--space-6)' }}>
      <section className="card card-pad stack">
        <h2 style={{ fontSize: 'var(--text-md)', fontWeight: 600 }}>General</h2>
        <div className="form-grid">
          <Input
            label="Nombre (singular)"
            required
            placeholder="Empaque"
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
          <Input label="Nombre (plural)" required placeholder="Empaques" value={d.labelPlural} error={visibleErrors.labelPlural} onChange={(e) => { setPluralTouched(true); setD((s) => ({ ...s, labelPlural: e.target.value })); }} />
          <Input
            label="Identificador"
            required
            className="mono"
            placeholder="Empaque"
            value={d.name}
            disabled={!isNew}
            error={visibleErrors.name}
            hint={isNew ? 'Nombre técnico de la tabla. No se puede cambiar después.' : 'No se puede cambiar.'}
            onChange={(e) => { setNameTouched(true); setD((s) => ({ ...s, name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })); }}
          />
          <IconPicker value={d.icon} onChange={(icon) => setD((s) => ({ ...s, icon: icon ?? 'package' }))} />
          <Textarea wrapClassName="span-2" label="Descripción" placeholder="Para qué sirve este catálogo" rows={2} style={{ minHeight: 64 }} value={d.description} onChange={(e) => setD((s) => ({ ...s, description: e.target.value }))} maxLength={500} />
          <Input label="Grupo del menú" hint="Sección del menú lateral, p. ej. Compras. Vacío: Catálogos." maxLength={60} value={d.group} onChange={(e) => setD((s) => ({ ...s, group: e.target.value }))} />
          <Select label="Campo para mostrar en referencias" hint="Se usa cuando otro catálogo apunta a este" value={d.displayField} onChange={(e) => setD((s) => ({ ...s, displayField: e.target.value }))}>
            <option value="">Automático</option>
            {textFields.map((f) => <option key={f._key} value={f.name}>{f.label || f.name}</option>)}
          </Select>
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
              roleNames={roleNames}
              onChange={(p) => patchField(f._key, p)}
              onMove={(dir) => move(i, dir)}
              onRemove={() => setD((s) => ({ ...s, fields: s.fields.filter((x) => x._key !== f._key) }))}
            />
          ))}
        </div>
      </section>

      <details className="card card-pad advanced-section" open={!!(original?.perCompany || original?.rules?.length || original?.automations?.length || original?.security || original?.views?.length || original?.reports?.length || original?.webhooks?.length || original?.schedules?.length || original?.ledger) || undefined}>
        <summary className="advanced-summary">
          <span className="section-title">Reglas, automatizaciones, seguridad, vistas e integraciones</span>
          <span className="subtle">Avanzado · validaciones de negocio, efectos en otros registros, quién ve o edita qué, vistas, reportes, webhooks y tareas</span>
        </summary>
        <div className="stack" style={{ marginTop: 'var(--space-4)' }}>
          {/* Spec 010: catálogo compartido o por empresa. Pasar a por empresa no se puede deshacer. */}
          <Field
            label="Empresas"
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
          <Textarea
            className="mono json-editor"
            label="Reglas (JSON)"
            rows={8}
            spellCheck={false}
            placeholder={'[\n  { "name": "FechaValida", "assert": "FechaEntrega >= Fecha", "message": "La entrega no puede ser antes de la fecha", "field": "FechaEntrega" }\n]'}
            hint="Lista de reglas: name, assert, message y opcionalmente when y field. Se validan en la vista previa."
            error={visibleErrors.rulesJson ?? (d.rulesJson ? parseJsonError(d.rulesJson, 'array') : undefined)}
            value={d.rulesJson}
            onChange={(e) => setD((s) => ({ ...s, rulesJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Automatizaciones (JSON)"
            rows={12}
            spellCheck={false}
            placeholder={'[\n  { "name": "AlConfirmar", "label": "Al confirmar", "on": "enterState", "state": "Confirmada",\n    "actions": [{ "type": "transition", "target": "OrdenCompra", "transition": "recibir" }] }\n]'}
            hint="Efectos en otros registros: on (create, update, enterState, transition) y acciones update, transition, create (con createLines y capture), rollup o cost (políticas: uncostedEntry average/fallback/require, methodChange merge/keep). Corren en la misma transacción, o después con mode: async; se validan en la vista previa."
            error={visibleErrors.automationsJson ?? (d.automationsJson ? parseJsonError(d.automationsJson, 'array') : undefined)}
            value={d.automationsJson}
            onChange={(e) => setD((s) => ({ ...s, automationsJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Seguridad (JSON)"
            rows={12}
            spellCheck={false}
            placeholder={'{\n  "fields": [{ "field": "Costo", "read": ["Gerente"] }, { "field": "Motivo", "editableIn": [] }],\n  "owner": "Solicitante",\n  "records": [{ "roles": ["Solicitante"], "scope": "own" }, { "roles": ["Gerente"], "scope": "all" }]\n}'}
            hint="Por campo: read y edit (roles que lo ven o capturan) y editableIn (estados en que se captura). Por registro: owner y records con roles, scope (all, unit, own), where y access (read o write). systemManaged: solo el sistema lo escribe. Se aplica en el servidor."
            error={visibleErrors.securityJson ?? (d.securityJson ? parseJsonError(d.securityJson, 'object') : undefined)}
            value={d.securityJson}
            onChange={(e) => setD((s) => ({ ...s, securityJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Vistas compartidas (JSON)"
            rows={10}
            spellCheck={false}
            placeholder={'[\n  { "name": "Mias", "label": "Mis registros", "filters": [{ "field": "Responsable", "op": "eq", "value": "$me" }], "default": true }\n]'}
            hint="Vistas del listado: name, label y opcionalmente roles, columns, filters (field, op, value; $me y $today), sort, dir, state y default."
            error={visibleErrors.viewsJson ?? (d.viewsJson ? parseJsonError(d.viewsJson, 'array') : undefined)}
            value={d.viewsJson}
            onChange={(e) => setD((s) => ({ ...s, viewsJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Reportes (JSON)"
            rows={12}
            spellCheck={false}
            placeholder={'[\n  { "name": "PorMes", "label": "Por mes", "groupBy": [{ "field": "Fecha", "period": "month" }],\n    "measures": [{ "fn": "count" }, { "fn": "sum", "field": "Total" }], "chart": "line" }\n]'}
            hint="groupBy (hasta 2: Campo o Relacion.Campo; period day, month o year en fechas), measures (count, sum, avg, min, max), filter, orderBy, dir, limit, chart (bar, line, pie) y roles."
            error={visibleErrors.reportsJson ?? (d.reportsJson ? parseJsonError(d.reportsJson, 'array') : undefined)}
            value={d.reportsJson}
            onChange={(e) => setD((s) => ({ ...s, reportsJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Webhooks (JSON)"
            rows={10}
            spellCheck={false}
            placeholder={'[\n  { "name": "Alta", "label": "Registro nuevo", "events": [{ "on": "create" }],\n    "role": "Integración", "fields": ["Folio", "Nombre"] }\n]'}
            hint="events (on: create, update, enterState con state, transition con transition), role (rol de servicio: se envía lo que ese rol ve), fields, when. El destino y el secreto se configuran en Integraciones."
            error={visibleErrors.webhooksJson ?? (d.webhooksJson ? parseJsonError(d.webhooksJson, 'array') : undefined)}
            value={d.webhooksJson}
            onChange={(e) => setD((s) => ({ ...s, webhooksJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Tareas programadas (JSON)"
            rows={10}
            spellCheck={false}
            placeholder={'[\n  { "name": "Vencer", "label": "Vencer pedidos", "every": "day", "at": "07:00",\n    "where": "Estado = \'Abierto\' AND Vence < TODAY()", "role": "Cobranza",\n    "actions": [{ "type": "transition", "transition": "vencer" }] }\n]'}
            hint="every (minutes con interval, hour con minute, day, week con weekdays 1–7, month con day) y at 'HH:mm'; where (qué registros, traducible a SQL); role (rol de servicio); acciones como las automatizaciones (sin target, el propio registro); batchSize (máx. 1000). Llegan en pausa: se activan en Configuración › Automatizaciones."
            error={visibleErrors.schedulesJson ?? (d.schedulesJson ? parseJsonError(d.schedulesJson, 'array') : undefined)}
            value={d.schedulesJson}
            onChange={(e) => setD((s) => ({ ...s, schedulesJson: e.target.value }))}
          />
          <Textarea
            className="mono json-editor"
            label="Libro de partida doble (JSON)"
            rows={8}
            spellCheck={false}
            placeholder={'{\n  "lines": "Partidas", "debit": "Cargo", "credit": "Abono", "date": "Fecha", "states": ["Aplicada"],\n  "period": { "entity": "PeriodoContable", "start": "Inicio", "end": "Fin", "open": "Estado = \'Abierto\'" }\n}'}
            hint="lines (partidas), debit y credit (campos numéricos de la partida), date, states (estados contabilizados: ahí cargos = abonos) y period (catálogo de periodos con inicio, fin y la condición de abierto). Vacío: no es un libro."
            error={visibleErrors.ledgerJson ?? (d.ledgerJson ? parseJsonError(d.ledgerJson, 'object') : undefined)}
            value={d.ledgerJson}
            onChange={(e) => setD((s) => ({ ...s, ledgerJson: e.target.value }))}
          />
        </div>
      </details>
      </div>}

      <div className="row" style={{ justifyContent: 'flex-end' }}>
        {showErrors && Object.keys(errors).length > 0 && <span className="field-error" style={{ marginRight: 'auto' }}>Corrige los campos marcados antes de continuar.</span>}
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
