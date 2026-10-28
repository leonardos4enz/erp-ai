import type { StudioLimits, StudioReport, StudioSession, StudioSessionStatus, StudioStep } from '../api/types';

/** Spec 035: lógica sin DOM de la pantalla de ERP AI Studio (probada con Vitest). */

export const STATUS_LABEL: Record<StudioSessionStatus, string> = {
  queued: 'En cola', running: 'Trabajando', proposed: 'Propuesta lista', answered: 'Respondida', failed: 'Falló',
  cancelled: 'Cancelada', interrupted: 'Interrumpida', applied: 'Aplicada', discarded: 'Descartada',
};

export const STATUS_TONE: Record<StudioSessionStatus, 'accent' | 'danger' | 'neutral' | 'success' | 'warning'> = {
  queued: 'neutral', running: 'accent', proposed: 'accent', answered: 'neutral', failed: 'danger',
  cancelled: 'neutral', interrupted: 'warning', applied: 'success', discarded: 'neutral',
};

export const isActive = (s: StudioSessionStatus) => s === 'queued' || s === 'running';

/**
 * Agrega los pasos de un sondeo (`after`) a los que ya se ven. Dos sondeos que se cruzan pueden traer el mismo paso: se
 * conserva uno por `seq`, en orden.
 */
export function mergeTrail(current: StudioStep[], incoming: StudioStep[]): StudioStep[] {
  if (incoming.length === 0) return current;
  const bySeq = new Map(current.map((s) => [s.seq, s]));
  for (const s of incoming) bySeq.set(s.seq, s);
  return [...bySeq.values()].sort((a, b) => a.seq - b.seq);
}

export const lastSeq = (trail: StudioStep[]) => (trail.length ? trail[trail.length - 1].seq : 0);

export interface Meter { key: string; label: string; value: number; max: number; text: string }

const int = new Intl.NumberFormat('es-MX', { maximumFractionDigits: 0 });
const usd = new Intl.NumberFormat('es-MX', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
const min = new Intl.NumberFormat('es-MX', { minimumFractionDigits: 0, maximumFractionDigits: 1 });

export const formatUsd = (v: number) => `${usd.format(v)} USD`;

/** Minutos que lleva (o llevó) la sesión: de que empezó a que terminó, o a `now` si sigue. En cola, 0. */
export function elapsedMinutes(s: Pick<StudioSession, 'startedAt' | 'finishedAt'>, now: number): number {
  if (!s.startedAt) return 0;
  const end = s.finishedAt ? Date.parse(s.finishedAt) : now;
  return Math.max(0, (end - Date.parse(s.startedAt)) / 60_000);
}

/** H2 CA2: el consumo de la sesión contra sus topes. */
export function consumption(s: StudioSession, limits: StudioLimits, now: number): Meter[] {
  const tokens = s.inputTokens + s.outputTokens;
  const minutes = elapsedMinutes(s, now);
  return [
    { key: 'steps', label: 'Pasos', value: s.steps, max: limits.maxSteps, text: `${int.format(s.steps)} de ${int.format(limits.maxSteps)}` },
    { key: 'tools', label: 'Herramientas', value: s.toolCalls, max: limits.maxToolCalls, text: `${int.format(s.toolCalls)} de ${int.format(limits.maxToolCalls)}` },
    { key: 'tokens', label: 'Tokens', value: tokens, max: limits.maxTokens, text: `${int.format(tokens)} de ${int.format(limits.maxTokens)}` },
    { key: 'minutes', label: 'Minutos', value: minutes, max: limits.maxMinutes, text: `${min.format(minutes)} de ${int.format(limits.maxMinutes)}` },
    { key: 'cost', label: 'Costo estimado', value: s.costUsd, max: limits.maxCostUsd, text: `${formatUsd(s.costUsd)} de ${formatUsd(limits.maxCostUsd)}` },
  ].map((m) => ({ ...m, value: Math.min(m.value, m.max) }));
}

/** Herramientas que corren en la copia aislada (la preparación la registra el servidor como `aislado_preparar`). */
const isSandboxTool = (step: StudioStep) => step.kind === 'tool' && !!step.tool?.startsWith('aislado_') && step.tool !== 'aislado_preparar';

/**
 * R-033-11: el informe lo declara el agente. Avisos que la pantalla muestra junto a él, comparándolo con los pasos reales.
 * `trailComplete` es falso si la pantalla no tiene todos los pasos (no se puede afirmar que no corrió algo).
 */
export function reportNotes(report: StudioReport | null | undefined, trail: StudioStep[], trailComplete = true): string[] {
  if (!report || !trailComplete) return [];
  const notes: string[] = [];
  const passed = report.tested.filter((t) => t.ok === true).length;
  const ran = trail.filter(isSandboxTool);
  if (report.tested.length > 0 && ran.length === 0)
    notes.push('El agente informa pruebas, pero no ejecutó ninguna herramienta en la copia aislada: tómalas como lo que dice, no como algo comprobado.');
  else if (passed > 0 && ran.length > 0 && ran.every((s) => s.status !== 'ok'))
    notes.push('El agente informa pruebas superadas, pero todas sus herramientas en la copia aislada fallaron: revisa los pasos.');
  return notes;
}

/** H3 CA3: el plan para llevarlo a un entorno de desarrollo y exportarlo como paquete. */
export function planFile(s: Pick<StudioSession, 'id' | 'plan'>): { name: string; content: string } | null {
  if (!s.plan) return null;
  return { name: `erp-ai-studio-sesion-${s.id}.json`, content: JSON.stringify(s.plan, null, 2) };
}

/** Entrada o salida de un paso como texto (nunca como marcado). */
export function asText(v: unknown): string {
  if (v === undefined || v === null) return '';
  if (typeof v === 'string') return v;
  return JSON.stringify(v, null, 2);
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${int.format(ms)} ms`;
  return `${min.format(ms / 1000)} s`;
}
