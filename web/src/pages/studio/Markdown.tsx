import { memo, useMemo, useState, type ReactNode } from 'react';
import { Check, Copy } from 'lucide-react';
import { parseMarkdown, type Block, type Inline } from '../../lib/markdown';
import { splitWords } from '../../lib/studio';
import { useToast } from '../../components/Toast';

/**
 * Spec 037 (H3 CA3): el texto del modelo con el subconjunto seguro de `lib/markdown`. Solo crea elementos de React con
 * texto escapado: sin HTML, sin enlaces, sin imágenes (constitución II.8). Con `words`, cada palabra es un `span` que se
 * funde al montarse (texto en vivo); las que ya estaban conservan su clave y no vuelven a animarse.
 */
export const Markdown = memo(function Markdown({ text, words = false }: { text: string; words?: boolean }) {
  const blocks = useMemo(() => parseMarkdown(text), [text]);
  return <div className="md">{blocks.map((b, i) => <BlockView key={i} b={b} words={words} />)}</div>;
});

function BlockView({ b, words }: { b: Block; words: boolean }) {
  const r = (c: Inline[]) => <InlineView c={c} words={words} />;
  switch (b.t) {
    case 'p': return <p>{r(b.c)}</p>;
    case 'h': return <p className={`md-h md-h${b.level}`}><strong>{r(b.c)}</strong></p>;
    case 'ul': return <ul>{b.items.map((it, i) => <li key={i}>{r(it)}</li>)}</ul>;
    case 'ol': return <ol start={b.start}>{b.items.map((it, i) => <li key={i}>{r(it)}</li>)}</ol>;
    case 'quote': return <blockquote>{r(b.c)}</blockquote>;
    case 'hr': return <hr />;
    case 'code': return <CodeBlock lang={b.lang} code={b.v} />;
    case 'table':
      return (
        <div className="md-table" tabIndex={0} role="region" aria-label="Tabla">
          <table>
            <thead><tr>{b.head.map((h, i) => <th key={i} scope="col">{r(h)}</th>)}</tr></thead>
            <tbody>{b.rows.map((row, i) => <tr key={i}>{row.map((c, k) => <td key={k}>{r(c)}</td>)}</tr>)}</tbody>
          </table>
        </div>
      );
  }
}

function InlineView({ c, words }: { c: Inline[]; words: boolean }) {
  return (
    <>
      {c.map((x, i): ReactNode => {
        switch (x.t) {
          case 'text': return words ? <Words key={i} text={x.v} /> : x.v;
          case 'br': return <br key={i} />;
          case 'code': return <code key={i}>{x.v}</code>;
          case 'strong': return <strong key={i}><InlineView c={x.c} words={words} /></strong>;
          case 'em': return <em key={i}><InlineView c={x.c} words={words} /></em>;
        }
      })}
    </>
  );
}

function Words({ text }: { text: string }) {
  return <>{splitWords(text).map((w, i) => <span key={i} className="chat-w">{w}</span>)}</>;
}

function CodeBlock({ lang, code }: { lang: string; code: string }) {
  return (
    <div className="md-code">
      <div className="md-code-head">
        <span className="md-code-lang">{lang || 'texto'}</span>
        <CopyButton text={code} label="Copiar el código" />
      </div>
      <pre tabIndex={0} aria-label={lang ? `Código ${lang}` : 'Código'}><code>{code}</code></pre>
    </div>
  );
}

/** H4 CA4: copia el texto tal cual y lo confirma (ícono y aviso). */
export function CopyButton({ text, label = 'Copiar', className }: { text: string; label?: string; className?: string }) {
  const toast = useToast();
  const [done, setDone] = useState(false);
  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
      setDone(true);
      toast.success('Copiado');
      window.setTimeout(() => setDone(false), 2000);
    } catch {
      toast.fromError(null, 'No se pudo copiar: tu navegador no dio acceso al portapapeles.');
    }
  }
  return (
    <button type="button" className={`chat-icon-btn${className ? ` ${className}` : ''}`} aria-label={label} title={label} onClick={() => void copy()}>
      {done ? <Check size={15} aria-hidden /> : <Copy size={15} aria-hidden />}
    </button>
  );
}

