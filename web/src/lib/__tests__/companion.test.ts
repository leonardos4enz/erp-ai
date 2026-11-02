import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { ApiError } from '../../api/client';
import type { CompanionAction, CompanionConversation, StudioStep } from '../../api/types';
import { ACTION_STATUS, AVATAR_TEXT, TOOL_LABELS, actionView, askingAction, avatarState, latestAction, screenContext, showValue, suggestions, warningsOf } from '../companion';
import { groupTurns, liveLabel } from '../studio';

/** Spec 047: lógica sin DOM del panel del Acompañante. */

const action = (extra: Partial<CompanionAction> = {}): CompanionAction => ({
  id: 3, kind: 'update', entity: 'Pedido', entityLabel: 'Pedido', recordId: 12, hash: 'h'.repeat(64), status: 'proposed',
  createdAt: '2026-11-01T10:00:00Z',
  preview: { tipo: 'update', catalogo: 'Pedido', registro: 12, segunElAcompanante: 'Cambio el teléfono.', cambios: [{ campo: 'Teléfono', antes: '555', despues: '777' }] },
  ...extra,
});
const conv = (extra: Partial<Pick<CompanionConversation, 'status' | 'actions'>> = {}) => ({ status: 'answered' as const, actions: [] as CompanionAction[], ...extra });

