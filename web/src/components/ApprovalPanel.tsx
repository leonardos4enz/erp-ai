import { useState } from 'react';
import { CheckCircle2, Circle, CircleDot, MinusCircle, ShieldCheck, XCircle, type LucideIcon } from 'lucide-react';
import { ApiError, fieldError } from '../api/client';
import { dataApi } from '../api/endpoints';
import type { ApprovalLevelStatus, EntityDef, RecordApproval, Row } from '../api/types';
import { formatDateTime } from '../lib/format';
import { Dialog } from './Dialog';
import { Alert, Badge, Button, Textarea } from './ui';
import { useToast } from './Toast';

const LEVEL: Record<ApprovalLevelStatus, { label: string; tone: 'neutral' | 'success' | 'danger' | 'warning' | 'accent'; icon: LucideIcon }> = {
  approved: { label: 'Autorizado', tone: 'success', icon: CheckCircle2 },
  rejected: { label: 'Rechazado', tone: 'danger', icon: XCircle },
  current: { label: 'En curso', tone: 'warning', icon: CircleDot },
  pending: { label: 'Pendiente', tone: 'neutral', icon: Circle },
  skipped: { label: 'Saltado', tone: 'neutral', icon: MinusCircle },
};

const RUN: Record<RecordApproval['status'], { label: string; tone: 'neutral' | 'success' | 'danger' | 'warning' }> = {
  pending: { label: 'En autorización', tone: 'warning' },
  approved: { label: 'Autorizada', tone: 'success' },
  rejected: { label: 'Rechazada', tone: 'danger' },
  cancelled: { label: 'Cancelada', tone: 'neutral' },
  reset: { label: 'Reiniciada', tone: 'neutral' },
};

export interface ApprovalPanelProps {
  def: EntityDef;
  row: Row;
  approval: RecordApproval;
  /** Razón para no decidir ahora (p. ej. cambios sin guardar). */
  blockedReason?: string;
  onDone: (row: Row) => void;
  onConflict: () => void;
}

/**
 * Spec 053 (H3): el avance de la matriz de autorización del registro (regla, niveles, quién decidió y quién puede ahora) y, a quien
 * le toca, «Autorizar» y «Rechazar». La decisión viaja con las versiones del registro y de la autorización que se ven aquí.
 */
export function ApprovalPanel({ def, row, approval, blockedReason, onDone, onConflict }: ApprovalPanelProps) {
  const toast = useToast();
  const [deciding, setDeciding] = useState<'approve' | 'reject' | null>(null);
  const run = RUN[approval.status];

  return (
    <section className="card card-pad stack approval-panel" aria-labelledby="approval-title">
      <div className="row approval-head">
        <h2 id="approval-title" className="section-title grow"><ShieldCheck size={16} aria-hidden /> Autorización</h2>
        <Badge tone={run.tone}>{run.label}</Badge>
      </div>
      <p className="subtle approval-rule">Regla «{approval.rule}»</p>
      {approval.waitingReason && <Alert tone="warning" title="El documento espera">{approval.waitingReason}</Alert>}
      <ol className="timeline" aria-label="Niveles de autorización">
        {approval.levels.map((l) => {
          const s = LEVEL[l.status];
          return (
            <li key={l.level} className={`timeline-item approval-level approval-${l.status}`}>
              <s.icon size={14} className="approval-icon" aria-hidden />
              <div className="grow">
                <div className="row timeline-head">
                  <strong>Nivel {l.level} · {l.profile}</strong>
                  <Badge tone={s.tone}>{s.label}</Badge>
                </div>
                {l.reason && <div className="subtle timeline-meta">{l.reason}</div>}
                {l.decisions.map((d, i) => (
                  <div key={i}>
                    <div className="subtle timeline-meta">
                      {d.decision === 'approve' ? 'Autorizó' : 'Rechazó'} {d.user ?? '—'} · {formatDateTime(d.at)}
                    </div>
                    {d.comment && <p className="timeline-comment">{d.comment}</p>}
                  </div>
                ))}
                {l.status === 'current' && approvers(approval)}
              </div>
            </li>
          );
        })}
      </ol>
      {approval.canDecide && (
        <div className="row approval-actions" role="group" aria-label="Decidir la autorización">
          <Button variant="primary" icon={CheckCircle2} aria-disabled={blockedReason ? true : undefined} title={blockedReason}
            onClick={() => (blockedReason ? toast.info('Autorizar', blockedReason) : setDeciding('approve'))}>Autorizar</Button>
          <Button variant="danger" icon={XCircle} aria-disabled={blockedReason ? true : undefined} title={blockedReason}
            onClick={() => (blockedReason ? toast.info('Rechazar', blockedReason) : setDeciding('reject'))}>Rechazar</Button>
        </div>
      )}
      {deciding && (
        <DecisionDialog def={def} row={row} approval={approval} decision={deciding} onClose={() => setDeciding(null)}
          onDone={(r) => { setDeciding(null); onDone(r); }} onConflict={() => { setDeciding(null); onConflict(); }} />
      )}
    </section>
  );
}

