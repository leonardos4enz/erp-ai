import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { ArrowRight, ArrowRightLeft, CircleAlert, ExternalLink, FilePen, FilePlus, MessageSquareQuote, MessageSquareReply, Send, ShieldCheck, X } from 'lucide-react';
import { ApiError } from '../../api/client';
import { companionApi } from '../../api/endpoints';
import type { CompanionAction } from '../../api/types';
import { actionView, showValue, warningsOf } from '../../lib/companion';
import { problemText } from '../../pages/studio/ChatView';
import { Badge, Button } from '../ui';

const KIND_ICON = { create: FilePlus, update: FilePen, transition: ArrowRightLeft } as const;

/**
 * Spec 047 (H5): la tarjeta de una acción que propuso el Acompañante. Lo que cambia lo arma el servidor (`preview`: campo,
 * antes → después; transición, desde → hacia) y se pinta como texto; lo que dijo el modelo va aparte, marcado como suyo
 * (046 F-03, R-046-2). Confirmar manda la huella de lo que se ve; un 428 muestra los avisos y pide confirmar otra vez.
 */
export function ActionCard({ action, entityLabel, onChanged, asking = true, position = null }: {
  action: CompanionAction | undefined; entityLabel: (entity: string, fallback: string) => string;
  /** Spec 048 (H6 CA1): es la propuesta que se pregunta ahora (las demás pendientes esperan su turno). */
  asking?: boolean;
  /** «Propuesta 1 de 3» cuando hay varias pendientes. */
  position?: string | null;
  /** La acción cambió (o puede haber cambiado): el panel relee la conversación. */
  onChanged: (a?: CompanionAction) => void;
}) {
  const { pathname } = useLocation();
  const [busy, setBusy] = useState<'confirm' | 'discard' | null>(null);
  const [warnings, setWarnings] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  /** Spec 048: «No, y dile qué cambiar» abre un campo para el motivo. */
  const [why, setWhy] = useState<string | null>(null);
  const whyRef = useRef<HTMLTextAreaElement>(null);
  const card = useRef<HTMLElement>(null);
  useEffect(() => { if (why === '') whyRef.current?.focus(); }, [why === '']);
  /** Tras decidir, los botones desaparecen: el foco pasa a la tarjeta (no a body, guardia de la 030). */
  const refocus = useRef(false);
  const transition = useRef<string | null>(null);
  if (action?.preview?.transicion) transition.current = action.preview.transicion;
  useEffect(() => {
    if (!refocus.current || !action || action.status === 'proposed') return;
    refocus.current = false;
    card.current?.focus({ preventScroll: true });
  }, [action?.status]);
  // Revisión adversarial 047 (F-05): decidida en otra pestaña o por el barrido, los avisos ya no piden nada.
  useEffect(() => { if (action && action.status !== 'proposed') setWarnings(null); }, [action?.status]);

  if (!action) {
    return (
      <article className="cmp-action" aria-label="Propuesta">
        <div className="cmp-action-head"><FilePen size={16} aria-hidden /><p className="cmp-action-title">Preparando la propuesta…</p></div>
      </article>
    );
  }
  const a = action;
  const v = actionView(a, entityLabel(a.entity, a.entityLabel), transition.current);
  const Icon = KIND_ICON[a.kind] ?? FilePen;
  const p = v.preview;

  async function confirm() {
    setBusy('confirm');
    setError(null);
    try {
      const r = await companionApi.confirm(a.id, a.hash, warnings ?? undefined);
      setWarnings(null);
      refocus.current = true;
      onChanged(r.action);
    } catch (e) {
      const w = warningsOf(e);
      // H5 CA2: los avisos no aplican nada; se muestran y la persona decide si confirma con ellos.
      if (w) setWarnings(w);
      else {
        setWarnings(null);
        // Revisión adversarial 047 (F-01): `api()` convierte una falla de red en ApiError con estado 0, y un proxy caído da 5xx.
        // Sin respuesta no sabemos si se aplicó: la relectura muestra el estado real y nunca se repite sola.
        setError(e instanceof ApiError && e.status > 0 && e.status < 500
          ? problemText(e, 'No se pudo aplicar la propuesta.')
          : 'No llegó la respuesta del servidor: no sabemos si se aplicó. Revisa el estado de la propuesta antes de volver a intentarlo.');
        // F-03: si la relectura quita los botones, el foco pasa a la tarjeta (no fuera del panel).
        refocus.current = true;
      }
      onChanged();
    } finally { setBusy(null); }
  }

  async function discard(reason?: string) {
    setBusy('discard');
    setError(null);
    try {
      const d = await companionApi.discard(a.id, reason?.trim() || undefined);
      setWarnings(null);
      setWhy(null);
      refocus.current = true;
      onChanged(d);
    } catch (e) {
      setError(problemText(e, 'No se pudo descartar la propuesta.'));
      refocus.current = true;
      onChanged();
    } finally { setBusy(null); }
  }

  return (
    <article ref={card} tabIndex={-1} className={`cmp-action is-${a.status}`} aria-label={`Propuesta: ${v.title}`}>
      <div className="cmp-action-head">
        <Icon size={16} aria-hidden />
        <p className="cmp-action-title">{v.title}</p>
        <Badge tone={v.status.tone}>{v.status.label}</Badge>
      </div>
      {position && a.status === 'proposed' && <p className="cmp-action-pos">{position}</p>}
      {(p || v.message || error || warnings || (a.status === 'discarded' && a.reason)) && (
        <div className="cmp-action-body">
          {p?.tipo === 'transition' && (
            <dl className="cmp-changes" aria-label="Cambio de estado">
              <div className="cmp-change">
                <dt>Estado</dt>
                <dd><span className="cmp-before">{showValue(p.desde)}</span><ArrowRight size={14} aria-label="pasa a" /><span className="cmp-after">{showValue(p.hacia)}</span></dd>
              </div>
              {p.comentario && (
                // Revisión adversarial 047 (F-08): lo escribió el modelo; es lo que se guardará, marcado como tal.
                <div className="cmp-change"><dt>Comentario que se guardará (lo escribió el Acompañante)</dt><dd><span className="cmp-quoted">{p.comentario}</span></dd></div>
              )}
            </dl>
          )}
          {p?.cambios && p.cambios.length > 0 && (
            <dl className="cmp-changes" aria-label="Cambios">
              {p.cambios.map((c, i) => (
                <div key={i} className="cmp-change">
                  <dt>{c.campo}</dt>
                  <dd>
                    {a.kind !== 'create' && <><span className="cmp-before">{showValue(c.antes)}</span><ArrowRight size={14} aria-label="cambia a" /></>}
                    <span className="cmp-after">{showValue(c.despues)}</span>
                  </dd>
                </div>
              ))}
            </dl>
          )}
          {p?.segunElAcompanante && (
            <blockquote className="cmp-says">
              <p className="cmp-says-label"><MessageSquareQuote size={13} aria-hidden /> Según el Acompañante</p>
              <p>{p.segunElAcompanante}</p>
            </blockquote>
          )}
          {a.status === 'discarded' && a.reason && (
            <p className="cmp-action-reason"><MessageSquareReply size={14} aria-hidden /> <span>Le dijiste: «{a.reason}»</span></p>
          )}
          {v.message && <p className="cmp-action-msg"><CircleAlert size={14} aria-hidden /> <span>{v.message}</span></p>}
          {warnings && (
            <div className="alert alert-warning" role="alert">
              <CircleAlert size={16} aria-hidden />
              <div className="grow">
                <div className="cmp-warn-title">Revisa estos avisos antes de confirmar</div>
                <ul className="warning-list">{warnings.map((w) => <li key={w}>{w}</li>)}</ul>
              </div>
            </div>
          )}
          {error && (
            <div className="alert alert-danger" role="alert">
              <CircleAlert size={16} aria-hidden />
              <div className="grow">{error}</div>
            </div>
          )}
        </div>
      )}
      {v.canDecide && !asking && (
        <div className="cmp-action-foot">
          <p className="cmp-action-note">En espera: te la pregunto cuando decidas la anterior.</p>
        </div>
      )}
      {v.canDecide && asking && why !== null && (
        // Spec 048 (H1 CA2): el motivo le llega al Acompañante como tus palabras y el turno sigue.
        <form className="cmp-action-why" onSubmit={(e) => { e.preventDefault(); void discard(why); }}>
          <label htmlFor={`cmp-why-${a.id}`}>¿Qué debe cambiar?</label>
          <textarea id={`cmp-why-${a.id}`} ref={whyRef} rows={2} value={why} maxLength={2000}
            placeholder="Por ejemplo: llámalo Michi, no Garfield"
            onChange={(e) => setWhy(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setWhy(null); card.current?.focus({ preventScroll: true }); }
              if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); if (why.trim()) void discard(why); }
            }} />
          <div className="cmp-action-why-foot">
            <Button size="sm" type="button" onClick={() => { setWhy(null); card.current?.focus({ preventScroll: true }); }} disabled={busy !== null}>Cancelar</Button>
            <Button size="sm" variant="primary" type="submit" icon={Send} disabled={!why.trim() || busy === 'confirm'} loading={busy === 'discard'}>Enviar</Button>
          </div>
        </form>
      )}
      {((v.canDecide && asking && why === null) || (!v.canDecide && v.recordLink)) && (
        <div className="cmp-action-foot">
          {v.canDecide ? (
            <>
              {/* Spec 048 (H6 CA1): como los permisos de Claude Code: una pregunta y tres respuestas. */}
              <p className="cmp-action-ask">¿Lo hago? <span className="cmp-action-note">Nada cambia si no dices que sí. Se aplica con tus permisos.</span></p>
              <Button size="sm" icon={MessageSquareReply} onClick={() => setWhy('')} disabled={busy !== null}>No, y dile qué cambiar</Button>
              <Button size="sm" icon={X} onClick={() => void discard()} disabled={busy === 'confirm'} loading={busy === 'discard'}>No</Button>
              <Button size="sm" variant="primary" icon={ShieldCheck} onClick={() => void confirm()} disabled={busy === 'discard'} loading={busy === 'confirm'}>
                {warnings ? 'Sí, con los avisos' : 'Sí, adelante'}
              </Button>
            </>
          ) : (
            // Revisión exploratoria 047 (F-02): con ese registro ya abierto, el enlace no hacía nada y la pantalla seguía con
            // los valores de antes: recarga la página (el panel y la conversación abierta se recuerdan).
            <Link to={v.recordLink!} reloadDocument={pathname === v.recordLink} className="btn btn-secondary btn-sm"><ExternalLink size={14} aria-hidden /> {a.status === 'unknown' ? 'Revisar el registro' : 'Ver el registro'}</Link>
          )}
        </div>
      )}
    </article>
  );
}
