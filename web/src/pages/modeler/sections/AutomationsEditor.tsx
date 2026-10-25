import { Zap } from 'lucide-react';
import type { Automation, AutomationEvent } from '../../../api/types';
import { ExprInput } from '../../../components/ExprInput';
import { Badge, Input, Select } from '../../../components/ui';
import { ACTION_LABELS, ActionsEditor } from './ActionsEditor';
import { ItemList, selfScope, uniqueName, type SectionCtx } from './common';

const EVENTS: Record<AutomationEvent, string> = {
  create: 'Al crear un registro', update: 'Al editar un registro', enterState: 'Al entrar a un estado', transition: 'Al ejecutar una transición',
};

function whenText(a: Automation, ctx: SectionCtx) {
  if (a.on === 'enterState') return `Al entrar a ${ctx.states.find((s) => s.name === a.state)?.label ?? a.state ?? '…'}`;
  if (a.on === 'transition') return `Al ejecutar ${ctx.transitions.find((t) => t.name === a.transition)?.label ?? a.transition ?? '…'}`;
  return EVENTS[a.on] ?? a.on;
}

/** Spec 026 (H4 CA2): «Cuando → si → hacer», con modo síncrono o asíncrono (016). */
export function AutomationsEditor({ automations, onChange, ctx }: { automations: Automation[]; onChange: (a: Automation[]) => void; ctx: SectionCtx }) {
  const hasWorkflow = ctx.states.length > 0;
  return (
    <ItemList<Automation>
      items={automations}
      onChange={onChange}
      kind="automation"
      ctx={ctx}
      icon={Zap}
      noun="automatización"
      emptyTitle="Este catálogo no tiene automatizaciones"
      emptyDescription="Una automatización hace algo en otros registros cuando pasa algo en este: asignar, transicionar, crear, acumular o valuar."
      addLabel="Agregar automatización"
      create={(items) => {
        const name = uniqueName('Automatizacion', items.map((a) => a.name));
        return { name, label: 'Nueva automatización', on: 'create', actions: [] };
      }}
      name={(a) => a.name}
      title={(a) => a.label}
      summary={(a) => (
        <>
          <span>{whenText(a, ctx)}</span>
          <span aria-hidden>→</span>
          <span>{(a.actions ?? []).map((x) => ACTION_LABELS[x.type] ?? x.type).join(', ') || 'sin acciones'}</span>
          {a.mode === 'async' && <Badge>Asíncrona</Badge>}
        </>
      )}
      render={(a, patch) => (
        <div className="item-grid">
          <Input label="Nombre visible" required value={a.label ?? ''} maxLength={100} onChange={(e) => patch({ label: e.target.value })} />
          <Input label="Identificador" required className="mono" value={a.name ?? ''} maxLength={60} onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
          <fieldset className="subgroup span-all">
            <legend className="field-label">Cuando</legend>
            <div className="item-grid">
              <Select label="Evento" value={a.on} onChange={(e) => patch({ on: e.target.value as AutomationEvent, state: undefined, transition: undefined })}>
                {(Object.keys(EVENTS) as AutomationEvent[]).map((k) => (
                  <option key={k} value={k} disabled={(k === 'enterState' || k === 'transition') && !hasWorkflow}>{EVENTS[k]}{(k === 'enterState' || k === 'transition') && !hasWorkflow ? ' (requiere proceso)' : ''}</option>
                ))}
              </Select>
              {a.on === 'enterState' && (
                <Select label="Estado" value={a.state ?? ''} onChange={(e) => patch({ state: e.target.value || undefined })}>
                  <option value="">Elige un estado…</option>
                  {ctx.states.map((s) => <option key={s.name} value={s.name}>{s.label}</option>)}
                </Select>
              )}
              {a.on === 'transition' && (
                <Select label="Transición" value={a.transition ?? ''} onChange={(e) => patch({ transition: e.target.value || undefined })}>
                  <option value="">Elige una transición…</option>
                  {ctx.transitions.map((t) => <option key={t.name} value={t.name}>{t.label}</option>)}
                </Select>
              )}
              <Select label="Modo" value={a.mode ?? 'sync'} onChange={(e) => patch({ mode: e.target.value === 'async' ? 'async' : undefined })}
                hint={a.mode === 'async' ? 'Corre después, desde la bandeja, con reintentos; no detiene el guardado.' : 'Corre en la misma transacción: si falla, no se guarda nada.'}>
                <option value="sync">Síncrono (en la misma transacción)</option>
                <option value="async">Asíncrono (después)</option>
              </Select>
            </div>
          </fieldset>
          <ExprInput condition className="span-all" label="Si (condición, opcional)" value={a.when ?? ''} onChange={(v) => patch({ when: v })} scope={selfScope(ctx)} entities={ctx.entities} hint="Vacío: siempre." />
          <ActionsEditor actions={a.actions ?? []} onChange={(actions) => patch({ actions })} ctx={ctx} />
        </div>
      )}
    />
  );
}
