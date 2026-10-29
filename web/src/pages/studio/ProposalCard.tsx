import { AlertTriangle, Ban, Braces, CheckCircle2, Download, FlaskConical, PackageCheck, Rocket, Trash2, XCircle } from 'lucide-react';
import type { StudioReport, StudioSession, StudioStep } from '../../api/types';
import { Alert, Badge, Button } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { PLAN_LABEL, reportNotes } from '../../lib/studio';
import { PlanPreviewView } from '../modeler/PlanReviewDialog';

/** R-033-11: lo que el agente dice haber probado, como afirmación suya, comparada con los pasos de su turno. */
export function ReportView({ report, steps, id }: { report: StudioReport; steps: StudioStep[]; id: string }) {
  if (report.tested.length === 0 && report.failures.length === 0 && report.risks.length === 0) return null;
  const notes = reportNotes(report, steps);
  return (
    <section className="chat-report" aria-labelledby={id}>
      <h3 id={id} className="chat-report-title"><FlaskConical size={14} aria-hidden /> Lo que el agente informa</h3>
      <p className="chat-muted">Es una afirmación del agente: compárala con sus herramientas.</p>
      {notes.map((n) => <Alert key={n} tone="warning" icon={AlertTriangle} title={n} />)}
      {report.tested.length > 0 && (
        <>
          <h4 className="chat-report-sub">Pruebas</h4>
          <ul className="chat-tests">
            {report.tested.map((t, i) => (
              <li key={i} className={t.ok ? 'is-ok' : 'is-fail'}>
                {t.ok ? <CheckCircle2 size={15} aria-hidden /> : <XCircle size={15} aria-hidden />}
                <span className="sr-only">{t.ok ? 'Superada: ' : 'No superada: '}</span>
                <span>{t.description}{t.detail && <span className="chat-muted chat-test-detail">{t.detail}</span>}</span>
              </li>
            ))}
          </ul>
        </>
      )}
      {report.failures.length > 0 && (
        <>
          <h4 className="chat-report-sub">Fallas</h4>
          <ul className="chat-list">{report.failures.map((f, i) => <li key={i}>{f}</li>)}</ul>
        </>
      )}
      {report.risks.length > 0 && (
        <>
          <h4 className="chat-report-sub">Riesgos</h4>
          <ul className="chat-list">{report.risks.map((r, i) => <li key={i}>{r}</li>)}</ul>
        </>
      )}
    </section>
  );
}

/**
 * Spec 037 (H5): la propuesta en el chat. Solo la vigente tiene acciones; una que reemplazó una entrega posterior se ve
 * como tal. Mientras un turno corre no se aplica ni se descarta (la huella llega con la lectura completa al terminar).
 */
export function ProposalCard({ s, replaced, working, refreshed, busy, onApply, onDiscard, onDownload }: {
  s: StudioSession; replaced: boolean; working: boolean; refreshed: boolean; busy: string | null;
  onApply: () => void; onDiscard: () => void; onDownload?: () => void;
}) {
  const proposed = !replaced && s.planStatus === 'proposed';
  const state = replaced ? 'Reemplazada' : s.planStatus ? PLAN_LABEL[s.planStatus] : 'Propuesta';
  const tone = replaced ? 'neutral' : s.planStatus === 'applied' ? 'success' : s.planStatus === 'proposed' ? 'accent' : 'neutral';
  return (
    <article className={`chat-proposal${replaced ? ' is-replaced' : ''}`} aria-label={replaced ? 'Propuesta reemplazada' : 'Propuesta'}>
      <header className="chat-proposal-head">
        <PackageCheck size={16} aria-hidden />
        <span className="chat-proposal-title">{replaced ? 'Propuesta anterior' : s.plan?.description ?? 'Propuesta'}</span>
        <Badge tone={tone}>{state}</Badge>
      </header>
      {replaced ? (
        <p className="chat-muted">Una entrega posterior de esta conversación la reemplazó: revisa y aplica la más reciente.</p>
      ) : (
        <>
          {s.planStatus === 'applied' && (
            <Alert tone="info" icon={CheckCircle2} title="Propuesta aplicada">
              {s.changeSetId ? `Conjunto de cambios #${s.changeSetId}` : null}{s.appliedAt ? ` · el ${formatDateTime(s.appliedAt)}` : null}
            </Alert>
          )}
          {s.planStatus === 'discarded' && <p className="chat-muted">Descartaste esta propuesta. Puedes pedir otra en la conversación.</p>}
          {proposed && s.warnings.length > 0 && (
            <Alert tone="warning" icon={AlertTriangle} title="Revisa con atención">
              <ul className="import-errors">{s.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
            </Alert>
          )}
          {proposed && refreshed && (
            <Alert tone="info" title="La propuesta se actualizó">El modelo cambió desde tu revisión: revisa la vista previa de nuevo antes de aplicarla.</Alert>
          )}
          {proposed && s.preview && !working && (
            <PlanPreviewView preview={s.preview} validText="Si la apruebas, se aplica en una sola transacción. Si un paso falla, no se aplica ninguno." />
          )}
          {s.plan && (
            <details className="assistant-json">
              <summary><Braces size={14} aria-hidden /> Ver plan en JSON ({s.plan.operations.length} operaciones)</summary>
              <pre className="code-block" tabIndex={0} aria-label="Plan de cambios en JSON">{JSON.stringify(s.plan, null, 2)}</pre>
            </details>
          )}
          {proposed && s.blockedReason && (
            <Alert tone="warning" icon={Ban} title="No se aplica en este entorno">{s.blockedReason} Descarga el plan para aplicarlo en un entorno de desarrollo y exportarlo como paquete.</Alert>
          )}
          {proposed && working && <p className="chat-muted">El agente está trabajando: la propuesta se revisa cuando termine.</p>}
          {proposed && (
            <div className="chat-proposal-actions">
              {onDownload && <Button size="sm" icon={Download} onClick={onDownload}>Descargar plan</Button>}
              <Button size="sm" icon={Trash2} loading={busy === 'discard'} disabled={working} onClick={onDiscard}>Descartar</Button>
              {s.canApply && !working && (
                <Button size="sm" variant="primary" icon={Rocket} loading={busy === 'apply'} onClick={onApply}>Revisar y aplicar…</Button>
              )}
            </div>
          )}
        </>
      )}
    </article>
  );
}
