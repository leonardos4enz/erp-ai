import { useEffect, useId, useRef, useState, type DragEvent, type ReactNode } from 'react';
import {
  ArrowDown, ArrowUp, ChevronDown, ChevronRight, ExternalLink, Eye, FileText, GripVertical, Heading, Image as ImageIcon, Minus, PenLine,
  Plus, Rows3, Scissors, Sigma, Table2, Text as TextIcon, Trash2, type LucideIcon,
} from 'lucide-react';
import { ApiError } from '../../../api/client';
import { imagesApi, modelApi } from '../../../api/endpoints';
import type { PrintBlock, PrintBlockType, PrintFormat, PrintTemplate, PrintValue, StoredImage } from '../../../api/types';
import { ExprInput, type ExprScope } from '../../../components/ExprInput';
import { ReferenceSelect } from '../../../components/ReferenceSelect';
import { TagInput } from '../../../components/TagInput';
import { Alert, Badge, Button, Field, Input, Select, Switch, Textarea } from '../../../components/ui';
import { clean, fieldOptions, ItemList, MultiPick, roleOptions, scopeOf, selfScope, uniqueName, type SectionCtx } from './common';

/* ------------------------------------------------------------------------- */
/* Catálogo de bloques                                                         */

const BLOCKS: { type: PrintBlockType; label: string; icon: LucideIcon; hint: string }[] = [
  { type: 'header', label: 'Encabezado', icon: Heading, hint: 'Logo, datos de la empresa, título y subtítulo.' },
  { type: 'fields', label: 'Campos con etiqueta', icon: Rows3, hint: 'Valores del registro en 1 a 3 columnas.' },
  { type: 'table', label: 'Tabla de partidas', icon: Table2, hint: 'Una fila por partida; columnas elegibles.' },
  { type: 'totals', label: 'Totales', icon: Sigma, hint: 'Renglones alineados a la derecha.' },
  { type: 'text', label: 'Texto libre', icon: TextIcon, hint: 'Párrafo con campos insertados.' },
  { type: 'image', label: 'Imagen', icon: ImageIcon, hint: 'Una imagen subida en Configuración › Modelo › Imágenes.' },
  { type: 'separator', label: 'Separador', icon: Minus, hint: 'Línea horizontal.' },
  { type: 'signatures', label: 'Firmas', icon: PenLine, hint: 'Hasta 4 líneas de firma.' },
  { type: 'pageBreak', label: 'Salto de página', icon: Scissors, hint: 'Lo que sigue empieza en una hoja nueva.' },
  { type: 'footer', label: 'Pie de página', icon: FileText, hint: 'Texto y número de página en cada hoja (uno por plantilla).' },
];
const blockInfo = (t: PrintBlockType) => BLOCKS.find((b) => b.type === t) ?? { type: t, label: t, icon: FileText, hint: '' };

const FORMATS: { value: PrintFormat | ''; label: string }[] = [
  { value: '', label: 'Automático' }, { value: 'text', label: 'Texto' }, { value: 'number', label: 'Número' }, { value: 'integer', label: 'Entero' },
  { value: 'money', label: 'Moneda' }, { value: 'percent', label: 'Porcentaje' }, { value: 'date', label: 'Fecha corta' },
  { value: 'dateLong', label: 'Fecha larga' }, { value: 'datetime', label: 'Fecha y hora' },
];

function newBlock(type: PrintBlockType, ctx: SectionCtx): PrintBlock {
  const display = ctx.fields.find((f) => f.type === 'autonumber')?.name;
  switch (type) {
    case 'header': return { type, logo: true, company: true, title: ctx.entity, subtitle: display ? `{{ ${display} }}` : undefined };
    case 'fields': return { type, perRow: 2, items: [] };
    case 'table': return { type, lines: ctx.fields.find((f) => f.type === 'lines')?.name ?? '', columns: [] };
    case 'totals': return { type, items: [] };
    case 'text': return { type, text: '' };
    case 'image': return { type, file: '', width: 40 };
    case 'signatures': return { type, signatures: [{ label: 'Autorizó' }] };
    case 'footer': return { type, pageNumbers: true };
    default: return { type };
  }
}

/* ------------------------------------------------------------------------- */
/* Texto con campos insertados                                                 */

