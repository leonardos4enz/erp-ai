# Verify 047 — Acompañante: panel lateral con avatar

## Métricas
- inicio: 2026-11-02 00:01
- cierre: 2026-11-02 01:10
- unitarias: 764 (+0); Vitest 81 (+8)
- e2e: 485 (+17)
- corridas completas: 2 (11 min 48 s, 485 en verde); la segunda completa fue CI en `main` (`8371867`), en rojo por una carrera de la prueba en `visual-pdf › H5 CA1 (móvil)`, corregida en el commit siguiente, `1fdc65b`, cuya CI quedó en verde (ver §2)
- corridas parciales: 17 (`acompanante.spec.ts` al iterar ×9; `capacidades.spec.ts` ×2 y `--veces=3 --lento` con el código anterior y con la corrección; `acompanante.spec.ts` con correcciones revertidas ×5; `acompanante.spec.ts` + `capacidades.spec.ts --veces=3`, 4 min 51 s)
- contra el código anterior: 6 (`capacidades.spec.ts --veces=3 --lento` con `ModulesPage.tsx` de `d422553`; `acompanante.spec.ts` con cada corrección de VERIFY revertida a mano: 5 corridas)
- hallazgos: 0/4/9 (revisión adversarial F-01…F-11, exploratoria F-01 y F-02, sin contar la intermitente de CI de la 046)
- categorías: UI/UX=7, accesibilidad=2, concurrencia=2, fallas parciales=1, límites=1
- revisión adversarial: 11 hallazgos
- riesgos: R-047-1, R-047-2, R-047-3, R-047-4, R-047-5, R-047-6

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. La corrida de CI de `d422553` (spec 046) estaba en curso al abrir y terminó **en
rojo**: `capacidades.spec.ts › H6 CA2 (UI)`, foco en body tras el 409 de instalar un módulo, intermitente (pasó al repetirse).
Va primero, en su propio commit (`32c9acb`): tras el 409 el diálogo recalcula la vista previa y deshabilitaba «Instalar
módulo» con el foco encima. Reproducida con el código de `d422553` (`--veces=3 --lento`, falla en la primera vuelta); con la
corrección (el botón queda `loading`, enfocable) 3 de 3 en verde. Anotada en el `verify.md` de la 046 y en R-030-5. Sin `gh`:
se consultó la API pública de GitHub con `curl` (corrida y anotaciones del trabajo).

