# Verify 036 — ERP AI Studio como chat: motor conversacional y streaming

## Métricas
- inicio: 2026-10-29 09:45
- cierre: 2026-10-29 11:00
- unitarias: 793 (+28: 659 de Erp.Tests, +9; 134 de integración, +19; además Vitest 43, sin cambios)
- e2e: 389 (+0: sin archivos nuevos; cambia studio.spec.ts, el proveedor simulado responde en streaming)
- corridas completas: 2 (7 min 56 s, 389 en verde, antes de las correcciones de la revisión; 7 min 44 s, 389 en verde, final)
- corridas parciales: 9 (studio.spec.ts ×2 al iterar; --veces=3 --lento sobre studio.spec.ts ×3, las dos primeras con la falla F-10; integración de Studio ×4)
- contra el código anterior: 9 (las 4 protecciones principales y los 5 hallazgos de la revisión adversarial, quitando cada corrección y corriendo su prueba)
- hallazgos: 0/2/8
- categorías: seguridad=3, concurrencia=2, motor-api=3, pruebas=2
- revisión adversarial: 9 hallazgos (0 altos, 1 medio, 1 medio/bajo, 7 bajos) y 2 sospechas
- riesgos: R-036-1, R-036-2, R-036-3, R-036-4, R-036-5

## 0. Apertura
`git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main` (`23a1901`, «Spec 035: ERP AI Studio,
pantalla») en verde. Fase 1, el [análisis de Claude web](analisis-claude-web.md): medido en claude.ai con la sesión del dueño
en su Chrome (Claude in Chrome), en un chat incógnito con tres mensajes de prueba; no se inició sesión ni se escribió ninguna
credencial. En el navegador integrado claude.ai pedía sesión: se le preguntó al dueño, que indicó usar su Chrome. CLARIFY 1:
se divide (motor 036, pantalla de chat 037); roadmap renumerado (MCP 038 con la auditoría 7, Ventas 039, CFDI 4.0 040) y
referencias corregidas en ADR-0008, `riesgos.md` y las specs 033 a 035. Producto: ~810 líneas (+813/−111 en `src` y
`web/src`); pruebas: ~900.

## 1. Pruebas unitarias e integración
`dotnet test`: **793 correctas**: 659 de `tests/Erp.Tests` (650 + 9) y 134 de integración (115 + 19).
`npm --prefix web test`: **43** (sin cambios).
- `StudioChatTests.cs` (nuevo, 9): serializar y leer el historial (ids, firmas, herramienta), `Normalize` (roles seguidos,
  usos sin resultado al final o en medio, empieza con la persona), `Purge` (solo `datos`, idempotente, F-02), un evento SSE
  no se parte con saltos en los datos, detener por lease (F-08).
- `StudioTests.Chat.cs` (nuevo, 19): H1 (historial completo, pasos `user` con turno, bitácora sin el texto, lista por
  actividad; rechazos 400/403/404/409 con motivo; diez mensajes a la vez → uno 202 y nueve 409; identidad actual en cada
  mensaje), H2 (topes que suman turnos: 8 llamadas entre dos turnos, no 5 + 8; tope de mensajes y costo del día antes de
  encolar), H3 (refinar: una respuesta sin plan no retira la propuesta, una entrega nueva invalida la huella anterior,
  aplicar no cierra la conversación; no se aplica ni descarta con un turno en curso), H4 (la copia se reutiliza sin volver a
  copiar el modelo, una base con otra marca no se reutiliza, una copia viva por persona, lo leído se borra de pasos e
  historial al terminar cada turno), H5 (flujo SSE con pasos en orden e `id`, herramienta en curso y texto parcial antes del
  paso de texto, `end: idle`, reanudar con `Last-Event-ID` y `after`; 404 a otra persona, 403 sin capacidad, 429 al tercer
  flujo, `end: identity` al desactivarse; detener en < 3 s conservando el texto parcial), fallas (un trabajador sin lease no
  escribe el historial; un turno interrumpido se continúa) y F01, F03, F06/F07.
- `StudioProviderTests`: el proveedor simulado responde en SSE como la API real; el adaptador en streaming (agregador del
  SDK) devuelve razonamiento con su firma, `tool_use` con su entrada y el costo con los tokens de salida del
  `message_delta` (0,0208 USD: también responde a la sospecha S-1 de la revisión).
- Cambiadas por el comportamiento nuevo: `H2_CA1_…sin_empujon` (antes pedía «entregar» una vez más), `H2_CA3_…` y
  `H3_CA1_CA2_…` (la copia se conserva y se borra sin uso), `H2_CA1_una_sesion_investiga…` (el pedido es el primer paso).
- Contra el código anterior, quitando cada protección y corriendo su prueba (falla en todos, por la razón esperada; p. ej.
  aplicar con el turno en curso daba 200 en lugar de 409): purga del historial (`Chat_H4_CA3`), marca de la copia
  (`Chat_H4_CA1_una_base_ajena…`), aplicar con un turno en curso (`Chat_H3_CA2`), identidad del mensaje (`Chat_H1_CA4`),
  F-01, F-02, F-03, F-06 y F-07. `it:regresion` no aplica: las pruebas nuevas usan el contrato nuevo y no compilan contra
  `23a1901`.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `studio.spec.ts` | 6 en verde, 1 falla (captura de la propuesta: la pantalla de la 035 mostraba el paso `user`) | 38 s |
