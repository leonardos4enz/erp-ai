/**
 * Spec 037: subconjunto seguro de Markdown para el texto del modelo (entrada no confiable: constitución II.8, R-033-4).
 * Devuelve un árbol de datos; `components/Markdown` lo pinta con elementos de React (texto escapado). No hay HTML, enlaces
 * ni imágenes: `[texto](url)`, `<b>` y las URL quedan como texto. Todo el análisis es lineal (sin expresiones con retroceso
 * sobre el texto completo): un tope de búsqueda que falló una vez no se vuelve a intentar.
 */

export type Inline =
  | { t: 'text'; v: string }
  | { t: 'br' }
  | { t: 'code'; v: string }
  | { t: 'strong'; c: Inline[] }
  | { t: 'em'; c: Inline[] };

export type Block =
  | { t: 'p'; c: Inline[] }
  | { t: 'h'; level: 1 | 2 | 3; c: Inline[] }
  | { t: 'ul'; items: Inline[][] }
  | { t: 'ol'; items: Inline[][]; start: number }
  | { t: 'quote'; c: Inline[] }
  | { t: 'code'; lang: string; v: string }
  | { t: 'table'; head: Inline[][]; rows: Inline[][][] }
  | { t: 'hr' };

const FENCE = /^ {0,3}(`{3,}|~{3,})\s*([\w+#.-]{0,20})/;
const BULLET = /^\s*[-*+]\s+(.*)$/;
const ORDERED = /^\s*(\d{1,9})[.)]\s+(.*)$/;
const RULE = /^ {0,3}([-*_])(\s*\1){2,}\s*$/;
const QUOTE = /^ {0,3}>\s?(.*)$/;

function cells(line: string): string[] {
  let s = line.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|') && !s.endsWith('\\|')) s = s.slice(0, -1);
  return s.split('|').map((c) => c.trim());
}

const isBlank = (l: string) => l.trim() === '';

/**
 * Título `#`–`######` sin expresiones regulares. Revisión adversarial 037 (F-02): `^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$`
 * retrocedía en O(n³) con un título lleno de espacios y congelaba la pestaña con cada cuadro del texto en vivo.
 */
function heading(line: string): { level: 1 | 2 | 3; text: string } | null {
  let i = 0;
  while (i < 3 && line[i] === ' ') i++;
  let n = 0;
  while (line[i + n] === '#') n++;
  if (n < 1 || n > 6) return null;
  const rest = line.slice(i + n);
  if (rest.trim() === '' || (rest[0] !== ' ' && rest[0] !== '\t')) return null;
  let text = rest.trim();
  let e = text.length;
  while (e > 0 && text[e - 1] === '#') e--;
  // Los `#` del cierre solo cuentan si van separados del texto.
  if (e < text.length && (e === 0 || /\s/.test(text[e - 1]))) text = text.slice(0, e).trim();
  return { level: Math.min(n, 3) as 1 | 2 | 3, text };
}

/** Separador de tabla (`|---|:--:|`), celda por celda: lineal (la expresión anterior era O(n²), F-02). */
function isTableSep(line: string): boolean {
  let s = line.trim();
  if (!s.includes('-')) return false;
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|')) s = s.slice(0, -1);
  return s.split('|').every((c) => /^:?-+:?$/.test(c.trim()));
}

export function parseMarkdown(src: string): Block[] {
  const lines = src.replace(/\r\n?/g, '\n').split('\n');
  const out: Block[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (isBlank(line)) { i++; continue; }

    const fence = FENCE.exec(line);
    if (fence) {
      const marker = fence[1];
      const body: string[] = [];
      i++;
      // Sin cierre (texto en vivo a mitad de un bloque) el bloque llega hasta el final.
      const closes = (l: string) => { const s = l.trim(); return s.length >= marker.length && [...s].every((c) => c === marker[0]); };
      while (i < lines.length && !closes(lines[i])) body.push(lines[i++]);
      i++;
      out.push({ t: 'code', lang: fence[2] ?? '', v: body.join('\n') });
      continue;
    }

    const h = heading(line);
    if (h) {
      out.push({ t: 'h', level: h.level, c: parseInline(h.text) });
      i++;
      continue;
    }

    if (RULE.test(line)) { out.push({ t: 'hr' }); i++; continue; }

    if (line.includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1])) {
      const head = cells(line);
      const rows: Inline[][][] = [];
      i += 2;
      while (i < lines.length && !isBlank(lines[i]) && lines[i].includes('|')) {
        const row = cells(lines[i++]);
        rows.push(head.map((_, k) => parseInline(row[k] ?? '')));
      }
      out.push({ t: 'table', head: head.map(parseInline), rows });
      continue;
    }

    if (QUOTE.test(line)) {
      const body: string[] = [];
      let m: RegExpExecArray | null;
      while (i < lines.length && (m = QUOTE.exec(lines[i]))) { body.push(m[1]); i++; }
      out.push({ t: 'quote', c: parseInline(body.join('\n')) });
      continue;
    }

    const bullet = BULLET.exec(line);
    const ordered = bullet ? null : ORDERED.exec(line);
    if (bullet || ordered) {
      const kind = bullet ? 'ul' : 'ol';
      const items: string[] = [];
      while (i < lines.length) {
        const l = lines[i];
        const b = BULLET.exec(l);
        const o = b ? null : ORDERED.exec(l);
        // Una lista anidada se aplana: cada viñeta es un punto.
        if (b || o) { items.push((b ? b[1] : o![2])); i++; continue; }
        // Continuación del punto (con sangría y sin línea en blanco).
        if (!isBlank(l) && /^\s+\S/.test(l) && items.length) { items[items.length - 1] += `\n${l.trim()}`; i++; continue; }
        break;
      }
      const parsed = items.map(parseInline);
      out.push(kind === 'ul' ? { t: 'ul', items: parsed } : { t: 'ol', items: parsed, start: Number(ordered![1]) || 1 });
      continue;
    }

    const para: string[] = [];
    while (i < lines.length && !isBlank(lines[i]) && !FENCE.test(lines[i]) && !heading(lines[i]) && !BULLET.test(lines[i])
      && !ORDERED.test(lines[i]) && !QUOTE.test(lines[i]) && !RULE.test(lines[i])
      && !(lines[i].includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1]) && para.length === 0)) {
      para.push(lines[i++]);
    }
    if (para.length === 0) para.push(lines[i++]); // salvaguarda: siempre avanza
    out.push({ t: 'p', c: parseInline(para.join('\n')) });
  }
  return out;
}

