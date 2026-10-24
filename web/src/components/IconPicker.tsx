import { useState } from 'react';
import { Search } from 'lucide-react';
import { Dialog } from './Dialog';
import { CURATED_ICONS, DynamicIcon } from './DynamicIcon';
import { Button, Field, Input } from './ui';

/** Selector de iconos Lucide. Con `allowNone`, el icono es opcional (p. ej. el botón de una transición). */
export function IconPicker({ value, onChange, label = 'Icono', allowNone }: {
  value: string | undefined; onChange: (v: string | undefined) => void; label?: string; allowNone?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const list = CURATED_ICONS.filter((i) => i.includes(q.trim().toLowerCase()));
  return (
    <>
      <Field label={label}>
        <Button onClick={() => setOpen(true)} aria-label={`${label}: ${value ?? 'sin icono'}. Cambiar`} className="icon-btn-preview">
          {value ? <DynamicIcon name={value} size={16} /> : null} <span className="muted">{value ?? 'Sin icono'}</span>
        </Button>
      </Field>
      <Dialog open={open} onClose={() => setOpen(false)} title="Elegir icono" size="lg">
        <div className="stack">
          <Input icon={Search} placeholder="Buscar icono…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Buscar icono" data-autofocus />
          {allowNone && (
            <div><Button size="sm" aria-pressed={!value} onClick={() => { onChange(undefined); setOpen(false); }}>Sin icono</Button></div>
          )}
          <div className="icon-grid" role="group" aria-label="Iconos">
            {list.map((name) => (
              <button
                key={name}
                type="button"
                className="icon-cell"
                title={name}
                aria-label={name}
                aria-pressed={value === name}
                onClick={() => { onChange(name); setOpen(false); }}
              >
                <DynamicIcon name={name} size={18} />
              </button>
            ))}
          </div>
          {list.length === 0 && <p className="muted">Sin coincidencias.</p>}
        </div>
      </Dialog>
    </>
  );
}
