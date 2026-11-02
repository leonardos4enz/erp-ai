import { useEffect, useRef } from 'react';
import type { AvatarState } from '../../lib/companion';

/*
 * Spec 047 (H3): el avatar del Acompañante. Port a React de <fleet-eyes> (Grok Bot Harness · agent-fleet, derivado de
 * <agent-eyes> de AI Engineer · mascot.js; ambos del dueño). Cambios del port (CLARIFY 3):
 * - Sin Shadow DOM ni <style>: la CSP es `style-src 'self'`; los estilos están en styles/companion.css y la animación mueve
 *   atributos SVG (transform, d, opacity, stroke-width). El estado va en `data-state` y los colores son los tokens.
 * - Los estados de la flota se cambian por los del chat; sin la etiqueta de la frente ni seguir al puntero.
 * - Con `prefers-reduced-motion` la cara toma la pose del estado de golpe y se queda quieta (sin parpadeo, sacádicos ni saltos).
 * Motor: un solo bucle rAF compartido, que corre si hay algún avatar visible (IntersectionObserver) y la pestaña está activa.
 * Física: poses con resortes amortiguados (leve overshoot), salto balístico y squash & stretch al despegar y aterrizar.
 */

/* ---------- Poses (parámetros del ojo izquierdo; el derecho es su espejo) ----------
   sx/sy: escala de la píldora · po: opacidad de la píldora · dy: desplazamiento vertical del ojo · rot: inclinación
   lidY/lidS: altura y pendiente del párpado (máscara) · lidA: párpado extra del ojo derecho · L: trazo «cerrado» · lo/lw: su
   opacidad y grosor · blush · gx/gy/gw: mirada forzada y su peso · frot/fdy: giro y caída de la cara · sag: aplastamiento ·
   bob: vaivén · brA/brP: respiración · sacc: radio de sacádicos */
interface Pose {
  sx: number; sy: number; po: number; dy: number; rot: number; lidY: number; lidS: number; lidA: number; L: number[]; lo: number; lw: number;
  blush: number; gx: number; gy: number; gw: number; frot: number; fdy: number; sag: number; bob: number; brA: number; brP: number; sacc: number;
}
type NumKey = Exclude<keyof Pose, 'L'>;

const FLAT = [-7, 0, -3.5, 0, 0, 0, 3.5, 0, 7, 0];
const BASE: Pose = {
  sx: 1, sy: 1, po: 1, dy: 0, rot: 0, lidY: -24, lidS: 0, lidA: 0, L: FLAT, lo: 0, lw: 5.6, blush: 0,
  gx: 0, gy: 0, gw: 0, frot: 0, fdy: 0, sag: 0, bob: 0, brA: 0.0075, brP: 4000, sacc: 1,
};
const HAPPY_L = [-7.5, 3, -6.6, -5.2, 0, -5.2, 6.6, -5.2, 7.5, 3];
const SAD: Pose = { ...BASE, sx: 0.96, sy: 0.86, dy: 3.5, lidY: -7.5, lidS: -0.42, gy: 0.6, gw: 0.75, fdy: 1.6, brA: 0.005, brP: 5600, sacc: 0.35 };
/** Pose de cada estado del chat (las de la flota: idle, think, code, alert, happy, fail). */
const POSE: Record<AvatarState, Pose> = {
  idle: BASE,
  thinking: { ...BASE, sx: 0.94, sy: 0.94, lidY: -15, lidS: 0.06, frot: -4, brA: 0.006, brP: 4800 },
  tool: { ...BASE, sx: 0.84, sy: 0.8, dy: 1, lidY: -9.5, brA: 0.005, brP: 3600 },
  waiting: { ...BASE, sx: 1.17, sy: 1.17, lidY: -28, brA: 0.009, brP: 3000 },
  done: { ...BASE, sy: 0.14, po: 0, dy: -2.5, lo: 1, lw: 5.8, L: HAPPY_L, blush: 0.6, gy: -0.15, gw: 0.65, brA: 0.01 },
  error: { ...SAD, fdy: 3.4, frot: -3, sag: 0.035, brP: 6400, blush: 0 },
};
const NUM_KEYS = (Object.keys(BASE) as (keyof Pose)[]).filter((k): k is NumKey => k !== 'L');
const STATES = Object.keys(POSE) as AvatarState[];

