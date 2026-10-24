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
- Flujo: `spec.md` → `plan.md` (contrato y STRIDE) → `tasks.md` → implementar → `verify.md`.
- Toda capacidad nueva es **genérica del motor** y se ejerce con metadatos (p. ej. en un paquete de `modules/`).
- Cambios a un módulo instalado: nueva entrada en `upgrades` del paquete (no editar el plan base).
- Cada bug encontrado se vuelve prueba **y se verifica que la prueba falla con el código anterior**.

## Comandos
```bash
dotnet test                       # unitarias (núcleo de seguridad)
npm --prefix web run e2e          # E2E Playwright sobre base desechable ErpAi_E2E
npm --prefix web run e2e:report   # reporte con video/trace de fallas
npm --prefix web run build        # SPA (la API la sirve desde web/dist)
```
Servidor de desarrollo: `.claude/launch.json` → `erp-api` (http://localhost:5080, base `ErpAi` en LocalDB).
**Detenerlo antes de compilar o correr E2E**: bloquea `Erp.Api.exe`.

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

## Antes de cada push (repositorio público)
- `git diff --cached` sin secretos: contraseñas reales, API keys (la de Anthropic se captura en la app, cifrada; nunca en archivos), tokens.
- Sin artefactos: `bin/`, `obj/`, `node_modules/`, `web/dist/`, `web/e2e/.auth|.results|.report`, `*.tsbuildinfo`.
- Credenciales de desarrollo: el usuario `admin` es del dueño; no cambiarla ni escribirla en archivos.

## Commits
Mensaje en español, describiendo el qué y el porqué, terminando con:
`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
