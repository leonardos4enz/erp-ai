import { AlertTriangle } from 'lucide-react';
import type { Warnings } from '../api/endpoints';
import type { EntityDef } from '../api/types';

/** Spec 023: avisos como texto: «Campo: mensaje», «Partida 2 · Campo: mensaje» o solo el mensaje (regla del registro). */
export function warningLines(w: Warnings | null | undefined, def?: EntityDef, children?: Record<string, EntityDef>): string[] {
  if (!w) return [];
  const label = (d: EntityDef | undefined, name: string) => d?.fields.find((f) => f.name.toLowerCase() === name.toLowerCase())?.label ?? name;
  const out: string[] = [];
  for (const [key, messages] of Object.entries(w)) {
    const m = /^(\w+)\[(\d+)\]\.(\w+)$/.exec(key);
    let where = '';
    if (m) {
      const lf = def?.fields.find((f) => f.name.toLowerCase() === m[1].toLowerCase());
      const child = lf?.childEntity ? children?.[lf.childEntity] : undefined;
      where = `Partida ${Number(m[2]) + 1}${m[3].toLowerCase() === 'registro' ? '' : ` · ${label(child, m[3])}`}`;
    } else if (key.toLowerCase() !== 'registro') where = label(def, key);
    for (const msg of messages) out.push(where ? `${where}: ${msg}` : msg);
  }
  return [...new Set(out)];
}

/**
 * Spec 023: avisos vigentes del registro. No impiden guardar: en el formulario son estado (`status`, no interrumpen al
 * lector); en el diálogo de una acción que pide confirmarlos se anuncian (`alert`), como los errores (F-03 de la 022).
 */
export function WarningsAlert({ warnings, def, childDefs, title = 'Avisos', announce = false }: {
  warnings: Warnings | null | undefined; def?: EntityDef; childDefs?: Record<string, EntityDef>; title?: string; announce?: boolean;
}) {
  const lines = warningLines(warnings, def, childDefs);
  if (!lines.length) return null;
  return (
    <div className="alert alert-warning" role={announce ? 'alert' : 'status'}>
      <AlertTriangle size={16} aria-hidden />
      <div className="grow">
        <div style={{ fontWeight: 500 }}>{title}</div>
        {lines.length === 1 ? <div>{lines[0]}</div> : <ul className="warning-list">{lines.map((l) => <li key={l}>{l}</li>)}</ul>}
      </div>
    </div>
  );
}
