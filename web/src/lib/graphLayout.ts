/**
 * Spec 025: distribución jerárquica propia (decisión CLARIFY: sin librería), al estilo Sugiyama. Pura y determinista.
 * 1. Capa = camino más largo hacia un sumidero (las aristas de regreso de un ciclo se ignoran): lo que más refiere queda a la
 *    izquierda y los catálogos referidos a la derecha. Una capa con más de `maxPerColumn` cajas se parte en subcolumnas.
 * 2. Una arista que salta columnas recibe un punto de paso (nodo virtual) en cada columna intermedia: así pasa por un carril
 *    entre las cajas en lugar de atravesarlas.
 * 3. Barrido de baricentros (ida y vuelta) con transposición de vecinos para reducir cruces.
 * 4. Las etiquetas de las aristas se acomodan para no encimarse entre sí ni con las cajas (`placeLabels`).
 */
export interface LayoutBox { id: string; width: number; height: number }
export interface LayoutLink { id?: string; from: string; to: string }
export interface Placed { x: number; y: number; width: number; height: number; column: number }
export interface Waypoint { x1: number; x2: number; y: number }
export interface GraphLayout {
  pos: Map<string, Placed>;
  width: number;
  height: number;
  /** Cajas reales en orden de lectura (columna y luego de arriba abajo). */
  order: string[];
  /** Por arista (id): carriles que cruza en las columnas intermedias. */
  routes: Map<string, Waypoint[]>;
}

export interface LayoutOptions { gapX?: number; gapY?: number; maxPerColumn?: number; margin?: number }

const DUMMY_H = 10;
const DUMMY_GAP = 6;