const lerp = (a: number, b: number, k: number) => a + (b - a) * k;
const clamp = (v: number, a: number, b: number) => (v < a ? a : v > b ? b : v);
const rand = (a: number, b: number) => a + Math.random() * (b - a);
const easeOut = (k: number) => 1 - Math.pow(1 - k, 3);
const easeIn = (k: number) => k * k;
const f = (n: number) => (Math.round(n * 100) / 100).toString();
const clonePose = (p: Pose): Pose => ({ ...p, L: p.L.slice() });
const zeroPose = (): Pose => { const z = { ...BASE, L: new Array(10).fill(0) }; for (const k of NUM_KEYS) z[k] = 0; return z; };

/** Resorte amortiguado por clave (semi-implícito, subpasos de 8 ms). ζ≈0.7: llega rápido con un leve overshoot. */
function springPose(x: Pose, v: Pose, T: Pose, dt: number, k: number, c: number) {
  const n = Math.max(1, Math.ceil(dt / 8));
  const h = dt / n / 1000;
  for (let s = 0; s < n; s++) {
    for (const key of NUM_KEYS) { v[key] += (k * (T[key] - x[key]) - c * v[key]) * h; x[key] += v[key] * h; }
    for (let i = 0; i < 10; i++) { v.L[i] += (k * (T.L[i] - x.L[i]) - c * v.L[i]) * h; x.L[i] += v.L[i] * h; }
  }
}

/* ---------- Motor global ---------- */
const mq = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
const engine = { items: new Set<Eyes>(), raf: 0, last: 0, bound: false, reduce: !!mq?.matches };
function onVis() { kick(); }
function onReduce() { engine.reduce = !!mq?.matches; for (const el of engine.items) el.tick(0); kick(); }
function bind() {
  if (engine.bound) return;
  engine.bound = true;
  document.addEventListener('visibilitychange', onVis);
  mq?.addEventListener?.('change', onReduce);
}
function unbind() {
  if (!engine.bound) return;
  engine.bound = false;
  document.removeEventListener('visibilitychange', onVis);
  mq?.removeEventListener?.('change', onReduce);
}
function frame(ts: number) {
  engine.raf = requestAnimationFrame(frame);
  const dt = engine.last ? Math.min(64, ts - engine.last) : 16;
  engine.last = ts;
  for (const el of engine.items) if (el.visible) el.tick(dt);
}
/** El bucle corre solo con algún avatar visible, la pestaña activa y movimiento permitido. */
function kick() {
  let want = !document.hidden && !engine.reduce;
  if (want) { want = false; for (const el of engine.items) if (el.visible) { want = true; break; } }
  if (want && !engine.raf) { engine.last = 0; engine.raf = requestAnimationFrame(frame); }
  else if (!want && engine.raf) { cancelAnimationFrame(engine.raf); engine.raf = 0; }
  for (const el of engine.items) el.running(want && el.visible);
}
const io = typeof IntersectionObserver !== 'undefined'
  ? new IntersectionObserver((entries) => {
    for (const e of entries) for (const el of engine.items) if (el.svg === e.target) el.visible = e.isIntersecting;
    kick();
  })
  : null;

interface EyeEls { side: number; g: SVGGElement; lid: SVGPathElement; pill: SVGGElement; line: SVGPathElement }

/** La cara: el estado, la física y el pintado (atributos SVG, con caché para no tocar el DOM si no cambian). */
class Eyes {
  visible: boolean;
  private t = 0;
  private base: AvatarState = 'idle';
  private state: AvatarState | null = null;
  private stateAt = 0;
  private pose: Pose | null = null;
  private vel: Pose | null = null;
  private gx = 0; private gy = 0;
  private sac: { fx: number; fy: number; tx: number; ty: number; t0: number; d: number; w: [number, number] } | null = null;
  private nextSac = rand(300, 900);
  private nextBlink = rand(1200, 3500); private blinkT0 = -1; private blinkQ = -1;
  private brPh = 0;
  private hy = 0; private hv = 0; private air = false; private jq = 0; private jh = 8; private launchAt = 0; private nextHop = Infinity;
  private sq = 0; private sqV = 0;
  private read = { x: -0.62, line: 0 }; private arcA = -1.6; private arcDir = 1;
  private cache = new Map<Element, Record<string, string>>();

  constructor(readonly svg: SVGSVGElement, private head: SVGGElement, private blushG: SVGGElement, private eyes: EyeEls[], private accs: SVGGElement[]) {
    this.visible = !io;
  }

