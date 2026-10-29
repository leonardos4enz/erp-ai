| R-033-5 | Las rutas despachadas en proceso no pasan por los limitadores de frecuencia de ASP.NET (global, `search`, `bulk`, `aggregate`). | Baja | Corregido en 034: el despachador aplica los mismos topes por persona (`DispatchLimits`, contadores propios: R-034-2) | `Hardening034Tests › H3_CA4_las_rutas_despachadas_tienen_su_limite` | Cerrado en 034 | 2026-10-27 || R-032-6 | Preexistente, visto en el VERIFY de la 032 (F-08): `jerarquia.spec.ts › H5 CA1` falla cuando corre solo después de `h5-h6-seguridad.spec.ts`. Causa (auditoría 6): la prueba comparaba los `recordId` de la bitácora de `J15Caso` con ids de registro, y en `model.change` el `recordId` es el id del ChangeSet. Aparte, `AuditFilter.Build` sí dejaba ver a cualquier auditor `data.print`, `automation.schedule` y `automation.retry` (A-07). | Media | Corregido en 034: la prueba filtra por acciones de datos; la bitácora deniega lo no clasificado | `jerarquia.spec.ts › H5 CA1`, `Hardening034Tests › H4_CA1_la_bitacora_no_muestra_impresiones_ni_tareas_fuera_del_alcance`, `Hardening5Tests › Toda_accion_de_auditoria_esta_clasificada` | Cerrado en 034 | 2026-10-27 || R-021-4 | Cambiar el calendario de una tarea activa la pausa; el superusuario debe reactivarla. | Media (est.) | Aceptado (por diseño: un calendario nuevo se revisa antes de correr) | `Hardening021Tests › H4_CA1_CA2_cambiar_el_contenido_de_una_tarea_activa_o_volver_a_declararla_la_deja_en_pausa` | Cerrado en 034 (con prueba) | 2026-10-27 || R-016-3 | Un registro que siempre falla ocupa lugar del lote en cada ocurrencia; si todo el lote falla, los demás esperan. | Media (est.) | Diferido (ver R-029-4): la prueba exige una tarea programada con registros que fallan; va con R-029-4 en la spec de pendientes | sin prueba (no cupo en la 034) | Abierto · **auditoría** (la auditoría 4 encontró lo contrario también: registros que se saltan para siempre, D-06) | 2026-10-27 || R-015-3 | Un administrador puede desactivar a otros sin aprobación (denegación de servicio auditada). | Media (est.) | Aceptado: reducir no espera aprobación (015). Desde la 034, solo a quien no tiene más capacidades que él | `HierarchyTests › Usuario_ampliar_espera_y_reducir_no`, `Hardening034Tests › Un_gestor_de_usuarios_no_cambia_la_contrasena_ni_desactiva_a_quien_tiene_mas_capacidades` | Cerrado en 034 | 2026-10-27 || R-011-2 | Inyección indirecta por metadatos mitigada, no eliminada: depende de que la persona lea antes de confirmar. | Alta (est.) | Aceptado (la mitigación está probada: advertencias de cambios destructivos y de seguridad, compilador, datos delimitados como no confiables, aplicar con huella por la persona) | `AssistantTests › Las_advertencias_destacan_cambios_destructivos_de_seguridad_y_externos`, `› Un_plan_hostil_que_se_interpreta_lo_rechaza_el_compilador`; `StudioTests › H5_CA2_un_texto_hostil_llega_escapado_como_dato`, `› H4_CA2_aplicar_lo_hace_la_persona_con_la_huella` | Cerrado en 034 (con prueba de la mitigación; el residuo es la lectura de la persona) | 2026-10-27 || R-011-1 | La estructura del modelo y el texto de la persona salen hacia Anthropic en cada solicitud del asistente (011) y en cada paso de ERP AI Studio (033); en Studio, con el alcance `datos`, también **valores de registros** que la persona ve (las salidas de `leer_registros` y `leer_registro`, como datos no confiables). Sin `datos`, solo metadatos y conteos (R-033-9). | Media | Aceptado (el alcance `datos` es opcional y se pide por sesión; la 038 (MCP) lo pedirá como consentimiento del token) | `StudioTests › H1_CA4_una_herramienta_fuera_de_los_alcances_no_se_ejecuta` (sin `datos` no viajan registros), `› H5_CA1_el_canario_no_sale_por_ninguna_herramienta` | Abierto (reescrito en 034, con prueba de su alcance) | 2026-10-27 || R-009-1 | Firma HMAC con clave compartida (cualquier entorno de la cadena firma) y sin clave configurada se aceptan paquetes sin firma. | Alta (est.) | La mitad «sin clave»: corregida en 034 (un entorno con aprobación no arranca sin `Environments:SigningKey`). La clave compartida sigue en R-034-1 | `EnvironmentTests › Con_aprobacion_y_sin_clave_no_arranca`, `Hardening034Tests › H2_CA1_un_entorno_con_aprobacion_sin_clave_no_arranca` | Cerrado en 034 (la clave compartida: R-034-1) | 2026-10-27 || R-008-4 | La importación interpreta fechas-hora sin zona como UTC y números en formato es-MX: «1,5» se toma como 15 sin aviso. | Media (est.) | Diferido (ver R-029-5) | `Hardening5Tests › R_008_4_una_coma_decimal_se_toma_como_separador_de_miles` (alcance: así se lee hoy) | Abierto (con prueba de su alcance, 034) | 2026-10-27 |# Registro de riesgos residuales

Un solo lugar para lo que quedó abierto al cerrar cada spec (spec 028, propuesta P10 de la
[retrospectiva 001](retrospectivas/001-specs-001-026.md)). Antes vivían dispersos en cada `verify.md`, sin estado ni fecha,
y tres se volvieron defectos reales (005 → 012-3, 020 → 022 F-04, 018 → 021 F-01).

## Reglas
- **Quién agrega:** el VERIFY de cada spec registra aquí sus riesgos (ID `R-NNN-k`, en la sección de su spec) y su
  `verify.md` enlaza a ellos (`riesgos.md#NNN`) en lugar de listarlos sueltos. Una amenaza del STRIDE sin prueba también
  viene aquí.
- **Severidad** con el criterio de [verificacion.md](verificacion.md#severidad); «(est.)» = estimada al migrar (los
  `verify.md` 001–027 no la daban).
- **Decisión:** Aceptado · Diferido a NNN · Por decidir. **Prueba:** archivo › título que demuestra el alcance del riesgo
  (que existe, hasta dónde llega o que ya no ocurre), o «sin prueba».
- **Un riesgo medio o alto sin prueba no queda aceptado más de 5 specs:** o se le escribe la prueba, o se corrige, o la
  auditoría lo vuelve a decidir con el dueño. Los que ya rebasan el tope llevan **auditoría** en *Estado*.
- **Quién revisa:** cada auditoría externa empieza por esta tabla y actualiza *Estado* y *Revisado*. Al cerrar un riesgo,
  se anota la spec y la prueba; la fila no se borra.


