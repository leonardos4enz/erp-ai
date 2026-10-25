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
| 018 OAuth2 (client credentials) y API OData | Implementado ([spec](specs/018-oauth-odata/spec.md)) |
| 019 Editores visuales (layouts y modelador) | Implementado ([spec](specs/019-editores-visuales/spec.md)) |
| 020 Contabilidad y costeo | Implementado ([spec](specs/020-contabilidad-costeo/spec.md)) |
| 021 Endurecimiento 2 | Implementado ([spec](specs/021-endurecimiento-2/spec.md)) |
| 022 Costeo 2: políticas de valuación | Implementado ([spec](specs/022-costeo-2/spec.md)) |
| 023 Capacidades del motor (merge, avisos, rutas) | Implementado ([spec](specs/023-capacidades-motor/spec.md)) |
| 024 Navegación por módulos | Implementado ([spec](specs/024-navegacion-modulos/spec.md)) |
| 025 Explorador del modelo y de la base de datos | Implementado ([spec](specs/025-explorador-modelo/spec.md)) |
| 026 Modelador por módulos y sin JSON | Implementado ([spec](specs/026-modelador-visual/spec.md)) |
| 027 Documentos PDF con plantillas visuales | Implementado ([spec](specs/027-documentos-pdf/spec.md)) |
| 028 Mejoras del proceso (retrospectiva 001) | Implementado ([spec](specs/028-mejoras-proceso/spec.md)) |
| 029 Endurecimiento 3: capacidades declaradas y canario de fugas | Implementado ([spec](specs/029-endurecimiento-3/spec.md)) |
| 030 Integración y CI | Implementado ([spec](specs/030-integracion-ci/spec.md)) |
| 031 Módulo de Ventas | Planeado |
| 032 CFDI 4.0 con PAC | Planeado |

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
- **Estado:** implementado en la [spec 018](specs/018-oauth-odata/spec.md): clientes OAuth (cuenta de servicio con roles,
  alcances `odata.read`/`data.read`/`data.write`, rotación con gracia y revocación, con la aprobación de la 015),
  `POST /oauth/token` con tokens opacos de 5 a 60 minutos, `$metadata` CSDL generado para quien lo pide y `/odata` de solo
  lectura con `$filter`, `$select`, `$orderby`, `$top`, `$skip`, `$count` y `$expand` de un nivel, con el mismo alcance por
  registro, campo y empresa que la API de datos. Pendiente: escritura y `$batch` por OData, filtros por rutas
  (`Proveedor/Nombre`), opciones anidadas en `$expand`, introspección/revocación de tokens por el cliente (RFC 7662/7009)
  y nombres de usuario en las filas.

### 019 — Editores visuales (layouts y modelador)
- **Problema:** layouts, vistas y procesos (007) se editan como listas y formularios; es correcto pero lento, y el
  asistente (011) es la única forma «visual» de construir.
- **Alcance:** editor de layout con arrastrar y soltar (secciones, pestañas, columnas) y vista previa en vivo; diagrama
  del proceso (estados y transiciones) editable; todo produce el mismo Plan de Cambios, con teclado y accesibilidad AA.
- **Depende de:** 007, 011. No agrega capacidad: mejora cómo se usa la que ya existe. Antes era la 020; pasó antes de
  Contabilidad por decisión del dueño (2026-10-24).
- **Estado:** implementado en la [spec 019](specs/019-editores-visuales/spec.md): el editor del catálogo tiene pestañas
  *Catálogo*, *Formulario* y *Proceso*. El diseñador del formulario arrastra campos y secciones (y con teclado: tomar,
  mover con flechas, soltar, con región en vivo), maneja pestañas, columnas, ancho completo y condiciones, y muestra la
  vista previa con la misma función que el formulario real. El diagrama del proceso se ordena solo desde el estado
  inicial, se edita con un panel y se conecta arrastrando de un estado a otro. Los dos editan el mismo borrador que el
  JSON (que sigue disponible) y producen el mismo `setLayout`/`setWorkflow`, con la vista previa, la aprobación y la
  auditoría de siempre; sin cambios en el servidor. Pendiente: diseñador de vistas, reportes y tableros, deshacer y
  rehacer dentro del diseñador, y editar en el modelador un plan propuesto por el asistente.

