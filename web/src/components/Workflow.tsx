import { useEffect, useState } from 'react';
import { ArrowRight, History, Lock } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { dataApi } from '../api/endpoints';
import type { EntityDef, FieldDef, HistoryEntry, RecordTransition, RecordWorkflow, Row } from '../api/types';
import { formatDateTime } from '../lib/format';
import { StateBadge } from '../lib/entity';
import { rowToValues, type Values } from '../lib/draft';
import { ConfirmDialog, Dialog } from './Dialog';
import { DynamicIcon } from './DynamicIcon';
import { FieldInput } from './FieldInput';
import { Alert, Button, EmptyState, Field, LoadingBlock, Textarea } from './ui';
import { useToast } from './Toast';

export function stateLabel(def: EntityDef, state: string): string {
  return def.workflow?.states.find((s) => s.name === state)?.label ?? state;
}

export interface WorkflowBarProps {
  workflow: RecordWorkflow;
  /** Razón para bloquear todas las transiciones (p. ej. cambios sin guardar). */
  blockedReason?: string;
  onTransition: (t: RecordTransition) => void;
}

/** Estado actual + botones de las transiciones disponibles. */
export function WorkflowBar({ workflow, blockedReason, onTransition }: WorkflowBarProps) {
  const toast = useToast();
  return (
    <div className="workflow-bar" role="group" aria-label="Proceso del documento">
      <div className="row workflow-state">
        <span className="subtle">Estado</span>
        <StateBadge label={workflow.stateLabel} color={workflow.color} />
        {!workflow.editable && <Lock size={14} className="subtle" aria-label="No editable" />}
      </div>
      <div className="workflow-actions">
        {workflow.transitions.map((t) => {
          const reason = blockedReason ?? (t.allowed ? undefined : t.reason ?? 'No disponible para tu rol.');
          const variant = t.variant === 'danger' ? 'danger' : t.variant === 'primary' ? 'primary' : 'secondary';
          return (
            <Button
              key={t.name}
              size="sm"
              variant={reason ? 'secondary' : variant}
              className={reason ? 'btn-blocked' : undefined}
              aria-disabled={reason ? true : undefined}
              title={reason}
              onClick={() => (reason ? toast.info(t.label, reason) : onTransition(t))}
            >
              {t.icon ? <DynamicIcon name={t.icon} size={15} /> : null}
              {t.label}
            </Button>
          );
        })}
      </div>
    </div>
  );
}

export interface TransitionDialogProps {
  def: EntityDef;
  row: Row;
  transition: RecordTransition | null;
  onClose: () => void;
  onDone: (row: Row) => void;
  onConflict: () => void;
}

