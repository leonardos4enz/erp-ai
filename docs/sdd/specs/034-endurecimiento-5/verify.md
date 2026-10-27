# Verify 034 — Endurecimiento 5: nadie actúa sobre quien tiene más, y el catálogo de Studio con los permisos exactos

## Métricas
- inicio: 2026-10-27 16:15
- cierre: 2026-10-27 17:35
- unitarias: 758 (+19: 649 de Erp.Tests, +6; 109 de integración, +12; además Vitest 38, +2)
- e2e: 374 (+0: la spec no tiene pantalla; cambian jerarquia, asistente y compras)
- corridas completas: 2 (7 min 46 s antes de la revisión adversarial; 7 min 40 s la final con sus correcciones)
- corridas parciales: 9 (--veces=3 en modo lento sobre h5-h6-seguridad, jerarquia y asistente; Hardening034Tests y StudioTests ×3; integración completa ×2; mutaciones de F-01, F-03 y F-05; iteración)
- contra el código anterior: 3 (it:regresion ad0534b Hardening034Tests y StudioTests.A0 con --sin; it:regresion ad0534b de H1_CA2 y H3_CA3 solas; mutaciones de las correcciones de la revisión)
- hallazgos: 0/2/5
- categorías: seguridad=4, concurrencia=1, pruebas=2
- revisión adversarial: 7 hallazgos (0 altos, 2 medios, 5 bajos)
- riesgos: R-034-1, R-034-2, R-034-3, R-034-4 (y R-008-4, R-009-1, R-011-1, R-011-2, R-015-3, R-016-3, R-021-4, R-032-6, R-033-5 actualizados)

## 0. Apertura
`git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main` (`ad0534b`, «captura del explorador sin
depender del momento») en verde. Los hallazgos de la auditoría 6 se reconfirmaron en el código antes de escribir la spec
(A-01: `RequireGrantableRolesAsync` sale con `changed.Length == 0`; R-009-1: `EnvironmentOptions.Create` sin validación;
A-02 a A-07 en `src/Erp.Api/Studio/*` y `AuditFilter.cs`). Roadmap renumerado: pantalla de Studio 035, MCP 036 (auditoría
7 al cerrarla), Ventas 037, CFDI 4.0 038; referencias corregidas en ADR-0008, `riesgos.md` y la spec 033 (spec, plan y
verify). CLARIFY: un solo criterio de división (7 historias); la estabilidad de CI entra (no es código de producto:
~340 líneas de producto y ~600 de pruebas e infraestructura).

## 1. Pruebas unitarias e integración
`dotnet test`: **758 correctas**: 649 de `tests/Erp.Tests` (643 + 6) y 109 de `tests/Erp.IntegrationTests` (97 + 12).
- `Hardening5Tests` (5): toda constante de `AuditActions` está en exactamente una lista de `AuditFilter`; ninguna escritura
  de bitácora usa un texto suelto (recorre `src/Erp.Api`); `Writes` por el efecto declarado (también un GET); `BearerScope`
  niega lo mismo que el middleware; prueba del alcance de R-008-4 («1,5» → 15).
- `EnvironmentTests › Con_aprobacion_y_sin_clave_no_arranca`; `RouteInventoryTests`: un GET solo declara «bitácora».
  Cambiadas: `HardeningTests` (la condición de la bitácora va por lista) y las que creaban «producción» sin clave.
- `Hardening034Tests` (8, SQL real): A-01 por API (contraseña, desactivar, unidad y, tras la revisión, roles; control sobre
  quien no tiene más y por el superusuario) y por aprobación (`canApprove` falso y 403 al gestor; otro superusuario sí);
  R-009-1 al arrancar una instancia hermana sin clave; A-02 (cuatro herramientas aisladas `denied` sin preparar la copia);
  A-03 (token `data.read` hacia `POST /api/data` y hacia el modelo, clave de API hacia el explorador; por el runner y, tras
  la revisión, en la copia); A-04 (recorre las 67 GET como administrador y compara el rastro con el efecto); R-033-5 (10 a
  la vez con tope 3: 3 pasan y 7 dan 429; otra persona no comparte el tope); A-07 (auditor de alcance «propios»: no ve la
  impresión ajena, las tareas de un catálogo que no lee ni una acción sin clasificar; sí lo suyo, con la plantilla).
