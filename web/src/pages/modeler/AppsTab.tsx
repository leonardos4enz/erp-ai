import { useState } from 'react';
import { Archive, LayoutGrid, Pencil, Plus } from 'lucide-react';
import { ApiError } from '../../api/client';
import { modelApi } from '../../api/endpoints';
import type { AppSummary, ChangePlan } from '../../api/types';
import { Dialog } from '../../components/Dialog';
import { DynamicIcon } from '../../components/DynamicIcon';
import { IconPicker } from '../../components/IconPicker';
import { Alert, Badge, Button, EmptyState, Input, LoadingBlock, Textarea } from '../../components/ui';
import { useAsync } from '../../lib/hooks';

const MAX_NAME = 60;
const MAX_DESCRIPTION = 300;
const MAX_ORDER = 9999;

interface Draft { name: string; label: string; icon: string; description: string; order: string; isNew: boolean; fixedName: boolean }

function draftOf(a?: AppSummary): Draft {
  return a
    ? { name: a.name, label: a.label, icon: a.icon, description: a.description ?? '', order: a.declared ? String(a.order) : '', isNew: !a.declared, fixedName: true }
    : { name: '', label: '', icon: 'layout-grid', description: '', order: '', isNew: true, fixedName: false };
}

/** Lo mismo que revisa el compilador, para avisar antes de la vista previa. */
function validate(d: Draft): Record<string, string> {
  const e: Record<string, string> = {};
  if (!d.name.trim()) e.name = 'Escribe el nombre del módulo.';
  else if (d.name.trim().length > MAX_NAME) e.name = `Máximo ${MAX_NAME} caracteres.`;
  if (!d.label.trim()) e.label = 'Escribe la etiqueta que verán las personas.';
  if (d.order.trim() && !(/^\d+$/.test(d.order.trim()) && Number(d.order) <= MAX_ORDER)) e.order = `Un número de 0 a ${MAX_ORDER}.`;
  return e;
}

/**
 * Spec 024 (H2 CA3): módulos del menú. Lista los declarados y los implícitos (un grupo de catálogos sin declaración) y
 * produce `setApp`/`archiveApp`, que pasan por la misma vista previa que cualquier cambio del modelo.
 */
