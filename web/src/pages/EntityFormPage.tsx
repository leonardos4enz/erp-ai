import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, RefreshCw, Save, Trash2 } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { dataApi } from '../api/endpoints';
import type { FieldDef, Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog } from '../components/Dialog';
import { resolveIcon } from '../components/DynamicIcon';
import { ReferenceSelect } from '../components/ReferenceSelect';
import { useToast } from '../components/Toast';
import { Alert, Button, Field, Input, LoadingBlock, PageHeader, Select, Switch, Textarea } from '../components/ui';
import { activeFields, isRequiredFor, useEntityDef } from '../lib/entity';
import { formatDateTime, toDateInput, toDateTimeInput } from '../lib/format';
import { ForbiddenPage, NotFoundPage } from './StatusPages';

type Values = Record<string, unknown>;

function initialValues(fields: FieldDef[], row?: Row): Values {
  const v: Values = {};
  for (const f of fields) {
    const raw = row ? row[f.name] : undefined;
    switch (f.type) {
      case 'boolean': v[f.name] = row ? !!raw : false; break;
      case 'date': v[f.name] = toDateInput(raw); break;
      case 'datetime': v[f.name] = toDateTimeInput(raw); break;
      case 'integer':
      case 'decimal': v[f.name] = raw == null ? '' : String(raw); break;
      case 'reference':
        v[f.name] = raw == null ? null : Number(raw);
        v[`${f.name}__label`] = row?.[`${f.name}__label`] ?? null;
        break;
      default: v[f.name] = raw == null ? '' : String(raw);
    }
  }
  return v;
}

function toPayload(fields: FieldDef[], values: Values): Values {
  const out: Values = {};
  for (const f of fields) {
    const v = values[f.name];
    switch (f.type) {
      case 'boolean': out[f.name] = !!v; break;
      case 'integer': out[f.name] = v === '' || v == null ? null : Number.parseInt(String(v), 10); break;
      case 'decimal': out[f.name] = v === '' || v == null ? null : Number(String(v)); break;
      case 'datetime': out[f.name] = v ? new Date(String(v)).toISOString() : null; break;
      case 'reference': out[f.name] = v ?? null; break;
      default: out[f.name] = v === '' ? null : v;
    }
  }
  return out;
}

function isEmpty(f: FieldDef, v: unknown): boolean {
  if (f.type === 'boolean') return false;
  return v === null || v === undefined || (typeof v === 'string' && v.trim() === '');
}

