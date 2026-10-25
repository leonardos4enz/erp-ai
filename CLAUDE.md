# ERP AI — guía para sesiones de IA

ERP dirigido por metadatos sobre SQL Server. Catálogos, documentos, fórmulas, reglas y procesos
se declaran como datos; el motor genera tablas, API y pantallas. Idioma del proyecto: **español**.

## Antes de escribir código, leer
1. [Constitución](docs/sdd/constitution.md): principios no negociables (seguridad, sin código por módulo, UX).
2. [Metodología SDD](docs/sdd/README.md) y [fase VERIFY](docs/sdd/verificacion.md).
3. [Roadmap](docs/sdd/roadmap.md): qué spec sigue y por qué.
4. Las specs previas relevantes en `docs/sdd/specs/` (sobre todo 002: contrato de API y lenguaje de expresiones).

## Reglas de trabajo
- **Una spec por sesión.** Esta sesión trabaja solo su spec, de SPECIFY a VERIFY, commit y **push**.
  La siguiente spec se abre en una sesión nueva.
- **Push obligatorio al cerrar cada spec** (decisión del dueño, 2026-10-24): con las suites en verde y la revisión de
  «Antes de cada push», se sube a `origin/main`. No se abre una spec nueva mientras `git rev-list --count origin/main..main`
  no dé 0; la sesión que abre una spec lo comprueba primero y, si hay commits sin subir, los revisa y los sube antes.
- Flujo: `spec.md` → `plan.md` (contrato y STRIDE) → `tasks.md` → implementar → `verify.md`, con las
  [plantillas](docs/sdd/plantillas/) (desde la 028).
- **STRIDE con 8 filas obligatorias** (plantilla de `plan.md`): entradas, caminos alternos, lectura de datos, delegación,
  concurrencia, fallas parciales, límites y semántica. Cada mitigación cita su prueba (archivo › título); sin prueba, es un
  riesgo en [riesgos.md](docs/sdd/riesgos.md), no una mitigación. «Revisión de código» no basta para una amenaza alta.
- **Revisión adversarial en VERIFY:** un subagente sin el contexto de la sesión lee el diff y la tabla STRIDE (no las
  conclusiones del plan) para romper la seguridad y la concurrencia. Sus hallazgos son F-xx como cualquier otro.
- **Una corrida completa al cerrar**, más los archivos nuevos o modificados con `--veces=3` (no dos completas). Al
  abrir la spec siguiente, su primera acción tras el `rev-list` es correr la suite completa: es la segunda corrida de la
  anterior (hasta que CI la haga, spec 030). Si falla, se corrige primero lo anterior.
- **Alcance congelado en VERIFY:** un pedido nuevo entra solo si bloquea el uso; si no, va al roadmap como spec siguiente.
- **Dividir la spec** (se decide en CLARIFY) si cumple dos de: más de 6 historias, más de ~2 500 líneas de producto,
  seguridad o concurrencia junto con UI nueva, más de una pantalla principal.
- **Auditoría externa de solo lectura** (sesión aparte, contexto fresco) cada 5 specs o tras una spec que agregue un punto
  de entrada, un trabajo asíncrono o delegación de privilegios. El [roadmap](docs/sdd/roadmap.md) indica la siguiente.
- Toda capacidad nueva es **genérica del motor** y se ejerce con metadatos (p. ej. en un paquete de `modules/`).
- Cambios a un módulo instalado: nueva entrada en `upgrades` del paquete (no editar el plan base).
- Cada bug encontrado se vuelve prueba **y se verifica que la prueba falla con el código anterior**.

