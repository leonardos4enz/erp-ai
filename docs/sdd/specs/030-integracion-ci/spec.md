# Spec 030 — Integración y CI

**Estado:** Implementado · **Depende de:** 012, 021, 026, 028, 029 · **Origen:** la
[retrospectiva 001](../../retrospectivas/001-specs-001-026.md) §5 (P5, P8, P9 y P14), planeada en el
[roadmap](../../roadmap.md) desde la 028; riesgos R-028-2, R-028-3 y R-029-12 diferidos a esta spec.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | P5 · `tests/Erp.Tests` no abre SQL (solo existe ese proyecto en `ErpAi.slnx`) | Toda prueba de concurrencia o de falla parcial vive en Playwright (`endurecimiento.spec.ts` H3, `endurecimiento-2.spec.ts` H3, `pendientes.spec.ts` H6), se escribe con `Promise.all` y `sqlcmd` a mano, y tarda lo que tarda levantar el navegador y la suite serial |
| 2 | P5 · `api-security`, `endurecimiento`, `endurecimiento-2` y `pendientes` | ~45 pruebas que no abren una página corren en la suite E2E: dependen del orden de los archivos (Compras e Inventario los instala otro archivo), levantan instancias extra con `spawn` en puertos +2/+4 y usan la base compartida |
| 3 | P8 · no existe `.github/workflows` | La segunda corrida completa la hace la sesión siguiente (028); `npm audit` y `NuGetAudit` solo se vigilan si alguien compila |
| 4 | P14 · la SPA no tiene pruebas unitarias | `layoutEdit.ts`, `workflowEdit.ts`, el constructor de condiciones, el autocompletado de expresiones y la comparación canónica de la 026 solo se prueban por E2E (026 F-06 fue intermitente) |
| 5 | P9 · `helpers.ts` solo vigila errores de consola y CSP | Las lecciones repetidas (absoluto que escapa ×3, error fuera de la vista ×3, foco en `body` ×3, pantalla sin captura ×2) siguen siendo texto en `CLAUDE.md` |
| 6 | R-028-2 / R-028-3 | La suite no se fragmenta y 20 archivos seriales no toleran datos existentes con `E2E_INDEPENDENT=1` |
| 7 | R-029-12 | La carrera entre un plan y la instalación de un módulo (huella comparada con el candado del modelo) no tiene prueba |

## Principio
Ninguna capacidad nueva de negocio: es infraestructura de pruebas y de entrega. Las pruebas de integración ejercen el
motor **por su API real** (la misma composición `ErpApp.AddErp`/`MapErpRoutes` de la 029, sobre `WebApplicationFactory`) y
preparan sus datos con metadatos (Plan de Cambios y módulos de `modules/`), nunca con código por módulo.

## Historias

**H1 — Arnés de integración con SQL real (P5).** Como *sesión que escribe una spec*, quiero probar concurrencia y fallas
parciales contra SQL Server sin navegador, para que cada fila «Concurrencia» y «Fallas parciales» del STRIDE tenga su
prueba en segundos.
- CA1: `tests/Erp.IntegrationTests` levanta la API con `WebApplicationFactory` y una **base propia por clase**
  (`ErpAi_<clase>_<azar>_IT`); el nombre se valida (`[A-Za-z0-9_]`, termina en `_IT` o `_E2E`, nunca `ErpAi`) antes de
  crear o borrar, y la base se borra al terminar la clase.
- CA2: `Concurrently(n, fn)` lanza n peticiones a la vez (con una barrera, no en serie) y hay aserciones de invariante
  reutilizables: `recibido ≤ pedido`, una sola ejecución, saldo ≥ 0, folios únicos y consecutivos.
- CA3: inyección de fallas entre dos pasos: una restricción `CHECK` temporal (la técnica de la 021) o retener una fila
  desde otra conexión, con limpieza garantizada aunque la prueba falle.
- CA4: corre en paralelo con la suite E2E de la misma carpeta sin chocar (base y puertos propios; TestServer sin puertos).

**H2 — Pruebas de API fuera de Playwright y regresiones contra el código anterior (P5).** Como *dueño*, quiero que las
pruebas que no usan la UI corran en integración, para que la suite E2E pruebe pantallas y la de integración, reglas.
- CA1: las pruebas sin página de `api-security`, `endurecimiento`, `endurecimiento-2` y `pendientes` pasan a integración
  con los mismos títulos (historia y criterio) y se borran de la suite E2E; lo que sí usa la página se queda.
- CA2: las regresiones de 012-3 (sobre-recepción con confirmaciones simultáneas; cancelar mientras se confirma) y 021-3
  (trabajo asíncrono que deja efectos sin marcar éxito o se ejecuta dos veces) **fallan con el código anterior**
  (`cd81760` y `f6c5445`) con un script repetible (`npm --prefix web run it:regresion -- <commit> <filtro>`).
- CA3: R-029-12: un plan y una actualización de módulo simultáneos: gana uno y el otro se rechaza con su motivo (400 o
  409, nunca 500); el modelo queda como lo dejó el ganador.

