import { forwardRef, useImperativeHandle, useLayoutEffect, useRef, type FormEvent, type KeyboardEvent, type ReactNode } from 'react';
import { ArrowUp, Square } from 'lucide-react';

/** H2 CA1 (análisis §1): de una línea (28 px de área) hasta 384 px; después se desplaza por dentro. */
export const COMPOSER_MAX = 384;

export interface ComposerHandle { focus: () => void }

interface Props {
  value: string;
  onChange: (v: string) => void;
  onSubmit: () => void;
  onStop?: () => void;
  /** El agente trabaja: el botón de enviar se vuelve «Detener respuesta». */
  working?: boolean;
  stopping?: boolean;
  sending?: boolean;
  /** Motivo por el que no se puede enviar (`sendBlockedReason`). */
  blocked?: string | null;
  maxChars: number;
  placeholder: string;
  welcome?: boolean;
  footer?: ReactNode;
}

/**
 * Spec 037 (H2): el compositor. Enter envía y Shift+Enter inserta una línea (también respeta la composición de un IME);
 * crece con el texto. Mientras el agente trabaja, el mismo botón detiene la respuesta.
 */
export const Composer = forwardRef<ComposerHandle, Props>(function Composer(
  { value, onChange, onSubmit, onStop, working, stopping, sending, blocked, maxChars, placeholder, welcome, footer }, ref,
) {
  const area = useRef<HTMLTextAreaElement>(null);
  useImperativeHandle(ref, () => ({ focus: () => area.current?.focus() }), []);

  useLayoutEffect(() => {
    const el = area.current;
    if (!el) return;
    el.style.height = 'auto';
    const h = Math.min(el.scrollHeight, COMPOSER_MAX);
    el.style.height = `${h}px`;
    el.style.overflowY = el.scrollHeight > COMPOSER_MAX ? 'auto' : 'hidden';
  }, [value]);

  const canSend = !!value.trim() && !blocked && !working && !sending;
  function submit(e?: FormEvent) {
    e?.preventDefault();
    if (canSend) onSubmit();
  }
  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      submit();
    }
  }
  const near = value.length > maxChars * 0.8;
  return (
    <form className={`chat-composer${welcome ? ' is-welcome' : ''}`} onSubmit={submit} aria-label="Mensaje para ERP AI Studio">
      {blocked && <p className="chat-composer-blocked" role="note">{blocked}</p>}
      <div className="chat-composer-box">
        <textarea
          ref={area}
          className="chat-composer-input"
          rows={1}
          value={value}
          maxLength={maxChars}
          placeholder={placeholder}
          aria-label="Escribe tu mensaje"
          aria-describedby={near ? 'chat-composer-count' : undefined}
          disabled={!!blocked}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={onKeyDown}
        />
        <div className="chat-composer-actions">
          {near && <span id="chat-composer-count" className="chat-composer-count">{value.length}/{maxChars}</span>}
          {working ? (
            <button type="button" className="chat-send is-stop" aria-label="Detener respuesta" title="Detener respuesta" disabled={stopping} onClick={onStop}>
              <Square size={12} fill="currentColor" aria-hidden />
            </button>
          ) : (
            <button type="submit" className="chat-send" aria-label="Enviar mensaje" title="Enviar mensaje (Enter)" disabled={!canSend} aria-busy={sending || undefined}>
              <ArrowUp size={16} aria-hidden />
            </button>
          )}
        </div>
      </div>
      {footer}
    </form>
  );
});