### 020 — Contabilidad y costeo (promedio y FIFO)
- **Problema:** Inventario (005) lleva cantidades, no valor: no hay costo del inventario, pólizas ni costo de ventas;
  «costo último» (Compras) es solo una sugerencia de precio.
- **Alcance:** capacidades genéricas del motor (capas de costo por movimiento, costo promedio ponderado y FIFO, partida
  doble con cuadre obligatorio) y módulos `contabilidad` (catálogo de cuentas, pólizas, periodos con cierre) y
  `costeo` (valuación de existencias, póliza de entrada por recepción). Multi-moneda con `CONVERT` (010).
- **Depende de:** 005, 010, 016 (cierres y revaluaciones programadas). Va al final por decisión del dueño (2026-10-24):
  por ahora no se necesita; antes era la 019.
- **Estado:** implementado en la [spec 020](specs/020-contabilidad-costeo/spec.md). Tres capacidades del motor: acción
  `cost` (capas por llave en `meta.CostLayer`, promedio ponderado o FIFO por expresión, costo del movimiento y valor
  restante en el saldo, faltante al costo de respaldo) con `UNITCOST(catálogo, cantidad, llave…)`; libro de partida doble
  `setLedger` (cuadre en los estados contabilizados y periodo abierto por fecha, antes y después del cambio, con `HOLDLOCK`
  contra el cierre concurrente, y periodos sin traslapes), y `create` con `createLines` y `transition`. Módulos:
  Inventario 1.5.0 (costo de entrada con `CONVERT` a la fecha de la recepción, traspaso al costo de origen),
  Contabilidad 1.0.0 (cuentas, periodos con cierre y tarea de cierre en pausa, pólizas, saldos, balanza) y Costeo 1.0.0
  (método por producto, valor de existencias, póliza de entrada por recepción). Pendiente: póliza de salidas (costo de
  ventas), revaluación de existencias, pólizas en moneda extranjera, reversa de pólizas con otra póliza y rutas de tres
  niveles en expresiones (una partida de póliza por producto).

### 021 — Endurecimiento 2
Origen: auditoría de solo lectura (2026-10-24) de las specs 014–020 contra el código. Sin capacidades nuevas: reglas que el
motor ya prometía y no se cumplían en todos los caminos.
- **Estado:** implementado en la [spec 021](specs/021-endurecimiento-2/spec.md). Nadie otorga ni quita lo que no tiene
  (superusuario solo por superusuario; modelo y auditoría solo por quien los tiene; también al aprobar, y el beneficiario no
  aprueba); los catálogos llevan `version` y la vista previa `baseHash`, que aplicar exige (el diseñador ya no pisa el
  cambio de otra persona; el asistente incluye la huella en la suya); la bandeja marca el éxito en la transacción del
  trabajo, con dueño del *lease*, renovación y tope de intentos; una tarea activa queda atada a lo que se activó (un plan
  que la cambia, la quita o archiva su catálogo la pausa); una reversión lista y pide confirmar lo que cambia de seguridad;
  Contabilidad 1.1.0 no deja cancelar a mano una póliza automática aplicada; OData con tope de `$skip` y de partidas
  expandidas; tokens OAuth auditados; solicitudes de seguridad atoradas se liberan; no se quita un estado con registros;
  el editor avisa de cambios sin aplicar.
- **Pendiente:** ~~costeo 2~~ (resuelto en la 022); límite de `/oauth/token` detrás de un proxy (configurar `ForwardedHeaders` con los proxies conocidos de cada despliegue, nunca a
  ciegas); aviso de cambios sin aplicar también al navegar dentro de la SPA (requiere *data router*); confirmar seguridad en
  paquetes normales.