describe('Acompañante (047)', () => {
  it('H3 CA1: el avatar sigue a la conversación (seis estados)', () => {
    expect(avatarState(null, null)).toBe('idle');
    expect(avatarState(null, null, { sending: true })).toBe('thinking');
    expect(avatarState(conv({ status: 'queued' }), null)).toBe('thinking');
    expect(avatarState(conv({ status: 'running' }), { kind: 'text', text: 'Hola' })).toBe('thinking');
    expect(avatarState(conv({ status: 'running' }), { kind: 'tool', tool: 'buscar_registros' })).toBe('tool');
    expect(avatarState(conv({ actions: [action()] }), null)).toBe('waiting');
    expect(avatarState(conv({ actions: [action({ status: 'applied' })] }), null)).toBe('done');
    expect(avatarState(conv({ status: 'failed' }), null)).toBe('error');
    expect(avatarState(conv({ status: 'interrupted' }), null)).toBe('error');
    expect(avatarState(conv({ status: 'cancelled' }), null)).toBe('idle');
    expect(avatarState(conv(), null, { failed: true })).toBe('error');
  });

  it('CLARIFY 4: toda herramienta de CompanionTools.cs tiene nombre para personas', () => {
    const source = readFileSync(path.resolve(import.meta.dirname, '..', '..', '..', '..', 'src', 'Erp.Api', 'Companion', 'CompanionTools.cs'), 'utf8');
    const names = [...source.matchAll(/Tool\("([a-z_]+)"/g)].map((m) => m[1]);
    const propose = /Propose = "([a-z_]+)"/.exec(source)?.[1];
    expect(names.length).toBeGreaterThan(5);
    for (const n of [...names, propose!]) expect(TOOL_LABELS[n], `sin nombre para personas: ${n}`).toBeTruthy();
    expect(liveLabel({ kind: 'tool', tool: 'buscar_registros' }, 'running', TOOL_LABELS)).toBe('Buscando registros…');
    expect(liveLabel({ kind: 'tool', tool: 'otra_nueva' }, 'running', TOOL_LABELS)).toBe('Consultando…');
    // Studio sigue igual sin los nombres.
    expect(liveLabel({ kind: 'tool', tool: 'listar_catalogos' }, 'running')).toBe('Usando listar_catalogos…');
  });

  it('H5 CA1/CA3: la tarjeta por tipo y estado; sin vista previa no se ofrece confirmar', () => {
    const v = actionView(action(), 'Pedido');
    expect(v.title).toBe('Editar Pedido n.º 12');
    expect(v.canDecide).toBe(true);
    expect(actionView(action({ kind: 'create', recordId: null }), 'Cliente').title).toBe('Crear Cliente');
    expect(actionView(action({ kind: 'transition', preview: { tipo: 'transition', catalogo: 'Pedido', transicion: 'Aprobar', desde: 'Borrador', hacia: 'Aprobado' } }), 'Pedido').title)
      .toBe('Aprobar: Pedido n.º 12');
    // Al decidirse se borra la vista previa: el título conserva la transición que la tarjeta mostró.
    expect(actionView(action({ kind: 'transition', status: 'applied', preview: null }), 'Pedido', 'Aprobar').title).toBe('Aprobar: Pedido n.º 12');
    // Decidida o caducada: el servidor borra la vista previa y no hay botones.
    expect(actionView(action({ preview: null }), 'Pedido').canDecide).toBe(false);
    expect(actionView(action({ status: 'applying' }), 'Pedido').canDecide).toBe(false);
    const applied = actionView(action({ kind: 'create', entity: 'Cliente', recordId: null, status: 'applied', preview: null, result: { recordId: 40 } }), 'Cliente');
    expect(applied.recordLink).toBe('/c/Cliente/40');
    const unknown = actionView(action({ status: 'unknown', preview: null, error: 'No sabemos si se aplicó.' }), 'Pedido');
    expect(unknown.status.label).toBe('Sin confirmar si se aplicó');
    expect(unknown.message).toBe('No sabemos si se aplicó.');
    expect(unknown.recordLink).toBe('/c/Pedido/12');
    expect(actionView(action({ status: 'expired', preview: null }), 'Pedido').message).toMatch(/caducó/);
    expect(actionView(action({ status: 'failed', preview: null, error: 'Los pedidos de más de 1 000 los aprueba dirección.' }), 'Pedido').message).toMatch(/dirección/);
    for (const s of Object.keys(ACTION_STATUS)) expect(ACTION_STATUS[s as keyof typeof ACTION_STATUS].label).toBeTruthy();
  });

  it('Regresión (revisión adversarial 047, F-04): una lectura vieja no devuelve a «pendiente» una acción ya decidida', () => {
    const applied = action({ status: 'applied', preview: null });
    expect(latestAction(action(), applied)).toBe(applied);
    expect(latestAction(action({ status: 'applying' }), applied)).toBe(applied);
    // Lo que el servidor sabe después (otro estado decidido) gana; sin decisión local, el del servidor.
    const unknown = action({ status: 'unknown', preview: null });
    expect(latestAction(unknown, applied)).toBe(unknown);
    expect(latestAction(action(), undefined)?.status).toBe('proposed');
    expect(latestAction(undefined, applied)).toBe(applied);
  });

  it('Semántica: un valor vacío se muestra (vacío) y nada se pinta como marcado', () => {
    expect(showValue(null)).toBe('(vacío)');
    expect(showValue('')).toBe('(vacío)');
    expect(showValue(undefined)).toBe('(vacío)');
    expect(showValue(true)).toBe('Sí');
    expect(showValue(0)).toBe('0');
    expect(showValue('<b>x</b>')).toBe('<b>x</b>');
  });

  it('H5 CA2: avisos de un 428 (los mensajes tal cual, sin repetir, a lo más 50)', () => {
    const e = new ApiError(428, { title: 'Avisos', detail: 'Confirma', errors: { Total: ['Supera el presupuesto.'], registro: ['Supera el presupuesto.', 'Sin cliente.'] } });
    expect(warningsOf(e)).toEqual(['Supera el presupuesto.', 'Sin cliente.']);
    expect(warningsOf(new ApiError(428, { detail: 'Hay avisos.' }))).toEqual(['Hay avisos.']);
    expect(warningsOf(new ApiError(409, { detail: 'Cambió.' }))).toBeNull();
    expect(warningsOf(new Error('red'))).toBeNull();
    const many = new ApiError(428, { errors: { x: Array.from({ length: 80 }, (_, i) => `aviso ${i}`) } });
    expect(warningsOf(many)).toHaveLength(50);
  });

  it('H6 CA1: contexto de la pantalla solo en catálogos y registros', () => {
    expect(screenContext('/c/Pedido')).toEqual({ entity: 'Pedido', id: null });
    expect(screenContext('/c/Pedido/new')).toEqual({ entity: 'Pedido', id: null });
    expect(screenContext('/c/Pedido/12')).toEqual({ entity: 'Pedido', id: 12 });
    expect(screenContext('/c/Pedido/12/')).toEqual({ entity: 'Pedido', id: 12 });
    expect(screenContext('/')).toBeNull();
    expect(screenContext('/admin/studio')).toBeNull();
    expect(screenContext('/reports/Pedido/x')).toBeNull();
    expect(screenContext('/c/%3Cscript%3E')).toBeNull();
    expect(screenContext('/c/%E0%A4%A')).toBeNull();
    expect(suggestions({ entity: 'Pedido', id: 12 })[0]).toMatch(/por qué no puedo/i);
    expect(suggestions(null)).toContain('¿Qué tengo pendiente?');
  });

  it('H5: groupTurns da un segmento por acción propuesta, con su id', () => {
    const at = '2026-11-01T10:00:00Z';
    const trail: StudioStep[] = [
      { seq: 1, kind: 'user', status: 'ok', summary: 'Cambia el teléfono', durationMs: 0, at, turn: 1, output: { text: 'Cambia el teléfono' } },
      { seq: 2, kind: 'tool', tool: 'ver_registro', status: 'ok', summary: 'Pedido 12', durationMs: 5, at, turn: 1 },
      { seq: 3, kind: 'action', tool: 'proponer_accion', status: 'ok', summary: 'Propuesta 3', durationMs: 5, at, turn: 1, output: { accion: 3 } },
      { seq: 4, kind: 'text', status: 'ok', summary: '', durationMs: 0, at, turn: 1, output: { text: 'Te dejé la propuesta.' } },
    ];
    const [t] = groupTurns(trail, 'x');
    expect(t.user.text).toBe('Cambia el teléfono');
    expect(t.segments.map((s) => s.kind)).toEqual(['tools', 'action', 'text']);
    expect(t.segments[1]).toMatchObject({ kind: 'action', action: 3 });
  });
});

describe('Acompañante 2 (048)', () => {
  it('H6 CA3: un turno que espera tu respuesta pone al avatar esperando', () => {
    expect(avatarState(conv({ status: 'waiting' }), null)).toBe('waiting');
    expect(AVATAR_TEXT.waiting).toBe('Esperando tu respuesta');
  });

  it('H6 CA1: las propuestas se preguntan una por una (la primera pendiente)', () => {
    const list = [action({ id: 9 }), action({ id: 4, status: 'applied' }), action({ id: 7 })];
    const ask = askingAction(list);
    expect(ask.id).toBe(7);
    expect(ask.position(7)).toBe('Propuesta 1 de 2');
    expect(ask.position(9)).toBe('Propuesta 2 de 2');
    expect(ask.position(4)).toBeNull();
    expect(askingAction([action({ id: 2 })]).position(2)).toBeNull();
    expect(askingAction([]).id).toBeNull();
  });

  it('H1 CA3: lo que la persona escribió mientras esperaba es un segmento propio del turno', () => {
    const at = '2026-11-02T10:00:00Z';
    const trail: StudioStep[] = [
      { seq: 1, kind: 'user', status: 'ok', summary: 'Registra a mi gato', durationMs: 0, at, turn: 1, output: { text: 'Registra a mi gato' } },
      { seq: 2, kind: 'action', tool: 'proponer_accion', status: 'ok', summary: 'p', durationMs: 5, at, turn: 1, output: { accion: 3 } },
      { seq: 3, kind: 'answer', status: 'ok', summary: 'Llámalo Michi', durationMs: 0, at, turn: 1, output: { text: 'Llámalo Michi' } },
      { seq: 4, kind: 'text', status: 'ok', summary: '', durationMs: 0, at, turn: 1, output: { text: 'Va.' } },
    ];
    const [t] = groupTurns(trail, 'x');
    expect(t.user.text).toBe('Registra a mi gato');
    expect(t.segments.map((s) => s.kind)).toEqual(['action', 'answer', 'text']);
    expect(t.segments[1]).toMatchObject({ kind: 'answer', text: 'Llámalo Michi' });
  });
});