  setState(s: AvatarState) { this.base = s; if (!engine.raf || !this.visible) this.tick(0); }
  running(on: boolean) { this.svg.toggleAttribute('data-paused', !on); }

  private set(el: Element, attr: string, val: string) {
    let c = this.cache.get(el);
    if (!c) { c = {}; this.cache.set(el, c); }
    if (c[attr] !== val) { c[attr] = val; el.setAttribute(attr, val); }
  }

  private hop(n = 1, h = 8) {
    if (engine.reduce) return;
    this.jq = Math.max(1, n | 0); this.jh = h;
    if (!this.air && !this.launchAt) { this.launchAt = this.t + 80; this.sqV += 1.9; } // anticipación: se agacha
  }

  private blink() {
    if (this.pose && this.pose.po < 0.5) return;
    if (this.blinkT0 < 0) this.blinkT0 = this.t; else this.blinkQ = this.t + 200;
  }

  private enter(state: AvatarState, t: number) {
    this.state = state;
    this.stateAt = t;
    this.sac = null;
    this.nextSac = t + rand(120, 380);
    this.nextHop = Infinity;
    this.read = { x: -0.62, line: 0 };
    this.svg.setAttribute('data-state', state);
    for (const a of this.accs) a.classList.toggle('on', a.dataset.s === state);
    if (engine.reduce) return;
    switch (state) {
      case 'waiting': this.hop(2, 6); this.nextHop = t + rand(2400, 3200); break; // pide atención, como «atencion»
      case 'done': this.hop(2, 7); break;
      case 'error': this.sqV += 2.2; break;   // se desinfla
      case 'tool': this.sqV += 0.8; break;
      default: this.sqV += 0.6;
    }
  }

  /** Mirada de reposo sin movimiento (prefers-reduced-motion). */
  private restGaze(state: AvatarState) {
    if (state === 'thinking') return { x: 0.45, y: -0.6 };
    if (state === 'tool') return { x: 0, y: 0.45 };
    return { x: 0, y: 0 };
  }

  /** Siguiente sacádico según el estado: hacia arriba al pensar, leyendo en renglones al consultar. */
  private pick(state: AvatarState, sacc: number): { x: number; y: number; d?: number; w: [number, number] } {
    switch (state) {
      case 'thinking': {
        this.arcA += this.arcDir * rand(0.3, 0.6);
        if (this.arcA > -0.3) { this.arcA = -0.3; this.arcDir = -1; }
        if (this.arcA < -2.3) { this.arcA = -2.3; this.arcDir = 1; }
        return { x: 0.36 + 0.42 * Math.cos(this.arcA), y: -0.42 + 0.3 * Math.sin(this.arcA), d: 300, w: [600, 1400] };
      }
      case 'tool': {
        const r = this.read;
        r.x += rand(0.16, 0.28);
        let d = 60; let w: [number, number] = [110, 240];
        if (r.x > 0.62) { r.x = -0.62; r.line = (r.line + 1) % 4; d = 170; w = [260, 420]; }
        return { x: r.x, y: 0.4 + r.line * 0.07, d, w };
      }
      case 'waiting': return { x: rand(-0.06, 0.06), y: rand(-0.06, 0.04), w: [1400, 2600] };
      default: {
        let x = 0; let y = 0;
        if (Math.random() > 0.22 || Math.hypot(this.gx, this.gy) < 0.15) {
          const ang = Math.random() * Math.PI * 2; const rad = Math.sqrt(Math.random()) * 0.62 * sacc;
          x = Math.cos(ang) * rad; y = Math.sin(ang) * rad * 0.65;
        }
        return { x, y, w: [600, 2500] };
      }
    }
  }

