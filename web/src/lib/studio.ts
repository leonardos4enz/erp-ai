import type {
  StudioLimits, StudioLive, StudioPlanStatus, StudioReport, StudioSession, StudioSessionStatus, StudioStep,
} from '../api/types';

/** Specs 035 y 037: lógica sin DOM de la pantalla de ERP AI Studio (probada con Vitest). */

export const STATUS_LABEL: Record<StudioSessionStatus, string> = {
  queued: 'En cola', running: 'Trabajando', proposed: 'Propuesta lista', answered: 'Respondida', failed: 'Falló',
  cancelled: 'Detenida', interrupted: 'Interrumpida', applied: 'Aplicada', discarded: 'Descartada',
};

export const STATUS_TONE: Record<StudioSessionStatus, 'accent' | 'danger' | 'neutral' | 'success' | 'warning'> = {
  queued: 'neutral', running: 'accent', proposed: 'accent', answered: 'neutral', failed: 'danger',
  cancelled: 'neutral', interrupted: 'warning', applied: 'success', discarded: 'neutral',
};

export const PLAN_LABEL: Record<StudioPlanStatus, string> = { proposed: 'Propuesta lista', applied: 'Aplicada', discarded: 'Descartada' };

export const isActive = (s: StudioSessionStatus) => s === 'queued' || s === 'running';

/**
 * Agrega los pasos que llegan (flujo o lectura con `after`) a los que ya se ven. Dos lecturas que se cruzan, o un flujo que
 * se reanuda, pueden traer el mismo paso: se conserva uno por `seq`, en orden.
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
const dec = new Intl.NumberFormat('es-MX', { minimumFractionDigits: 0, maximumFractionDigits: 1 });

export const formatUsd = (v: number) => `${usd.format(v)} USD`;

/** H5 CA4: el consumo de la conversación contra sus topes (los minutos son por turno: no se muestran). */
export function consumption(s: Pick<StudioSession, 'turns' | 'steps' | 'toolCalls' | 'inputTokens' | 'outputTokens' | 'costUsd'>, limits: StudioLimits): Meter[] {
  const tokens = s.inputTokens + s.outputTokens;
  return [
    { key: 'turns', label: 'Mensajes', value: s.turns, max: limits.maxTurns, text: `${int.format(s.turns)} de ${int.format(limits.maxTurns)}` },
    { key: 'steps', label: 'Pasos', value: s.steps, max: limits.maxSteps, text: `${int.format(s.steps)} de ${int.format(limits.maxSteps)}` },
    { key: 'tools', label: 'Herramientas', value: s.toolCalls, max: limits.maxToolCalls, text: `${int.format(s.toolCalls)} de ${int.format(limits.maxToolCalls)}` },
    { key: 'tokens', label: 'Tokens', value: tokens, max: limits.maxTokens, text: `${int.format(tokens)} de ${int.format(limits.maxTokens)}` },
    { key: 'cost', label: 'Costo estimado', value: s.costUsd, max: limits.maxCostUsd, text: `${formatUsd(s.costUsd)} de ${formatUsd(limits.maxCostUsd)}` },
  ].map((m) => ({ ...m, value: Math.min(m.value, m.max) }));
}

/** Herramientas que corren en la copia aislada (la preparación la registra el servidor como `aislado_preparar`). */
const isSandboxTool = (step: StudioStep) => step.kind === 'tool' && !!step.tool?.startsWith('aislado_') && step.tool !== 'aislado_preparar';

/**
 * R-033-11: el informe lo declara el agente. Avisos que la pantalla muestra junto a él, comparándolo con los pasos reales
 * del turno que lo entregó. `trailComplete` es falso si la pantalla no tiene todos los pasos.
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

/** H5 CA3: el plan para llevarlo a un entorno de desarrollo y exportarlo como paquete. */
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
  return `${dec.format(ms / 1000)} s`;
}

/* ---------------- Spec 037: conversación ---------------- */

/** Lo que entregó el agente (entrada de `entregar`, guardada como dato no confiable). */
export interface Delivery { summary: string; report: StudioReport | null; hasPlan: boolean }

export type Segment =
  | { kind: 'text'; seq: number; text: string }
  | { kind: 'tools'; steps: StudioStep[] }
  | { kind: 'deliver'; seq: number; step: StudioStep; delivery: Delivery }
  | { kind: 'notice'; step: StudioStep };

