import { useId } from 'react';

/**
 * Spec 032: marca de ERP AI en SVG propio. El isotipo son las tres filas del glifo de la barra lateral (filas de metadatos)
 * con un nodo al final de la fila corta (la relación, la IA que completa el modelo). Decorativo: el nombre va como texto.
 */
export function BrandMark({ size = 36 }: { size?: number }) {
  // Un `id` por instancia: dos isotipos en la página no comparten `url(#…)`.
  const id = `bm${useId().replace(/:/g, '')}`;
  return (
    <svg className="brand-logo-mark" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#6366f1" />
          <stop offset="1" stopColor="#3730a3" />
        </linearGradient>
      </defs>
      <rect width="32" height="32" rx="8" fill={`url(#${id})`} />
      <path d="M8.5 10.5h15M8.5 16h9M8.5 21.5h15" stroke="#fff" strokeWidth="2.6" strokeLinecap="round" fill="none" />
      <circle cx="23" cy="16" r="2.4" fill="#c7d2fe" />
    </svg>
  );
}

/** Logotipo: isotipo y nombre (texto real, no imagen). */
export function BrandLogo({ size = 36 }: { size?: number }) {
  return (
    <span className="brand-logo">
      <BrandMark size={size} />
      <span className="brand-logo-name">ERP AI</span>
    </span>
  );
}

/** Una tarjeta de catálogo: encabezado y filas de campos, sin texto. */
function Card({ x, y, rows, className }: { x: number; y: number; rows: number[]; className?: string }) {
  const h = 34 + rows.length * 20;
  return (
    <g className={['ill-card', className].filter(Boolean).join(' ')} transform={`translate(${x} ${y})`}>
      <rect className="ill-card-body" width="150" height={h} rx="12" />
      <path className="ill-card-head" d="M0 12a12 12 0 0 1 12-12h126a12 12 0 0 1 12 12v14H0z" />
      <circle className="ill-card-icon" cx="16" cy="13" r="5" />
      <rect className="ill-bar-strong" x="28" y="10" width="54" height="6" rx="3" />
      {rows.map((w, i) => (
        <g key={i} className="ill-row" transform={`translate(0 ${38 + i * 20})`}>
          <rect className="ill-bar" x="12" y="0" width="34" height="6" rx="3" />
          <rect className={i === 0 ? 'ill-bar-accent' : 'ill-bar-soft'} x="54" y="0" width={w} height="6" rx="3" />
        </g>
      ))}
    </g>
  );
}

/**
 * Ilustración del panel de marca (spec 032): catálogos unidos por relaciones a un nodo central (el motor y el asistente).
 * Decorativa (`aria-hidden`, sin texto). Se anima solo con CSS (`login.css`, `transform`/`opacity`); sin animación es la
 * composición completa. Los pulsos recorren cada relación hacia el nodo; las filas del catálogo de abajo a la derecha
 * aparecen una a una.
 */
export function BrandIllustration() {
  const id = `bi${useId().replace(/:/g, '')}`;
  return (
    <svg className="login-illustration" viewBox="0 0 560 420" aria-hidden="true" focusable="false">
      <defs>
        <pattern id={`${id}-dots`} width="24" height="24" patternUnits="userSpaceOnUse">
          <circle cx="2" cy="2" r="1.2" className="ill-dot" />
        </pattern>
        <radialGradient id={`${id}-core`} cx="0.5" cy="0.4" r="0.6">
          <stop offset="0" stopColor="#ffffff" />
          <stop offset="1" stopColor="#c7d2fe" />
        </radialGradient>
      </defs>
      <rect width="560" height="420" fill={`url(#${id}-dots)`} />

      {/* Relaciones: de cada catálogo al nodo central (280, 210). */}
      <g className="ill-edges">
        <path d="M180 76H215V210H250" />
        <path d="M380 86H345V210H310" />
        <path d="M180 322H215V210H250" />
        <path d="M380 318H345V210H310" />
      </g>
      {/* Cada pulso sigue su relación con `translate` (login.css, @keyframes ill-flow-N). */}
      <g className="ill-pulses">
        <circle className="ill-pulse ill-pulse-1" cx="180" cy="76" r="3.5" />
        <circle className="ill-pulse ill-pulse-2" cx="380" cy="86" r="3.5" />
        <circle className="ill-pulse ill-pulse-3" cx="180" cy="322" r="3.5" />
        <circle className="ill-pulse ill-pulse-4" cx="380" cy="318" r="3.5" />
      </g>

      <Card x={30} y={30} rows={[60, 44, 70]} />
      <Card x={380} y={40} rows={[50, 72, 38]} />
      <Card x={30} y={286} rows={[66, 40]} />
      <Card x={380} y={272} rows={[56, 70, 46]} className="ill-card-new" />

      {/* Nodo central: el motor y el asistente. */}
      <g className="ill-core">
        <circle className="ill-orbit" cx="280" cy="210" r="52" />
        <circle className="ill-halo" cx="280" cy="210" r="40" />
        <circle cx="280" cy="210" r="30" fill={`url(#${id}-core)`} />
        <path className="ill-spark" d="M280 194c1.6 8.4 5.6 12.4 14 14-8.4 1.6-12.4 5.6-14 14-1.6-8.4-5.6-12.4-14-14 8.4-1.6 12.4-5.6 14-14z" />
      </g>
    </svg>
  );
}
