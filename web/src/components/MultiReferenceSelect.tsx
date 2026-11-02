import { useRef, useState } from 'react';
import { X } from 'lucide-react';
import type { LookupItem } from '../api/types';
import { ReferenceSelect } from './ReferenceSelect';

export interface MultiReferenceSelectProps {
  entity: string;
  /** Ids elegidos, en el orden en que se muestran. */
  value: number[];
  /** Etiquetas en la misma posición que `value` (`Campo__label` del servidor). */
  labels: (string | null)[];
  onChange: (ids: number[], labels: (string | null)[]) => void;
  /** Etiqueta del campo: nombra la lista de elegidos y los anuncios. */
  fieldLabel: string;
  id?: string;
  invalid?: boolean;
  disabled?: boolean;
  describedBy?: string;
  ariaLabel?: string;
  /** Fuente alternativa de opciones (personas, spec 050 `multiuser`). */
  lookup?: (search: string, signal: AbortSignal) => Promise<LookupItem[]>;
}

/**
 * Spec 050: selector de varios registros (o personas). Cada valor elegido es una etiqueta con su botón «Quitar …»; el buscador
 * (el mismo combobox de una referencia) agrega más y no ofrece lo ya elegido. Agregar y quitar se anuncian en vivo.
 */
export function MultiReferenceSelect({ entity, value, labels, onChange, fieldLabel, id, invalid, disabled, describedBy, ariaLabel, lookup }: MultiReferenceSelectProps) {
  const [announce, setAnnounce] = useState('');
  const wrapRef = useRef<HTMLDivElement>(null);
  const text = (i: number) => labels[i] ?? `#${value[i]}`;

  function add(nid: number | null, label: string | null) {
    if (nid == null || value.includes(nid)) return;
    onChange([...value, nid], [...labels, label]);
    setAnnounce(`${label ?? `#${nid}`} agregado a ${fieldLabel}.`);
  }

  function remove(i: number) {
    const gone = text(i);
    onChange(value.filter((_, k) => k !== i), labels.filter((_, k) => k !== i));
    setAnnounce(`${gone} quitado de ${fieldLabel}.`);
    // El botón desaparece: el foco vuelve al buscador (nunca a body).
    wrapRef.current?.querySelector<HTMLInputElement>('input[role="combobox"]')?.focus();
  }

  if (disabled) {
    return (
      <output id={id} className="readonly-value" aria-label={ariaLabel}>
        {value.length ? value.map((_, i) => text(i)).join(', ') : <span className="subtle">—</span>}
      </output>
    );
  }

  return (
    <div className="multi-ref" ref={wrapRef}>
      {value.length > 0 && (
        <ul className="multi-ref-tags" aria-label={`${fieldLabel}: ${value.length} ${value.length === 1 ? 'elegido' : 'elegidos'}`}>
          {value.map((v, i) => (
            <li key={v} className="tag">
              {text(i)}
              <button type="button" aria-label={`Quitar ${text(i)}`} onClick={() => remove(i)}>
                <X size={12} aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      )}
      <ReferenceSelect
        id={id}
        entity={entity}
        lookup={lookup}
        exclude={value}
        value={null}
        label={null}
        invalid={invalid}
        describedBy={describedBy}
        ariaLabel={ariaLabel}
        placeholder="Agregar…"
        onChange={add}
      />
      <span className="sr-only" aria-live="polite">{announce}</span>
    </div>
  );
}
