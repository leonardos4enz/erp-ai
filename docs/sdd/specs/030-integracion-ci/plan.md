# Plan 030 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni metadatos nuevos. Las pruebas de integración crean bases desechables `ErpAi_<clase>_<8 hex>_IT` (una por
clase) con el `Bootstrap` de siempre y las borran al terminar.

## 2. Contrato de la API
Sin rutas nuevas ni cambios de capacidad (`docs/sdd/rutas.md` no cambia). Cambios de producto, mínimos y sin efecto en la
API:
| Cambio | Por qué |
|---|---|
| `web/src/lib/canonical.ts` (`clean`, `sameValue`), antes dentro de `pages/modeler/sections/common.tsx` | Probar la comparación canónica (026 F-03) sin React; `common.tsx` la reexporta |
| `completeAfterChoice` en `lib/exprComplete.ts`; `ExprInput.tsx` la usa al reabrir la lista | Probar 026 F-06 (recalcular con el texto actual) como función pura |

## 3. SPA
Sin pantallas nuevas. Las guardias viven en `web/e2e/helpers.ts` (fixture automático `guard` + opción `guardias`):
- `desborde`: al terminar cada prueba, si el documento se desplaza (`scrollWidth/Height > client…`) y deja de hacerlo al
  ocultar todos los elementos `position: absolute`, falla y nombra los absolutos que quedan fuera de la vista.
- `alerta` y `foco`: cada respuesta 4xx a una escritura (`fetch`/`xhr` no GET) se revisa 800 ms después: debe haber un
  `role="alert"`/`alertdialog` con texto cuyo punto central sea el propio elemento (`elementFromPoint`), y el foco no debe
  estar en `body`. Un script de inicio observa los diálogos (`role="dialog"`, `dialog[open]`) y anota si al cerrarse el
  foco queda en `body`.
- Una prueba que provoca a propósito la situación la apaga con `test.use({ guardias: { … } })` y un comentario.
- `web/src/__tests__/pantallas.test.ts` (Vitest): cada `<Route path>` de `App.tsx` tiene captura o razón escrita.

## 4. Pruebas
- **Integración** (`tests/Erp.IntegrationTests`, nuevo): `Harness/ErpHost.cs` (fixture por clase, `WebApplicationFactory<Program>`
  con `UseSetting` para que `AddErp` lea la configuración al registrar servicios; `SiblingAsync` para una segunda instancia
  sobre la misma base), `Harness/Client.cs` (sesión por cookie, `ApplyPlanAsync`, `InstallModuleAsync`),
  `Harness/Concurrency.cs` (`Concurrently`, `WaitForAsync`, `Invariant`, `Faults.FailWhenAsync` con CHECK temporal y
  `Faults.HoldAsync` con una transacción abierta en otra conexión). Clases: `HarnessTests`, `ApiSecurityTests`,
  `Hardening012Tests`, `Hardening021Tests`, `PendingTests`, `ModuleRaceTests`.
- **Contra el código anterior:** `npm --prefix web run it:regresion -- <commit> <filtro>` (worktree en el commit con el
  proyecto de integración copiado; resumen en `tests/Erp.IntegrationTests/TestResults/regresion-<sha>.json`).
- **Vitest** (`web/src/lib/__tests__/*.test.ts`, `web/src/__tests__/pantallas.test.ts`): `npm --prefix web test`.
- **E2E:** se borran `api-security.spec.ts` y `endurecimiento.spec.ts`; `endurecimiento-2.spec.ts` y `pendientes.spec.ts`
  conservan lo de UI; `capacidades.spec.ts` recibe la prueba de la cabecera `Server` (Kestrel). Archivos modificados para
  `--veces=3`: `helpers.ts` (afecta a todos: va en la corrida completa), `endurecimiento-2`, `pendientes`, `capacidades`.
- **CI** (`.github/workflows/ci.yml`): trabajo rápido en `ubuntu-latest`; integración y E2E en `windows-2025` solo en `main`
  o a mano.