  tick(dt: number) {
    this.t += dt;
    const t = this.t;
    const reduce = engine.reduce;
    const state = this.base;
    if (state !== this.state) this.enter(state, t);
    const target = POSE[state];
    if (!this.pose || reduce) { this.pose = clonePose(target); this.vel = zeroPose(); }
    else if (dt > 0) springPose(this.pose, this.vel!, target, dt, 170, 18.5); // ζ≈0.71, leve overshoot
    const p = this.pose;
    const st = t - this.stateAt;

    if (t >= this.nextHop) { this.hop(2, 5); this.nextHop = t + rand(2600, 3400); }

    // --- Mirada ---
    if (reduce) {
      const g = this.restGaze(state);
      this.gx = g.x; this.gy = g.y; this.sac = null;
    } else {
      if (!this.sac && t >= this.nextSac) {
        const n = this.pick(state, p.sacc);
        const dist = Math.hypot(n.x - this.gx, n.y - this.gy);
        this.sac = { fx: this.gx, fy: this.gy, tx: n.x, ty: n.y, t0: t, d: n.d || 85 + 70 * Math.min(1, dist), w: n.w };
        if (dist > 0.45 && Math.random() < 0.2 && state !== 'tool') this.blink();
      }
      if (this.sac) {
        const s = this.sac;
        const k = clamp((t - s.t0) / s.d, 0, 1);
        const e = easeOut(k);
        this.gx = lerp(s.fx, s.tx, e);
        this.gy = lerp(s.fy, s.ty, e);
        if (k >= 1) { this.sac = null; this.nextSac = t + rand(s.w[0], s.w[1]); }
      }
    }
    const drift = reduce ? 0 : 1;
    const gx0 = this.gx + drift * 0.018 * Math.sin(t / 830);
    const gy0 = this.gy + drift * 0.014 * Math.sin(t / 610 + 1.3);
    let gx = lerp(gx0, p.gx, clamp(p.gw, 0, 1));
    let gy = lerp(gy0, p.gy, clamp(p.gw, 0, 1));
    const gl = Math.hypot(gx, gy);
    if (gl > 1) { gx /= gl; gy /= gl; }

    // --- Parpadeo (no con movimiento reducido: la cara se queda quieta) ---
    let lid = 1;
    if (!reduce) {
      const open = p.po >= 0.5;
      if (open && this.blinkT0 < 0 && t >= this.nextBlink) {
        this.blinkT0 = t;
        this.nextBlink = t + rand(2000, 6000);
        if (Math.random() < 0.15) this.blinkQ = t + 200;
      }
      if (this.blinkQ >= 0 && t >= this.blinkQ && this.blinkT0 < 0) { this.blinkT0 = t; this.blinkQ = -1; }
      if (this.blinkT0 >= 0) {
        const D = 140;
        const bt = t - this.blinkT0;
        const tc = D * 0.4;
        const c = bt < tc ? easeIn(bt / tc) : 1 - easeOut(clamp((bt - tc) / (D - tc), 0, 1));
        lid = 1 - 0.9 * c;
        if (bt >= D) { this.blinkT0 = -1; lid = 1; }
      }
    } else { this.blinkT0 = -1; this.blinkQ = -1; }

    // --- Respiración, salto balístico y squash & stretch ---
    let breath = 1; let bob = 0;
    if (!reduce) {
      this.brPh += (dt / Math.max(800, p.brP)) * Math.PI * 2;
      if (this.brPh > Math.PI * 2) this.brPh -= Math.PI * 2;
      breath = 1 + p.brA * (1 - Math.cos(this.brPh));
      bob = -(1 - Math.cos((st / 1500) * Math.PI * 2)) * 0.5 * 1.4 * clamp(p.bob, 0, 1.2);
      if (this.launchAt && t >= this.launchAt) {
        this.launchAt = 0;
        this.air = true;
        this.hv = -Math.sqrt(2 * 520 * this.jh);
        this.sqV -= 2.8; // despegue: se estira
      }
      const n = Math.max(1, Math.ceil(dt / 8));
      const h = dt / n / 1000;
      for (let i = 0; i < n; i++) {
        if (this.air) {
          this.hv += 520 * h;
          this.hy += this.hv * h;
          if (this.hy >= 0 && this.hv > 0) {
            const impact = this.hv;
            this.hy = 0; this.hv = 0; this.air = false;
            this.sqV += (impact / 95) * 3.1; // aterrizaje: se aplasta
            if (--this.jq > 0) { this.jh *= 0.72; this.launchAt = t + 70; }
          }
        }
        const sqT = this.air ? -clamp(Math.abs(this.hv) / 95, 0, 1) * 0.075 : p.sag;
        this.sqV += (520 * (sqT - this.sq) - 11 * this.sqV) * h; // resorte gelatinoso (ζ≈0.24)
        this.sq += this.sqV * h;
      }
      this.sq = clamp(this.sq, -0.2, 0.22);
    } else {
      this.hy = 0; this.hv = 0; this.air = false; this.launchAt = 0; this.jq = 0;
      this.sq = p.sag; this.sqV = 0;
    }

    // --- Pintado ---
    const fx = gx * 1.8;
    const fy = gy * 1.3 + p.fdy + this.hy + bob;
    const sxF = breath * (1 + this.sq);
    const syF = breath * (1 - this.sq);
    this.set(this.head, 'transform', `translate(${f(50 + fx)} ${f(97 + fy)}) rotate(${f(p.frot)}) scale(${sxF.toFixed(4)} ${syF.toFixed(4)}) translate(-50 -97)`);
    this.set(this.blushG, 'opacity', f(clamp(p.blush, 0, 1) * 0.55));
    this.set(this.blushG, 'transform', `translate(${f(gx * 3)} ${f(gy * 1.5)})`);
    const ex = gx * 10;
    const ey = gy * 7.5;
    const squash = 1 - 0.1 * Math.abs(gx);
    for (const e of this.eyes) {
      const s = e.side;
      this.set(e.g, 'transform', `translate(${f(50 + s * 16 + ex)} ${f(52 + ey + p.dy)}) rotate(${f(p.rot * -s)})`);
      const slope = p.lidS * -s;
      const ly = p.lidY + (s > 0 ? p.lidA : 0);
      this.set(e.lid, 'd', `M-40 ${f(ly - slope * 40)}L40 ${f(ly + slope * 40)}L40 -60L-40 -60Z`);
      this.set(e.pill, 'transform', `translate(0 3) scale(${f(p.sx * squash)} ${f(Math.max(0.02, p.sy * lid))}) translate(0 -3)`);
      this.set(e.pill, 'opacity', f(clamp(p.po, 0, 1)));
      const L = p.L;
      const X = (i: number) => f(L[i] * -s);
      const Y = (i: number) => f(L[i]);
      this.set(e.line, 'd', `M${X(0)} ${Y(1)}Q${X(2)} ${Y(3)} ${X(4)} ${Y(5)}Q${X(6)} ${Y(7)} ${X(8)} ${Y(9)}`);
      this.set(e.line, 'stroke-width', f(p.lw));
      this.set(e.line, 'opacity', f(clamp(p.lo, 0, 1)));
    }
  }
}