function approvers(a: RecordApproval) {
  return a.approvers.length > 0
    ? <div className="subtle timeline-meta">Pueden autorizar: {a.approvers.join(', ')}</div>
    : null;
}

function DecisionDialog({ def, row, approval, decision, onClose, onDone, onConflict }: {
  def: EntityDef; row: Row; approval: RecordApproval; decision: 'approve' | 'reject';
  onClose: () => void; onDone: (row: Row) => void; onConflict: () => void;
}) {
  const toast = useToast();
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [missing, setMissing] = useState(false);
  const reject = decision === 'reject';
  const level = approval.levels.find((l) => l.level === approval.level);

  async function run() {
    if (reject && !comment.trim()) { setMissing(true); return; }
    setBusy(true);
    setError(null);
    try {
      const updated = await dataApi.decide(def.name, row.id, {
        decision, comment: comment.trim() || undefined, _rowVersion: row._rowVersion, approvalVersion: approval.version,
      });
      // Revisión adversarial 053 (F-13): si ya no le toca, deja de verlo; la respuesta solo dice que se guardó.
      toast.success(reject ? 'Documento rechazado' : 'Nivel autorizado',
        updated._gone ? 'Ya no te toca: el documento sale de tu lista.' : level ? `Nivel ${level.level} · ${level.profile}` : undefined);
      onDone(updated);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) { toast.fromError(e); onConflict(); }
      else if (e instanceof ApiError && (e.status === 400 || e.status === 403)) setError(e);
      else { toast.fromError(e); onClose(); }
    } finally {
      setBusy(false);
    }
  }

  const commentError = (missing && !comment.trim() ? 'Escribe el motivo del rechazo' : undefined) ?? fieldError(error, 'comment');
  return (
    <Dialog
      open
      onClose={() => { if (!busy) onClose(); }}
      title={reject ? 'Rechazar' : 'Autorizar'}
      description={reject
        ? <>El documento pasará a rechazado y la autorización termina aquí.</>
        : <>Autorizas el nivel {level?.level} ({level?.profile}) de la regla «{approval.rule}».</>}
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>Cancelar</Button>
          <Button variant={reject ? 'danger' : 'primary'} loading={busy} onClick={run}>{reject ? 'Rechazar' : 'Autorizar'}</Button>
        </>
      }
    >
      <div className="stack">
        {error && !fieldError(error, 'comment') && <Alert tone="danger" title={error.title}>{error.detail}</Alert>}
        <Textarea
          id="approval-comment"
          label={reject ? 'Motivo del rechazo' : 'Comentario'}
          hint={reject ? undefined : 'Opcional'}
          required={reject}
          rows={3}
          maxLength={1000}
          value={comment}
          error={commentError}
          disabled={busy}
          data-autofocus
          onChange={(e) => setComment(e.target.value)}
        />
      </div>
    </Dialog>
  );
}
