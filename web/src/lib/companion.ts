import { ApiError } from '../api/client';
import type { CompanionAction, CompanionActionStatus, CompanionConversation, CompanionScreen, StudioLive } from '../api/types';
import { isActive } from './studio';

/** Spec 047: lógica sin DOM del panel del Acompañante (probada con Vitest). */

/* ---------------- Avatar (H3) ---------------- */

/** Los seis estados del chat (CLARIFY 3: reemplazan a los de la flota de `<fleet-eyes>`). */
export type AvatarState = 'idle' | 'thinking' | 'tool' | 'waiting' | 'done' | 'error';

export const AVATAR_TEXT: Record<AvatarState, string> = {
  idle: 'En reposo', thinking: 'Pensando…', tool: 'Consultando…', waiting: 'Esperando tu respuesta', done: 'Listo', error: 'Algo salió mal',
};

/**
 * H3 CA1: el estado del avatar sale de la conversación: un turno en curso (o un mensaje recién enviado) es «pensando» o
 * «usando una herramienta»; un turno que falló, «error»; una propuesta pendiente, «esperando tu confirmación»; una respuesta,
 * «listo». Sin conversación (o detenida), en reposo.
 */
export function avatarState(c: Pick<CompanionConversation, 'status' | 'actions'> | null, live: StudioLive | null, opts: { failed?: boolean; sending?: boolean } = {}): AvatarState {
  if (opts.failed) return 'error';
  if (opts.sending || (c && isActive(c.status))) return live?.kind === 'tool' ? 'tool' : 'thinking';
  if (!c) return 'idle';
  if (c.status === 'failed' || c.status === 'interrupted') return 'error';
  // Spec 048: el turno se detuvo en una propuesta (o quedó una pendiente de antes).
  if (c.status === 'waiting' || c.actions.some((a) => a.status === 'proposed')) return 'waiting';
  if (c.status === 'answered') return 'done';
  return 'idle';
}

/* ---------------- Herramientas en lenguaje de usuario (H2 CA2, CLARIFY 4) ---------------- */

/** Toda herramienta de `CompanionTools.cs` (y `proponer_accion`) tiene nombre para personas: lo exige `companion.test.ts`. */
export const TOOL_LABELS: Readonly<Record<string, { done: string; live: string }>> = {
  mis_modulos: { done: 'Revisó tus módulos', live: 'Revisando tus módulos' },
  mis_catalogos: { done: 'Revisó tus catálogos', live: 'Revisando tus catálogos' },
  como_funciona: { done: 'Revisó cómo funciona un catálogo', live: 'Revisando cómo funciona' },
  buscar_en_el_sistema: { done: 'Buscó en el sistema', live: 'Buscando en el sistema' },
  buscar_registros: { done: 'Buscó registros', live: 'Buscando registros' },
  ver_registro: { done: 'Abrió un registro', live: 'Abriendo un registro' },
  historial_registro: { done: 'Revisó el historial de un registro', live: 'Revisando el historial' },
  buscar_folio: { done: 'Buscó un folio', live: 'Buscando un folio' },
  por_que_no_puedo: { done: 'Revisó por qué no puedes', live: 'Revisando por qué no puedes' },
  mis_pendientes: { done: 'Revisó tus pendientes', live: 'Revisando tus pendientes' },
  proponer_accion: { done: 'Preparó una propuesta', live: 'Preparando una propuesta' },
};

/** Herramientas que leen registros (su salida se purga al terminar el turno, 046 H2 CA4). */
export const DATA_TOOLS: ReadonlySet<string> = new Set([
  'buscar_registros', 'ver_registro', 'historial_registro', 'buscar_folio', 'por_que_no_puedo', 'mis_pendientes', 'proponer_accion',
]);

/* ---------------- Acciones (H5) ---------------- */

export type Tone = 'accent' | 'danger' | 'neutral' | 'success' | 'warning';

export const ACTION_STATUS: Record<CompanionActionStatus, { label: string; tone: Tone }> = {
  proposed: { label: 'Pendiente de tu confirmación', tone: 'accent' },
  applying: { label: 'Aplicándose…', tone: 'neutral' },
  applied: { label: 'Aplicada', tone: 'success' },
  failed: { label: 'No se aplicó', tone: 'danger' },
  discarded: { label: 'Descartada', tone: 'neutral' },
  expired: { label: 'Caducó', tone: 'neutral' },
  unknown: { label: 'Sin confirmar si se aplicó', tone: 'warning' },
};