**H3 — CI en GitHub Actions (P8).** Como *dueño de un repositorio público*, quiero que cada push se compruebe solo, para
que la segunda corrida completa no dependa de la sesión siguiente.
- CA1: en cada push y *pull request*: unitarias (`dotnet test tests/Erp.Tests`), `npm --prefix web run build`, Vitest y
  `npm audit --omit=dev --audit-level=high` (NuGetAudit ya rompe el build con NU1902–NU1904).
- CA2: en `main` (y a mano): integración y la suite E2E completa con SQL Server, con la contraseña del administrador y la
  clave de firma generadas por corrida; ningún secreto en el repositorio ni en la configuración del workflow.
- CA3: el reporte de Playwright (video y *trace*) se guarda como artefacto cuando la suite falla.
- CA4: `verificacion.md` y `CLAUDE.md` dicen que CI es la segunda corrida completa y qué hacer si falla (la spec se
  reabre antes de abrir la siguiente).

**H4 — Pruebas unitarias de la SPA (P14).** Como *sesión que cambia el modelador*, quiero probar su lógica sin
navegador, para que los casos combinatorios no dependan de Playwright.
- CA1: `npm --prefix web test` (Vitest, sin DOM) cubre `layoutEdit.ts`, `workflowEdit.ts`, el constructor de condiciones,
  el autocompletado de expresiones y la comparación canónica de la 026; corre en CI.
- CA2: 026 F-03 y F-06 tienen pruebas deterministas que fallan con la lógica anterior a su corrección.

**H5 — Guardias de la suite (P9).** Como *dueño*, quiero que las lecciones repetidas fallen solas en cualquier prueba
de UI, para no depender de que alguien recuerde una línea de `CLAUDE.md`.
- CA1: el documento no tiene desborde por un absoluto que escapa (más ancho o alto que su contenido visible).
- CA2: tras una respuesta 4xx a una escritura, el mensaje está en un `role="alert"` (o `alertdialog`) **dentro de la
  vista**.
- CA3: tras cerrar un diálogo o recibir un rechazo, el foco no cae en `body`.
- CA4: una prueba lista las pantallas de la SPA sin captura de referencia ni excepción declarada y falla.
- CA5: cada guardia falla con el código anterior a su hallazgo de origen (§1.3 de la retrospectiva).

**H6 — Riesgos de la suite (R-028-2, R-028-3).** Como *dueño*, quiero saber si la suite se puede fragmentar y repetir
de forma independiente, o por qué todavía no.
- CA1: R-028-2 y R-028-3 quedan resueltos o con su estado y razón actualizados en `riesgos.md`.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6, no) · más de ~2 500 líneas de
   producto estimadas (no: casi todo es código de pruebas y configuración; producto ≈ 0) · seguridad o concurrencia junto
   con UI nueva (no hay UI nueva) · más de una pantalla principal (0). → **No se divide.**
2. **Runner de CI:** `windows-latest` (Windows Server 2025) trae LocalDB (componente
   `Microsoft.VisualStudio.Component.SQL.LocalDB.Runtime`), `sqlcmd`, .NET 10 y Node 22 (comprobado en el
   [README de la imagen](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md),
   2026-10-25). Se elige sobre un contenedor en `ubuntu-latest` porque: (a) las capturas de referencia son de Chromium en
   Windows (`snapshotPathTemplate` sin plataforma) y en Linux fallarían todas; (b) la suite y la integración corren igual
   que en local (Integrated Security), sin un segundo camino de autenticación SQL que probar. **Consecuencia:** no se agrega
   autenticación SQL a la cadena de conexión ni a `run.mjs`/`instancia.mjs` (quedaría sin prueba); si algún día se usa un
   contenedor, es su propia tarea. Lo rápido (unitarias, build, Vitest, audit) corre en `ubuntu-latest`.
3. **Qué se mueve:** todas las pruebas sin `page` de los cuatro archivos. `api-security.spec.ts` y
   `endurecimiento.spec.ts` desaparecen (ninguna usa la UI); en `endurecimiento-2.spec.ts` y `pendientes.spec.ts` quedan
   las de UI con su preparación mínima por API.
4. **Regresiones contra el código anterior en integración:** un script copia `tests/Erp.IntegrationTests` a un worktree
   del commit y corre el filtro pedido. El arnés solo usa la API HTTP y `Program` (público desde la 001:
   `public partial class Program`) como punto de entrada, para compilar contra código anterior.
5. **Fragmentar la suite (R-028-2):** no en esta spec. Con las pruebas de API fuera, la suite E2E corre en un solo
   trabajo de CI dentro del límite de 6 h de un runner; fragmentar exige que cada archivo instale lo que necesita y que
   las capturas del menú no dependan de los demás. Se mide la duración en CI y el riesgo se actualiza con ese dato.
6. **Fuera de alcance:** D-06 (R-029-4), D-07 (R-029-5), R-021-5 y R-021-6: van a una spec propia antes de Ventas
   (031), sin renumerar.

## Fuera de alcance (anotado en el roadmap)
- Tareas programadas que se saltan registros (D-06, R-029-4) e importación «1,5» → 15 (D-07, R-029-5).
- `ForwardedHeaders` (R-021-5) y bloqueo de navegación de la SPA (R-021-6).
- Autenticación SQL para un SQL Server en contenedor (decisión 2).
- Fragmentar la suite E2E (decisión 5).
