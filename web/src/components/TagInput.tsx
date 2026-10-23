import { useId, useState } from 'react';
import { X } from 'lucide-react';

export interface TagInputProps {
  value: string[];
  onChange: (v: string[]) => void;
  placeholder?: string;
  suggestions?: string[];
  id?: string;
  'aria-label'?: string;
  invalid?: boolean;
}

export function TagInput({ value, onChange, placeholder = 'Escribe y presiona Enter', suggestions, id, invalid, ...aria }: TagInputProps) {
  const [draft, setDraft] = useState('');
  const listId = useId();

  function add(raw: string) {
    const v = raw.trim();
    if (!v) return;
    if (!value.some((x) => x.toLowerCase() === v.toLowerCase())) onChange([...value, v]);
    setDraft('');
  }

  return (
    <div className="tags" aria-invalid={invalid || undefined}>
      {value.map((t) => (
        <span key={t} className="tag">
          {t}
          <button type="button" aria-label={`Quitar ${t}`} onClick={() => onChange(value.filter((x) => x !== t))}>
            <X size={12} aria-hidden />
          </button>
        </span>
      ))}
      <input
        id={id}
        value={draft}
        placeholder={value.length ? '' : placeholder}
        aria-label={aria['aria-label']}
        list={suggestions?.length ? listId : undefined}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); add(draft); }
          else if (e.key === 'Backspace' && !draft && value.length) onChange(value.slice(0, -1));
        }}
        onBlur={() => add(draft)}
      />
      {suggestions?.length ? (
        <datalist id={listId}>
          {suggestions.filter((s) => !value.includes(s)).map((s) => <option key={s} value={s} />)}
        </datalist>
      ) : null}
    </div>
  );
}