## 1. Pruebas unitarias e integración
`dotnet test`: **764 unitarias** y **324 de integración** en verde (1 min 53 s): sin cambios de servidor; las filas de concurrencia
y fallas parciales del servidor siguen cubiertas por `CompanionTests` (046). Vitest: **81** (73 + 8 en
`web/src/lib/__tests__/companion.test.ts`): estado del avatar en los seis estados, toda herramienta de `CompanionTools.cs`
con nombre para personas (lee el archivo C#: una herramienta nueva sin nombre hace fallar la prueba), título y estados de la
tarjeta (y que sin vista previa no se ofrece confirmar), valores vacíos, avisos de un 428, contexto por ruta, paso `action`
en `groupTurns`, y la regresión F-04 (una lectura vieja no devuelve a «pendiente» una acción decidida).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa (con `--update-snapshots`, por el botón nuevo de la barra superior) | 485 en verde | 11 min 48 s |
| 2 | `--veces=3` (con `--lento`) sobre `e2e/capacidades.spec.ts` (corrección de la CI de la 046) | 9 en verde, 3 vueltas | 35 s |
| 3 | `--veces=3` (con `--lento`) sobre `e2e/acompanante.spec.ts` y `e2e/capacidades.spec.ts`, al cerrar | 26 en verde, 3 vueltas | 4 min 51 s |
| 4 | CI de `8371867` ([corrida](https://github.com/leonardos4enz/erp-ai/actions/runs/41403890386)) | Roja: `visual-pdf › H5 CA1 (móvil)` («Cannot read properties of null (reading 'width')») | — |
| 5 | `compras`, `inventario`, `valuacion-*` y `visual-pdf` con la prueba corregida | 92 en verde | 1 min 30 s |
| 6 | CI de `1fdc65b` (la corrección de la fila 4; [corrida](https://github.com/leonardos4enz/erp-ai/actions/runs/41407163887)), comprobada al abrir la 048 | Verde | — |

La falla de CI es una carrera de la prueba: `boundingBox()` no espera a que el título del formulario se vea y en el runner lo
medía antes (null). En local pasa con y sin `--lento`; no se reprodujo. La prueba ahora espera el título y el botón antes de
medirlos. El reintento de CI la marcó «reproducible» porque repitió `visual-pdf.spec.ts` solo, sin los archivos de los que
depende (su encabezado los nombra como texto, no en la forma «Corre después de …» que lee `reintentar-ci.mjs`). Anotada en
[R-030-5](../../riesgos.md#030).

Archivo nuevo: `web/e2e/acompanante.spec.ts` (17 pruebas, un proveedor simulado como `studio.spec.ts`, una persona sin rol de
modelador y con las guardias de la 030). Configuración: `Companion__PollSeconds` y `Companion__RenewSeconds` en 1 (como Studio).

Capturas nuevas, revisadas una por una: `acompanante-light.png` y `acompanante-dark.png` (panel con una propuesta de crear, la
explicación aparte, la nota y los botones; avatar quieto con `reducedMotion`, horas enmascaradas) y `acompanante-movil.png`
(bienvenida a todo el ancho). La primera versión tenía el globo del avatar cortado arriba y la nota «Nada cambia…» en una
columna angosta junto a los botones: corregidas antes de aprobarlas. Capturas de otros archivos: ninguna superó el umbral (1 % de
píxeles) con el botón «Acompañante» de la barra superior, así que `--update-snapshots` no reescribió ninguna referencia.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff contra `d422553` (más los archivos nuevos) y solo la tabla STRIDE del
plan. Revisó XSS desde el modelo, los registros y la vista previa; confirmar otra cosa que lo que se ve (huella, avisos,
lecturas cruzadas); doble clic y dos pestañas; fugas por `localStorage`, empresa y contexto de pantalla; regresiones del
refactor de Studio; CSP del avatar; fallas parciales; y que cada fila cite pruebas reales.

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | fallas parciales | Sin respuesta de confirmar (red), la tarjeta decía «No se pudo conectar con el servidor»: `api()` convierte la falla de red en `ApiError(0)` y la rama «no sabemos si se aplicó» nunca corría | Estado 0 y 5xx dicen que no se sabe si se aplicó y piden revisar antes de repetir | `acompanante.spec.ts › Regresión (revisión adversarial 047, F-01 · F-02): …` (falla revertida) |
| F-02 | Media | UI/UX | Una falla pasajera al releer `/api/companion` (tras confirmar, enviar, borrar) reemplazaba la conversación por «No se pudo cargar el Acompañante»: se perdían borrador, avisos y flujo | El error a pantalla completa solo sin datos cargados | la misma (falla revertida) |
| F-03 | Media | accesibilidad | Tras un rechazo que quita los botones (409), el foco salía del panel a `#main` | El foco pasa a la tarjeta también en los rechazos | `› H5 CA2 · CA3: un rechazo se muestra en la tarjeta y descartar la cierra` (`toBeFocused`, falla revertida) |
| F-04 | Baja | concurrencia | Dos tarjetas: una lectura completa pedida antes de confirmar la segunda podía devolverla a «pendiente» | Una acción decidida en la tarjeta no vuelve a «pendiente» por una lectura vieja (`latestAction`) | `companion.test.ts › Regresión (revisión adversarial 047, F-04): …` |
| F-05 | Baja | UI/UX | Los avisos de un 428 seguían a la vista si la acción se decidía en otra pestaña o caducaba | Se limpian al dejar de estar pendiente | sin prueba de pantalla (efecto de una línea) |
| F-06 | Baja | concurrencia | En reposo, una decisión en otra pestaña no se veía hasta hacer clic (409) | Relee la conversación al volver a la pestaña (`visibilitychange`) | sin prueba (el servidor ya serializa: `CompanionTests › H4_CA3_…`) |
| F-07 | Baja | límites | Con más de 50 avisos la persona no puede aceptarlos todos (el servidor toma 50) | Riesgo | [R-047-5](../../riesgos.md#047) |
| F-08 | Baja | UI/UX | El comentario de una transición lo escribe el modelo y se pintaba como dato del servidor | Rótulo «Comentario que se guardará (lo escribió el Acompañante)» y otro estilo | `› H5 CA2: los avisos piden confirmar otra vez (428) y la transición se aplica` (lo muestra) |
| F-09 | Baja | accesibilidad | `aria-controls` del botón apuntaba a un id inexistente con el panel cerrado | Solo con el panel abierto | sin prueba |
| F-10 | Baja | UI/UX | Las consultas sin entrada ni salida tenían efecto de botón y fuente de código | Estilo propio (`.is-plain`) | captura `acompanante-*.png` |
| F-11 | Baja | UI/UX | Una falla al releer al terminar el turno dejaba «No se pudo abrir la conversación» sin salida | «Intentar de nuevo» | sin prueba |

Divergencias de la tabla corregidas en el plan: títulos de pruebas citados con otro nombre; «un rAF por avatar» (es uno
compartido); la fila T citaba un 409 por huella que la UI no puede provocar (el 409 de la prueba es por la versión del
registro; la huella la cubre integración); la fila de límites citaba una prueba que no prueba ningún 429. Observación sin
cambio: al cambiar de empresa el panel pide una lectura de más que el servidor responde 404 ([R-047-6](../../riesgos.md#047)).

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con un proveedor simulado en 127.0.0.1
(`Assistant__BaseUrl`, clave ficticia; nunca se llamó a Anthropic). Persona de prueba con un rol de captura (sin modelador),
un catálogo con proceso y una regla de aviso, y registros con acentos, ñ, `<b>` y montos negativos. Navegador integrado, claro y
oscuro, 1280 px y 375 px; teclado para confirmar y para los avisos.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Baja | UI/UX | Con el panel abierto (la barra superior pierde 400 px) el atajo «Ctrl K» de la búsqueda se encimaba con los botones | Con el panel abierto se ocultan el atajo y el texto del botón (y el de la búsqueda bajo 1 440 px) | `acompanante.spec.ts › H1 CA1 · CA3: …` (atajo oculto, falla revertida) |
| F-02 | Media | UI/UX | Tras confirmar, «Ver el registro» sobre el registro ya abierto no hacía nada y la pantalla seguía con el valor viejo | Con ese registro abierto, el enlace recarga la página; el panel y la conversación abierta se recuerdan (por persona) | `› H5 CA2 · CA3: confirmar manda la huella…` (teléfono nuevo en el formulario, falla revertida) |

Durante la implementación salieron, por las E2E, otros dos que se corrigieron antes de VERIFY: el título de una transición
cambiaba a «Cambiar de estado» al decidirse (el servidor borra la vista previa) y al recargar el panel volvía a la bienvenida.

### Contra el código anterior
- `capacidades.spec.ts --veces=3 --lento` con `ModulesPage.tsx` de `d422553`: falla en la primera vuelta con el mismo mensaje
  que CI; con la corrección, 3 de 3.
- Las correcciones de VERIFY no estaban publicadas: se revirtieron a mano (exploratoria F-01 y F-02, adversarial F-01, F-02 y
  F-03) y se corrió `acompanante.spec.ts` una vez por corrección (serial: con `E2E_INDEPENDENT=1` el `afterAll` de cada proceso archiva el
  catálogo de prueba y arrastra a las siguientes). Falla exactamente la prueba de cada una: `H1 CA1 · CA3` (exploratoria F-01,
  atajo visible), `H5 CA2 · CA3: confirmar…` (exploratoria F-02, el formulario seguía con «100»), `H5 CA2 · CA3: un rechazo…`
  (adversarial F-03, foco fuera de la tarjeta) y `Regresión (revisión adversarial 047, F-01 · F-02)` (con cada una revertida).
  Restauradas, el archivo corrió tres veces en verde (§2). Las demás pruebas nuevas son de una pantalla que no existía en `d422553`.

### Lista de comprobación
- [x] Camino feliz de cada historia sin ayuda (pregunta, consultas, propuesta, confirmar, avisos, descartar, historial, borrar)
- [x] Mensajes en español claro y en lenguaje de usuario («Revisó tus pendientes», no `mis_pendientes`)
- [x] Doble clic en «Confirmar» (una sola petición), recargar a mitad (el panel y la conversación vuelven), datos límite
- [x] Claro y oscuro, 1280 y 375 px; solo Lucide fuera del avatar; estados vacío, cargando, error y no disponible
- [x] Teclado: abrir, confirmar, aceptar avisos y cerrar (el foco vuelve al botón); Esc cierra en pantallas angostas
- [x] Consola sin violaciones de CSP (solo el 401 de antes de entrar y el 428 esperado)
- [x] Seguridad: sin rutas nuevas; persona sin modelador no ve Studio; fuera de su alcance, el servidor (046)
- [x] Hallazgos de componentes genéricos: el diálogo de módulos (CI) y el compositor compartido revisados en Studio
- [x] Alcance congelado: guías paso a paso, modo instructor y eliminar registros siguen en el roadmap

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#047): R-047-1 … R-047-6; R-046-2 actualizado (la tarjeta separa la explicación del
modelo). La **auditoría 9** cubre 045–047 (decisión del dueño, 2026-11-01; [roadmap](../../roadmap.md#auditorías)).
