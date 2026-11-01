import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ArrowDown, ArrowUp, LayoutDashboard, Pencil, Plus, Save, Share2, Trash2 } from 'lucide-react';
import { ApiError } from '../api/client';
import { myDashboardsApi, myReportsApi, reportsApi } from '../api/endpoints';
import type { PersonalDashboardResult, PersonalReportSummary, PersonalWidget, ReportSummary } from '../api/types';
import { ConfirmDialog } from '../components/Dialog';
import { ShareDialog } from '../components/ShareDialog';
import { useToast } from '../components/Toast';
import { Alert, Button, EmptyState, Input, LoadingBlock, PageHeader, Select, Switch, Textarea } from '../components/ui';
import { useAsync } from '../lib/hooks';
import { WidgetBody } from './DashboardsPage';
import { NotFoundPage } from './StatusPages';

const TYPES: Record<PersonalWidget['type'], string> = { kpi: 'Indicador', chart: 'Gráfica', table: 'Tabla' };
const MAX_WIDGETS = 20;

type Draft = { label: string; description: string; widgets: PersonalWidget[] };

/** Valor del selector de reporte de una tarjeta: `m:Catalogo/Reporte` (del modelo) o `p:id` (mío). */
function sourceOf(w: PersonalWidget): string {
  if (w.personalReport) return `p:${w.personalReport}`;
  return w.entity && w.report ? `m:${w.entity}/${w.report}` : '';
}

function withSource(w: PersonalWidget, value: string): PersonalWidget {
  if (value.startsWith('p:')) return { type: w.type, label: w.label, wide: w.wide, personalReport: Number(value.slice(2)) };
  if (value.startsWith('m:')) {
    const [entity, report] = value.slice(2).split('/');
    return { type: w.type, label: w.label, wide: w.wide, entity, report };
  }
  return { type: w.type, label: w.label, wide: w.wide };
}

/**
 * Spec 042: tablero personal. `/my-dashboards` arma uno nuevo; `/my-dashboards/:id` lo abre (el dueño lo edita, borra y
 * comparte; a quien se lo compartieron, solo lectura). Cada tarjeta corre en el servidor con los permisos de quien lo ve.
 */
