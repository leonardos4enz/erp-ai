// Spec 030: en CI, convierte la salida de la suite (o de la integración) en anotaciones de GitHub Actions. Los registros
// de un repositorio público solo se ven con sesión iniciada; las anotaciones, no (API de check-runs): así una sesión puede
// saber por qué falló CI sin credenciales.
// Uso: node web/e2e/anotar-ci.mjs <archivo de salida> [título]
import { existsSync, readFileSync } from 'node:fs';

const [archivo, titulo = 'E2E'] = process.argv.slice(2);
if (!archivo || !existsSync(archivo)) {
  console.log(`::error title=${titulo}::No hay salida que anotar (${archivo ?? 'sin archivo'}): la suite no llegó a escribirla.`);
  process.exit(0);
}
// Sin colores de la terminal; GitHub limita a 10 errores por paso: 9 fallas y el final de la salida.
const lineas = readFileSync(archivo, 'utf8').replace(/\u001b\[[0-9;]*m/g, '').split(/\r?\n/);
const esc = (s) => s.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
const fallas = [];
for (let i = 0; i < lineas.length && fallas.length < 9; i++) {
  // Playwright: «  1) [chromium] › e2e\a.spec.ts:10:1 › título»; dotnet test: «  Con error Clase.Prueba [12 ms]» / «Failed …».
  if (/^\s+\d+\) \[\w+\] › /.test(lineas[i]) || /^\s+(Con error|Failed) \S+/.test(lineas[i])) {
    const cuerpo = lineas.slice(i + 1, i + 40).filter((l) => l.trim() && !/^\s+(at |\d+ \||>)/.test(l)).slice(0, 14);
    fallas.push(`${lineas[i].trim()}\n${cuerpo.map((l) => l.trim()).join('\n')}`.slice(0, 3500));
  }
}
for (const f of fallas) console.log(`::error title=${titulo}: prueba fallida::${esc(f)}`);
const final = lineas.filter((l) => l.trim()).slice(-25).join('\n').slice(-3500);
console.log(`::error title=${titulo}: final de la salida::${esc(final)}`);
