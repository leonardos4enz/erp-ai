import { useId, useState } from 'react';
import { Ban, CheckCircle2, ChevronRight, Wrench, XCircle } from 'lucide-react';
import type { StudioStep } from '../../api/types';
import { asText, formatDuration } from '../../lib/studio';

const STATUS = {
  ok: { icon: CheckCircle2, label: 'Correcta', cls: 'is-ok' },
  error: { icon: XCircle, label: 'Con error', cls: 'is-error' },
  denied: { icon: Ban, label: 'Negada', cls: 'is-denied' },
} as const;

/**
 * Spec 037 (H3 CA2): las herramientas seguidas de una respuesta en una línea plegable («Usó 3 herramientas»); cada fila
 * abre su entrada y su salida, como texto (nunca como marcado).
 */
export function ToolGroup({ steps, dataTools, labels }: {
  steps: StudioStep[]; dataTools: ReadonlySet<string>;
  /** Spec 047: nombres para personas (el Acompañante); con ellos no se muestran la entrada ni la salida técnicas. */
  labels?: Readonly<Record<string, { done: string }>>;
}) {
  const [open, setOpen] = useState(false);
  const panel = useId();
  const failed = steps.filter((s) => s.status !== 'ok').length;
  const named = (s: StudioStep) => (s.tool && labels?.[s.tool]?.done) || (labels ? 'Consultó el sistema' : s.tool ?? 'herramienta');
  const label = labels
    ? `${steps.length === 1 ? named(steps[0]) : `Consultó ${steps.length} cosas`}${failed ? ` · ${failed} sin respuesta` : ''}`
    : `Usó ${steps.length === 1 ? steps[0].tool ?? 'una herramienta' : `${steps.length} herramientas`}${failed ? ` · ${failed} con error` : ''}`;
  return (
    <div className="chat-tools">
      <button type="button" className="chat-tools-toggle" aria-expanded={open} aria-controls={panel} onClick={() => setOpen((o) => !o)}>
        <Wrench size={14} aria-hidden />
        <span>{label}</span>
        <ChevronRight size={14} aria-hidden className={`chat-chevron${open ? ' is-open' : ''}`} />
      </button>
      {open && (
        <ul id={panel} className="chat-tools-list" aria-label="Herramientas">
          {steps.map((s) => (labels ? <PlainRow key={s.seq} step={s} name={named(s)} /> : <ToolRow key={s.seq} step={s} dataTools={dataTools} />))}
        </ul>
      )}
    </div>
  );
}

/** Spec 047: una consulta del Acompañante, sin entrada ni salida (lenguaje de usuario, H2 CA2). */
function PlainRow({ step, name }: { step: StudioStep; name: string }) {
  const st = STATUS[step.status] ?? STATUS.error;
  const Icon = st.icon;
  return (
    <li className={`chat-tool ${st.cls}`}>
      <div className="chat-tool-row is-plain">
        <Icon size={15} aria-hidden className="chat-tool-icon" />
        <span className="sr-only">{st.label}: </span>
        <span className="chat-tool-name">{name}</span>
        <span className="chat-tool-summary">{step.summary}</span>
      </div>
    </li>
  );
}

function ToolRow({ step, dataTools }: { step: StudioStep; dataTools: ReadonlySet<string> }) {
  const [open, setOpen] = useState(false);
  const body = useId();
  const st = STATUS[step.status] ?? STATUS.error;
  const Icon = st.icon;
  const input = asText(step.input);
  const output = asText(step.output);
  return (
    <li className={`chat-tool ${st.cls}`}>
      <button type="button" className="chat-tool-row" aria-expanded={open} aria-controls={body} onClick={() => setOpen((o) => !o)}>
        <Icon size={15} aria-hidden className="chat-tool-icon" />
        <span className="sr-only">{st.label}: </span>
        <span className="chat-tool-name">{step.tool ?? 'herramienta'}</span>
        <span className="chat-tool-summary">{step.summary}</span>
        {step.kind === 'tool' && <span className="chat-tool-time studio-duration">{formatDuration(step.durationMs)}</span>}
        <ChevronRight size={14} aria-hidden className={`chat-chevron${open ? ' is-open' : ''}`} />
      </button>
      {open && (
        <div id={body} className="chat-tool-body">
          {input && (
            <div>
              <div className="chat-io-label">Entrada</div>
              <pre className="chat-io" tabIndex={0} aria-label={`Entrada de ${step.tool ?? 'la herramienta'}`}>{input}</pre>
            </div>
          )}
          <div>
            <div className="chat-io-label">Salida</div>
            {output ? (
              <pre className="chat-io" tabIndex={0} aria-label={`Salida de ${step.tool ?? 'la herramienta'}`}>{output}</pre>
            ) : (
              <p className="chat-io-empty">
                {step.tool && dataTools.has(step.tool) ? 'Se borró al terminar el turno: contenía registros.' : 'Sin salida guardada.'}
              </p>
            )}
          </div>
        </div>
      )}
    </li>
  );
}
