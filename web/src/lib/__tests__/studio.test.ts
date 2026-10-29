import { describe, expect, it } from 'vitest';
import type { StudioLimits, StudioSession, StudioStep } from '../../api/types';
import {
  REVEAL_CPS, consumption, createSseParser, currentProposalSeq, deliveryOf, groupTurns, lastUserText, liveLabel, mergeTrail, nextBackoff,
  planFile, relativeTime, reportNotes, revealStep, splitWords,
} from '../studio';

const step = (seq: number, extra: Partial<StudioStep> = {}): StudioStep => ({
  seq, kind: 'tool', tool: 'listar_catalogos', status: 'ok', summary: '200', durationMs: 10, at: '2026-10-27T10:00:00Z', turn: 1, ...extra,
});
const user = (seq: number, turn: number, text: string) => step(seq, { kind: 'user', tool: null, turn, summary: text.slice(0, 40), output: { text } });
const text = (seq: number, turn: number, t: string) => step(seq, { kind: 'text', tool: null, turn, output: { text: t } });
const deliver = (seq: number, turn: number, input: unknown, status: StudioStep['status'] = 'ok') =>
  step(seq, { kind: 'deliver', tool: 'entregar', turn, status, input, output: { summary: 'x' } });

const limits: StudioLimits = {
  maxSteps: 30, maxToolCalls: 80, maxTokens: 1_500_000, maxMinutes: 20, maxCostUsd: 5, costPerDayUsd: 20, sessionsPerDay: 10,
  maxPromptChars: 4000, maxTurns: 20,
};

const session = (extra: Partial<StudioSession> = {}): StudioSession => ({
  id: 7, prompt: 'p', scopes: ['modelo'], status: 'running', model: 'm', steps: 3, toolCalls: 5, inputTokens: 9000, outputTokens: 1000,
  costUsd: 0.028, createdAt: '2026-10-27T10:00:00Z', startedAt: '2026-10-27T10:00:00Z', warnings: [], canApply: false, trail: [],
  turns: 2, canSend: false, ...extra,
});

describe('ERP AI Studio (035)', () => {
  it('mergeTrail no duplica pasos si dos lecturas se cruzan o el flujo se reanuda, y los ordena', () => {
    const a = [step(1), step(2)];
    expect(mergeTrail(a, [])).toBe(a);
    const merged = mergeTrail(a, [step(3), step(2, { summary: 'otra vez' }), step(4)]);
    expect(merged.map((s) => s.seq)).toEqual([1, 2, 3, 4]);
    expect(mergeTrail(merged, [step(4), step(3)]).map((s) => s.seq)).toEqual([1, 2, 3, 4]);
  });

  it('consumption: cada medidor de la conversación contra su tope, sin pasarse (H5 CA4)', () => {
    const m = Object.fromEntries(consumption(session(), limits).map((x) => [x.key, x]));
    expect(m.turns).toMatchObject({ label: 'Mensajes', value: 2, max: 20 });
    expect(m.steps).toMatchObject({ value: 3, max: 30 });
    expect(m.tools).toMatchObject({ value: 5, max: 80 });
    expect(m.tokens).toMatchObject({ value: 10_000, max: 1_500_000 });
    expect(m.cost.text).toContain('0.028');
    expect(m.minutes).toBeUndefined();
    expect(Object.fromEntries(consumption(session({ costUsd: 9 }), limits).map((x) => [x.key, x])).cost.value).toBe(5);
  });

  it('reportNotes avisa pruebas sin copia aislada (R-033-11)', () => {
    const report = { tested: [{ description: 'La regla bloquea montos negativos', ok: true }], failures: [], risks: [] };
    expect(reportNotes(report, [step(1), step(2, { tool: 'aislado_preparar' })])).toHaveLength(1);
    expect(reportNotes(report, [step(1), step(2, { tool: 'aislado_crear' })])).toEqual([]);
    expect(reportNotes(report, [step(2, { tool: 'aislado_crear', status: 'error' })])[0]).toContain('fallaron');
    expect(reportNotes({ tested: [], failures: [], risks: [] }, [])).toEqual([]);
    expect(reportNotes(report, [], false)).toEqual([]);
    expect(reportNotes(null, [])).toEqual([]);
  });

  it('planFile da el JSON del plan para llevarlo como paquete', () => {
    expect(planFile({ id: 3, plan: null })).toBeNull();
    const f = planFile({ id: 3, plan: { description: 'd', operations: [{ op: 'createEntity' }] } as never })!;
    expect(f.name).toBe('erp-ai-studio-sesion-3.json');
    expect(JSON.parse(f.content).operations).toHaveLength(1);
  });
});

