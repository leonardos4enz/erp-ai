// Spec 030 (H4): la comparación canónica del modelador (026), fuera de sections/common.tsx para probarla sin React.

/** Quita lo vacío (undefined, nulo, '', [] y false): el servidor lo omite y así no aparecen cambios que nadie hizo. */
export function clean<T extends object>(o: T): T {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(o)) {
    if (v === undefined || v === null || v === '' || v === false || (Array.isArray(v) && v.length === 0)) continue;
    out[k] = v;
  }
  return out as T;
}

// Regresión (revisión exploratoria 026, F-03): sin quitar los nulos (el servidor los manda; el borrador no), solo abrir un
// elemento instalado avisaba de un cambio local que nadie hizo.
const canonical = (v: unknown) => JSON.stringify(v ?? null, (_k, x) => (x === null ? undefined : x && typeof x === 'object' && !Array.isArray(x)
  ? Object.fromEntries(Object.keys(x).sort().map((k) => [k, (x as Record<string, unknown>)[k]])) : x));
export const sameValue = (a: unknown, b: unknown) => canonical(a) === canonical(b);
