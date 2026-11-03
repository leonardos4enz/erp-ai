# Verify 053 — Matriz de autorización: núcleo

## Métricas
- inicio: 2026-11-02 17:40
- cierre: 2026-11-02 22:10
- unitarias: 1376 (+37: 849 de Erp.Tests, +9; 436 de integración, +28; Vitest 91, +0 con `pantallas` ampliado)
- e2e: 509 (+5 en `autorizaciones`; el canario recorre la ruta nueva)
- corridas completas: 2 de .NET (2 min 7 s; 2 min 12 s tras la revisión) y 2 E2E (12 min 30 s: 501 en verde y 3 fallas de `navegacion` por el menú nuevo; 15 min 12 s: 504 en verde); la siguiente completa es la CI del push
- corridas parciales: 10 (integración de `ApprovalTests` 6: iteración, tras la revisión y sin las correcciones de F-01, F-05 y F-06; E2E 3: `autorizaciones` con capturas nuevas, `compras` + `inventario` + `navegacion` con capturas de configuración, lo nuevo y lo modificado tres veces 2: la primera falló en la vuelta 1 porque `navegacion` en aislado necesita un catálogo sin grupo de otro archivo, la segunda con `h3-h4-datos` en verde)
- contra el código anterior: 2 (F-01, F-05 y F-06 sin su corrección en esta carpeta: fallan sus 3 pruebas; las pruebas de la matriz no existen en `9f98e6b`: el plan rechaza `approval`, desconocido)
- hallazgos: 2/7/7 (revisión adversarial F-01…F-16)
- categorías: seguridad=7, concurrencia=2, motor-api=5, UI/UX=1, pruebas=1
- revisión adversarial: 16 hallazgos
- riesgos: R-053-1, R-053-2, R-053-3, R-053-4, R-053-5, R-053-6, R-053-7, R-053-8, R-053-9

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0 (`9f98e6b`, spec 052). La CI de `9f98e6b` terminó en verde (`success`, 23:05 UTC;
anotado en el `verify.md` de la 052). CLARIFY con el dueño (2026-11-02): **se divide** (motor, matriz y panel en la 053; bandeja
global, exportar la bitácora, Acompañante y expresión libre a la 055); **solo el perfil autoriza**, también el superusuario;
**personas vivas** (se congelan regla y niveles); **falla cerrado** sin regla o sin niveles.

## 1. Pruebas unitarias e integración
`dotnet test`: **849 unitarias** (840 + 9) y **436 de integración** (408 + 28), en verde (2 min 12 s).
- Unitarias nuevas (`tests/Erp.Tests/ApprovalTests.cs`, 9): declaración válida y su vista previa; cada error del plan (catálogo
  compartido, estado inicial o inexistente, transiciones no `system` o que no salen solo del estado, iguales, monto no numérico,
  condición de un tipo no admitido o repetida, más de 5, autorizadores editables o de otro tipo, asignarlos en una transición,
  estado editable, otra entrada al estado autorizado, `when` en la salida); atajos desde una automatización de otro catálogo y
  desde una tarea; forma JSON omitida en nulo; elegir regla (prioridad, especificidad, antigüedad, condición vacía = cualquiera);
  vigencia inclusiva y monto vacío; saltos por rango (sin monto no se salta); huella de condiciones; quién revisa un cambio a la
  matriz (miembro agregado o quitado, otra empresa).
- Modificadas: `Hardening5Tests` (las acciones `approval.*` clasificadas) y `RouteInventory` regenerado (`docs/sdd/rutas.md`: 199
  rutas; `POST /api/data/{entity}/{id:long}/approval` con huella; `GET/POST/PUT /api/admin/approvals*`, usuarios, seguridad con
  aprobación de seguridad).
- Integración nueva (`tests/Erp.IntegrationTests/ApprovalTests.cs`, 28, SQL real): flujo con un nivel vacío que se salta al llegar
  y uno fuera de rango al empezar (la automatización de la salida corre con la persona como actor); prioridad; falla cerrado (sin
  regla y sin niveles); creador y dos niveles; superusuario fuera del perfil; nivel que solo tiene al creador espera; nadie cambia
  su propia pertenencia; 403 sin administrar usuarios; la matriz de otra empresa no se ve ni se usa; **visibilidad solo cuando le
  toca** (registro, lista, buscador, exportación, OData, impresión); baja y alta de personas al momento; reglas congeladas;
  reinicio por automatización (y la edición en espera es 409); salir por otra transición cancela y una al mismo estado no reinicia;
  nadie ejecuta la salida por la ruta de transiciones; el plan rechaza atajos; formulario, API e importación no escriben
  autorizadores ni el estado; quien está en el perfil y no ve el registro no autoriza; versión vieja 409 y rechazar pide motivo;
  bitácora con regla, nivel, perfil, persona y huella; **diez personas del mismo nivel a la vez: una decisión**; **autorizar
  mientras se quita a la persona** (candado retenido por otra conexión: orden serial); **falla la automatización de la salida:
  no queda la decisión**; la compuerta de seguridad (solicitud, el miembro agregado no aprueba, otro administrador sí); F-01,
  F-03/F-04, F-05 y F-06.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `autorizaciones` (capturas nuevas) | 6 en verde | 16 s |
