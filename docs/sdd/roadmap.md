# Roadmap — de motor de catálogos a plataforma ERP

Origen: auditoría externa (2026-10-23) que comparó el proyecto con Salesforce, Dataverse y Odoo.
**Las observaciones son correctas.** Este documento las convierte en specs ordenadas por
dependencia y valor. Cada una sigue el flujo SDD completo (spec → plan → tareas → verify).

## Cómo se ejecuta
**Una spec = una sesión nueva** (ver [README](README.md#una-spec-por-sesión)). Las specs se hacen
en orden, no en paralelo: casi todas tocan el núcleo del motor (`PlanCompiler`, `RecordService`).

| Spec | Estado |
|---|---|
| 005 Automatizaciones + inventario | Implementado ([spec](specs/005-automatizaciones-inventario/spec.md)) |
| 006 Seguridad por campo/registro | Implementado ([spec](specs/006-seguridad-campos-registros/spec.md)) |
| 007 Vistas, layouts, reportes | Implementado ([spec](specs/007-metadatos-interfaz/spec.md)) |
| 008 Integraciones | Implementado ([spec](specs/008-integraciones/spec.md)) |
| 009 Entornos | Implementado ([spec](specs/009-entornos/spec.md)) |
| 010 Multi-empresa / multi-moneda | Implementado ([spec](specs/010-multi-empresa-moneda/spec.md)) |
| 011 Asistente IA (Claude) | Implementado ([spec](specs/011-asistente-ia/spec.md)) |
| 012 Endurecimiento | Implementado ([spec](specs/012-endurecimiento/spec.md)) |
| 013 Formulario en solo lectura | Implementado ([spec](specs/013-formulario-solo-lectura/spec.md)) |
| 014 Pendientes menores y riesgos residuales | Implementado ([spec](specs/014-pendientes-menores/spec.md)) |
| 015 Jerarquía de roles, equipos y unidades | Implementado ([spec](specs/015-jerarquia-roles-equipos/spec.md)) |
| 016 Automatizaciones asíncronas y programadas | Implementado ([spec](specs/016-automatizaciones-asincronas/spec.md)) |
| 017 Rollback de entornos | Implementado ([spec](specs/017-rollback-entornos/spec.md)) |
| 018 OAuth2 (client credentials) y API OData | Planeada |
| 019 Editores visuales (layouts y modelador) | Planeada |
| 020 Contabilidad y costeo | Planeada |

## Diagnóstico
| Brecha señalada | Situación actual | Spec |
|---|---|---|
| Automatización entre registros | Una transición solo modifica su propio registro; la recepción no actualiza la OC; no hay cantidades recibidas/pendientes ni inventario | **005** |
| Seguridad por campo y por registro | ~~Solo permisos por entidad~~ Resuelto en la 006: campos ocultos/solo lectura por rol y por estado, alcance por registro (propios, unidad, todos, reglas `where`) en el servidor | **006** |
| Metadatos de interfaz | ~~El formulario se deriva del orden de los campos~~ Resuelto en la 007: layouts con secciones, pestañas y visibilidad condicional; vistas compartidas y personales; reportes exportables; tableros; todo con la seguridad de la 006 en el servidor | **007** |
| Integraciones y webhooks | ~~No existen~~ Resuelto en la 008: webhooks firmados con bandeja transaccional y reintentos, claves de API por rol con rotación, importación masiva por el motor y exportación de listados, todo con la seguridad de la 006 | **008** |
| Entornos (sandbox → producción) | ~~No se exporta lo hecho en el modelador~~ Resuelto en la 009: paquetes exportados con el formato de los módulos, firmados, con diff contra el destino (plan mínimo por el Plan de Cambios), aplicación versionada y aprobación de otro administrador en producción | **009** |
| Multi-empresa y multi-moneda real | ~~Una sola empresa; el tipo de cambio se captura a mano~~ Resuelto en la 010: empresas con moneda base, catálogos compartidos o por empresa (filtro en el servidor junto con la 006, también para el superusuario), tipos de cambio por fecha y `RATE`/`CONVERT`/`BASECURRENCY()` en fórmulas | **010** |
| Extensión con código | Prohibida por la constitución (I.1), a propósito | Decisión abajo |

## Orden y razón
```
005 Automatizaciones + inventario ──┐   (el hueco más grande; hace que Compras funcione solo)
006 Seguridad por campo/registro ───┼─▶ 010 Multi-empresa (la empresa es un caso de seguridad por registro)
007 Vistas, layouts, reportes ──────┤
008 Webhooks e integraciones ───────┼─▶ 011 Asistente IA (genera todo lo anterior como metadatos)
009 Exportar/importar entornos ─────┘
```

### 005 — Automatizaciones y cantidades (prioridad 1)
- **Eventos:** al crear, al editar, al entrar a un estado, al ejecutar una transición.
- **Acciones declarativas:** asignar campos de un registro relacionado, ejecutar una transición en un
  registro relacionado, crear registros, acumular cantidades con `SUM` sobre registros relacionados
  (no solo partidas propias).
- Todo dentro de la **misma transacción** que el evento, con límite de profundidad para evitar ciclos.
- Compras 1.2: la partida de la OC gana *Cantidad recibida* y *Pendiente*. Confirmar una recepción
  acumula lo recibido y mueve la OC sola a *Recibida parcial* o *Recibida*.
- Módulo **Inventario** 1.0: existencias por almacén y producto, y movimientos (entrada por
  recepción, salida, ajuste, traspaso) generados por automatización.

### 006 — Seguridad por campo y por registro (prioridad 1, deuda de la constitución)
- **Por campo:** por rol, oculto, solo lectura o editable. Por estado: un campo editable solo en
  ciertos estados. Resuelve el hallazgo F-25: «Motivo de rechazo» se captura solo al rechazar.
- **Por registro:** dueño (*creado por*), reglas declarativas de acceso (`Solicitante = CURRENTUSER()`,
  `Departamento = USER.Departamento`) y alcance por rol (propios, de mi unidad, todos).
- Se aplica en el servidor a listados, lookups, reportes y API. Nunca solo en la pantalla.

### 007 — Metadatos de interfaz
- **Layouts:** secciones y pestañas del formulario, campos por sección y visibilidad condicional.
- **Vistas guardadas:** columnas, filtros, orden; personales o compartidas por rol.
- **Reportes:** agrupaciones y totales sobre una entidad y sus relaciones, exportables a Excel/CSV.
- **Dashboards:** tarjetas de indicadores y gráficas definidas como metadatos (respetando 006).
- Reportes y vistas deben filtrar con `RecordFilter.For` y quitar campos con `FieldAccess.Strip` (los mismos de la 006).
- ~~Pendiente del modelador (hallazgo F-03 de la 006)~~ resuelto en la 007.
- ~~Pendiente para la 008: exportar e importar listados~~ resuelto en la 008.

### 008 — Integraciones
- **Webhooks salientes** por evento, firmados con HMAC, con reintentos y bitácora de entregas.
- **Claves de API** para servicios (alcance por rol, rotación, nunca en el repositorio).
- **Importación masiva** (CSV/Excel) por el mismo motor: valores por defecto, reglas y auditoría.
- Implementado. Para la 009: el destino y el secreto de los webhooks son configuración del entorno (no viajan en el paquete); los
  roles de servicio sí, y una actualización de módulo ya puede agregar roles (`upgrades[].roles`).

### 009 — Ciclo de vida de metadatos
- Exportar lo hecho en el modelador como **paquete** (mismo formato que los módulos), con *diff*
  contra el entorno destino y aplicación como actualización versionada.
- Flujo sandbox → pruebas → producción con aprobación.
- Implementado. Para la 010: la empresa será configuración del entorno o dato, no metadato del paquete; si se agrega
  metadato nuevo (p. ej. catálogos «por empresa»), `ModelDiff` debe compararlo (la prueba de ida y vuelta lo detecta).
  Pendiente: archivar catálogos completos por paquete y un indicador del entorno en la barra superior.

### 010 — Multi-empresa y multi-moneda
- Empresa como dimensión de seguridad por registro (requiere 006), con catálogos compartidos o por empresa.
- Moneda base por empresa, tipos de cambio por fecha y conversión automática en fórmulas.
- Implementado. `perCompany` es metadato (viaja en paquetes y el diff lo compara); empresas, empresas de cada usuario y
  tipos de cambio son datos del entorno. Para la 011: la IA puede proponer `perCompany` y fórmulas con `RATE`/`CONVERT`
  como cualquier metadato; no debe proponer datos del entorno (empresas, tipos de cambio). Pendiente: series de folios
  por empresa, consolidación entre empresas, rol de finanzas para capturar tipos de cambio y carga automática.

### 011 — Asistente IA
Va al final a propósito: la IA solo propone planes de cambios, y cuanto más completo sea el lenguaje
de metadatos, más puede construir.
- Implementado con **Claude (Anthropic)** en lugar de MiniMax, a pedido del dueño ([ADR-0003](../adr/0003-asistente-ia-anthropic.md)).
  El asistente recibe solo metadatos, devuelve un Plan de Cambios que pasa por el mismo compilador y vista previa, se
  corrige con los errores del compilador y lo aplica la persona que lo pidió. Pendiente: respuestas en flujo y editar el
  plan propuesto en el modelador antes de aplicarlo.

### 012 — Endurecimiento
Origen: auditoría de solo lectura (2026-10-23) que verificó en el código seis lugares donde una regla ya declarada no se
aplicaba en todos los caminos. Sin capacidades nuevas: la bitácora respeta la seguridad del dato (006/010); una transición
sin roles exige el alcance de escritura; el motor bloquea los documentos que tocarán las automatizaciones antes de validar
(sobre-recepción concurrente) y Compras 1.7.0 agrega la regla en la partida; con aprobación (009) el modelo solo cambia por
paquetes; el límite del asistente se reserva antes de llamar al proveedor y aplicar exige la huella de lo revisado; la
etiqueta de una referencia fuera de alcance es «(sin acceso)».
- Diferidos (con razón en la spec): acumulado en SQL en lugar de memoria, folios fuera de la transacción de importación,
  y que un paquete no archive campos locales sin decisión explícita. Resueltos en la 014.

### 013 — Formulario en solo lectura fuera del alcance de escritura
Origen: hallazgo F-05 de la verificación 012. El registro trae `_access` (`update`, `delete`, `reason`) con el permiso del
rol, el alcance de escritura por registro (006/010) y el catálogo del sistema; el formulario se muestra en solo lectura,
sin «Guardar cambios» ni «Eliminar» y con el motivo, cuando la persona solo puede consultar ese registro (también
si una transición lo saca de su alcance). El servidor
sigue rechazando con 403. Pendiente: el listado no lo calcula por fila (no ofrece edición en línea).

### 014 — Pendientes menores y riesgos residuales
Origen: auditoría de solo lectura posterior a la 013. Compras 1.8.0 sugiere el precio en la moneda de la orden y el costo
estimado en la moneda base (sin tipo de cambio no sugiere nada); el acumulado suma en SQL cuando el filtro es traducible
(sin tope); la vista previa de una importación no toca los folios y la importación reserva su bloque sin detener altas;
archivar campos locales con un paquete se confirma aparte al aprobar y aplicar; un webhook con 4xx definitivo no se
reintenta y Teredo es interno; el motor toma el bloqueo por clave de los acumulados por `match` antes de insertar
(interbloqueos en salidas simultáneas); aviso de `AllowDirectChanges` con aprobación. Decisiones: `CostoMXN` no se
renombra; la bitácora se filtra con la política actual; roles y permisos son dato del entorno
([ADR-0004](../adr/0004-seguridad-como-dato-del-entorno.md)).

## Specs planeadas (brechas de plataforma)
Señaladas por la misma auditoría. Cada una es capacidad nueva del motor y va en su propia sesión, en este orden.

### 015 — Jerarquía de roles, equipos y unidades de negocio
- **Problema:** el alcance «de mi unidad» (006) es una etiqueta plana en el usuario: no hay árbol de unidades (un gerente
  regional no ve lo de sus sucursales), ni equipos que compartan registros, ni roles que hereden de otros. Cada permiso se
  repite rol por rol, y los cambios de seguridad no tienen cuatro ojos (ADR-0004).
- **Alcance:** unidades en árbol con alcance «mi unidad y sus descendientes»; equipos (dueño de un registro = persona o
  equipo); herencia de roles; aprobación opcional de cambios de seguridad (roles, asignaciones, claves de API) en
  entornos con aprobación. Todo en `RecordFilter` (SQL), también en reportes, bitácora y etiquetas.
- **Depende de:** 006, 010, 012. Va primero: las siguientes (automatizaciones con autoridad, OAuth) necesitan un modelo
  de principales más rico.
- Implementado. Unidades en `meta.BusinessUnit` con ruta materializada y alcance `unitTree`; equipos como principales
  sin sesión (`IsTeam`) con `teamOwners` en la política; herencia de roles por cierre transitivo (sin capacidades
  administrativas); con `Environments:RequireSecurityApproval`, lo que amplía acceso espera a otro administrador con
  huella ([ADR-0005](../adr/0005-aprobacion-de-cambios-de-seguridad.md)). Para la 016: una tarea programada puede
  correr con un rol de servicio (`ServiceRole.LoadAsync` ya incluye la herencia) y dejar lo que crea a nombre de un
  equipo; un equipo no tiene roles, así que no puede ser la identidad de la tarea. Pendiente: roles de
  equipo, borrar unidades, comparar un campo de texto contra el árbol en reglas `where`.

### 016 — Automatizaciones asíncronas y programadas
- **Problema:** las automatizaciones (005) corren en la transacción del evento: no hay acciones diferidas (avisar,
  recalcular en lote), ni programadas (vencimientos, recordatorios, cierres diarios), ni reintentos.
- **Alcance:** bandeja transaccional como la de webhooks (008) para acciones `async`; disparadores por calendario
  (`schedule`) con consulta declarativa de registros; ejecución con un principal de servicio y su alcance; bitácora de
  ejecuciones, reintentos y límite de lotes.
- **Depende de:** 005, 008, 015 (con qué identidad corre una tarea programada).
- Implementado. `mode: "async"` en una automatización deja su trabajo en `meta.AutomationRun` dentro de la transacción del
  evento; un servicio en segundo plano lo ejecuta a nombre de quien lo disparó (recargado: dado de baja → se cancela), con
  reintentos solo de lo transitorio y un máximo de 5 generaciones. Tareas `setSchedules` (cada N minutos, hora, día,
  semana o mes) con consulta `where` traducida a SQL (`ADDDAYS(TODAY(), n)` nuevo), lote ≤ 1000, una transacción por
  registro y el alcance de lectura de su rol de servicio; lo que cambian queda a nombre de la cuenta de sistema «Tareas
  programadas» y la auditoría nombra la tarea. Llegan en pausa: el superusuario las activa por entorno en
  *Configuración › Automatizaciones*, donde está también la bitácora. `update`/`transition` sin `target` actúan sobre el propio registro.
  Para la 020: los cierres pueden ser tareas mensuales. Pendiente: tareas sin consulta (crear un registro «cierre del
  día»), asignar a un equipo por nombre, zonas horarias por tarea y continuar el siguiente lote sin esperar la ocurrencia.

### 017 — Rollback de entornos
- **Problema:** un paquete aplicado (009) no se puede revertir: volver atrás exige otro paquete hecho a mano, y los
  campos archivados no se reactivan por paquete.
- **Alcance:** instantánea del modelo antes de aplicar; «revertir a la versión anterior» como un diff inverso que pasa por
  el mismo compilador, vista previa, aprobación y auditoría; reactivar campos y catálogos archivados como operación del
  plan (nunca borrar datos).
- **Depende de:** 009, 014 (campos locales confirmados), 012 (cuatro ojos sin atajos).
- **Estado:** implementado en la [spec 017](specs/017-rollback-entornos/spec.md): instantáneas antes y después de aplicar,
  solicitud de reversión con diff inverso que pasa por la misma compuerta, y `restoreEntity`/`restoreField` en el plan.

### 018 — OAuth2 (client credentials) y API OData con descubrimiento
- **Problema:** las integraciones usan claves de API estáticas (008) y endpoints propios: sin expiración corta, sin
  alcances por cliente, y cada integrador aprende la API a mano.
- **Alcance:** servidor de tokens con *client credentials* (tokens cortos, alcances, rotación del secreto del cliente);
  documento de descubrimiento (`$metadata`/OpenAPI) generado del modelo; consultas OData de solo lectura (`$filter`,
  `$select`, `$orderby`, `$top`, `$expand` de un nivel) traducidas con la misma seguridad por registro y campo.
- **Depende de:** 008, 015.

### 019 — Editores visuales (layouts y modelador)
- **Problema:** layouts, vistas y procesos (007) se editan como listas y formularios; es correcto pero lento, y el
  asistente (011) es la única forma «visual» de construir.
- **Alcance:** editor de layout con arrastrar y soltar (secciones, pestañas, columnas) y vista previa en vivo; diagrama
  del proceso (estados y transiciones) editable; todo produce el mismo Plan de Cambios, con teclado y accesibilidad AA.
- **Depende de:** 007, 011. No agrega capacidad: mejora cómo se usa la que ya existe. Antes era la 020; pasó antes de
  Contabilidad por decisión del dueño (2026-10-24).

### 020 — Contabilidad y costeo (promedio y FIFO)
- **Problema:** Inventario (005) lleva cantidades, no valor: no hay costo del inventario, pólizas ni costo de ventas;
  «costo último» (Compras) es solo una sugerencia de precio.
- **Alcance:** capacidades genéricas del motor (capas de costo por movimiento, costo promedio ponderado y FIFO, partida
  doble con cuadre obligatorio) y módulos `contabilidad` (catálogo de cuentas, pólizas, periodos con cierre) y
  `costeo` (valuación de existencias, póliza de entrada por recepción). Multi-moneda con `CONVERT` (010).
- **Depende de:** 005, 010, 016 (cierres y revaluaciones programadas). Va al final por decisión del dueño (2026-10-24):
  por ahora no se necesita; antes era la 019.

## Decisión: ¿extensión con código?
La auditoría tiene razón en el costo: cada necesidad nueva exige una capacidad del motor. Se mantiene
la prohibición por ahora, con tres vías de escape en orden de seguridad:

1. **Webhooks e integraciones (008):** el código vive fuera del ERP, en el sistema que lo necesite.
2. **Biblioteca de acciones del motor (005):** cada acción nueva pasa por spec, pruebas y revisión.
3. **Scripts en sandbox (futuro, requiere ADR):** intérprete aislado, sin red ni archivos, con
   límites de tiempo y memoria, y solo para administradores. Se evaluará cuando 005 y 008 no alcancen.

Así se conserva lo que distingue al proyecto (todo es metadato auditable y la IA puede generarlo)
sin cerrar la puerta a casos que no quepan en el modelo declarativo.