/** Ejecuta una transición; pide comentario o campos obligatorios si la transición lo requiere. */
export function TransitionDialog({ def, row, transition: t, onClose, onDone, onConflict }: TransitionDialogProps) {
  const toast = useToast();
  const [values, setValues] = useState<Values>({});
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});

  const fields: FieldDef[] = t ? t.requiredFields.map((n) => def.fields.find((f) => f.name === n)).filter((f): f is FieldDef => !!f) : [];
  const needsInput = !!t && (t.requireComment || fields.length > 0);

  useEffect(() => {
    if (!t) return;
    setValues(rowToValues(fields, row));
    setComment('');
    setError(null);
    setClientErrors({});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [t?.name, row._rowVersion]);

  if (!t) return null;
  const toLabel = stateLabel(def, t.to);

  async function run() {
    if (!t) return;
    const errs: Record<string, string> = {};
    if (t.requireComment && !comment.trim()) errs.comment = 'Escribe un comentario';
    for (const f of fields) {
      const v = values[f.name];
      if (f.type !== 'boolean' && (v == null || (typeof v === 'string' && !v.trim()))) errs[f.name] = 'Este campo es obligatorio';
    }
    setClientErrors(errs);
    if (Object.keys(errs).length) return;

    const payload: Values = {};
    for (const f of fields) {
      const v = values[f.name];
      payload[f.name] = f.type === 'integer' || f.type === 'decimal' ? (v === '' ? null : Number(v))
        : f.type === 'datetime' ? (v ? new Date(String(v)).toISOString() : null)
        : v === '' ? null : v;
    }
    setBusy(true);
    setError(null);
    try {
      const updated = await dataApi.transition(def.name, row.id, {
        transition: t.name,
        comment: comment.trim() || undefined,
        values: fields.length ? payload : undefined,
        _rowVersion: row._rowVersion,
      });
      const wf = updated._workflow as RecordWorkflow | undefined;
      toast.success(`Estado: ${wf?.stateLabel ?? toLabel}`);
      onDone(updated);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) { onConflict(); onClose(); }
      else if (e instanceof ApiError && e.status === 400) setError(e);
      else { toast.fromError(e); onClose(); }
    } finally {
      setBusy(false);
    }
  }

  if (!needsInput) {
    return (
      <ConfirmDialog
        open
        title={t.label}
        danger={t.variant === 'danger'}
        message={<>El documento pasará al estado <strong>{toLabel}</strong>. El cambio queda registrado en el historial.{error && <><br /><span className="field-error">{error.title}</span></>}</>}
        confirmLabel={t.label}
        loading={busy}
        onConfirm={run}
        onCancel={onClose}
      />
    );
  }

  const generalError = error && Object.keys(error.fieldErrors).length === 0 ? error : null;
  const commentErr = clientErrors.comment ?? fieldError(error, 'comment');
  return (
    <Dialog
      open
      onClose={() => { if (!busy) onClose(); }}
      title={t.label}
      description={<>El documento pasará al estado <strong>{toLabel}</strong>.</>}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>Cancelar</Button>
          <Button variant={t.variant === 'danger' ? 'danger' : 'primary'} loading={busy} onClick={run}>{t.label}</Button>
        </>
      }
    >
      <div className="stack">
        {generalError && <Alert tone="danger" title={generalError.title}>{generalError.detail}</Alert>}
        {fields.map((f) => (
          <FieldInput
            key={f.name}
            field={f}
            def={def}
            id={`t-${f.name}`}
            value={values[f.name]}
            refLabel={values[`${f.name}__label`] as string | null}
            onChange={(v, label) => setValues((s) => ({ ...s, [f.name]: v, ...(label !== undefined ? { [`${f.name}__label`]: label } : {}) }))}
            required
            disabled={busy}
            error={clientErrors[f.name] ?? fieldError(error, f.name)}
          />
        ))}
        {t.requireComment ? (
          <Textarea
            id="t-comment"
            label="Comentario"
            required
            rows={3}
            maxLength={1000}
            value={comment}
            error={commentErr}
            disabled={busy}
            data-autofocus
            onChange={(e) => setComment(e.target.value)}
          />
        ) : (
          <Field label="Comentario" hint="Opcional" htmlFor="t-comment">
            <textarea id="t-comment" className="textarea" rows={2} maxLength={1000} value={comment} disabled={busy} onChange={(e) => setComment(e.target.value)} />
          </Field>
        )}
      </div>
    </Dialog>
  );
}

/** Línea de tiempo de transiciones del documento. */
export function HistoryPanel({ entity, id, version }: { entity: string; id: number | string; version: string | undefined }) {
  const [items, setItems] = useState<HistoryEntry[] | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(false);
    dataApi.history(entity, id, ctrl.signal)
      .then((r) => { if (!ctrl.signal.aborted) setItems(r); })
      .catch(() => { if (!ctrl.signal.aborted) setError(true); });
    return () => ctrl.abort();
  }, [entity, id, version]);

  return (
    <section className="card card-pad stack" aria-labelledby="history-title">
      <h2 id="history-title" className="section-title"><History size={16} aria-hidden /> Historial</h2>
      {error ? (
        <p className="muted">No se pudo cargar el historial.</p>
      ) : items === null ? (
        <LoadingBlock />
      ) : items.length === 0 ? (
        <EmptyState icon={History} title="Sin movimientos" description="Los cambios de estado aparecerán aquí." />
      ) : (
        <ol className="timeline">
          {items.map((h, i) => (
            <li key={i} className="timeline-item">
              <span className="timeline-dot" aria-hidden />
              <div className="grow">
                <div className="row timeline-head">
                  <strong>{h.transitionLabel}</strong>
                  <span className="row subtle timeline-states">
                    {h.fromLabel ?? 'Inicio'} <ArrowRight size={13} aria-label="a" /> {h.toLabel}
                  </span>
                </div>
                <div className="subtle timeline-meta">{h.user} · {formatDateTime(h.at)}</div>
                {h.comment && <p className="timeline-comment">{h.comment}</p>}
              </div>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
