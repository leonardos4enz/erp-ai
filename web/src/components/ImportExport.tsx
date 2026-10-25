import { useEffect, useId, useRef, useState } from 'react';
import { Download, FileSpreadsheet, FileText, Upload } from 'lucide-react';
import { ApiError } from '../api/client';
import { dataApi, type ListQuery } from '../api/endpoints';
import type { EntityDef, ImportResult } from '../api/types';
import { activeFields } from '../lib/entity';
import { Dialog } from './Dialog';
import { useToast } from './Toast';
import { Alert, Badge, Button, Spinner } from './ui';

/** Spec 008: exportar el listado tal como se ve (vista, filtros, orden y columnas visibles). */
export function ExportDialog({ open, def, query, columns, onClose }: {
  open: boolean; def: EntityDef; query: Omit<ListQuery, 'page' | 'pageSize'>; columns: string[]; onClose: () => void;
}) {
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={`Exportar ${def.labelPlural.toLowerCase()}`}
      description="Se exporta la vista actual: búsqueda, filtros, estado, orden y columnas visibles (máximo 5000 registros)."
      footer={<Button onClick={onClose}>Cerrar</Button>}
    >
      <div className="row" style={{ gap: 'var(--space-2)', flexWrap: 'wrap' }}>
        <a className="btn btn-secondary" href={dataApi.exportUrl(def.name, 'csv', query, columns)} download onClick={onClose}>
          <FileText size={16} aria-hidden /> Descargar CSV
        </a>
        <a className="btn btn-secondary" href={dataApi.exportUrl(def.name, 'xlsx', query, columns)} download onClick={onClose}>
          <FileSpreadsheet size={16} aria-hidden /> Descargar Excel
        </a>
      </div>
    </Dialog>
  );
}

/** Campos que el usuario puede capturar al crear: las columnas de la plantilla. */
function importableLabels(def: EntityDef): string[] {
  return activeFields(def)
    .filter((f) => f.type !== 'lines' && f.type !== 'parent' && !f.readOnly && f.editable !== false && (f.access ?? 'edit') === 'edit')
    .filter((f) => !f.editableIn || (def.workflow && f.editableIn.includes(def.workflow.initial)))
    .map((f) => f.label);
}