export function layeredLayout(boxes: LayoutBox[], links: LayoutLink[], opts: LayoutOptions = {}): GraphLayout {
  const gapX = opts.gapX ?? 96;
  const gapY = opts.gapY ?? 28;
  const maxPer = Math.max(1, opts.maxPerColumn ?? 8);
  const margin = opts.margin ?? 24;
  const ids = boxes.map((b) => b.id);
  const known = new Set(ids);
  const byId = new Map(boxes.map((b) => [b.id, b]));
  const out = new Map<string, string[]>(ids.map((id) => [id, []]));
  const degree = new Map<string, number>(ids.map((id) => [id, 0]));
  const valid = links.filter((l) => known.has(l.from) && known.has(l.to) && l.from !== l.to);
  for (const l of valid) {
    out.get(l.from)!.push(l.to);
    degree.set(l.from, degree.get(l.from)! + 1);
    degree.set(l.to, degree.get(l.to)! + 1);
  }

  // 1. Rango por camino más largo; DFS que ignora las aristas hacia nodos en la pila (ciclos).
  const rank = new Map<string, number>();
  const visiting = new Set<string>();
  const sorted = [...ids].sort((a, b) => a.localeCompare(b));
  const visit = (id: string): number => {
    const r0 = rank.get(id);
    if (r0 !== undefined) return r0;
    visiting.add(id);
    let r = 0;
    for (const to of out.get(id)!) if (!visiting.has(to)) r = Math.max(r, visit(to) + 1);
    visiting.delete(id);
    rank.set(id, r);
    return r;
  };
  sorted.forEach(visit);
  const maxRank = Math.max(0, ...rank.values());
  const layers: string[][] = Array.from({ length: maxRank + 1 }, () => []);
  for (const id of sorted) layers[maxRank - rank.get(id)!].push(id);
  // Las cajas sueltas al final de su capa; las más conectadas primero (quedan al centro tras el barrido).
  for (const layer of layers)
    layer.sort((a, b) => Number(degree.get(a) === 0) - Number(degree.get(b) === 0) || degree.get(b)! - degree.get(a)! || a.localeCompare(b));
  const columns: string[][] = [];
  for (const layer of layers) for (let i = 0; i < layer.length; i += maxPer) columns.push(layer.slice(i, i + maxPer));
  const colOf = new Map<string, number>();
  columns.forEach((c, i) => c.forEach((id) => colOf.set(id, i)));

  // 2. Nodos virtuales: una arista que va de la columna a a la b (b > a + 1) pasa por a+1 … b-1.
  const dummies = new Map<string, string[]>(); // id de arista → nodos virtuales en orden
  const next = new Map<string, Set<string>>(); // adyacencia hacia la derecha (incluye virtuales)
  const prev = new Map<string, Set<string>>();
  const link = (a: string, b: string) => {
    if (!next.has(a)) next.set(a, new Set());
    if (!prev.has(b)) prev.set(b, new Set());
    next.get(a)!.add(b);
    prev.get(b)!.add(a);
  };
  valid.forEach((l, k) => {
    const a = colOf.get(l.from)!, b = colOf.get(l.to)!;
    if (b <= a) return; // de regreso o dentro de la misma capa: se dibuja por fuera
    const key = l.id ?? `#${k}`;
    let last = l.from;
    const chain: string[] = [];
    for (let c = a + 1; c < b; c++) {
      const d = `\u0000${key}\u0000${c}`;
      columns[c].push(d);
      colOf.set(d, c);
      chain.push(d);
      link(last, d);
      last = d;
    }
    link(last, l.to);
    dummies.set(key, chain);
  });
  const isDummy = (id: string) => id.charCodeAt(0) === 0;

  // 3. Barrido de baricentros con transposición.
  const index = new Map<string, number>();
  const reindex = (c: number) => columns[c].forEach((id, i) => index.set(id, i));
  columns.forEach((_, c) => reindex(c));
  const bary = (nbrs: Set<string> | undefined) => {
    if (!nbrs || nbrs.size === 0) return Number.NaN;
    let s = 0;
    for (const n of nbrs) s += index.get(n)!;
    return s / nbrs.size;
  };
  const crossings = (a: string, b: string) => {
    // Cruces entre a (arriba) y b (abajo) con la columna anterior y la siguiente.
    let n = 0;
    for (const side of [prev, next]) {
      const na = [...(side.get(a) ?? [])].map((x) => index.get(x)!);
      const nb = [...(side.get(b) ?? [])].map((x) => index.get(x)!);
      for (const x of na) for (const y of nb) if (x > y) n++;
    }
    return n;
  };
  const sortColumn = (c: number, nbrsOf: Map<string, Set<string>>) => {
    const items = columns[c].map((id, k) => ({ id, k, b: bary(nbrsOf.get(id)) }));
    // Sin vecinos: conserva su lugar relativo.
    items.forEach((it) => { if (Number.isNaN(it.b)) it.b = it.k; });
    items.sort((x, y) => x.b - y.b || x.k - y.k);
    columns[c] = items.map((it) => it.id);
    reindex(c);
  };
  for (let pass = 0; pass < 8; pass++) {
    if (pass % 2 === 0) for (let c = 1; c < columns.length; c++) sortColumn(c, prev);
    else for (let c = columns.length - 2; c >= 0; c--) sortColumn(c, next);
    for (let c = 0; c < columns.length; c++) {
      let improved = true;
      for (let guard = 0; improved && guard < 6; guard++) {
        improved = false;
        for (let i = 0; i + 1 < columns[c].length; i++) {
          const a = columns[c][i], b = columns[c][i + 1];
          if (crossings(b, a) < crossings(a, b)) {
            columns[c][i] = b; columns[c][i + 1] = a;
            reindex(c);
            improved = true;
          }
        }
      }
    }
  }

  // 4. Coordenadas: columnas centradas en la más alta.
  const heightOf = (id: string) => (isDummy(id) ? DUMMY_H : byId.get(id)!.height);
  const gapAfter = (id: string, nextId?: string) => (isDummy(id) || (nextId && isDummy(nextId)) ? DUMMY_GAP : gapY);
  const heights = columns.map((c) => c.reduce((s, id, i) => s + heightOf(id) + (i + 1 < c.length ? gapAfter(id, c[i + 1]) : 0), 0));
  const tallest = Math.max(0, ...heights);
  const pos = new Map<string, Placed>();
  const dummyY = new Map<string, number>();
  const colX: { x: number; w: number }[] = [];
  let x = margin;
  columns.forEach((col, ci) => {
    const width = Math.max(40, ...col.filter((id) => !isDummy(id)).map((id) => byId.get(id)!.width));
    colX.push({ x, w: width });
    let y = margin + (tallest - heights[ci]) / 2;
    col.forEach((id, i) => {
      if (isDummy(id)) dummyY.set(id, y + DUMMY_H / 2);
      else { const b = byId.get(id)!; pos.set(id, { x, y, width: b.width, height: b.height, column: ci }); }
      y += heightOf(id) + (i + 1 < col.length ? gapAfter(id, col[i + 1]) : 0);
    });
    x += width + gapX;
  });
  const routes = new Map<string, Waypoint[]>();
  for (const [key, chain] of dummies)
    routes.set(key, chain.map((d) => { const c = colX[colOf.get(d)!]; return { x1: c.x, x2: c.x + c.w, y: dummyY.get(d)! }; }));
  const order = columns.flatMap((c) => c.filter((id) => !isDummy(id)));
  return { pos, width: Math.max(x - gapX + margin, margin * 2), height: tallest + margin * 2, order, routes };
}