### 022 — Costeo 2: políticas de valuación
Origen: los cinco casos de costeo que difirió la 021, más dos de la misma familia, y la decisión del dueño (2026-10-24): **son
reglas de negocio** (constitución I.1), configurables por metadatos con valores por defecto razonables, no decisiones fijas.
- **Estado:** implementado en la [spec 022](specs/022-costeo-2/spec.md). Invariante: un traspaso no crea ni destruye valor
  (la salida va primero y guarda su costo real con `capture`; la entrada entra a ese total exacto con `totalCost`).
  Capacidades del motor: en `cost`, `uncostedEntry` (`average` | `fallback` | `require`), `methodChange` (`merge` | `keep`),
  `totalCost` y `quantityField`; en `create`, `capture`; `setRules` con `merge`. Omitidas, el motor se comporta como en la
  020. Inventario 1.6.0 (respaldo de la orden solo con base MXN, regla `TipoCambioDeLaRecepcion`, ninguna partida
  confirmada sin costo, el movimiento con la fecha de la recepción) y Costeo 1.1.0 (entrada sin costo al costo último,
  fundir capas al pasar a Promedio, cantidad costeada y «Costeo incompleto», regla `PolizaConCuentas` o, sin ella, *Póliza
  pendiente*). La tabla de la spec dice qué metadato cambia cada política.
- **Corrección posterior:** Inventario 1.6.1 (sin tipo de cambio ni respaldo el costo queda vacío, no en cero: en expresiones
  `ROUND` de un vacío da 0) y foco y anuncio del error en el diálogo de confirmación de una acción (ver `verify.md`, F-03/F-04).
- **Pendiente:** decidir si el motor debe propagar el vacío en la aritmética y en `ROUND` (hoy vale 0: cada fórmula se
  protege con `IF(ISBLANK(...))`); ~~reglas de advertencia (avisar sin bloquear)~~ (resuelto en la 023); generar la póliza de una recepción pendiente cuando el almacén
  ya tenga cuentas; dar al rol *Contador* lectura de recepciones (dato del entorno, ADR-0004); póliza de salidas (costo de
  ventas) y revaluación, que siguen de la 020.

### 023 — Capacidades del motor: listas que se fusionan, reglas de aviso y rutas de tres niveles
Origen: pendientes de la 022 que limitan a los módulos y que Ventas (024, luego 025, 026, 027, 028, ahora 031) necesitará; plan del dueño (2026-10-24).
- **Estado:** implementado en la [spec 023](specs/023-capacidades-motor/spec.md). `merge` en las seis listas del catálogo
  (reglas, automatizaciones, vistas, reportes, webhooks, tareas): agrega o reemplaza por nombre en su lugar; la vista previa
  ya no dice que quita lo que un `merge` conserva, y un paquete de Entornos que extiende un catálogo que no crea llega al
  destino y se revierte (antes se descartaba en silencio). Reglas con `severity: warning`: no impiden guardar, se ven en el
  formulario en vivo, en la API (`_warnings`, `/compute`), en la importación y en la auditoría; cada transición declara
  `onWarning` (`confirm`: la persona acepta los avisos concretos, 428 en la API; `block`; `allow`). Política 7 de la 022:
  «avisar» = `PolizaConCuentas` en `warning`. Rutas de hasta tres saltos (`Recepcion.OrdenCompra.Moneda.Clave`) en todas
  las expresiones y en los filtros traducidos a SQL, con la seguridad por campo revisada en cada salto. Inventario 1.7.0
  usa la moneda de la orden en el costo de la recepción. Costeo 1.1.0 instala su vista con `merge`.
- **Pendiente:** quitar un elemento con `merge` (hoy, reemplazo completo); rutas en reportes agrupados y en reglas de
  acceso; avisos en listados.