| 2 | Completa | 501 en verde; 3 fallas de `navegacion` (H3 CA1 y las capturas de configuración: el elemento nuevo «Autorizaciones» del menú) | 12 min 30 s |
| 3 | `compras`, `inventario`, `navegacion` (capturas de configuración) | 43 en verde; 2 de `navegacion` dependen de catálogos que crean otros archivos (en la completa pasan) | 56 s |
| 4 | Completa (tras la revisión adversarial) | 504 en verde | 15 min 12 s |
| 5 | `--veces=3` (con `--lento`) sobre `compras`, `inventario`, `autorizaciones`, `canario`, `navegacion` | Vuelta 1: 2 de `navegacion` fallan (dependen de un catálogo sin grupo que deja otro archivo; no es de la spec) | 2 min 23 s |
| 6 | `--veces=3` (con `--lento`) sobre los de la fila 5 más `h3-h4-datos` | 70 × 3 en verde | 7 min 49 s |

- Nuevo: `web/e2e/autorizaciones.spec.ts` (H2 CA1: el administrador da de alta dos perfiles con el buscador de personas y una
  regla de dos niveles en la pantalla, axe sin violaciones; H3 CA1–CA3 · H4 CA4: el gerente no ve el pendiente hasta que le toca;
  la jefa ve el panel, autoriza con teclado y comentario y sale de su lista; el gerente lo ve, rechazar sin motivo lo pide y con
  motivo lo rechaza; capturas de la matriz en claro, oscuro y móvil, sin desborde).
- Modificados: `canario.spec.ts` (la ruta nueva en el recorrido de las que devuelven datos) y `navegacion.spec.ts` (el menú de
  Seguridad tiene «Autorizaciones»).
- Capturas de referencia: nuevas `autorizaciones.spec.ts/matriz-{light,dark,movil}.png`; cambiaron
  `navegacion.spec.ts/configuracion-{light,dark}.png` (solo la tarjeta y el enlace «Autorizaciones»). Revisadas una por una.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión, de solo lectura. Recibió el diff (`git diff HEAD` y los archivos nuevos) y la tabla STRIDE
como lista de preguntas a verificar.

| ID | Severidad | Hallazgo | Decisión |
|---|---|---|---|
| F-01 | Alta | Un nivel se saltaba si sus personas estaban inactivas o sin la empresa: desactivar a alguien (sin la compuerta 015) hacía avanzar documentos | Corregido: vacío es solo por configuración de la matriz (perfil inactivo o sin personas); si no, espera. IT › `F01_…` (falla sin la corrección) |
| F-02 | Alta | El estado en espera podía ser editable: se cambiaba el proveedor o las partidas sin mover el monto y el nivel 2 firmaba otra cosa | Corregido: el plan exige `editable: false`. Unit e IT › `F03_F04_…` y `Cambiar_el_monto_…` (409) |
| F-03 | Media | Otra transición (de personas o del superusuario) podía llegar al estado autorizado | Corregido: el plan lo prohíbe. Unit e IT |
| F-04 | Media | La salida se ejecutaba sin su `when` ni campos obligatorios, y sin preguntar avisos | `when` y campos obligatorios prohibidos por el plan; los avisos, R-053-5 |
| F-05 | Media | Cambios al proceso con corridas pendientes las dejaban huérfanas (y una nueva chocaba con el índice único: 500) | Una huérfana se cancela al empezar otra (IT › `F05_…`, falla sin la corrección); el resto, R-053-4 |
| F-06 | Media | Quien mandaba a autorizar (sin ser el creador) podía autorizar | Corregido: se excluye a quien lo mandó. IT › `F06_…` (falla sin la corrección) |
| F-07 | Media | Recalcular la lista leía la fila del documento bajo el candado (interbloqueo con una edición que la tenía en X); la decisión no bloqueaba los destinos de sus automatizaciones antes del candado | Corregido: creador en la corrida; destinos bloqueados antes del candado. El candado global queda en R-053-1 |
| F-08 | Media | El revisor de una solicitud de la matriz podía ser de otra empresa | Corregido: `WhyNotApprover` exige acceso a la empresa. Unit |
| F-09 | Media | La bitácora de `approval.start`/`reset` llevaba el monto y las condiciones (campos que el auditor quizá no ve) | Corregido: sin valores. IT (`Cambiar_el_monto_…`) |
| F-10 | Baja | Sin monto, un nivel con rango se saltaba; un monto negativo saltaba los de mínimo | Corregido: sin monto no se salta; negativo falla cerrado. Unit |
| F-11 | Baja | Borrar un documento en espera dejaba la corrida pendiente | Corregido (cancela); hoy no se puede borrar en espera (no editable) |
| F-12 | Baja | `SystemTransitionAsync` no comprobaba la salida (solo el plan) | Corregido: defensa en profundidad |
| F-13 | Baja | La respuesta de la decisión devolvía el registro a quien ya no lo ve | Corregido: `{ id, _gone }`; la SPA vuelve a la lista. IT y E2E |
| F-14 | Baja | Un monto fuera de `decimal(19,4)` daba 500 al empezar | Corregido: falla cerrado con mensaje |
| F-15 | Baja | La pantalla lista catálogos y opciones aunque el administrador no los lea | R-053-7 |
| F-16 | Baja | Adjuntos (054) que no muevan la versión no los cubre la huella | R-053-9, diferido a 054 |