export function PersonalDashboardPage() {
  const { id: idParam } = useParams();
  const id = idParam ? Number(idParam) : undefined;
  const navigate = useNavigate();
  const toast = useToast();
  const { data, loading, error, reload, setData } = useAsync<PersonalDashboardResult | null>(
    (s) => (id ? myDashboardsApi.get(id, s) : Promise.resolve(null)), [id]);
  const [draft, setDraft] = useState<Draft | null>(() => (id ? null : { label: '', description: '', widgets: [{ type: 'kpi' }] }));
  const [busy, setBusy] = useState(false);
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [shareOpen, setShareOpen] = useState(false);
  const editButton = useRef<HTMLButtonElement>(null);
  const shareButton = useRef<HTMLButtonElement>(null);

  // Sin id, el editor de uno nuevo; al abrir otro tablero, se cierra el editor.
  useEffect(() => {
    setSaveError(null);
    setDraft(id ? null : { label: '', description: '', widgets: [{ type: 'kpi' }] });
  }, [id]);

  if (id && error instanceof ApiError && error.status === 404) return <NotFoundPage />;
  if (id && loading && !data) return <LoadingBlock />;
  if (id && (error || !data)) {
    return (
      <div className="page">
        <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo cargar el tablero'}
          action={<Button size="sm" onClick={reload}>Reintentar</Button>} />
      </div>
    );
  }

  async function save() {
    if (!draft) return;
    setBusy(true);
    setSaveError(null);
    try {
      const desc = draft.description.trim() || undefined;
      const r = data
        ? await myDashboardsApi.update(data.id, draft.label, desc, draft.widgets, data.version)
        : await myDashboardsApi.create(draft.label, desc, draft.widgets);
      toast.success(data ? 'Cambios guardados' : 'Tablero guardado', r.label);
      // Uno nuevo cambia de dirección y el efecto de `id` cierra el editor; sin datos todavía no hay tablero que mostrar.
      if (r.id !== id) navigate(`/my-dashboards/${r.id}`, { replace: true });
      else { setDraft(null); reload(); }
      window.setTimeout(() => editButton.current?.focus(), 0);
    } catch (e) {
      if (e instanceof ApiError) setSaveError(e); else toast.fromError(e);
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!data) return;
    setBusy(true);
    try {
      await myDashboardsApi.remove(data.id, data.version);
      toast.success('Tablero borrado', data.label);
      navigate('/dashboards', { replace: true });
    } catch (e) {
      setDeleting(false);
      if (e instanceof ApiError) setSaveError(e); else toast.fromError(e);
    } finally {
      setBusy(false);
    }
  }

  const errorAlert = saveError && (
    <Alert tone="danger" title={saveError.status === 409 ? 'Otra pestaña cambió este tablero' : saveError.title}
      action={saveError.status === 409 && data ? <Button size="sm" onClick={() => window.location.reload()}>Volver a abrir</Button> : undefined}>
      {saveError.status === 409
        ? 'Vuelve a abrirlo para ver la versión actual; lo que cambiaste aquí no se guardó.'
        : Object.values(saveError.fieldErrors).flat().length
          ? <ul className="plain-list">{Object.values(saveError.fieldErrors).flat().map((m) => <li key={m}>{m}</li>)}</ul>
          : undefined}
    </Alert>
  );

  if (draft) {
    return (
      <div className="page">
        <PageHeader icon={LayoutDashboard} title={data ? `Editar ${data.label}` : 'Nuevo tablero'}
          subtitle="Junta reportes del modelo y tuyos. Cada tarjeta corre con los registros y campos de quien mira el tablero."
          actions={<div className="report-actions">
            <Button onClick={() => {
              setSaveError(null);
              if (!data) { navigate('/dashboards'); return; }
              setDraft(null);
              window.setTimeout(() => editButton.current?.focus(), 0); // F-03: vuelve a «Editar»
            }} disabled={busy}>Cancelar</Button>
            <Button variant="primary" icon={Save} loading={busy} onClick={save}>Guardar</Button>
          </div>} />
        {errorAlert}
        <Editor draft={draft} onChange={setDraft} />
      </div>
    );
  }

  if (!data) return <LoadingBlock />;
  const d = data;
  return (
    <div className="page">
      <PageHeader icon={LayoutDashboard} title={d.label}
        subtitle={d.owned
          ? d.roles?.length ? `Tablero personal compartido con ${d.roles.join(', ')}.` : d.description ?? 'Tablero personal: solo tú lo ves.'
          : `Compartido por ${d.owner}: cada tarjeta muestra los registros y campos que tú puedes ver.`}
        actions={d.owned && (
          <div className="report-actions">
            <Button ref={editButton} icon={Pencil} onClick={() => {
              setSaveError(null);
              setDraft({ label: d.label, description: d.description ?? '', widgets: d.widgets ?? [] });
            }}>Editar</Button>
            <Button ref={shareButton} icon={Share2} onClick={() => setShareOpen(true)} aria-haspopup="dialog">
              Compartir{d.roles?.length ? ` (${d.roles.length})` : ''}
            </Button>
            <Button variant="ghost" iconOnly icon={Trash2} aria-label="Borrar el tablero" onClick={() => setDeleting(true)} />
          </div>
        )} />
      {errorAlert}
      {d.owned && d.description && d.roles?.length ? <p className="subtle">{d.description}</p> : null}
      {d.cards.length === 0 ? (
        <div className="card"><EmptyState icon={LayoutDashboard} title="Ninguna tarjeta está disponible para ti"
          description="Las tarjetas de este tablero usan catálogos o campos que tu rol no puede ver." /></div>
      ) : (
        <div className="dashboard-grid">
          {d.cards.map((c, i) => {
            const titleId = `pw-${i}`;
            return (
              <section key={i} className={`card widget${c.wide ? ' wide' : ''}`} aria-labelledby={titleId}>
                <h2 id={titleId} className="widget-title">{c.link ? <Link to={c.link}>{c.label}</Link> : c.label}</h2>
                {c.available && c.result
                  ? <WidgetBody type={c.type} label={c.label} result={c.result} />
                  : <Alert tone="warning" title="No disponible">{c.problems.join(' ')}</Alert>}
              </section>
            );
          })}
        </div>
      )}
      {d.owned && (
        <ShareDialog open={shareOpen} what="el tablero" shareable={d.shareableRoles ?? []} current={d.roles ?? []}
          onClose={() => { setShareOpen(false); window.setTimeout(() => shareButton.current?.focus(), 0); }}
          onSave={async (roles) => {
            const r = await myDashboardsApi.share(d.id, roles, d.version);
            setData({ ...d, version: r.version, roles: r.roles });
            setShareOpen(false);
            window.setTimeout(() => shareButton.current?.focus(), 0);
            toast.success(r.roles.length ? 'Tablero compartido' : 'Tablero sin compartir', r.roles.join(', ') || d.label);
            return r;
          }} />
      )}
      <ConfirmDialog open={deleting} danger title="Borrar el tablero" message={`¿Borrar «${d.label}»? No se puede deshacer.`} confirmLabel="Borrar"
        loading={busy} onCancel={() => setDeleting(false)} onConfirm={remove} />
    </div>
  );
}