/* ---------- Plantilla ---------- */
const SQUIRCLE = 'M50 3C84 3 97 16 97 50C97 84 84 97 50 97C16 97 3 84 3 50C3 16 16 3 50 3Z';
let uid = 0;

/** Accesorios en una caja local de 32×32, arriba a la derecha de la cara (los de la flota que corresponden a cada estado). */
function Accessory({ s }: { s: AvatarState }) {
  switch (s) {
    case 'thinking': // libreta que se escribe («planificando»)
      return (<><rect x="6" y="3" width="22" height="27" rx="2" /><path className="thin" d="M3 8h5M3 14h5M3 20h5M3 26h5" />
        <path className="ln ln1" d="M11 10h13" /><path className="ln ln2" d="M11 16h13" /><path className="ln ln3" d="M11 22h8" /></>);
    case 'tool': // llave («arreglo»)
      return <g className="wr"><path d="M27.9 9A6 6 0 1 1 23 4.1" /><path className="thick" d="M17.8 14.2L6 26" /></g>;
    case 'waiting': // globo con «!» («atencion»)
      return (<g className="pop"><path d="M5 3h22a3 3 0 0 1 3 3v13a3 3 0 0 1-3 3H14l-6 5.5V22H5a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3z" />
        <path className="thick" d="M16 7.5v7" /><circle className="fill" cx="16" cy="18.2" r="1.5" /></g>);
    case 'done': // palomita que se dibuja
      return <path className="chk" d="M5 17l7 7L28 7" />;
    case 'error': // nube con gotas («fallo»)
      return (<><path className="cloud" d="M9 21a5 5 0 0 1 .8-9.9 7 7 0 0 1 13.5 1.6A4.2 4.2 0 0 1 24 21z" />
        <path className="drop d1" d="M12 24.5v3" /><path className="drop d2" d="M17 24.5v3" /><path className="drop d3" d="M22 24.5v3" /></>);
    default:
      return null;
  }
}

/**
 * H3: la cara del Acompañante. Decorativa (`aria-hidden`): quien la usa anuncia el estado en texto. `size` < 48 usa la caja
 * ajustada y no muestra accesorios.
 */
