import { useEffect, useMemo, useRef, useState } from 'react';
import { Building2, Check, ClipboardCheck, KeyRound, MoveRight, Network, Pencil, Plus, ShieldCheck, UserPlus, Users, UsersRound, X } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { adminApi, companiesApi, modelApi, type RoleInput } from '../api/endpoints';
import { isPending, type BusinessUnit, type Company, type EntityDef, type EntityPermission, type Role, type SecurityChange, type Team, type User } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, Checkbox, EmptyState, Field, Input, LoadingBlock, PageHeader, Select, Switch, Tabs, Textarea } from '../components/ui';
import { formatDateTime, initials } from '../lib/format';
import { useAsync } from '../lib/hooks';

type Tab = 'users' | 'roles' | 'teams' | 'units' | 'requests';

/** Spec 015: un cambio que amplía acceso en un entorno con aprobación de seguridad queda como solicitud (202). */
const PENDING_TEXT = 'Solicitud enviada: otro administrador debe aprobarla en Seguridad › Solicitudes';

const isAdminRole = (r: Role) => !!r.isSuperuser || r.canManageModel || r.canManageUsers || r.canViewAudit;

/** Unidades con sangría por nivel (el servidor las devuelve en orden de árbol). */
function UnitSelect({ label, units, value, onChange, error, hint, exclude, emptyLabel = '— Sin unidad —' }: {
  label: string; units: BusinessUnit[]; value: string; onChange: (v: string) => void; error?: string; hint?: string;
  exclude?: (u: BusinessUnit) => boolean; emptyLabel?: string;
}) {
  return (
    <Select label={label} value={value} error={error} hint={hint} onChange={(e) => onChange(e.target.value)}>
      <option value="">{emptyLabel}</option>
      {units.filter((u) => !exclude?.(u)).map((u) => (
        <option key={u.id} value={u.name}>{'  '.repeat(u.depth)}{u.name}</option>
      ))}
    </Select>
  );
}

/* ----------------------------- Users ----------------------------- */

/**
 * Regresión (revisión exploratoria 021, F-01): el error de un diálogo largo se trae a la vista. Se guarda desde el pie y el
 * aviso queda arriba, fuera de la vista: parecía que «Guardar» no hacía nada.
 */
function DialogError({ error }: { error: ApiError }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => { ref.current?.scrollIntoView({ block: 'nearest' }); }, [error]);
  return (
    <div ref={ref}>
      <Alert tone="danger" title={error.title}>{error.detail ?? Object.values(error.fieldErrors).flat().join(' ')}</Alert>
    </div>
  );
}

