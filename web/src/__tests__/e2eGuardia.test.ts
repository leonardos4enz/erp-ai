import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Spec 034 (H7, R-030-5): las intermitentes de CI de la 033 eran carreras de la prueba, no del producto. Dos formas se
 * repetían: leer el último elemento de un receptor (`captured.at(-1)`: si la petición de esta acción aún no llega, es la
 * de la anterior) y `getByText(...).last()` (el aviso de la acción anterior, aún visible). Esta guardia las prohíbe en
 * `web/e2e`; una que sí está bien se queda con una línea `// razón: …` justo encima (o en la misma línea).
 * Para receptores: `receptor()` con `marca()`/`siguiente()` de `e2e/helpers.ts`.
 */
const E2E = path.resolve(__dirname, '..', '..', 'e2e');

export function revisar(archivo: string, texto: string): string[] {
  const lineas = texto.split(/\r?\n/);
  const conRazon = (i: number) => /\/\/\s*razón:/.test(lineas[i]) || (i > 0 && /\/\/\s*razón:/.test(lineas[i - 1]));
  // Receptor: un arreglo declarado vacío (en el archivo o dentro de una prueba) que algo llena con push (un servidor
  // simulado, un evento). Revisión adversarial 034 (F-06): también los declarados con sangría.
  const receptores = [...texto.matchAll(/^\s*(?:const|let)\s+(\w+)\s*(?::[^=\n]+)?=\s*\[\s*\]/gm)].map((m) => m[1])
    .filter((n) => new RegExp(`\\b${n}\\.push\\(`).test(texto));
  const problemas: string[] = [];
  lineas.forEach((l, i) => {
    // Los comentarios pueden nombrarlas (la documentación de helpers.ts lo hace).
    if (conRazon(i) || /^\s*(\*|\/\/|\/\*)/.test(l)) return;
    for (const r of receptores)
      if (new RegExp(`\\b${r}\\.at\\(\\s*-1\\s*\\)|\\b${r}\\[\\s*${r}\\.length\\s*-\\s*1\\s*\\]`).test(l))
        problemas.push(`${archivo}:${i + 1}: «${r}» es un receptor: usa receptor() con marca()/siguiente() en lugar del último elemento`);
    if (/getByText\((?:[^()]|\([^()]*\))*\)\s*\.last\(\)/.test(l))
      problemas.push(`${archivo}:${i + 1}: getByText(...).last() sin «// razón:» (¿es el aviso de la acción anterior? usa expectToast)`);
  });
  return problemas;
}

describe('guardia de las pruebas E2E (spec 034)', () => {
  it('detecta las dos formas (la guardia no es un adorno)', () => {
    const muestra = [
      'const captured: string[] = [];',
      'server.on("x", (v) => captured.push(v));',
      'expect(captured.at(-1)).toBe(1);',
      'expect(captured[captured.length - 1]).toBe(1);',
      'await expect(page.getByText("Guardado").last()).toBeVisible();',
      '// razón: el texto aparece dos veces y el segundo es el de la tabla',
      'await expect(page.getByText("Fila").last()).toBeVisible();',
      'const history = await api.get("/h"); expect(history.at(-1)).toBe(2);',
      '  const pings: string[] = [];',
      '  srv.on("p", (p) => pings.push(p));',
      '  expect(pings.at(-1)).toBe("x");',
    ].join('\n');
    expect(revisar('muestra.ts', muestra)).toEqual([
      'muestra.ts:3: «captured» es un receptor: usa receptor() con marca()/siguiente() en lugar del último elemento',
      'muestra.ts:4: «captured» es un receptor: usa receptor() con marca()/siguiente() en lugar del último elemento',
      'muestra.ts:5: getByText(...).last() sin «// razón:» (¿es el aviso de la acción anterior? usa expectToast)',
      'muestra.ts:11: «pings» es un receptor: usa receptor() con marca()/siguiente() en lugar del último elemento',
    ]);
  });

  it('ningún archivo de web/e2e lee el último elemento de un receptor ni usa getByText(...).last() sin razón', () => {
    const archivos = readdirSync(E2E).filter((f) => f.endsWith('.ts'));
    expect(archivos.length).toBeGreaterThan(30);
    const problemas = archivos.flatMap((f) => revisar(f, readFileSync(path.join(E2E, f), 'utf8')));
    expect(problemas).toEqual([]);
  });
});