export function CompanionAvatar({ state, size = 40 }: { state: AvatarState; size?: number }) {
  const idRef = useRef<string>('');
  if (!idRef.current) idRef.current = `cmpav${++uid}`;
  const id = idRef.current;
  const svg = useRef<SVGSVGElement>(null);
  const eyes = useRef<Eyes | null>(null);
  const small = size < 48;

  useEffect(() => {
    const root = svg.current!;
    const q = <T extends Element>(sel: string) => root.querySelector(sel) as T;
    const e = new Eyes(root, q<SVGGElement>('.cmp-av-head'), q<SVGGElement>('.cmp-av-blush'),
      [...root.querySelectorAll<SVGGElement>('.cmp-av-eye')].map((g) => ({
        side: Number(g.dataset.side), g, lid: g.querySelector('.cmp-av-lid')!, pill: g.querySelector('.cmp-av-pill')!, line: g.querySelector('.cmp-av-line')!,
      })),
      [...root.querySelectorAll<SVGGElement>('.cmp-av-acc')]);
    eyes.current = e;
    engine.items.add(e);
    bind();
    io?.observe(root);
    e.setState(state);
    kick();
    return () => {
      io?.unobserve(root);
      engine.items.delete(e);
      if (!engine.items.size) unbind();
      eyes.current = null;
      kick();
    };
    // El estado se aplica en el otro efecto; este solo monta y desmonta el motor.
  }, []);

  useEffect(() => { eyes.current?.setState(state); }, [state]);

  const eye = (side: number) => (
    <g className="cmp-av-eye" data-side={side}>
      <mask id={`${id}-m${side}`} maskUnits="userSpaceOnUse" x="-40" y="-60" width="80" height="120">
        <rect x="-40" y="-60" width="80" height="120" fill="#fff" />
        <path className="cmp-av-lid" fill="#000" d="M-40 -24L40 -24L40 -60L-40 -60Z" />
      </mask>
      <g mask={`url(#${id}-m${side})`}>
        <g className="cmp-av-pill">
          <rect className="cmp-av-eye-fill" x="-8" y="-15" width="16" height="30" rx="8" />
          {size >= 28 && <circle className="cmp-av-glint" cx="-2.8" cy="-8.4" r="2.5" />}
        </g>
      </g>
      <path className="cmp-av-line" fill="none" d="" opacity="0" />
    </g>
  );

  return (
    <svg ref={svg} className="cmp-av" width={size} height={size} viewBox={small ? '-1 -1 102 102' : '-20 -26 140 140'}
      aria-hidden="true" focusable="false" data-state={state} data-small={small || undefined}>
      <defs>
        <linearGradient id={`${id}-hl`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#fff" stopOpacity=".26" />
          <stop offset=".46" stopColor="#fff" stopOpacity="0" />
        </linearGradient>
        <linearGradient id={`${id}-sh`} x1="0" y1="0" x2="0" y2="1">
          <stop offset=".62" stopColor="#000" stopOpacity="0" />
          <stop offset="1" stopColor="#000" stopOpacity=".12" />
        </linearGradient>
        <radialGradient id={`${id}-bl`}>
          <stop offset="0" stopColor="#fff" stopOpacity="1" />
          <stop offset="1" stopColor="#fff" stopOpacity="0" />
        </radialGradient>
        <mask id={`${id}-blm`} maskContentUnits="objectBoundingBox">
          <rect width="1" height="1" fill={`url(#${id}-bl)`} />
        </mask>
      </defs>
      <g className="cmp-av-head">
        <path className="cmp-av-face" d={SQUIRCLE} />
        <path d={SQUIRCLE} fill={`url(#${id}-sh)`} />
        <path d={SQUIRCLE} fill={`url(#${id}-hl)`} />
        <ellipse cx="40" cy="14" rx="15" ry="3.6" fill="#fff" opacity=".12" />
        <g className="cmp-av-blush" opacity="0">
          <ellipse className="cmp-av-blush-dot" cx="22" cy="68" rx="9" ry="5.5" mask={`url(#${id}-blm)`} />
          <ellipse className="cmp-av-blush-dot" cx="78" cy="68" rx="9" ry="5.5" mask={`url(#${id}-blm)`} />
        </g>
        <g>{eye(-1)}{eye(1)}</g>
      </g>
      {!small && (
        <g className="cmp-av-accs" transform="translate(88 -24)">
          {STATES.map((s) => <g key={s} className="cmp-av-acc" data-s={s}><Accessory s={s} /></g>)}
        </g>
      )}
    </svg>
  );
}
