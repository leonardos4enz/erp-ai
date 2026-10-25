import { useId, useRef, useState } from 'react';
import { ImageOff, Trash2, Upload } from 'lucide-react';
import { ApiError } from '../api/client';
import { ConfirmDialog } from './Dialog';
import { Button } from './ui';

/**
 * Spec 027: subir, ver y quitar una imagen (logo de empresa o imagen de plantilla). El navegador solo sugiere PNG o JPEG:
 * quien decide es el servidor, que revisa el contenido del archivo. Sus mensajes se muestran tal cual (dicen qué falló).
 */
export function ImageUpload({ label, url, alt, hint, onUpload, onRemove, removeMessage, disabled }: {
  label: string;
  url?: string | null;
  alt: string;
  hint?: string;
  onUpload: (file: File) => Promise<void>;
  onRemove?: () => Promise<void>;
  removeMessage?: string;
  disabled?: boolean;
}) {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<'up' | 'rm' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);

  async function upload(file: File | undefined) {
    if (!file) return;
    setBusy('up');
    setError(null);
    try {
      await onUpload(file);
    } catch (e) {
      setError(e instanceof ApiError ? (Object.values(e.fieldErrors)[0]?.[0] ?? e.title) : 'No se pudo subir la imagen.');
    } finally {
      setBusy(null);
      if (input.current) input.current.value = '';
    }
  }

  async function remove() {
    if (!onRemove) return;
    setBusy('rm');
    setError(null);
    try {
      await onRemove();
      setConfirm(false);
    } catch (e) {
      setError(e instanceof ApiError ? e.title : 'No se pudo quitar la imagen.');
    } finally {
      setBusy(null);
    }
  }

  return (
    <fieldset className="image-upload" aria-describedby={`${id}-hint`}>
      <legend className="field-label">{label}</legend>
      <div className="image-upload-row">
        <div className="image-upload-thumb">
          {url ? <img src={url} alt={alt} /> : <span className="subtle"><ImageOff size={20} aria-hidden /><span className="sr-only">Sin imagen</span></span>}
        </div>
        <div className="stack" style={{ gap: 'var(--space-2)' }}>
          <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
            <input ref={input} id={`${id}-file`} type="file" accept="image/png,image/jpeg" className="sr-only" tabIndex={-1}
              aria-label={`${label}: archivo`} onChange={(e) => void upload(e.target.files?.[0])} disabled={disabled || !!busy} />
            <Button size="sm" icon={Upload} loading={busy === 'up'} disabled={disabled || !!busy} onClick={() => input.current?.click()}>
              {url ? 'Cambiar imagen' : 'Subir imagen'}
            </Button>
            {url && onRemove && (
              <Button size="sm" variant="ghost" icon={Trash2} disabled={disabled || !!busy} onClick={() => setConfirm(true)}>Quitar</Button>
            )}
          </div>
          <span id={`${id}-hint`} className="field-hint">{hint ?? 'PNG o JPEG, hasta 1 MB y 3000 px por lado.'}</span>
          {error && <span className="field-error" role="alert">{error}</span>}
        </div>
      </div>
      <ConfirmDialog open={confirm} danger title={`Quitar ${label.toLowerCase()}`} message={removeMessage ?? 'La imagen deja de usarse. Se conserva archivada en el historial.'}
        confirmLabel="Quitar" loading={busy === 'rm'} onConfirm={() => void remove()} onCancel={() => setConfirm(false)} />
    </fieldset>
  );
}