- `StudioTests` (+4): cancelar cobra lo reservado; una sesión vencida lo cobra; una copia que falla no se vuelve a crear;
  un error del proveedor no se cobra (F-03).
- Integración completa dos veces (42 s y 41 s) y `Hardening034Tests` + `StudioTests` tres veces seguidas (34–35 s, sin fallas).

`npm --prefix web test` (Vitest): **38 correctas** (+2: `e2eGuardia.test.ts`, que se prueba con una muestra y recorre
`web/e2e`; con el `asistente.spec.ts` anterior encuentra 8 formas prohibidas).

### Contra el código anterior
| Comando | Resultado |
|---|---|
| `npm --prefix web run it:regresion -- ad0534b "FullyQualifiedName~Hardening034Tests\|FullyQualifiedName~StudioTests.A0" --sin=Hardening034Tests.Credential.cs` | **10 de 10 fallan**: el gestor cambia la contraseña del administrador (200); sin clave arranca; la herramienta aislada prepara la copia («error inesperado» en lugar de `denied`); las 10 exportaciones despachadas pasan; el auditor ve la impresión ajena; la sesión cancelada y la vencida pierden la reserva; la copia que falla se reintenta. H1_CA2 y H3_CA3 fallaron además de rebote (la primera prueba le cambió la contraseña al administrador) |
| ídem, solo `H1_CA2` y `H3_CA3` | **2 fallan por su motivo**: `canApprove` verdadero para el gestor; «GET que dejan rastro sin declararlo: `/api/data/{entity}/export` (200), `/api/data/{entity}/{id:long}/pdf` (200)» |
| `Hardening034Tests.Credential.cs` | no compila contra `ad0534b`: la credencial (`BearerCredential`, `ToolContext.Credential`) no existía; falla por definición (por eso `--sin`, opción nueva de `it:regresion`) |
| Mutaciones tras la revisión: sin la regla de roles (F-01), con el nombre de la URL en `data.print` (F-05), cobrando la reserva ante un error del proveedor (F-03) | las 3 pruebas fallan; restaurado, pasan |
| `jerarquia.spec.ts › H5 CA1` | la prueba anterior fallaba al correr después de `h5-h6-seguridad.spec.ts` (R-032-6, también con `71246b2`); la nueva, en ese orden, pasa 3 veces en modo lento |

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `--veces=3` (modo lento obligatorio) sobre `h5-h6-seguridad`, `jerarquia` y `asistente` | 3 × 45 en verde | 4 min 29 s |
| 2 | Completa, antes de las correcciones de la revisión | 374 en verde | 7 min 46 s |
| 3 | Completa, final | 374 en verde | 7 min 40 s |

