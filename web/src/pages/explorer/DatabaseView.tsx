import { useMemo, useRef, useState } from 'react';
import { Crosshair, Database, FileCode2, ImageDown, KeyRound, Link2, Search, Table2, TriangleAlert, Waypoints } from 'lucide-react';
import { ApiError } from '../../api/client';
import { explorerApi, type DbColumn, type DbForeignKey, type DbSchema, type DbTable, type DriftKind } from '../../api/explorer';
import { useAuth } from '../../auth/AuthContext';
import { BoxGraph, type BoxGraphHandle, type GraphEdge, type GraphNode } from '../../components/BoxGraph';
import { useToast } from '../../components/Toast';
import { Alert, Badge, Button, EmptyState, Input, LoadingBlock, Select, Switch } from '../../components/ui';
import { neighborhood } from '../../lib/graphLayout';
import { useAsync, useDebounced } from '../../lib/hooks';
import { download, exportPng, exportSvg } from '../../lib/svgExport';
import { AppLegend, EntityLinks, appColors, colorOf, type ExplorerView } from './common';

const DRIFT_LABELS: Record<DriftKind, string> = {
  orphanTable: 'Tabla sin catálogo',
  archivedTable: 'Catálogo archivado',
  missingTable: 'Falta la tabla',
  orphanColumn: 'Columna sin campo',
  archivedColumn: 'Campo archivado',
  missingColumn: 'Falta la columna',
  typeMismatch: 'Tipo distinto',
};
const STATUS_LABELS: Record<DbColumn['status'], string> = {
  ok: 'Coincide', system: 'Sistema', archived: 'Campo archivado', orphan: 'Sin campo', mismatch: 'Tipo distinto',
};
const tableId = (schema: string, name: string) => `${schema}.${name}`;

/** «Cada OrdenCompra tiene cero o un Proveedor; cada Proveedor tiene cero o muchas OrdenCompra.» */
export function cardinality(t: DbTable, f: DbForeignKey) {
  return `Cada ${t.name} tiene ${f.nullable ? 'cero o un' : 'exactamente un'} ${f.refTable}; cada ${f.refTable} tiene ${f.unique ? 'cero o un' : 'cero o muchos'} ${t.name}.`;
}

interface Props { entity?: string; onEntity: (e?: string) => void; onOpen: (v: ExplorerView, e?: string) => void }

