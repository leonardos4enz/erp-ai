import { ListPlus, Plus, Trash2 } from 'lucide-react';
import { fieldError, type ApiError } from '../api/client';
import type { EntityDef, FieldDef } from '../api/types';
import { formatFormula, isEditableField, isNumericField, isRequiredFor } from '../lib/entity';
import { lineErrorKey, lineFields, lookupFilterFor, type LineDraft, type Values } from '../lib/draft';
import { FieldControl } from './FieldInput';
import { Button } from './ui';

export interface LinesEditorProps {
  field: FieldDef;
  child: EntityDef;
  lines: LineDraft[];
  roles: string[];
  readOnly: boolean;
  /** Documento dueño de las partidas: para filtros como `Recepcion.OrdenCompra`. */
  parent?: { def: EntityDef; values: Values };
  serverError: ApiError | null;
  clientErrors: Record<string, string>;
  onAdd: () => void;
  onRemove: (index: number) => void;
  onChange: (index: number, field: string, value: unknown, label?: string | null) => void;
}

/** Partidas de un documento: tabla editable con una fila por renglón. */
export function LinesEditor({ field, child, lines, roles, readOnly, parent, serverError, clientErrors, onAdd, onRemove, onChange }: LinesEditorProps) {
  const columns = lineFields(child).filter((f) => f.showInList !== false);
  const totalsOnly = (f: FieldDef) => f.type === 'formula' || f.type === 'autonumber';

  return (
    <section className="card lines-card" aria-labelledby={`lines-${field.name}-title`}>
      <div className="lines-head">
        <h2 id={`lines-${field.name}-title`} className="section-title grow">
          {field.label}
          <span className="subtle lines-count">{lines.length} {lines.length === 1 ? 'partida' : 'partidas'}</span>
        </h2>
        {!readOnly && <Button size="sm" icon={Plus} onClick={onAdd} aria-label="Agregar partida">Agregar partida</Button>}
      </div>

      {lines.length === 0 ? (
        <div className="lines-empty">
          <ListPlus size={20} aria-hidden />
          <span>{readOnly ? 'Este documento no tiene partidas.' : 'Agrega la primera partida para empezar.'}</span>
        </div>
      ) : (
        <div className="table-scroll lines-scroll" tabIndex={0} role="region" aria-label={`${field.label}: tabla desplazable`}>
          <table className="table lines-table">
            <thead>
              <tr>
                <th className="lines-num" scope="col">#</th>
                {columns.map((c) => (
                  <th key={c.name} scope="col" className={isNumericField(c) ? 'td-num' : undefined}>
                    {c.label}{!readOnly && isRequiredFor(c, roles) && !totalsOnly(c) && <span className="field-required" aria-hidden>*</span>}
                  </th>
                ))}
                {!readOnly && <th className="td-actions" scope="col"><span className="sr-only">Acciones</span></th>}
              </tr>
            </thead>
            <tbody>
              {lines.map((l, i) => (
                <tr key={l._key}>
                  <td className="lines-num subtle">{i + 1}</td>
                  {columns.map((c) => {
                    const key = lineErrorKey(field.name, i, c.name);
                    const err = clientErrors[key] ?? fieldError(serverError, key);
                    const id = `f-${field.name}-${i}-${c.name}`;
                    const name = `${c.label}, partida ${i + 1}`;
                    if (readOnly && !totalsOnly(c)) {
                      return (
                        <td key={c.name} className={isNumericField(c) ? 'td-num' : undefined}>
                          <ReadOnlyCell field={c} line={l} />
                        </td>
                      );
                    }
                    return (
                      <td key={c.name} className={`lines-cell${isNumericField(c) ? ' td-num' : ''}${c.type === 'reference' || c.type === 'user' ? ' lines-cell-wide' : ''}`}>
                        <FieldControl
                          field={c}
                          def={child}
                          id={id}
                          value={l.values[c.name]}
                          refLabel={l.values[`${c.name}__label`] as string | null}
                          onChange={(v, label) => onChange(i, c.name, v, label)}
                          disabled={readOnly || (!isEditableField(c) && !totalsOnly(c))}
                          required={isRequiredFor(c, roles)}
                          invalid={!!err}
                          describedBy={err ? `${id}-err` : undefined}
                          ariaLabel={name}
                          isNew={l.id == null}
                          filter={lookupFilterFor(c, { def: child, values: l.values }, parent)}
                          compact
                        />
                        {err && <span id={`${id}-err`} className="field-error cell-error" role="alert">{err}</span>}
                      </td>
                    );
                  })}
                  {!readOnly && (
                    <td className="td-actions">
                      <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Eliminar partida ${i + 1}`} title="Eliminar partida" onClick={() => onRemove(i)} />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {fieldError(serverError, field.name) && (
        <div className="lines-foot"><span className="field-error" role="alert">{fieldError(serverError, field.name)}</span></div>
      )}
    </section>
  );
}

function ReadOnlyCell({ field, line }: { field: FieldDef; line: LineDraft }) {
  const v = line.values[field.name];
  if (v === null || v === undefined || v === '') return <span className="subtle">—</span>;
  switch (field.type) {
    case 'boolean': return <>{v ? 'Sí' : 'No'}</>;
    case 'reference':
    case 'user': return <>{String(line.values[`${field.name}__label`] ?? `#${String(v)}`)}</>;
    case 'decimal': return <>{Number(v).toLocaleString('es-MX', { maximumFractionDigits: field.scale ?? 6 })}</>;
    case 'integer': return <>{Number(v).toLocaleString('es-MX')}</>;
    case 'formula': return <>{formatFormula(field, v)}</>;
    default: return <>{String(v)}</>;
  }
}
