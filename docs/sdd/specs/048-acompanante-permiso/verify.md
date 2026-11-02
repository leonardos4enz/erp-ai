# Verify 048 — Acompañante 2: permiso en la conversación y de dónde sale cada campo

## Métricas
- inicio: 2026-11-02 01:34
- cierre: 2026-11-02 02:41
- unitarias: 767 (+3); integración 347 (+23); Vitest 84 (+3)
- e2e: 487 (+2)
- corridas completas: 1 (13 min 30 s, 487 en verde); la segunda completa es CI en `main`
- corridas parciales: 12 (`CompanionTests` en integración ×6 al iterar y con las correcciones; `acompanante.spec.ts` ×6: iteración, capturas y la corrección exploratoria F-01 revertida y restaurada)
- contra el código anterior: 5 (`it:regresion -- 1fdc65b CompanionTests` ×2; las correcciones de la revisión adversarial revertidas a mano: `Regresion_048` 4 de 4 en rojo; la nota de la exploratoria F-02 revertida: la unitaria en rojo; `acompanante.spec.ts` con la cabecera de la tarjeta de antes: en rojo)
- hallazgos: 0/1/9 (revisión adversarial F-01…F-08, exploratoria F-01 y F-02, sin contar los dos ajustes que pidió el dueño en IMPLEMENT)
- categorías: concurrencia=2, límites=2, seguridad=1, motor-api=4, UI/UX=1
- revisión adversarial: 8 hallazgos
- riesgos: R-048-1, R-048-2, R-048-3, R-048-4, R-048-5

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. La corrida de CI de `1fdc65b` (corrección de la CI de la 047) estaba en curso al
abrir y terminó **en verde** ([corrida](https://github.com/leonardos4enz/erp-ai/actions/runs/41407163887)); anotada en el
`verify.md` de la 047 (métrica y fila 6 de §2). Sin `gh`: API pública de GitHub con `curl`.

## 1. Pruebas unitarias e integración
`dotnet test`: **767 unitarias** y **347 de integración** en verde (1 min 54 s). Nuevas:
- `CompanionCoreTests` (+3): `FieldSources` (update con y sin `target`, por partida, `create`, `capture`, `rollup`,
  transición con `set`, catálogo no visible), el prompt (cree al sistema, «loLlena», sin «Nunca digas que ya se hizo») y el
  resultado de una acción (lo del sistema escapado; solo el motivo de la persona en `<pedido>`; sin `<`).
- `CompanionPermisoTests.cs` (+23, parte de `CompanionTests`, con su host y Gato / Vacuna aplicada / Clínica): H1 (el turno se
  detiene sin lease; «aplicada» con su registro vuelve al modelo y el turno sigue; «no» con y sin motivo; escribir es «no» con
  el texto; dos propuestas una por una; detener; esperar cuenta como activa), H2 (confirmar y descartar a la vez reanudan una
  vez; decidir y detener a la vez; caída entre aplicar y reanudar; caducar), H3 (`filledBy` solo de catálogos visibles,
  `como_funciona` con «loLlena», `buscar_en_el_sistema`), H4 (el rechazo dice de dónde sale), entradas, delegación, la
  regla hostil y las cuatro regresiones de la revisión adversarial.
- Las de la 046 se ajustaron a que una propuesta deja la conversación esperando (`ProposeAsync`, `FreeAsync`, `WaitResumedAsync`).

Contra `1fdc65b` (`npm --prefix web run it:regresion -- 1fdc65b CompanionTests`): **las 23 nuevas fallan** (y 12 de la 046,
porque su ayudante ahora espera `waiting`); 17 pasan (las de la 046 que no proponen).

Vitest: **84** (+3 en `companion.test.ts`: avatar `waiting`, `askingAction`, segmento `answer` en `groupTurns`).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 487 en verde | 13 min 30 s |
| 2 | `--veces=3` (con `--lento`) sobre `e2e/acompanante.spec.ts` | 20 en verde, 3 vueltas | 5 min 02 s |

Archivo modificado: `web/e2e/acompanante.spec.ts` (+2 pruebas: «No, y dile qué cambiar» y escribir mientras espera; las de
confirmar usan «Sí, adelante», el rechazo comprueba «Propuesta 1 de 2» y «En espera», y confirmar comprueba que el modelo
recibió «aplicada»). `start()` detiene la conversación que dejó esperando la prueba anterior.

Capturas cambiadas, revisadas una por una: `acompanante-light.png` y `acompanante-dark.png` (la tarjeta pregunta «¿Lo hago?»
con «No, y dile qué cambiar», «No» y «Sí, adelante»; el avatar es solo la cara; el compositor dice «Escribe qué cambiar
(cuenta como «no»)…» y su nota). Se borraron y se volvieron a escribir porque el cambio del texto del compositor quedaba
dentro de la tolerancia y la referencia vieja lo habría protegido. `acompanante-movil.png` no cambia (bienvenida, en reposo).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff sin commitear y la tabla STRIDE (§5 del plan). Revisó el orden de
bloqueos en confirmar, descartar, responder, detener, borrar y el barrido; la reanudación; `filledBy` y la búsqueda contra
`CanSee`/`ForUser`; la inyección por el error de una regla; la delegación; y que `filledBy` no vuelva al modelo guardado.
Sin hallazgos altos.

| ID | Severidad | Hallazgo | Corrección | Prueba |
|---|---|---|---|---|
| F-01 | Media | «Detener» mientras el turno cerraba en espera se perdía: `FinishAsync` dejaba `waiting` y la conversación se reanudaba al decidir | El cierre en espera con `CancelRequested` queda `cancelled` y descarta lo pendiente | `CompanionTests › Regresion_048_F01_detener_mientras_el_turno_se_detiene_a_esperar_gana` |
| F-02 | Baja | `Reason nvarchar(2000)` con `MaxPromptChars` hasta 4000: un motivo largo daba 500 | `nvarchar(4000)` (y `ALTER` para bases existentes) | `› Regresion_048_F02_el_motivo_cabe_hasta_el_maximo_de_un_mensaje` |
| F-03 | Baja | Confirmar lento: el barrido reanuda con «sin confirmar» | Sin cambio: la confirmación tardía sí guarda el resultado real (`WHERE Status IN ('applying','unknown')`); el modelo ya contestó | [R-048-2](../../riesgos.md#048) |
| F-04 | Baja | Escribir en espera sin nada pendiente: el texto no llegaba al modelo | Sin nada que descartar, es un mensaje normal en un turno nuevo | `› Regresion_048_F04_escribir_sin_nada_pendiente_es_un_mensaje_nuevo` |
| F-05 | Baja | Descartar no exigía la empresa de la propuesta | El mismo 409 que confirmar | `› Regresion_048_F05_descartar_pide_la_empresa_de_la_propuesta` |
| F-06 | Baja | Sin tope propio de vueltas «proponer → no → proponer» por turno | Aceptado: lo frenan pasos, consultas, tokens y costo | [R-048-1](../../riesgos.md#048) |
| F-07 | Baja (sospecha) | `filledBy` que se colara en el modelo guardado se publicaría | `ForUser` lo pone en null antes de calcularlo (además de `PlanCompiler` y `ModelDiff.Clean`) | `CompanionTests › H3_CA1_la_procedencia_solo_de_catalogos_visibles` (sin prueba propia: ningún camino lo guarda) |
| F-08 | Baja | `filledBy` incluye transiciones de sistema | Aceptado (es una fuente real; no es una fuga) | [R-048-4](../../riesgos.md#048) |

Las correcciones F-01, F-02, F-04 y F-05 se revirtieron a mano: las cuatro pruebas `Regresion_048_*` en rojo; restauradas, en verde.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con un proveedor simulado genérico en
127.0.0.1 (`Assistant__BaseUrl`; el mensaje decide qué herramienta usa y la respuesta cita lo que recibió el modelo; nunca se
llamó a Anthropic). El caso del dueño: Gato con «Vacunado» (lo llena «Marcar al gato como vacunado» de Vacuna aplicada) y
«Adoptado» (transición «Adoptar»), una persona con rol «Cuidador» sin modelador. Navegador integrado, 1280 px.

Recorrido: proponer «Vacunado» → el modelo recibe «lo llena el sistema, no la persona: la automatización «Marcar al gato
como vacunado» de Vacuna aplicada, al crear un registro de Vacuna aplicada»; `buscar_en_el_sistema` «vacunado» desde Gatos
encuentra el campo y la automatización con lo que toca; dos propuestas → «Propuesta 1 de 2», la segunda «En espera»;
«Sí, adelante» → «Aplicada» y se pregunta la segunda; «No, y dile qué cambiar» con Enter → «Le dijiste: …», el turno sigue solo
y el modelo recibe «aplicada» con el registro y «rechazada» con el pedido de la persona. Avatar «Esperando tu respuesta» y
luego «Listo», solo la cara.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Baja | UI/UX | «Propuesta 2 de 2» en la cabecera aplastaba el título de la tarjeta a una columna («Ad / opt / ar») | La posición va en su propia línea, bajo el título | `acompanante.spec.ts › H5 CA2 · CA3: un rechazo…` (título de 26 px con la cabecera de antes; > 100 px con la corrección) |
| F-02 | Baja | motor-api | La nota del resultado «rechazada» decía `<pedido>`, que el codificador de datos no confiables convierte en `<pedido>` | Nota sin corchetes («Justo después, en su pedido…») | `CompanionCoreTests › El_resultado_de_una_accion_separa_lo_del_sistema_de_lo_de_la_persona` |

También: `buscar_en_el_sistema` mandaba `"recortado": null`; ahora solo se manda cuando hay recorte.

### Pedidos del dueño durante IMPLEMENT (alcance aún abierto)
- **Buscar en todo el sistema** («¿qué pasa si le pregunto estando en Gatos algo de otra cosa?»): herramienta
  `buscar_en_el_sistema` (H3 CA3). «¿Qué pasa si…?» sigue en el roadmap.
- **El avatar solo con la cara**: se quitaron los indicadores sobre la cabeza (libreta, llave, globo, palomita, nube) (H6 CA4).

### Lista de comprobación
- [x] Camino feliz de cada historia sin ayuda (proponer, sí, no, no con motivo, escribir en espera, varias una por una, buscar)
- [x] Mensajes en español claro y en lenguaje de usuario («¿Lo hago?», «Sí, adelante», «En espera: te la pregunto…»)
- [x] Teclado: «No, y dile qué cambiar» enfoca el campo; Enter envía, Esc cancela y regresa a la tarjeta
- [x] Claro y oscuro (capturas), 375 px por la E2E de móvil; solo Lucide fuera del avatar
- [x] Lo que ya se sabía y sigue: la lista abierta no se recarga sola tras aplicar (R-047-2)

## 5. Después del cierre (mismo día)
- **Pantalla desactualizada (R-047-2)**: el dueño, al probar, vio que el Gato pasó de «En cuarentena» a «Disponible» y la
  pantalla siguió igual. Se descartó recargar sola (pisaría lo que se captura); decisión del dueño: un aviso. Al aplicarse
  una acción, la lista y el registro abiertos muestran «El Acompañante aplicó cambios: es posible que … esté
  desactualizado» con «Actualizar» (cualquier catálogo: una automatización puede haber tocado el que se ve). Evento
  `erp:datos-cambiados` (`lib/hooks.ts`). Pruebas: `acompanante.spec.ts › H5 CA2 · CA3: confirmar…` (aviso en el registro) y
  `› R-047-2 (048): en la lista abierta…`; sin el aviso, la primera falla.
- **CI de `cfcef01` en rojo**: interbloqueo intermitente en `StudioTests › H5_CA1_una_sesion_interrumpida_tambien_borra_lo_leido`
  (no la toca la 048): la prueba insertaba la sesión vencida y sus pasos en dos sentencias, y el barrido de Studio podía
  tomar la sesión entre ambas. Ahora van en una transacción.
- Corridas: E2E completa 488 en verde (12 min 26 s); `acompanante.spec.ts --veces=3` 21 en verde ×3 (5 min 21 s); `StudioTests` ×3 en verde (57; el interbloqueo no se reprodujo en local: es una carrera con el barrido). Vitest 84.

- Ajustes visuales del dueño: el aviso de la lista con margen abajo (`.list-stale`) y el avatar de la cabecera del panel de
  52 a 44 px. Capturas `acompanante-light/dark/movil.png` regeneradas y revisadas; `acompanante.spec.ts --veces=3` en verde.
- **CI de `a1341b4` en verde** (anotada al abrir la auditoría 9, 2026-11-02): corrida
  [41466140553](https://github.com/leonardos4enz/erp-ai/actions/runs/41466140553); «Unitarias, build, Vitest y auditoría» en
  46 s e «Integración y E2E (SQL Server LocalDB)» en 20 min 18 s, las dos `success`.

## 6. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md): [R-048-1](../../riesgos.md#048) (la caducidad reanuda sin la persona; sin
tope de vueltas por turno), [R-048-2](../../riesgos.md#048) (confirmación lenta y «sin confirmar»), [R-048-3](../../riesgos.md#048)
(la purga al esperar borra el motivo de una propuesta fallida), [R-048-4](../../riesgos.md#048) (transiciones de sistema en
`filledBy`), [R-048-5](../../riesgos.md#048) (costo de la búsqueda).