### 024 — Navegación por módulos
Origen: pedido del dueño (2026-10-24) antes de Ventas: el menú lateral ya no cabía y mezclaba operación con administración.
- **Estado:** implementado en la [spec 024](specs/024-navegacion-modulos/spec.md). Módulo de navegación como metadato
  (`setApp`/`archiveApp`, `meta.App`; el `group` de cada catálogo es el nombre de su módulo; un grupo sin declarar es un
  módulo implícito y los catálogos sin grupo forman *General*), que viaja en paquetes de Entornos (export, diff, instantánea
  y reversión) y que el asistente conoce. Compras 1.9.0, Inventario 1.8.0 y Contabilidad 1.2.0 declaran su módulo. En la
  SPA: selector de módulos en el inicio (`/`) y en la barra superior, página de cada módulo (`/m/:módulo`) con su
  análisis, menú lateral solo del módulo actual (el último se recuerda por usuario), área de configuración aparte con
  engrane y menú agrupado (`/admin`), `?app=` en reportes y tableros, y búsqueda global Ctrl+K (módulos, catálogos,
  análisis, configuración y documentos por folio con `GET /api/search`, con la seguridad del servidor). El modelador gana
  la pestaña *Módulos* y el campo *Módulo* con sugerencias.
- **Pendiente:** favoritos y recientes en el menú, búsqueda de texto completo en registros y preferencias del usuario en
  el servidor (el último módulo vive en el navegador).

### 025 — Explorador del modelo y de la base de datos
Origen: pedido del dueño (2026-10-24): ver la estructura completa del sistema, en especial un diagrama ER físico literal.
- **Estado:** implementado en la [spec 025](specs/025-explorador-modelo/spec.md). Área de solo lectura en *Configuración ›
  Modelo › Explorador* (`/admin/explorer`, capacidad *administrar modelo*). **Base de datos:** diagrama ER leído del catálogo de
  SQL Server (`sys.*`, esquema por lista blanca y parámetro; `meta` solo superusuario) con tipos SQL exactos, nulabilidad,
  valores por defecto, llaves, índices filtrados, columnas de sistema y pata de gallo por nulabilidad y unicidad; **deriva**
  (catálogos y campos archivados, columnas o tablas sin metadato, faltantes y tipos distintos de los que genera el motor);
  exportar SVG, PNG y DDL de estructura. **Relaciones** con color por módulo y foco a 1–2 niveles; **ficha** del catálogo
  (con el diagrama del proceso de la 019 en solo lectura, seguridad por rol con herencia y el paquete que lo instaló);
  **¿dónde se usa?** calculado en el servidor con el analizador de expresiones (rutas de tres saltos, `SUM`, `UNITCOST`), en
  caché por versión del modelo; **flujo de automatizaciones** síncronas, asíncronas y programadas con ciclos; **matriz de
  seguridad** y **árbol de navegación**. Cada diagrama tiene su alternativa en tabla o lista con teclado. Distribución de
  grafos propia (sin `elkjs`: tamaño y CSP), usable con 100+ tablas por filtro en el servidor, foco y zoom.
- **Pendiente:** comparar la base de dos entornos, sugerir limpieza de columnas archivadas (con respaldo y aprobación),
  exportar el diagrama lógico a DDL, y vistas, disparadores y procedimientos de SQL si algún día el motor los crea.

### 026 — Modelador por módulos y sin JSON
Origen: revisión del modelador por el dueño (2026-10-24): lista plana, sin origen de cada elemento, textos de ejemplo que
parecían contenido (bug) y ocho secciones en JSON manual. Ventas pasa a la 027 y CFDI 4.0 a la 028.
- **Estado:** implementado en la [spec 026](specs/026-modelador-visual/spec.md). Sin capacidades del motor: los editores
  escriben el mismo borrador que el JSON y el mismo Plan de Cambios. **Catálogos por módulo** (`group` de la 024, filtro y
  búsqueda en la URL) con quién instaló cada uno. **Origen** reproducido en memoria con el compilador puro a partir de las
  cadenas de los paquetes del servidor instalados (`GET /api/model/provenance`; no es metadato, así que no se falsea desde
  un plan): cada campo, regla, automatización, vista, reporte, webhook, tarea, proceso, formulario, seguridad y libro dice
  su paquete y versión, o «Local», y «Modificado localmente»; cambiar algo de un paquete avisa junto al elemento, y la vista
  previa de actualizar un módulo lista los choques (lo local o modificado que la actualización reemplaza o quita; gana la
  actualización). **Editor de expresiones** con autocompletado (campos, rutas de tres saltos, funciones, opciones) como
  *combobox* accesible y validación en vivo con `ExprAnalyzer` (`POST /api/model/expressions/check`, con los campos del
  borrador), y **constructor de condiciones** por renglones (campo · operador · valor, Y/O) que arma el mismo texto (pedido del
  dueño a mitad de la spec). **Editores visuales** de reglas, automatizaciones (con todas las acciones y políticas de costeo), seguridad,
  vistas, reportes, webhooks, tareas y libro, cada uno con su pestaña, estado vacío y «Ver como JSON» plegado; vista previa
  en vivo que muestra los errores del compilador junto a la sección y al elemento.
