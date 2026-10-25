# Verify NNN — Título

<!-- Plantilla (spec 028). Copiar a docs/sdd/specs/NNN-nombre/verify.md. El bloque «Métricas» lo lee
     `node docs/sdd/metricas.mjs`: conservar las claves y el formato `- clave: valor`. Fechas en hora local, AAAA-MM-DD HH:MM.
     Un dato que no se midió se escribe «n/d», nunca 0. -->

## Métricas
- inicio: AAAA-MM-DD HH:MM
- cierre: AAAA-MM-DD HH:MM
- unitarias: TOTAL (+NUEVAS)
- e2e: TOTAL (+NUEVAS)
- corridas completas: N (duración de cada una, p. ej. 7 min 40 s; 8 min 02 s)
- corridas parciales: N (qué y para qué: --veces=3 sobre …, iteración)
- contra el código anterior: N (comando y commit)
- hallazgos: A/M/B (p. ej. 0/2/1)
- categorías: categoría=N, … (UI/UX, motor-api, accesibilidad, pruebas, seguridad, proceso, visual, concurrencia)
- revisión adversarial: N hallazgos
- riesgos: R-NNN-1, R-NNN-2 (o «ninguno»)

## 1. Pruebas unitarias
`dotnet test`: **N correctas** (M de antes + K nuevas). Qué cubren las nuevas.

## 2. E2E
Corridas, con su duración:
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | N en verde | |
| 2 | `--veces=3` sobre `e2e/…` | | |

Archivos nuevos o modificados, y capturas de referencia: cuáles cambiaron (revisadas una por una) o por qué una pantalla
nueva no tiene captura.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió: diff (`git diff <commit>`), tabla STRIDE. Revisó: … Encontró: F-xx o
«nada» (y qué revisó).

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto, base). Roles usados. Pantallas: claro/oscuro, 1280/390 px.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|

### Contra el código anterior
`npm --prefix web run e2e:regresion -- <commit> e2e/…`: qué pruebas fallan con el código anterior y por qué.

### Lista de comprobación
(La de [verificacion.md](../../verificacion.md), marcada.)

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md): [R-NNN-1](../../riesgos.md#nnn), … (o «ninguno»).