/** Un texto con `{{ expresión }}`: se escribe libre y el campo se arma con el editor de expresiones (026) y se inserta. */
function TemplateText({ label, value, onChange, scope, ctx, multiline, hint }: {
  label: string; value: string; onChange: (v: string) => void; scope: ExprScope | null; ctx: SectionCtx; multiline?: boolean; hint?: ReactNode;
}) {
  const ref = useRef<HTMLTextAreaElement & HTMLInputElement>(null);
  const [expr, setExpr] = useState('');
  const [open, setOpen] = useState(false);
  const id = useId();
  function insert() {
    const e = expr.trim();
    if (!e) return;
    const el = ref.current;
    const at = el?.selectionStart ?? value.length;
    const end = el?.selectionEnd ?? at;
    const token = `{{ ${e} }}`;
    onChange(value.slice(0, at) + token + value.slice(end));
    setExpr('');
    setOpen(false);
    requestAnimationFrame(() => { el?.focus(); el?.setSelectionRange(at + token.length, at + token.length); });
  }
  const common = { label, value, onChange: (e: { target: { value: string } }) => onChange(e.target.value), hint: hint ?? 'Escribe el texto; los campos van entre {{ y }}, p. ej. {{ Folio }}.' };
  return (
    <div className="stack span-all" style={{ gap: 'var(--space-2)' }}>
      {multiline ? <Textarea ref={ref} rows={3} maxLength={4000} {...common} /> : <Input ref={ref} maxLength={300} {...common} />}
      <div>
        <Button size="sm" variant="ghost" icon={open ? ChevronDown : Plus} aria-expanded={open} aria-controls={`${id}-ins`} onClick={() => setOpen((o) => !o)}>
          Insertar campo en «{label}»
        </Button>
        {open && (
          <div id={`${id}-ins`} className="row" style={{ alignItems: 'flex-end', gap: 'var(--space-2)', flexWrap: 'wrap', marginTop: 'var(--space-2)' }}>
            <ExprInput className="grow" label="Campo o expresión a insertar" value={expr} onChange={setExpr} scope={scope} entities={ctx.entities}
              hint="Con autocompletado: un campo, una ruta (Proveedor.RFC) o una fórmula." />
            <Button size="sm" icon={Plus} disabled={!expr.trim()} onClick={insert}>Insertar</Button>
          </div>
        )}
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------------- */
/* Lista de valores (campos con etiqueta, columnas, totales)                   */

function ValuesEditor({ legend, values, onChange, scope, ctx, mode, max }: {
  legend: string; values: PrintValue[]; onChange: (v: PrintValue[]) => void; scope: ExprScope | null; ctx: SectionCtx;
  mode: 'fields' | 'table' | 'totals'; max: number;
}) {
  const noun = mode === 'table' ? 'columna' : mode === 'totals' ? 'renglón' : 'campo';
  const patch = (i: number, p: Partial<PrintValue>) => onChange(values.map((v, k) => (k === i ? clean({ ...v, ...p }) : v)));
  const move = (i: number, d: -1 | 1) => { const n = values.slice(); [n[i], n[i + d]] = [n[i + d], n[i]]; onChange(n); };
  const fields = scope?.fields ?? [];
  const add = (name?: string) => {
    const f = fields.find((x) => x.name === name);
    onChange([...values, clean({ label: f?.label ?? '', value: f?.name ?? '' }) as PrintValue]);
  };
  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">{legend}</legend>
      {values.length === 0 && <p className="field-hint">Sin {noun}s todavía: agrega el primero.</p>}
      <ol className="action-list">
        {values.map((v, i) => {
          const name = v.label || v.value || `${noun} ${i + 1}`;
          const numeric = v.format === 'number' || v.format === 'money' || v.format === 'percent';
          return (
            <li key={i} className="action">
              <div className="value-row">
                <Input label="Etiqueta" required={mode !== 'table'} value={v.label} maxLength={100} onChange={(e) => patch(i, { label: e.target.value })} />
                <ExprInput label="Valor" required value={v.value} onChange={(x) => patch(i, { value: x })} scope={scope} entities={ctx.entities}
                  hint={mode === 'table' ? 'En el ámbito de la partida (el documento es el campo padre).' : 'Un campo, una ruta o una fórmula.'} />
                <Select label="Formato" value={v.format ?? ''} onChange={(e) => patch(i, { format: (e.target.value || undefined) as PrintFormat | undefined })}>
                  {FORMATS.map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
                </Select>
                <div className="row" style={{ gap: 2, alignSelf: 'end' }}>
                  <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} aria-label={`Subir ${noun} ${name}`} disabled={i === 0} onClick={() => move(i, -1)} />
                  <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} aria-label={`Bajar ${noun} ${name}`} disabled={i === values.length - 1} onClick={() => move(i, 1)} />
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar ${noun} ${name}`} onClick={() => onChange(values.filter((_, k) => k !== i))} />
                </div>
              </div>
              {(numeric || v.format === 'money' || mode !== 'fields') && (
                <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)', alignItems: 'flex-end' }}>
                  {numeric && (
                    <Input label="Decimales" type="number" min={0} max={6} value={v.decimals ?? ''} style={{ width: 90 }}
                      onChange={(e) => patch(i, { decimals: e.target.value === '' ? undefined : Number(e.target.value) })} />
                  )}
                  {v.format === 'money' && (
                    <ExprInput className="grow" label="Clave de moneda (opcional)" value={v.currency ?? ''} onChange={(x) => patch(i, { currency: x || undefined })}
                      scope={scope} entities={ctx.entities} hint="P. ej. Moneda.Clave. Vacío: la moneda base de la empresa." />
                  )}
                  {mode === 'table' && (
                    <>
                      <Input label="Ancho (1 a 10)" type="number" min={1} max={10} value={v.width ?? ''} style={{ width: 110 }}
                        onChange={(e) => patch(i, { width: e.target.value === '' ? undefined : Number(e.target.value) })} />
                      <Select label="Alineación" value={v.align ?? ''} onChange={(e) => patch(i, { align: (e.target.value || undefined) as PrintValue['align'] })}>
                        <option value="">Automática</option><option value="left">Izquierda</option><option value="center">Centro</option><option value="right">Derecha</option>
                      </Select>
                    </>
                  )}
                  {mode === 'totals' && <Switch checked={!!v.bold} onChange={(b) => patch(i, { bold: b || undefined })} label="Resaltado (total)" />}
                </div>
              )}
            </li>
          );
        })}
      </ol>
      {values.length < max && (
        <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
          <Select aria-label={`${legend}: agregar ${noun} con un campo`} value="" onChange={(e) => e.target.value && add(e.target.value)}>
            {fieldOptions(fields.filter((f) => f.type !== 'lines' && f.type !== 'parent'), `Agregar ${noun} con un campo…`)}
          </Select>
          <Button size="sm" icon={Plus} onClick={() => add()}>Agregar {noun} con una fórmula</Button>
        </div>
      )}
    </fieldset>
  );
}

/* ------------------------------------------------------------------------- */
/* Bloques: lista con teclado y arrastrar y soltar                             */

function BlockBody({ b, patch, ctx, images }: { b: PrintBlock; patch: (p: Partial<PrintBlock>) => void; ctx: SectionCtx; images: StoredImage[] | null }) {
  const self = selfScope(ctx);
  const linesFields = ctx.fields.filter((f) => f.type === 'lines');
  const child = scopeOf(ctx, linesFields.find((f) => f.name === b.lines)?.childEntity);
  const alignSelect = (
    <Select label="Alineación" value={b.align ?? ''} onChange={(e) => patch({ align: (e.target.value || undefined) as PrintBlock['align'] })}>
      <option value="">Izquierda</option><option value="center">Centro</option><option value="right">Derecha</option>
    </Select>
  );
  return (
    <div className="item-grid">
      {b.type === 'header' && (
        <>
          <Switch checked={!!b.logo} onChange={(v) => patch({ logo: v || undefined })} label="Logo de la empresa" />
          <Switch checked={!!b.company} onChange={(v) => patch({ company: v || undefined })} label="Nombre, RFC y domicilio de la empresa" />
          <TemplateText label="Título" value={b.title ?? ''} onChange={(v) => patch({ title: v || undefined })} scope={self} ctx={ctx} />
          <TemplateText label="Subtítulo" value={b.subtitle ?? ''} onChange={(v) => patch({ subtitle: v || undefined })} scope={self} ctx={ctx} />
        </>
      )}
      {b.type === 'text' && (
        <>
          <TemplateText multiline label="Texto" value={b.text ?? ''} onChange={(v) => patch({ text: v })} scope={self} ctx={ctx} />
          <Input label="Tamaño de letra (puntos)" type="number" min={6} max={28} value={b.size ?? ''} onChange={(e) => patch({ size: e.target.value === '' ? undefined : Number(e.target.value) })} hint="Vacío: 9." />
          {alignSelect}
          <Switch checked={!!b.bold} onChange={(v) => patch({ bold: v || undefined })} label="Negritas" />
          <Switch checked={!!b.italic} onChange={(v) => patch({ italic: v || undefined })} label="Cursivas" />
        </>
      )}
      {b.type === 'fields' && (
        <>
          <Select label="Campos por renglón" value={String(b.perRow ?? 2)} onChange={(e) => patch({ perRow: Number(e.target.value) })}>
            <option value="1">1</option><option value="2">2</option><option value="3">3</option>
          </Select>
          <ValuesEditor legend="Campos" values={b.items ?? []} onChange={(items) => patch({ items })} scope={self} ctx={ctx} mode="fields" max={30} />
        </>
      )}
      {b.type === 'totals' && <ValuesEditor legend="Renglones" values={b.items ?? []} onChange={(items) => patch({ items })} scope={self} ctx={ctx} mode="totals" max={12} />}
      {b.type === 'table' && (
        <>
          <Select label="Partidas" required value={b.lines ?? ''} onChange={(e) => patch({ lines: e.target.value })}
            hint={linesFields.length ? 'Una fila por partida del documento.' : 'Este catálogo no tiene campos de partidas.'}>
            {fieldOptions(linesFields, 'Elige las partidas…')}
          </Select>
          {b.lines && <ValuesEditor legend="Columnas" values={b.columns ?? []} onChange={(columns) => patch({ columns })} scope={child} ctx={ctx} mode="table" max={12} />}
        </>
      )}
      {b.type === 'image' && (
        <>
          <Select label="Imagen" required value={b.file ?? ''} onChange={(e) => patch({ file: e.target.value })}
            hint={images === null ? 'No se pudieron leer las imágenes.' : images.length ? 'Las imágenes son de cada entorno; se suben en Configuración › Modelo › Imágenes.' : 'Aún no hay imágenes: súbelas en Configuración › Modelo › Imágenes.'}>
            <option value="">Elige una imagen…</option>
            {(images ?? []).map((i) => <option key={i.key} value={i.key}>{i.key} ({i.width} × {i.height} px)</option>)}
            {b.file && !(images ?? []).some((i) => i.key.toLowerCase() === b.file!.toLowerCase()) && <option value={b.file}>{b.file} (no existe en este entorno)</option>}
          </Select>
          <Input label="Ancho (mm)" type="number" min={5} max={190} value={b.width ?? ''} onChange={(e) => patch({ width: e.target.value === '' ? undefined : Number(e.target.value) })} hint="Vacío: 40 mm." />
          {alignSelect}
        </>
      )}
      {b.type === 'signatures' && (
        <fieldset className="subgroup span-all">
          <legend className="field-label">Firmas</legend>
          <ol className="action-list">
            {(b.signatures ?? []).map((s, i) => (
              <li key={i} className="action">
                <div className="row" style={{ gap: 'var(--space-2)', alignItems: 'flex-end', flexWrap: 'wrap' }}>
                  <Input wrapClassName="grow" label="Etiqueta bajo la línea" required value={s.label} maxLength={100}
                    onChange={(e) => patch({ signatures: (b.signatures ?? []).map((x, k) => (k === i ? { ...x, label: e.target.value } : x)) })} />
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar la firma ${s.label || i + 1}`}
                    onClick={() => patch({ signatures: (b.signatures ?? []).filter((_, k) => k !== i) })} />
                </div>
                <TemplateText label={`Nombre sobre la línea de «${s.label || `firma ${i + 1}`}» (opcional)`} value={s.name ?? ''} scope={self} ctx={ctx}
                  onChange={(v) => patch({ signatures: (b.signatures ?? []).map((x, k) => (k === i ? clean({ ...x, name: v }) : x)) })} hint="P. ej. {{ AutorizadoPor }}. Vacío: la línea va en blanco." />
              </li>
            ))}
          </ol>
          {(b.signatures?.length ?? 0) < 4 && <div><Button size="sm" icon={Plus} onClick={() => patch({ signatures: [...(b.signatures ?? []), { label: '' }] })}>Agregar firma</Button></div>}
        </fieldset>
      )}
      {b.type === 'footer' && (
        <>
          <TemplateText label="Texto del pie" value={b.text ?? ''} onChange={(v) => patch({ text: v || undefined })} scope={self} ctx={ctx} />
          <Switch checked={!!b.pageNumbers} onChange={(v) => patch({ pageNumbers: v || undefined })} label="«Página N de M»" />
        </>
      )}
      {(b.type === 'separator' || b.type === 'pageBreak') && <p className="field-hint span-all">{blockInfo(b.type).hint} No tiene opciones.</p>}
      {b.type !== 'footer' && (
        <ExprInput condition className="span-all" label="Mostrar solo cuando (opcional)" value={b.visibleWhen ?? ''} onChange={(v) => patch({ visibleWhen: v || undefined })}
          scope={self} entities={ctx.entities} hint="Vacío: siempre. Si la condición lee un campo que el rol no ve, el bloque no se imprime." />
      )}
    </div>
  );
}