function UserDialog({ user, roles, companies, units, open, onClose, onSaved }: { user: User | null; roles: Role[]; companies: Company[]; units: BusinessUnit[]; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const isNew = !user;
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [isActive, setIsActive] = useState(true);
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [unit, setUnit] = useState('');
  const [companyIds, setCompanyIds] = useState<number[]>([]);
  // Spec 010: con una sola empresa no hay nada que elegir (el servidor asigna la predeterminada).
  const pickCompanies = companies.length > 1;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [local, setLocal] = useState<Record<string, string>>({});

  useEffect(() => {
    if (!open) return;
    setUsername(user?.username ?? '');
    setDisplayName(user?.displayName ?? '');
    setPassword('');
    setIsActive(user?.isActive ?? true);
    setRoleIds(user?.roleIds ?? []);
    setUnit(user?.unit ?? '');
    setCompanyIds(user?.companyIds ?? companies.filter((c) => c.isDefault).map((c) => c.id));
    setError(null);
    setLocal({});
  }, [open, user, companies]);

  async function save() {
    const e: Record<string, string> = {};
    if (isNew && !/^[A-Za-z0-9._-]{3,50}$/.test(username)) e.username = 'De 3 a 50 caracteres: letras, números, punto, guion o guion bajo';
    if (!displayName.trim()) e.displayName = 'Requerido';
    if (isNew && password.length < 12) e.password = 'Mínimo 12 caracteres';
    if (!isNew && password && password.length < 12) e.password = 'Mínimo 12 caracteres';
    setLocal(e);
    if (Object.keys(e).length) return;
    setSaving(true);
    setError(null);
    try {
      const u = unit || null;
      const cs = pickCompanies ? companyIds : undefined;
      const r = isNew
        ? await adminApi.createUser({ username, displayName: displayName.trim(), password, roleIds, isActive, unit: u, companyIds: cs })
        : await adminApi.updateUser(user!.id, { displayName: displayName.trim(), isActive, roleIds, password: password || undefined, unit: u, companyIds: cs });
      toast.success(isPending(r) ? PENDING_TEXT : isNew ? 'Usuario creado' : 'Usuario actualizado');
      onSaved();
    } catch (err) {
      if (err instanceof ApiError) setError(err); else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  const fe = (k: string) => local[k] ?? fieldError(error, k);
  const general = error && Object.keys(error.fieldErrors).length === 0 ? error : null;

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isNew ? 'Nuevo usuario' : `Editar ${user?.displayName}`}
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={save}>{isNew ? 'Crear usuario' : 'Guardar'}</Button></>}
    >
      <div className="stack">
        {general && <DialogError error={general} />}
        <Input label="Usuario" required autoComplete="off" value={username} disabled={!isNew} error={fe('username')} onChange={(e) => setUsername(e.target.value)} maxLength={50} data-autofocus />
        <Input label="Nombre completo" required value={displayName} error={fe('displayName')} onChange={(e) => setDisplayName(e.target.value)} maxLength={100} />
        <Input
          label={isNew ? 'Contraseña' : 'Nueva contraseña'}
          required={isNew}
          type="password"
          autoComplete="new-password"
          icon={KeyRound}
          value={password}
          error={fe('password')}
          hint={isNew ? 'Mínimo 12 caracteres. Recomendado: una frase.' : 'Déjalo vacío para conservar la actual.'}
          onChange={(e) => setPassword(e.target.value)}
          maxLength={256}
        />
        <UnitSelect
          label="Unidad"
          units={units}
          value={unit}
          onChange={setUnit}
          error={fe('unit')}
          hint="Área o sucursal (Seguridad › Unidades). Define qué registros ve con los alcances «de mi unidad» y «mi unidad y las que cuelgan de ella»."
        />
        <Field label="Roles">
          <div className="stack" style={{ gap: 'var(--space-2)' }}>
            {roles.map((r) => (
              <Checkbox
                key={r.id}
                checked={roleIds.includes(r.id)}
                onChange={(c) => setRoleIds((ids) => (c ? [...ids, r.id] : ids.filter((x) => x !== r.id)))}
                label={<>{r.name}{r.description && <span className="subtle"> · {r.description}</span>}</>}
              />
            ))}
            {roles.length === 0 && <span className="subtle">No hay roles definidos.</span>}
          </div>
        </Field>
        {pickCompanies && (
          <Field label="Empresas" error={fe('companyIds')} hint="Las que puede elegir en la barra superior. Sin ninguna no ve ni captura registros de catálogos por empresa.">
            <div className="stack" style={{ gap: 'var(--space-2)' }}>
              {companies.map((c) => (
                <Checkbox
                  key={c.id}
                  checked={companyIds.includes(c.id)}
                  onChange={(v) => setCompanyIds((ids) => (v ? [...ids, c.id] : ids.filter((x) => x !== c.id)))}
                  label={<>{c.name}<span className="subtle"> · {c.code} · {c.baseCurrency}{c.isActive ? '' : ' · inactiva'}</span></>}
                />
              ))}
            </div>
          </Field>
        )}
        <Switch checked={isActive} onChange={setIsActive} label="Usuario activo" />
      </div>
    </Dialog>
  );
}

