import { useState } from 'react';
import { ArrowRightLeft, Building2, Pencil, Plus, Save, Trash2 } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { companiesApi, type CompanyInput } from '../api/endpoints';
import type { Company, ExchangeRate } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog, Dialog } from '../components/Dialog';
import { ImageUpload } from '../components/ImageUpload';
import { useToast } from '../components/Toast';
import { Alert, Badge, Button, EmptyState, Input, LoadingBlock, PageHeader, Switch, Tabs, Textarea } from '../components/ui';
import { formatDate, formatDateTime } from '../lib/format';
import { useAsync, useDebounced } from '../lib/hooks';

type Tab = 'companies' | 'rates';

const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

/** Tipo de cambio con sus decimales significativos (hasta 8). */
const formatRate = (r: number) => r.toLocaleString('es-MX', { minimumFractionDigits: 2, maximumFractionDigits: 8 });

/** Spec 010: empresas y tipos de cambio. Son datos de este entorno: no viajan en los paquetes de Entornos. */
export function CompaniesPage() {
  const [tab, setTab] = useState<Tab>('companies');
  return (
    <div className="page">
      <PageHeader
        icon={Building2}
        title="Empresas"
        subtitle="Empresas del grupo, su moneda base y los tipos de cambio. Los catálogos «por empresa» guardan los registros de cada una por separado."
      />
      <Tabs
        label="Empresas y monedas"
        value={tab}
        onChange={setTab}
        items={[{ id: 'companies', label: 'Empresas', icon: Building2 }, { id: 'rates', label: 'Tipos de cambio', icon: ArrowRightLeft }]}
      />
      <div style={{ marginTop: 'var(--space-5)' }}>
        {tab === 'companies' ? <CompaniesTab /> : <RatesTab />}
      </div>
    </div>
  );
}

/* ----------------------------- Empresas ----------------------------- */

