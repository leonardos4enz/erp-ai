import { KeyRound, Plus, Trash2, UserCheck } from 'lucide-react';
import type { FieldSecurity, RecordGrant, SecurityPolicy } from '../../../api/types';
import { ExprInput } from '../../../components/ExprInput';
import { Button, EmptyState, Select, Switch } from '../../../components/ui';
import { clean, fieldOptions, MultiPick, OriginBadge, PackageNotice, roleOptions, sameValue, selfScope, type SectionCtx } from './common';

const SCOPES: Record<NonNullable<RecordGrant['scope']>, string> = {
  own: 'Solo los propios', unit: 'Los de su unidad', unitTree: 'Su unidad y subunidades', all: 'Todos',
};

/** Spec 026 (H4 CA3): seguridad por campo y por registro (006, 015). Vacío = sin política. */
export function SecurityEditor({ policy, onChange, ctx }: { policy: SecurityPolicy | null; onChange: (p: SecurityPolicy | null) => void; ctx: SectionCtx }) {
  const origin = ctx.origin('security', '');
  const saved = ctx.saved('security', '');
  const changed = !!origin?.package && saved !== undefined && !sameValue(saved, policy);
  if (!policy) {
    return (
      <div className="section-empty">
        <EmptyState icon={KeyRound} title="Este catálogo no tiene seguridad propia"
          description="Todos los roles con permiso sobre el catálogo ven y capturan todos sus campos y registros."
          action={<Button size="sm" icon={Plus} onClick={() => onChange({ records: [] })}>Definir seguridad</Button>} />
      </div>
    );
  }
  const set = (p: Partial<SecurityPolicy>) => {
    const next = clean({ ...policy, ...p });
    onChange(Object.keys(next).length ? next : { records: [] });
  };
  const fields = policy.fields ?? [];
  const records = policy.records ?? [];
  const ownerFields = ctx.fields.filter((f) => f.type === 'user' || f.type === 'reference');
  const securable = ctx.fields.filter((f) => f.type !== 'state' && f.type !== 'parent');
  const roles = roleOptions(ctx);
  const states = ctx.states.map((s) => ({ value: s.name, label: s.label }));
  // `editableIn: []` sí significa algo (solo en las transiciones que lo piden): no se limpia como lista vacía.
  const patchField = (i: number, p: Partial<FieldSecurity>) => set({
    fields: fields.map((x, k) => {
      if (k !== i) return x;
      const merged = { ...x, ...p };
      const out = clean(merged) as FieldSecurity;
      if (merged.editableIn !== undefined) out.editableIn = merged.editableIn;
      return out;
    }),
  });
  // `effectiveScope` es solo de salida: al cambiar el alcance ya no corresponde y no se reescribe.
  const patchGrant = (i: number, p: Partial<RecordGrant>) => set({
    records: records.map((x, k) => (k === i ? clean({ ...x, ...p, ...('scope' in p ? { effectiveScope: undefined } : {}) }) : x)),
  });

  return (
    <div className="stack" style={{ gap: 'var(--space-4)' }}>
      <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
        <OriginBadge origin={origin} />
        <span className="grow" />
        <Button size="sm" variant="ghost" icon={Trash2} onClick={() => onChange(null)}>Quitar la seguridad</Button>
      </div>
      {changed && <PackageNotice origin={origin} />}

      <fieldset className="subgroup">
        <legend className="field-label">Dueño del registro</legend>
        <div className="item-grid">
          <Select label="Campo dueño" value={policy.owner ?? ''} onChange={(e) => set({ owner: e.target.value || undefined, teamOwners: e.target.value ? policy.teamOwners : undefined })}
            hint="Define «propios»: el registro es de quien está en este campo. Vacío: quien lo creó.">
            {fieldOptions(ownerFields, 'Quien lo creó')}
          </Select>
          <div className="field">
            <span className="field-label">Equipos</span>
            <Switch checked={!!policy.teamOwners} disabled={!policy.owner} onChange={(v) => set({ teamOwners: v || undefined })} label="El dueño puede ser un equipo" />
            <span className="field-hint">{policy.owner ? 'Los miembros del equipo cuentan como dueños.' : 'Requiere un campo dueño.'}</span>
          </div>
          <div className="field span-all">
            <Switch checked={!!policy.systemManaged} onChange={(v) => set({ systemManaged: v || undefined })} label="Solo el sistema escribe (las personas solo consultan)" />
          </div>
        </div>
      </fieldset>

      <fieldset className="subgroup">
        <legend className="field-label">Qué registros ve o modifica cada rol</legend>
        {records.length === 0 && <p className="field-hint">Sin reglas por registro: cada rol ve todos los registros que su permiso permite.</p>}
        {records.map((g, i) => (
          <div className="subitem" key={i}>
            <div className="item-grid">
              <Select label={`Regla ${i + 1}: alcance`} value={g.scope ?? 'all'} onChange={(e) => patchGrant(i, { scope: e.target.value as RecordGrant['scope'] })}>
                {(Object.keys(SCOPES) as NonNullable<RecordGrant['scope']>[]).map((s) => <option key={s} value={s}>{SCOPES[s]}</option>)}
              </Select>
              <Select label={`Regla ${i + 1}: acceso`} value={g.access ?? 'write'} onChange={(e) => patchGrant(i, { access: e.target.value === 'read' ? 'read' : undefined })}>
                <option value="write">Ver y modificar</option>
                <option value="read">Solo ver</option>
              </Select>
              <MultiPick legend={`Regla ${i + 1}: roles`} options={roles} value={g.roles ?? []} onChange={(r) => patchGrant(i, { roles: r })} hint="Ninguno: todos los roles." />
              <ExprInput condition className="span-all" label={`Regla ${i + 1}: además, solo donde (opcional)`} value={g.where ?? ''} onChange={(v) => patchGrant(i, { where: v })}
                scope={selfScope(ctx)} entities={ctx.entities} hint="Condición traducible a SQL, p. ej. Departamento = USER.Departamento." />
            </div>
            <Button size="sm" variant="ghost" icon={Trash2} onClick={() => set({ records: records.filter((_, k) => k !== i) })}>Quitar regla {i + 1}</Button>
          </div>
        ))}
        <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => set({ records: [...records, { scope: 'own' }] })}>Agregar regla por registro</Button></div>
      </fieldset>

      <fieldset className="subgroup">
        <legend className="field-label">Campos con acceso restringido</legend>
        {fields.length === 0 && <p className="field-hint">Ningún campo restringido: todos los roles con permiso los ven y capturan.</p>}
        {fields.map((fs, i) => (
          <div className="subitem" key={i}>
            <div className="item-grid">
              <Select label={`Campo ${i + 1}`} value={fs.field ?? ''} onChange={(e) => patchField(i, { field: e.target.value })}>
                {fieldOptions(securable, 'Elige un campo…')}
              </Select>
              <div className="field">
                <span className="field-label">Estados en que se captura</span>
                <Switch checked={fs.editableIn !== undefined} onChange={(v) => patchField(i, { editableIn: v ? [] : undefined })} label="Solo en ciertos estados" />
              </div>
              <MultiPick legend={`Campo ${i + 1}: roles que lo ven`} options={roles} value={fs.read ?? []} onChange={(r) => patchField(i, { read: r })} hint="Ninguno: todos lo ven." />
              <MultiPick legend={`Campo ${i + 1}: roles que lo capturan`} options={roles} value={fs.edit ?? []} onChange={(r) => patchField(i, { edit: r })} hint="Ninguno: lo captura quien lo ve." />
              {fs.editableIn !== undefined && (
                <MultiPick legend={`Campo ${i + 1}: estados en que se captura`} options={states} value={fs.editableIn} onChange={(s) => set({ fields: fields.map((x, k) => (k === i ? { ...x, editableIn: s } : x)) })}
                  hint="Ninguno: solo en las transiciones que lo piden (p. ej. el motivo de un rechazo)." empty="El catálogo no tiene proceso." />
              )}
            </div>
            <Button size="sm" variant="ghost" icon={Trash2} onClick={() => set({ fields: fields.filter((_, k) => k !== i) })}>Quitar campo {i + 1}</Button>
          </div>
        ))}
        <div><Button size="sm" variant="ghost" icon={UserCheck} onClick={() => set({ fields: [...fields, { field: '' }] })}>Restringir un campo</Button></div>
      </fieldset>
    </div>
  );
}