/** Nodos a `levels` saltos (sin dirección) de `center`, incluido él. */
export function neighborhood(center: string, links: LayoutLink[], levels: number): Set<string> {
  const adj = new Map<string, Set<string>>();
  const add = (a: string, b: string) => { if (!adj.has(a)) adj.set(a, new Set()); adj.get(a)!.add(b); };
  for (const l of links) { add(l.from, l.to); add(l.to, l.from); }
  const seen = new Set([center]);
  let frontier = [center];
  for (let i = 0; i < levels; i++) {
    const nxt: string[] = [];
    for (const n of frontier) for (const m of adj.get(n) ?? []) if (!seen.has(m)) { seen.add(m); nxt.push(m); }
    frontier = nxt;
  }
  return seen;
}

/** Punto medio de una curva cúbica (t = 0.5). */
export function bezierMid(p0: [number, number], p1: [number, number], p2: [number, number], p3: [number, number]): [number, number] {
  return [(p0[0] + 3 * p1[0] + 3 * p2[0] + p3[0]) / 8, (p0[1] + 3 * p1[1] + 3 * p2[1] + p3[1]) / 8];
}

export interface LabelBox { id: string; x: number; y: number; width: number; height: number }

/**
 * Acomoda etiquetas (centradas en x, y) para que no se encimen entre sí ni con las cajas: cada una se desliza en vertical,
 * alternando arriba y abajo, al hueco libre más cercano. Determinista (orden de entrada).
 */
export function placeLabels(labels: LabelBox[], obstacles: { x: number; y: number; width: number; height: number }[], pad = 3): Map<string, { x: number; y: number }> {
  const placed: { x: number; y: number; width: number; height: number }[] = [];
  const hits = (r: { x: number; y: number; width: number; height: number }) =>
    [...placed, ...obstacles].some((o) => r.x < o.x + o.width + pad && o.x < r.x + r.width + pad && r.y < o.y + o.height + pad && o.y < r.y + r.height + pad);
  const result = new Map<string, { x: number; y: number }>();
  for (const l of labels) {
    const base = { x: l.x - l.width / 2, y: l.y - l.height / 2, width: l.width, height: l.height };
    // Cerca de su flecha: primero en vertical y luego también corriéndose a los lados, a no más de ~3 alturas.
    let best = base;
    search: for (let step = 0; step < 9; step++) {
      const dy = Math.ceil(step / 2) * (l.height / 2 + pad) * (step % 2 === 0 ? 1 : -1);
      for (const dx of [0, -l.width / 3, l.width / 3]) {
        const r = { ...base, x: base.x + dx, y: base.y + dy };
        if (!hits(r)) { best = r; break search; }
      }
    }
    placed.push(best);
    result.set(l.id, { x: best.x + l.width / 2, y: best.y + l.height / 2 });
  }
  return result;
}