function blockSummary(b: PrintBlock): string {
  switch (b.type) {
    case 'header': return [b.logo && 'logo', b.company && 'empresa', b.title].filter(Boolean).join(' · ');
    case 'text': return (b.text ?? '').slice(0, 80);
    case 'fields': case 'totals': return (b.items ?? []).map((v) => v.label || v.value).join(', ');
    case 'table': return `${b.lines ?? ''}: ${(b.columns ?? []).map((v) => v.label || v.value).join(', ')}`;
    case 'image': return b.file ?? '';
    case 'signatures': return (b.signatures ?? []).map((s) => s.label).join(', ');
    case 'footer': return [b.text, b.pageNumbers && 'Página N de M'].filter(Boolean).join(' · ');
    default: return '';
  }
}

function BlocksEditor({ blocks, onChange, ctx, images }: { blocks: PrintBlock[]; onChange: (b: PrintBlock[]) => void; ctx: SectionCtx; images: StoredImage[] | null }) {
  const base = useId();
  const [open, setOpen] = useState<Set<number>>(() => new Set());
  const [drag, setDrag] = useState<number | null>(null);
  const [drop, setDrop] = useState<number | null>(null);
  const [announce, setAnnounce] = useState('');

  function reorder(from: number, to: number, focusLabel?: string) {
    if (from === to || to < 0 || to >= blocks.length) return;
    const next = blocks.slice();
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    onChange(next);
    setOpen((s) => {
      const arr = blocks.map((_, i) => s.has(i));
      const [o] = arr.splice(from, 1);
      arr.splice(to, 0, o);
      return new Set(arr.flatMap((v, i) => (v ? [i] : [])));
    });
    setAnnounce(`${blockInfo(moved.type).label} movido a la posición ${to + 1} de ${blocks.length}.`);
    // El foco sigue al bloque: el mismo botón en su nueva posición (o el de al lado si ya no se puede mover).
    if (focusLabel) requestAnimationFrame(() => {
      const li = document.getElementById(`${base}-${to}`);
      (li?.querySelector<HTMLButtonElement>(`button[data-move="${focusLabel}"]:not(:disabled)`) ?? li?.querySelector<HTMLButtonElement>('button[data-move]:not(:disabled)'))?.focus();
    });
  }
  function add(type: PrintBlockType) {
    // El pie va al final; los demás antes del pie.
    const footerAt = blocks.findIndex((b) => b.type === 'footer');
    const at = type === 'footer' || footerAt < 0 ? blocks.length : footerAt;
    const next = blocks.slice();
    next.splice(at, 0, newBlock(type, ctx));
    onChange(next);
    setOpen((s) => new Set([...[...s].map((i) => (i >= at ? i + 1 : i)), at]));
    requestAnimationFrame(() => document.getElementById(`${base}-${at}`)?.querySelector<HTMLElement>('.block-body input, .block-body select, .block-body textarea, .block-body button')?.focus());
  }
  const onDragOver = (e: DragEvent, i: number) => { if (drag === null) return; e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDrop(i); };
  const onDrop = (e: DragEvent, i: number) => { e.preventDefault(); if (drag !== null) reorder(drag, drag < i ? i - 1 : i); setDrag(null); setDrop(null); };
  const hasFooter = blocks.some((b) => b.type === 'footer');

  return (
    <fieldset className="subgroup span-all">
      <legend className="field-label">Bloques (de arriba abajo)</legend>
      <p className="field-hint">Reordénalos con Subir y Bajar o arrastrándolos por el asa. Cada bloque fluye después del anterior; el pie se repite en cada hoja.</p>
      <span className="sr-only" aria-live="polite">{announce}</span>
      {blocks.length === 0 && <p className="field-hint">Sin bloques: agrega el encabezado para empezar.</p>}
      <ol className="block-list">
        {blocks.map((b, i) => {
          const info = blockInfo(b.type);
          const Icon = info.icon;
          const expanded = open.has(i);
          const name = `${info.label} ${i + 1}`;
          return (
            <li key={i} id={`${base}-${i}`} className="block" data-dragging={drag === i} data-drop={drop === i && drag !== i}
              onDragOver={(e) => onDragOver(e, i)} onDrop={(e) => onDrop(e, i)} onDragLeave={() => setDrop((d) => (d === i ? null : d))}>
              <div className="block-head">
                <span className="block-grip" draggable aria-hidden title="Arrastra para mover"
                  onDragStart={(e) => { setDrag(i); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(i)); }}
                  onDragEnd={() => { setDrag(null); setDrop(null); }}>
                  <GripVertical size={16} />
                </span>
                <button type="button" className="item-toggle" aria-expanded={expanded} aria-controls={`${base}-${i}-body`}
                  onClick={() => setOpen((s) => { const n = new Set(s); if (n.has(i)) n.delete(i); else n.add(i); return n; })}>
                  {expanded ? <ChevronDown size={16} aria-hidden /> : <ChevronRight size={16} aria-hidden />}
                  <Icon size={15} aria-hidden />
                  <span className="item-title">{info.label}</span>
                  {!expanded && <span className="item-name">{blockSummary(b)}</span>}
                </button>
                {b.visibleWhen && <Badge>Condicional</Badge>}
                <div className="item-tools">
                  <Button variant="ghost" size="sm" iconOnly icon={ArrowUp} data-move="up" aria-label={`Subir el bloque ${name}`} disabled={i === 0} onClick={() => reorder(i, i - 1, 'up')} />
                  <Button variant="ghost" size="sm" iconOnly icon={ArrowDown} data-move="down" aria-label={`Bajar el bloque ${name}`} disabled={i === blocks.length - 1} onClick={() => reorder(i, i + 1, 'down')} />
                  <Button variant="ghost" size="sm" iconOnly icon={Trash2} aria-label={`Quitar el bloque ${name}`} onClick={() => {
                    onChange(blocks.filter((_, k) => k !== i));
                    setOpen((s) => new Set([...s].filter((x) => x !== i).map((x) => (x > i ? x - 1 : x))));
                    setAnnounce(`${info.label} quitado.`);
                  }} />
                </div>
              </div>
              {expanded && (
                <div className="block-body" id={`${base}-${i}-body`}>
                  <BlockBody b={b} ctx={ctx} images={images} patch={(p) => onChange(blocks.map((x, k) => (k === i ? clean({ ...x, ...p }) : x)))} />
                </div>
              )}
            </li>
          );
        })}
      </ol>
      {blocks.length < 60 && (
        <Select aria-label="Agregar bloque" value="" onChange={(e) => e.target.value && add(e.target.value as PrintBlockType)}>
          <option value="">Agregar bloque…</option>
          {BLOCKS.filter((x) => x.type !== 'footer' || !hasFooter).map((x) => <option key={x.type} value={x.type}>{x.label} — {x.hint}</option>)}
        </Select>
      )}
    </fieldset>
  );
}

