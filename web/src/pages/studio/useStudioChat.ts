import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/client';
import { studioApi } from '../../api/endpoints';
import type { StudioEndReason, StudioLive, StudioSession, StudioStep, StudioStreamSession } from '../../api/types';
import { createSseParser, isActive, lastSeq, mergeTrail, nextBackoff } from '../../lib/studio';

export type Connection = 'ok' | 'reconnecting' | 'offline';

/** Mensaje enviado que todavía no llega como paso `user` (se ve al instante, como en Claude: H2 CA3). */
export interface Pending { text: string; after: number }

const sleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve) => {
  const t = window.setTimeout(resolve, ms);
  signal.addEventListener('abort', () => { window.clearTimeout(t); resolve(); }, { once: true });
});

/**
 * Spec 037 (H3): una conversación de Studio. Lectura completa al abrir; mientras un turno corre, el flujo SSE de la 036
 * (`fetch`, CLARIFY 4) con `after` = último paso recibido; al terminar el turno, otra lectura completa (la propuesta con su
 * vista previa y su huella). Si el flujo se corta, se reanuda con espera creciente; tras 4 fallas, «Sin conexión».
 */
export function useStudioChat(id: number, onListChanged: () => void, dataTools: ReadonlySet<string> = new Set()) {
  const [session, setSessionState] = useState<StudioSession | null>(null);
  const [live, setLive] = useState<StudioLive | null>(null);
  const [conn, setConn] = useState<Connection>('ok');
  const [loadError, setLoadError] = useState<unknown>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const current = useRef<StudioSession | null>(null);
  const stream = useRef<AbortController | null>(null);
  const alive = useRef(true);
  const listChanged = useRef(onListChanged);
  listChanged.current = onListChanged;
  const data = useRef(dataTools);
  data.current = dataTools;
  // Revisión adversarial 037 (F-04): cada lectura completa (o respuesta de enviar o detener) toma un turno al pedirse; una
  // que llega después de otra más nueva no la pisa con un estado viejo.
  const ticket = useRef(0);
  /** Alguien pidió seguir el avance mientras el flujo abierto cerraba su turno (F-04). */
  const again = useRef(false);
  const applied = useRef(0);

  const setSession = useCallback((next: StudioSession | ((s: StudioSession) => StudioSession)) => {
    const value = typeof next === 'function' ? (current.current ? next(current.current) : null) : next;
    if (!value) return;
    current.current = value;
    if (alive.current) setSessionState(value);
  }, []);

  /** Aplica una foto completa del servidor si nadie aplicó una pedida después. */
  const snapshot = useCallback(async (request: () => Promise<StudioSession>, merge?: (prev: StudioSession, s: StudioSession) => StudioSession) => {
    const mine = ++ticket.current;
    const s = await request();
    if (mine > applied.current) {
      applied.current = mine;
      setSession(merge && current.current ? merge(current.current, s) : s);
    }
    return s;
  }, [setSession]);

  const full = useCallback(() => snapshot(() => studioApi.session(id)), [id, snapshot]);

  const runStream = useCallback(async (ctrl: AbortController) => {
    try { await streamLoop(ctrl); } finally {
      // Se suelta aquí y no en un `.finally` posterior: un `follow` que llegue justo después ya abre otro flujo.
      if (stream.current === ctrl) stream.current = null;
    }
  }, [id, full, setSession]);

  async function streamLoop(ctrl: AbortController) {
    let attempt = 0;
    const { signal } = ctrl;
    while (!signal.aborted) {
      let ended: StudioEndReason | null = null;
      try {
        const res = await fetch(studioApi.streamUrl(id, lastSeq(current.current?.trail ?? [])), {
          headers: { Accept: 'text/event-stream' }, credentials: 'same-origin', signal,
        });
        // 401 y 404: la lectura con `api()` lo resuelve (inicio de sesión o «ya no existe»).
        if (res.status === 401 || res.status === 404) { await full().catch((e) => alive.current && setLoadError(e)); return; }
        if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);
        if (alive.current) setConn('ok');
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        const parser = createSseParser();
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          for (const ev of parser.push(decoder.decode(value, { stream: true }))) {
            let parsed: unknown;
            try { parsed = JSON.parse(ev.data); } catch { continue; } // un evento mal formado se ignora (F-05)
            if (ev.event === 'step') {
              // F-05: solo un paso nuevo o el final cuentan como avance: un flujo que se corta tras `session`/`live` (lo que
              // el servidor manda siempre al abrir) sigue sumando fallas hasta «Sin conexión».
              attempt = 0;
              setSession((s) => ({ ...s, trail: mergeTrail(s.trail, [parsed as StudioStep]) }));
            } else if (ev.event === 'session') {
              const x = parsed as StudioStreamSession;
              const changed = current.current?.status !== x.status;
              setSession((s) => ({ ...s, ...x, error: x.error ?? null }));
              if (changed) listChanged.current();
            } else if (ev.event === 'live') {
              const l = parsed as StudioLive;
              if (alive.current) setLive(l.kind === 'none' ? null : l);
            } else if (ev.event === 'end') {
              ended = (parsed as { reason: StudioEndReason }).reason;
            }
          }
          if (ended) { void reader.cancel().catch(() => undefined); break; }
        }
      } catch {
        if (signal.aborted) return;
      }
      if (signal.aborted) return;
      if (ended === 'timeout') continue;
      if (ended) {
        // idle: el turno terminó; identity o gone: la lectura completa dice qué pasó.
        if (alive.current) setLive(null);
        let after: StudioSession | null = null;
        try { after = await full(); } catch (e) { if (alive.current) setLoadError(e); }
        listChanged.current();
        // F-04: la persona pudo mandar otro mensaje mientras llegaba esta lectura (el estado `session` ya decía que
        // terminó): `follow` encontró este flujo abierto y lo marcó; ese turno sigue aquí.
        if (!signal.aborted && (again.current || (after && isActive(after.status)))) { again.current = false; attempt = 0; continue; }
        return;
      }
      const wait = nextBackoff(attempt++);
      if (wait === null) {
        // F-08: lo que leyeron las herramientas de «datos» se borra del servidor al terminar el turno; sin conexión la
        // pantalla no recibirá esa lectura, así que lo quita de lo que muestra.
        setSession((s) => ({ ...s, trail: s.trail.map((x) => (x.tool && data.current.has(x.tool) ? { ...x, output: undefined } : x)) }));
        if (alive.current) setConn('offline');
        return;
      }
      if (alive.current) setConn('reconnecting');
      await sleep(wait, signal);
    }
  }

  /** Abre el flujo si no hay uno abierto (al abrir una conversación activa, tras enviar, o con «Intentar de nuevo»). */
  const follow = useCallback(() => {
    // Revisión adversarial 037 (F-03): una lectura que llega tras desmontar (cambiar de conversación) no abre un flujo
    // huérfano que nadie cerraría.
    if (!alive.current) return;
    if (stream.current && !stream.current.signal.aborted) { again.current = true; return; }
    again.current = false;
    const ctrl = new AbortController();
    stream.current = ctrl;
    setConn('ok');
    void runStream(ctrl);
  }, [runStream]);

  useEffect(() => {
    alive.current = true;
    current.current = null;
    setSessionState(null);
    setLive(null);
    setPending(null);
    setLoadError(null);
    full().then((s) => { if (isActive(s.status)) follow(); }).catch((e) => { if (alive.current) setLoadError(e); });
    return () => { alive.current = false; stream.current?.abort(); stream.current = null; };
  }, [full, follow]);

  // El mensaje enviado deja de ser «pendiente» cuando llega su paso `user`.
  useEffect(() => {
    if (pending && session?.trail.some((s) => s.kind === 'user' && s.seq > pending.after)) setPending(null);
  }, [pending, session]);

  /** H2 CA2: manda un mensaje de seguimiento. Devuelve el error (para dejarlo en la vista) o null. */
  const send = useCallback(async (text: string): Promise<unknown> => {
    const before = current.current;
    if (!before) return null;
    setPending({ text, after: lastSeq(before.trail) });
    try {
      // La respuesta 202 no trae pasos ni compila: se conserva lo que ya se ve y la propuesta queda sin huella mientras corre.
      await snapshot(() => studioApi.send(id, text),
        (s, res) => ({ ...s, ...res, trail: s.trail, plan: s.plan, preview: s.preview, warnings: s.warnings, reviewHash: null, canApply: false }));
      setLive({ kind: 'waiting' });
      listChanged.current();
      follow();
      return null;
    } catch (e) {
      setPending(null);
      // Un 409 (turno en curso, otra conversación) puede deberse a un estado que la pantalla no tenía: se relee.
      if (e instanceof ApiError && e.status === 409) void full().then((s) => { if (isActive(s.status)) follow(); }).catch(() => undefined);
      return e;
    }
  }, [id, follow, full, snapshot]);

  /** H4 CA1: detener. La lectura completa que devuelve trae lo escrito hasta ese momento. */
  const stop = useCallback(async () => {
    const s = await snapshot(() => studioApi.cancel(id), (prev, x) => ({ ...x, trail: mergeTrail(prev.trail, x.trail) }));
    listChanged.current();
    // El flujo termina solo (`end: idle`) cuando el trabajador cierra el turno.
    return s;
  }, [id, snapshot]);

  const reconnect = useCallback(() => {
    void full().then((s) => { if (isActive(s.status)) follow(); else setConn('ok'); }).catch(() => setConn('offline'));
  }, [full, follow]);

  return { session, setSession, live, conn, loadError, pending, send, stop, full, reconnect };
}