Sin hallazgos en: transiciones `system` (también para el superusuario), Acompañante y MCP sin ruta para autorizar, cuentas de
servicio fuera, `approvers` solo del motor, decisiones concurrentes (409), la ruta no es oráculo (404 antes de distinguir), una
sola transacción, nadie cambia su propia pertenencia ni la revisa, tamaños de columnas.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con un catálogo inventado «Solicitudes de
gasto» (reglas «General» de tres niveles y «Servicios de sistemas»), cuatro personas y una vista «Pendientes de mi autorización»
(filtro «Autorizadores contiene yo»). Recorrida con el navegador integrado como la jefa de área.
- La lista muestra solo sus dos pendientes; la vista funciona como bandeja.
- Panel en oscuro (1280) y claro (375): regla, niveles con icono y texto (no solo color), «Pueden autorizar», el nivel de
  Dirección saltado «El monto no está en el rango del perfil»; sin desborde horizontal.
- Rechazar sin motivo: «Escribe el motivo del rechazo» junto al campo; Esc cierra y el foco vuelve al botón «Rechazar».
- Autorizar con comentario: aviso «Nivel autorizado · Ya no te toca: el documento sale de tu lista» y vuelve a la lista (que
  ya no lo tiene).
- Consola: solo los 401 previos al inicio de sesión.

### Hallazgos
Ninguno nuevo en la exploración (los de la revisión adversarial están arriba).

### Contra el código anterior
Las pruebas de la matriz no pueden correr en `9f98e6b` (el plan no conoce `approval` y no hay rutas). Las correcciones de la
revisión se comprobaron quitándolas en esta carpeta: `F01_…`, `F05_…` y `F06_…` fallan sin ellas y pasan con ellas.

### Lista de comprobación
- [x] El camino feliz de cada historia se completa sin ayuda
- [x] Mensajes de error en español claro («No te toca autorizar este documento: lo creaste tú (control interno).», «Ninguna regla de autorización aplica…», «Escribe el motivo del rechazo»)
- [x] Claro y oscuro, 1280 y 375 px
- [x] Solo iconos Lucide
- [x] Teclado: autorizar con Enter, diálogos con foco atrapado y Esc; axe sin violaciones serias (E2E)
- [x] Un usuario sin permiso no ve la pantalla (403) ni el pendiente que no le toca (IT en todos los caminos)
- [x] La consola no muestra errores (solo el 401 previo al inicio de sesión)
- [x] Cada camino alterno del STRIDE probado (IT: transición, plan, formulario, API, importación, automatización)
- [x] Dos o más peticiones a la vez (IT: diez decisiones; decidir mientras se quita a la persona)
- [x] Hallazgos de un componente genérico: el panel reutiliza la línea de tiempo del historial; la compuerta 015 suma sus tipos
- [x] Alcance congelado: bandeja global, exportación, Acompañante y expresión libre van a la 055

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#053): R-053-1 (candado global), R-053-2, R-053-3 (colusión, con la compuerta 015),
R-053-4 (cambios al proceso con corridas pendientes), R-053-5 (avisos de la salida), R-053-6, R-053-7, R-053-8 y R-053-9.

## 6. CI del push
Corrida [41576204209](https://github.com/leonardos4enz/erp-ai/actions/runs/41576204209) de `98713f3`: **en verde** (`success`; unitarias,
integración y E2E completa).