export function EntityFormPage() {
  const { entity = '', id } = useParams();
  const isNew = !id;
  const navigate = useNavigate();
  const toast = useToast();
  const { me, perms } = useAuth();
  const { def, loading: defLoading } = useEntityDef(entity);
  const fields = useMemo(() => (def ? activeFields(def) : []), [def]);
  const p = perms(entity);

  const [row, setRow] = useState<Row | undefined>();
  const [values, setValues] = useState<Values>({});
  const [loadError, setLoadError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(!isNew);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});
  const [conflict, setConflict] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);

  async function load() {
    if (!def || isNew || !id) return;
    setLoading(true);
    setLoadError(null);
    try {
      const r = await dataApi.get(def.name, id);
      setRow(r);
      setValues(initialValues(fields, r));
      setConflict(false);
      setError(null);
    } catch (e) {
      setLoadError(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (!def) return;
    if (isNew) { setValues(initialValues(fields)); setLoading(false); }
    else void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [def?.name, id]);

  if (defLoading) return <LoadingBlock />;
  if (!def) return <NotFoundPage />;
  if (!p.read || (isNew && !p.create)) return <ForbiddenPage />;
  if (loading) return <LoadingBlock />;
  if (loadError) return loadError.status === 404 ? <NotFoundPage /> : loadError.status === 403 ? <ForbiddenPage /> : (
    <div className="page page-narrow"><Alert tone="danger" title={loadError.title}>{loadError.detail}</Alert></div>
  );

  const readOnly = !isNew && !p.update;
  const roles = me?.roles ?? [];
  const listPath = `/c/${encodeURIComponent(def.name)}`;
  const Icon = resolveIcon(def.icon);

  function set(name: string, v: unknown) {
    setValues((s) => ({ ...s, [name]: v }));
    if (clientErrors[name]) setClientErrors((e) => { const n = { ...e }; delete n[name]; return n; });
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (!def || readOnly) return;
    const errs: Record<string, string> = {};
    for (const f of fields) {
      if (isRequiredFor(f, roles) && isEmpty(f, values[f.name])) errs[f.name] = 'Este campo es obligatorio';
      if ((f.type === 'integer' || f.type === 'decimal') && values[f.name] !== '' && Number.isNaN(Number(values[f.name]))) errs[f.name] = 'Debe ser un número';
      if (f.maxLength && typeof values[f.name] === 'string' && (values[f.name] as string).length > f.maxLength) errs[f.name] = `Máximo ${f.maxLength} caracteres`;
    }
    setClientErrors(errs);
    if (Object.keys(errs).length) {
      document.getElementById(`f-${Object.keys(errs)[0]}`)?.focus();
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const payload = toPayload(fields, values);
      if (isNew) {
        const created = await dataApi.create(def.name, payload);
        toast.success('Registro creado');
        navigate(`${listPath}/${created.id}`, { replace: true });
      } else {
        const updated = await dataApi.update(def.name, id!, { ...payload, _rowVersion: row?._rowVersion });
        setRow(updated);
        setValues(initialValues(fields, updated));
        toast.success('Cambios guardados');
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) setConflict(true);
      else if (err instanceof ApiError) setError(err);
      else toast.fromError(err);
    } finally {
      setSaving(false);
    }
  }

  async function onDelete() {
    if (!def || !id) return;
    setDeleting(true);
    try {
      await dataApi.remove(def.name, id);
      toast.success('Registro eliminado');
      navigate(listPath, { replace: true });
    } catch (err) {
      toast.fromError(err);
      setDeleting(false);
      setConfirmDelete(false);
    }
  }

  function renderInput(f: FieldDef) {
    const fid = `f-${f.name}`;
    const required = isRequiredFor(f, roles);
    const err = clientErrors[f.name] ?? fieldError(error, f.name);
    const v = values[f.name];
    const common = { id: fid, disabled: readOnly || saving };
    const roleHint = !f.required && required ? 'Obligatorio para tu rol' : undefined;

    switch (f.type) {
      case 'longtext':
        return <Textarea key={f.name} {...common} wrapClassName="span-2" label={f.label} required={required} error={err} hint={roleHint} value={String(v ?? '')} maxLength={f.maxLength} onChange={(e) => set(f.name, e.target.value)} />;
      case 'boolean':
        return (
          <Field key={f.name} label={f.label} required={false} error={err} htmlFor={fid}>
            <div style={{ height: 36, display: 'flex', alignItems: 'center' }}>
              <Switch {...common} checked={!!v} onChange={(c) => set(f.name, c)} label={v ? 'Sí' : 'No'} />
            </div>
          </Field>
        );
      case 'integer':
      case 'decimal':
        return <Input key={f.name} {...common} label={f.label} required={required} error={err} hint={roleHint} type="number" inputMode={f.type === 'integer' ? 'numeric' : 'decimal'} step={f.type === 'integer' ? 1 : 'any'} value={String(v ?? '')} onChange={(e) => set(f.name, e.target.value)} />;
      case 'date':
        return <Input key={f.name} {...common} label={f.label} required={required} error={err} hint={roleHint} type="date" value={String(v ?? '')} onChange={(e) => set(f.name, e.target.value)} />;
      case 'datetime':
        return <Input key={f.name} {...common} label={f.label} required={required} error={err} hint={roleHint} type="datetime-local" value={String(v ?? '')} onChange={(e) => set(f.name, e.target.value)} />;
      case 'choice':
        return (
          <Select key={f.name} {...common} label={f.label} required={required} error={err} hint={roleHint} value={String(v ?? '')} onChange={(e) => set(f.name, e.target.value)}>
            <option value="">{required ? 'Selecciona…' : '— Ninguno —'}</option>
            {(f.options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
          </Select>
        );
      case 'reference':
        return (
          <Field key={f.name} label={f.label} required={required} error={err} hint={roleHint} htmlFor={fid}>
            <ReferenceSelect
              id={fid}
              entity={f.referenceEntity ?? ''}
              value={(v as number | null) ?? null}
              label={(values[`${f.name}__label`] as string | null) ?? null}
              invalid={!!err}
              disabled={readOnly || saving}
              describedBy={err ? `${fid}-err` : undefined}
              onChange={(nid, nlabel) => { set(f.name, nid); set(`${f.name}__label`, nlabel); }}
            />
          </Field>
        );
      default:
        return <Input key={f.name} {...common} label={f.label} required={required} error={err} hint={roleHint} value={String(v ?? '')} maxLength={f.maxLength} onChange={(e) => set(f.name, e.target.value)} />;
    }
  }

  const generalError = error && Object.keys(error.fieldErrors).length === 0 ? error : null;

  return (
    <div className="page page-narrow">
      <PageHeader
        icon={Icon}
        title={isNew ? `Nuevo ${def.label.toLowerCase()}` : `${def.label} #${id}`}
        subtitle={!isNew && row ? `Creado ${formatDateTime(row.createdAt)}${row.createdBy ? ` por ${row.createdBy}` : ''}${row.updatedAt ? ` · Actualizado ${formatDateTime(row.updatedAt)}${row.updatedBy ? ` por ${row.updatedBy}` : ''}` : ''}` : def.description}
        actions={<Button variant="ghost" icon={ArrowLeft} onClick={() => navigate(listPath)}>Volver</Button>}
      />

      <form className="card" onSubmit={onSubmit} noValidate>
        <div className="card-pad stack">
          {conflict && (
            <Alert
              tone="warning"
              title="Otro usuario modificó este registro"
              action={<Button size="sm" icon={RefreshCw} onClick={() => void load()}>Recargar</Button>}
            >
              Tus cambios no se guardaron. Recarga para ver la versión actual y vuelve a aplicar tus cambios.
            </Alert>
          )}
          {generalError && <Alert tone="danger" title={generalError.title}>{generalError.detail}</Alert>}
          {error && Object.keys(error.fieldErrors).length > 0 && (
            <Alert tone="danger" title="Revisa los campos marcados" />
          )}
          {readOnly && <Alert tone="info" title="Solo lectura">Tu rol no tiene permiso para editar este catálogo.</Alert>}
          {fields.length === 0
            ? <p className="muted">Este catálogo no tiene campos activos.</p>
            : <div className="form-grid">{fields.map(renderInput)}</div>}
        </div>
        {!readOnly && (
          <div className="dialog-footer">
            {!isNew && p.delete && (
              <Button variant="ghost" icon={Trash2} onClick={() => setConfirmDelete(true)} style={{ marginRight: 'auto', color: 'var(--danger)' }}>Eliminar</Button>
            )}
            <Button onClick={() => navigate(listPath)} disabled={saving}>Cancelar</Button>
            <Button type="submit" variant="primary" icon={Save} loading={saving}>{isNew ? 'Crear' : 'Guardar cambios'}</Button>
          </div>
        )}
      </form>

      <ConfirmDialog
        open={confirmDelete}
        danger
        title="Eliminar registro"
        message={<>El registro <strong>#{id}</strong> dejará de aparecer en {def.labelPlural.toLowerCase()}. La acción queda registrada en la auditoría.</>}
        confirmLabel="Eliminar"
        loading={deleting}
        onConfirm={onDelete}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}
