# Verify 049 — Endurecimiento 8 (auditoría 9)

## Métricas
- inicio: 2026-11-02 09:20
- cierre: 2026-11-02 10:05
- unitarias: 1216 (+18: 770 de Erp.Tests, +3; 362 de integración, +15; Vitest 84, +0)
- e2e: 488 (+0: sin cambios de pantalla)
- corridas completas: 1 (dotnet test 2 min 10 s; E2E 488 en verde, 12 min 17 s); la segunda completa es la CI del push
- corridas parciales: 6 (integración del Acompañante y del MCP 3, antes y después de la revisión adversarial; un `dotnet test` completo de iteración antes de O-02; Vitest 1); lo nuevo tres veces 1 (las 15 de integración, 19–20 s por vuelta)
- contra el código anterior: 3 (`it:regresion -- a1341b4 H049`: la primera no compiló por constantes nuevas en las pruebas; la segunda, 9 fallan y 1 pasa; `it:regresion -- a1341b4 049`, con lo de la revisión adversarial: 13 fallan y 2 pasan)
- hallazgos: 1/1/5 (revisión adversarial F-01…F-07: F-01 a F-05 corregidos; F-06 y F-07 a riesgos); exploratoria sin hallazgos
- categorías: seguridad=3, motor-api=3, concurrencia=1
- revisión adversarial: 7 hallazgos
- riesgos: R-049-1, R-049-2, R-049-3, R-049-4, R-049-5

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0 (`b6f7a58`, el informe de la auditoría 9, solo docs). La CI de `b6f7a58` estaba
en curso al abrir; terminó en verde (`success`) antes de cerrar. El servidor de desarrollo de esta carpeta no estaba corriendo.

## 1. Pruebas unitarias e integración
`dotnet test`: **770 unitarias** (767 + 3 en `tests/Erp.Tests/Hardening8Tests.cs`) y **362 de integración** (347 + 15), en
verde (2 min 10 s). Vitest: 84.
- Unitarias (`Hardening8Tests`): el estimador de entrada (con y sin medición del turno), `MaxInputTokens` por omisión y acotado
  en Studio y el Acompañante, «prompt is too long» reconocido y su mensaje en los dos.
- Integración nueva:
  - `CompanionApagadoTests` (1, clase propia: reinicia la API apagada con `ErpHost.RestartAsync`): confirmar y descartar 503 sin
    escribir; la espera y la cola se cierran como `failed`/`disabled` con lo propuesto descartado y su `companion.finish`; el
    proveedor no se vuelve a llamar (D-01).
  - `CompanionTests.Hardening049.cs` (13): tope a media respuesta descarta la propuesta y su resultado «detenida» vuelve al
    modelo en el mensaje siguiente (las demás llamadas siguen con «el turno se interrumpió»); «Detener» durante una consulta
    retenida con `HoldAsync`; turno sin trabajador (`ExpireAsync`) (D-02). Llamada que no cabe se detiene antes del proveedor;
    `too_long` del proveedor con su mensaje (D-03). Decidir tras cambiar la contraseña reanuda y contesta (O-01). Procedencia,
    búsqueda y ruta del modelo sin lo de partidas ocultas; la definición de la partida 403 (O-03, F-01, F-02). Detener y borrar
    piden la empresa (O-04). Reglas y condiciones sobre un campo oculto sin su expresión, también en `como_funciona` y en lo que
    viaja al proveedor (O-02). Revisión adversarial F-03, F-04 y F-05.
  - `McpTests › H049_O05_registro_dinamico_y_renovaciones_a_la_vez_no_se_interbloquean` (O-05): tres vueltas de 6 renovaciones y
    8 registros a la vez, con un cliente viejo barrido en cada una. Pasa también contra `a1341b4`: no había interbloqueo; queda
    como guardia (la O-03 de la 045 se cierra con esta prueba).
- Modificadas a propósito: el anfitrión de `CompanionTests` gana `Companion:MaxInputTokens=40000`, cinco personas, el rol
  «Comp Nomina» y los catálogos de nómina; el de `McpTests`, tres personas para O-05 (la primera versión usaba personas de
  `H6_CA4`, cuya bitácora cuenta sus conexiones, y la rompió en la primera corrida completa).
- `docs/sdd/rutas.md`: sin cambios (ninguna ruta nueva ni cambio de capacidad o efecto).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 488 en verde | 12 min 17 s |