- **Pendiente:** origen de lo que llegó por paquetes de Entornos (hoy «Local»), fusión de tres vías al actualizar un
  módulo, rutas de más de un salto en el selector de agrupación de reportes y deshacer/rehacer en los editores.

### 027 — Documentos PDF con plantillas visuales
Origen: pedido del dueño (2026-10-25): diseñar desde el sistema el PDF de los documentos (orden de compra, póliza y, después,
la representación impresa del CFDI). Ventas pasa a la 028 y CFDI 4.0 a la 029.
- **Estado:** implementado en la [spec 027](specs/027-documentos-pdf/spec.md). **Archivos de imagen** del entorno
  (`meta.[File]`): PNG y JPEG validados por firma y estructura completa (polyglots, bombas de descompresión y dimensiones
  falsas se rechazan; SVG y WebP no), servidos autenticados con su tipo, `nosniff`, CSP `sandbox` y caché por huella;
  **logo, RFC y domicilio por empresa** (superusuario) e **imágenes de plantilla por clave** (*Configuración › Modelo ›
  Imágenes*). **Plantillas de impresión como metadato** (`setPrintTemplates` con `merge`): varias por catálogo, con roles,
  empresas, condición, predeterminada, página y bloques (encabezado, texto con `{{ campos }}`, campos con etiqueta, tabla de
  partidas, totales, imagen, separador, firmas, salto y pie con «Página N de M»); viajan en paquetes, origen, explorador y
  asistente. **Editor visual** en la pestaña *Impresión* del modelador (teclado y arrastre, constructor de condiciones,
  «Ver como JSON» plegado) con **vista previa** de un registro real. **Generación en el servidor** con PDFsharp + MigraDoc
  (MIT, [ADR-0007](../adr/0007-motor-pdf.md)) y fuente Lato incrustada: alcance por registro y por salto de ruta, campos
  ocultos «(sin acceso)» (columnas omitidas, bloques condicionados ocultos), auditoría y límites. «Descargar PDF» en el
  formulario. Compras 1.10.0 (OC para proveedor e interna) y Contabilidad 1.3.0 (póliza).
- **Pendiente (diferido con razón):** enviar el PDF por correo (no hay capacidad de correo: necesita su spec con
  servidor SMTP/proveedor, plantillas de mensaje y bitácora); adjuntarlo automáticamente al ejecutar una transición
  (requiere el tipo de campo archivo); firma electrónica (e.firma/FIEL: certificados y custodia de llaves, otra spec);
  **tipo de campo `image`/`file` en catálogos** (DDL, captura, importación, OData, miniaturas y límites por registro: la
  base `meta.[File]` ya existe); SVG sanitizado y WebP; códigos QR y de barras (los pedirá el CFDI, ahora 032); diseño libre por
  coordenadas.

