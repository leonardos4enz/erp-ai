import { useState } from 'react';
import { Eye } from 'lucide-react';
import type { EntityDef, FieldDef, Layout } from '../../api/types';
import { Badge, Tabs } from '../../components/ui';
import { FIELD_TYPE_LABELS } from '../../lib/entity';
import { formSections, formTabs, type FormSection } from '../../lib/layout';
import { findField } from '../../lib/layoutEdit';

const NOTHING_HIDDEN = { hiddenSections: [], hiddenFields: [] };

/**
 * Spec 019 (H1 CA5): el formulario como lo verá el usuario, con la misma función que el formulario real
 * (`formSections`). Controles simulados: muestra estructura, no datos, y no consulta al servidor. Lo condicional
 * se marca en lugar de evaluarse (no hay registro con qué evaluarlo).
 */
export function LayoutPreview({ layout, fields, isDocument }: { layout: Layout | null; fields: FieldDef[]; isDocument: boolean }) {
  const [tab, setTab] = useState('');
  const def = { layout: layout ?? undefined } as EntityDef;
  const placed = new Set((layout?.sections ?? []).flatMap((s) => s.fields.map((i) => (typeof i === 'string' ? i : i.field).toLowerCase())));
  const isTotal = (f: FieldDef) => isDocument && f.type === 'formula' && !placed.has(f.name.toLowerCase());
  const sections = formSections(def, fields, NOTHING_HIDDEN, isTotal);
  const totals = fields.filter(isTotal);
  const tabs = sections ? formTabs(sections) : [];
  const active = tabs.includes(tab) ? tab : tabs[0];

  const conditionOf = (s: FormSection, f?: FieldDef): string | undefined => {
    if (!layout) return undefined;
    const sec = layout.sections.find((x) => x.name === s.name);
    if (!f) return sec?.visibleWhen;
    const pos = findField(layout, f.name);
    const item = pos ? layout.sections[pos.section].fields[pos.index] : undefined;
    return item && typeof item !== 'string' ? item.visibleWhen : undefined;
  };

  function renderField(f: FieldDef, wide: boolean, condition?: string) {
    return (
      <div key={f.name} className={`field preview-field${wide || f.type === 'longtext' ? ' span-2' : ''}`}>
        <span className="field-label">
          {f.label || f.name}
          {f.required && <span className="field-required" aria-hidden>*</span>}
          {condition && <Badge tone="warning">Condicional</Badge>}
        </span>
        <span className={`input preview-control${f.type === 'longtext' ? ' preview-control-tall' : ''}`} title={condition ? `Visible solo si ${condition}` : undefined}>
          {FIELD_TYPE_LABELS[f.type]}
        </span>
      </div>
    );
  }

  function renderSection(s: FormSection) {
    const condition = conditionOf(s);
    return (
      <div key={s.name} className="form-section">
        <h4 className="form-section-title">
          {s.label} {condition && <Badge tone="warning">Condicional</Badge>}
        </h4>
        {condition && <p className="subtle form-section-desc">Visible solo si <code>{condition}</code></p>}
        {s.description && <p className="subtle form-section-desc">{s.description}</p>}
        <div className={`form-grid${s.columns === 1 ? ' form-grid-1' : ''}`}>{s.fields.map((x) => renderField(x.field, x.wide, conditionOf(s, x.field)))}</div>
      </div>
    );
  }

  let body;
  if (!sections) {
    const main = fields.filter((f) => !isTotal(f));
    body = main.length ? <div className="form-grid">{main.map((f) => renderField(f, false))}</div> : <p className="muted">Sin campos que mostrar.</p>;
  } else if (!tabs.length) {
    body = <div className="form-sections">{sections.map(renderSection)}</div>;
  } else {
    body = (
      <div>
        <Tabs items={tabs.map((t) => ({ id: t, label: t }))} value={active!} onChange={setTab} label="Pestañas de la vista previa" />
        {tabs.map((t) => (
          <div key={t} role="tabpanel" aria-label={t} hidden={t !== active} className="form-sections">
            {sections.filter((s) => s.tab === t).map(renderSection)}
          </div>
        ))}
      </div>
    );
  }

  return (
    <section className="ld-preview" aria-labelledby="ld-preview-title">
      <h3 id="ld-preview-title" className="ld-pane-title"><Eye size={15} aria-hidden /> Vista previa</h3>
      <div className="ld-preview-frame">
        {body}
        {totals.length > 0 && (
          <div className="form-section">
            <h4 className="form-section-title">Totales</h4>
            <div className="form-grid">{totals.map((f) => renderField(f, false))}</div>
          </div>
        )}
      </div>
    </section>
  );
}
