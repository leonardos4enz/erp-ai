import { describe, expect, it } from 'vitest';
import type { StudioLimits, StudioSession, StudioStep } from '../../api/types';
import { consumption, elapsedMinutes, mergeTrail, planFile, reportNotes } from '../studio';

const step = (seq: number, extra: Partial<StudioStep> = {}): StudioStep => ({
  seq, kind: 'tool', tool: 'listar_catalogos', status: 'ok', summary: '200', durationMs: 10, at: '2026-10-27T10:00:00Z', ...extra,
});

const limits: StudioLimits = { maxSteps: 30, maxToolCalls: 80, maxTokens: 1_500_000, maxMinutes: 20, maxCostUsd: 5, costPerDayUsd: 20, sessionsPerDay: 10, maxPromptChars: 4000 };

const session = (extra: Partial<StudioSession> = {}): StudioSession => ({
  id: 7, prompt: 'p', scopes: ['modelo'], status: 'running', model: 'm', steps: 3, toolCalls: 5, inputTokens: 9000, outputTokens: 1000,
  costUsd: 0.028, createdAt: '2026-10-27T10:00:00Z', startedAt: '2026-10-27T10:00:00Z', warnings: [], canApply: false, trail: [], ...extra,
});

describe('ERP AI Studio (035)', () => {
  it('mergeTrail no duplica pasos si dos sondeos se cruzan, y los ordena', () => {
    const a = [step(1), step(2)];
    expect(mergeTrail(a, [])).toBe(a);
    const merged = mergeTrail(a, [step(3), step(2, { summary: 'otra vez' }), step(4)]);
    expect(merged.map((s) => s.seq)).toEqual([1, 2, 3, 4]);
    expect(mergeTrail(merged, [step(4), step(3)]).map((s) => s.seq)).toEqual([1, 2, 3, 4]);
  });

  it('consumption: cada medidor contra su tope, sin pasarse, y los minutos desde que empezó', () => {
    const now = Date.parse('2026-10-27T10:03:00Z');
    const m = Object.fromEntries(consumption(session(), limits, now).map((x) => [x.key, x]));
    expect(m.steps).toMatchObject({ value: 3, max: 30 });
    expect(m.tools).toMatchObject({ value: 5, max: 80 });
    expect(m.tokens).toMatchObject({ value: 10_000, max: 1_500_000 });
    expect(m.minutes.value).toBeCloseTo(3);
    expect(m.cost.text).toContain('0.028');
    // Un valor por encima del tope (p. ej. el costo de la llamada cortada) no rebasa el medidor.
    expect(Object.fromEntries(consumption(session({ costUsd: 9 }), limits, now).map((x) => [x.key, x])).cost.value).toBe(5);
  });

  it('elapsedMinutes: en cola es 0; terminada, hasta que terminó', () => {
    expect(elapsedMinutes({ startedAt: null }, Date.now())).toBe(0);
    expect(elapsedMinutes({ startedAt: '2026-10-27T10:00:00Z', finishedAt: '2026-10-27T10:01:30Z' }, Date.parse('2026-10-27T12:00:00Z'))).toBeCloseTo(1.5);
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
