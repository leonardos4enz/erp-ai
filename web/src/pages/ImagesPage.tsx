import { useState } from 'react';
import { Image as ImageIcon, Plus } from 'lucide-react';
import { ApiError } from '../api/client';
import { imagesApi } from '../api/endpoints';
import type { StoredImage } from '../api/types';
import { ImageUpload } from '../components/ImageUpload';
import { useToast } from '../components/Toast';
import { Alert, EmptyState, Input, LoadingBlock, PageHeader } from '../components/ui';
import { formatDateTime, IDENTIFIER_RE } from '../lib/format';
import { useAsync } from '../lib/hooks';

const kb = (n: number) => `${Math.max(1, Math.round(n / 1024))} KB`;

/**
 * Spec 027 (H2): imágenes que usan las plantillas de impresión (un sello, una firma escaneada). Son datos de este entorno:
 * la plantilla las nombra por su clave y viaja entre entornos; cada entorno sube su imagen.
 */
export function ImagesPage() {
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const { data, loading, error } = useAsync((s) => imagesApi.list(s), [tick]);
  const [key, setKey] = useState('');
  const keyError = key && !IDENTIFIER_RE.test(key) ? 'Letras, números y guion bajo; empieza con letra (p. ej. SelloCalidad).' : undefined;
  const taken = !!data?.some((i) => i.key.toLowerCase() === key.toLowerCase());

  const upload = (k: string) => async (file: File) => {
    const saved = await imagesApi.save(k, file);
    toast.success('Imagen guardada', `${saved.key} · ${saved.width} × ${saved.height} px`);
    setKey('');
    setTick((t) => t + 1);
  };

  return (
    <div className="page">
      <PageHeader icon={ImageIcon} title="Imágenes"
        subtitle="Imágenes para las plantillas de impresión. La plantilla las nombra por su clave; son de este entorno y no viajan en los paquetes." />
      <div className="stack">
        <section className="card card-pad stack" aria-labelledby="img-new">
          <h2 id="img-new" className="section-title" style={{ margin: 0 }}>Subir una imagen</h2>
          <div className="row" style={{ flexWrap: 'wrap', alignItems: 'flex-start', gap: 'var(--space-4)' }}>
            <Input label="Clave" required className="mono" value={key} maxLength={60} error={keyError}
              hint={taken ? 'Ya existe: subir otra la reemplaza (la anterior se archiva).' : 'La que escribirás en la plantilla, p. ej. SelloCalidad.'}
              onChange={(e) => setKey(e.target.value.replace(/[^A-Za-z0-9_]/g, ''))} />
            {key && !keyError ? (
              <ImageUpload label={`Imagen «${key}»`} alt={`Imagen ${key}`} onUpload={upload(key)} />
            ) : (
              <p className="field-hint" style={{ marginTop: 28 }}><Plus size={13} aria-hidden /> Escribe la clave para elegir el archivo.</p>
            )}
          </div>
        </section>

        {loading && !data ? <LoadingBlock /> : error ? (
          <Alert tone="danger" title={error instanceof ApiError ? error.title : 'No se pudieron cargar las imágenes'} />
        ) : !data?.length ? (
          <div className="table-wrap">
            <EmptyState icon={ImageIcon} title="Sin imágenes"
              description="Sube un sello o una firma escaneada para usarlos en las plantillas (bloque «Imagen»). El logo de cada empresa se sube en Configuración › Empresas." />
          </div>
        ) : (
          <ul className="image-grid" aria-label="Imágenes subidas" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {data.map((img: StoredImage) => (
              <li key={img.id} className="card card-pad stack" style={{ gap: 'var(--space-2)' }}>
                <div className="row" style={{ justifyContent: 'space-between', gap: 'var(--space-2)' }}>
                  <strong className="mono">{img.key}</strong>
                  <span className="subtle" style={{ fontSize: 'var(--text-sm)' }}>{img.width} × {img.height} px · {kb(img.size)} · {img.contentType === 'image/png' ? 'PNG' : 'JPEG'}</span>
                </div>
                <ImageUpload label={`Imagen «${img.key}»`} url={img.url} alt={`Imagen ${img.key}`} onUpload={upload(img.key)}
                  removeMessage={`Las plantillas que usan «${img.key}» dejarán de imprimirla (la vista previa lo avisará). El archivo se conserva archivado.`}
                  onRemove={async () => { await imagesApi.remove(img.key); toast.success('Imagen archivada', img.key); setTick((t) => t + 1); }} />
                <span className="subtle" style={{ fontSize: 'var(--text-xs)' }}>Subió {img.createdBy} · {formatDateTime(img.createdAt)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