export function DatabaseView({ entity, onEntity, onOpen }: Props) {
  const { me } = useAuth();
  const toast = useToast();
  const [schema, setSchema] = useState('app');
  const [app, setApp] = useState('');
  const [text, setText] = useState('');
  const q = useDebounced(text.trim(), 300);
  const [mode, setMode] = useState<'diagram' | 'table'>('diagram');
  const [keysOnly, setKeysOnly] = useState(false);
  const [driftOnly, setDriftOnly] = useState(false);
  // Regresión (revisión 025, F-05): al llegar con una tabla elegida (p. ej. desde la ficha) se enfoca: entre 100+ tablas se perdía.
  const [focus, setFocus] = useState(!!entity);
  const [levels, setLevels] = useState(1);
  const [showDrift, setShowDrift] = useState(false);
  const graph = useRef<BoxGraphHandle>(null);
  const { data, loading, error } = useAsync<DbSchema>((s) => explorerApi.database({ schema, app: app || undefined, q: q || undefined }, s), [schema, app, q]);

  const selected = entity ? tableId(schema, entity) : null;
  const colors = useMemo(() => appColors(data?.apps ?? []), [data]);

  // Lo que se muestra: filtros del servidor + solo deriva + foco (en el cliente, sobre lo que ya llegó filtrado).
  const tables = useMemo(() => {
    let list = data?.tables ?? [];
    if (driftOnly) list = list.filter((t) => t.driftCount > 0);
    if (focus && selected && list.some((t) => tableId(t.schema, t.name) === selected)) {
      const links = list.flatMap((t) => t.foreignKeys.map((f) => ({ from: tableId(t.schema, t.name), to: tableId(f.refSchema, f.refTable) })));
      const near = neighborhood(selected, links, levels);
      list = list.filter((t) => near.has(tableId(t.schema, t.name)));
    }
    return list;
  }, [data, driftOnly, focus, selected, levels]);

  const { nodes, edges } = useMemo(() => {
    const shown = new Set(tables.map((t) => tableId(t.schema, t.name)));
    const nodes: GraphNode[] = tables.map((t) => {
      const fkCols = new Set(t.foreignKeys.flatMap((f) => f.columns.map((c) => c.toLowerCase())));
      const pkCols = new Set((t.primaryKey?.columns ?? []).map((c) => c.toLowerCase()));
      const cols = keysOnly ? t.columns.filter((c) => pkCols.has(c.name.toLowerCase()) || fkCols.has(c.name.toLowerCase())) : t.columns;
      const e = t.entity;
      return {
        id: tableId(t.schema, t.name), title: `${t.schema}.${t.name}`, subtitle: e?.label,
        color: colorOf(colors, e?.app),
        tone: e?.isArchived ? 'archived' : t.driftCount > 0 ? 'warning' : undefined,
        label: `Tabla ${t.schema}.${t.name}${e ? `, catálogo ${e.label} del módulo ${e.appLabel}` : ''}, ${t.columns.length} columnas, ${t.foreignKeys.length} llaves foráneas${t.driftCount ? `, ${t.driftCount} con deriva` : ''}`,
        rows: cols.map((c) => {
          const pk = pkCols.has(c.name.toLowerCase()), fk = fkCols.has(c.name.toLowerCase());
          return {
            key: c.name, text: c.name, detail: `${c.type}${c.nullable ? '' : ' NN'}`,
            kind: pk && fk ? 'pkfk' : pk ? 'pk' : fk ? 'fk' : undefined,
            tone: c.status === 'mismatch' ? 'danger' : c.status === 'archived' || c.status === 'orphan' ? 'warning' : c.system ? 'muted' : undefined,
          } as const;
        }),
      };
    });
    const external = new Map<string, GraphNode>();
    const edges: GraphEdge[] = [];
    for (const t of tables)
      for (const f of t.foreignKeys) {
        const to = tableId(f.refSchema, f.refTable);
        if (!shown.has(to) && !external.has(to))
          external.set(to, { id: to, title: to, subtitle: f.refSchema === 'meta' ? 'sistema' : 'fuera del filtro', rows: [], tone: 'external', label: `Tabla externa ${to}` });
        edges.push({
          id: `${t.name}:${f.name}`, from: tableId(t.schema, t.name), fromRow: keysOnly || f.columns[0] ? f.columns[0] : undefined,
          to, toRow: shown.has(to) ? f.refColumns[0] : undefined,
          start: f.unique ? 'zeroOne' : 'zeroMany', end: f.nullable ? 'zeroOne' : 'one',
        });
      }
    return { nodes: [...nodes, ...external.values()], edges };
  }, [tables, keysOnly, colors]);

  const current = data?.tables.find((t) => tableId(t.schema, t.name) === selected);
  const select = (id: string) => {
    const [s, ...rest] = id.split('.');
    if (s !== schema) return; // una tabla externa: no tiene detalle aquí
    onEntity(rest.join('.'));
  };

  async function exportImage(kind: 'svg' | 'png') {
    const svg = graph.current?.svg;
    if (!svg || !graph.current) return;
    const title = `Diagrama ER del esquema ${schema}`;
    try {
      if (kind === 'svg') exportSvg(svg, graph.current.layoutSize, `diagrama-${schema}.svg`, title);
      else await exportPng(svg, graph.current.layoutSize, `diagrama-${schema}.png`, title);
    } catch {
      toast.error('No se pudo generar la imagen.');
    }
  }
  async function exportDdl() {
    try {
      const blob = await explorerApi.ddl({ schema, app: app || undefined, q: q || undefined, tables: tables.map((t) => t.name) });
      download(blob, `esquema-${schema}.sql`);
    } catch (e) {
      toast.error(e instanceof ApiError ? e.title : 'No se pudo generar el DDL.');
    }
  }

  const driftCounts = useMemo(() => {
    const m = new Map<DriftKind, number>();
    for (const d of data?.drift ?? []) m.set(d.kind, (m.get(d.kind) ?? 0) + 1);
    return [...m.entries()];
  }, [data]);

  return (
    <div className="stack">
      <div className="ex-filters" role="search" aria-label="Filtros de la base de datos">
        {me?.isSuperuser && (
          <Select label="Esquema" selectSize="sm" value={schema} onChange={(e) => { setSchema(e.target.value); setApp(''); onEntity(undefined); }}>
            <option value="app">app (catálogos)</option>
            <option value="meta">meta (sistema)</option>
          </Select>
        )}
        {schema === 'app' && (
          <Select label="Módulo" selectSize="sm" value={app} onChange={(e) => setApp(e.target.value)}>
            <option value="">Todos los módulos</option>
            {data?.apps.map((a) => <option key={a.name} value={a.name}>{a.label}</option>)}
          </Select>
        )}
        <Input label="Buscar tabla o columna" inputSize="sm" icon={Search} value={text} maxLength={100} onChange={(e) => setText(e.target.value)} placeholder="OrdenCompra, Folio…" />
        <div className="ex-switches">
          <Switch label="Solo llaves" checked={keysOnly} onChange={setKeysOnly} />
          {schema === 'app' && <Switch label="Solo con deriva" checked={driftOnly} onChange={setDriftOnly} />}
          <Switch label="Enfocar la tabla elegida" checked={focus} onChange={setFocus} disabled={!selected} />
          {focus && selected && (
            <Select aria-label="Niveles de vecinas" selectSize="sm" value={levels} onChange={(e) => setLevels(Number(e.target.value))}>
              <option value={1}>1 nivel</option>
              <option value={2}>2 niveles</option>
            </Select>
          )}
        </div>
      </div>

      {!!error && <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudo leer la base de datos'} />}
      {loading && !data && <LoadingBlock />}

      {data && schema === 'app' && data.drift.length > 0 && (
        <Alert tone="warning" icon={TriangleAlert} title={`Deriva entre la base y el modelo: ${data.drift.length} ${data.drift.length === 1 ? 'diferencia' : 'diferencias'}`}
          action={<Button size="sm" onClick={() => setShowDrift((v) => !v)} aria-expanded={showDrift}>{showDrift ? 'Ocultar detalle' : 'Ver detalle'}</Button>}>
          {driftCounts.map(([k, n]) => `${DRIFT_LABELS[k]}: ${n}`).join(' · ')}. Las columnas de campos archivados se conservan a propósito (no se borran datos).
        </Alert>
      )}
      {data && showDrift && (
        <div className="table-wrap">
          <div className="table-scroll" tabIndex={0}>
            <table className="table">
              <caption className="sr-only">Deriva entre la base de datos y el modelo</caption>
              <thead><tr><th scope="col">Tipo</th><th scope="col">Tabla</th><th scope="col">Columna</th><th scope="col">Detalle</th></tr></thead>
              <tbody>
                {data.drift.map((d, i) => (
                  <tr key={i}>
                    <td><Badge tone={d.kind === 'typeMismatch' || d.kind.startsWith('missing') ? 'danger' : 'warning'}>{DRIFT_LABELS[d.kind]}</Badge></td>
                    <td className="mono">{d.table}</td>
                    <td className="mono">{d.column ?? '—'}</td>
                    <td>{d.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {data && (
        <div className="ex-toolbar">
          <div className="ex-segmented" role="radiogroup" aria-label="Presentación">
            <button type="button" role="radio" aria-checked={mode === 'diagram'} className="ex-seg" onClick={() => setMode('diagram')}><Waypoints size={15} aria-hidden />Diagrama</button>
            <button type="button" role="radio" aria-checked={mode === 'table'} className="ex-seg" onClick={() => setMode('table')}><Table2 size={15} aria-hidden />Tabla</button>
          </div>
          <span className="subtle" aria-live="polite">{tables.length} {tables.length === 1 ? 'tabla' : 'tablas'}{data.external.length ? ` · ${data.external.length} ${data.external.length === 1 ? 'externa' : 'externas'}` : ''}</span>
          <span className="grow" />
          <Button size="sm" icon={ImageDown} onClick={() => exportImage('svg')} disabled={mode !== 'diagram' || tables.length === 0}>SVG</Button>
          <Button size="sm" icon={ImageDown} onClick={() => exportImage('png')} disabled={mode !== 'diagram' || tables.length === 0}>PNG</Button>
          <Button size="sm" icon={FileCode2} onClick={exportDdl} disabled={tables.length === 0}>DDL</Button>
        </div>
      )}

      {data && tables.length === 0 && (
        <div className="card"><EmptyState icon={Database} title="Ninguna tabla coincide" description="Cambia el módulo, el texto a buscar o quita «Solo con deriva»." /></div>
      )}

      {data && tables.length > 0 && (
        <div className={`ex-split${current ? ' with-panel' : ''}`}>
          <div className="ex-main">
            {mode === 'diagram' ? (
              <>
                <BoxGraph ref={graph} nodes={nodes} edges={edges} selected={selected} onSelect={select}
                  ariaLabel={`Diagrama entidad-relación del esquema ${schema}: ${tables.length} tablas`} />
                {schema === 'app' && <AppLegend apps={data.apps} colors={colors} />}
                <p className="subtle ex-note"><KeyRound size={13} aria-hidden /> llave primaria · <Link2 size={13} aria-hidden /> llave foránea · NN = NOT NULL · pata de gallo: ‖ uno, ○| cero o uno, ○&lt; cero o muchos</p>
              </>
            ) : (
              <TableList tables={tables} selected={selected} onSelect={select} />
            )}
          </div>
          {current && (
            <aside className="ex-panel card card-pad" aria-label={`Detalle de ${current.schema}.${current.name}`}>
              <TableDetail t={current} onFocus={() => setFocus(true)} onOpen={onOpen} onClose={() => onEntity(undefined)} />
            </aside>
          )}
        </div>
      )}
    </div>
  );
}

function TableList({ tables, selected, onSelect }: { tables: DbTable[]; selected: string | null; onSelect: (id: string) => void }) {
  return (
    <div className="table-wrap">
      <div className="table-scroll" tabIndex={0}>
        <table className="table">
          <caption className="sr-only">Tablas de la base de datos (alternativa al diagrama)</caption>
          <thead>
            <tr><th scope="col">Tabla</th><th scope="col">Catálogo</th><th scope="col">Módulo</th><th scope="col" className="td-num">Columnas</th><th scope="col">Llaves foráneas</th><th scope="col">Deriva</th></tr>
          </thead>
          <tbody>
            {tables.map((t) => {
              const id = tableId(t.schema, t.name);
              return (
                <tr key={id} aria-selected={selected === id}>
                  <td><button type="button" className="link-btn mono" aria-pressed={selected === id} onClick={() => onSelect(id)}>{id}</button></td>
                  <td>{t.entity?.label ?? '—'}{t.entity?.isArchived && <> <Badge tone="warning">archivado</Badge></>}</td>
                  <td>{t.entity?.appLabel ?? '—'}</td>
                  <td className="td-num">{t.columns.length}</td>
                  <td className="mono subtle">{t.foreignKeys.map((f) => `${f.columns.join(', ')} → ${f.refSchema}.${f.refTable}`).join('; ') || '—'}</td>
                  <td>{t.driftCount ? <Badge tone="warning">{t.driftCount}</Badge> : '—'}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function TableDetail({ t, onFocus, onOpen, onClose }: { t: DbTable; onFocus: () => void; onOpen: (v: ExplorerView, e?: string) => void; onClose: () => void }) {
  const fks = new Map(t.foreignKeys.flatMap((f) => f.columns.map((c) => [c.toLowerCase(), f] as const)));
  return (
    <div className="stack ex-detail">
      <div className="row">
        <h2 className="ex-h2 mono grow">{t.schema}.{t.name}</h2>
        <Button size="sm" variant="ghost" onClick={onClose}>Cerrar</Button>
      </div>
      {t.entity ? (
        <p>Catálogo <strong>{t.entity.label}</strong> · módulo {t.entity.appLabel}{t.entity.isChild ? ' · partida' : ''}{t.entity.isArchived ? ' · archivado' : ''}</p>
      ) : t.schema === 'app' ? <Alert tone="warning" title="Sin catálogo: la tabla no corresponde a ningún metadato." /> : <p className="subtle">Tabla del sistema (esquema meta).</p>}
      <div className="ex-links">
        <Button size="sm" icon={Crosshair} onClick={onFocus}>Enfocar</Button>
        {t.entity && <Button size="sm" icon={Waypoints} onClick={() => onOpen('relaciones', t.entity!.name)}>Ver relaciones</Button>}
      </div>
      {t.entity && <EntityLinks entity={t.entity.name} label={t.entity.label} isChild={t.entity.isChild} />}

      <h3 className="ex-h3">Columnas</h3>
      <div className="table-scroll" tabIndex={0}>
        <table className="table table-compact">
          <caption className="sr-only">Columnas de {t.name}</caption>
          <thead><tr><th scope="col">Columna</th><th scope="col">Tipo</th><th scope="col">Campo</th></tr></thead>
          <tbody>
            {t.columns.map((c) => {
              const fk = fks.get(c.name.toLowerCase());
              const pk = t.primaryKey?.columns.some((x) => x.toLowerCase() === c.name.toLowerCase());
              return (
                <tr key={c.name}>
                  <td className="mono">
                    {pk && <KeyRound size={12} aria-label="llave primaria" />} {fk && <Link2 size={12} aria-label={`llave foránea a ${fk.refTable}`} />} {c.name}
                    {c.identity && <span className="subtle"> identidad</span>}
                  </td>
                  <td className="mono">
                    {c.type} <span className="subtle">{c.nullable ? 'NULL' : 'NOT NULL'}</span>
                    {c.status === 'mismatch' && <div className="field-error">(el motor generaría {c.expectedType})</div>}
                    {(c.default || c.computed) && <div className="subtle ex-expr">{c.computed ? `AS ${c.computed}` : `DEFAULT ${c.default!.definition}`}</div>}
                  </td>
                  <td>
                    {c.field ? c.field.label : c.system ? <span className="subtle">Sistema</span> : '—'}
                    {c.status !== 'ok' && c.status !== 'system' && <> <Badge tone={c.status === 'mismatch' ? 'danger' : 'warning'}>{STATUS_LABELS[c.status]}</Badge></>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {t.primaryKey && <p><span className="subtle">Llave primaria</span> <span className="mono">{t.primaryKey.name} ({t.primaryKey.columns.join(', ')})</span></p>}

      <h3 className="ex-h3">Llaves foráneas</h3>
      {t.foreignKeys.length === 0 ? <p className="subtle">Ninguna.</p> : (
        <ul className="ex-list">
          {t.foreignKeys.map((f) => (
            <li key={f.name}>
              <span className="mono">{f.columns.join(', ')} → {f.refSchema}.{f.refTable} ({f.refColumns.join(', ')})</span>
              <div className="subtle">{cardinality(t, f)} <span className="mono">{f.name}</span>{f.onDelete !== 'NO_ACTION' ? ` · ON DELETE ${f.onDelete.replace('_', ' ')}` : ''}</div>
            </li>
          ))}
        </ul>
      )}

      <h3 className="ex-h3">Índices</h3>
      {t.indexes.length === 0 ? <p className="subtle">Ninguno.</p> : (
        <ul className="ex-list">
          {t.indexes.map((i) => (
            <li key={i.name}>
              <span className="mono">{i.name}</span>{' '}
              {i.primaryKey ? <Badge tone="accent">Primaria</Badge> : i.unique ? <Badge tone="accent">Único</Badge> : <Badge>Índice</Badge>}
              <div className="mono subtle">({i.columns.join(', ')}){i.included.length ? ` INCLUDE (${i.included.join(', ')})` : ''}{i.filter ? ` WHERE ${i.filter}` : ''}</div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
