import { useEffect, useState } from 'react';
import { ApiError } from '../api/client';
import type { ShareResult } from '../api/types';
import { Dialog } from './Dialog';
import { Alert, Button, Checkbox } from './ui';

/**
 * Spec 042: compartir un reporte o tablero personal por rol. Solo ofrece los roles que la persona tiene (el servidor los da
 * en `shareable`); sin casillas deja de compartir. Guarda con la versión que se abrió: un 409 se dice en el diálogo.
 */
export function ShareDialog({ open, what, shareable, current, onClose, onSave }: {
  open: boolean;
  what: string;
  shareable: string[];
  current: string[];
  onClose: () => void;
  onSave: (roles: string[]) => Promise<ShareResult>;
}) {
  const [chosen, setChosen] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  useEffect(() => { if (open) { setChosen(current); setError(null); } }, [open, current]);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await onSave(chosen);
    } catch (e) {
      if (e instanceof ApiError) setError(e); else throw e;
    } finally {
      setBusy(false);
    }
  }

  const toggle = (role: string, on: boolean) => setChosen((c) => (on ? [...c, role] : c.filter((r) => r !== role)));
  return (
    <Dialog open={open} onClose={onClose} title={`Compartir ${what}`}
      description="Quien tenga uno de estos roles lo verá con sus propios permisos: nunca con los tuyos. Solo puedes elegir roles que tienes."
      footer={<><Button onClick={onClose} disabled={busy}>Cancelar</Button><Button variant="primary" loading={busy} onClick={save}>Guardar</Button></>}>
      <div className="stack">
        {error && (
          <Alert tone="danger" title={error.status === 409 ? 'Otra pestaña lo cambió' : error.title}>
            {error.status === 409 ? 'Vuelve a abrirlo para ver la versión actual antes de compartirlo.' : Object.values(error.fieldErrors).flat().join(' ') || undefined}
          </Alert>
        )}
        {shareable.length === 0 ? (
          <p className="subtle">No tienes roles con los que compartir.</p>
        ) : (
          <fieldset className="wf-fieldset">
            <legend className="field-label">Roles</legend>
            {/* Revisión exploratoria 042 (F-05): el diálogo enfocaba «Cerrar»; lo que se usa es la primera casilla. */}
            {shareable.map((role, i) => (
              <Checkbox key={role} label={role} checked={chosen.includes(role)} onChange={(on) => toggle(role, on)} autoFocusInDialog={i === 0} />
            ))}
          </fieldset>
        )}
        {chosen.length === 0 && current.length > 0 && <p className="subtle">Sin roles, deja de compartirse.</p>}
      </div>
    </Dialog>
  );
}
