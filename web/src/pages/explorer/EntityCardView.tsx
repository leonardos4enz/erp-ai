import type { ReactNode } from 'react';
import { Database, IdCard, SearchCode } from 'lucide-react';
import { ApiError } from '../../api/client';
import { explorerApi, type EntityCard, type RelGraph } from '../../api/explorer';
import type { AutomationAction } from '../../api/types';
import { WorkflowDiagram } from '../../components/WorkflowDiagram';
import { Alert, Badge, Button, EmptyState, LoadingBlock, Select } from '../../components/ui';
import { useAsync } from '../../lib/hooks';
import { EntityLinks, typeLabel, type ExplorerView } from './common';

const EVENTS: Record<string, string> = { create: 'Al crear', update: 'Al editar', enterState: 'Al entrar a', transition: 'Al ejecutar' };
const ACTIONS: Record<string, string> = { update: 'Actualiza', transition: 'Transiciona', create: 'Crea', rollup: 'Acumula', cost: 'Valúa' };
const SCOPES: Record<string, string> = { all: 'todos', own: 'propios', unit: 'de su unidad', unitTree: 'de su unidad y descendientes' };
const FREQ: Record<string, string> = { minutes: 'cada N minutos', hour: 'cada hora', day: 'diaria', week: 'semanal', month: 'mensual' };

const actionText = (a: AutomationAction) =>
  `${ACTIONS[a.type] ?? a.type} ${a.entity ?? a.target ?? 'el propio registro'}${a.lines ? ` (por cada partida de ${a.lines})` : ''}${a.set?.length ? `: ${a.set.map((s) => s.field).join(', ')}` : ''}${a.transition ? ` → ${a.transition}` : ''}${a.sum ? `: suma ${a.sum} en ${a.field}` : ''}`;

function Section({ title, count, children }: { title: string; count?: number; children: ReactNode }) {
  return (
    <section className="card card-pad stack ex-section" aria-labelledby={`ex-s-${title}`}>
      <h3 className="ex-h3" id={`ex-s-${title}`}>{title}{count !== undefined && <span className="subtle"> ({count})</span>}</h3>
      {children}
    </section>
  );
}
const None = ({ children = 'Ninguno.' }: { children?: ReactNode }) => <p className="subtle">{children}</p>;

interface Props { entity?: string; onEntity: (e?: string) => void; onOpen: (v: ExplorerView, e?: string) => void }

export function EntityCardView({ entity, onEntity, onOpen }: Props) {
  const list = useAsync<RelGraph>((s) => explorerApi.relations({}, s), []);
  const card = useAsync<EntityCard | null>((s) => (entity ? explorerApi.entity(entity, s) : Promise.resolve(null)), [entity]);

  return (
    <div className="stack">
      <div className="ex-filters">
        <Select label="Catálogo" selectSize="sm" value={entity ?? ''} onChange={(e) => onEntity(e.target.value || undefined)}>
          <option value="">Elige un catálogo…</option>
          {list.data?.apps.map((a) => {
            const nodes = list.data!.nodes.filter((n) => n.app === a.name);
            return nodes.length ? (
              <optgroup key={a.name} label={a.label}>
                {nodes.map((n) => <option key={n.name} value={n.name}>{n.labelPlural}{n.isChild ? ' (partida)' : ''}</option>)}
              </optgroup>
            ) : null;
          })}
        </Select>
      </div>
      {!entity && <div className="card"><EmptyState icon={IdCard} title="Elige un catálogo" description="La ficha reúne sus campos, reglas, proceso, automatizaciones, seguridad, interfaz, paquete y tabla física." /></div>}
      {entity && card.loading && <LoadingBlock />}
      {entity && !!card.error && <Alert tone="danger" title={card.error instanceof ApiError ? card.error.title : 'No se pudo leer el catálogo'} />}
      {card.data && <Card c={card.data} onOpen={onOpen} />}
    </div>
  );
}

