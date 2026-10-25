import { ShieldCheck } from 'lucide-react';
import type { Rule } from '../../../api/types';
import { ExprInput } from '../../../components/ExprInput';
import { Badge, Input, Select } from '../../../components/ui';
import { fieldOptions, ItemList, selfScope, uniqueName, type SectionCtx } from './common';

/** Spec 026 (H4 CA1): reglas del catálogo (validaciones de negocio y avisos de la 023). */
export function RulesEditor({ rules, onChange, ctx }: { rules: Rule[]; onChange: (r: Rule[]) => void; ctx: SectionCtx }) {
  return (
    <ItemList<Rule>
      items={rules}
      onChange={onChange}
      kind="rule"
      ctx={ctx}
      icon={ShieldCheck}
      noun="regla"
      emptyTitle="Este catálogo no tiene reglas"
      emptyDescription="Una regla impide guardar (error) o avisa sin impedirlo (aviso) cuando una condición no se cumple."
      addLabel="Agregar regla"
      create={(items) => ({ name: uniqueName('Regla', items.map((r) => r.name)), message: '', assert: '' })}
      name={(r) => r.name}
      title={(r) => r.message}
      summary={(r) => (
        <>
          <Badge tone={r.severity === 'warning' ? 'warning' : 'danger'}>{r.severity === 'warning' ? 'Aviso' : 'Error'}</Badge>
          <code className="mono subtle">{r.assert || 'sin condición'}</code>
        </>
      )}
      render={(r, patch) => (
        <div className="item-grid">
          <Input label="Identificador" required className="mono" value={r.name ?? ''} maxLength={60}
            hint="Letras, números y guion bajo; único en el catálogo."
            onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
          <Select label="Severidad" value={r.severity ?? 'error'} onChange={(e) => patch({ severity: e.target.value === 'warning' ? 'warning' : undefined })}
            hint={r.severity === 'warning' ? 'Avisa sin impedir guardar; las transiciones deciden si piden confirmación.' : 'Impide guardar mientras no se cumpla.'}>
            <option value="error">Error (impide guardar)</option>
            <option value="warning">Aviso (no impide guardar)</option>
          </Select>
          <Input wrapClassName="span-all" label="Mensaje" required value={r.message ?? ''} maxLength={300}
            hint="Lo que verá la persona cuando la condición no se cumpla." onChange={(e) => patch({ message: e.target.value })} />
          <ExprInput condition className="span-all" label="Condición que debe cumplirse" required value={r.assert ?? ''} onChange={(v) => patch({ assert: v })}
            scope={selfScope(ctx)} entities={ctx.entities} hint="Si da falso, se muestra el mensaje. Comparar contra un campo vacío no viola la regla." />
          <ExprInput condition className="span-all" label="Aplica solo cuando (opcional)" value={r.when ?? ''} onChange={(v) => patch({ when: v })}
            scope={selfScope(ctx)} entities={ctx.entities} hint="Vacío: siempre." />
          <Select label="Campo donde se muestra" value={r.field ?? ''} onChange={(e) => patch({ field: e.target.value || undefined })}
            hint="Vacío: en el encabezado del registro.">
            {fieldOptions(ctx.fields, 'Todo el registro')}
          </Select>
        </div>
      )}
    />
  );
}