## 5. STRIDE
**Las 8 filas son obligatorias.** Si una no aplica, se dice por qué. Cada mitigación cita su prueba (archivo › título);
sin prueba, la amenaza va a [riesgos.md](../../riesgos.md) con su ID (`R-NNN-k`) y no cuenta como mitigada.

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones de plan, acciones de automatización, opciones de OData o caminos de módulo/paquete/asistente agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta ni operación nueva (inventario sin cambios). Entradas nuevas **de infraestructura**: el workflow de CI (eventos `push`, `pull_request`, `workflow_dispatch`) y los scripts `it:regresion` (commit y filtro) | `RouteInventoryTests › El_inventario_versionado_coincide_con_las_rutas` (sin diff en `rutas.md`); el filtro y el commit van como argumentos sin shell (`regresion.mjs`, revisión de código: amenaza baja, la corre quien ya tiene el repositorio) |
| Caminos alternos | ¿Por qué otros caminos se llega al mismo dato o efecto? | Un `pull_request` desde un fork ejecuta el workflow con código ajeno: solo corre el trabajo rápido, con `permissions: contents: read`, sin secretos (no hay ninguno) y sin `pull_request_target`. El trabajo completo solo corre en `main` o a mano | Revisión del workflow en VERIFY (adversarial); riesgo **R-030-2** (sin prueba automática del workflow fuera de GitHub) |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | Las pruebas movidas siguen comprobando la bitácora, `__label` y el buscador con usuarios restringidos; el JSON se compara sin escapar acentos (`JsonText.Json`) para que un `DoesNotContain` no pase en falso | `Hardening012Tests › H1_CA1_CA2_CA3_…`, `› H6_CA1_CA2_…` |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? | Sin cambios de producto; las pruebas de la 021 (nadie otorga lo que no tiene, el beneficiario no se aprueba) pasan a integración con una segunda instancia que exige aprobación de seguridad | `Hardening021Tests › H1_CA1…`, `› H1_CA4…`, `› H1_CA5_el_beneficiario_no_aprueba_su_propio_cambio` |
| Concurrencia | ¿Qué invariante entre filas protege? ¿10 peticiones a la vez? | `Concurrently` con barrera: recibido ≤ pedido (10 confirmaciones), una sola ganadora entre cancelar y confirmar, existencia y saldo ≥ 0 (10 salidas y 10 cargos), folios únicos y consecutivos en la importación, cada trabajo asíncrono una vez con **dos instancias** sobre la misma bandeja, plan contra instalación de módulo | `Hardening012Tests › H3_CA1…`, `› H3_CA3…` (fallan con `cd81760`); `PendingTests › H6_CA1…` ×2, `› H3_CA2_CA3…`; `Hardening021Tests › H3_CA1_con_dos_instancias…`; `ModuleRaceTests › H2_CA3…` (invariante; la ventana no se fuerza: **R-029-12** sigue abierto); `HarnessTests › H1_CA2_Concurrently_lanza_todas_a_la_vez` |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | Inyección con CHECK temporal (marcar el éxito falla tras confirmar los efectos) y con una transacción retenida en otra conexión (importación detenida tras su primer folio); limpieza en `await using`, también si la prueba falla. Solicitud atorada en `applying` y *lease* vencido con intentos agotados | `Hardening021Tests › H3_CA1_los_efectos_y_el_estado_correcto…`, `› H3_CA4…` (fallan con `f6c5445`); `› H8_CA1…`; `PendingTests › H3_CA1…`; `HarnessTests › H1_CA3_una_falla_inyectada_se_deshace…` |
| Límites | Tamaño, paginación, topes, tasa | El servidor de tokens conserva su límite (30/min por dirección) en la instancia «producción»; 10 001 movimientos en el acumulado; el arnés sube los demás límites como la suite E2E. CI: `timeout-minutes` (20 y 90) y `cancel-in-progress` fuera de `main` | `Hardening012Tests › Spec018_H2_CA5…`; `PendingTests › H2_CA1…` |
| Semántica | Nulos y vacíos, monedas, fechas con zona, redondeo | La comparación canónica ignora nulos (026 F-03); moneda de la orden contra moneda base y sin tipo de cambio | `canonical.test.ts › 026 F-03…` (falla al quitar el filtro de nulos); `PendingTests › H1_CA1_CA2_CA4…`, `› H1_CA3…` |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S/E** Un fork abre un *pull request* que cambia el workflow para leer secretos o escribir en el repositorio | Medio | No hay secretos; `permissions: contents: read`; disparador `pull_request` (no `pull_request_target`), que corre con el token de solo lectura del fork | Revisión adversarial del workflow (VERIFY); **R-030-2** |
| **T** Una prueba de integración apunta a la base del dueño (`ErpAi`) y la borra | Alto | `ValidateDatabase` exige `[A-Za-z0-9_]` y sufijo `_IT`/`_E2E`, rechaza `ErpAi`; el nombre lleva un sufijo aleatorio y se valida antes del `DROP` | `HarnessTests › H1_CA1_solo_acepta_bases_desechables` (5 casos, con inyección en el nombre) |
| **I** La contraseña del administrador de prueba o la clave de firma quedan en el repositorio o en los registros de CI | Medio | Se generan por corrida (`RandomNumberGenerator` en el arnés, `run.mjs` en E2E); el workflow no las imprime | Revisión de `git diff --cached` antes del push; `HarnessTests › H1_CA1_la_base_es_propia…` (la clase arranca con la suya) |
| **D** Una prueba de concurrencia deja una restricción CHECK o una transacción abierta y rompe las demás | Bajo | `await using` con deshacer garantizado; cada clase tiene su base | `HarnessTests › H1_CA3_una_falla_inyectada_se_deshace_aunque_la_prueba_falle` |
| **R** CI «en verde» sin haber corrido la suite completa (p. ej. el trabajo completo se salta) | Medio | El trabajo completo depende del rápido y corre en cada push a `main`; `verify.md` registra el enlace de la corrida | Primera corrida en GitHub (VERIFY); **R-030-2** |
