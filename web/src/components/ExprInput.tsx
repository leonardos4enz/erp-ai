import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { AlertCircle, Check, ListChecks, Loader2, TextCursorInput } from 'lucide-react';
import { ApiError } from '../api/client';
import { modelApi } from '../api/endpoints';
import type { EntityDef, ExprFunction, FieldDef } from '../api/types';
import { buildCondition, parseCondition, type CondGroup } from '../lib/conditionBuilder';
import { apply, complete, type Completion } from '../lib/exprComplete';
import { ConditionBuilder } from './ConditionBuilder';
import { Button, Field } from './ui';

let functionsCache: Promise<ExprFunction[]> | null = null;
/** La lista sale del analizador del servidor (una sola fuente); se pide una vez por sesión. */
function loadFunctions(): Promise<ExprFunction[]> {
  functionsCache ??= modelApi.functions().catch(() => { functionsCache = null; return []; });
  return functionsCache;
}

export interface ExprScope {
  /** Catálogo en cuyo ámbito se evalúa (registro, partida, registro creado…). */
  entity: string;
  /** Campos del ámbito; para el catálogo que se edita, los del borrador (se envían al validar). */
  fields: FieldDef[];
  /** true si `fields` es el borrador y hay que mandarlo al servidor (campos nuevos aún sin aplicar). */
  draft?: boolean;
  states?: string[];
}

export interface ExprInputProps {
  label: ReactNode;
  value: string;
  onChange: (v: string) => void;
  scope: ExprScope | null;
  entities: EntityDef[];
  hint?: ReactNode;
  /** Error que viene de fuera (p. ej. requerido); se muestra antes que el del servidor. */
  error?: string;
  multiline?: boolean;
  required?: boolean;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  /** Sin validación en vivo (p. ej. cuando el ámbito todavía no está elegido). */
  noCheck?: boolean;
  /** Spec 026 (H3 CA5): es una condición; se ofrece el constructor por renglones además del texto. */
  condition?: boolean;
}

/**
 * Spec 026 (H3): texto con autocompletado (patrón combobox de ARIA) y validación en vivo con el mismo analizador que el
 * compilador. Las sugerencias son ayuda: lo que se escriba a mano vale igual y lo juzga el servidor.
 */
