import { useEffect, useState } from 'react';
import { AlertCircle, CheckCircle2, Code2, Rocket } from 'lucide-react';
import { ApiError } from '../../api/client';
import { modelApi } from '../../api/endpoints';
import type { ChangePlan, PlanPreview } from '../../api/types';
import { Dialog } from '../../components/Dialog';
import { Alert, Button, LoadingBlock } from '../../components/ui';

export interface PlanReviewDialogProps {
  plan: ChangePlan | null;
  onClose: () => void;
  onApplied: (changeSetId: number) => void;
}

/** Shows the server-side preview (steps + SQL) of a change plan and applies it on confirmation. */
export function PlanReviewDialog({ plan, onClose, onApplied }: PlanReviewDialogProps) {
  const [preview, setPreview] = useState<PlanPreview | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(false);
  const [applying, setApplying] = useState(false);

  useEffect(() => {
    if (!plan) return;
    setPreview(null);
    setError(null);
    setLoading(true);
    modelApi.preview(plan)
      .then(setPreview)
      .catch((e) => setError(e instanceof ApiError ? e : new ApiError(0, {})))
      .finally(() => setLoading(false));
  }, [plan]);

  async function apply() {
    if (!plan) return;
    setApplying(true);
    setError(null);
    try {
      // Spec 021 (H2): se aplica lo que se revisó; si el modelo cambió desde la vista previa, el servidor responde 409.
      const r = await modelApi.apply({ ...plan, baseHash: preview?.baseHash ?? undefined });
      onApplied(r.changeSetId);
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError(0, {}));
    } finally {
      setApplying(false);
    }
  }

  return (
    <Dialog
      open={!!plan}
      onClose={() => { if (!applying) onClose(); }}
      size="lg"
      title="Revisar cambios al modelo"
      description={plan?.description}
      footer={
        <>
          <Button onClick={onClose} disabled={applying}>Volver a editar</Button>
          <Button variant="primary" icon={Rocket} loading={applying} disabled={!preview?.valid || loading} onClick={apply}>Aplicar cambios</Button>
        </>
      }
    >
      {loading && <LoadingBlock />}
      {error && (
        <Alert tone="danger" title={error.title}>
          {error.detail}
          {Object.entries(error.fieldErrors).map(([k, v]) => <div key={k}>{k}: {v.join(', ')}</div>)}
        </Alert>
      )}
      {preview && <PlanPreviewView preview={preview} />}
    </Dialog>
  );
}

/** Resultado de validar un plan: estado general, errores y pasos con su SQL. Se reutiliza al instalar módulos. */
export function PlanPreviewView({ preview, validText = 'Se aplicará en una sola transacción. Si un paso falla, no se aplica ninguno.', invalid }: {
  preview: PlanPreview; validText?: string;
  /** Cuando lo que bloquea no son errores del plan (p. ej. conflictos de un paquete, que se muestran aparte). */
  invalid?: { title: string; text: string };
}) {
  const generalErrors = preview.errors.filter((e) => !preview.steps.some((s) => s.operation === e.operation));
  return (
    <div className="stack">
      {preview.valid ? (
        <Alert tone="info" icon={CheckCircle2} title="El plan es válido">{validText}</Alert>
      ) : (
        <Alert tone="danger" title={invalid?.title ?? 'El plan tiene errores'}>{invalid?.text ?? 'Corrige los puntos marcados y vuelve a revisar.'}</Alert>
      )}
      {generalErrors.map((e, i) => (
        <Alert key={i} tone="danger" title={e.operation >= 0 ? `Operación ${e.operation + 1}` : 'Plan'}>{e.message}</Alert>
      ))}
      <ol className="plan-steps" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
        {preview.steps.map((s, i) => {
          const errs = preview.errors.filter((e) => e.operation === s.operation);
          return (
            <li key={i} className={`plan-step${errs.length ? ' has-error' : ''}`}>
              <div className="plan-step-head">
                <span className="plan-step-num">{i + 1}</span>
                <div className="grow">
                  <div>{s.description}</div>
                  {errs.map((e, j) => (
                    <div key={j} className="field-error" style={{ marginTop: 4 }}><AlertCircle size={13} aria-hidden /> {e.message}</div>
                  ))}
                </div>
              </div>
              {s.sql && (
                <details>
                  <summary><Code2 size={14} aria-hidden /> Ver SQL</summary>
                  {/* tabIndex: el bloque tiene scroll y debe poder recorrerse con teclado (WCAG 2.1.1) */}
                  <pre className="code-block" tabIndex={0} aria-label={`SQL del paso ${i + 1}`}>{s.sql}</pre>
                </details>
              )}
            </li>
          );
        })}
      </ol>
    </div>
  );
}