describe('ERP AI Studio como chat (037)', () => {
  it('groupTurns arma los turnos con el mensaje de la persona y sus segmentos en orden', () => {
    const trail = [
      user(1, 1, 'Agrega el peso'), text(2, 1, 'Voy a revisar.'), step(3), step(4, { tool: 'ver_catalogo', status: 'error' }),
      deliver(5, 1, { summary: 'Listo', plan: { operations: [] }, tested: [{ description: 'a', ok: true }], failures: ['f'], risks: [] }),
      user(6, 2, '¿Por qué?'), text(7, 2, 'Porque sí.'), step(8, { kind: 'limit', tool: null, turn: 2, status: 'error', summary: 'Tope' }),
    ];
    const turns = groupTurns([...trail].reverse(), 'Agrega el peso');
    expect(turns.map((t) => t.turn)).toEqual([1, 2]);
    expect(turns[0].user).toMatchObject({ seq: 1, text: 'Agrega el peso' });
    expect(turns[0].segments.map((s) => s.kind)).toEqual(['text', 'tools', 'deliver']);
    const tools = turns[0].segments[1];
    expect(tools.kind === 'tools' && tools.steps.map((s) => s.seq)).toEqual([3, 4]);
    const d = turns[0].segments[2];
    expect(d.kind === 'deliver' && d.delivery).toMatchObject({ summary: 'Listo', hasPlan: true, report: { failures: ['f'] } });
    expect(turns[1].user.text).toBe('¿Por qué?');
    expect(turns[1].segments.map((s) => s.kind)).toEqual(['text', 'notice']);
    expect(lastUserText(turns)).toBe('¿Por qué?');
  });

  it('groupTurns: una sesión anterior a la 036 (sin paso user) toma el pedido; una entrega que no compiló es una herramienta con error', () => {
    const turns = groupTurns([step(1, { turn: undefined }), deliver(2, 1, { summary: 'mal' }, 'error')], 'Pedido viejo');
    expect(turns).toHaveLength(1);
    expect(turns[0].user).toMatchObject({ seq: null, text: 'Pedido viejo' });
    expect(turns[0].segments).toHaveLength(1);
    expect(turns[0].segments[0].kind === 'tools' && turns[0].segments[0].steps).toHaveLength(2);
  });

  it('deliveryOf tolera una entrada cortada (texto) o con pruebas sueltas, sin confiar en sus tipos', () => {
    expect(deliveryOf(deliver(1, 1, '{"summary":"x","plan":{"operations":[ …'))).toMatchObject({ summary: 'x', hasPlan: true, report: null });
    const d = deliveryOf(deliver(1, 1, { summary: 3, tested: ['suelta', { description: 'b', ok: 'true' }], failures: 'no es lista' }));
    expect(d.summary).toBe('3');
    expect(d.report!.tested).toEqual([{ description: 'suelta', ok: false, detail: null }, { description: 'b', ok: false, detail: null }]);
    expect(d.report!.failures).toEqual([]);
    // Revisión adversarial 037 (F-06, descartado): un «"plan"» escrito por el modelo dentro de un texto no marca la entrega
    // como propuesta: en el JSON crudo esas comillas van escapadas.
    expect(deliveryOf(deliver(1, 1, '{"summary":"Revisa el \\"plan\\": {nada} y otro \\"plan\\":{"…')).hasPlan).toBe(false);
    expect(deliveryOf(deliver(1, 1, '{"summary":"x","plan" : {"operations":[ …')).hasPlan).toBe(true);
  });

  it('currentProposalSeq: la última entrega con plan; las anteriores quedan reemplazadas; sin planStatus no hay propuesta (H5 CA2)', () => {
    const trail = [deliver(2, 1, { summary: 'v1', plan: { operations: [{}] } }), deliver(5, 2, { summary: 'v2', plan: { operations: [{}] } }),
      deliver(8, 3, { summary: 'solo contesta' }), deliver(9, 3, { summary: 'mal', plan: {} }, 'error')];
    expect(currentProposalSeq(trail, 'proposed')).toBe(5);
    expect(currentProposalSeq(trail, 'applied')).toBe(5);
    expect(currentProposalSeq(trail, null)).toBeNull();
    expect(currentProposalSeq([], 'proposed')).toBeNull();
  });

  it('liveLabel dice qué hace el agente, nunca el texto', () => {
    expect(liveLabel(null, 'queued')).toBe('En cola…');
    expect(liveLabel({ kind: 'waiting' }, 'running')).toBe('Pensando…');
    expect(liveLabel({ kind: 'tool', tool: 'ver_catalogo' }, 'running')).toBe('Usando ver_catalogo…');
    expect(liveLabel({ kind: 'text', text: 'secreto' }, 'running')).toBe('Escribiendo…');
  });

  it('createSseParser une eventos partidos entre trozos, separa varios en uno e ignora comentarios', () => {
    const p = createSseParser();
    expect(p.push('id: 3\nevent: st')).toEqual([]);
    expect(p.push('ep\ndata: {"seq":3}\n')).toEqual([]);
    expect(p.push('\n: ping\n\nevent: live\r\ndata: {"kind":"waiting"}\r\n\r\nevent: end\ndata: {"reason"')).toEqual([
      { event: 'step', data: '{"seq":3}', id: '3' }, { event: 'live', data: '{"kind":"waiting"}', id: null },
    ]);
    expect(p.push(':"idle"}\n\n')).toEqual([{ event: 'end', data: '{"reason":"idle"}', id: null }]);
    expect(p.push('data: a\ndata: b\n\n')).toEqual([{ event: 'message', data: 'a\nb', id: null }]);
  });

  it('nextBackoff: 1, 2, 4 y 8 s; a la quinta falla deja de intentar (CLARIFY 4)', () => {
    expect([0, 1, 2, 3].map(nextBackoff)).toEqual([1000, 2000, 4000, 8000]);
    expect(nextBackoff(4)).toBeNull();
  });

  it('revealStep revela ~240 car/s por palabras completas y se pone al día si va atrás (CLARIFY 5)', () => {
    const target = 'uno dos tres cuatro cinco seis siete ocho nueve diez '.repeat(5);
    const next = revealStep(0, target, 100); // 24 caracteres en 100 ms
    expect(next).toBeGreaterThanOrEqual(24);
    expect(next).toBeLessThan(40);
    expect(/\s/.test(target[next]) || next === target.length).toBe(true);
    // Más de 2 s atrás (> 480 caracteres): medio segundo para ponerse al día.
    const long = 'a '.repeat(1000);
    expect(revealStep(0, long, 250)).toBeGreaterThanOrEqual(1000);
    expect(revealStep(5, 'hola', 16)).toBe(4);
    expect(REVEAL_CPS).toBe(240);
  });

  it('splitWords conserva el texto completo palabra por palabra', () => {
    const t = '  Hola,  mundo\n¿qué tal? ';
    expect(splitWords(t).join('')).toBe(t);
    expect(splitWords('a b c')).toEqual(['a ', 'b ', 'c']);
  });

  it('relativeTime: ahora, minutos, horas, días y fecha', () => {
    const now = Date.parse('2026-10-29T12:00:00Z');
    expect(relativeTime('2026-10-29T11:59:30Z', now)).toBe('ahora');
    expect(relativeTime('2026-10-29T11:55:00Z', now)).toMatch(/5 minutos/);
    expect(relativeTime('2026-10-29T09:00:00Z', now)).toMatch(/3 horas/);
    expect(relativeTime('2026-10-28T12:00:00Z', now)).toBe('ayer');
    expect(relativeTime('2026-10-01T12:00:00Z', now)).toMatch(/oct/);
    expect(relativeTime('no es fecha', now)).toBe('');
  });
});