**Resumen (2026-10-29, al cerrar la 037):** 182 filas, 157 abiertas (contadas por su estado). La 037 cerró R-035-2 (la pantalla ya no sondea: flujo SSE) y registró R-037-1…3 (bajos). **Al cerrar la 035:** 173 riesgos, 143 abiertos. La 035 cerró R-033-4 y R-033-10, atendió la primera mitad de R-033-11 y registró R-035-1 y R-035-2 (bajos). **Al cerrar la 034 (auditoría 6):** 171 riesgos, 143 abiertos. La 034 cerró R-009-1 (la mitad sin clave; la clave compartida sigue en R-034-1), R-011-2, R-015-3, R-021-4, R-032-6 y R-033-5; reescribió R-011-1 para Studio y dio prueba de su alcance a R-008-4; R-016-3 quedó sin prueba (va con R-029-4); registró R-034-1…4. De los 28 medios o altos marcados para auditoría sin prueba quedan 22 (cerrados R-009-1, R-011-2, R-015-3 y R-021-4; con prueba R-008-4 y R-011-1). **Al cerrar la 033:** 167 riesgos, 145 abiertos; la 033 registró R-033-1…11 (R-033-4 medio, con prueba de su alcance). **Al cerrar la 032:** 156 riesgos, 134 abiertos; la 032 registró R-032-1…6 (R-032-6 medio, preexistente). **Al cerrar la 031:** 150 riesgos, 128 abiertos. La 031 (auditoría 5) amplió R-029-3 y R-029-6 (con prueba) y registró R-031-1…7. **Al cerrar la 030:** 143 riesgos, 121 abiertos. La 030 registró R-030-1…5 y actualizó R-028-2, R-028-3 y R-029-12 (con prueba de invariante). Al cerrar la 029: 138 riesgos, 116 abiertos. La 029 cerró R-006-1 y R-023-5 (auditoría 4 D-05 y D-01), dejó prueba a R-023-3 y R-024-3 y registró R-029-1…14. Siguen 28 medios o altos sin prueba aceptados hace más de 5 specs, marcados para la próxima auditoría: R-001-1, R-006-4, R-007-1, R-007-2, R-008-2, R-008-4, R-009-1, R-009-3, R-010-3, R-011-1, R-011-2, R-011-3, R-012-2, R-012-3, R-014-1, R-014-2, R-015-3, R-016-2, R-016-3, R-016-4, R-018-3, R-018-4, R-021-4, R-021-5, R-021-6, R-021-7, R-022-1, R-022-3.

Migración (028): filas tomadas de las secciones de riesgos y pendientes de cada `verify.md`, de las «Decisiones y diferidos» de la 012, de los hallazgos que quedaron pendientes al cerrar (004 F-25, 005 F-04, 006 F-03) y, para la 027 (sin sección de riesgos), del roadmap. Cada cierre se comprobó en la spec posterior. Quedaron fuera las notas de migración de `Bootstrap` (016–018) y el estado de la base `ErpAi` del dueño (022). **Dudas** para la primera auditoría: severidades «Alta (est.)»; R-023-5 frente al cierre de R-006-2; R-009-3 (si un paquete normal puede archivar un catálogo); R-006-4 (si `PUT /admin/users/{id}` sigue quitando la unidad tras el árbol de la 015); R-006-5 (cierre tomado del roadmap, sin prueba localizada); R-012-3/5/6 son decisiones de la 012 registradas por su efecto en la seguridad.


## 001

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-001-1 | Fecha-hora en UTC (F-02) sin prueba automática con una zona horaria distinta: una regresión pasaría la suite. | Media | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-001-2 | `icons.js` incluye todo Lucide (423 kB, 112 kB gzip); la lista cerrada quedó por revisar. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |

## 003

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-003-1 | La miga de pan de un documento muestra `#id` en lugar del folio. | Baja (est.) | Aceptado | sin prueba | Abierto (sigue en `Layout.tsx`: `#${id}`) | 2026-10-25 |
| R-003-2 | La recepción no actualiza el estado de la orden de compra. | Media (est.) | Diferido a 005 | `compras › Spec 005 · H2 CA1 · CA2` | Cerrado en 005 (automatizaciones acumulan y mueven la OC) | 2026-10-25 |
| R-003-3 | Procesos y reglas solo se editan como JSON en el modelador. | Baja (est.) | Diferido a 019 | `editores.spec.ts`, `visual-modelador.spec.ts` | Cerrado en 019/026 (diagrama de proceso; editores visuales de reglas) | 2026-10-25 |

## 004

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-004-1 | «Motivo de rechazo» capturable en Borrador (F-25). | Media | Diferido a 006 | `seguridad › H4 CA1` | Cerrado en 006 (`editableIn: []`, Compras 1.3.0) | 2026-10-25 |

## 005

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-005-1 | Dos recepciones simultáneas de la misma partida pueden pasar a la vez la regla «no más de lo pendiente». | Alta (est.) | Aceptado | `endurecimiento › H3 CA1`, `H3 CA3` | Cerrado en 012 (bloqueo previo a validar + regla en Compras 1.7.0; había sobre-recepción real) | 2026-10-25 |
| R-005-2 | Recepciones confirmadas antes de Compras 1.2.0 no tienen liga a la partida: sus cantidades no se reconstruyen. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-005-3 | Se ofrece «Nuevo» en *Existencias* aunque las crea el sistema (F-04). | Baja | Diferido a 006 | `seguridad.spec.ts` (Inventario 1.1.0) | Cerrado en 006 (`systemManaged`) | 2026-10-25 |

## 006

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-006-1 | Una fórmula visible que depende de un campo oculto revela el dato derivado; depende de que el modelador oculte también los derivados. | Alta (est.) | Corregido en 029 | `Hardening3Tests › Una_formula_que_lee_un_campo_oculto_se_oculta`, `canario › H3 CA2` | Cerrado en 029 (auditoría 4 D-05: `DerivedAccess`, también en orden, filtros y reportes; el *Costo* de la recepción en Compras) | 2026-10-25 |
| R-006-2 | La etiqueta `__label` de una referencia muestra el registro referido aunque el usuario no pueda abrirlo. | Alta (est.) | Aceptado | `endurecimiento › H6 CA1 · CA2` | Cerrado en 012 (fuera de alcance → «(sin acceso)») | 2026-10-25 |
| R-006-3 | La auditoría muestra valores completos a quien tiene ese permiso. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-006-4 | `PUT /admin/users/{id}` sin `unit` borra la unidad (igual que `roleIds` vacío quita roles). | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-006-5 | El modelador ofrece «Revisar cambios» sin cambios en *Orden de compra* (renumera `displayOrder`) (F-03). | Baja | Diferido a 007 | sin prueba | Cerrado en 007 (según roadmap §007) | 2026-10-25 |

## 007

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-007-1 | Las agrupaciones por fecha-hora usan UTC: un registro de las 23:30 en México cuenta en el día siguiente. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-007-2 | Un reporte sobre partidas no filtra por campos del documento; «Productos más comprados» incluye órdenes canceladas. | Media (est.) | Aceptado | sin prueba | Abierto (023 CA3: los filtros de reporte siguen sin rutas) · **auditoría** | 2026-10-25 |
| R-007-3 | La visibilidad condicional es presentación: el valor oculto por condición se conserva y se guarda. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-007-4 | Los listados no se exportan. | Baja (est.) | Diferido a 008 | `integraciones.spec.ts` | Cerrado en 008 (exportación de listados) | 2026-10-25 |
| R-007-5 | La exportación de reportes se limita a 1000 grupos. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 008

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-008-1 | Webhooks «al menos una vez» y sin orden: el receptor debe deduplicar por `X-ERP-Delivery`. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-008-2 | URL y secreto de webhooks dependen de las llaves de Data Protection; si se pierden, las entregas fallan hasta reconfigurar. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-008-3 | La carga del webhook es una foto del evento; un cambio posterior llega como otro evento. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-008-4 | La importación interpreta fechas-hora sin zona como UTC y números en formato es-MX. | Media (est.) | Diferido (ver R-029-5) | sin prueba | Abierto · **auditoría** (auditoría 4 D-07: «1,5» se toma como 15) | 2026-10-25 |
| R-008-5 | La importación solo da de alta: no actualiza registros ni importa partidas. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |

## 009

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-009-1 | Firma HMAC con clave compartida (cualquier entorno de la cadena firma) y sin clave configurada se aceptan paquetes sin firma. | Alta (est.) | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-009-2 | El paquete archiva al aplicar un campo agregado en el destino por otro medio (no se fusiona). | Media (est.) | Diferido a 012/014 | `entornos › H2 CA1 + H4 CA1` | Cerrado en 014 (archivar campos locales se confirma aparte) | 2026-10-25 |
| R-009-3 | Archivar un catálogo completo no viaja en el paquete. | Media (est.) | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-009-4 | Un paquete aplicado no se revierte (hay que promover otra versión). | Media (est.) | Diferido a 017 | `rollback.spec.ts` | Cerrado en 017 (reversión con instantánea y diff inverso) | 2026-10-25 |
| R-009-5 | Una vista compartida filtrada por un registro lleva su id: en otro entorno puede apuntar a otro registro. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-009-6 | Un metadato nuevo del motor debe agregarse a `ModelDiff`; solo se detecta si los módulos lo usan. | Media (est.) | Aceptado | `EnvironmentTests` (ida y vuelta) | Abierto | 2026-10-25 |