function CompaniesTab() {
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync((s) => companiesApi.list(s), [tick]);
  const [editing, setEditing] = useState<Company | 'new' | null>(null);

  return (
    <div className="stack">
      <div className="row" style={{ flexWrap: 'wrap' }}>
        <p className="muted grow" style={{ margin: 0 }}>
          Cada usuario trabaja en una empresa a la vez; las que tiene asignadas se eligen en <strong>Usuarios y roles</strong>.
        </p>
        <Button variant="primary" icon={Plus} onClick={() => setEditing('new')}>Nueva empresa</Button>
      </div>
      {loading && !data ? <LoadingBlock /> : error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar las empresas'} />
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Empresa</th><th>Moneda base</th><th>Usuarios</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {(data ?? []).map((c) => (
                  <tr key={c.id}>
                    <td>
                      <div className="row" style={{ gap: 'var(--space-3)' }}>
                        {c.logoUrl && <img className="company-logo" src={c.logoUrl} alt={`Logo de ${c.name}`} />}
                        <div><div style={{ fontWeight: 500 }}>{c.name}</div><div className="subtle mono" style={{ fontSize: 'var(--text-xs)' }}>{c.code}{c.taxId ? ` · RFC ${c.taxId}` : ''}</div></div>
                      </div>
                    </td>
                    <td className="mono">{c.baseCurrency}</td>
                    <td>{c.users}</td>
                    <td>
                      <div className="row" style={{ flexWrap: 'wrap', gap: 6 }}>
                        <Badge tone={c.isActive ? 'success' : 'neutral'}>{c.isActive ? 'Activa' : 'Inactiva'}</Badge>
                        {c.isDefault && <Badge tone="accent">Predeterminada</Badge>}
                      </div>
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <Button size="sm" variant="ghost" icon={Pencil} onClick={() => setEditing(c)} aria-label={`Editar ${c.name}`}>Editar</Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <CompanyDialog company={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); setTick((t) => t + 1); }}
        onLogo={(c) => { setEditing(c); setTick((t) => t + 1); }} />
    </div>
  );
}

function CompanyDialog({ company, onClose, onSaved, onLogo }: { company: Company | 'new' | null; onClose: () => void; onSaved: () => void; onLogo: (c: Company) => void }) {
  const toast = useToast();
  const { refresh, me } = useAuth();
  const isNew = company === 'new';
  const existing = company && company !== 'new' ? company : null;
  const [form, setForm] = useState<CompanyInput>({ code: '', name: '', baseCurrency: 'MXN', isActive: true });
  const [key, setKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<ApiError | null>(null);

  // Al abrir, el formulario toma los datos de la empresa elegida.
  const openKey = company === null ? null : isNew ? 'new' : `c${existing!.id}`;
  if (openKey !== key) {
    setKey(openKey);
    setErr(null);
    setForm(existing
      ? { code: existing.code, name: existing.name, baseCurrency: existing.baseCurrency, isActive: existing.isActive, taxId: existing.taxId ?? '', address: existing.address ?? '' }
      : { code: '', name: '', baseCurrency: 'MXN', isActive: true });
  }

  async function save() {
    setBusy(true);
    setErr(null);
    try {
      const saved = existing ? await companiesApi.update(existing.id, form) : await companiesApi.create(form);
      toast.success(existing ? 'Empresa actualizada' : 'Empresa creada', `${saved.name} · ${saved.baseCurrency}`);
      await refresh(); // el selector de empresa del superusuario cambia
      onSaved();
    } catch (e) {
      setErr(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setBusy(false);
    }
  }

  const set = (patch: Partial<CompanyInput>) => setForm((f) => ({ ...f, ...patch }));
  return (
    <Dialog
      open={company !== null}
      onClose={() => { if (!busy) onClose(); }}
      title={existing ? `Editar ${existing.name}` : 'Nueva empresa'}
      description={existing ? 'La clave no cambia. Cambiar la moneda base no recalcula los importes ya guardados.' : 'La clave identifica a la empresa en fórmulas (CURRENTCOMPANY()) y no se puede cambiar.'}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>Cancelar</Button>
          <Button variant="primary" icon={Save} loading={busy} onClick={save}>{existing ? 'Guardar cambios' : 'Crear empresa'}</Button>
        </>
      }
    >
      <form className="stack" onSubmit={(e) => { e.preventDefault(); void save(); }}>
        {err && !Object.keys(err.fieldErrors).length && <Alert tone="danger" title={err.title}>{err.detail}</Alert>}
        <Input label="Clave" required value={form.code ?? ''} disabled={!isNew} maxLength={20} className="mono"
          hint="De 2 a 20 letras sin acento, números, «_» o «-»; se guarda en mayúsculas."
          onChange={(e) => set({ code: e.target.value.toUpperCase() })} error={fieldError(err, 'code')} />
        <Input label="Nombre" required value={form.name} maxLength={150} onChange={(e) => set({ name: e.target.value })} error={fieldError(err, 'name')} />
        <Input label="Moneda base" required value={form.baseCurrency} maxLength={3} className="mono"
          hint="Código ISO de 3 letras (MXN, USD, EUR…). Es la moneda de BASECURRENCY() en las fórmulas."
          onChange={(e) => set({ baseCurrency: e.target.value.toUpperCase() })} error={fieldError(err, 'baseCurrency')} />
        <Switch checked={form.isActive} disabled={existing?.isDefault} onChange={(v) => set({ isActive: v })}
          label={existing?.isDefault ? 'Activa (la empresa predeterminada no se desactiva)' : 'Activa'} />
        {fieldError(err, 'isActive') && <Alert tone="danger" title={fieldError(err, 'isActive')} />}
        {/* Spec 027: datos que imprime el encabezado de los PDF. */}
        <Input label="RFC (identificación fiscal)" value={form.taxId ?? ''} maxLength={20} className="mono"
          hint="Lo imprime el encabezado de los documentos PDF." onChange={(e) => set({ taxId: e.target.value.toUpperCase() })} error={fieldError(err, 'taxId')} />
        <Textarea label="Domicilio" rows={3} value={form.address ?? ''} maxLength={500} hint="Una línea por renglón; sale en el encabezado de los PDF."
          onChange={(e) => set({ address: e.target.value })} error={fieldError(err, 'address')} />
        {existing && me?.isSuperuser && (
          <ImageUpload label="Logo" url={existing.logoUrl} alt={`Logo de ${existing.name}`}
            hint="PNG o JPEG, hasta 1 MB y 3000 px por lado. Se guarda al subirlo y sale en el encabezado de los PDF de esta empresa."
            removeMessage={`Los PDF de ${existing.name} dejarán de llevar logo. El archivo se conserva archivado.`}
            onUpload={async (file) => { const c = await companiesApi.saveLogo(existing.id, file); toast.success('Logo guardado', c.name); onLogo(c); }}
            onRemove={async () => { const c = await companiesApi.removeLogo(existing.id); toast.success('Logo quitado', c.name); onLogo(c); }} />
        )}
        <button type="submit" hidden aria-hidden tabIndex={-1} />
      </form>
    </Dialog>
  );
}

/* ----------------------------- Tipos de cambio ----------------------------- */

function RatesTab() {
  const toast = useToast();
  const { me } = useAuth();
  const [tick, setTick] = useState(0);
  const [filter, setFilter] = useState({ from: '', to: '' });
  const f = useDebounced(filter, 300);
  const valid = (c: string) => c === '' || /^[A-Z]{3}$/.test(c);
  const { data, loading, error } = useAsync(
    (s) => companiesApi.rates({ from: valid(f.from) ? f.from : '', to: valid(f.to) ? f.to : '' }, s), [tick, f.from, f.to]);
  const [form, setForm] = useState({ from: 'USD', to: me?.company?.baseCurrency ?? 'MXN', date: today(), rate: '' });
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<ApiError | null>(null);
  const [deleting, setDeleting] = useState<ExchangeRate | null>(null);
  const [removing, setRemoving] = useState(false);

  async function save() {
    setBusy(true);
    setErr(null);
    try {
      const r = await companiesApi.saveRate({ from: form.from, to: form.to, date: form.date, rate: Number(form.rate.replace(',', '.')) });
      toast.success('Tipo de cambio guardado', `1 ${r.from} = ${formatRate(r.rate)} ${r.to} · ${formatDate(r.date)}`);
      setForm((x) => ({ ...x, rate: '' }));
      setTick((t) => t + 1);
    } catch (e) {
      setErr(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!deleting) return;
    setRemoving(true);
    try {
      await companiesApi.removeRate(deleting.id);
      toast.success('Tipo de cambio eliminado', `${deleting.from} → ${deleting.to} · ${formatDate(deleting.date)}`);
      setDeleting(null);
      setTick((t) => t + 1);
    } catch (e) {
      toast.fromError(e, 'No se pudo eliminar');
    } finally {
      setRemoving(false);
    }
  }

  const set = (patch: Partial<typeof form>) => setForm((x) => ({ ...x, ...patch }));
  return (
    <div className="stack">
      <section className="card card-pad rate-form" aria-labelledby="rate-new">
        <h2 id="rate-new" className="section-title">Registrar tipo de cambio</h2>
        <p className="muted" style={{ marginTop: 0 }}>
          RATE() y CONVERT() usan el más reciente en o antes de la fecha del documento. El par inverso se calcula solo; si ya hay uno ese día, se reemplaza.
        </p>
        <form className="rate-fields" onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <Input label="De" required value={form.from} maxLength={3} className="mono" onChange={(e) => set({ from: e.target.value.toUpperCase() })} error={fieldError(err, 'from')} />
          <Input label="A" required value={form.to} maxLength={3} className="mono" onChange={(e) => set({ to: e.target.value.toUpperCase() })} error={fieldError(err, 'to')} />
          <Input label="Fecha" required type="date" value={form.date} onChange={(e) => set({ date: e.target.value })} error={fieldError(err, 'date')} />
          <Input label={`Tipo de cambio (${form.to || '…'} por 1 ${form.from || '…'})`} required inputMode="decimal" value={form.rate}
            onChange={(e) => set({ rate: e.target.value })} error={fieldError(err, 'rate')} />
          <div className="rate-submit"><Button type="submit" variant="primary" icon={Save} loading={busy}>Guardar</Button></div>
        </form>
        {err && !Object.keys(err.fieldErrors).length && <Alert tone="danger" title={err.title}>{err.detail}</Alert>}
      </section>

      <div className="row" style={{ flexWrap: 'wrap' }}>
        <h2 className="section-title grow" style={{ margin: 0 }}>Registrados</h2>
        <Input aria-label="Filtrar moneda de origen" placeholder="De" value={filter.from} maxLength={3} className="mono rate-filter"
          onChange={(e) => setFilter((x) => ({ ...x, from: e.target.value.toUpperCase() }))} />
        <Input aria-label="Filtrar moneda destino" placeholder="A" value={filter.to} maxLength={3} className="mono rate-filter"
          onChange={(e) => setFilter((x) => ({ ...x, to: e.target.value.toUpperCase() }))} />
      </div>
      {loading && !data ? <LoadingBlock /> : error ? (
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los tipos de cambio'} />
      ) : !data?.length ? (
        <div className="table-wrap">
          <EmptyState icon={ArrowRightLeft} title="Sin tipos de cambio"
            description="Registra el tipo de cambio de cada moneda extranjera; mientras no haya uno, RATE() queda vacío y el documento pide capturarlo." />
        </div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <thead><tr><th>Fecha</th><th>Par</th><th style={{ textAlign: 'right' }}>Tipo de cambio</th><th>Registró</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {data.map((r) => (
                  <tr key={r.id}>
                    <td style={{ whiteSpace: 'nowrap' }}>{formatDate(r.date)}</td>
                    <td className="mono" style={{ whiteSpace: 'nowrap' }}>{r.from} → {r.to}</td>
                    <td className="mono" style={{ textAlign: 'right' }}>{formatRate(r.rate)}</td>
                    <td>{r.updatedBy}<div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{formatDateTime(r.updatedAt)}</div></td>
                    <td style={{ textAlign: 'right' }}>
                      <Button size="sm" variant="ghost" iconOnly icon={Trash2} onClick={() => setDeleting(r)}
                        aria-label={`Eliminar ${r.from} → ${r.to} del ${formatDate(r.date)}`} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <ConfirmDialog
        open={!!deleting}
        danger
        title="Eliminar tipo de cambio"
        message={deleting ? `Se elimina ${deleting.from} → ${deleting.to} del ${formatDate(deleting.date)} (${formatRate(deleting.rate)}). Los documentos ya guardados no cambian; los nuevos usarán el anterior a esa fecha.` : ''}
        confirmLabel="Eliminar"
        loading={removing}
        onConfirm={() => void remove()}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
