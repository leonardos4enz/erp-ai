# Registro de riesgos residuales

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


**Resumen (2026-10-25, al cerrar la 029):** 138 riesgos, 116 abiertos. La 029 cerró R-006-1 y R-023-5 (auditoría 4 D-05 y D-01), dejó prueba a R-023-3 y R-024-3 y registró R-029-1…14. Siguen 28 medios o altos sin prueba aceptados hace más de 5 specs, marcados para la próxima auditoría: R-001-1, R-006-4, R-007-1, R-007-2, R-008-2, R-008-4, R-009-1, R-009-3, R-010-3, R-011-1, R-011-2, R-011-3, R-012-2, R-012-3, R-014-1, R-014-2, R-015-3, R-016-2, R-016-3, R-016-4, R-018-3, R-018-4, R-021-4, R-021-5, R-021-6, R-021-7, R-022-1, R-022-3.

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
| R-028-2 | La suite no se puede fragmentar (`--shard`): 13 archivos dependen de otros y las capturas dependen del menú que dejan los demás. | Media | Diferido a 030 | sin prueba (dependencias listadas en `verificacion.md`) | Abierto | 2026-10-25 |
| R-028-3 | Con `E2E_INDEPENDENT=1`, tras una falla Playwright vuelve a correr `beforeAll`; 20 de los 23 archivos seriales no se adaptaron para tolerar datos que ya existen y pueden fallar en cascada (solo al verificar contra el código anterior). | Baja | Aceptado | sin prueba | Abierto | 2026-10-25 |

## 029

| ID | Riesgo | Severidad | Decisión | Prueba | Estado | Revisado |
|---|---|---|---|---|---|---|
| R-029-1 | Lo relacionado (`Ref.Campo`, plantillas de PDF, `UNITCOST`) de un registro al alcance se lee aunque el rol no pueda abrir ese catálogo (`RelatedLoader.Visible` no exige `Access(e).Read`, como `__label`); la seguridad por campo sí se revisa en cada salto. | Media | Aceptado (decisión 3 de la 029; auditoría 4, especulativo) | sin prueba | Abierto | 2026-10-25 |
| R-029-2 | Un valor por defecto que lee un campo oculto escribe el dato en un campo capturable que el usuario ve. | Media | Aceptado (decisión 5: lo decide el modelador) | sin prueba | Abierto | 2026-10-25 |
| R-029-3 | Las reglas de error y las condiciones de transición se evalúan con la autoridad del motor: su mensaje fijo o «permitida / no permitida» puede revelar un bit de un dato oculto o fuera de alcance. | Media | Aceptado (decisión 6) | sin prueba | Abierto | 2026-10-25 |
| R-029-4 | Auditoría 4 D-06: una tarea programada se salta para siempre un registro que falla (`RecordService.Background.cs`). | Media | Diferido a 030 o spec propia | sin prueba | Abierto | 2026-10-25 |
| R-029-5 | Auditoría 4 D-07: la importación toma «1,5» como 15 y las fechas con desfase mal (`RecordService.Import.cs`). | Media | Diferido a spec propia | sin prueba | Abierto | 2026-10-25 |
| R-029-6 | En Inventario y Costeo los costos que copian las automatizaciones (`MovimientoInventario.CostoUnitario`, valor de `Existencia`, capas) salen del precio de la orden pero son campos guardados, no fórmulas: el almacenista los ve. | Media | Por decidir (seguridad por campo del módulo, decisión del dueño) | sin prueba | Abierto | 2026-10-25 |
| R-029-7 | El tope de 12 MP de imágenes por PDF (422) solo tiene revisión de código: probarlo pide subir tres imágenes de 6 MP en la suite. | Baja | Aceptado | sin prueba | Abierto | 2026-10-25 |
| R-029-8 | Instalar o actualizar un módulo (superusuario, paquetes del servidor) y un plan con `setSecurity` cambian roles, permisos y seguridad del catálogo sin la aprobación de seguridad de la 015; su compuerta es el cambio directo. | Media | Por decidir (ver R-021-7) | `RouteInventoryTests › Cambiar_el_modelo_o_la_seguridad_pasa_por_su_compuerta` (declara la compuerta, no la aprobación) | Abierto | 2026-10-25 |
| R-029-9 | Una fórmula guardada que recorre un catálogo con alcance restringido se oculta entera a ese usuario, aunque el registro de esa fila sí lo vea (más estricto que Salesforce); para mostrarlo hay que copiarlo con una automatización. | Baja | Aceptado (decisión 2) | `Hardening3Tests › Una_formula_que_recorre_un_catalogo_con_alcance_restringido…` | Abierto | 2026-10-25 |
| R-029-10 | `GET /api/users/lookup` lista usuarios y equipos (nombre) a cualquier sesión: los campos de tipo usuario lo necesitan. | Baja | Aceptado | `rutas.md` (capacidad «sesión») | Abierto | 2026-10-25 |
| R-029-11 | Un aviso cuya regla lee una fórmula que el usuario ya no ve (029) deja de mostrársele y de pedirle confirmar: con `PolizaConCuentas` en aviso, el almacenista confirma la recepción sin aviso (lee `CostoTotal`, que sale del precio). La regla de error (lo predeterminado) sigue bloqueando a todos. | Media | Aceptado (R-023-3: mostrarlo revelaría el dato) | `valuacion-rutas-avisos › H2 CA6` (confirma quien ve el costo) | Abierto | 2026-10-25 |
| R-029-12 | Revisión adversarial F-A: la huella del módulo se comparaba sin el candado del modelo; corregido (`PlanService.LockModelAsync` antes de comparar), pero la carrera entre un plan y la instalación no tiene prueba: necesita dos conexiones y una pausa entre comparar y aplicar. | Media | Diferido a 030 (integración con `Concurrently`) | sin prueba (revisión de código) | Abierto | 2026-10-25 |
| R-029-13 | Revisión adversarial F-D: si un camino de lectura usara un modelo sin anotar (copia con `with` de un campo fórmula), `DerivedAccess` no ocultaría nada; hoy todos leen el de `MetadataStore.LoadAsync`. | Baja | Aceptado | `Hardening3Tests › Sin_anotar_el_modelo_las_reglas_son_las_de_antes` (documenta el comportamiento) | Abierto | 2026-10-25 |
| R-029-14 | Revisión adversarial F-E: un render de PDF que se cuelga retiene su lugar del semáforo sin plazo; con 4 colgados no hay PDF hasta reiniciar, y no hay métrica de lugares ocupados. | Baja | Aceptado (mejor que rebasar el límite, D-02) | `Hardening3Tests › El_lugar_del_semaforo…` | Abierto | 2026-10-25 |
