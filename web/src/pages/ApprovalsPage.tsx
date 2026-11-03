import { useState } from 'react';
import { ArrowDown, ArrowUp, ListOrdered, Pencil, Plus, ShieldCheck, Trash2, Users } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { approvalsApi, usersApi, type ApprovalProfileInput, type ApprovalRuleInput, type ApprovalSaveResult } from '../api/endpoints';
import type { ApprovalConfig, ApprovalEntity, ApprovalProfile, ApprovalRule } from '../api/types';
import { Dialog } from '../components/Dialog';
import { MultiReferenceSelect } from '../components/MultiReferenceSelect';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, Field, Input, LoadingBlock, PageHeader, Select, Switch, Tabs } from '../components/ui';
import { useAsync } from '../lib/hooks';

type Tab = 'profiles' | 'rules';

const money = (n: number | null) => (n == null ? null : n.toLocaleString('es-MX', { maximumFractionDigits: 2 }));
const range = (min: number | null, max: number | null) =>
  min == null && max == null ? 'Cualquier monto' : min != null && max != null ? `${money(min)} a ${money(max)}` : min != null ? `Desde ${money(min)}` : `Hasta ${money(max)}`;
const num = (v: string) => (v.trim() === '' ? null : Number(v));

/**
 * Spec 053 (H2): la matriz de autorización de la empresa actual. Perfiles (personas y rango de montos) y reglas por catálogo
 * (condiciones, prioridad, vigencia y niveles). Un cambio puede quedar como solicitud para otro administrador (015).
 */
export function ApprovalsPage() {
  const [tab, setTab] = useState<Tab>('profiles');
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync((s) => approvalsApi.config(s), [tick]);
  const reload = () => setTick((t) => t + 1);
  return (
    <div className="page">
      <PageHeader
        icon={ShieldCheck}
        title="Autorizaciones"
        subtitle={<>Quién autoriza cada documento{data ? <> en <strong>{data.company}</strong></> : null}: perfiles con sus personas y reglas con sus niveles. Cambiarlos no altera los documentos que ya están en autorización.</>}
      />
      <Tabs label="Matriz de autorización" value={tab} onChange={setTab}
        items={[{ id: 'profiles', label: 'Perfiles', icon: Users }, { id: 'rules', label: 'Reglas', icon: ListOrdered }]} />
      <div style={{ marginTop: 'var(--space-5)' }}>
        {loading && !data ? <LoadingBlock /> : error || !data ? (
          <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar la matriz'} />
        ) : tab === 'profiles' ? <ProfilesTab config={data} onSaved={reload} /> : <RulesTab config={data} onSaved={reload} />}
      </div>
    </div>
  );
}

function useSaved(onSaved: () => void) {
  const toast = useToast();
  return (r: ApprovalSaveResult, what: string) => {
    if ('securityChange' in r) toast.info('Cambio enviado a aprobación', `${r.securityChange.summary}: lo aplica otro administrador.`);
    else toast.success(what);
    onSaved();
  };
}

/* ----------------------------- Perfiles ----------------------------- */