export interface Turn { turn: number; user: { seq: number | null; text: string; at: string | null }; segments: Segment[]; steps: StudioStep[] }

const str = (v: unknown, max = 4000) => (typeof v === 'string' ? v.slice(0, max) : v === undefined || v === null ? '' : String(v).slice(0, max));
const strings = (v: unknown) => (Array.isArray(v) ? v.slice(0, 50).map((x) => str(x, 1000)) : []);

/** La entrega de un paso `deliver`: su entrada (cortada a 8 000 caracteres por el servidor puede llegar como texto). */
export function deliveryOf(step: StudioStep): Delivery {
  const input = step.input;
  if (input && typeof input === 'object' && !Array.isArray(input)) {
    const o = input as Record<string, unknown>;
    const tested = Array.isArray(o.tested)
      ? o.tested.slice(0, 50).map((t) => (t && typeof t === 'object'
        ? { description: str((t as Record<string, unknown>).description, 500), ok: (t as Record<string, unknown>).ok === true, detail: str((t as Record<string, unknown>).detail, 1000) || null }
        : { description: str(t, 500), ok: false, detail: null }))
      : [];
    return { summary: str(o.summary), report: { tested, failures: strings(o.failures), risks: strings(o.risks) }, hasPlan: !!o.plan && typeof o.plan === 'object' };
  }
  const out = step.output as { summary?: unknown } | undefined;
  // En el JSON crudo, unas comillas que el modelo escribió dentro de un texto van escapadas (`\"plan\"`): solo una clave
  // real tiene `"plan"` sin barra antes, y aquí además seguida de un objeto (revisión adversarial 037, F-06).
  return { summary: str(out?.summary ?? step.summary), report: null, hasPlan: typeof input === 'string' && /(^|[^\\])"plan"\s*:\s*\{/.test(input) };
}

/**
 * H3: los pasos agrupados por turno. Cada turno empieza con el mensaje de la persona (paso `user`; las sesiones anteriores a
 * la 036 no lo tienen y toman el pedido de la sesión) y sigue con segmentos en orden: texto del agente, grupos de
 * herramientas seguidas (una entrega que no compiló cuenta como herramienta con error), la entrega y los avisos de tope.
 */
export function groupTurns(trail: StudioStep[], prompt: string): Turn[] {
  const turns = new Map<number, Turn>();
  const get = (n: number) => {
    let t = turns.get(n);
    if (!t) turns.set(n, (t = { turn: n, user: { seq: null, text: n === 1 ? prompt : '', at: null }, segments: [], steps: [] }));
    return t;
  };
  for (const step of [...trail].sort((a, b) => a.seq - b.seq)) {
    const t = get(step.turn ?? 1);
    t.steps.push(step);
    if (step.kind === 'user') {
      const text = (step.output as { text?: unknown } | undefined)?.text;
      t.user = { seq: step.seq, text: typeof text === 'string' ? text : step.summary, at: step.at };
      continue;
    }
    const last = t.segments[t.segments.length - 1];
    if (step.kind === 'tool' || (step.kind === 'deliver' && step.status !== 'ok')) {
      if (last?.kind === 'tools') last.steps.push(step);
      else t.segments.push({ kind: 'tools', steps: [step] });
    } else if (step.kind === 'text') {
      const text = (step.output as { text?: unknown } | undefined)?.text;
      t.segments.push({ kind: 'text', seq: step.seq, text: typeof text === 'string' ? text : step.summary });
    } else if (step.kind === 'deliver') {
      t.segments.push({ kind: 'deliver', seq: step.seq, step, delivery: deliveryOf(step) });
    } else {
      t.segments.push({ kind: 'notice', step });
    }
  }
  return [...turns.values()].sort((a, b) => a.turn - b.turn);
}

/**
 * H5 CA2: la entrega que dejó la propuesta vigente (la última entrega con plan). Las anteriores con plan son «reemplazadas»
 * y no tienen acciones. Sin `planStatus` no hay propuesta.
 */
export function currentProposalSeq(trail: StudioStep[], planStatus: StudioPlanStatus | null | undefined): number | null {
  if (!planStatus) return null;
  let lastOk: number | null = null;
  for (let i = trail.length - 1; i >= 0; i--) {
    const s = trail[i];
    if (s.kind !== 'deliver' || s.status !== 'ok') continue;
    lastOk ??= s.seq;
    if (deliveryOf(s).hasPlan) return s.seq;
  }
  return lastOk;
}

/** El último mensaje de la persona (para reintentar y editar). */
export function lastUserText(turns: Turn[]): string | null {
  for (let i = turns.length - 1; i >= 0; i--) if (turns[i].user.text) return turns[i].user.text;
  return null;
}

/** Estado en vivo en palabras (para la línea de estado y `aria-live`). */
export function liveLabel(live: StudioLive | null, status: StudioSessionStatus): string {
  if (status === 'queued') return 'En cola…';
  if (live?.kind === 'tool') return `Usando ${live.tool ?? 'una herramienta'}…`;
  if (live?.kind === 'text') return 'Escribiendo…';
  return 'Pensando…';
}

/* ---------------- Flujo SSE ---------------- */

export interface SseEvent { event: string; data: string; id: string | null }

/**
 * Analizador incremental de `text/event-stream`: recibe trozos de texto tal como llegan (un evento puede partirse entre dos
 * trozos, o venir varios en uno) y devuelve los eventos completos. Ignora comentarios (`: ping`).
 */
export function createSseParser() {
  let buffer = '';
  let event = 'message';
  let data: string[] = [];
  let id: string | null = null;
  return {
    push(chunk: string): SseEvent[] {
      buffer += chunk;
      const out: SseEvent[] = [];
      let nl: number;
      while ((nl = buffer.search(/\r\n|\r|\n/)) >= 0) {
        const line = buffer.slice(0, nl);
        buffer = buffer.slice(nl + (buffer[nl] === '\r' && buffer[nl + 1] === '\n' ? 2 : 1));
        if (line === '') {
          if (data.length) out.push({ event, data: data.join('\n'), id });
          event = 'message';
          data = [];
          id = null;
          continue;
        }
        if (line.startsWith(':')) continue;
        const colon = line.indexOf(':');
        const field = colon < 0 ? line : line.slice(0, colon);
        const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
        if (field === 'event') event = value;
        else if (field === 'data') data.push(value);
        else if (field === 'id') id = value;
      }
      return out;
    },
  };
}

/** CLARIFY 4: espera antes de reconectar tras la falla número `attempt` (0, 1, …); `null` = dejar de intentar. */
export function nextBackoff(attempt: number): number | null {
  if (attempt >= 4) return null;
  return Math.min(15_000, 1000 * 2 ** attempt);
}

/* ---------------- Ritmo del texto ---------------- */

/** Caracteres por segundo medidos en Claude web (análisis §2.4). */
export const REVEAL_CPS = 240;

/**
 * CLARIFY 5: cuántos caracteres de `target` mostrar tras `dtMs`, partiendo de `shown`. A ~240 car/s; si va más de 2 s atrás,
 * se pone al día en medio segundo. Corta en un límite de palabra (nunca a mitad), salvo al final del texto.
 */
export function revealStep(shown: number, target: string, dtMs: number): number {
  if (shown >= target.length) return target.length;
  const behind = target.length - shown;
  const cps = behind > REVEAL_CPS * 2 ? behind * 2 : REVEAL_CPS;
  let next = Math.min(target.length, shown + Math.max(1, Math.round((cps * dtMs) / 1000)));
  while (next < target.length && !/\s/.test(target[next])) next++;
  return next;
}

/** Palabras con su espacio final (para el fundido por palabra; la clave es su posición). */
export function splitWords(text: string): string[] {
  return text.match(/\s*\S+\s*|\s+/g) ?? [];
}

/* ---------------- Fechas ---------------- */

const rtf = new Intl.RelativeTimeFormat('es-MX', { numeric: 'auto' });
const dayFmt = new Intl.DateTimeFormat('es-MX', { day: 'numeric', month: 'short' });

/** Hora relativa de un mensaje («ahora», «hace 5 minutos», «ayer», «12 oct»). */
export function relativeTime(iso: string, now: number): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const s = Math.round((now - t) / 1000);
  if (s < 45) return 'ahora';
  const m = Math.round(s / 60);
  if (m < 60) return rtf.format(-m, 'minute');
  const h = Math.round(m / 60);
  if (h < 24) return rtf.format(-h, 'hour');
  const d = Math.round(h / 24);
  if (d < 7) return rtf.format(-d, 'day');
  return dayFmt.format(t);
}