export function ExprInput({ label, value, onChange, scope, entities, hint, error, multiline, required, placeholder, disabled, className, noCheck, condition }: ExprInputProps) {
  const id = useId();
  const listId = `${id}-list`;
  const msgId = `${id}-msg`;
  const ref = useRef<HTMLInputElement & HTMLTextAreaElement>(null);
  const [functions, setFunctions] = useState<ExprFunction[]>([]);
  const [completion, setCompletion] = useState<Completion | null>(null);
  const [active, setActive] = useState(0);
  const [check, setCheck] = useState<{ state: 'idle' | 'checking' | 'ok' | 'error'; errors: string[] }>({ state: 'idle', errors: [] });
  const caretAfter = useRef<number | null>(null);

  useEffect(() => { loadFunctions().then(setFunctions); }, []);

  const completionScope = useMemo(() => scope && ({ fields: scope.fields, states: scope.states, entities, functions }), [scope, entities, functions]);
  const labelText = typeof label === 'string' ? label : 'la condición';

  // Constructor: los renglones viven aquí (los incompletos no tienen texto todavía); el texto es lo que se guarda.
  const parsed = useMemo(() => (condition && completionScope ? parseCondition(value, completionScope) : null), [condition, completionScope, value]);
  const [mode, setMode] = useState<'builder' | 'text'>(() => (condition && scope && (!value.trim() || parsed) ? 'builder' : 'text'));
  const [group, setGroup] = useState<CondGroup>(() => parsed ?? { join: 'AND', items: [] });
  const lastBuilt = useRef(value);
  useEffect(() => {
    // Un cambio que no vino del constructor (p. ej. el JSON avanzado) se vuelve a leer, o pasa a texto si no cabe.
    if (mode !== 'builder' || value === lastBuilt.current) return;
    lastBuilt.current = value;
    if (parsed) setGroup(parsed); else setMode('text');
  }, [value, parsed, mode]);

  // Validación en vivo: espera 0.4 s y cancela la anterior.
  const scopeKey = scope ? `${scope.entity}|${scope.draft ? JSON.stringify(scope.fields.map((f) => [f.name, f.type, f.referenceEntity, f.childEntity, f.options, f.resultType])) : ''}` : '';
  useEffect(() => {
    if (noCheck || !scope || !value.trim()) { setCheck({ state: 'idle', errors: [] }); return; }
    const ctl = new AbortController();
    const t = window.setTimeout(() => {
      setCheck((c) => ({ ...c, state: 'checking' }));
      modelApi.checkExpression(scope.entity, value, scope.draft ? scope.fields : undefined, ctl.signal)
        .then((r) => setCheck({ state: r.errors.length ? 'error' : 'ok', errors: r.errors }))
        .catch((e) => { if (!ctl.signal.aborted) setCheck({ state: 'error', errors: [e instanceof ApiError ? e.title : 'No se pudo validar la expresión.'] }); });
    }, 400);
    return () => { ctl.abort(); window.clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, scopeKey, noCheck]);

  useEffect(() => {
    if (caretAfter.current !== null && ref.current) {
      ref.current.setSelectionRange(caretAfter.current, caretAfter.current);
      caretAfter.current = null;
    }
  }, [value]);

  function refresh(text: string, caret: number, force = false) {
    if (!completionScope) { setCompletion(null); return; }
    const c = complete(text, caret, completionScope, force);
    setCompletion(c);
    setActive(0);
  }

  function choose(i: number) {
    if (!completion) return;
    const r = apply(value, completion, completion.items[i]);
    caretAfter.current = r.caret;
    onChange(r.text);
    setCompletion(null);
    // Después de una referencia o de abrir una función, seguir sugiriendo.
    const s = completion.items[i];
    // Regresión (revisión 026, F-06): se lee el texto actual; con el de la elección, si la persona ya siguió escribiendo,
    // la lista mostraba sugerencias viejas.
    if (s.kind === 'function' || (s.detail ?? '').endsWith('›')) {
      requestAnimationFrame(() => { const el = ref.current; if (el) refresh(el.value, el.selectionStart ?? el.value.length, s.kind === 'function'); });
    }
  }

  function onKeyDown(e: KeyboardEvent) {
    if (e.key === ' ' && e.ctrlKey) { e.preventDefault(); refresh(value, ref.current?.selectionStart ?? value.length, true); return; }
    if (!completion) return;
    const n = completion.items.length;
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => (a + 1) % n); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => (a + n - 1) % n); }
    else if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); choose(active); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setCompletion(null); }
  }

  const serverErrors = check.state === 'error' ? check.errors : [];
  const shownError = error ?? serverErrors[0];
  const open = !!completion && !disabled;
  const common = {
    id,
    ref,
    value,
    disabled,
    placeholder,
    required,
    spellCheck: false,
    autoComplete: 'off',
    className: ['mono', multiline ? 'textarea expr-textarea' : 'input'].join(' '),
    role: 'combobox',
    'aria-autocomplete': 'list' as const,
    'aria-expanded': open,
    'aria-controls': listId,
    'aria-activedescendant': open ? `${listId}-${active}` : undefined,
    'aria-invalid': shownError ? true : undefined,
    'aria-describedby': msgId,
    onChange: (e: { target: { value: string; selectionStart: number | null } }) => {
      onChange(e.target.value);
      refresh(e.target.value, e.target.selectionStart ?? e.target.value.length);
    },
    onKeyDown,
    onBlur: () => setCompletion(null),
    onClick: () => { if (completion) refresh(value, ref.current?.selectionStart ?? value.length); },
  };

  const message = (
    <div id={msgId} className="expr-msg" aria-live="polite">
      {shownError ? (
        <span className="field-error"><AlertCircle size={13} aria-hidden /> {shownError}</span>
      ) : check.state === 'checking' ? (
        <span className="field-hint"><Loader2 size={12} className="spin" aria-hidden /> Validando…</span>
      ) : check.state === 'ok' ? (
        <span className="field-hint expr-ok"><Check size={12} aria-hidden /> Expresión válida</span>
      ) : hint ? (
        <span className="field-hint">{hint}</span>
      ) : null}
      {mode === 'text' && condition && value.trim() && !parsed && completionScope && (
        <span className="field-hint expr-complex">Tiene fórmulas, paréntesis o mezcla Y con O: se edita como texto.</span>
      )}
      <span className="sr-only">{open ? `${completion!.items.length} ${completion!.items.length === 1 ? 'sugerencia' : 'sugerencias'}; flechas para elegir, Enter para usarla.` : ''}</span>
    </div>
  );
  const modeBar = condition && completionScope && (
    <div className="segmented expr-mode" role="group" aria-label={`Modo de ${labelText}`}>
      <Button size="sm" variant={mode === 'builder' ? 'primary' : 'ghost'} icon={ListChecks} aria-pressed={mode === 'builder'} disabled={disabled || (!parsed && mode !== 'builder')}
        title={!parsed && mode !== 'builder' ? 'Esta condición no cabe en el constructor' : undefined}
        onClick={() => { if (parsed) { lastBuilt.current = value; setGroup(parsed); setMode('builder'); } }}>Constructor</Button>
      <Button size="sm" variant={mode === 'text' ? 'primary' : 'ghost'} icon={TextCursorInput} aria-pressed={mode === 'text'} disabled={disabled}
        onClick={() => setMode('text')}>Texto</Button>
    </div>
  );

  if (mode === 'builder' && completionScope) {
    return (
      <fieldset className={['expr-field', 'expr-builder', className].filter(Boolean).join(' ')} aria-describedby={msgId}>
        <legend className="field-label">{label}{required && <span className="field-required" aria-hidden>*</span>}</legend>
        {modeBar}
        <ConditionBuilder group={group} scope={completionScope} label={labelText}
          onChange={(g) => { setGroup(g); const text = buildCondition(g); lastBuilt.current = text; onChange(text); }} />
        {message}
      </fieldset>
    );
  }

  // Una condición es un grupo con su nombre en los dos modos (el selector de modo queda dentro).
  const field = (
    <Field label={label} required={required} htmlFor={id} className={['expr-field', className].filter(Boolean).join(' ')}>
      {modeBar}
      <div className="expr-wrap">
        {multiline ? <textarea rows={2} {...common} /> : <input {...common} />}
        <ul id={listId} role="listbox" aria-label="Sugerencias" className="expr-list" hidden={!open}>
          {completion?.items.map((s, i) => (
            <li
              key={`${s.kind}-${s.insert}`}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === active}
              className="expr-option"
              onMouseDown={(e) => { e.preventDefault(); choose(i); }}
              onMouseEnter={() => setActive(i)}
            >
              <span className="expr-option-label">{s.label}</span>
              {s.detail && <span className="expr-option-detail">{s.detail}</span>}
            </li>
          ))}
        </ul>
      </div>
      {message}
    </Field>
  );
  return condition && completionScope
    ? <div role="group" aria-label={labelText} className={['expr-group', className].filter(Boolean).join(' ')}>{field}</div>
    : field;
}