function ProfilesTab({ config, onSaved }: { config: ApprovalConfig; onSaved: () => void }) {
  const [editing, setEditing] = useState<ApprovalProfile | 'new' | null>(null);
  return (
    <div className="stack">
      <div className="row" style={{ flexWrap: 'wrap' }}>
        <p className="muted grow" style={{ margin: 0 }}>Un perfil agrupa a las personas que autorizan un nivel. Con rango, el nivel se salta si el monto no cae en él.</p>
        <Button variant="primary" icon={Plus} onClick={() => setEditing('new')}>Nuevo perfil</Button>
      </div>
      {config.profiles.length === 0 ? (
        <EmptyState icon={Users} title="Sin perfiles" description="Da de alta el primer perfil (p. ej. «Jefes de área») y agrega a sus personas." />
      ) : (
        <div className="table-wrap"><div className="table-scroll">
          <table className="table">
            <thead><tr><th>Perfil</th><th>Montos</th><th>Personas</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>
              {config.profiles.map((p) => (
                <tr key={p.id}>
                  <td style={{ fontWeight: 500 }}>{p.name}</td>
                  <td>{range(p.minAmount, p.maxAmount)}</td>
                  <td>{p.members.length === 0 ? <span className="subtle">Nadie (el nivel se salta)</span> : p.members.map((m) => m.label).join(', ')}</td>
                  <td><Badge tone={p.isActive ? 'success' : 'neutral'}>{p.isActive ? 'Activo' : 'Inactivo'}</Badge></td>
                  <td style={{ textAlign: 'right' }}>
                    <Button size="sm" variant="ghost" icon={Pencil} onClick={() => setEditing(p)} aria-label={`Editar el perfil ${p.name}`}>Editar</Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div></div>
      )}
      {editing && <ProfileDialog profile={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onSaved(); }} />}
    </div>
  );
}

function ProfileDialog({ profile, onClose, onSaved }: { profile: ApprovalProfile | null; onClose: () => void; onSaved: () => void }) {
  const saved = useSaved(onSaved);
  const [name, setName] = useState(profile?.name ?? '');
  const [min, setMin] = useState(profile?.minAmount?.toString() ?? '');
  const [max, setMax] = useState(profile?.maxAmount?.toString() ?? '');
  const [active, setActive] = useState(profile?.isActive ?? true);
  const [members, setMembers] = useState<number[]>(profile?.members.map((m) => m.id) ?? []);
  const [labels, setLabels] = useState<(string | null)[]>(profile?.members.map((m) => m.label) ?? []);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<ApiError | null>(null);

  async function save() {
    setBusy(true);
    setErr(null);
    try {
      const input: ApprovalProfileInput = { name, minAmount: num(min), maxAmount: num(max), isActive: active, memberIds: members, version: profile?.version };
      saved(await approvalsApi.saveProfile(profile?.id ?? null, input), profile ? 'Perfil actualizado' : 'Perfil creado');
    } catch (e) {
      setErr(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setBusy(false);
    }
  }

  const general = err && Object.keys(err.fieldErrors).length === 0 ? err : null;
  return (
    <Dialog open onClose={() => { if (!busy) onClose(); }} title={profile ? `Perfil ${profile.name}` : 'Nuevo perfil'}
      description="Nadie se agrega ni se quita a sí mismo: eso lo hace otro administrador."
      footer={<><Button onClick={onClose} disabled={busy}>Cancelar</Button><Button variant="primary" loading={busy} onClick={save}>Guardar</Button></>}>
      <div className="stack">
        {general && <Alert tone="danger" title={general.title}>{general.detail}</Alert>}
        <Input label="Nombre" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} error={fieldError(err, 'name')} data-autofocus />
        <div className="row" style={{ gap: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'flex-start' }}>
          <Input label="Monto desde" hint="Opcional" type="number" min={0} step="0.01" value={min} onChange={(e) => setMin(e.target.value)} error={fieldError(err, 'minAmount')} />
          <Input label="Monto hasta" hint="Opcional" type="number" min={0} step="0.01" value={max} onChange={(e) => setMax(e.target.value)} error={fieldError(err, 'maxAmount')} />
        </div>
        <Field label="Personas" htmlFor="profile-members" error={fieldError(err, 'memberIds')}>
          <MultiReferenceSelect id="profile-members" entity="" fieldLabel="Personas" value={members} labels={labels}
            lookup={(search, signal) => usersApi.lookup(search, signal)} invalid={!!fieldError(err, 'memberIds')}
            onChange={(ids, ls) => { setMembers(ids); setLabels(ls); }} />
        </Field>
        {profile && <Switch checked={active} onChange={setActive} label="Activo" />}
      </div>
    </Dialog>
  );
}

/* ----------------------------- Reglas ----------------------------- */

function RulesTab({ config, onSaved }: { config: ApprovalConfig; onSaved: () => void }) {
  const [editing, setEditing] = useState<ApprovalRule | 'new' | null>(null);
  const profileName = (id: number) => config.profiles.find((p) => p.id === id)?.name ?? `#${id}`;
  const entityLabel = (name: string) => config.entities.find((e) => e.name === name)?.label ?? name;
  return (
    <div className="stack">
      <div className="row" style={{ flexWrap: 'wrap' }}>
        <p className="muted grow" style={{ margin: 0 }}>Gana la regla de menor prioridad que se cumple; con la misma prioridad, la que fija más condiciones.</p>
        <Button variant="primary" icon={Plus} onClick={() => setEditing('new')} disabled={config.entities.length === 0 || config.profiles.length === 0}>Nueva regla</Button>
      </div>
      {config.entities.length === 0 ? (
        <Alert tone="info" title="Ningún catálogo se autoriza por matriz">El modelador lo declara en el proceso de un catálogo por empresa.</Alert>
      ) : config.rules.length === 0 ? (
        <EmptyState icon={ListOrdered} title="Sin reglas" description="Sin una regla que aplique, un documento no puede mandarse a autorizar." />
      ) : (
        <div className="table-wrap"><div className="table-scroll">
          <table className="table">
            <thead><tr><th>Catálogo</th><th>Regla</th><th>Prioridad</th><th>Montos</th><th>Niveles</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>
              {config.rules.map((r) => (
                <tr key={r.id}>
                  <td>{entityLabel(r.entity)}</td>
                  <td style={{ fontWeight: 500 }}>{r.name}</td>
                  <td>{r.priority}</td>
                  <td>{range(r.minAmount, r.maxAmount)}</td>
                  <td>{r.levels.map(profileName).join(' → ')}</td>
                  <td><Badge tone={r.isActive ? 'success' : 'neutral'}>{r.isActive ? 'Activa' : 'Inactiva'}</Badge></td>
                  <td style={{ textAlign: 'right' }}>
                    <Button size="sm" variant="ghost" icon={Pencil} onClick={() => setEditing(r)} aria-label={`Editar la regla ${r.name}`}>Editar</Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div></div>
      )}
      {editing && <RuleDialog config={config} rule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onSaved(); }} />}
    </div>
  );
}

type CondValue = { ids: (string | number | boolean)[]; labels: (string | null)[] };

function RuleDialog({ config, rule, onClose, onSaved }: { config: ApprovalConfig; rule: ApprovalRule | null; onClose: () => void; onSaved: () => void }) {
  const saved = useSaved(onSaved);
  const [entity, setEntity] = useState(rule?.entity ?? config.entities[0]?.name ?? '');
  const [name, setName] = useState(rule?.name ?? '');
  const [priority, setPriority] = useState(String(rule?.priority ?? 1));
  const [from, setFrom] = useState(rule?.validFrom ?? '');
  const [to, setTo] = useState(rule?.validTo ?? '');
  const [min, setMin] = useState(rule?.minAmount?.toString() ?? '');
  const [max, setMax] = useState(rule?.maxAmount?.toString() ?? '');
  const [active, setActive] = useState(rule?.isActive ?? true);
  const [levels, setLevels] = useState<number[]>(rule?.levels ?? [config.profiles[0]?.id].filter((x): x is number => x != null));
  const [conds, setConds] = useState<Record<string, CondValue>>(() => Object.fromEntries(Object.entries(rule?.conditions ?? {})
    .map(([k, v]) => [k, { ids: v, labels: v.map(() => null) }])));
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<ApiError | null>(null);
  const def: ApprovalEntity | undefined = config.entities.find((e) => e.name === entity);
  const activeProfiles = config.profiles;

  function move(i: number, d: -1 | 1) {
    setLevels((ls) => { const n = [...ls]; [n[i], n[i + d]] = [n[i + d], n[i]]; return n; });
  }

  async function save() {
    setBusy(true);
    setErr(null);
    try {
      const conditions: ApprovalRuleInput['conditions'] = {};
      for (const c of def?.conditions ?? []) {
        const v = conds[c.field];
        if (v && v.ids.length > 0) conditions[c.field] = c.type === 'reference' || c.type === 'user' ? v.ids.map(Number) : v.ids;
      }
      const input: ApprovalRuleInput = {
        entity, name, priority: Number(priority), isActive: active, validFrom: from || null, validTo: to || null,
        minAmount: num(min), maxAmount: num(max), conditions, levels, version: rule?.version,
      };
      saved(await approvalsApi.saveRule(rule?.id ?? null, input), rule ? 'Regla actualizada' : 'Regla creada');
    } catch (e) {
      setErr(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setBusy(false);
    }
  }

  const general = err && Object.keys(err.fieldErrors).length === 0 ? err : null;
  const setCond = (field: string, v: CondValue) => setConds((c) => ({ ...c, [field]: v }));
  const toggle = (field: string, value: string | boolean, on: boolean) => {
    const cur = conds[field]?.ids ?? [];
    const ids = on ? [...cur, value] : cur.filter((x) => String(x) !== String(value));
    setCond(field, { ids, labels: ids.map(() => null) });
  };
  return (
    <Dialog open size="lg" onClose={() => { if (!busy) onClose(); }} title={rule ? `Regla ${rule.name}` : 'Nueva regla'}
      description="Los documentos que ya están en autorización conservan la regla con la que empezaron."
      footer={<><Button onClick={onClose} disabled={busy}>Cancelar</Button><Button variant="primary" loading={busy} onClick={save}>Guardar</Button></>}>
      <div className="stack">
        {general && <Alert tone="danger" title={general.title}>{general.detail}</Alert>}
        <Select label="Catálogo" required value={entity} disabled={!!rule} onChange={(e) => { setEntity(e.target.value); setConds({}); }} error={fieldError(err, 'entity')}>
          {config.entities.map((e) => <option key={e.name} value={e.name}>{e.label}</option>)}
        </Select>
        <Input label="Nombre" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} error={fieldError(err, 'name')} data-autofocus />
        <div className="row" style={{ gap: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'flex-start' }}>
          <Input label="Prioridad" hint="1 = la primera" type="number" min={1} max={999} value={priority} onChange={(e) => setPriority(e.target.value)} error={fieldError(err, 'priority')} />
          <Input label="Vigente desde" hint="Opcional" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          <Input label="Vigente hasta" hint="Opcional" type="date" value={to} onChange={(e) => setTo(e.target.value)} error={fieldError(err, 'validTo')} />
        </div>
        {def?.amount && (
          <div className="row" style={{ gap: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'flex-start' }}>
            <Input label={`${def.amount} desde`} hint="Opcional" type="number" min={0} step="0.01" value={min} onChange={(e) => setMin(e.target.value)} error={fieldError(err, 'minAmount')} />
            <Input label={`${def.amount} hasta`} hint="Opcional" type="number" min={0} step="0.01" value={max} onChange={(e) => setMax(e.target.value)} error={fieldError(err, 'maxAmount')} />
          </div>
        )}
        {(def?.conditions ?? []).map((c) => {
          const v = conds[c.field] ?? { ids: [], labels: [] };
          const error = fieldError(err, `conditions.${c.field}`);
          if (c.type === 'choice' || c.type === 'boolean') {
            const opts: { value: string | boolean; label: string }[] = c.type === 'boolean'
              ? [{ value: true, label: 'Sí' }, { value: false, label: 'No' }]
              : (c.options ?? []).map((o) => ({ value: o, label: o }));
            return (
              <fieldset key={c.field} className="wf-fieldset">
                <legend>{c.label} <span className="subtle">(ninguno = cualquiera)</span></legend>
                <div className="row" style={{ gap: 'var(--space-3)', flexWrap: 'wrap' }}>
                  {opts.map((o) => (
                    <label key={String(o.value)} className="row" style={{ gap: 6 }}>
                      <input type="checkbox" checked={v.ids.some((x) => String(x) === String(o.value))} onChange={(e) => toggle(c.field, o.value, e.target.checked)} />
                      {o.label}
                    </label>
                  ))}
                </div>
                {error && <p className="field-error">{error}</p>}
              </fieldset>
            );
          }
          return (
            <Field key={c.field} label={<>{c.label} <span className="subtle">(vacío = cualquiera)</span></>} htmlFor={`cond-${c.field}`} error={error}>
              <MultiReferenceSelect id={`cond-${c.field}`} entity={c.referenceEntity ?? ''} fieldLabel={c.label}
                value={v.ids.map(Number)} labels={v.labels} invalid={!!error}
                lookup={c.type === 'user' ? (search, signal) => usersApi.lookup(search, signal) : undefined}
                onChange={(ids, labels) => setCond(c.field, { ids, labels })} />
            </Field>
          );
        })}
        <fieldset className="wf-fieldset">
          <legend>Niveles, en orden</legend>
          <ol className="approval-levels-edit">
            {levels.map((pid, i) => (
              <li key={i} className="row">
                <Select aria-label={`Perfil del nivel ${i + 1}`} value={pid} onChange={(e) => setLevels((ls) => ls.map((x, j) => (j === i ? Number(e.target.value) : x)))}>
                  {activeProfiles.map((p) => <option key={p.id} value={p.id}>{`Nivel ${i + 1}: ${p.name}${p.isActive ? '' : ' (inactivo)'}`}</option>)}
                </Select>
                <Button size="sm" variant="ghost" icon={ArrowUp} disabled={i === 0} onClick={() => move(i, -1)} aria-label={`Subir el nivel ${i + 1}`} />
                <Button size="sm" variant="ghost" icon={ArrowDown} disabled={i === levels.length - 1} onClick={() => move(i, 1)} aria-label={`Bajar el nivel ${i + 1}`} />
                <Button size="sm" variant="ghost" icon={Trash2} disabled={levels.length === 1} onClick={() => setLevels((ls) => ls.filter((_, j) => j !== i))} aria-label={`Quitar el nivel ${i + 1}`} />
              </li>
            ))}
          </ol>
          {fieldError(err, 'levels') && <p className="field-error" role="alert">{fieldError(err, 'levels')}</p>}
          <Button size="sm" icon={Plus} disabled={levels.length >= 10 || activeProfiles.length === 0} onClick={() => setLevels((ls) => [...ls, activeProfiles[0].id])}>Agregar nivel</Button>
        </fieldset>
        {rule && <Switch checked={active} onChange={setActive} label="Activa" />}
      </div>
    </Dialog>
  );
}