const isWord = (ch: string | undefined) => !!ch && /[\p{L}\p{N}]/u.test(ch);
const isSpace = (ch: string | undefined) => !ch || /\s/.test(ch);

/** Negritas (`**`/`__`), cursivas (`*`/`_`, con límite de palabra para `_`), código en línea y saltos de línea. */
export function parseInline(src: string): Inline[] {
  // Marcadores cuya búsqueda de cierre ya falló desde una posición: tampoco cerrarán desde una posterior.
  const failed = new Set<string>();
  return inline(src, failed);
}

function inline(src: string, failed: Set<string>): Inline[] {
  const out: Inline[] = [];
  let text = '';
  const flush = () => { if (text) { out.push({ t: 'text', v: text }); text = ''; } };
  let i = 0;
  while (i < src.length) {
    const ch = src[i];
    if (ch === '\n') { flush(); out.push({ t: 'br' }); i++; continue; }
    if (ch === '\\' && i + 1 < src.length && /[\\`*_|#>[\]-]/.test(src[i + 1])) { text += src[i + 1]; i += 2; continue; }
    if (ch === '`') {
      let n = 1;
      while (src[i + n] === '`') n++;
      const tick = '`'.repeat(n);
      const key = `code${n}`;
      const end = failed.has(key) ? -1 : src.indexOf(tick, i + n);
      if (end < 0) { failed.add(key); text += tick; i += n; continue; }
      flush();
      out.push({ t: 'code', v: src.slice(i + n, end).replace(/^ (.+) $/, '$1') });
      i = end + n;
      continue;
    }
    if ((ch === '*' || ch === '_') && src[i + 1] === ch && !isSpace(src[i + 2])) {
      const mark = ch + ch;
      const key = `strong${ch}`;
      if (ch === '_' && isWord(src[i - 1])) { text += mark; i += 2; continue; }
      const end = failed.has(key) ? -1 : findClose(src, mark, i + 2);
      if (end < 0) { failed.add(key); text += mark; i += 2; continue; }
      flush();
      out.push({ t: 'strong', c: inline(src.slice(i + 2, end), new Set(failed)) });
      i = end + 2;
      continue;
    }
    if ((ch === '*' || ch === '_') && !isSpace(src[i + 1]) && src[i + 1] !== ch) {
      // `_` solo marca cursiva fuera de una palabra: `listar_catalogos` se queda como está.
      if (ch === '_' && isWord(src[i - 1])) { text += ch; i++; continue; }
      const key = `em${ch}`;
      const end = failed.has(key) ? -1 : findClose(src, ch, i + 1);
      if (end < 0 || (ch === '_' && isWord(src[end + 1]))) { if (end < 0) failed.add(key); text += ch; i++; continue; }
      flush();
      out.push({ t: 'em', c: inline(src.slice(i + 1, end), new Set(failed)) });
      i = end + 1;
      continue;
    }
    text += ch;
    i++;
  }
  flush();
  return out;
}

/** Cierre de un marcador: no precedido de espacio y sin saltar un párrafo. Para `*` sencillo, que no sea parte de `**`. */
function findClose(src: string, mark: string, from: number): number {
  let k = from;
  while (true) {
    const end = src.indexOf(mark, k);
    if (end < 0) return -1;
    if (end > from && !isSpace(src[end - 1]) && (mark.length === 2 || src[end + 1] !== mark)) return end;
    k = end + mark.length;
  }
}

/** Texto plano de un árbol (para copiar y para el lector de pantalla). */
export function inlineText(c: Inline[]): string {
  return c.map((x) => (x.t === 'text' || x.t === 'code' ? x.v : x.t === 'br' ? '\n' : inlineText(x.c))).join('');
}