### 028 — Mejoras del proceso (retrospectiva 001)
Origen: la [retrospectiva 001](retrospectivas/001-specs-001-026.md) de las specs 001–026 y el pedido del dueño
(2026-10-25) de aplicarla. Ventas pasa a la 031 y CFDI 4.0 a la 032.
- **Estado:** implementado en la [spec 028](specs/028-mejoras-proceso/spec.md). Reglas (P3, P4, P7, P12): STRIDE con 8
  filas obligatorias y prueba enlazada, revisión adversarial con un subagente independiente en cada VERIFY, auditoría
  externa cada 5 specs (ver [Auditorías](#auditorías)), una corrida completa + lo nuevo tres veces (`--veces=3`), criterios
  para dividir specs y alcance congelado en VERIFY. [Plantillas](plantillas/) de spec, plan y verify y
  `docs/sdd/metricas.mjs` (P13). [Registro de riesgos](riesgos.md) con los residuales de 001–027 (P10). Base y puertos de
  E2E por worktree, `E2E_INDEPENDENT` en todos los archivos seriales, `npm run e2e:regresion` y `npm run revision`
  (P6, P11).
- **Pendiente (diferido con razón):** fragmentar la suite (`--shard`): 13 archivos dependen de otros y las capturas del
  menú dependen de lo que dejan los demás → 030. Guardias de la suite (P9) → 030.

## Auditorías
Regla (028): auditoría externa de solo lectura cada **5 specs**, o antes tras una spec que agregue un punto de entrada, un
trabajo asíncrono o delegación de privilegios. Empieza por [riesgos.md](riesgos.md) (29 riesgos medios o altos sin prueba
ya marcados) y sus hallazgos se vuelven una spec de endurecimiento.

| Auditoría | Cubrió | Resultado |
|---|---|---|
| 2026-10-23 (origen de la 012) | 001–011 | 6 defectos |
| 2026-10-23 (origen de la 014) | 012–013 | 7 defectos |
| 2026-10-24 (origen de la 021) | 014–020 | 8 defectos |
| 2026-10-25, auditoría 4 (origen de la 029) | 021–028 | 9 defectos (1 alto: rutas de varios saltos fuera de alcance); ninguno de los 29 riesgos marcados estaba resuelto |
| **Próxima: adelantada, antes de abrir la 031** (al cerrar la 030) | 029–030 | — |

La auditoría 4 cubrió 021–028. La siguiente **conviene adelantarla** (al cerrar la 030 y antes de la 031, sin esperar 5 specs):
la 029 cambió la autorización de **todas** las rutas (capacidad como metadato, requisito de autorización en lugar de
comprobaciones en cada handler) y la visibilidad de las fórmulas en todos los caminos. La auditoría ya tiene herramientas:
[`rutas.md`](rutas.md) (qué pide cada ruta, generado y probado) y `web/e2e/canario.spec.ts` (fugas por cualquier ruta GET).

### 029 — Endurecimiento 3: capacidades declaradas y canario de fugas
Origen: retrospectiva 001 (P1 y P2) y la auditoría 4 (2026-10-25), que el dueño pidió incluir.
- **Estado:** implementado en la [spec 029](specs/029-endurecimiento-3/spec.md). **Capacidad como metadato de cada ruta**
  (`.Requires`, `.Writes` con su compuerta, `.Costly`, `.AppliesReviewed`), exigida como requisito de autorización antes de
  leer el cuerpo; **inventario** [`rutas.md`](rutas.md) generado y probado (`RouteInventoryTests`); **canario de fugas** que
  recorre todas las GET del inventario. Auditoría 4: D-01 (alcance en cada salto de lo vivo; fórmulas guardadas que cruzan
  un catálogo restringido se ocultan), D-02 (semáforo del PDF y 12 MP), D-03 (`WhyNotApprover` sin caso por omisión), D-04
  (huella de módulos), D-05 (fórmulas que leen ocultos), D-08 (avisos de bloqueo), D-09 (límite de búsqueda); imágenes solo
  a modeladores.
- **Pendiente (diferido con razón):** D-06 tareas programadas que se saltan registros (R-029-4) y D-07 importación de
  números y fechas (R-029-5) → spec propia o la 030; R-021-5 `ForwardedHeaders` y R-021-6 bloqueo de navegación en la SPA →
  030 o spec propia; costos de Inventario copiados por automatizaciones (R-029-6) → decisión del dueño; aprobación de
  seguridad para módulos y planes con `setSecurity` (R-029-8).

### 030 — Integración y CI
Origen: retrospectiva 001 (P5, P8, P9 y P14) y los riesgos R-028-2, R-028-3 y R-029-12.
- **Estado:** implementado en la [spec 030](specs/030-integracion-ci/spec.md). **Integración con SQL real**
  (`tests/Erp.IntegrationTests`: API real con `WebApplicationFactory`, base `_IT` por clase, `Concurrently`, invariantes,
  fallas inyectadas, segunda instancia sobre la misma base); las pruebas de API de `api-security`, `endurecimiento`,
  `endurecimiento-2` y `pendientes` pasaron ahí, y 012-3 y 021-3 fallan con `cd81760` y `f6c5445`
  (`npm --prefix web run it:regresion`). **CI** en GitHub Actions (rápido en Ubuntu en cada push; integración y E2E completa
  en `windows-2025` con LocalDB en `main`): es la segunda corrida completa. **Vitest** para la lógica del modelador.
  **Guardias de la suite** (desborde por absolutos, 4xx sin aviso en la vista, foco en `body`, pantallas sin captura), que
  encontraron y corrigieron: `.content` sin `position: relative` (causa raíz de los absolutos que escapan), botones que al
  esperar al servidor sacaban el foco, foco perdido cuando desaparece lo que lo tenía y el conflicto de edición sin `role="alert"`.
- **Pendiente (diferido con razón):** fragmentar la suite (R-028-2: corre en un solo trabajo de CI); caché del modelo por
  instancia (R-030-1, solo importa con varias instancias); 16 de 23 pantallas sin captura de referencia (R-030-3); autenticación SQL
  para un SQL Server en contenedor (no hace falta con LocalDB en Windows).

## Specs planeadas (después de la 029)
En este orden, por decisión del dueño (2026-10-25):
1. ~~**029 — Endurecimiento 3: capacidades declaradas y canario de fugas** (retrospectiva P1 y P2).~~ Implementado (ver arriba).
   - *Problema:* la constitución II.2 pide que cada endpoint declare su permiso, pero los grupos solo piden autenticación y
     la capacidad se revisa dentro de cada handler (`RequireSuperuser(http)`, `RequireModeler`…): nadie puede listar qué
     pide cada una de las ~117 rutas, y así se escapó que asignar roles solo pedía *administrar usuarios* (021-1, desde la
     001). Las fugas por otro camino al mismo dato (bitácora, `__label`, reportes, OData) las encontraron auditorías, no VERIFY.
   - *Alcance:* capacidad como **metadato de cada ruta** (`.RequireCapability(...)` como filtro, `.MutatesModel()`), sin
     comprobaciones imperativas; **inventario** que recorre `EndpointDataSource` y falla si una ruta no tiene autorización
     fuera de la lista blanca anónima, no declara capacidad o cambia el modelo sin la compuerta de aprobación; **snapshot**
     ruta → capacidad versionado (toda ruta nueva aparece en el diff); **canario**: un campo oculto y un registro fuera de
     alcance con `CANARIO-<guid>`, y todas las rutas GET del inventario (más una lista de POST que devuelven datos)
     recorridas como ese usuario sin que el canario aparezca.
   - *Verificación:* quitar la capacidad de una ruta hace fallar el inventario; el canario falla con el código de la 011
     (antes de la 012) en `/api/audit` y en `__label`; el snapshot está en el diff.
2. ~~**030 — Integración y CI** (retrospectiva P5, P8, P14 y P9).~~ Implementado (ver arriba).
   - *Problema:* las unitarias no usan base, así que toda prueba de concurrencia o falla parcial vive en Playwright, se
     escribe a mano y solo después de una auditoría; no hay CI (`.github/workflows` no existe) y la segunda corrida
     completa la hace cada sesión; la lógica de la SPA solo se prueba por E2E (026 F-06 fue intermitente); las lecciones
     repetidas (absoluto que escapa ×3, error fuera de la vista ×3, foco en `body` ×3) siguen siendo texto.
   - *Alcance:* `tests/Erp.IntegrationTests` con `WebApplicationFactory` y base propia por clase (`ErpAi_IT_<guid>`),
     `Concurrently(n, fn)`, aserciones de invariante e inyección de fallas entre dos pasos; mover ahí las pruebas sin UI de
     `api-security`, `endurecimiento` y `pendientes`. GitHub Actions: `dotnet test` y `build` en cada push; en `main`, la
     suite completa con SQL Server (contenedor con autenticación SQL, o runner Windows con LocalDB si lo trae) e
     integración; `npm audit` y `NuGetAudit`. Vitest (`npm --prefix web test`) para `layoutEdit.ts`, `workflowEdit.ts`, el
     constructor de condiciones, el autocompletado y la comparación canónica de la 026. **Guardias de la suite** (P9,
     movidas de la 028 porque cada una debe demostrarse contra el código anterior a su hallazgo): documento sin desbordes
     por absolutos (003 F-16, 025 F-01, 026 F-02), error 4xx dentro de la vista con `role="alert"` (018 F-01, 021 F-01,
     022 F-03; y R-018-6), foco que no cae en `body` (022 F-03, 025 F-07, 026 F-01), aviso de pantallas sin captura de
     referencia (011 F-05, 024 F-04). Fragmentación de la suite (R-028-2), si CI la necesita.
   - *Verificación:* las regresiones de 012-3 y 021-3 corren en integración y fallan con `cd81760` y `f6c5445`; CI en
     verde en `main` y un push con una prueba rota falla; `npm --prefix web test` cubre 026 F-03 y F-06; cada guardia falla
     con el CSS o componente anterior a su hallazgo. En CLARIFY, revisar si se divide (P12): son cuatro frentes.
   - **Antes de la 031 (número al abrirla, decisión del dueño si se renumera):** auditoría externa adelantada de la 029–030 y
     una spec de pendientes con D-06 (tareas programadas que se saltan registros, R-029-4), D-07 (importación «1,5» → 15 y
     fechas con desfase, R-029-5), R-021-5 (`ForwardedHeaders`) y R-021-6 (bloqueo de navegación en la SPA), más lo que
     encuentre la auditoría. Sus pruebas de concurrencia y fallas parciales van en integración.
3. **031 — Módulo de Ventas:** clientes, cotización, pedido, salida de almacén con costo de ventas (póliza de salidas),
   factura y cobranza. Usa `merge` para extender Producto e Inventario, avisos (límite de crédito) y rutas de tres niveles;
   declara su módulo con `setApp` (orden 15, entre Compras e Inventario).
4. **032 — CFDI 4.0 con PAC:** timbrado de la factura de Ventas con un proveedor autorizado, cancelación y complemento de
   pago; el secreto del PAC fuera del código (constitución II.7). Su representación impresa usa las plantillas de la 027
   (con un bloque de código QR nuevo).
5. **Pólizas en moneda extranjera** (pendiente de la 020).
6. **Revaluación de existencias** (pendiente de la 020).
7. **Reversa de pólizas con otra póliza** (pendiente de la 020).

## Decisión: ¿extensión con código?
La auditoría tiene razón en el costo: cada necesidad nueva exige una capacidad del motor. Se mantiene
la prohibición por ahora, con tres vías de escape en orden de seguridad:

1. **Webhooks e integraciones (008):** el código vive fuera del ERP, en el sistema que lo necesite.
2. **Biblioteca de acciones del motor (005):** cada acción nueva pasa por spec, pruebas y revisión.
3. **Scripts en sandbox (futuro, requiere ADR):** intérprete aislado, sin red ni archivos, con
   límites de tiempo y memoria, y solo para administradores. Se evaluará cuando 005 y 008 no alcancen.

Así se conserva lo que distingue al proyecto (todo es metadato auditable y la IA puede generarlo)
sin cerrar la puerta a casos que no quepan en el modelo declarativo.
