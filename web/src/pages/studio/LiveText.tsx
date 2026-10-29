import { useEffect, useRef, useState } from 'react';
import { revealStep } from '../../lib/studio';
import { Markdown } from './Markdown';

export const reducedMotion = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

/**
 * Spec 037 (H3 CA1, CLARIFY 5): el texto que el agente está escribiendo. El servidor manda el acumulado cada ≥ 250 ms; aquí se
 * revela a ~240 car/s por palabras completas y cada palabra se funde al aparecer. Con movimiento reducido, de golpe.
 */
export function LiveText({ text }: { text: string }) {
  const [shown, setShown] = useState(() => (reducedMotion() ? text.length : 0));
  const target = useRef(text);
  target.current = text;

  useEffect(() => {
    if (reducedMotion()) { setShown(text.length); return; }
    let raf = 0;
    let last = performance.now();
    const tick = (now: number) => {
      const dt = now - last;
      last = now;
      setShown((s) => (s >= target.current.length ? s : revealStep(s, target.current, dt)));
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [text]);

  return <Markdown text={text.slice(0, Math.min(shown, text.length))} words />;
}
