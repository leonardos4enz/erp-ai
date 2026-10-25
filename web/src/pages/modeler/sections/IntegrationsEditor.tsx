import { CalendarClock, Plus, Trash2, Webhook } from 'lucide-react';
import type { ScheduleDef, WebhookDef, WebhookEvent } from '../../../api/types';
import { ExprInput } from '../../../components/ExprInput';
import { Badge, Button, Input, Select } from '../../../components/ui';
import { ActionsEditor } from './ActionsEditor';
import { clean, fieldPickOptions, ItemList, MultiPick, selfScope, uniqueName, type SectionCtx } from './common';

const EVENTS: Record<WebhookEvent['on'], string> = { create: 'Al crear', update: 'Al editar', enterState: 'Al entrar a un estado', transition: 'Al ejecutar una transición' };

/** Spec 026 (H4 CA6): webhooks (008). El destino y el secreto no son metadatos: se configuran en Integraciones. */
export function WebhooksEditor({ webhooks, onChange, ctx }: { webhooks: WebhookDef[]; onChange: (w: WebhookDef[]) => void; ctx: SectionCtx }) {
  return (
    <ItemList<WebhookDef>
      items={webhooks} onChange={onChange} kind="webhook" ctx={ctx} icon={Webhook} noun="webhook"
      emptyTitle="Este catálogo no tiene webhooks"
      emptyDescription="Un webhook avisa a otro sistema cuando pasa algo aquí. El destino y el secreto se configuran después en Integraciones."
      addLabel="Agregar webhook"
      create={(items) => ({ name: uniqueName('Webhook', items.map((w) => w.name)), label: 'Nuevo webhook', events: [{ on: 'create' }], role: '' })}
      name={(w) => w.name} title={(w) => w.label}
      summary={(w) => <span>{(w.events ?? []).map((e) => EVENTS[e.on]).join(', ')} · rol {w.role || '—'}</span>}
      render={(w, patch) => {
        const events = w.events ?? [];
        const setEvent = (i: number, p: Partial<WebhookEvent>) => patch({ events: events.map((e, k) => (k === i ? clean({ ...e, ...p }) as WebhookEvent : e)) });
        return (
          <div className="item-grid">
            <Input label="Nombre visible" required value={w.label ?? ''} maxLength={100} onChange={(e) => patch({ label: e.target.value })} />
            <Input label="Identificador" required className="mono" value={w.name ?? ''} maxLength={60} onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
            <Input wrapClassName="span-all" label="Descripción (opcional)" value={w.description ?? ''} maxLength={300} onChange={(e) => patch({ description: e.target.value })} />
            <fieldset className="subgroup span-all">
              <legend className="field-label">Eventos</legend>
              {events.map((ev, i) => (
                <div className="assign-row" key={i}>
                  <Select aria-label={`Evento ${i + 1}`} value={ev.on} onChange={(e) => setEvent(i, { on: e.target.value as WebhookEvent['on'], state: undefined, transition: undefined })}>
                    {(Object.keys(EVENTS) as WebhookEvent['on'][]).map((k) => <option key={k} value={k} disabled={(k === 'enterState' || k === 'transition') && !ctx.states.length}>{EVENTS[k]}</option>)}
                  </Select>
                  {ev.on === 'enterState' ? (
                    <Select aria-label={`Evento ${i + 1}: estado`} value={ev.state ?? ''} onChange={(e) => setEvent(i, { state: e.target.value || undefined })}>
                      <option value="">Estado…</option>{ctx.states.map((s) => <option key={s.name} value={s.name}>{s.label}</option>)}
                    </Select>
                  ) : ev.on === 'transition' ? (
                    <Select aria-label={`Evento ${i + 1}: transición`} value={ev.transition ?? ''} onChange={(e) => setEvent(i, { transition: e.target.value || undefined })}>
                      <option value="">Transición…</option>{ctx.transitions.map((t) => <option key={t.name} value={t.name}>{t.label}</option>)}
                    </Select>
                  ) : <span />}
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar evento ${i + 1}`} disabled={events.length === 1} onClick={() => patch({ events: events.filter((_, k) => k !== i) })} />
                </div>
              ))}
              <div><Button size="sm" variant="ghost" icon={Plus} onClick={() => patch({ events: [...events, { on: 'update' }] })}>Agregar evento</Button></div>
            </fieldset>
            <Select label="Rol de servicio" required value={w.role ?? ''} onChange={(e) => patch({ role: e.target.value })}
              hint="Se envía solo lo que este rol puede ver (seguridad por campo de la 006).">
              <option value="">Elige un rol…</option>
              {(ctx.roleNames ?? []).map((r) => <option key={r} value={r}>{r}</option>)}
              {w.role && !(ctx.roleNames ?? []).includes(w.role) && <option value={w.role}>{w.role} (no existe)</option>}
            </Select>
            <ExprInput condition className="span-all" label="Solo si (opcional)" value={w.when ?? ''} onChange={(v) => patch({ when: v })} scope={selfScope(ctx)} entities={ctx.entities} />
            <MultiPick legend="Campos que se envían" options={fieldPickOptions(ctx.fields.filter((f) => f.type !== 'lines'))} value={w.fields ?? []} onChange={(fields) => patch({ fields })}
              hint="Ninguno: todos los que el rol ve." />
          </div>
        );
      }}
    />
  );
}

const EVERY: Record<ScheduleDef['every'], string> = { minutes: 'Cada N minutos', hour: 'Cada hora', day: 'Diaria', week: 'Semanal', month: 'Mensual' };
const WEEKDAYS = ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo'].map((l, i) => ({ value: String(i + 1), label: l }));

function calendar(s: ScheduleDef) {
  switch (s.every) {
    case 'minutes': return `Cada ${s.interval ?? '?'} min`;
    case 'hour': return `Cada hora al minuto ${s.minute ?? 0}`;
    case 'week': return `Semanal a las ${s.at ?? '?'}`;
    case 'month': return `Día ${s.day ?? '?'} a las ${s.at ?? '?'}`;
    default: return `Diaria a las ${s.at ?? '?'}`;
  }
}

/** Spec 026 (H4 CA7): tareas programadas (016). Llegan en pausa: se activan en Configuración › Automatizaciones. */
export function SchedulesEditor({ schedules, onChange, ctx }: { schedules: ScheduleDef[]; onChange: (s: ScheduleDef[]) => void; ctx: SectionCtx }) {
  return (
    <ItemList<ScheduleDef>
      items={schedules} onChange={onChange} kind="schedule" ctx={ctx} icon={CalendarClock} noun="tarea"
      emptyTitle="Este catálogo no tiene tareas programadas"
      emptyDescription="Una tarea revisa registros con un calendario (vencimientos, recordatorios, cierres) y les aplica acciones. Llega en pausa."
      addLabel="Agregar tarea"
      create={(items) => ({ name: uniqueName('Tarea', items.map((s) => s.name)), label: 'Nueva tarea', every: 'day', at: '07:00', where: '', role: '', actions: [] })}
      name={(s) => s.name} title={(s) => s.label}
      summary={(s) => <><Badge>{calendar(s)}</Badge><code className="mono subtle">{s.where || 'sin consulta'}</code></>}
      render={(s, patch) => (
        <div className="item-grid">
          <Input label="Nombre visible" required value={s.label ?? ''} maxLength={100} onChange={(e) => patch({ label: e.target.value })} />
          <Input label="Identificador" required className="mono" value={s.name ?? ''} maxLength={60} onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
          <fieldset className="subgroup span-all">
            <legend className="field-label">Calendario</legend>
            <div className="item-grid">
              <Select label="Frecuencia" value={s.every} onChange={(e) => {
                const every = e.target.value as ScheduleDef['every'];
                patch({ every, interval: every === 'minutes' ? s.interval ?? 15 : undefined, minute: every === 'hour' ? s.minute ?? 0 : undefined,
                  at: every === 'minutes' || every === 'hour' ? undefined : s.at ?? '07:00', weekdays: every === 'week' ? s.weekdays ?? [1] : undefined, day: every === 'month' ? s.day ?? 1 : undefined });
              }}>
                {(Object.keys(EVERY) as ScheduleDef['every'][]).map((k) => <option key={k} value={k}>{EVERY[k]}</option>)}
              </Select>
              {s.every === 'minutes' && <Input label="Cada cuántos minutos" type="number" min={5} max={720} value={s.interval ?? ''} onChange={(e) => patch({ interval: e.target.value ? Number(e.target.value) : undefined })} />}
              {s.every === 'hour' && <Input label="Minuto de cada hora" type="number" min={0} max={59} value={s.minute ?? 0} onChange={(e) => patch({ minute: Number(e.target.value) })} />}
              {s.every !== 'minutes' && s.every !== 'hour' && <Input label="Hora" type="time" value={s.at ?? ''} onChange={(e) => patch({ at: e.target.value })} />}
              {s.every === 'month' && <Input label="Día del mes" type="number" min={1} max={28} value={s.day ?? 1} onChange={(e) => patch({ day: Number(e.target.value) })} />}
              {s.every === 'week' && (
                <MultiPick legend="Días" options={WEEKDAYS} value={(s.weekdays ?? []).map(String)} onChange={(v) => patch({ weekdays: v.map(Number) })} />
              )}
            </div>
          </fieldset>
          <ExprInput condition className="span-all" label="Qué registros (consulta)" required value={s.where ?? ''} onChange={(v) => patch({ where: v })} scope={selfScope(ctx)} entities={ctx.entities}
            hint="Debe poder traducirse a SQL, p. ej. Estado = 'Abierto' AND Vence < TODAY()." />
          <Select label="Rol de servicio" required value={s.role ?? ''} onChange={(e) => patch({ role: e.target.value })} hint="La tarea solo ve los registros que este rol ve.">
            <option value="">Elige un rol…</option>
            {(ctx.roleNames ?? []).map((r) => <option key={r} value={r}>{r}</option>)}
            {s.role && !(ctx.roleNames ?? []).includes(s.role) && <option value={s.role}>{s.role} (no existe)</option>}
          </Select>
          <Input label="Registros por corrida (opcional)" type="number" min={1} max={1000} value={s.batchSize ?? ''} onChange={(e) => patch({ batchSize: e.target.value ? Number(e.target.value) : undefined })} />
          <ActionsEditor actions={s.actions ?? []} onChange={(actions) => patch({ actions })} ctx={ctx} schedule />
        </div>
      )}
    />
  );
}
