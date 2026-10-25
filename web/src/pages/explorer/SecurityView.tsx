import { useMemo, useState } from 'react';
import { ListTree, ShieldCheck } from 'lucide-react';
import { ApiError } from '../../api/client';
import { explorerApi, type NavApp, type RoleAccess, type SecurityMatrix } from '../../api/explorer';
import { DynamicIcon } from '../../components/DynamicIcon';
import { Alert, Badge, EmptyState, LoadingBlock, Select } from '../../components/ui';
import { useAsync } from '../../lib/hooks';
import { typeLabel } from './common';

const SCOPES: Record<string, string> = { all: 'todos', own: 'propios', unit: 'de su unidad', unitTree: 'de su unidad y descendientes' };
const crud = (c: RoleAccess) => [c.read && 'L', c.create && 'C', c.update && 'E', c.delete && 'B'].filter(Boolean).join(' ');
const crudText = (c: RoleAccess) => [c.read && 'leer', c.create && 'crear', c.update && 'editar', c.delete && 'eliminar'].filter(Boolean).join(', ') || 'sin acceso';
const scopeText = (c: RoleAccess) => c.scopes.map((s) => `${s.access === 'read' ? 'lee' : 'escribe'} ${SCOPES[s.scope] ?? s.scope}${s.where ? ` donde ${s.where}` : ''}`).join('; ');