function UsersTab({ roles, units }: { roles: Role[]; units: BusinessUnit[] }) {
  const { data, loading, error, reload } = useAsync<User[]>(() => adminApi.users(), []);
  const { data: companies } = useAsync<Company[]>((s) => companiesApi.list(s), []);
  const [editing, setEditing] = useState<User | null>(null);
  const [open, setOpen] = useState(false);
  const roleName = (id: number) => roles.find((r) => r.id === id)?.name ?? `#${id}`;
  const companyNames = (ids: number[]) => (companies ?? []).filter((c) => ids.includes(c.id)).map((c) => c.name).join(', ');
  const manyCompanies = (companies?.length ?? 0) > 1;

  return (
    <>
      <div className="toolbar">
        <span className="spacer" />
        <Button variant="primary" icon={UserPlus} onClick={() => { setEditing(null); setOpen(true); }}>Nuevo usuario</Button>
      </div>
      {loading ? <LoadingBlock /> : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los usuarios'} /> : !data?.length ? (
        <div className="card"><EmptyState icon={Users} title="Sin usuarios" /></div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Usuario</th><th>Roles</th><th>Estado</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {data.map((u) => {
                  const locked = u.lockoutEnd && new Date(u.lockoutEnd) > new Date();
                  return (
                    <tr key={u.id}>
                      <td>
                        <div className="row" style={{ gap: 'var(--space-3)' }}>
                          <span className="avatar">{initials(u.displayName || u.username)}</span>
                          <div>
                            <div style={{ fontWeight: 500 }}>{u.displayName}</div>
                            <div className="subtle"><span className="mono">{u.username}</span>{u.unit && <> · {u.unit}</>}</div>
                            {manyCompanies && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{companyNames(u.companyIds) || 'Sin empresa'}</div>}
                          </div>
                        </div>
                      </td>
                      <td>
                        <div className="row" style={{ flexWrap: 'wrap', gap: 4 }}>
                          {(u.roles ?? u.roleIds.map(roleName)).map((r) => <Badge key={r} tone="accent">{r}</Badge>)}
                          {u.roleIds.length === 0 && !u.roles?.length && <span className="subtle">Sin roles</span>}
                        </div>
                      </td>
                      <td>
                        {locked ? <Badge tone="danger">Bloqueado</Badge> : u.isActive ? <Badge tone="success">Activo</Badge> : <Badge>Inactivo</Badge>}
                      </td>
                      <td className="td-actions">
                        <Button variant="ghost" size="sm" icon={Pencil} onClick={() => { setEditing(u); setOpen(true); }}>Editar</Button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <UserDialog open={open} user={editing} roles={roles} companies={companies ?? []} units={units} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); reload(); }} />
    </>
  );
}

/* ----------------------------- Roles ----------------------------- */

const PERM_COLS: { key: keyof Omit<EntityPermission, 'entity'>; label: string }[] = [
  { key: 'canRead', label: 'Leer' },
  { key: 'canCreate', label: 'Crear' },
  { key: 'canUpdate', label: 'Editar' },
  { key: 'canDelete', label: 'Eliminar' },
];

function RoleDialog({ role, roles, entities, open, onClose, onSaved }: { role: Role | null; roles: Role[]; entities: EntityDef[]; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const isNew = !role;
  const [form, setForm] = useState<RoleInput>({ name: '', description: '', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [], inheritsRoleIds: [] });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [nameErr, setNameErr] = useState<string | undefined>();

  useEffect(() => {
    if (!open) return;
    setForm({
      name: role?.name ?? '',
      description: role?.description ?? '',
      canManageModel: role?.canManageModel ?? false,
      canManageUsers: role?.canManageUsers ?? false,
      canViewAudit: role?.canViewAudit ?? false,
      permissions: role?.permissions ?? [],
      inheritsRoleIds: role?.inheritsRoleIds ?? [],
    });
    setError(null);
    setNameErr(undefined);
  }, [open, role]);

  const permFor = (entity: string): EntityPermission =>
    form.permissions.find((p) => p.entity.toLowerCase() === entity.toLowerCase()) ?? { entity, canRead: false, canCreate: false, canUpdate: false, canDelete: false };

  function setPerm(entity: string, key: keyof Omit<EntityPermission, 'entity'>, value: boolean) {
    setForm((f) => {
      const current = permFor(entity);
      const next = { ...current, [key]: value };
      // Any write permission implies read; removing read removes everything.
      if (key !== 'canRead' && value) next.canRead = true;
      if (key === 'canRead' && !value) { next.canCreate = false; next.canUpdate = false; next.canDelete = false; }
      const others = f.permissions.filter((p) => p.entity.toLowerCase() !== entity.toLowerCase());
      return { ...f, permissions: [...others, next] };
    });
  }

  function setColumn(key: keyof Omit<EntityPermission, 'entity'>, value: boolean) {
    entities.forEach((e) => setPerm(e.name, key, value));
  }

  async function save() {
    if (!form.name.trim()) { setNameErr('Requerido'); return; }
    setSaving(true);
    setError(null);
    try {
      const payload: RoleInput = {
        ...form,
        name: form.name.trim(),
        description: form.description?.trim() || undefined,
        permissions: form.permissions.filter((p) => p.canRead || p.canCreate || p.canUpdate || p.canDelete),
      };
      const r = isNew ? await adminApi.createRole(payload) : await adminApi.updateRole(role!.id, payload);
      toast.success(isPending(r) ? PENDING_TEXT : isNew ? 'Rol creado' : 'Rol actualizado');
      onSaved();
    } catch (err) {
      if (err instanceof ApiError) setError(err); else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  const superuser = !!role?.isSuperuser;
  // Spec 015: las capacidades administrativas no se heredan; tampoco se ofrecen roles que las tengan.
  const parents = roles.filter((r) => r.id !== role?.id && !isAdminRole(r));
  const inheritsFromThis = role ? roles.filter((r) => r.inheritsRoleIds.includes(role.id)) : [];

  return (
    <Dialog
      open={open}
      onClose={onClose}
      size="lg"
      title={isNew ? 'Nuevo rol' : `Rol: ${role?.name}`}
      description={superuser ? 'Este rol del sistema tiene todos los permisos y no se puede restringir.' : undefined}
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={save} disabled={superuser}>{isNew ? 'Crear rol' : 'Guardar'}</Button></>}
    >
      <div className="stack">
        {error && <DialogError error={error} />}
        <div className="form-grid">
          <Input label="Nombre" required value={form.name} disabled={superuser} error={nameErr ?? fieldError(error, 'name')} onChange={(e) => { setNameErr(undefined); setForm((f) => ({ ...f, name: e.target.value })); }} maxLength={50} data-autofocus />
          <Textarea label="Descripción" rows={1} style={{ minHeight: 36 }} value={form.description ?? ''} disabled={superuser} onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))} maxLength={200} />
        </div>
        <Field label="Capacidades administrativas" hint={inheritsFromThis.length ? `Otros roles heredan de este (${inheritsFromThis.map((r) => r.name).join(', ')}): no puede tener capacidades administrativas.` : undefined}>
          <div className="stack" style={{ gap: 'var(--space-2)' }}>
            <Switch checked={superuser || form.canManageModel} disabled={superuser} onChange={(v) => setForm((f) => ({ ...f, canManageModel: v }))} label="Modificar el modelo (catálogos y campos)" />
            <Switch checked={superuser || form.canManageUsers} disabled={superuser} onChange={(v) => setForm((f) => ({ ...f, canManageUsers: v }))} label="Administrar usuarios y roles" />
            <Switch checked={superuser || form.canViewAudit} disabled={superuser} onChange={(v) => setForm((f) => ({ ...f, canViewAudit: v }))} label="Consultar auditoría" />
          </div>
        </Field>
        {!superuser && (
          <Field label="Hereda de" error={fieldError(error, 'inheritsRoleIds')} hint="Quien tiene este rol recibe también los permisos de datos de estos roles (y de los que ellos heredan). Las capacidades administrativas no se heredan.">
            <div className="stack" style={{ gap: 'var(--space-2)' }}>
              {parents.map((r) => (
                <Checkbox
                  key={r.id}
                  checked={form.inheritsRoleIds?.includes(r.id) ?? false}
                  onChange={(v) => setForm((f) => ({ ...f, inheritsRoleIds: v ? [...(f.inheritsRoleIds ?? []), r.id] : (f.inheritsRoleIds ?? []).filter((x) => x !== r.id) }))}
                  label={<>{r.name}{r.description && <span className="subtle"> · {r.description}</span>}</>}
                />
              ))}
              {parents.length === 0 && <span className="subtle">No hay otros roles sin capacidades administrativas.</span>}
            </div>
          </Field>
        )}
        <Field label="Permisos por catálogo">
          {entities.length === 0 ? <span className="subtle">No hay catálogos todavía.</span> : (
            <div className="table-wrap">
              <div className="table-scroll">
                <table className="table perm-table">
                  <thead>
                    <tr>
                      <th>Catálogo</th>
                      {PERM_COLS.map((c) => {
                        const all = entities.every((e) => permFor(e.name)[c.key]);
                        return (
                          <th key={c.key}>
                            <span className="row" style={{ justifyContent: 'center', gap: 6 }}>
                              <Checkbox checked={superuser || all} disabled={superuser} onChange={(v) => setColumn(c.key, v)} aria-label={`${c.label}: todos`} />
                              {c.label}
                            </span>
                          </th>
                        );
                      })}
                    </tr>
                  </thead>
                  <tbody>
                    {entities.map((e) => {
                      const p = permFor(e.name);
                      return (
                        <tr key={e.name}>
                          <td>{e.labelPlural}</td>
                          {PERM_COLS.map((c) => (
                            <td key={c.key}>
                              <Checkbox checked={superuser || p[c.key]} disabled={superuser} onChange={(v) => setPerm(e.name, c.key, v)} aria-label={`${c.label} ${e.labelPlural}`} />
                            </td>
                          ))}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </Field>
      </div>
    </Dialog>
  );
}

function RolesTab({ roles, loading, error, reload }: { roles: Role[]; loading: boolean; error: unknown; reload: () => void }) {
  const { data: entities } = useAsync<EntityDef[]>(() => modelApi.entities(), []);
  const [editing, setEditing] = useState<Role | null>(null);
  const [open, setOpen] = useState(false);
  const active = useMemo(() => (entities ?? []).filter((e) => !e.isArchived), [entities]);
  const roleName = (id: number) => roles.find((r) => r.id === id)?.name ?? `#${id}`;

  return (
    <>
      <div className="toolbar">
        <span className="spacer" />
        <Button variant="primary" icon={Plus} onClick={() => { setEditing(null); setOpen(true); }}>Nuevo rol</Button>
      </div>
      {loading ? <LoadingBlock /> : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los roles'} /> : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Rol</th><th>Capacidades</th><th className="td-num">Catálogos con acceso</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {roles.map((r) => (
                  <tr key={r.id}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{r.name}</div>
                      {r.description && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{r.description}</div>}
                      {r.inheritsRoleIds.length > 0 && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>Hereda de {r.inheritsRoleIds.map(roleName).join(', ')}</div>}
                    </td>
                    <td>
                      <div className="row" style={{ flexWrap: 'wrap', gap: 4 }}>
                        {r.isSuperuser && <Badge tone="accent">Acceso total</Badge>}
                        {!r.isSuperuser && r.canManageModel && <Badge>Modelo</Badge>}
                        {!r.isSuperuser && r.canManageUsers && <Badge>Usuarios</Badge>}
                        {!r.isSuperuser && r.canViewAudit && <Badge>Auditoría</Badge>}
                        {!r.isSuperuser && !r.canManageModel && !r.canManageUsers && !r.canViewAudit && <span className="subtle">—</span>}
                      </div>
                    </td>
                    <td className="td-num">{r.isSuperuser ? 'Todos' : r.permissions.filter((p) => p.canRead).length}</td>
                    <td className="td-actions">
                      <Button variant="ghost" size="sm" icon={Pencil} onClick={() => { setEditing(r); setOpen(true); }}>{r.isSuperuser ? 'Ver' : 'Editar'}</Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <RoleDialog open={open} role={editing} roles={roles} entities={active} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); reload(); }} />
    </>
  );
}

/* ----------------------------- Teams (spec 015) ----------------------------- */

function TeamDialog({ team, users, units, open, onClose, onSaved }: { team: Team | null; users: User[]; units: BusinessUnit[]; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const isNew = !team;
  const [name, setName] = useState('');
  const [unit, setUnit] = useState('');
  const [isActive, setIsActive] = useState(true);
  const [memberIds, setMemberIds] = useState<number[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [nameErr, setNameErr] = useState<string | undefined>();

  useEffect(() => {
    if (!open) return;
    setName(team?.name ?? '');
    setUnit(team?.unit ?? '');
    setIsActive(team?.isActive ?? true);
    setMemberIds(team?.memberIds ?? []);
    setError(null);
    setNameErr(undefined);
  }, [open, team]);

  async function save() {
    if (!name.trim()) { setNameErr('Requerido'); return; }
    setSaving(true);
    setError(null);
    try {
      const input = { name: name.trim(), unit: unit || null, isActive, memberIds };
      const r = isNew ? await adminApi.createTeam(input) : await adminApi.updateTeam(team!.id, input);
      toast.success(isPending(r) ? PENDING_TEXT : isNew ? 'Equipo creado' : 'Equipo actualizado');
      onSaved();
    } catch (err) {
      if (err instanceof ApiError) setError(err); else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  const general = error && Object.keys(error.fieldErrors).length === 0 ? error : null;
  const people = users.filter((u) => u.isActive || memberIds.includes(u.id));

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isNew ? 'Nuevo equipo' : `Equipo: ${team?.name}`}
      description="Un equipo puede ser dueño de registros en los catálogos que lo permiten: sus miembros los ven como propios. No inicia sesión ni tiene roles."
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={save}>{isNew ? 'Crear equipo' : 'Guardar'}</Button></>}
    >
      <div className="stack">
        {general && <DialogError error={general} />}
        <Input label="Nombre" required value={name} error={nameErr ?? fieldError(error, 'name')} onChange={(e) => { setNameErr(undefined); setName(e.target.value); }} maxLength={150} data-autofocus />
        <UnitSelect label="Unidad" units={units} value={unit} onChange={setUnit} error={fieldError(error, 'unit')}
          hint="Sus registros cuentan como de esta unidad para los alcances por unidad." />
        <Field label="Miembros" error={fieldError(error, 'memberIds')}>
          <div className="stack" style={{ gap: 'var(--space-2)', maxHeight: 260, overflowY: 'auto', position: 'relative' }}>
            {people.map((u) => (
              <Checkbox
                key={u.id}
                checked={memberIds.includes(u.id)}
                onChange={(v) => setMemberIds((ids) => (v ? [...ids, u.id] : ids.filter((x) => x !== u.id)))}
                label={<>{u.displayName}<span className="subtle"> · {u.username}</span></>}
              />
            ))}
            {people.length === 0 && <span className="subtle">No hay usuarios.</span>}
          </div>
        </Field>
        {!isNew && <Switch checked={isActive} onChange={setIsActive} label="Equipo activo" />}
      </div>
    </Dialog>
  );
}

function TeamsTab({ units }: { units: BusinessUnit[] }) {
  const { data, loading, error, reload } = useAsync<Team[]>(() => adminApi.teams(), []);
  const { data: users } = useAsync<User[]>(() => adminApi.users(), []);
  const [editing, setEditing] = useState<Team | null>(null);
  const [open, setOpen] = useState(false);
  const userName = (id: number) => users?.find((u) => u.id === id)?.displayName ?? `#${id}`;

  return (
    <>
      <div className="toolbar">
        <span className="spacer" />
        <Button variant="primary" icon={Plus} onClick={() => { setEditing(null); setOpen(true); }}>Nuevo equipo</Button>
      </div>
      {loading ? <LoadingBlock /> : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los equipos'} /> : !data?.length ? (
        <div className="card"><EmptyState icon={UsersRound} title="Sin equipos" description="Crea un equipo para que varias personas trabajen los mismos registros." /></div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Equipo</th><th>Miembros</th><th>Estado</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {data.map((t) => (
                  <tr key={t.id}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{t.name}</div>
                      {t.unit && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{t.unit}</div>}
                    </td>
                    <td>{t.memberIds.length ? t.memberIds.map(userName).join(', ') : <span className="subtle">Sin miembros</span>}</td>
                    <td>{t.isActive ? <Badge tone="success">Activo</Badge> : <Badge>Inactivo</Badge>}</td>
                    <td className="td-actions">
                      <Button variant="ghost" size="sm" icon={Pencil} onClick={() => { setEditing(t); setOpen(true); }} aria-label={`Editar ${t.name}`}>Editar</Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <TeamDialog open={open} team={editing} users={users ?? []} units={units} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); reload(); }} />
    </>
  );
}

/* ----------------------------- Units (spec 015) ----------------------------- */

function UnitDialog({ unit, units, open, onClose, onSaved }: { unit: BusinessUnit | null; units: BusinessUnit[]; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const isNew = !unit;
  const [name, setName] = useState('');
  const [parent, setParent] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [nameErr, setNameErr] = useState<string | undefined>();

  useEffect(() => {
    if (!open) return;
    setName('');
    setParent(unit ? units.find((u) => u.id === unit.parentId)?.name ?? '' : '');
    setError(null);
    setNameErr(undefined);
  }, [open, unit, units]);

  // Una unidad no puede quedar debajo de sí misma ni de las que cuelgan de ella (las siguientes con más profundidad).
  const descendants = useMemo(() => {
    if (!unit) return new Set<number>();
    const i = units.findIndex((u) => u.id === unit.id);
    const set = new Set<number>([unit.id]);
    for (let j = i + 1; j < units.length && units[j].depth > unit.depth; j++) set.add(units[j].id);
    return set;
  }, [unit, units]);

  async function save() {
    if (isNew && !name.trim()) { setNameErr('Requerido'); return; }
    setSaving(true);
    setError(null);
    try {
      const parentId = units.find((u) => u.name === parent)?.id ?? null;
      if (isNew) {
        await adminApi.createUnit({ name: name.trim(), parentId });
        toast.success('Unidad creada');
      } else {
        const r = await adminApi.moveUnit(unit!.id, parentId);
        toast.success(isPending(r) ? PENDING_TEXT : 'Unidad movida');
      }
      onSaved();
    } catch (err) {
      if (err instanceof ApiError) setError(err); else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  const general = error && Object.keys(error.fieldErrors).length === 0 ? error : null;
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isNew ? 'Nueva unidad' : `Mover ${unit?.name}`}
      description={isNew ? 'El nombre no se cambia después: lo usan las reglas de acceso y los datos.' : 'Quienes tienen alcance sobre la unidad superior verán también lo de esta y de las que cuelgan de ella.'}
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant="primary" loading={saving} onClick={save}>{isNew ? 'Crear unidad' : 'Mover'}</Button></>}
    >
      <div className="stack">
        {general && <DialogError error={general} />}
        {isNew && <Input label="Nombre" required value={name} error={nameErr ?? fieldError(error, 'name')} onChange={(e) => { setNameErr(undefined); setName(e.target.value); }} maxLength={100} data-autofocus />}
        <UnitSelect label="Unidad superior" units={units} value={parent} onChange={setParent} error={fieldError(error, 'parentId')}
          exclude={(u) => descendants.has(u.id)} emptyLabel="— Ninguna (unidad raíz) —" />
      </div>
    </Dialog>
  );
}

function UnitsTab({ units, loading, error, reload }: { units: BusinessUnit[]; loading: boolean; error: unknown; reload: () => void }) {
  const [moving, setMoving] = useState<BusinessUnit | null>(null);
  const [open, setOpen] = useState(false);
  return (
    <>
      <div className="toolbar">
        <span className="subtle">El alcance «mi unidad y las que cuelgan de ella» ve los registros de toda la rama.</span>
        <span className="spacer" />
        <Button variant="primary" icon={Plus} onClick={() => { setMoving(null); setOpen(true); }}>Nueva unidad</Button>
      </div>
      {loading ? <LoadingBlock /> : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar las unidades'} /> : !units.length ? (
        <div className="card"><EmptyState icon={Network} title="Sin unidades" description="Crea las áreas o sucursales y organízalas en un árbol." /></div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Unidad</th><th className="td-num">Usuarios</th><th className="td-num">Equipos</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {units.map((u) => (
                  <tr key={u.id}>
                    <td>
                      <span className="row" style={{ gap: 'var(--space-2)', paddingLeft: `calc(${u.depth} * var(--space-5))` }}>
                        <Building2 size={15} aria-hidden className="subtle" />
                        <span style={{ fontWeight: u.depth === 0 ? 500 : undefined }}>{u.name}</span>
                      </span>
                    </td>
                    <td className="td-num">{u.users}</td>
                    <td className="td-num">{u.teams}</td>
                    <td className="td-actions">
                      <Button variant="ghost" size="sm" icon={MoveRight} onClick={() => { setMoving(u); setOpen(true); }} aria-label={`Mover ${u.name}`}>Mover</Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <UnitDialog open={open} unit={moving} units={units} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); reload(); }} />
    </>
  );
}

/* ----------------------------- Requests (spec 015) ----------------------------- */

const KIND_LABEL: Record<SecurityChange['kind'], string> = {
  'user.create': 'Alta de usuario', 'user.update': 'Cambio de usuario', 'role.create': 'Alta de rol', 'role.update': 'Cambio de rol',
  'team.create': 'Alta de equipo', 'team.update': 'Cambio de equipo', 'unit.move': 'Mover unidad',
  'apikey.create': 'Alta de clave de API', 'apikey.update': 'Roles de clave de API',
  'oauthclient.create': 'Alta de cliente OAuth', 'oauthclient.update': 'Roles y alcances de cliente OAuth',
};

const FIELD_LABEL: Record<string, string> = {
  username: 'Usuario', displayName: 'Nombre', name: 'Nombre', isActive: 'Activo', roleIds: 'Roles', unit: 'Unidad', companyIds: 'Empresas',
  passwordChanged: 'Contraseña', canManageModel: 'Modificar el modelo', canManageUsers: 'Administrar usuarios', canViewAudit: 'Consultar auditoría',
  permissions: 'Permisos', inheritsRoleIds: 'Hereda de', memberIds: 'Miembros', parentId: 'Unidad superior', expiresInDays: 'Vence en (días)',
  description: 'Descripción', scopes: 'Alcances', tokenMinutes: 'Vida del token (min)', clientId: 'client_id',
};

type Lookups = { roles: Role[]; users: User[]; companies: Company[]; units: BusinessUnit[] };

function show(key: string, v: unknown, l: Lookups): string {
  if (key === 'parentId' && v == null) return 'Ninguna (raíz)';
  if (v === null || v === undefined || v === '') return '—';
  const names = (ids: unknown, find: (id: number) => string | undefined) => (Array.isArray(ids) && ids.length ? ids.map((id: number) => find(id) ?? `#${id}`).join(', ') : '—');
  switch (key) {
    case 'roleIds': case 'inheritsRoleIds': return names(v, (id) => l.roles.find((r) => r.id === id)?.name);
    case 'memberIds': return names(v, (id) => l.users.find((u) => u.id === id)?.displayName);
    case 'companyIds': return names(v, (id) => l.companies.find((c) => c.id === id)?.name);
    case 'parentId': return l.units.find((u) => u.id === v)?.name ?? '— (raíz)';
    case 'passwordChanged': return v ? 'Se cambia' : '—';
    case 'permissions': {
      if (!Array.isArray(v) || !v.length) return 'Ninguno';
      // Antes (instantánea: read/create…) y después (petición: canRead/canCreate…).
      return v.map((p: Record<string, unknown>) => {
        const bits = [['L', p.read ?? p.canRead], ['C', p.create ?? p.canCreate], ['E', p.update ?? p.canUpdate], ['B', p.delete ?? p.canDelete]]
          .filter(([, on]) => on).map(([k]) => k).join('');
        return `${p.entity}: ${bits}`;
      }).join(' · ');
    }
    default: return typeof v === 'boolean' ? (v ? 'Sí' : 'No') : String(v);
  }
}

function ChangeTable({ change, lookups }: { change: SecurityChange; lookups: Lookups }) {
  // Regresión (revisión exploratoria 015, F-02/F-03): una carga sin empresas (null) es «sin cambio», no «quitarlas»; un
  // dato que la instantánea no guarda (el nombre de una clave) no se presenta como cambio.
  const rows = Object.keys(FIELD_LABEL)
    .filter((k) => k in change.after && !(k === 'companyIds' && change.after[k] == null))
    .filter((k) => !((k === 'name' || k === 'username') && !change.after[k]))
    .map((k) => {
      const tracked = !!change.before && k in change.before;
      const before = change.before ? (tracked ? show(k, change.before[k], lookups) : '') : null;
      const after = show(k, change.after[k], lookups);
      return { k, before, after, changed: before === null ? after !== '—' : tracked && before !== after };
    }).filter((r) => r.changed || r.k === 'name' || r.k === 'username');
  return (
    <div className="table-wrap">
      <div className="table-scroll">
        <table className="table">
          <thead><tr><th>Dato</th>{change.before && <th>Antes</th>}<th>{change.before ? 'Después' : 'Valor'}</th></tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.k}>
                <td>{FIELD_LABEL[r.k]}</td>
                {change.before && <td className="subtle">{r.before}</td>}
                <td style={{ fontWeight: r.changed && change.before ? 500 : undefined }}>{r.after}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function ReviewDialog({ change, mode, onClose, onDone }: { change: SecurityChange | null; mode: 'approve' | 'reject'; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [comment, setComment] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  useEffect(() => { setComment(''); setError(null); }, [change]);
  async function confirm() {
    if (!change) return;
    setSaving(true);
    setError(null);
    try {
      if (mode === 'approve') await adminApi.approveChange(change.id, change.hash, comment.trim() || undefined);
      else await adminApi.rejectChange(change.id, comment.trim() || undefined);
      toast.success(mode === 'approve' ? 'Solicitud aprobada y aplicada' : 'Solicitud rechazada');
      onDone();
    } catch (err) {
      if (err instanceof ApiError) setError(err); else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }
  return (
    <Dialog
      open={!!change}
      onClose={onClose}
      title={mode === 'approve' ? 'Aprobar solicitud' : 'Rechazar solicitud'}
      description={change?.summary}
      footer={<><Button onClick={onClose} disabled={saving}>Cancelar</Button><Button variant={mode === 'approve' ? 'primary' : 'danger'} loading={saving} onClick={confirm}>{mode === 'approve' ? 'Aprobar y aplicar' : 'Rechazar'}</Button></>}
    >
      <div className="stack">
        {error && <Alert tone="danger" title={error.title}>{error.detail}</Alert>}
        {mode === 'approve' && <p className="subtle" style={{ margin: 0 }}>El cambio se aplica al aprobar. Si lo solicitado cambió desde entonces, la aprobación se rechaza y hay que volver a solicitarlo.</p>}
        <Textarea label="Comentario" rows={2} value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} data-autofocus />
      </div>
    </Dialog>
  );
}

function RequestsTab({ roles, units, onChanged }: { roles: Role[]; units: BusinessUnit[]; onChanged: () => void }) {
  const [history, setHistory] = useState(false);
  const { data, loading, error, reload } = useAsync(() => adminApi.securityChanges(history ? 'all' : 'pending'), [history]);
  const { data: users } = useAsync<User[]>(() => adminApi.users(), []);
  const { data: companies } = useAsync<Company[]>((s) => companiesApi.list(s), []);
  const [reviewing, setReviewing] = useState<{ change: SecurityChange; mode: 'approve' | 'reject' } | null>(null);
  const lookups: Lookups = { roles, users: users ?? [], companies: companies ?? [], units };
  const status = (c: SecurityChange) => c.status === 'pending' ? <Badge tone="warning">Pendiente</Badge>
    : c.status === 'applied' ? <Badge tone="success">Aplicada</Badge> : c.status === 'rejected' ? <Badge tone="danger">Rechazada</Badge> : <Badge>Aplicando</Badge>;

  return (
    <>
      {data && (data.required
        ? <Alert tone="info" title="Este entorno exige aprobación para ampliar accesos">Altas de usuarios, roles, equipos y claves, y lo que agrega permisos, roles, empresas, miembros o unidades, esperan a que otro administrador lo apruebe aquí. Lo que solo quita accesos se aplica de inmediato.</Alert>
        : <Alert tone="info" title="Este entorno no exige aprobación de cambios de seguridad">Los cambios se aplican al guardarlos. Se activa con Environments:RequireSecurityApproval en un entorno con aprobación.</Alert>)}
      <div className="toolbar">
        <span className="spacer" />
        <Switch checked={history} onChange={setHistory} label="Ver también las resueltas" />
      </div>
      {loading ? <LoadingBlock /> : error ? <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar las solicitudes'} /> : !data?.items.length ? (
        <div className="card"><EmptyState icon={ClipboardCheck} title={history ? 'Sin solicitudes' : 'Sin solicitudes pendientes'} /></div>
      ) : (
        <div className="stack">
          {data.items.map((c) => (
            <section key={c.id} className="card stack" aria-label={c.summary}>
              <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
                <strong>{c.summary}</strong>
                <Badge>{KIND_LABEL[c.kind] ?? c.kind}</Badge>
                {status(c)}
                <span className="spacer" />
                {c.canReject && <Button size="sm" variant="ghost" icon={X} onClick={() => setReviewing({ change: c, mode: 'reject' })}>Rechazar</Button>}
                {c.canApprove && <Button size="sm" variant="primary" icon={Check} onClick={() => setReviewing({ change: c, mode: 'approve' })}>Aprobar</Button>}
              </div>
              <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>
                Solicitó <span className="mono">{c.requestedBy}</span> · {formatDateTime(c.requestedAt)}
                {c.reviewedBy && <> · {c.status === 'rejected' ? 'Rechazó' : 'Aprobó'} <span className="mono">{c.reviewedBy}</span>{c.reviewedAt && <> · {formatDateTime(c.reviewedAt)}</>}</>}
                {c.reviewComment && <> · «{c.reviewComment}»</>}
              </div>
              {c.status === 'pending' && !c.canApprove && c.canReject && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>La debe aprobar otro administrador; puedes cancelarla con «Rechazar».</div>}
              <ChangeTable change={c} lookups={lookups} />
            </section>
          ))}
        </div>
      )}
      <ReviewDialog change={reviewing?.change ?? null} mode={reviewing?.mode ?? 'approve'} onClose={() => setReviewing(null)}
        onDone={() => { setReviewing(null); reload(); onChanged(); }} />
    </>
  );
}

export function SecurityPage() {
  const [tab, setTab] = useState<Tab>('users');
  const { reloadEntities } = useAuth();
  const roles = useAsync<Role[]>(() => adminApi.roles(), []);
  const units = useAsync<BusinessUnit[]>(() => adminApi.units(), []);
  const reloadAll = () => { roles.reload(); units.reload(); void reloadEntities(); };
  return (
    <div className="page">
      <PageHeader icon={ShieldCheck} title="Usuarios y roles" subtitle="Controla quién entra al sistema y qué puede hacer en cada catálogo. Por defecto, todo está denegado." />
      <Tabs<Tab>
        label="Secciones de seguridad"
        value={tab}
        onChange={setTab}
        items={[
          { id: 'users', label: 'Usuarios', icon: Users },
          { id: 'roles', label: 'Roles y permisos', icon: ShieldCheck },
          { id: 'teams', label: 'Equipos', icon: UsersRound },
          { id: 'units', label: 'Unidades', icon: Network },
          { id: 'requests', label: 'Solicitudes', icon: ClipboardCheck },
        ]}
      />
      {tab === 'users' && <UsersTab roles={roles.data ?? []} units={units.data ?? []} />}
      {tab === 'roles' && <RolesTab roles={roles.data ?? []} loading={roles.loading} error={roles.error} reload={() => { roles.reload(); void reloadEntities(); }} />}
      {tab === 'teams' && <TeamsTab units={units.data ?? []} />}
      {tab === 'units' && <UnitsTab units={units.data ?? []} loading={units.loading} error={units.error} reload={units.reload} />}
      {tab === 'requests' && <RequestsTab roles={roles.data ?? []} units={units.data ?? []} onChanged={reloadAll} />}
    </div>
  );
}
