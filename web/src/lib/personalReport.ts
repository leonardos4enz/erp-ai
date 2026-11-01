import type { PersonalReportDef } from '../api/types';

/** Spec 041 (decisión 4): el explorador pide el resultado 400 ms después del último cambio. */
export const EXPLORE_DELAY_MS = 400;

/** Quita lo vacío (claves sin valor) para que la definición guardada sea la mínima. */
export function clean<T extends object>(o: T): T {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined && v !== '' && !(Array.isArray(v) && v.length === 0))) as T;
}

/**
 * ¿Falta algo para poder pedir el resultado? Un campo sin elegir no es un error del servidor: es la persona armando, y no
 * se le muestra una alerta por eso.
 */
export function missingPart(d: PersonalReportDef): string | null {
  const g = (d.groupBy ?? []).findIndex((x) => !x.field);
  if (g >= 0) return `Elige el campo de la agrupación ${g + 1}.`;
  const m = d.measures.findIndex((x) => (x.fn === 'calc' ? !x.expr?.trim() : x.fn !== 'count' && !x.field));
  if (m >= 0) return d.measures[m].fn === 'calc' ? `Escribe la expresión de la medida ${m + 1}.` : `Elige el campo de la medida ${m + 1}.`;
  return d.measures.length ? null : 'Agrega una medida.';
}