| 2 | `studio.spec.ts` con la pantalla sin el paso `user` | **16 en verde**, capturas sin cambios | 33 s |
| 3 | Completa (antes de las correcciones de la revisión) | **389 en verde** | 7 min 56 s |
| 4 | Completa (final) | **389 en verde** | 7 min 44 s |
| 5 | `--veces=3 --lento` sobre `studio.spec.ts` | vuelta 2: 1 falla (F-10), dos veces | 1 min 26 s y 2 min 50 s |
| 6 | `--veces=3 --lento` sobre `studio.spec.ts` (con F-10) | **16 × 3 en verde** | 2 min 48 s |

- Sin archivos ni capturas nuevas (la pantalla de chat es la 037). `studio.spec.ts`: el proveedor simulado responde en
  streaming cuando la petición lo pide. La pantalla de la 035 no repite el pedido como paso (`kind: 'user'`), así que sus
  capturas siguen iguales (no se regeneraron).
- `docs/sdd/rutas.md` regenerado: 151 rutas (+2).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff contra `23a1901` (con los archivos nuevos) y las preguntas de la
tabla STRIDE, sin sus respuestas. Revisó capacidad y CSRF de las dos rutas, dueño en el SSE, caminos alternos al historial,
delegación de identidad, carreras entre mensajes, cancelar, aplicar, reclamar, vencer y soltar copias, escrituras sin lease,
la purga, el adaptador en streaming y los topes. Encontró:

| ID | Sev. | Hallazgo | Corrección | Prueba |
|---|---|---|---|---|
| F-01 | Media | El costo del día solo sumaba las conversaciones **creadas** en 24 h: seguir conversaciones de días anteriores rodeaba `CostPerDayUsd`. | Cuenta toda conversación con actividad en 24 h (también al reservar). | `StudioTests › F01_seguir_conversaciones_de_dias_anteriores_no_rodea_el_costo_del_dia` |
| F-02 | Media/Baja | La purga se saltaba un resultado que «contenía» la frase del aviso: un registro con esa frase quedaba en el historial y viajaba en cada turno. | Idempotencia por igualdad exacta con el aviso envuelto. | `StudioChatTests › F02_un_registro_con_la_frase_del_aviso_tambien_se_purga` |
| F-03 | Baja | Un trabajador viejo (lease perdido), el vencimiento o soltar por inactividad borraban por nombre la copia que otra instancia ya creó para el turno siguiente. | `DropIfOwnedAsync`: solo con la marca propia. | `StudioTests › F03_soltar_una_copia_no_borra_la_de_otro_turno_con_el_mismo_nombre` |
| F-04 | Baja | La pérdida del lease o una falla de la base al escribir el estado en vivo salían como error del proveedor (y la llamada no se cobraba). | `StudioLeaseLost` y `SqlException` pasan sin convertirse. | sin prueba automática: [R-036-4](../../riesgos.md#036) |
| F-05 | Baja | El texto del modelo que cita registros vuelve al proveedor en los turnos siguientes (amplía R-035-1). | Aceptado. | [R-036-5](../../riesgos.md#036) |
| F-06 | Baja | Un turno fallido sin plan borraba el informe (pruebas, fallas, riesgos) de la propuesta que seguía aplicable. | El informe se conserva mientras la propuesta esté vigente. | `StudioTests › F06_F07_un_turno_fallido_conserva_el_informe_y_aplicar_limpia_el_error` |
| F-07 | Baja | Aplicar o descartar no limpiaba el error del turno anterior («La sesión falló» con estado `applied`). | `ErrorCode = NULL`. | ídem |
| F-08 | Baja | Un «detener» tardío podía cortar el turno siguiente de la misma conversación. | Se detiene por `(sesión, lease)`. | `StudioChatTests › F08_detener_solo_corta_el_turno_de_ese_lease` |
| F-09 | Baja | El texto en vivo se copiaba entero con cada fragmento, antes del limitador. | Se arma solo cuando toca escribirlo. | (rendimiento; lo cubren `Chat_H5_CA1` y `Chat_H5_CA4`) |

Sospechas: S-1 (el agregador del SDK y los tokens de salida) se descartó con `StudioProviderTests` (ver §1); S-2 (paso y
borrado del estado en vivo en dos sentencias): ahora van en una transacción.

## 4. Revisión exploratoria
Sin pantalla nueva (CLARIFY 1). La pantalla de la 035 se recorrió en la E2E (16 escenarios, axe y capturas claro/oscuro/móvil
sin cambios). El recorrido con navegador de la conversación es de la 037; el comportamiento del motor que la pantalla verá
(flujo, detener, refinar, propuesta) está probado en integración. El análisis de Claude web (fase 1) se hizo con navegador
sobre claude.ai.

### Hallazgos
Los de la revisión adversarial (§3), y uno de la suite:

| ID | Sev. | Hallazgo | Corrección | Prueba |
|---|---|---|---|---|
| F-10 | Baja | `studio.spec.ts › H2 CA1 CA2` (035) falló en la vuelta 2 de `--veces=3 --lento`, dos veces seguidas: mira el estado pasajero «Trabajando» en el historial y, con las lecturas retrasadas hasta 700 ms, la sesión ya había terminado. Carrera de la prueba (R-030-5), no del producto. | El paso `compilar_plan` del guion tarda 5 s (antes 1,5 s), lo que sostiene el estado mientras se mira. | la misma prueba: 16 × 3 en verde en modo lento |

Ninguno alto.

## 5. Alcance congelado
Sin pedidos nuevos durante VERIFY.

## 6. CI
El push a `main` corre integración y la E2E completa (segunda corrida completa). El resultado lo anota la spec 037 al abrir.
