import { Bot, Lock } from 'lucide-react';
import type { Workflow } from '../api/types';
import { LABEL_H, NODE_H, NODE_W, clip, layoutDiagram } from '../lib/workflowEdit';

/**
 * Spec 025: el diagrama del proceso de la 019 en solo lectura (misma geometría `layoutDiagram` y mismos estilos). Es una
 * imagen: la alternativa accesible es la lista de transiciones que la acompaña.
 */
export function WorkflowDiagram({ workflow: w, label }: { workflow: Workflow; label: string }) {
  const d = layoutDiagram(w);
  const stateOf = (n: string) => w.states.find((s) => s.name.toLowerCase() === n.toLowerCase());
  return (
    <div className="wf-canvas ex-wf">
      <svg width={d.width} height={d.height} viewBox={`0 0 ${d.width} ${d.height}`} role="img"
        aria-label={`${label}: ${w.states.length} estados y ${w.transitions.length} transiciones (detalle en la lista)`}>
        <defs>
          <marker id="exwf-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" className="wf-arrow" />
          </marker>
        </defs>
        {d.edges.map((e) => {
          const t = w.transitions.find((x) => x.name === e.transition);
          return <path key={e.key} d={e.path} className={`wf-edge${t?.system ? ' system' : ''}`} markerEnd="url(#exwf-arrow)" />;
        })}
        {d.nodes.map((n) => {
          const s = stateOf(n.name)!;
          return (
            <g key={n.name} className={`wf-node wf-tone-${s.color ?? 'neutral'}`} transform={`translate(${n.x} ${n.y})`}>
              {s.name === w.initial && <><circle cx={-22} cy={NODE_H / 2} r={5} className="wf-start" /><line x1={-17} y1={NODE_H / 2} x2={-3} y2={NODE_H / 2} className="wf-edge" markerEnd="url(#exwf-arrow)" /></>}
              <rect className="wf-node-box" width={NODE_W} height={NODE_H} rx={10} />
              <text x={12} y={21} className="wf-node-label">{clip(s.label || s.name, 18)}</text>
              <text x={12} y={38} className="wf-node-sub">{clip(s.name, 22)}</text>
              {s.editable === false && <Lock x={NODE_W - 24} y={8} size={14} className="wf-node-icon" />}
            </g>
          );
        })}
        {d.edges.map((e) => {
          const t = w.transitions.find((x) => x.name === e.transition);
          return (
            <g key={`l-${e.key}`} className="wf-edge-label" transform={`translate(${e.labelX - e.labelWidth / 2} ${e.labelY - LABEL_H / 2})`}>
              <rect width={e.labelWidth} height={LABEL_H} rx={LABEL_H / 2} />
              {t?.system && <Bot x={8} y={4} size={14} className="wf-node-icon" />}
              <text x={t?.system ? 26 : 9} y={15}>{e.labelText}</text>
            </g>
          );
        })}
      </svg>
    </div>
  );
}