Sin archivos E2E nuevos ni modificados y sin capturas cambiadas: la spec no toca la SPA (los mensajes nuevos llegan en el
`error` de la conversación y en los 503/409 que el panel ya muestra). «Lo nuevo tres veces» se corrió sobre las 15 pruebas
nuevas de integración (tres vueltas en verde, 19–20 s cada una).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff HEAD -- src tests`, antes de O-02), la descripción de lo que
cambia sin conclusiones, la tabla STRIDE con sus ocho preguntas y el inventario de rutas. Revisó `CompanionActions`,
`CompanionAgent`, `CompanionService`, `ModelEndpoints`, `FieldSources`, `RecordFilter.SeesLines`, `UserContext` y los índices.

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Alta | seguridad | `ForUser` entregaba las automatizaciones crudas: la ruta del modelo (pedida sin el Acompañante) daba la acción sobre las partidas ocultas, su campo y lo que asigna | Sin las acciones sobre partidas cuyo campo no ve (ni la automatización que solo tenía esas) y sin expresiones en todas las acciones | `CompanionTests › H049_H5_CA2_la_ruta_del_modelo_no_entrega_lo_de_partidas_ocultas` (falla con `a1341b4`) |
| F-02 | Media | seguridad | La definición de la partida (`/entities/{partida}` y la lista con `includeChildren`) solo pedía el documento | `SeesCatalog`: la partida, solo a quien ve su campo de partidas (403 / se omite) | la misma (falla con `a1341b4`) |
| F-03 | Baja | seguridad | Confirmar una propuesta caducada desde otra empresa la caducaba y reanudaba el turno (con el sello de quien confirma) antes de revisar la empresa | La empresa antes de la caducidad | `CompanionTests › Regresion_049_F03_una_propuesta_caducada_no_se_confirma_ni_reanuda_desde_otra_empresa` (falla con `a1341b4`) |
| F-04 | Baja | motor-api | Las propuestas que quedaron `proposed` en conversaciones cerradas antes de esta spec se podían confirmar hasta que caducaran | Confirmar exige que la conversación corra o espere; si no, la descarta y responde 409 | `CompanionTests › Regresion_049_F04_una_propuesta_que_quedo_de_un_turno_cerrado_no_se_aplica` (falla con `a1341b4`) |
| F-05 | Baja | motor-api | O-04 dejaba sin detener ni borrar una conversación sin empresa si la persona tiene una | Sin empresa, se detiene y borra desde cualquiera | `CompanionTests › Regresion_049_F05_una_conversacion_sin_empresa_se_detiene_y_borra` (falla con el código de O-04 de esta misma spec; pasa con `a1341b4`, que no revisaba empresa) |
| F-06 | Baja | motor-api | El estimador de entrada rechaza de más en la primera llamada de un turno (español) y de menos con JSON numérico | A riesgos: el 400 queda de respaldo con su mensaje | [R-049-4](../../riesgos.md#049) |
| F-07 | Baja | concurrencia | Con instancias de configuración mezclada, una encendida todavía puede reclamar y llamar al proveedor | A riesgos | [R-049-1](../../riesgos.md#049) |

O-02 (H8) se implementó después de la revisión, con la decisión del dueño; lo cubre su prueba y la corrida completa.

## 4. Revisión exploratoria
La spec no tiene pantalla nueva ni cambia la SPA: no se levantó la instancia de revisión. Cada camino alterno de la tabla
STRIDE se ejerció por API con una persona sin permiso en integración (otra empresa, sin el campo de partidas, sin el campo
oculto, apagado). La suite E2E completa (incluido `acompanante.spec.ts`, que muestra los errores de la conversación y los 409
de la tarjeta) siguió en verde.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| — | — | — | Sin hallazgos | — | — |

### Contra el código anterior
`npm --prefix web run it:regresion -- a1341b4 049`: fallan las 13 de defectos, observaciones y F-03/F-04 (cada una en su aserción:
503, estado de la acción, `filledBy`/`assert` no nulos, mensaje, 409); pasan O-05 (guardia: no había interbloqueo) y F-05 (el
defecto lo introdujo esta spec). La primera corrida no compiló porque las pruebas usaban constantes nuevas del motor; se
cambiaron por literales.

### Lista de comprobación
**Flujo**
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo («se apagó», «ya no cabe: pide algo más acotado», «es de otra empresa»)
- [ ] Doble clic, Atrás, recargar: n/a (sin pantalla)
- [x] Datos límite: entrada de ~160 000 caracteres en una respuesta, conversación sin empresa

**Diseño** — n/a (sin pantalla)

**Accesibilidad** — n/a (sin pantalla)

**Seguridad**
- [x] Un usuario sin permiso no ve lo oculto ni por URL directa (ruta del modelo pedida sin el Acompañante)
- [x] Cada camino alterno de la tabla STRIDE probado con un usuario sin permiso (API directa)
- [x] Dos peticiones a la vez: registro y renovaciones del MCP; confirmar contra cerrar queda en [R-049-3](../../riesgos.md#049)

**Cierre**
- [x] Hermanos de un componente genérico: `SeesCatalog` en la lista, la definición y la procedencia; `DiscardPendingAsync` en los cuatro cierres
- [x] Alcance congelado: lo que quedó fuera (cancelación en las consultas de datos, unificar bucles) está en el roadmap

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#049): R-049-1 (instancias con el interruptor distinto), R-049-2 (el mensaje de una
regla lo redacta el modelador), R-049-3 (confirmar contra cerrar, sin prueba de la ventana), R-049-4 (el estimador de entrada),
R-049-5 («Detener» no corta una consulta en curso). Actualizados R-046-7 y R-048-1 (el interruptor ya apaga la cola, la espera
y la reanudación).