/* ------------------------------------------------------------------------- */
/* Vista previa con un registro real (H4 CA6)                                  */

function Preview({ template, ctx, saved }: { template: PrintTemplate; ctx: SectionCtx; saved: boolean }) {
  const [record, setRecord] = useState<{ id: number | null; label: string | null }>({ id: null, label: null });
  const [state, setState] = useState<{ busy: boolean; url?: string; warnings: string[]; errors: string[] }>({ busy: false, warnings: [], errors: [] });
  const [stale, setStale] = useState(false);
  const key = JSON.stringify(template);
  const last = useRef<string | null>(null);
  useEffect(() => { if (last.current !== null && last.current !== key) setStale(true); }, [key]);

  async function generate() {
    if (record.id == null) return;
    setState({ busy: true, warnings: [], errors: [] });
    try {
      const r = await modelApi.printPreview(ctx.entity, record.id, template);
      last.current = key;
      setStale(false);
      setState({ busy: false, url: `${modelApi.printPreviewUrl(r.token)}#view=FitH`, warnings: r.warnings, errors: [] });
    } catch (e) {
      const errors = e instanceof ApiError ? (e.fieldErrors.template ?? [e.title]) : ['No se pudo generar la vista previa.'];
      setState({ busy: false, warnings: [], errors });
    }
  }

  if (!saved) {
    return <Alert tone="info" title="Vista previa">Aplica primero el catálogo: la vista previa usa un registro guardado.</Alert>;
  }
  return (
    <section className="print-preview" aria-label="Vista previa del PDF">
      <h3 className="section-heading">Vista previa</h3>
      <Field label="Registro para la vista previa" hint="Un registro real de este catálogo; se genera con tu propia seguridad (lo que tu rol no ve sale «(sin acceso)»).">
        <ReferenceSelect entity={ctx.entity} value={record.id} label={record.label} ariaLabel="Registro para la vista previa"
          onChange={(id, label) => { setRecord({ id, label }); setStale(true); }} />
      </Field>
      <div className="row" style={{ flexWrap: 'wrap', gap: 'var(--space-2)' }}>
        <Button variant="primary" icon={Eye} loading={state.busy} disabled={record.id == null} onClick={() => void generate()}>
          {state.url ? 'Actualizar vista previa' : 'Generar vista previa'}
        </Button>
        {state.url && (
          <a className="btn btn-ghost" href={state.url} target="_blank" rel="noopener noreferrer">
            <ExternalLink size={16} aria-hidden /> Abrir en otra pestaña<span className="sr-only"> (vista previa del PDF)</span>
          </a>
        )}
      </div>
      <div aria-live="polite">
        {stale && state.url && <span className="field-hint">La plantilla o el registro cambiaron: actualiza la vista previa.</span>}
      </div>
      {state.errors.length > 0 && (
        <Alert tone="danger" title="La plantilla tiene errores">
          <ul className="compiler-errors">{state.errors.map((e) => <li key={e}>{e}</li>)}</ul>
        </Alert>
      )}
      {state.warnings.length > 0 && (
        <Alert tone="warning" title="Avisos de la vista previa">
          <ul className="compiler-errors">{state.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
        </Alert>
      )}
      {state.url ? (
        <iframe className="print-preview-frame" src={state.url} title={`Vista previa del PDF «${template.label}»${record.label ? ` con ${record.label}` : ''}`} />
      ) : (
        <div className="print-preview-empty subtle">Elige un registro y genera la vista previa para ver el PDF aquí.</div>
      )}
    </section>
  );
}

/* ------------------------------------------------------------------------- */
/* Plantillas del catálogo                                                     */

/** Spec 027 (H4): plantillas de impresión del catálogo, con su vista previa. */
export function PrintTemplatesEditor({ templates, onChange, ctx, saved }: { templates: PrintTemplate[]; onChange: (t: PrintTemplate[]) => void; ctx: SectionCtx; saved: boolean }) {
  const [images, setImages] = useState<StoredImage[] | null>([]);
  useEffect(() => {
    const ctl = new AbortController();
    imagesApi.list(ctl.signal).then(setImages).catch((e) => { if (!ctl.signal.aborted) setImages(e instanceof ApiError ? null : []); });
    return () => ctl.abort();
  }, []);
  const hasLines = ctx.fields.some((f) => f.type === 'lines');
  return (
    <ItemList<PrintTemplate>
      items={templates}
      onChange={onChange}
      kind="printTemplate"
      ctx={ctx}
      icon={FileText}
      noun="plantilla"
      emptyTitle="Este catálogo no tiene plantillas de impresión"
      emptyDescription="Una plantilla define el PDF de un registro: encabezado con logo, campos, partidas, totales y firmas. Puedes tener varias (p. ej. para el proveedor e interna)."
      addLabel="Agregar plantilla"
      create={(items) => {
        const blocks: PrintBlock[] = [newBlock('header', ctx)];
        if (hasLines) blocks.push(newBlock('table', ctx));
        blocks.push(newBlock('footer', ctx));
        return { name: uniqueName('Plantilla', items.map((t) => t.name)), label: '', default: items.length === 0 ? true : undefined, blocks } as PrintTemplate;
      }}
      name={(t) => t.name}
      title={(t) => t.label}
      summary={(t) => (
        <>
          {t.default && <Badge tone="success">Predeterminada</Badge>}
          <span>{t.blocks.length} bloque(s) · {t.page?.size === 'a4' ? 'A4' : 'Carta'}{t.page?.orientation === 'landscape' ? ' horizontal' : ''}</span>
          {t.roles?.length ? <span>· {t.roles.join(', ')}</span> : null}
        </>
      )}
      render={(t, patch) => (
        <div className="print-layout">
          <div className="item-grid">
            <Input label="Identificador" required className="mono" value={t.name ?? ''} maxLength={60}
              hint="Letras, números y guion bajo; único en el catálogo." onChange={(e) => patch({ name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
            <Input label="Nombre" required value={t.label ?? ''} maxLength={100} hint="Lo ve quien descarga el PDF, p. ej. «Orden de compra para proveedor»."
              onChange={(e) => patch({ label: e.target.value })} />
            <Input wrapClassName="span-all" label="Descripción (opcional)" value={t.description ?? ''} maxLength={300} onChange={(e) => patch({ description: e.target.value || undefined })} />
            <Switch checked={!!t.default} onChange={(v) => patch({ default: v || undefined })} label="Predeterminada (la que se ofrece primero)" />
            <span />
            <Select label="Tamaño de página" value={t.page?.size ?? 'letter'} onChange={(e) => patch({ page: clean({ ...t.page, size: e.target.value === 'a4' ? ('a4' as const) : undefined }) })}>
              <option value="letter">Carta</option><option value="a4">A4</option>
            </Select>
            <Select label="Orientación" value={t.page?.orientation ?? 'portrait'} onChange={(e) => patch({ page: clean({ ...t.page, orientation: e.target.value === 'landscape' ? ('landscape' as const) : undefined }) })}>
              <option value="portrait">Vertical</option><option value="landscape">Horizontal</option>
            </Select>
            <Input label="Márgenes (mm)" type="number" min={5} max={40} value={t.page?.margin ?? ''} hint="De 5 a 40. Vacío: 15."
              onChange={(e) => patch({ page: clean({ ...t.page, margin: e.target.value === '' ? undefined : Number(e.target.value) }) })} />
            <span />
            <MultiPick legend="Roles que la usan" options={roleOptions(ctx)} value={t.roles ?? []} onChange={(roles) => patch({ roles: roles.length ? roles : undefined })}
              hint="Ninguno marcado: todos los que pueden leer el catálogo." />
            <Field label="Empresas (claves)" className="span-all" hint="Vacío: todas. Útil si cada empresa tiene su formato.">
              <TagInput value={t.companies ?? []} onChange={(v) => patch({ companies: v.length ? v.map((x) => x.toUpperCase()) : undefined })} placeholder="Agregar clave…" aria-label="Empresas en que aplica" />
            </Field>
            <ExprInput condition className="span-all" label="Aplica solo cuando (opcional)" value={t.when ?? ''} onChange={(v) => patch({ when: v || undefined })}
              scope={selfScope(ctx)} entities={ctx.entities} hint="Vacío: siempre. P. ej. solo órdenes autorizadas." />
            <BlocksEditor blocks={t.blocks ?? []} onChange={(blocks) => patch({ blocks })} ctx={ctx} images={images} />
          </div>
          <Preview template={t} ctx={ctx} saved={saved} />
        </div>
      )}
    />
  );
}
