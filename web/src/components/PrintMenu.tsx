import { useEffect, useRef, useState } from 'react';
import { ChevronDown, FileDown } from 'lucide-react';
import { ApiError } from '../api/client';
import { dataApi } from '../api/endpoints';
import type { PrintChoice } from '../api/types';
import { useToast } from './Toast';
import { Button } from './ui';

/**
 * Spec 027 (H5 CA1): «Descargar PDF» del registro. Solo aparece si alguna plantilla aplica al registro y al usuario (lo
 * decide el servidor); con varias, un menú para elegir (la predeterminada primero). El PDF se pide con la sesión y se
 * guarda con el nombre que fija el servidor.
 */
export function PrintMenu({ entity, id, version }: { entity: string; id: number; version?: string }) {
  const toast = useToast();
  const [choices, setChoices] = useState<PrintChoice[]>([]);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);

  // La condición de una plantilla puede depender del estado: se vuelve a preguntar cuando el registro cambia.
  useEffect(() => {
    const ctl = new AbortController();
    dataApi.printChoices(entity, id, ctl.signal).then(setChoices).catch(() => setChoices([]));
    return () => ctl.abort();
  }, [entity, id, version]);

  useEffect(() => {
    if (!open) return;
    ref.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
    const onDoc = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); button.current?.focus(); return; }
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      e.preventDefault();
      const items = [...(ref.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
      const i = items.indexOf(document.activeElement as HTMLElement);
      items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus();
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDoc); document.removeEventListener('keydown', onKey); };
  }, [open]);

  if (!choices.length) return null;

  async function download(template: string) {
    setOpen(false);
    setBusy(true);
    try {
      const { blob, name } = await dataApi.pdf(entity, id, template);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = name;
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
      toast.success(`PDF descargado: ${name}`);
    } catch (e) {
      toast.error(e instanceof ApiError ? e.title : 'No se pudo generar el PDF.');
    } finally {
      setBusy(false);
      button.current?.focus();
    }
  }

  if (choices.length === 1) {
    return (
      <Button ref={button} icon={FileDown} loading={busy} onClick={() => void download(choices[0].name)} title={choices[0].label}>
        Descargar PDF
      </Button>
    );
  }
  return (
    <div className="menu-anchor" ref={ref}>
      <Button ref={button} icon={FileDown} loading={busy} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        Descargar PDF <ChevronDown size={14} aria-hidden />
      </Button>
      {open && (
        <div className="menu" role="menu" aria-label="Plantillas de impresión">
          <div className="menu-header subtle" style={{ fontSize: 'var(--text-sm)' }}>Elige la plantilla</div>
          {choices.map((c) => (
            <button key={c.name} className="menu-item" role="menuitem" onClick={() => void download(c.name)}>
              <span className="grow" style={{ minWidth: 0 }}>
                <span style={{ display: 'block', fontWeight: c.default ? 500 : undefined }}>{c.label}{c.default ? ' (predeterminada)' : ''}</span>
                {c.description && <span className="subtle" style={{ fontSize: 'var(--text-xs)' }}>{c.description}</span>}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