## Comandos
```bash
dotnet test                       # unitarias (núcleo de seguridad)
ACTUALIZAR_RUTAS=1 dotnet test --filter RouteInventory   # regenera docs/sdd/rutas.md tras agregar o cambiar una ruta (029)
npm --prefix web run e2e          # E2E Playwright sobre base desechable (ver abajo)
npm --prefix web run e2e -- e2e/<archivo>.spec.ts --veces=3   # lo nuevo, 3 veces sobre base nueva
npm --prefix web run e2e:regresion -- <commit> [e2e/<archivo>.spec.ts]   # pruebas nuevas contra el código anterior
npm --prefix web run revision     # instancia para la revisión exploratoria (base propia, puerto libre)
npm --prefix web run e2e:report   # reporte con video/trace de fallas
npm --prefix web run build        # SPA (la API la sirve desde web/dist)
node docs/sdd/metricas.mjs        # tabla de métricas de los verify.md (retrospectivas)
```
La suite usa una base y puertos **por carpeta**: la principal, `ErpAi_E2E` y 5090–5099; un worktree, `ErpAi_<worktree>_<huella>_E2E`
y su propio bloque. Varias sesiones pueden correr su suite a la vez si están en carpetas distintas; en la **misma**
carpeta, la segunda se detiene con un aviso (candado por base) en lugar de borrarle la base a la primera. `E2E_DB` y `E2E_PORT_BASE` las fijan a mano.

Servidor de desarrollo: `.claude/launch.json` → `erp-api` (http://localhost:5080, base `ErpAi` en LocalDB).
**Detenerlo antes de compilar o correr E2E en esta misma carpeta** (bloquea `Erp.Api.exe`); las suites de otros worktrees
compilan en su propia carpeta.

## Lecciones aprendidas (no repetir)
- **PowerShell 5.1 corrompe UTF-8** con `Get-Content`/`Set-Content`. Usar las herramientas de edición,
  `[IO.File]::WriteAllText(..., UTF8 sin BOM)` o Bash. No usar `-Encoding utf8NoBOM` (no existe en 5.1).
- En `cmd`/`npm` un `|` dentro de argumentos se interpreta como tubería: filtrar E2E por archivo, no con `-g "a|b"`.
- **Playwright es más hábil que una persona:** hace clic en elementos recortados. Antes de interactuar con
  listas o menús emergentes, verificar con `elementFromPoint` que sean visibles (ver `pick()` en `compras.spec.ts`).
- Revisar **cada captura de referencia** antes de aprobarla: una referencia sin revisar protege el bug.
- Los `.sr-only` absolutos escapan de contenedores con scroll sin `position: relative`.
- El backend debe cumplir el contrato del plan al pie de la letra (p. ej. `/compute` → `{ values }`).
- En expresiones, comparar contra nulo da *desconocido*: una regla sobre un campo vacío no se viola.
- En expresiones un vacío vale 0 en la aritmética y en `ROUND`: «sin dato» se protege con `IF(ISBLANK(...), NULL, ...)` (022 F-04).
- Una lección que se repite se vuelve **guardia de la suite**, no otra línea aquí (absolutos que escapan: 3 veces; error
  fuera de la vista: 3; foco en `body`: 3; ver la spec 030).
- La regla de un camino debe valer en **todos**: bitácora, etiquetas, buscador, reportes, exportación, OData, webhooks,
  importación, paquetes, reversión, impresión y asistente (012, 021).
- Toda operación «revisar → aplicar» o «abrir → guardar» lleva huella o versión (012-5, 021-2).
- Un trabajo asíncrono marca su éxito dentro de su transacción y comprueba que el *lease* sigue siendo suyo (021-3).
- Nadie otorga, aprueba ni hereda lo que no tiene; el beneficiario no se aprueba (021-1).
- Al borrar un worktree, quitar antes la unión de `node_modules` (013 F-03); `e2e:regresion` ya lo hace.

## Antes de cada push (repositorio público)
- `git diff --cached` sin secretos: contraseñas reales, API keys (la de Anthropic se captura en la app, cifrada; nunca en archivos), tokens.
- Sin artefactos: `bin/`, `obj/`, `node_modules/`, `web/dist/`, `web/e2e/.auth|.results|.report`, `*.tsbuildinfo`.
- Credenciales de desarrollo: el usuario `admin` es del dueño; no cambiarla ni escribirla en archivos.

## Commits
Mensaje en español, describiendo el qué y el porqué, terminando con:
`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