export function AppsTab({ version, onReview }: { version: number; onReview: (plan: ChangePlan) => void }) {
  const { data, loading, error } = useAsync((s) => modelApi.apps(true, s), [version]);
  const [editing, setEditing] = useState<Draft | null>(null);
  const [showErrors, setShowErrors] = useState(false);
  const errors = editing ? validate(editing) : {};
  const visible = showErrors ? errors : {};

  const open = (a?: AppSummary) => { setShowErrors(false); setEditing(draftOf(a)); };
  const review = () => {
    if (!editing) return;
    if (Object.keys(errors).length) { setShowErrors(true); return; }
    const app = {
      name: editing.name.trim(), label: editing.label.trim(), icon: editing.icon,
      description: editing.description.trim() || undefined, order: editing.order.trim() ? Number(editing.order) : undefined,
    };
    setEditing(null);
    onReview({ description: `${editing.isNew ? 'Declarar' : 'Actualizar'} módulo ${app.label}`, operations: [{ op: 'setApp', app }] });
  };

  if (loading && !data) return <LoadingBlock />;
  if (error) return <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar los módulos'} />;
  const list = data ?? [];
  return (
    <div className="stack">
      <div className="row" style={{ justifyContent: 'space-between', flexWrap: 'wrap', gap: 'var(--space-3)' }}>
        <p className="subtle" style={{ margin: 0 }}>
          Cada módulo agrupa los catálogos cuyo <strong>módulo</strong> es su nombre. Sin módulo, un catálogo queda en General.
        </p>
        <Button variant="primary" icon={Plus} onClick={() => open()}>Nuevo módulo</Button>
      </div>
      {list.length === 0 ? (
        <div className="card"><EmptyState icon={LayoutGrid} title="Aún no hay módulos" description="Declara uno o asigna un módulo a tus catálogos." /></div>
      ) : (
        <div className="table-wrap">
          <div className="table-scroll">
            <table className="table">
              <caption className="sr-only">Módulos del menú</caption>
              <thead><tr><th>Módulo</th><th>Nombre</th><th className="td-num">Catálogos</th><th className="td-num">Orden</th><th>Estado</th><th className="td-actions"><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {list.map((a) => (
                  <tr key={`${a.name}-${a.declared}-${!!a.isArchived}`}>
                    <td>
                      <div className="row" style={{ gap: 'var(--space-3)' }}>
                        <span className="entity-card-icon" style={{ width: 30, height: 30 }}><DynamicIcon name={a.icon} size={15} /></span>
                        <div>
                          <div style={{ fontWeight: 500 }}>{a.label}</div>
                          {a.description && <div className="subtle td-truncate" style={{ fontSize: 'var(--text-sm)' }}>{a.description}</div>}
                        </div>
                      </div>
                    </td>
                    <td className="mono muted">{a.name}</td>
                    <td className="td-num">{a.entities.length}</td>
                    <td className="td-num">{a.declared ? a.order : '—'}</td>
                    <td>
                      {a.isArchived ? <Badge tone="warning">Archivado</Badge> : a.declared ? <Badge tone="success">Declarado</Badge> : <Badge>Implícito</Badge>}
                    </td>
                    <td className="td-actions">
                      <div className="row" style={{ justifyContent: 'flex-end', gap: 2 }}>
                        <Button variant="ghost" size="sm" icon={Pencil} aria-label={`${a.declared && !a.isArchived ? 'Editar' : 'Declarar'} módulo ${a.label}`}
                          onClick={() => open(a.isArchived ? { ...a, declared: false } : a)}>{a.declared && !a.isArchived ? 'Editar' : 'Declarar'}</Button>
                        {a.declared && !a.isArchived && (
                          <Button variant="ghost" size="sm" iconOnly icon={Archive} aria-label={`Archivar módulo ${a.label}`} title="Archivar"
                            onClick={() => onReview({ description: `Archivar módulo ${a.label}`, operations: [{ op: 'archiveApp', name: a.name }] })} />
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <Dialog
        open={!!editing}
        onClose={() => setEditing(null)}
        title={editing?.isNew ? 'Declarar módulo' : `Editar módulo ${editing?.label ?? ''}`}
        description="El módulo aparece como tarjeta en el inicio y agrupa sus catálogos en el menú."
        footer={<>
          <Button onClick={() => setEditing(null)}>Cancelar</Button>
          <Button variant="primary" onClick={review}>Revisar cambios</Button>
        </>}
      >
        {editing && (
          <div className="stack">
            <Input label="Nombre" data-autofocus={editing.fixedName ? undefined : true} maxLength={MAX_NAME} value={editing.name}
              disabled={editing.fixedName}
              hint="Es el módulo que se escribe en cada catálogo (p. ej. Compras). No se cambia después."
              error={visible.name} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
            <Input label="Etiqueta" data-autofocus={editing.fixedName ? true : undefined} maxLength={100} value={editing.label}
              error={visible.label} onChange={(e) => setEditing({ ...editing, label: e.target.value })} />
            <IconPicker value={editing.icon} onChange={(icon) => setEditing({ ...editing, icon: icon ?? 'layout-grid' })} />
            <Textarea label="Descripción" hint="Una línea para la tarjeta del inicio." rows={2} maxLength={MAX_DESCRIPTION} value={editing.description}
              onChange={(e) => setEditing({ ...editing, description: e.target.value })} />
            <Input label="Orden" inputMode="numeric" hint={`De 0 a ${MAX_ORDER}; los menores van primero. Vacío: 100.`} value={editing.order}
              error={visible.order} onChange={(e) => setEditing({ ...editing, order: e.target.value })} />
          </div>
        )}
      </Dialog>
    </div>
  );
}