Archivos modificados: `jerarquia.spec.ts` (H5 CA1 filtra por acciones de datos), `asistente.spec.ts` (receptores con
`marca()`/`siguiente()` y dos `.last()` con razón), `compras.spec.ts` (un `.last()` con razón), `helpers.ts` (modo lento,
`tras`, `expectToast`, `receptor`), `run.mjs` (`--lento`). Sin capturas nuevas ni cambiadas (sin pantalla).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff ad0534b` con los archivos nuevos) y la tabla STRIDE con
solo filas, preguntas y amenazas (sin las respuestas del plan); leyó código. Revisó además la herencia de roles
(`UserContext`, `CheckInheritance`: los roles administrativos no se heredan), el SQL del filtro de la bitácora
(precedencia, parámetros, superusuario, catálogos hijos), `Writes` y el despachador, `ExpireAsync` frente a un trabajador
vivo y `--lento` con los `page.route` de las pruebas: sin hallazgos ahí. Encontró:

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | seguridad | Un gestor agregaba o quitaba al administrador un rol sin capacidades: pasaba 021-1 y la regla nueva, y renovaba el sello (cierra todas sus sesiones; repetido, lo deja fuera). Sobre un modelador, le quitaba sus roles de datos | Cambiar roles cuenta como «actuar sobre» (`RequireActOnAsync` y `ActsOnUser`) | `Hardening034Tests › Un_gestor…` (mutación: falla sin la regla) |
| F-02 | Media | seguridad | `aislado_aplicar_plan` y la preparación de la copia despachaban sin credencial ni `caller` (partición = id del admin de la copia, el mismo que el admin real); `aislado_procesar_asincronas` no tomaba permiso | La credencial y la persona llegan a `ApplyAsync`; la preparación usa una partición propia; el runner mira la credencial antes de preparar la copia (sin ruta: se niega); procesar las asíncronas toma un permiso | `Hardening034Tests › H3_CA2…` (las dos herramientas `denied` sin preparar la copia) |
| F-03 | Baja | concurrencia | La reserva se anotaba antes del commit; un error del proveedor (clave mala) cobraba la reserva y llenaba el tope del día | `Reserved` tras el commit; `AssistantProviderException` no cobra | `StudioTests › F03_un_error_del_proveedor_no_cobra_la_reserva` (mutación: falla) |
| F-04 | Baja | seguridad | La regla mira las capacidades actuales: una contraseña puesta por un gestor sigue valiendo si después ascienden al usuario | Riesgo | [R-034-3](../../riesgos.md#034) |
| F-05 | Baja | seguridad | `data.print` guardaba el catálogo como venía en la URL: el filtro dependía de la intercalación sin mayúsculas | Nombre del modelo (`PrintResult.Entity`) | `Hardening034Tests › H4_CA1…` (mutación: falla) |
| F-06 | Baja | pruebas | A06 no cuenta intentos; H3_CA3 podía dar un falso positivo con un escritor en segundo plano; la guardia no veía receptores con sangría; la credencial no se probaba en la copia | H3_CA3 relee antes de acusar; guardia con sangría (muestra nueva); credencial en la copia (F-02); A06 queda como riesgo | `e2eGuardia.test.ts` (muestra con `pings`); [R-034-4](../../riesgos.md#034) |
| F-07 | Baja | pruebas | El reintento de CI sin tope propio podía agotar los 90 min y el reporte no se subía al cancelarse | `timeout-minutes: 25`, `continue-on-error` y subir con `failure() \|\| cancelled()` | sin prueba (CI) |

## 4. Revisión exploratoria
Sin pantalla nueva ni cambio visible en la SPA: no se levantó una instancia de revisión. Lo que una persona vería distinto
(el 403 al editar a quien tiene más capacidades, `canApprove` en la lista de solicitudes, la bitácora del auditor) se
probó por API en integración con usuarios reales de cada rol, como pide la lista de seguridad. El servidor de desarrollo no
se usó (la contraseña de `admin` es del dueño).

### Lista de comprobación
- [x] Un usuario sin permiso no llega por API directa (A-01, A-02, A-03, A-07: integración).
- [x] Cada camino alterno del STRIDE probado con un usuario sin permiso: API, aprobación, despachador (real y copia), bitácora.
- [x] Dos peticiones a la vez: 10 despachadas a la vez contra el límite.
- [x] La consola sin errores ni CSP (guardias de la suite en las corridas completas).
- [x] Hallazgos de un componente genérico, con sus hermanos: toda acción de bitácora (reflexión y recorrido de fuentes),
      toda GET (recorrido), toda herramienta aislada.
- [x] Alcance congelado: R-016-3 (tarea atorada) no cupo y queda con R-029-4; la firma asimétrica (R-034-1) y exigir cambio
      de contraseña al ascender (R-034-3) van a riesgos.
- [ ] Claro/oscuro, móvil, teclado: no aplica (sin pantalla).

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#034): R-034-1 (HMAC compartido), R-034-2 (contadores del despachador aparte),
R-034-3 (contraseña puesta antes de un ascenso), R-034-4 (A06 sin contar intentos). Cerrados: R-009-1 (la mitad sin
clave), R-011-2, R-015-3, R-021-4, R-032-6, R-033-5. Con prueba de su alcance: R-008-4, R-011-1 (reescrito para Studio).
Sin prueba: R-016-3 (va con R-029-4).

## 6. CI
La segunda corrida completa es la de CI: `f412bcf` en `main`, **verde** (`completed`/`success`, 16 min 46 s: unitarias, build, Vitest, auditoría de dependencias, integración y E2E completa en `windows-2025`).
