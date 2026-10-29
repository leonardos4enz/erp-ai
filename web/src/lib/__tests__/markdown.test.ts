import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseInline, parseMarkdown } from '../markdown';

describe('Markdown seguro de Studio (037, H3 CA3)', () => {
  it('párrafos, títulos, listas, citas, reglas y saltos de línea', () => {
    const b = parseMarkdown('# Título\n\nUna línea\notra línea\n\n- uno\n- dos\n  sigue\n\n3. tres\n4. cuatro\n\n> cita\n\n---');
    expect(b.map((x) => x.t)).toEqual(['h', 'p', 'ul', 'ol', 'quote', 'hr']);
    expect(b[1]).toEqual({ t: 'p', c: [{ t: 'text', v: 'Una línea' }, { t: 'br' }, { t: 'text', v: 'otra línea' }] });
    expect(b[2]).toMatchObject({ t: 'ul', items: [[{ t: 'text', v: 'uno' }], [{ t: 'text', v: 'dos' }, { t: 'br' }, { t: 'text', v: 'sigue' }]] });
    expect(b[3]).toMatchObject({ t: 'ol', start: 3 });
    expect(parseMarkdown('## Título ##\n###### seis\n#sin espacio\n# C# es un lenguaje')).toEqual([
      { t: 'h', level: 2, c: [{ t: 'text', v: 'Título' }] }, { t: 'h', level: 3, c: [{ t: 'text', v: 'seis' }] },
      { t: 'p', c: [{ t: 'text', v: '#sin espacio' }] }, { t: 'h', level: 1, c: [{ t: 'text', v: 'C# es un lenguaje' }] },
    ]);
  });

  it('negritas, cursivas y código en línea; `_` dentro de una palabra no es cursiva', () => {
    expect(parseInline('**fuerte** y *suave* y `código *no*` y listar_catalogos_dos')).toEqual([
      { t: 'strong', c: [{ t: 'text', v: 'fuerte' }] }, { t: 'text', v: ' y ' }, { t: 'em', c: [{ t: 'text', v: 'suave' }] },
      { t: 'text', v: ' y ' }, { t: 'code', v: 'código *no*' }, { t: 'text', v: ' y listar_catalogos_dos' },
    ]);
    expect(parseInline('2 * 3 * 4')).toEqual([{ t: 'text', v: '2 * 3 * 4' }]);
    expect(parseInline('**sin cierre')).toEqual([{ t: 'text', v: '**sin cierre' }]);
  });

  it('bloques de código (también sin cierre, en vivo) y tablas', () => {
    expect(parseMarkdown('```sql\nSELECT 1;\n```\ndespués')).toEqual([{ t: 'code', lang: 'sql', v: 'SELECT 1;' }, { t: 'p', c: [{ t: 'text', v: 'después' }] }]);
    expect(parseMarkdown('```\na\nb')).toEqual([{ t: 'code', lang: '', v: 'a\nb' }]);
    const [t] = parseMarkdown('| Campo | Tipo |\n|---|:--:|\n| Peso | **decimal** |\n| Corto |');
    expect(t).toMatchObject({ t: 'table', head: [[{ t: 'text', v: 'Campo' }], [{ t: 'text', v: 'Tipo' }]] });
    expect(t.t === 'table' && t.rows).toEqual([[[{ t: 'text', v: 'Peso' }], [{ t: 'strong', c: [{ t: 'text', v: 'decimal' }] }]], [[{ t: 'text', v: 'Corto' }], []]]);
  });

  it('el HTML y los enlaces quedan como texto (sin enlaces activos ni imágenes)', () => {
    const src = '<img src=x onerror="alert(1)"> [Haz clic](https://phishing.example/login) ![i](https://x/y.png) https://a.example <b>n</b>';
    const b = parseMarkdown(src);
    expect(b).toEqual([{ t: 'p', c: [{ t: 'text', v: src }] }]);
    // El árbol solo tiene estos tipos: nada que el componente pueda volver un atributo o un enlace.
    const kinds = new Set<string>();
    const walk = (x: unknown) => { if (x && typeof x === 'object') { const o = x as Record<string, unknown>; if (typeof o.t === 'string') kinds.add(o.t); Object.values(o).forEach(walk); } };
    walk(parseMarkdown(`# ${src}\n- ${src}\n| ${src} | b |\n|-|-|\n| ${src} | c |\n> ${src}\n\`\`\`${src}\n${src}\n\`\`\``));
    expect([...kinds].sort()).toEqual(['code', 'h', 'quote', 'table', 'text', 'ul'].sort());
  });

  it('una entrada patológica se analiza en tiempo lineal', () => {
    // Regresión (revisión adversarial 037, F-02): un título con miles de espacios tardaba segundos (O(n³)).
    const inputs = [`# a${' '.repeat(8000)}b`, `# a${'\t '.repeat(4000)}#x`, `| a |\n|${' -'.repeat(4000)} x`, `${'- '.repeat(4000)}x`, '*a '.repeat(20_000), '**'.repeat(30_000), '`'.repeat(20_000), '_a_'.repeat(20_000), '| a '.repeat(20_000) + '\n|-|\n', '- \n'.repeat(20_000)];
    for (const x of inputs) {
      const t = performance.now();
      parseMarkdown(x);
      expect(performance.now() - t, x.slice(0, 10)).toBeLessThan(250);
    }
  });

  it('ningún archivo de la pantalla de Studio usa dangerouslySetInnerHTML ni crea enlaces o imágenes', () => {
    const root = join(__dirname, '..', '..', 'pages');
    const files = [join(root, 'StudioPage.tsx'), ...readdirSync(join(root, 'studio')).map((f) => join(root, 'studio', f))];
    for (const f of files) {
      const src = readFileSync(f, 'utf8');
      expect(src, f).not.toMatch(/dangerouslySetInnerHTML|innerHTML\s*=|<a\s|<img\s|<b>/);
    }
  });
});
