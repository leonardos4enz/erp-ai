import { useState, type ReactNode } from 'react';
import { Braces, MousePointerSquareDashed } from 'lucide-react';
import { Alert, Button, Textarea } from '../../components/ui';

export type JsonResult<T> = { ok: true; value: T | null } | { ok: false; error: string };

/**
 * Spec 019 (H1 CA7): el mismo texto del borrador se edita con el diseñador o como JSON. El diseñador recibe el valor
 * ya interpretado y devuelve el nuevo, que se vuelve a escribir en el texto: no hay un segundo borrador que se desfase.
 */
export function DesignOrJson<T>({ title, jsonLabel, text, parsed, onText, placeholder, hint, error, designer }: {
  title: string;
  jsonLabel: string;
  text: string;
  parsed: JsonResult<T>;
  onText: (text: string) => void;
  placeholder: string;
  hint: string;
  error?: string;
  designer: (value: T | null) => ReactNode;
}) {
  const [mode, setMode] = useState<'design' | 'json'>('design');
  return (
    <div className="stack">
      <div className="row design-mode-bar">
        <h2 className="section-title grow">{title}</h2>
        <div className="segmented" role="group" aria-label={`Modo de edición: ${title.toLowerCase()}`}>
          <Button size="sm" variant={mode === 'design' ? 'primary' : 'ghost'} icon={MousePointerSquareDashed} aria-pressed={mode === 'design'} onClick={() => setMode('design')}>Diseñador</Button>
          <Button size="sm" variant={mode === 'json' ? 'primary' : 'ghost'} icon={Braces} aria-pressed={mode === 'json'} onClick={() => setMode('json')}>JSON</Button>
        </div>
      </div>
      {mode === 'json' ? (
        <Textarea
          className="mono json-editor"
          label={jsonLabel}
          rows={18}
          spellCheck={false}
          placeholder={placeholder}
          hint={hint}
          error={error ?? (parsed.ok ? undefined : parsed.error)}
          value={text}
          onChange={(e) => onText(e.target.value)}
        />
      ) : parsed.ok ? designer(parsed.value) : (
        <Alert tone="danger" title="El JSON no es válido" action={<Button size="sm" onClick={() => setMode('json')}>Corregir el JSON</Button>}>
          El diseñador necesita un JSON válido. {parsed.error}
        </Alert>
      )}
    </div>
  );
}