function csvCell(s: string) {
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

const formatSize = (bytes: number) => (bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`);

const statusTone = { ok: 'success', ignored: 'neutral', error: 'danger' } as const;
const statusLabel = { ok: 'Se importa', ignored: 'Se ignora', error: 'Error' } as const;

/**
 * Spec 008: importación masiva. Primero una vista previa (el servidor corre cada fila por el motor y lo revierte);
 * solo si no hay errores se ofrece importar. Es todo o nada.
 */
export function ImportDialog({ open, def, onClose, onImported }: { open: boolean; def: EntityDef; onClose: () => void; onImported: (n: number) => void }) {
  const toast = useToast();
  const inputId = useId();
  const fileRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<ImportResult | null>(null);
  const [checking, setChecking] = useState(false);
  const [importing, setImporting] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [dragging, setDragging] = useState(false);

  useEffect(() => {
    if (!open) return;
    setFile(null);
    setPreview(null);
    setError(null);
    if (fileRef.current) fileRef.current.value = '';
  }, [open]);

  async function check(f: File) {
    setFile(f);
    setPreview(null);
    setError(null);
    setChecking(true);
    try {
      setPreview(await dataApi.importFile(def.name, f, true));
    } catch (e) {
      if (e instanceof ApiError) setError(e); else toast.fromError(e);
    } finally {
      setChecking(false);
    }
  }

  async function run() {
    if (!file) return;
    setImporting(true);
    try {
      const r = await dataApi.importFile(def.name, file, false);
      toast.success(`Se importaron ${r.imported.toLocaleString('es-MX')} ${r.imported === 1 ? 'registro' : 'registros'}`);
      onImported(r.imported);
    } catch (e) {
      if (e instanceof ApiError) setError(e); else toast.fromError(e);
    } finally {
      setImporting(false);
    }
  }

  function template() {
    const csv = '﻿' + importableLabels(def).map(csvCell).join(',') + '\r\n';
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = `${def.name}-plantilla.csv`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  const canImport = !!preview?.valid && preview.total > 0 && !checking;
  const okColumns = preview?.columns.filter((c) => c.status === 'ok').length ?? 0;

  return (
    <Dialog
      open={open}
      onClose={onClose}
      size="lg"
      title={`Importar ${def.labelPlural.toLowerCase()}`}
      description="La primera fila son los encabezados: el nombre o la etiqueta de cada campo (máximo 1000 filas). Antes de guardar verás una vista previa."
      footer={
        <>
          <Button icon={Download} onClick={template} style={{ marginRight: 'auto' }}>Descargar plantilla</Button>
          <Button onClick={onClose} disabled={importing}>Cancelar</Button>
          <Button variant="primary" icon={Upload} loading={importing} disabled={!canImport} onClick={() => void run()}>
            {preview?.valid && preview.total > 0 ? `Importar ${preview.total.toLocaleString('es-MX')} ${preview.total === 1 ? 'registro' : 'registros'}` : 'Importar'}
          </Button>
        </>
      }
    >
      <div className="stack">
        {/* Zona para elegir o soltar el archivo: el input nativo queda oculto pero accesible (etiqueta «Archivo»). */}
        <div className="file-picker">
          <input
            ref={fileRef}
            id={inputId}
            className="sr-only"
            type="file"
            aria-label="Archivo"
            accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            onChange={(e) => { const f = e.target.files?.[0]; if (f) void check(f); }}
            data-autofocus
          />
          <label
            htmlFor={inputId}
            className={`file-drop${dragging ? ' dragging' : ''}${file ? ' has-file' : ''}`}
            onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => { e.preventDefault(); setDragging(false); const f = e.dataTransfer.files?.[0]; if (f) void check(f); }}
          >
            <span className="file-drop-icon" aria-hidden>{file ? <FileSpreadsheet size={20} /> : <Upload size={20} />}</span>
            <span className="file-drop-text">
              <strong>{file ? file.name : 'Elige un archivo o suéltalo aquí'}</strong>
              <span className="subtle">{file ? `${formatSize(file.size)} · elige otro para reemplazarlo` : 'CSV o Excel (.xlsx), máximo 2 MB'}</span>
            </span>
            <span className="btn btn-secondary btn-sm file-drop-btn" aria-hidden>{file ? 'Cambiar' : 'Elegir archivo'}</span>
          </label>
        </div>

        {checking && <div className="row" style={{ gap: 'var(--space-2)' }}><Spinner size={16} label="Revisando" /> <span className="muted">Revisando el archivo…</span></div>}
        {error && (
          <Alert tone="danger" title={error.title}>
            {Object.keys(error.fieldErrors).length > 0 && (
              <ul className="import-errors">
                {Object.entries(error.fieldErrors).slice(0, 20).map(([k, v]) => <li key={k}><strong>{k}:</strong> {v.join(' ')}</li>)}
              </ul>
            )}
          </Alert>
        )}

        {preview && (
          <>
            {preview.valid ? (
              <Alert tone="info" title={preview.total === 0 ? 'El archivo no tiene filas con datos.' : 'Todo listo para importar'}>
                {preview.total > 0 && `${preview.total.toLocaleString('es-MX')} ${preview.total === 1 ? 'fila' : 'filas'} sin errores; ${okColumns} ${okColumns === 1 ? 'columna' : 'columnas'} se importan. Nada se guarda hasta que confirmes.`}
              </Alert>
            ) : (
              <Alert tone="danger" title="El archivo tiene errores; corrígelos y vuelve a elegirlo">
                No se importará ningún registro mientras haya errores.
              </Alert>
            )}

            <div>
              <h3 className="section-title">Columnas</h3>
              <div className="table-scroll">
                <table className="table">
                  <thead><tr><th>Columna del archivo</th><th>Campo</th><th>Resultado</th></tr></thead>
                  <tbody>
                    {preview.columns.filter((c) => c.column).map((c, i) => (
                      <tr key={i}>
                        <td>{c.column}</td>
                        <td>{c.label ?? '—'}</td>
                        <td>
                          <Badge tone={statusTone[c.status]}>{statusLabel[c.status]}</Badge>
                          {c.message && <div className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{c.message}</div>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>

            {preview.errors.length > 0 && (
              <div>
                <h3 className="section-title">Errores por fila</h3>
                <div className="table-scroll">
                  <table className="table">
                    <thead><tr><th>Fila</th><th>Columna</th><th>Error</th></tr></thead>
                    <tbody>
                      {preview.errors.map((e, i) => (
                        <tr key={i}><td className="mono">{e.row}</td><td>{e.column ?? '—'}</td><td>{e.message}</td></tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                {preview.truncatedErrors && <p className="muted">Se muestran los primeros {preview.errors.length} errores.</p>}
              </div>
            )}

            {/* Spec 023: los avisos no impiden importar la fila; se listan para revisarlos. */}
            {(preview.warnings?.length ?? 0) > 0 && (
              <div>
                <h3 className="section-title">Avisos por fila</h3>
                <p className="muted">Estas filas se importan; revisa los avisos antes de confirmar.</p>
                <div className="table-scroll">
                  <table className="table">
                    <thead><tr><th>Fila</th><th>Columna</th><th>Aviso</th></tr></thead>
                    <tbody>
                      {preview.warnings!.map((w, i) => (
                        <tr key={i}><td className="mono">{w.row}</td><td>{w.column ?? '—'}</td><td>{w.message}</td></tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </Dialog>
  );
}
