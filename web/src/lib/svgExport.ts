/**
 * Spec 025 (H1 CA7): exporta un diagrama SVG de la página como SVG o PNG. Los estilos vienen de las clases (tema claro u
 * oscuro): se copian los calculados como atributos del clon, así el archivo se ve igual fuera de la app y no hace falta
 * `style` en línea (CSP). El PNG pasa por una imagen `data:` (permitida por `img-src 'self' data:`).
 */
const PROPS = ['fill', 'fill-opacity', 'stroke', 'stroke-width', 'stroke-opacity', 'stroke-dasharray', 'opacity', 'font-family',
  'font-size', 'font-weight', 'text-anchor', 'dominant-baseline', 'visibility'];

function inline(src: Element, dst: Element) {
  const cs = getComputedStyle(src);
  for (const p of PROPS) {
    const v = cs.getPropertyValue(p);
    if (v) dst.setAttribute(p, v);
  }
  dst.removeAttribute('class');
  dst.removeAttribute('tabindex');
  dst.removeAttribute('role');
  dst.removeAttribute('aria-pressed');
  for (let i = 0; i < src.children.length; i++) inline(src.children[i], dst.children[i]);
}

export function serializeSvg(svg: SVGSVGElement, width: number, height: number, title: string): string {
  const clone = svg.cloneNode(true) as SVGSVGElement;
  inline(svg, clone);
  clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  clone.setAttribute('width', String(Math.round(width)));
  clone.setAttribute('height', String(Math.round(height)));
  clone.setAttribute('viewBox', `0 0 ${width} ${height}`);
  const t = document.createElementNS('http://www.w3.org/2000/svg', 'title');
  t.textContent = title;
  clone.insertBefore(t, clone.firstChild);
  return '<?xml version="1.0" encoding="UTF-8"?>\n' + new XMLSerializer().serializeToString(clone);
}

export function download(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function exportSvg(svg: SVGSVGElement, size: { width: number; height: number }, fileName: string, title: string) {
  download(new Blob([serializeSvg(svg, size.width, size.height, title)], { type: 'image/svg+xml;charset=utf-8' }), fileName);
}

/** PNG a 2× (o menos si el diagrama es enorme: el lienzo del navegador tiene un límite). */
export function exportPng(svg: SVGSVGElement, size: { width: number; height: number }, fileName: string, title: string): Promise<void> {
  const text = serializeSvg(svg, size.width, size.height, title);
  const ratio = Math.min(2, 8000 / Math.max(size.width, size.height));
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => {
      const canvas = document.createElement('canvas');
      canvas.width = Math.round(size.width * ratio);
      canvas.height = Math.round(size.height * ratio);
      const ctx = canvas.getContext('2d');
      if (!ctx) { reject(new Error('Sin lienzo')); return; }
      ctx.scale(ratio, ratio);
      ctx.drawImage(img, 0, 0);
      canvas.toBlob((b) => { if (b) { download(b, fileName); resolve(); } else reject(new Error('No se pudo generar la imagen')); }, 'image/png');
    };
    img.onerror = () => reject(new Error('No se pudo generar la imagen'));
    img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(text);
  });
}