export function SecurityView() {
  const sec = useAsync<SecurityMatrix>((s) => explorerApi.security(s), []);
  const nav = useAsync<NavApp[]>((s) => explorerApi.navigation(s), []);
  const [role, setRole] = useState('');
  const [app, setApp] = useState('');
  const apps = useMemo(() => [...new Map((sec.data?.entities ?? []).map((e) => [e.app, e.appLabel])).entries()], [sec.data]);
  const entities = (sec.data?.entities ?? []).filter((e) => !app || e.app === app);
  const roles = (sec.data?.roles ?? []).filter((r) => !role || r.name === role);
  const cell = (r: string, e: string) => sec.data?.cells.find((c) => c.role === r && c.entity === e);

  return (
    <div className="stack">
      <section className="stack" aria-labelledby="ex-sec">
        <h2 className="ex-h2" id="ex-sec">Matriz de roles y catálogos</h2>
        <div className="ex-filters">
          <Select label="Rol" selectSize="sm" value={role} onChange={(e) => setRole(e.target.value)}>
            <option value="">Todos los roles</option>
            {sec.data?.roles.map((r) => <option key={r.name} value={r.name}>{r.name}</option>)}
          </Select>
          <Select label="Módulo" selectSize="sm" value={app} onChange={(e) => setApp(e.target.value)}>
            <option value="">Todos los módulos</option>
            {apps.map(([a, l]) => <option key={a} value={a}>{l}</option>)}
          </Select>
        </div>
        {sec.loading && <LoadingBlock />}
        {!!sec.error && <Alert tone="danger" title={sec.error instanceof ApiError ? sec.error.title : 'No se pudo leer la seguridad'} />}
        {sec.data && entities.length === 0 && <div className="card"><EmptyState icon={ShieldCheck} title="Sin catálogos" /></div>}
        {sec.data && entities.length > 0 && !role && (
          <>
            <p className="subtle">L leer · C crear · E editar · B eliminar · <Badge tone="accent">h</Badge> incluye permisos heredados · elige un rol para ver alcance y campos ocultos.</p>
            <div className="table-wrap">
              <div className="table-scroll ex-matrix" tabIndex={0}>
                <table className="table table-compact">
                  <caption className="sr-only">Permisos de cada rol en cada catálogo</caption>
                  <thead>
                    <tr><th scope="col">Catálogo</th>{roles.map((r) => <th key={r.name} scope="col">{r.name}{r.inherits.length > 0 && <div className="subtle">hereda de {r.inherits.join(', ')}</div>}</th>)}</tr>
                  </thead>
                  <tbody>
                    {entities.map((e) => (
                      <tr key={e.name}>
                        <th scope="row">{e.label}<div className="subtle">{e.appLabel}</div></th>
                        {roles.map((r) => {
                          const c = cell(r.name, e.name);
                          if (!c) return <td key={r.name}>—</td>;
                          // Regresión (revisión 025, F-04): ocultos y alcance solo importan si el rol lee el catálogo.
                          const extra = c.read ? [c.hidden.length && `${c.hidden.length} ${c.hidden.length === 1 ? 'oculto' : 'ocultos'}`, c.scopes.length && 'alcance'].filter(Boolean).join(' · ') : '';
                          return (
                            <td key={r.name} className="mono">
                              <span aria-hidden>{crud(c) || '—'}</span>
                              <span className="sr-only">{crudText(c)}{c.inheritedFrom.length ? `, heredado de ${c.inheritedFrom.join(', ')}` : ''}</span>
                              {c.inheritedFrom.length > 0 && <> <Badge tone="accent">h</Badge></>}
                              {extra && <div className="subtle ex-cell-extra">{extra}</div>}
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </>
        )}
        {sec.data && entities.length > 0 && role && (
          <div className="table-wrap">
            <div className="table-scroll" tabIndex={0}>
              <table className="table table-compact">
                <caption className="sr-only">Permisos del rol {role}</caption>
                <thead><tr><th scope="col">Catálogo</th><th scope="col">Permisos</th><th scope="col">Heredado de</th><th scope="col">Alcance por registro</th><th scope="col">Campos ocultos</th><th scope="col">Solo lectura</th></tr></thead>
                <tbody>
                  {entities.map((e) => {
                    const c = cell(role, e.name);
                    return c ? (
                      <tr key={e.name}>
                        <th scope="row">{e.label}</th>
                        <td>{crudText(c)}</td>
                        <td>{c.inheritedFrom.join(', ') || '—'}</td>
                        <td>{scopeText(c) || (c.read ? 'todos' : '—')}</td>
                        <td className="mono">{c.hidden.join(', ') || '—'}</td>
                        <td className="mono">{c.readOnly.join(', ') || '—'}</td>
                      </tr>
                    ) : null;
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </section>

      <section className="stack" aria-labelledby="ex-nav">
        <h2 className="ex-h2" id="ex-nav"><ListTree size={18} aria-hidden /> Navegación</h2>
        <p className="subtle">Módulo → menú → pantalla → secciones y pestañas → campos.</p>
        {nav.loading && <LoadingBlock />}
        {!!nav.error && <Alert tone="danger" title="No se pudo leer la navegación" />}
        {nav.data && (
          <ul className="ex-tree card card-pad">
            {nav.data.map((a) => (
              <li key={a.name}>
                <details>
                  <summary><DynamicIcon name={a.icon} size={15} aria-hidden /> <strong>{a.label}</strong> <span className="subtle">({a.entities.length} pantallas)</span></summary>
                  <ul>
                    {a.entities.map((e) => (
                      <li key={e.name}>
                        <details>
                          <summary><DynamicIcon name={e.icon} size={14} aria-hidden /> {e.labelPlural} <span className="subtle mono">/c/{e.name}</span>{e.hasWorkflow && <> <Badge>proceso</Badge></>}{!e.layout && <> <Badge>sin layout</Badge></>}</summary>
                          <ul>
                            {e.sections.map((s) => (
                              <li key={s.name}>
                                <details>
                                  <summary>{s.label}{s.tab && <span className="subtle"> · pestaña {s.tab}</span>} <span className="subtle">({s.fields.length} campos)</span></summary>
                                  <ul>{s.fields.map((f) => <li key={f.name}>{f.label} <span className="subtle">{typeLabel(f.type)}</span></li>)}</ul>
                                </details>
                              </li>
                            ))}
                            {e.lines.length > 0 && <li className="subtle">Partidas: {e.lines.join(', ')}</li>}
                          </ul>
                        </details>
                      </li>
                    ))}
                  </ul>
                </details>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