## 010

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-010-1 | Folios únicos en todo el entorno (sin series por empresa); un catálogo por empresa no vuelve a ser compartido. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-010-2 | Una referencia de un catálogo compartido a uno por empresa muestra la etiqueta de un registro de otra empresa. | Alta (est.) | Aceptado | `endurecimiento › H6 CA1 · CA2` | Cerrado en 012 («(sin acceso)» para otra empresa) | 2026-10-25 |
| R-010-3 | La conversión se fija al guardar: un tipo de cambio capturado después no recalcula; cambiar moneda no vuelve a sugerir el tipo. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-010-4 | Tipos de cambio solo por superusuario, sin triangulación ni consolidación entre empresas. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-010-5 | Las claves de API trabajan en la empresa de quien las creó (no se cambia desde la UI). | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 011

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-011-1 | La estructura del modelo y el texto de la persona salen hacia Anthropic en cada solicitud. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-011-2 | Inyección indirecta por metadatos mitigada, no eliminada: depende de que la persona lea antes de confirmar. | Alta (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-011-3 | La clave de Anthropic depende de las llaves de Data Protection; si se pierden hay que recapturarla. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-011-4 | Un catálogo creado y modificado en el mismo plan no genera advertencias de reemplazo. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-011-5 | Sin respuestas en flujo: una propuesta grande tarda decenas de segundos. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-011-6 | El JSON del plan mostrado incluye propiedades calculadas en nulo o falso (ruido). | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-011-7 | Sin respaldo si el proveedor rechaza por seguridad (`refusal`): hay que reformular. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 012

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-012-1 | Un acumulado por `match` (sin `target`) no se bloquea antes de validar. | Media (est.) | Diferido a 014 | `pendientes › H6 CA1` | Cerrado en 014 (bloqueo por clave antes de insertar) | 2026-10-25 |
| R-012-2 | Una regla que lee otro documento sin automatización no se bloquea antes de validar (concurrencia). | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-012-3 | La bitácora de un registro cuyo catálogo cambió de política se filtra con la política actual. | Media (est.) | Aceptado | sin prueba | Abierto (confirmado como decisión en 014) · **auditoría** | 2026-10-25 |
| R-012-4 | Un entorno mal configurado con `AllowDirectChanges` reabre el atajo de cambios directos en producción. | Alta (est.) | Aceptado | `pendientes › H6 CA2` (solo el aviso) | Abierto (014 agrega aviso, no lo impide) | 2026-10-25 |
| R-012-5 | Las transiciones con roles no exigen el alcance de escritura (autorización de proceso). | Media (est.) | Aceptado | `endurecimiento › H2 CA1 · CA2` | Abierto | 2026-10-25 |
| R-012-6 | El superusuario ve la bitácora sin filtro de seguridad ni de empresa. | Baja (est.) | Aceptado | `endurecimiento › H1 CA1 · CA2 · CA3` | Abierto | 2026-10-25 |
| R-012-7 | Acumulado en memoria con tope de 10 000 fuentes (falla con mensaje). | Media (est.) | Diferido a 014 | `pendientes › H2 CA1` | Cerrado en 014 (suma en SQL) | 2026-10-25 |
| R-012-8 | La vista previa de importación retiene el bloqueo de folios y detiene las altas del catálogo. | Media (est.) | Diferido a 014 | `pendientes › H3 CA1`, `H3 CA2 · CA3` | Cerrado en 014 (bloque reservado sin detener altas) | 2026-10-25 |

## 013

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-013-1 | El listado no calcula `_access` por fila; una edición en línea futura deberá pedirlo. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 014

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-014-1 | Cambiar la moneda de una orden después de capturar partidas no recalcula los precios sugeridos. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-014-2 | Un acumulado por `match` sobre campos sin índice escanea la tabla y puede interbloquearse con altas simultáneas; el modelador no lo impide. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-014-3 | Una importación fallida con altas simultáneas deja un hueco en la numeración del tamaño de su bloque. | Baja (est.) | Aceptado | `pendientes › H3 CA2 · CA3` | Abierto | 2026-10-25 |
| R-014-4 | `AllowDirectChanges` se advierte pero no se impide (configuración del servidor). | Alta (est.) | Aceptado | `pendientes › H6 CA2` | Fusionado en R-012-4 | 2026-10-25 |

## 015

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-015-1 | Con un solo administrador nada se puede aprobar (ADR-0005). | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-015-2 | Si el proceso se detiene entre el reclamo y la operación, la solicitud queda en `applying` y hay que liberarla a mano. | Media (est.) | Aceptado | `endurecimiento-2 › H8 CA1` | Cerrado en 021 (las solicitudes atoradas se liberan) | 2026-10-25 |
| R-015-3 | Un administrador puede desactivar a otros sin aprobación (denegación de servicio auditada). | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-015-4 | El filtro `unitTree` usa una subconsulta sin índice en `meta.[User].Unit`; lento con miles de usuarios. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 016

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-016-1 | Trabajo asíncrono «al menos una vez»: si tarda más que el lease (5 min) otra instancia puede repetirlo. | Media (est.) | Aceptado | `endurecimiento-2 › H3 CA1`, `H3 CA4` | Cerrado en 021 (éxito en la transacción, dueño y renovación del lease, tope de intentos); resta R-021-3 | 2026-10-25 |
| R-016-2 | La tarea no recuerda qué procesó: si su `where` sigue cumpliéndose, reprocesa los mismos registros. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-016-3 | Un registro que siempre falla ocupa lugar del lote en cada ocurrencia; si todo el lote falla, los demás esperan. | Media (est.) | Diferido (ver R-029-4) | sin prueba | Abierto · **auditoría** (la auditoría 4 encontró lo contrario también: registros que se saltan para siempre, D-06) | 2026-10-25 |
| R-016-4 | La hora de las tareas es la local del servidor (en un servidor UTC, «07:00» es 07:00 UTC). | Media (est.) | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-016-5 | Una tarea en catálogo compartido corre con la empresa predeterminada. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 017

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-017-1 | La reversión no deshace datos; los registros de un catálogo archivado dejan de verse hasta reactivarlo. | Media (est.) | Aceptado | `rollback.spec.ts` (H2 CA3) | Abierto | 2026-10-25 |
| R-017-2 | Un campo obligatorio o folio reactivado queda vacío en registros creados mientras estuvo archivado. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-017-3 | Un ciclo de referencias entre catálogos creados por el paquete bloquea la reversión. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-017-4 | Los paquetes aplicados antes de la 017 no tienen instantánea y no se pueden revertir. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-017-5 | «Firmado y verificado» usa la clave vigente; si cambia, la verificación de la subida no se puede repetir. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-017-6 | El contexto del asistente no lleva lo archivado: solo propone reactivar lo que la persona nombre. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 018

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-018-1 | OData sin escritura, `$batch`, filtros por rutas, `$expand` anidado ni nombres de usuario (responde 400). | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-018-2 | Sin introspección ni revocación de tokens por el cliente (RFC 7662/7009). | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-018-3 | `/odata` anónimo responde 401 sin `WWW-Authenticate: Bearer`; Excel/Power BI pueden no ofrecer inicio de sesión. | Media (est.) | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-018-4 | `@odata.context`/`nextLink` y URLs de *Clientes OAuth* salen del `Host`; detrás de un proxy requieren `ForwardedHeaders`. | Media (est.) | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-018-5 | `meta.OAuthToken` solo se purga al emitir: un cliente inactivo deja sus filas. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-018-6 | *Claves de API* deja el error de roles al final de la lista, fuera de la vista, y su tabla no es región enfocable. | Media (est.) | Diferido a 030 (P9, guardia de error fuera de la vista) | sin prueba | Abierto (021 F-01 corrigió `SecurityPage`, no `KeyDialog` de `IntegrationsPage.tsx`) | 2026-10-25 |

## 019

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-019-1 | El diagrama del proceso no se reacomoda a mano; con muchos estados puede quedar denso. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-019-2 | Eliminar un estado guardado deja registros en ese valor sin transiciones (el motor no lo impide). | Media (est.) | Aceptado | `endurecimiento-2 › H8 CA3` | Cerrado en 021 (no se quita un estado con registros) | 2026-10-25 |
| R-019-3 | La vista previa del formulario no evalúa condiciones (solo las marca). | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-019-4 | Sin deshacer/rehacer en el diseñador; «Cancelar» descarta todo el borrador. | Baja (est.) | Por decidir | sin prueba | Abierto (sigue pendiente en 026) | 2026-10-25 |
| R-019-5 | La lógica de edición de la SPA (`layoutEdit.ts`, `workflowEdit.ts`) solo se prueba por E2E: no hay unitarias de JavaScript. | Media (est.) | Diferido a 030 (P14, Vitest) | sin prueba | Abierto (retrospectiva P14) | 2026-10-25 |

## 020

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-020-1 | Dos partidas FIFO del mismo producto en un traspaso leen las mismas capas; la entrada puede diferir de la salida. | Media (est.) | Aceptado | `valuacion-politicas › H1 CA3` | Cerrado en 022 (la salida captura su costo y la entrada entra a ese total) | 2026-10-25 |
| R-020-2 | En una salida el formulario ofrece capturar costo unitario, pero el costeo lo reemplaza. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-020-3 | Las listas muestran decimales sin ceros a la derecha junto a fórmulas con dos decimales. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-020-4 | El encabezado de un registro sin folio muestra `#id`, no el campo para mostrar. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-020-5 | Sin póliza de salidas (costo de ventas). | Media (est.) | Diferido a 031 (Ventas) | sin prueba | Abierto | 2026-10-25 |
| R-020-6 | Sin revaluación, pólizas en moneda extranjera ni reversa de pólizas con otra póliza. | Media (est.) | Diferido (roadmap, después de 032) | sin prueba | Abierto | 2026-10-25 |
| R-020-7 | Las capas no se reconstruyen si se borra un movimiento por fuera del motor. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 021

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-021-1 | Si el catálogo cambió mientras se editaba, el borrador no se fusiona: hay que reabrir y repetir el cambio. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-021-2 | Un rol que gana capacidades después de solicitarse un cambio de usuario da al aprobar un mensaje distinto del de la lista. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-021-3 | Un trabajo asíncrono de más de 5 min que no puede renovar su lease (base caída) se reintenta completo. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-021-4 | Cambiar el calendario de una tarea activa la pausa; el superusuario debe reactivarla. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-021-5 | El límite de `/oauth/token` detrás de un proxy necesita `ForwardedHeaders` con los proxies conocidos. | Media (est.) | Diferido a 030 o spec propia (auditoría 4) | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-021-6 | El aviso de cambios sin aplicar no salta al navegar dentro de la SPA (requiere *data router*). | Media (est.) | Diferido a 030 o spec propia (auditoría 4) | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-021-7 | Los paquetes normales no listan ni piden confirmar los cambios de seguridad (solo las reversiones). | Media (est.) | Por decidir | sin prueba | Abierto · **auditoría** | 2026-10-25 |

## 022

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-022-1 | En expresiones un vacío vale 0 en aritmética y en `ROUND`/`ABS`/`MIN`/`MAX` (F-04): cada fórmula debe protegerse con `IF(ISBLANK(...))`. | Alta (est.) | Por decidir | sin prueba (la regresión de F-04 cubre solo Inventario 1.6.1) | Abierto · **auditoría** | 2026-10-25 |
| R-022-2 | Recepciones en borrador anteriores a Inventario 1.5.0 sin `Moneda`: la fórmula del costo no usa la de la orden. | Media (est.) | Diferido a 023 | `H3_CA4_recepcion_sin_moneda_usa_la_moneda_de_la_orden` | Cerrado en 023 (Inventario 1.7.0; 023 F-02: el riesgo era menor) | 2026-10-25 |
| R-022-3 | Reemplazar todas las reglas sin `merge` quita las políticas que otro módulo agregó. | Media (est.) | Aceptado | sin prueba | Abierto · **auditoría** | 2026-10-25 |
| R-022-4 | Existencias anteriores a Costeo 1.1.0 sin cantidad costeada hasta su siguiente movimiento («Costeo incompleto»). | Baja (est.) | Aceptado | `valuacion-politicas › H6 CA2 · H1 CA1 · CA2` | Abierto | 2026-10-25 |
| R-022-5 | Sin la regla `PolizaConCuentas`, la póliza pendiente se captura a mano; generarla después no existe. | Media (est.) | Por decidir | `valuacion-politicas › H7 CA2` | Abierto | 2026-10-25 |
| R-022-6 | El rol *Contador* no lee recepciones por defecto (dato del entorno, ADR-0004). | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 023

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-023-1 | No se puede quitar un elemento con `merge`: requiere el reemplazo completo. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-023-2 | El dueño de un catálogo extendido debe usar `merge` (convención); la prueba de paquetes solo revisa catálogos ajenos. | Media (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-023-3 | Un aviso cuya regla lee un campo oculto no se muestra ni se pide confirmar a ese usuario. | Baja (est.) | Aceptado | `canario › H4 CA3` | Abierto (029: con `block` tampoco se lista; el mensaje es genérico) | 2026-10-25 |
| R-023-4 | `block` muestra los avisos como errores sin decir que son avisos. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-023-5 | Los saltos 2 y 3 de una ruta leen el registro apuntado sin revisar su alcance por registro (sí la seguridad por campo). | Alta | Corregido en 029 (decisión 2) | `canario › H2 CA2`, `› H3 CA1`, `› H3 CA2` | Cerrado en 029 (auditoría 4 D-01: lo vivo revisa cada salto; las fórmulas guardadas que cruzan un catálogo restringido se ocultan) | 2026-10-25 |

## 024

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-024-1 | El último módulo se recuerda en el navegador, no en el servidor. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-024-2 | Un error de dedo en `group` crea un módulo implícito nuevo. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-024-3 | La búsqueda de folios recorre hasta 30 catálogos por consulta; con muchos más habrá que indexarla. | Baja (est.) | Aceptado | `capacidades › H5 CA3` (límite) | Abierto (029, auditoría 4 D-09: 60 por minuto por usuario; sin índice) | 2026-10-25 |
| R-024-4 | Un tablero que mezcla módulos aparece en todos ellos. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 025

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-025-1 | No se compara la base de dos entornos. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-025-2 | No se sugiere limpiar columnas archivadas (con respaldo y aprobación). | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-025-3 | Las flechas que suben de columna en un ciclo se dibujan por fuera sin carril. | Baja (est.) | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 026

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-026-1 | Lo que llegó por paquetes de Entornos aparece como «Local» en el origen. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-026-2 | Sin fusión de tres vías al actualizar un módulo: gana la actualización (la vista previa lista los choques). | Media (est.) | Aceptado | `visual-modelador.spec.ts` (choques) | Abierto | 2026-10-25 |
| R-026-3 | Agrupar reportes por rutas de más de un salto y la etiqueta de un filtro de vista solo en «Ver como JSON». | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-026-4 | `lookupFilter` de un campo no se edita visualmente (solo por paquete o asistente). | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-026-5 | Sin deshacer/rehacer en los editores ni quitar con `merge` (ver R-019-4, R-023-1). | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |

## 027

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-027-1 | Sin códigos QR ni de barras en plantillas (los pedirá el CFDI). | Baja (est.) | Diferido a 032 (CFDI) | sin prueba | Abierto | 2026-10-25 |
| R-027-2 | Sin envío por correo, adjunto automático en transiciones ni firma electrónica del PDF. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |
| R-027-3 | Sin tipo de campo `image`/`file` en catálogos, SVG/WebP ni diseño libre por coordenadas. | Baja (est.) | Por decidir | sin prueba | Abierto | 2026-10-25 |

## 028

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-028-1 | Dos corridas de la suite en la **misma** carpeta no pueden ir a la vez: la segunda se detiene por el candado de la base (antes se borraban la base); solo carpetas distintas corren en paralelo. | Baja | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-028-2 | La suite no se puede fragmentar (`--shard`): 13 archivos dependen de otros y las capturas dependen del menú que dejan los demás. | Media | Aceptado (030: las pruebas de API pasaron a integración y ya no dependen de Compras; CI corre la suite completa en un solo trabajo, ~7 min en local; se fragmenta cuando la corrida de CI pase de 30 min) | sin prueba (dependencias listadas en `verificacion.md`) | Abierto | 2026-10-25 |
| R-028-3 | Con `E2E_INDEPENDENT=1`, tras una falla Playwright vuelve a correr `beforeAll`; 24 de los 25 archivos seriales no toleran datos que ya existen y pueden fallar en cascada (solo al verificar contra el código anterior). | Baja | Aceptado (030: las regresiones de API se verifican en integración con `it:regresion`, una base por clase y sin cascada; `endurecimiento-2.spec.ts` ya tolera lo existente) | `npm --prefix web run it:regresion` (012-3 y 021-3 contra `cd81760` y `f6c5445`) | Abierto | 2026-10-25 |

## 029

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-029-1 | Lo relacionado (`Ref.Campo`, plantillas de PDF, `UNITCOST`) de un registro al alcance se lee aunque el rol no pueda abrir ese catálogo (`RelatedLoader.Visible` no exige `Access(e).Read`, como `__label`); la seguridad por campo sí se revisa en cada salto. | Media | Aceptado (decisión 3 de la 029; auditoría 4, especulativo) | sin prueba | Abierto | 2026-10-25 |
| R-029-2 | Un valor por defecto que lee un campo oculto escribe el dato en un campo capturable que el usuario ve. | Media | Aceptado (decisión 5: lo decide el modelador) | sin prueba | Abierto | 2026-10-25 |
| R-029-3 | Las reglas de error, las condiciones de transición y **si un bloqueo por avisos (`onWarning: block`) bloquea** se evalúan con la autoridad del motor: su mensaje fijo o «permitida / no permitida / bloqueada» puede revelar un bit de un dato oculto o fuera de alcance. Auditoría 5: también los avisos de la importación y el 428/400 de una transición lo revelaban; la 031 los evalúa con el alcance de la persona (lo que se le dice = `/compute`) y solo queda el bit de si bloquea. | Media | Aceptado (029 decisión 6; 031 decisión 2: lo que se guarda y decide vale igual para todos) | `Hardening031Tests › H1_CA3_un_bloqueo_dice_lo_que_la_persona_ve…` (el bloqueo es del motor; el mensaje, de la persona) | Abierto | 2026-10-25 |
| R-029-4 | Auditoría 4 D-06: una tarea programada se salta para siempre un registro que falla (`RecordService.Background.cs`). | Media | Diferido a 030 o spec propia | sin prueba | Abierto | 2026-10-25 |
| R-029-5 | Auditoría 4 D-07: la importación toma «1,5» como 15 y las fechas con desfase mal (`RecordService.Import.cs`). | Media | Diferido a spec propia | sin prueba | Abierto | 2026-10-25 |
| R-029-6 | En Inventario y Costeo los costos que copian las automatizaciones (`MovimientoInventario.CostoUnitario`, valor de `Existencia`, capas) salen del precio de la orden pero son campos guardados, no fórmulas: el almacenista los ve. Auditoría 5 (A-03): era peor, los leía cualquier rol con acceso a esos catálogos, también la cuenta de servicio «Integración de tienda» por la API y OData. **031:** Costeo 1.2.0 los deja solo a supervisor, almacenista y contador; sigue abierto que el almacenista (captura entradas con costo) deduzca el precio de compra. Sin Costeo, `MovimientoInventario.CostoUnitario` (Inventario 1.5) no tiene seguridad por campo. | Media | Por decidir (el almacenista: decisión del dueño) | `Hardening031Tests › H2_CA1_la_integracion_de_tienda_lee_existencias_sin_costos…` | Abierto | 2026-10-25 |
| R-029-7 | El tope de 12 MP de imágenes por PDF (422) solo tiene revisión de código: probarlo pide subir tres imágenes de 6 MP en la suite. | Baja | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-029-8 | Instalar o actualizar un módulo (superusuario, paquetes del servidor) y un plan con `setSecurity` cambian roles, permisos y seguridad del catálogo sin la aprobación de seguridad de la 015; su compuerta es el cambio directo. | Media | Por decidir (ver R-021-7) | `RouteInventoryTests › Cambiar_el_modelo_o_la_seguridad_pasa_por_su_compuerta` (declara la compuerta, no la aprobación) | Abierto | 2026-10-25 |
| R-029-9 | Una fórmula guardada que recorre un catálogo con alcance restringido se oculta entera a ese usuario, aunque el registro de esa fila sí lo vea (más estricto que Salesforce); para mostrarlo hay que copiarlo con una automatización. | Baja | Aceptado (decisión 2) | `Hardening3Tests › Una_formula_que_recorre_un_catalogo_con_alcance_restringido…` | Abierto | 2026-10-25 |
| R-029-10 | `GET /api/users/lookup` lista usuarios y equipos (nombre) a cualquier sesión: los campos de tipo usuario lo necesitan. | Baja | Aceptado | `rutas.md` (capacidad «sesión») | Abierto | 2026-10-25 |
| R-029-11 | Un aviso cuya regla lee una fórmula que el usuario ya no ve (029) deja de mostrársele y de pedirle confirmar: con `PolizaConCuentas` en aviso, el almacenista confirma la recepción sin aviso (lee `CostoTotal`, que sale del precio). La regla de error (lo predeterminado) sigue bloqueando a todos. | Media | Aceptado (R-023-3: mostrarlo revelaría el dato) | `valuacion-rutas-avisos › H2 CA6` (confirma quien ve el costo) | Abierto | 2026-10-25 |
| R-029-12 | Revisión adversarial F-A: la huella del módulo se comparaba sin el candado del modelo; corregido (`PlanService.LockModelAsync` antes de comparar), pero la carrera entre un plan y la instalación no se puede forzar: la prueba de invariante pasa aun quitando el candado (030), porque sin un gancho de prueba la ventana entre comparar y aplicar no se abre. | Media | Aceptado (030: invariante probada; forzar la ventana pide un gancho de prueba en `ModuleService`) | `ModuleRaceTests › H2_CA3_un_plan_y_la_instalacion_de_un_modulo_a_la_vez…` (invariante, no detecta la mutación) | Abierto | 2026-10-25 |
| R-029-13 | Revisión adversarial F-D: si un camino de lectura usara un modelo sin anotar (copia con `with` de un campo fórmula), `DerivedAccess` no ocultaría nada; hoy todos leen el de `MetadataStore.LoadAsync`. | Baja | Aceptado | `Hardening3Tests › Sin_anotar_el_modelo_las_reglas_son_las_de_antes` (documenta el comportamiento) | Abierto | 2026-10-25 |
| R-029-14 | Revisión adversarial F-E: un render de PDF que se cuelga retiene su lugar del semáforo sin plazo; con 4 colgados no hay PDF hasta reiniciar, y no hay métrica de lugares ocupados. | Baja | Aceptado (mejor que rebasar el límite, D-02) | `Hardening3Tests › El_lugar_del_semaforo…` | Abierto | 2026-10-25 |

## 030

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-030-1 | Con varias instancias sobre la misma base, el modelo en caché de cada una (`MetadataStore`, 30 s) no ve al instante un cambio hecho en otra: hasta 30 s responde 404 a un catálogo nuevo o valida con reglas viejas. Hoy se despliega una sola instancia. | Media | Por decidir (antes de desplegar más de una instancia: versión del modelo en la base o aviso entre instancias) | `MultiInstanceTests › R_030_1_una_segunda_instancia_no_ve_al_instante…` | Abierto | 2026-10-25 |
| R-030-2 | El workflow de CI no se puede probar fuera de GitHub (sin `act`; Docker detenido): un error del workflow o de la imagen `windows-2025` (LocalDB, `sqlcmd`, fuentes de las capturas) se descubre en el push. Un *pull request* de un fork corre solo el trabajo rápido con permisos de lectura y sin secretos. | Baja | Aceptado | primera corrida de CI registrada en `specs/030-integracion-ci/verify.md` | Abierto | 2026-10-25 |
| R-030-3 | 16 de las 23 pantallas de la SPA no tienen captura de referencia (listas, reportes, tableros y la configuración: muestran datos de cada corrida); un cambio visual en ellas solo lo ven axe y las guardias. | Media | Aceptado (cada una tiene su razón escrita; captura con datos fijos en una spec de UI) | `web/src/__tests__/pantallas.test.ts › aviso: pantallas sin captura de referencia` | Abierto | 2026-10-25 |
| R-030-4 | Las guardias de foco y alerta no cubren: páginas de contextos propios (`browser.newContext()`), escrituras que no siguen a un clic o Enter en 300 ms (validación en vivo, autoguardado), respuestas 428 (confirmación de avisos) ni estados intermedios de desborde (solo se mide al terminar cada prueba). | Baja | Aceptado | `helpers.ts` (opción `guardias`) | Abierto | 2026-10-25 |
| R-030-5 | La primera corrida de CI (`257c438`, `windows-2025`) falló en la suite E2E a los 11 min sin reporte y sin que se pudiera saber qué prueba (los registros piden sesión); la siguiente, con el mismo código de producto, pasó en 8 min 13 s. Hay al menos una prueba intermitente en el runner que no se conoce. | Media | Por decidir (la siguiente falla de CI deja su motivo en las anotaciones; la sesión que la vea la vuelve hallazgo). **2026-10-27 (CI de la 033, `b2ad44f`):** las anotaciones nombraron dos: `multiempresa › H4 CA1` (carrera de la prueba: llenaba el siguiente tipo de cambio antes de que la respuesta anterior vaciara el campo; corregida, reproducida con 700 ms de retraso en el PUT) y `jerarquia › Spec 018 H1 CA2` (`read ECONNRESET` en `GET /api/admin/oauth-clients`; no se reproduce en local con `--veces=3`); en `8c2b53e`, `visual-modelador › H3 CA4` (escribía antes de que cargaran las funciones del editor) y `explorador › H1 CA9` (leía el foco antes de que llegara), corregidas; en `09ffa0e`, `integraciones › H1 CA4` (tras rotar el secreto tomaba la última entrega cuando veía el aviso de la prueba **anterior**, aún visible, y validaba el ping viejo con el secreto nuevo), corregida: cada «Probar» espera su propio ping; en `ccf8588` (solo `verify.md`), la captura `explorador › visual: diagrama ER … tema oscuro` difirió un 2 % con el mismo código que pasó en la corrida anterior: sin la imagen de diferencias (el artefacto pide sesión) la causa no se confirmó; en local ni el reajuste del diagrama ni otra caja bajo el puntero la reproducen. La prueba ahora fija el puntero, las fuentes y las cajas quietas. | `multiempresa.spec.ts › H4 CA1`, `visual-modelador.spec.ts › H3 CA4`, `explorador.spec.ts › H1 CA9`, `integraciones.spec.ts › H1 CA4` (corregidas; todas menos explorador reproducidas con un retraso) · el ECONNRESET y la captura del explorador, sin causa confirmada | Abierto (la mitad) | 2026-10-27 |

## 031

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-031-1 | `Producto.CostoUltimo` (Compras) lo lee la cuenta de servicio «Integración de tienda» (lee `Producto`) por la API y OData. Protegerlo pide que Compras nombre roles de Inventario (no lo requiere) o un permiso por campo para cuentas de servicio (lista de campos por rol de servicio, como los `fields` del webhook). | Media | Diferido a la spec de pendientes (capacidad nueva del motor) | sin prueba | Abierto | 2026-10-25 |
| R-031-2 | Si el cálculo de los avisos para la persona falla (`ExprException`, `AppException`, aritmética: revisión adversarial F-F), no se le dice ningún aviso y la operación sigue con la decisión del motor; un error de SQL en ese cálculo sí aborta la importación o la transición (no se atrapa: puede dejar la transacción condenada). Sin prueba con la falla inyectada. | Baja | Aceptado (la decisión no cambia; solo lo que se le dice) | sin prueba | Abierto | 2026-10-25 |
| R-031-3 | El límite por usuario de tableros, reportes y OData (`Security:AggregatesPerMinute`, 120) no tiene prueba de su 429: la suite lo sube a 1000 para no frenarse. Revisión adversarial F-J: un cliente de OData que pagina (Excel, Power BI con `@odata.nextLink`) recibe 429 a mitad de una actualización con catálogos de más de ~60 000 filas; no hay límite aparte para cuentas de servicio. | Baja | Aceptado (configurable por entorno; la declaración y su política sí se prueban) | `RouteInventoryTests › Lo_que_agrega_o_recorre_un_catalogo_es_costoso` | Abierto | 2026-10-25 |
| R-031-4 | Revisión adversarial F-D: `GainsMember` compara nombres de unidad sin distinguir mayúsculas (ordinal), pero la base los compara con su intercalación: con una insensible a acentos, «Logistica» pasa la validación como «Logística» (la unidad de quien revisa) y no se detecta la ganancia. La carga guarda la unidad como la escribió quien solicita. | Baja | Por decidir (guardar el nombre canónico o el Id de `meta.BusinessUnit`) | sin prueba | Abierto | 2026-10-25 |
| R-031-5 | Revisión adversarial F-E: la política por omisión (`FallbackPolicy`) solo niega rutas **sin** autorización; una ruta nueva dentro de un grupo con `.RequireAuthorization()` y sin `.Requires(...)` respondería a cualquier sesión. No se puede negar con `DefaultPolicy` porque los grupos la combinan con la de cada ruta. Lo detecta la prueba de inventario (una capacidad por ruta), no el pipeline. | Baja | Aceptado (quitar `.RequireAuthorization()` de los grupos en una spec propia) | `RouteInventoryTests › Cada_ruta_declara_exactamente_una_capacidad` | Abierto | 2026-10-25 |
| R-031-6 | Revisión adversarial F-G: si una automatización asíncrona o una tarea programada bloquea por avisos y quien disparó el evento no ve el registro, la ejecución guarda el mensaje genérico y, como se revierte, la auditoría tampoco guarda los avisos del motor: el superusuario no sabe cuál bloqueó. | Baja | Aceptado (diagnóstico; registrarlo en la ejecución en una spec propia) | sin prueba | Abierto | 2026-10-25 |
| R-031-7 | Revisión adversarial F-H (R-029-3): la vista previa de la importación devuelve fila por fila «válida o con error» con el mensaje de las **reglas de error**, que se evalúan con la autoridad del motor: una regla de error que lee un registro fuera de alcance o un campo oculto da un bit por fila sin efectos (hasta 1000 filas, 20 por minuto). Lo mismo el 400 de una automatización por una regla de error (`FirstMessage`). | Media | Diferido a la spec de pendientes (filtrar los mensajes de reglas de error que leen ocultos, como `VisibleWarnings`) | sin prueba | Abierto | 2026-10-25 |

## 032

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-032-1 | Revisión adversarial F-07 a/b: la guarda `if (busy) return` de `onSubmit` y la condición «había sesión» del manejador de 401 no tienen prueba propia: el doble envío lo impide el `Button` en espera (probado) y el 401 rezagado, la generación de sesión (probada). Quitarlas no rompe ninguna prueba. | Baja | Aceptado (defensa en profundidad) | `login.spec.ts › H3 CA1 y CA4` · `sessionGeneration.test.ts` | Abierto | 2026-10-25 |
| R-032-2 | Enumeración por tiempos (preexistente, 001): con un usuario existente y contraseña incorrecta, el login hace un `UPDATE` de intentos que no hace con uno inexistente; la diferencia de tiempo es medible en red local. La pantalla no agrega diferencias. | Baja | Aceptado (el *rate limit* por dirección y el bloqueo acotan el muestreo) | sin prueba | Abierto | 2026-10-25 |
| R-032-3 | Revisión adversarial F-07 d: `login.spec.ts` deja dos intentos fallidos por corrida en `e2e-login-fallido`; sobre la misma base, a la tercera corrida la cuenta queda bloqueada y el caso «contraseña incorrecta» se vuelve «bloqueada» (mismo mensaje: la prueba no falla, pero cubre menos). `run.mjs` recrea la base en cada corrida y vuelta. | Baja | Aceptado | `login.spec.ts › H4 CA1` (comprueba que otra cuenta sana sí entra) | Abierto | 2026-10-25 |
| R-032-4 | `prefers-reduced-motion` solo se respeta en el login: el resto de la SPA conserva sus transiciones, el indicador de espera y la entrada de diálogos y avisos. | Baja | Diferido (una regla global con su prueba, en la siguiente spec que toque estilos) | `login.spec.ts › H2 CA1` (alcance: solo el login) | Abierto | 2026-10-25 |
| R-032-5 | El minificador de CSS de Vite convierte en silencio `animation: 5.6s linear infinite` (sin nombre) en `animation: none`: una animación así desaparece en producción sin error. | Baja | Aceptado (propiedades largas; lección en CLAUDE.md) | `login.spec.ts › H2 CA2` (lista exacta de animaciones) | Abierto | 2026-10-25 |
| R-032-6 | Preexistente, visto en el VERIFY de la 032 (F-08): `jerarquia.spec.ts › H5 CA1` falla cuando corre solo después de `h5-h6-seguridad.spec.ts` (también con el código de la 031, `71246b2`; en la suite completa y solo, pasa): la bitácora de `gina` (alcance de unidad) incluye un evento de `J15Caso` del caso de J15 Sur. Hipótesis sin confirmar: `AuditFilter.Build` deja pasar a cualquiera con permiso de auditoría las acciones que no están en `RecordActions` ni en `EntityActions` (primera condición del `OR`), y alguna acción sobre ese registro no está en las listas. | Media | Diferido a la spec de pendientes o a la auditoría 6 (ya marcada: al cerrar la 033, antes de la 034) | `npm run e2e -- e2e/h5-h6-seguridad.spec.ts e2e/jerarquia.spec.ts` (falla) | Abierto · **auditoría** | 2026-10-25 |

## 033

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-033-1 | El entorno aislado exige permiso `CREATE DATABASE` en el servidor y crea una base por sesión. Medido: 0.3 s con 3 catálogos y 0.5 s con los cuatro módulos (22 catálogos) en LocalDB; no se midió con cientos de catálogos ni en un SQL Server administrado (Azure SQL no permite `CREATE DATABASE` desde una base de usuario de la misma forma). Sin permiso, el alcance `aislado` falla con un mensaje y el agente entrega sin pruebas. | Baja | Aceptado (`Studio:Sandbox=false` lo apaga) | `StudioModulesTests › H3_CA1_la_copia_aislada_lleva_el_modelo_de_los_cuatro_modulos` | Abierto | 2026-10-27 |
| R-033-2 | Suplantar al proveedor (DNS, proxy): como en la 011, una respuesta falsa solo produce lecturas con los permisos de la persona y una propuesta que ella revisa. | Baja | Aceptado | sin prueba | Abierto | 2026-10-27 |
| R-033-3 | La copia aislada tiene la empresa predeterminada y ningún tipo de cambio ni empresa extra: `RATE`/`CONVERT` dan vacío y las reglas por empresa no se prueban como en el entorno. | Baja | Aceptado (el agente lo debe decir en la entrega) | sin prueba | Abierto | 2026-10-27 |
| R-033-4 | Los pasos guardados (`meta.StudioStep`) conservaban lo que devolvieron las herramientas (con el alcance `datos`, valores de registros que la persona veía) sin caducidad. Solo los ve la persona dueña (033 F-08). | Media | Corregido en 035: al cerrar o vencer la sesión, en la misma transacción, se borran las salidas de las herramientas del alcance `datos` (el texto del modelo que los cite queda en R-035-1) | `StudioTests › H5_CA1_al_cerrar_la_sesion_se_borran_las_salidas_con_registros`, `› H5_CA1_una_sesion_interrumpida_tambien_borra_lo_leido` | Cerrado en 035 | 2026-10-27 |
| R-033-5 | Las rutas despachadas en proceso no pasan por los limitadores de frecuencia de ASP.NET (global, `search`, `bulk`, `aggregate`); las acotan el tope de herramientas por sesión (80), una sesión activa por persona y las sesiones del día. Ninguna herramienta usa hoy una ruta costosa. | Baja | Aceptado (la 035 decidirá un límite por token MCP) | `StudioTests › H2_CA2_el_tope_de_pasos_detiene_la_sesion_antes_de_llamar` | Abierto | 2026-10-27 |
| R-033-6 | En la copia aislada el agente actúa como su administrador: no prueba la seguridad por rol (lo que vería otro rol) ni procesos con transiciones restringidas a roles con usuarios reales (CLARIFY 3). | Baja | Aceptado | sin prueba | Abierto | 2026-10-27 |
| R-033-7 | El costo se estima con precios configurados (`Studio:InputUsdPerMTok`, `OutputUsdPerMTok`): si Anthropic cambia sus precios, la estimación se desvía. La reserva (revisión adversarial F-02) ya es el peor caso: tokens medidos de la llamada anterior, un token por carácter nuevo, toda la entrada a la tarifa de escritura de caché y la salida completa. | Baja | Aceptado | `StudioCoreTests › El_costo_se_estima_antes_de_llamar` | Abierto | 2026-10-27 |
| R-033-8 | Una sesión en curso cuando el servidor se reinicia queda «interrumpida» y la persona la pide de nuevo (no se retoma: CLARIFY 4). | Baja | Aceptado | `StudioTests › H2_CA5_una_sesion_sin_trabajador_termina_interrumpida` | Abierto | 2026-10-27 |
| R-033-9 | Revisión adversarial F-07: `compilar_plan` usa la vista previa del modelador, que para un plan que quita estados cuenta los registros por estado de toda la tabla (todas las empresas, sin alcance por registro); ese conteo viaja al proveedor aunque la sesión no tenga el alcance `datos`. Es lo mismo que la persona modeladora ve en el Modelador. | Baja | Aceptado (un conteo, no valores; omitirlo en Studio sin «datos» si la 038 (MCP) lo expone a terceros) | sin prueba | Abierto | 2026-10-27 |
| R-033-10 | Revisión adversarial F-10: cada `GET /api/studio/sessions/{id}` de una propuesta vigente recompilaba el plan y, sin `after`, devuelve todos los pasos (hasta ~2 MB con 80 herramientas). | Baja | Corregido en 035: con `after` no se compila (ni plan, ni vista previa, ni huella) y la pantalla sondea solo con `after`; la lectura completa (una al abrir y otra al terminar) sigue devolviendo todos los pasos, acotados por los topes de la sesión | `StudioTests › H5_CA2_sondear_con_after_no_compila_ni_da_la_huella` | Cerrado en 035 | 2026-10-27 |
| R-033-11 | Revisión adversarial F-11: el informe («probado: sí/no») lo declara el propio modelo; no se verifica contra los pasos y una inyección podría falsearlo. Además, cancelar o el tiempo máximo no interrumpen la creación de la base aislada (`Bootstrap.RunAsync` no recibe el token): termina y se borra en el `finally`. | Baja | Atendido en 035: la pantalla lo titula «Lo que el agente informa», como afirmación suya, y avisa si dice haber probado sin correr herramientas en la copia o si todas fallaron. La segunda mitad (cancelar durante la copia) sigue diferida | `studio.test.ts › reportNotes avisa pruebas sin copia aislada (R-033-11)`, `studio.spec.ts › H3 CA1 CA2: …` | Abierto (segunda mitad) | 2026-10-27 |

## 034

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-034-1 | La otra mitad de R-009-1: la firma de paquetes es HMAC con una clave compartida; cualquier entorno de la cadena que tenga la clave firma un paquete que los demás aceptan (no hay firma asimétrica por entorno de origen). | Media | Por decidir (firma asimétrica o clave por par de entornos: capacidad nueva con ADR) | `EnvironmentTests › Firma_hmac_detecta_cualquier_cambio_y_no_depende_del_formato` (alcance: otra clave no verifica) | Abierto | 2026-10-27 |
| R-034-2 | Los límites del despachador (Studio, MCP) son los mismos topes por persona que los de ASP.NET pero con contadores propios: en el peor caso una persona usa el doble mezclando navegador y herramientas. | Baja | Aceptado (la 038 —el MCP— decidirá si el token comparte la ventana) | `Hardening034Tests › H3_CA4_las_rutas_despachadas_tienen_su_limite` | Abierto | 2026-10-27 |
| R-034-3 | Revisión adversarial F-04: la regla de A-01 mira las capacidades **actuales** del destino. Si un gestor pone la contraseña a un usuario sin capacidades y después un superusuario lo asciende, la contraseña que el gestor conoce sigue valiendo (el sello se renueva, la contraseña no). | Baja | Aceptado (exigir cambio de contraseña al otorgar capacidades administrativas: spec de pendientes) | sin prueba | Abierto | 2026-10-27 |
| R-034-4 | Revisión adversarial F-06: `StudioTests › A06…` comprueba que la segunda herramienta responde la falla recordada, pero no cuenta los intentos de crear la copia (`SandboxFactory` no tiene contador observable): una regresión que reintente y diga el mismo texto pasaría. | Baja | Aceptado | `StudioTests › A06_una_copia_aislada_que_falla_no_se_vuelve_a_crear` | Abierto | 2026-10-27 |

## 035

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-035-1 | La purga de R-033-4 borra las salidas de `leer_registros`/`leer_registro`, pero lo que escribe el propio modelo puede citar valores que leyó y se conserva sin caducidad en la sesión de su dueña: su texto (pasos «El agente escribe», resumen, informe) y las entradas de sus herramientas (`search`, `values` de `aislado_crear`, expresiones, planes; revisión adversarial F-02). El aviso del formulario lo dice. | Baja | Aceptado (lo ve solo la dueña, que los vio con su permiso; reescribir el texto del modelo no es fiable) | `StudioTests › H4_CA2_otra_persona_no_aplica_ni_ve_la_sesion` (alcance: solo la dueña) | Abierto | 2026-10-27 |
| R-035-2 | El sondeo de la pantalla (cada 1.5 s mientras la sesión está activa) no tiene límite propio además del global por persona; varias pestañas abiertas sobre la misma sesión multiplican las lecturas (ligeras: sin compilar). | Baja | Corregido en 037: la pantalla ya no sondea; sigue el turno con el flujo SSE de la 036 (tope `Studio:MaxStreamsPerUser`, R-036-1) y reconecta con espera creciente hasta «Sin conexión» | `studio.spec.ts › H3 CA4: si el flujo se corta…` · `› Regresión F-05: un flujo que se corta tras su saludo llega a «Sin conexión»` | Cerrado en 037 | 2026-10-29 |

## 036

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-036-1 | El tope de flujos SSE por persona (`Studio:MaxStreamsPerUser`) se cuenta por instancia: con varias instancias detrás de un balanceador, una persona puede abrir el tope en cada una. Cada flujo hace una lectura ligera por segundo (o al aviso) y vence a los `Studio:StreamSeconds`. | Baja | Aceptado (el limitador global por persona sigue contando cada conexión; un contador compartido llega si hay despliegue con varias instancias) | `StudioTests › Chat_H5_CA3_el_flujo_es_solo_de_la_duena` (tope en una instancia) | Abierto | 2026-10-29 |
| R-036-2 | El texto de la persona en un mensaje va dentro de `<pedido>` sin escapar: si escribe `</pedido>` puede confundir el delimitador. Es su propio texto (no datos de terceros); los datos de terceros siguen dentro de `<datos_no_confiables>` con `<` escapado. | Baja | Aceptado (la persona solo puede «inyectarse» a sí misma; el agente sigue sin herramienta que escriba en el entorno real) | `StudioChatTests › Purge_reemplaza_solo_los_resultados_de_datos` (los datos de terceros siguen delimitados) | Abierto | 2026-10-29 |
| R-036-3 | La copia aislada se conserva entre turnos con el modelo del momento en que se creó: si otra persona cambia el modelo real entre dos turnos, la copia no lo refleja hasta que se crea otra (sin uso 60 min, o al empezar otra conversación). La entrega siempre se compila contra el modelo real y aplicar exige la huella. | Baja | Aceptado (la compilación y la huella de aplicar protegen el modelo real; el informe de pruebas es una afirmación del agente, R-033-11) | `StudioTests › Chat_H3_la_propuesta_se_refina_en_la_conversacion_y_se_aplica_con_su_huella` | Abierto | 2026-10-29 |
| R-036-4 | Revisión adversarial F-04: con streaming, si el flujo del proveedor se corta a mitad (red, o el estado en vivo falla), la llamada no se cobra (se trata como error del proveedor) aunque Anthropic ya facturó los tokens emitidos. Los topes de costo quedan algo por debajo de lo real en ese caso. | Baja | Aceptado (no lo controla un atacante; la pérdida del lease y las fallas de la base ya no se disfrazan de error del proveedor: corregido sin prueba automática, porque el proveedor simulado no corta un flujo a mitad) | sin prueba | Abierto | 2026-10-29 |
| R-036-5 | Revisión adversarial F-05 (amplía R-035-1): lo que el modelo escribió citando registros y las entradas de herramientas que los citan quedan en el historial y vuelven al proveedor en los turnos siguientes de la conversación; la purga por turno solo borra las salidas de las herramientas de `datos`. | Baja | Aceptado (el texto es del modelo y la persona eligió el alcance `datos` para esa conversación; viajó ya en el turno que leyó) | sin prueba | Abierto | 2026-10-29 |

## 037

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-037-1 | Editar el último mensaje y reintentar mandan un mensaje nuevo: el motor no reescribe el historial (sin ramas), así que el mensaje anterior y su respuesta quedan, cuentan para los topes de la conversación y vuelven al modelo en los turnos siguientes. El editor lo avisa. | Baja | Aceptado (ramas fuera de alcance, roadmap; los topes acumulados protegen el costo) | `studio.spec.ts › H4 CA1 CA2 CA3: detener conserva lo escrito; reintentar y editar mandan un mensaje nuevo` | Abierto | 2026-10-29 |
| R-037-2 | Revisión adversarial F-08: durante un turno, el flujo trae la salida de las herramientas de `datos`, que el servidor borra al terminar. Si la pantalla queda «Sin conexión» la quita de la vista; si la sesión de la persona vence (401) a mitad del turno, sigue en memoria hasta la redirección al inicio de sesión. | Baja | Aceptado (la vio la dueña con su permiso, R-035-1; la redirección descarta la pantalla) | sin prueba (corregido para «Sin conexión» sin prueba automática: el proveedor simulado no corta el flujo tras una herramienta de `datos`) | Abierto | 2026-10-29 |
| R-037-3 | La entrega vigente se reconoce en la pantalla por su entrada (`plan` en el JSON); una entrada de más de 8 000 caracteres llega cortada como texto y se reconoce por la clave `"plan": {` sin escapar. Una clave `plan` anidada en otra parte del informe del agente podría marcar como vigente la entrega equivocada (solo la etiqueta: la tarjeta muestra siempre el plan y la huella de la lectura completa, y aplicar exige esa huella). | Baja | Aceptado (un dato del servidor con el paso de la propuesta vigente llegaría con un cambio de contrato) | `studio.test.ts › deliveryOf tolera una entrada cortada…` · `› currentProposalSeq…` | Abierto | 2026-10-29 |