/**
 * Lo que dice la tarjeta de una acción (sin vista previa, la acción ya se decidió: sin botones). `seen` es el nombre de la
 * transición que la tarjeta mostró mientras estaba pendiente: al decidirse, el servidor borra la vista previa (046 H2 CA4) y
 * el título no debe cambiar de «Aprobar» a «Cambiar de estado».
 */
export function actionView(a: CompanionAction, entityLabel: string, seen?: string | null) {
  const p = a.preview ?? null;
  const n = a.recordId ?? p?.registro;
  const record = n ? ` n.º ${n}` : '';
  const title = a.kind === 'create' ? `Crear ${entityLabel}`
    : a.kind === 'update' ? `Editar ${entityLabel}${record}`
      : `${p?.transicion ?? seen ?? 'Cambiar de estado'}: ${entityLabel}${record}`;
  const status = ACTION_STATUS[a.status] ?? { label: a.status, tone: 'neutral' as const };
  const resultId = a.result?.recordId ?? (a.kind === 'create' ? null : a.recordId);
  return {
    title, status, preview: p,
    canDecide: a.status === 'proposed' && !!p,
    /** Aplicada o sin confirmar: el registro a revisar. */
    recordLink: (a.status === 'applied' || a.status === 'unknown') && resultId ? `/c/${encodeURIComponent(a.entity)}/${resultId}` : null,
    message: a.status === 'failed' || a.status === 'unknown' ? a.error ?? null
      : a.status === 'expired' ? 'La propuesta caducó sin confirmarse: pídela otra vez si todavía la necesitas.' : null,
  };
}

/**
 * Revisión adversarial 047 (F-04): la acción que se muestra. Una lectura completa pedida antes de decidir puede llegar después
 * de la respuesta de confirmar o descartar: una acción que la tarjeta ya vio decidida no vuelve a «pendiente».
 */
export function latestAction(server: CompanionAction | undefined, decided: CompanionAction | undefined): CompanionAction | undefined {
  if (!server) return decided;
  if (decided && decided.id === server.id && (server.status === 'proposed' || server.status === 'applying') && decided.status !== 'proposed') return decided;
  return server;
}

/**
 * Spec 048 (H6 CA1): las propuestas se preguntan una por una, como los permisos de Claude Code: la que se pregunta es la
 * primera pendiente; las demás esperan su turno. `position` es «Propuesta 1 de 3» cuando hay más de una pendiente.
 */
export function askingAction(actions: CompanionAction[]): { id: number | null; position: (id: number) => string | null } {
  const pending = actions.filter((a) => a.status === 'proposed').sort((a, b) => a.id - b.id);
  return {
    id: pending[0]?.id ?? null,
    position: (id) => {
      const i = pending.findIndex((a) => a.id === id);
      return pending.length > 1 && i >= 0 ? `Propuesta ${i + 1} de ${pending.length}` : null;
    },
  };
}

/** Un valor de la vista previa como texto (nunca como marcado). */
export function showValue(v: unknown): string {
  if (v === null || v === undefined || v === '') return '(vacío)';
  if (typeof v === 'boolean') return v ? 'Sí' : 'No';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

/** H5 CA2: los avisos de un 428 (tal como los devolvió el servidor: se reenvían como aceptados). */
export function warningsOf(e: unknown): string[] | null {
  if (!(e instanceof ApiError) || e.status !== 428) return null;
  const list = [...new Set(Object.values(e.fieldErrors).flat())];
  return list.length ? list.slice(0, 50) : [e.detail ?? e.title];
}

/* ---------------- Contexto de la pantalla (H6) ---------------- */

/** H6 CA1: el catálogo (y el registro) de la pantalla actual; fuera de `/c/` no hay contexto. */
export function screenContext(pathname: string): CompanionScreen | null {
  const m = /^\/c\/([^/]+)(?:\/([^/]+))?\/?$/.exec(pathname);
  if (!m) return null;
  let entity: string;
  try { entity = decodeURIComponent(m[1]); } catch { return null; }
  if (!/^[A-Za-z][A-Za-z0-9_]{0,99}$/.test(entity)) return null;
  const id = m[2] && /^\d{1,15}$/.test(m[2]) ? Number(m[2]) : null;
  return { entity, id };
}

/** Sugerencias de la bienvenida (rellenan el compositor; no se envían solas). */
export function suggestions(screen: CompanionScreen | null): string[] {
  const base = ['¿Qué tengo pendiente?', '¿Qué módulos puedo usar y para qué sirven?'];
  if (screen?.id) return ['¿Por qué no puedo avanzar este registro?', 'Explícame qué hace este registro y en qué estado está.', ...base];
  if (screen) return ['¿Cómo se captura un registro aquí?', 'Explícame las reglas de este catálogo.', ...base];
  return [...base, '¿Cómo registro un pedido?'];
}