/** Nombre, descripción y tarjetas. Los reportes que ofrece son los del modelo que veo y los míos. */
function Editor({ draft, onChange }: { draft: Draft; onChange: (d: Draft) => void }) {
  const model = useAsync<ReportSummary[]>((s) => reportsApi.list(s), []);
  const mine = useAsync<PersonalReportSummary[]>((s) => myReportsApi.list(s), []);
  const ws = draft.widgets;
  const setWidget = (i: number, w: PersonalWidget) => onChange({ ...draft, widgets: ws.map((x, k) => (k === i ? w : x)) });
  const move = (i: number, by: number) => {
    const next = [...ws];
    [next[i], next[i + by]] = [next[i + by], next[i]];
    onChange({ ...draft, widgets: next });
  };

  return (
    <div className="stack">
      <section className="card card-pad stack" aria-label="Datos del tablero">
        <Input label="Nombre del tablero" required maxLength={100} value={draft.label} onChange={(e) => onChange({ ...draft, label: e.target.value })} autoFocus />
        {/* Revisión exploratoria 042 (F-03): al abrir el editor (nuevo o «Editar», que desaparece) el foco caía en body. */}
        <Textarea label="Descripción (opcional)" rows={2} maxLength={300} value={draft.description} onChange={(e) => onChange({ ...draft, description: e.target.value })} />
      </section>
      <section className="stack" aria-labelledby="pd-cards">
        <h2 id="pd-cards" className="section-title">Tarjetas ({ws.length} de {MAX_WIDGETS})</h2>
        {ws.map((w, i) => (
          <fieldset key={i} className="card card-pad stack card-editor">
            <legend className="field-label">Tarjeta {i + 1}</legend>
            <div className="card-editor-row">
              <Select label="Reporte" value={sourceOf(w)} onChange={(e) => setWidget(i, withSource(w, e.target.value))}>
                <option value="">Elige un reporte…</option>
                {!!mine.data?.length && (
                  <optgroup label="Mis reportes">
                    {mine.data.map((r) => <option key={r.id} value={`p:${r.id}`}>{r.label}{r.available ? '' : ' (no disponible)'}</option>)}
                  </optgroup>
                )}
                {!!model.data?.length && (
                  <optgroup label="Reportes del modelo">
                    {model.data.map((r) => <option key={`${r.entity}/${r.name}`} value={`m:${r.entity}/${r.name}`}>{r.label} · {r.entityLabel}</option>)}
                  </optgroup>
                )}
              </Select>
              <Select label="Tipo" value={w.type} onChange={(e) => setWidget(i, { ...w, type: e.target.value as PersonalWidget['type'] })}>
                {(Object.keys(TYPES) as PersonalWidget['type'][]).map((t) => <option key={t} value={t}>{TYPES[t]}</option>)}
              </Select>
            </div>
            <div className="card-editor-row">
              <Input label="Título (opcional)" maxLength={100} value={w.label ?? ''} placeholder="El nombre del reporte"
                onChange={(e) => setWidget(i, { ...w, label: e.target.value || undefined })} />
              <Switch label="Ancho completo" checked={!!w.wide} onChange={(v) => setWidget(i, { ...w, wide: v || undefined })} />
            </div>
            <div className="row">
              <Button size="sm" variant="ghost" iconOnly icon={ArrowUp} aria-label={`Subir la tarjeta ${i + 1}`} disabled={i === 0} onClick={() => move(i, -1)} />
              <Button size="sm" variant="ghost" iconOnly icon={ArrowDown} aria-label={`Bajar la tarjeta ${i + 1}`} disabled={i === ws.length - 1} onClick={() => move(i, 1)} />
              <span className="spacer" />
              <Button size="sm" variant="ghost" icon={Trash2} disabled={ws.length === 1}
                onClick={() => onChange({ ...draft, widgets: ws.filter((_, k) => k !== i) })}>Quitar la tarjeta {i + 1}</Button>
            </div>
          </fieldset>
        ))}
        {ws.length < MAX_WIDGETS && (
          <div><Button icon={Plus} onClick={() => onChange({ ...draft, widgets: [...ws, { type: 'chart' }] })}>Agregar tarjeta</Button></div>
        )}
        <p className="field-hint">Un indicador usa un reporte sin agrupación (o agrupado por una fecha con periodo, para compararlo con el anterior); una gráfica, uno con agrupación; una tabla, cualquiera.</p>
      </section>
    </div>
  );
}
