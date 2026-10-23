import { useEffect, useMemo, useState } from 'react';
import { KeyRound, Pencil, Plus, ShieldCheck, UserPlus, Users } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { adminApi, companiesApi, modelApi, type RoleInput } from '../api/endpoints';
import type { Company, EntityDef, EntityPermission, Role, User } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/Dialog';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, Checkbox, EmptyState, Field, Input, LoadingBlock, PageHeader, Switch, Tabs, Textarea } from '../components/ui';
import { initials } from '../lib/format';
import { useAsync } from '../lib/hooks';

type Tab = 'users' | 'roles';

/* ----------------------------- Users ----------------------------- */

function UserDialog({ user, roles, companies, open, onClose, onSaved }: { user: User | null; roles: Role[]; companies: Company[]; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const isNew = !user;
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [isActive, setIsActive] = useState(true);
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [unit, setUnit] = useState('');
  const [units, setUnits] = useState<string[]>([]);
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
    // Sugerencias: las unidades que ya tienen otros usuarios (evita «Norte» y «norte »).
    adminApi.units().then(setUnits, () => setUnits([]));
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
      const u = unit.trim() || null;
      const cs = pickCompanies ? companyIds : undefined;
      if (isNew) await adminApi.createUser({ username, displayName: displayName.trim(), password, roleIds, isActive, unit: u, companyIds: cs });
      else await adminApi.updateUser(user!.id, { displayName: displayName.trim(), isActive, roleIds, password: password || undefined, unit: u, companyIds: cs });
      toast.success(isNew ? 'Usuario creado' : 'Usuario actualizado');
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
        {general && <Alert tone="danger" title={general.title}>{general.detail}</Alert>}
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
        <Input
          label="Unidad"
          value={unit}
          list="user-units"
          autoComplete="off"
          error={fe('unit')}
          hint="Área o sucursal. Define qué registros ve cuando su rol tiene alcance «de mi unidad»."
          onChange={(e) => setUnit(e.target.value)}
          maxLength={100}
        />
        <datalist id="user-units">{units.map((x) => <option key={x} value={x} />)}</datalist>
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

function UsersTab({ roles }: { roles: Role[] }) {
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
      <UserDialog open={open} user={editing} roles={roles} companies={companies ?? []} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); reload(); }} />
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

function RoleDialog({ role, entities, open, onClose, onSaved }: { role: Role | null; entities: EntityDef[]; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const isNew = !role;
  const [form, setForm] = useState<RoleInput>({ name: '', description: '', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] });
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
      if (isNew) await adminApi.createRole(payload); else await adminApi.updateRole(role!.id, payload);
      toast.success(isNew ? 'Rol creado' : 'Rol actualizado');
      onSaved();
    } catch (err) {
      if (err instanceof ApiError) setError(err); else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  const superuser = !!role?.isSuperuser;

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
        {error && <Alert tone="danger" title={error.title}>{error.detail}</Alert>}
        <div className="form-grid">
          <Input label="Nombre" required value={form.name} disabled={superuser} error={nameErr ?? fieldError(error, 'name')} onChange={(e) => { setNameErr(undefined); setForm((f) => ({ ...f, name: e.target.value })); }} maxLength={50} data-autofocus />
          <Textarea label="Descripción" rows={1} style={{ minHeight: 36 }} value={form.description ?? ''} disabled={superuser} onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))} maxLength={200} />
        </div>
        <Field label="Capacidades administrativas">
          <div className="stack" style={{ gap: 'var(--space-2)' }}>
            <Switch checked={superuser || form.canManageModel} disabled={superuser} onChange={(v) => setForm((f) => ({ ...f, canManageModel: v }))} label="Modificar el modelo (catálogos y campos)" />
            <Switch checked={superuser || form.canManageUsers} disabled={superuser} onChange={(v) => setForm((f) => ({ ...f, canManageUsers: v }))} label="Administrar usuarios y roles" />
            <Switch checked={superuser || form.canViewAudit} disabled={superuser} onChange={(v) => setForm((f) => ({ ...f, canViewAudit: v }))} label="Consultar auditoría" />
          </div>
        </Field>
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
      <RoleDialog open={open} role={editing} entities={active} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); reload(); }} />
    </>
  );
}

export function SecurityPage() {
  const [tab, setTab] = useState<Tab>('users');
  const { reloadEntities } = useAuth();
  const roles = useAsync<Role[]>(() => adminApi.roles(), []);
  return (
    <div className="page">
      <PageHeader icon={ShieldCheck} title="Usuarios y roles" subtitle="Controla quién entra al sistema y qué puede hacer en cada catálogo. Por defecto, todo está denegado." />
      <Tabs<Tab>
        label="Secciones de seguridad"
        value={tab}
        onChange={setTab}
        items={[{ id: 'users', label: 'Usuarios', icon: Users }, { id: 'roles', label: 'Roles y permisos', icon: ShieldCheck }]}
      />
      {tab === 'users'
        ? <UsersTab roles={roles.data ?? []} />
        : <RolesTab roles={roles.data ?? []} loading={roles.loading} error={roles.error} reload={() => { roles.reload(); void reloadEntities(); }} />}
    </div>
  );
}
