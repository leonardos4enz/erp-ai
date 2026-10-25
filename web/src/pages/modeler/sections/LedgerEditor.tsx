import { BookOpen, Plus, Trash2 } from 'lucide-react';
import type { LedgerDef } from '../../../api/types';
import { ExprInput } from '../../../components/ExprInput';
import { Button, EmptyState, Select } from '../../../components/ui';
import { activeFields, clean, entityByName, entityOptions, fieldOptions, isNumeric, MultiPick, OriginBadge, PackageNotice, sameValue, scopeOf, type SectionCtx } from './common';

/** Spec 026 (H4 CA8): libro de partida doble (020). El cuadre y los periodos los garantiza el motor. */
export function LedgerEditor({ ledger, onChange, ctx }: { ledger: LedgerDef | null; onChange: (l: LedgerDef | null) => void; ctx: SectionCtx }) {
  const origin = ctx.origin('ledger', '');
  const saved = ctx.saved('ledger', '');
  const changed = !!origin?.package && saved !== undefined && !sameValue(saved, ledger);
  const linesFields = ctx.fields.filter((f) => f.type === 'lines');
  if (!ledger) {
    return (
      <div className="section-empty">
        <EmptyState icon={BookOpen} title="Este catálogo no es un libro de partida doble"
          description={linesFields.length ? 'Un libro exige que cargos y abonos de sus partidas cuadren en los estados contabilizados y que la fecha caiga en un periodo abierto.' : 'Un libro necesita un campo de partidas (con cargo y abono).'}
          action={<Button size="sm" icon={Plus} disabled={!linesFields.length} onClick={() => onChange({ lines: linesFields[0]?.name ?? '', debit: '', credit: '' })}>Convertir en libro</Button>} />
      </div>
    );
  }
  const set = (p: Partial<LedgerDef>) => onChange(clean({ ...ledger, ...p }) as LedgerDef);
  const child = entityByName(ctx, linesFields.find((f) => f.name === ledger.lines)?.childEntity);
  const childNumeric = activeFields(child).filter(isNumeric);
  const dates = ctx.fields.filter((f) => f.type === 'date' || f.type === 'datetime');
  const period = ledger.period;
  const periodEntity = entityByName(ctx, period?.entity);
  const periodDates = activeFields(periodEntity).filter((f) => f.type === 'date' || f.type === 'datetime');
  const setPeriod = (p: Partial<NonNullable<LedgerDef['period']>>) => set({ period: { entity: '', start: '', end: '', open: '', ...period, ...p } });

  return (
    <div className="stack" style={{ gap: 'var(--space-4)' }}>
      <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
        <OriginBadge origin={origin} />
        <span className="grow" />
        <Button size="sm" variant="ghost" icon={Trash2} onClick={() => onChange(null)}>Dejar de ser libro</Button>
      </div>
      {changed && <PackageNotice origin={origin} />}
      <div className="item-grid">
        <Select label="Partidas" required value={ledger.lines ?? ''} onChange={(e) => set({ lines: e.target.value })}>
          {fieldOptions(linesFields, 'Elige…')}
        </Select>
        <Select label="Fecha del documento" value={ledger.date ?? ''} onChange={(e) => set({ date: e.target.value || undefined })} hint="Decide el periodo contable.">
          {fieldOptions(dates, 'Sin fecha')}
        </Select>
        <Select label="Cargo (campo de la partida)" required value={ledger.debit ?? ''} onChange={(e) => set({ debit: e.target.value })}>
          {fieldOptions(childNumeric, 'Elige…')}
        </Select>
        <Select label="Abono (campo de la partida)" required value={ledger.credit ?? ''} onChange={(e) => set({ credit: e.target.value })}>
          {fieldOptions(childNumeric, 'Elige…')}
        </Select>
        <MultiPick legend="Estados contabilizados (ahí cargos = abonos)" options={ctx.states.map((s) => ({ value: s.name, label: s.label }))} value={ledger.states ?? []}
          onChange={(states) => set({ states })} empty="El catálogo no tiene proceso: defínelo en la pestaña Proceso." />
      </div>
      <fieldset className="subgroup">
        <legend className="field-label">Periodos contables (opcional)</legend>
        {!period ? (
          <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => setPeriod({})}>Exigir periodo abierto</Button></div>
        ) : (
          <div className="item-grid">
            <Select label="Catálogo de periodos" value={period.entity ?? ''} onChange={(e) => setPeriod({ entity: e.target.value, start: '', end: '' })}>
              <option value="">Elige…</option>
              {entityOptions(ctx.entities.filter((e) => !e.isArchived && !e.isChild))}
            </Select>
            <span />
            <Select label="Inicio" value={period.start ?? ''} onChange={(e) => setPeriod({ start: e.target.value })}>{fieldOptions(periodDates, 'Elige…')}</Select>
            <Select label="Fin" value={period.end ?? ''} onChange={(e) => setPeriod({ end: e.target.value })}>{fieldOptions(periodDates, 'Elige…')}</Select>
            <ExprInput condition className="span-all" label="Condición de periodo abierto" required value={period.open ?? ''} onChange={(v) => setPeriod({ open: v })}
              scope={scopeOf(ctx, period.entity)} entities={ctx.entities} noCheck={!periodEntity} hint="Se evalúa en el periodo, p. ej. Estado = 'Abierto'." />
            <div className="span-all"><Button size="sm" variant="ghost" icon={Trash2} onClick={() => set({ period: undefined })}>No exigir periodo</Button></div>
          </div>
        )}
      </fieldset>
    </div>
  );
}