function Card({ c, onOpen }: { c: EntityCard; onOpen: (v: ExplorerView, e?: string) => void }) {
  const e = c.entity;
  const fields = e.fields.filter((f) => !f.isArchived);
  const archived = e.fields.filter((f) => f.isArchived);
  const isChild = fields.some((f) => f.type === 'parent');
  const label = (n: string) => e.fields.find((f) => f.name.toLowerCase() === n.toLowerCase())?.label ?? n;
  const created = c.packages.find((p) => p.created);
  return (
    <div className="stack">
      <section className="card card-pad stack" aria-label="Resumen del catálogo">
        <div className="row ex-wrap">
          <h2 className="ex-h2 grow">{e.labelPlural} <span className="mono subtle">{e.name}</span></h2>
          {e.isArchived && <Badge tone="warning">Archivado</Badge>}
          {isChild && <Badge>Partida</Badge>}
        </div>
        {e.description && <p>{e.description}</p>}
        <dl className="ex-dl">
          <dt>Módulo</dt><dd>{c.app.label}</dd>
          <dt>Tabla física</dt><dd className="mono">{c.table}{!c.tableExists && <> <Badge tone="danger">no existe</Badge></>}</dd>
          <dt>Empresa</dt><dd>{(e as { perCompany?: boolean }).perCompany ? 'Un registro por empresa (columna CompanyId)' : 'Compartido entre empresas'}</dd>
          <dt>Instalado por</dt>
          <dd>{created ? `${created.name}${created.installedVersion ? ` ${created.installedVersion}` : ' (no instalado aquí)'}` : 'Creado en el modelador'}</dd>
          {c.packages.filter((p) => !p.created).length > 0 && <><dt>Lo modifican</dt><dd>{c.packages.filter((p) => !p.created).map((p) => `${p.name} (${p.versions.join(', ')})`).join('; ')}</dd></>}
          {created && <><dt>Versiones que lo tocan</dt><dd>{created.versions.map((v) => (v === 'base' ? 'plan base' : v)).join(', ')}</dd></>}
        </dl>
        <div className="ex-links">
          <Button size="sm" icon={Database} onClick={() => onOpen('bd', e.name)}>Ver en la base de datos</Button>
          <Button size="sm" icon={SearchCode} onClick={() => onOpen('usos', e.name)}>¿Dónde se usa?</Button>
        </div>
        <EntityLinks entity={e.name} label={e.labelPlural} isChild={isChild} card={false} />
      </section>

      <Section title="Campos" count={fields.length}>
        <div className="table-scroll" tabIndex={0}>
          <table className="table table-compact">
            <caption className="sr-only">Campos de {e.labelPlural}</caption>
            <thead><tr><th scope="col">Campo</th><th scope="col">Tipo</th><th scope="col">Reglas</th><th scope="col">Valor por defecto o fórmula</th></tr></thead>
            <tbody>
              {fields.map((f) => (
                <tr key={f.name}>
                  <td><strong>{f.label}</strong> <span className="mono subtle">{f.name}</span></td>
                  <td>{typeLabel(f.type, (f as { resultType?: string }).resultType)}{f.referenceEntity ? ` → ${f.referenceEntity}` : ''}{(f as { childEntity?: string }).childEntity ? ` → ${(f as { childEntity?: string }).childEntity}` : ''}</td>
                  <td className="ex-badges">
                    {f.required && <Badge tone="accent">Obligatorio</Badge>}
                    {f.unique && <Badge tone="accent">Único</Badge>}
                    {f.editable === false && <Badge>Lo asigna el sistema</Badge>}
                  </td>
                  <td className="mono subtle ex-expr">{(f as { formula?: string }).formula ?? f.defaultValue ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {archived.length > 0 && <p className="subtle">Archivados (su columna se conserva): {archived.map((f) => f.name).join(', ')}</p>}
      </Section>

      <Section title="Reglas" count={e.rules?.length ?? 0}>
        {e.rules?.length ? (
          <ul className="ex-list">
            {e.rules.map((r) => (
              <li key={r.name}>
                <Badge tone={r.severity === 'warning' ? 'warning' : 'danger'}>{r.severity === 'warning' ? 'Aviso' : 'Error'}</Badge> <strong>{r.name}</strong>: {r.message}
                <div className="mono subtle ex-expr">{r.assert}{r.when ? `  · cuando ${r.when}` : ''}</div>
              </li>
            ))}
          </ul>
        ) : <None>Sin reglas.</None>}
      </Section>

      <Section title="Proceso">
        {e.workflow ? (
          <>
            <WorkflowDiagram workflow={e.workflow} label={`Proceso de ${e.labelPlural}`} />
            <div className="table-scroll" tabIndex={0}>
              <table className="table table-compact">
                <caption className="sr-only">Transiciones del proceso</caption>
                <thead><tr><th scope="col">Transición</th><th scope="col">De</th><th scope="col">A</th><th scope="col">Roles</th><th scope="col">Condición</th><th scope="col">Si hay avisos</th></tr></thead>
                <tbody>
                  {e.workflow.transitions.map((t) => (
                    <tr key={t.name}>
                      <td>{t.label}{t.system && <> <Badge>solo automatización</Badge></>}</td>
                      <td>{t.from.join(', ')}</td>
                      <td>{t.to}</td>
                      <td>{t.roles?.join(', ') || 'Todos'}</td>
                      <td className="mono subtle ex-expr">{t.when ?? '—'}</td>
                      <td>{({ confirm: 'Confirmar', block: 'Bloquear', allow: 'Permitir' } as Record<string, string>)[(t as { onWarning?: string }).onWarning ?? 'confirm']}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        ) : <None>Sin proceso (sin estados).</None>}
      </Section>

      <Section title="Automatizaciones" count={e.automations?.length ?? 0}>
        {e.automations?.length ? (
          <ul className="ex-list">
            {e.automations.map((a) => (
              <li key={a.name}>
                <strong>{a.label}</strong> <Badge tone={a.mode === 'async' ? 'accent' : 'neutral'}>{a.mode === 'async' ? 'Asíncrona' : 'Síncrona'}</Badge>
                <div className="subtle">{EVENTS[a.on] ?? a.on}{a.state ? ` ${a.state}` : ''}{a.transition ? ` ${a.transition}` : ''}{a.when ? ` · cuando ${a.when}` : ''}</div>
                <ul className="ex-sublist">{a.actions.map((x, i) => <li key={i}>{actionText(x)}</li>)}</ul>
              </li>
            ))}
          </ul>
        ) : <None>Sin automatizaciones.</None>}
      </Section>

      <Section title="Tareas programadas" count={e.schedules?.length ?? 0}>
        {e.schedules?.length ? (
          <ul className="ex-list">
            {e.schedules.map((s) => (
              <li key={s.name}><strong>{s.label}</strong> <Badge>{FREQ[s.every] ?? s.every}{s.at ? ` ${s.at}` : ''}</Badge> con el rol {s.role}
                <div className="mono subtle ex-expr">{s.where}</div>
                <ul className="ex-sublist">{s.actions.map((x, i) => <li key={i}>{actionText(x)}</li>)}</ul>
              </li>
            ))}
          </ul>
        ) : <None>Sin tareas programadas.</None>}
      </Section>

      <Section title="Webhooks" count={e.webhooks?.length ?? 0}>
        {e.webhooks?.length ? (
          <ul className="ex-list">
            {e.webhooks.map((h) => <li key={h.name}><strong>{h.label}</strong> · {h.events.map((v) => `${EVENTS[v.on] ?? v.on}${v.state ? ` ${v.state}` : ''}${v.transition ? ` ${v.transition}` : ''}`).join(', ')} · rol {h.role}{h.fields?.length ? ` · campos ${h.fields.join(', ')}` : ''}</li>)}
          </ul>
        ) : <None>Sin webhooks.</None>}
      </Section>

      <Section title="Contabilidad">
        {e.ledger ? (
          <p>Libro de partida doble: partidas <span className="mono">{e.ledger.lines}</span>, cargo <span className="mono">{e.ledger.debit}</span>, abono <span className="mono">{e.ledger.credit}</span>
            {e.ledger.date && <>, fecha <span className="mono">{e.ledger.date}</span></>}
            {e.ledger.states?.length ? `; contabilizado en ${e.ledger.states.join(', ')}` : '; contabilizado en todos los estados'}
            {e.ledger.period && <>; periodos en <span className="mono">{e.ledger.period.entity}</span> (abierto si <span className="mono">{e.ledger.period.open}</span>)</>}.</p>
        ) : <None>No es un libro contable.</None>}
      </Section>

      <Section title="Plantillas de impresión" count={e.printTemplates?.length ?? 0}>
        {e.printTemplates?.length ? (
          <ul className="ex-list">
            {e.printTemplates.map((t) => (
              <li key={t.name}><strong>{t.label}</strong>{t.default && <> <Badge tone="success">Predeterminada</Badge></>} · {t.blocks.length} bloque(s)
                {t.roles?.length ? ` · roles ${t.roles.join(', ')}` : ''}
                {t.when && <div className="mono subtle ex-expr">{t.when}</div>}</li>
            ))}
          </ul>
        ) : <None>Sin plantillas de impresión.</None>}
      </Section>

      <Section title="Formulario (layout)">
        {e.layout?.sections.length ? (
          <ul className="ex-list">
            {e.layout.sections.map((s) => (
              <li key={s.name}><strong>{s.label}</strong>{s.tab && <span className="subtle"> · pestaña {s.tab}</span>}{s.visibleWhen && <span className="mono subtle"> · visible si {s.visibleWhen}</span>}
                <div className="subtle">{s.fields.map((i) => label(typeof i === 'string' ? i : i.field)).join(', ') || 'Sin campos'}</div></li>
            ))}
          </ul>
        ) : <None>Sin layout: el formulario sigue el orden de los campos.</None>}
      </Section>

      <Section title="Vistas, reportes y tableros">
        <p><strong>Vistas:</strong> {e.views?.map((v) => v.label).join(', ') || 'ninguna'}</p>
        <p><strong>Reportes:</strong> {e.reports?.map((r) => r.label).join(', ') || 'ninguno'}</p>
        <p><strong>Tableros que lo usan:</strong> {c.dashboards.map((d) => d.label).join(', ') || 'ninguno'}</p>
      </Section>

      <Section title="Seguridad por rol" count={c.roles.length}>
        {e.security?.systemManaged && <Alert tone="info" title="Solo lo modifican las automatizaciones: las personas solo consultan." />}
        <div className="table-scroll" tabIndex={0}>
          <table className="table table-compact">
            <caption className="sr-only">Permisos de cada rol en {e.labelPlural}</caption>
            <thead><tr><th scope="col">Rol</th><th scope="col">Leer</th><th scope="col">Crear</th><th scope="col">Editar</th><th scope="col">Eliminar</th><th scope="col">Alcance</th><th scope="col">Campos ocultos</th><th scope="col">Solo lectura</th></tr></thead>
            <tbody>
              {c.roles.map((r) => (
                <tr key={r.role}>
                  <td>{r.role}{r.inheritedFrom.length > 0 && <div className="subtle">hereda de {r.inheritedFrom.join(', ')}</div>}</td>
                  {[r.read, r.create, r.update, r.delete].map((v, i) => <td key={i}>{v ? 'Sí' : <span className="subtle">No</span>}</td>)}
                  <td>{r.scopes.map((s) => `${s.access === 'read' ? 'lee' : 'escribe'} ${SCOPES[s.scope] ?? s.scope}${s.where ? ` donde ${s.where}` : ''}`).join('; ') || (r.read ? 'todos' : '—')}</td>
                  <td>{r.hidden.map(label).join(', ') || '—'}</td>
                  <td>{r.readOnly.map(label).join(', ') || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      <Section title="Lo referencian" count={c.referencedBy.length}>
        {c.referencedBy.length ? (
          <ul className="ex-list">{c.referencedBy.map((r) => <li key={`${r.from}.${r.field}`}><span className="mono">{r.from}.{r.field}</span> <span className="subtle">({r.kind === 'lines' ? 'partidas' : r.kind === 'parent' ? 'documento' : 'referencia'})</span></li>)}</ul>
        ) : <None>Nadie lo referencia.</None>}
      </Section>
    </div>
  );
}
