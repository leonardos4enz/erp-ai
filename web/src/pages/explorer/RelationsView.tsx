import { useMemo, useState } from 'react';
import { Search, Table2, Waypoints } from 'lucide-react';
import { ApiError } from '../../api/client';
import { explorerApi, type RelGraph, type RelNode } from '../../api/explorer';
import { BoxGraph, type GraphEdge, type GraphNode } from '../../components/BoxGraph';
import { Alert, Badge, Button, EmptyState, Input, LoadingBlock, Select, Switch } from '../../components/ui';
import { neighborhood } from '../../lib/graphLayout';
import { useAsync, useDebounced } from '../../lib/hooks';
import { AppLegend, EntityLinks, FieldBadges, appColors, colorOf, typeLabel, type ExplorerView } from './common';

const USER = '$user';
const KIND_LABELS = { reference: 'referencia', lines: 'partidas', parent: 'documento', user: 'usuario' } as const;

interface Props { entity?: string; onEntity: (e?: string) => void; onOpen: (v: ExplorerView, e?: string) => void }

export function RelationsView({ entity, onEntity, onOpen }: Props) {
  const [app, setApp] = useState('');
  const [text, setText] = useState('');
  const q = useDebounced(text.trim(), 300);
  const [mode, setMode] = useState<'diagram' | 'list'>('diagram');
  const [allFields, setAllFields] = useState(false);
  const [users, setUsers] = useState(false);
  const [focus, setFocus] = useState(true);
  const [levels, setLevels] = useState(1);
  const { data, loading, error } = useAsync<RelGraph>((s) => explorerApi.relations({ app: app || undefined, q: q || undefined }, s), [app, q]);
  const colors = useMemo(() => appColors(data?.apps ?? []), [data]);
  const byName = useMemo(() => new Map((data?.nodes ?? []).map((n) => [n.name.toLowerCase(), n])), [data]);
  const current = entity ? byName.get(entity.toLowerCase()) : undefined;

  const { nodes, edges } = useMemo(() => {
    if (!data) return { nodes: [] as GraphNode[], edges: [] as GraphEdge[] };
    const known = new Set(data.nodes.map((n) => n.name));
    // La partida → documento duplica documento → partidas: se dibuja una sola vez.
    let rel = data.edges.filter((e) => e.kind !== 'parent' && (e.kind !== 'user' || users) && (e.to === USER || known.has(e.to)));
    let shown = data.nodes;
    if (focus && current) {
      const near = neighborhood(current.name, rel.map((e) => ({ from: e.from, to: e.to })), levels);
      shown = shown.filter((n) => near.has(n.name));
      rel = rel.filter((e) => near.has(e.from) && (near.has(e.to) || e.to === USER));
    }
    const names = new Set(shown.map((n) => n.name));
    rel = rel.filter((e) => names.has(e.from) && (names.has(e.to) || e.to === USER));
    const nodes: GraphNode[] = shown.map((n) => {
      const expanded = allFields || n.name === current?.name;
      const fields = expanded ? n.fields : n.fields.filter((f) => rel.some((e) => e.from === n.name && e.field === f.name));
      return {
        id: n.name, title: n.labelPlural, subtitle: n.isChild ? 'partida' : n.name, color: colorOf(colors, n.app),
        label: `Catálogo ${n.labelPlural} (${n.name}) del módulo ${data.apps.find((a) => a.name === n.app)?.label ?? n.app}${n.isChild ? ', partida' : ''}${n.hasWorkflow ? ', con proceso' : ''}, ${n.fields.length} campos`,
        rows: fields.map((f) => ({ key: f.name, text: `${f.label}${f.required ? ' *' : ''}`, detail: f.target ? `→ ${byName.get(f.target.toLowerCase())?.label ?? f.target}` : typeLabel(f.type, f.resultType), kind: f.target ? 'fk' : undefined })),
      };
    });
    if (users && rel.some((e) => e.to === USER))
      nodes.push({ id: USER, title: 'Usuarios', subtitle: 'sistema', rows: [], tone: 'external', label: 'Usuarios del sistema' });
    const edges: GraphEdge[] = rel.map((e) => ({
      id: `${e.from}.${e.field}`, from: e.from, fromRow: e.field, to: e.to, start: 'none', end: 'arrow',
      dashed: e.kind === 'lines', tone: e.kind === 'user' ? 'muted' : undefined,
    }));
    return { nodes, edges };
  }, [data, focus, current, levels, allFields, users, colors, byName]);

  return (
    <div className="stack">
      <div className="ex-filters" role="search" aria-label="Filtros de relaciones">
        <Select label="Módulo" selectSize="sm" value={app} onChange={(e) => setApp(e.target.value)}>
          <option value="">Todos los módulos</option>
          {data?.apps.map((a) => <option key={a.name} value={a.name}>{a.label}</option>)}
        </Select>
        <Input label="Buscar catálogo o campo" inputSize="sm" icon={Search} value={text} maxLength={100} onChange={(e) => setText(e.target.value)} />
        <Select label="Catálogo en foco" selectSize="sm" value={current?.name ?? ''} onChange={(e) => onEntity(e.target.value || undefined)}>
          <option value="">Ninguno</option>
          {data?.nodes.map((n) => <option key={n.name} value={n.name}>{n.labelPlural}</option>)}
        </Select>
        <div className="ex-switches">
          <Switch label="Solo vecinos del foco" checked={focus} onChange={setFocus} disabled={!current} />
          {focus && current && (
            <Select aria-label="Niveles de vecinos" selectSize="sm" value={levels} onChange={(e) => setLevels(Number(e.target.value))}>
              <option value={1}>1 nivel</option>
              <option value={2}>2 niveles</option>
            </Select>
          )}
          <Switch label="Todos los campos" checked={allFields} onChange={setAllFields} />
          <Switch label="Incluir usuarios" checked={users} onChange={setUsers} />
        </div>
      </div>
      {!!error && <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron leer las relaciones'} />}
      {loading && !data && <LoadingBlock />}
      {data && (
        <div className="ex-toolbar">
          <div className="ex-segmented" role="radiogroup" aria-label="Presentación">
            <button type="button" role="radio" aria-checked={mode === 'diagram'} className="ex-seg" onClick={() => setMode('diagram')}><Waypoints size={15} aria-hidden />Diagrama</button>
            <button type="button" role="radio" aria-checked={mode === 'list'} className="ex-seg" onClick={() => setMode('list')}><Table2 size={15} aria-hidden />Lista</button>
          </div>
          <span className="subtle" aria-live="polite">{nodes.filter((n) => n.id !== USER).length} catálogos · {edges.length} relaciones</span>
        </div>
      )}
      {data && nodes.length === 0 && <div className="card"><EmptyState icon={Waypoints} title="Ningún catálogo coincide" description="Cambia el módulo o el texto a buscar." /></div>}
      {data && nodes.length > 0 && (
        <div className={`ex-split${current ? ' with-panel' : ''}`}>
          <div className="ex-main">
            {mode === 'diagram' ? (
              <>
                <BoxGraph nodes={nodes} edges={edges} selected={current?.name ?? null} onSelect={(id) => id !== USER && onEntity(id)} nodeWidth={240}
                  ariaLabel={`Relaciones entre ${nodes.length} catálogos`} />
                <AppLegend apps={data.apps} colors={colors} />
                <p className="subtle ex-note">Flecha continua: referencia · discontinua: partidas del documento · * obligatorio</p>
              </>
            ) : <RelationsList data={data} selected={current?.name} onSelect={onEntity} />}
          </div>
          {current && (
            <aside className="ex-panel card card-pad" aria-label={`Detalle de ${current.labelPlural}`}>
              <NodeDetail n={current} data={data} onOpen={onOpen} onClose={() => onEntity(undefined)} />
            </aside>
          )}
        </div>
      )}
    </div>
  );
}

function RelationsList({ data, selected, onSelect }: { data: RelGraph; selected?: string; onSelect: (e: string) => void }) {
  const label = (n: string) => data.nodes.find((x) => x.name === n)?.labelPlural ?? (n === USER ? 'Usuarios' : n);
  return (
    <div className="table-wrap">
      <div className="table-scroll" tabIndex={0}>
        <table className="table">
          <caption className="sr-only">Relaciones entre catálogos (alternativa al diagrama)</caption>
          <thead><tr><th scope="col">Catálogo</th><th scope="col">Módulo</th><th scope="col">Refiere a</th><th scope="col">Referido por</th></tr></thead>
          <tbody>
            {data.nodes.map((n) => (
              <tr key={n.name} aria-selected={selected === n.name}>
                <td><button type="button" className="link-btn" aria-pressed={selected === n.name} onClick={() => onSelect(n.name)}>{n.labelPlural}</button>{n.isChild && <> <Badge>partida</Badge></>}</td>
                <td>{data.apps.find((a) => a.name === n.app)?.label ?? n.app}</td>
                <td>{data.edges.filter((e) => e.from === n.name && e.kind !== 'parent').map((e) => `${label(e.to)} (${e.field}, ${KIND_LABELS[e.kind]})`).join('; ') || '—'}</td>
                <td>{data.edges.filter((e) => e.to === n.name && e.kind !== 'parent').map((e) => `${label(e.from)} (${e.field})`).join('; ') || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function NodeDetail({ n, data, onOpen, onClose }: { n: RelNode; data: RelGraph; onOpen: (v: ExplorerView, e?: string) => void; onClose: () => void }) {
  return (
    <div className="stack ex-detail">
      <div className="row">
        <h2 className="ex-h2 grow">{n.labelPlural}</h2>
        <Button size="sm" variant="ghost" onClick={onClose}>Cerrar</Button>
      </div>
      <p className="subtle"><span className="mono">{n.name}</span> · {data.apps.find((a) => a.name === n.app)?.label ?? n.app}{n.perCompany ? ' · por empresa' : ' · compartido entre empresas'}{n.hasWorkflow ? ' · con proceso' : ''}</p>
      <div className="ex-links">
        <Button size="sm" onClick={() => onOpen('bd', n.name)}>Ver en la base de datos</Button>
        <Button size="sm" onClick={() => onOpen('usos', n.name)}>¿Dónde se usa?</Button>
      </div>
      <EntityLinks entity={n.name} label={n.labelPlural} isChild={n.isChild} />
      <h3 className="ex-h3">Campos</h3>
      <ul className="ex-list">
        {n.fields.map((f) => (
          <li key={f.name}>
            <strong>{f.label}</strong> <span className="mono subtle">{f.name}</span>
            <div className="subtle">{typeLabel(f.type, f.resultType)}{f.target ? ` → ${data.nodes.find((x) => x.name === f.target)?.labelPlural ?? f.target}` : ''}</div>
            <FieldBadges f={f} />
          </li>
        ))}
      </ul>
    </div>
  );
}
