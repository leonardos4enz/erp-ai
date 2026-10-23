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
| 007 Vistas, layouts, reportes | **Siguiente** |
| 008 Integraciones | Pendiente |
| 009 Entornos | Pendiente |
| 010 Multi-empresa / multi-moneda | Pendiente |
| 011 Asistente IA | Pendiente |

## Diagnóstico
| Brecha señalada | Situación actual | Spec |
|---|---|---|
| Automatización entre registros | Una transición solo modifica su propio registro; la recepción no actualiza la OC; no hay cantidades recibidas/pendientes ni inventario | **005** |
| Seguridad por campo y por registro | ~~Solo permisos por entidad~~ Resuelto en la 006: campos ocultos/solo lectura por rol y por estado, alcance por registro (propios, unidad, todos, reglas `where`) en el servidor | **006** |
| Metadatos de interfaz | El formulario se deriva del orden de los campos; no hay vistas, layouts, reportes ni dashboards | **007** |
| Integraciones y webhooks | No existen | **008** |
| Entornos (sandbox → producción) | Los módulos se instalan y actualizan, pero no se exporta lo hecho en el modelador | **009** |
| Multi-empresa y multi-moneda real | Una sola empresa; el tipo de cambio se captura a mano | **010** |
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
- Pendiente del modelador (hallazgo F-03 de la 006): «Revisar cambios» aparece sin cambios en catálogos con `displayOrder` no consecutivo.

### 008 — Integraciones
- **Webhooks salientes** por evento, firmados con HMAC, con reintentos y bitácora de entregas.
- **Claves de API** para servicios (alcance por rol, rotación, nunca en el repositorio).
- **Importación masiva** (CSV/Excel) por el mismo motor: valores por defecto, reglas y auditoría.

### 009 — Ciclo de vida de metadatos
- Exportar lo hecho en el modelador como **paquete** (mismo formato que los módulos), con *diff*
  contra el entorno destino y aplicación como actualización versionada.
- Flujo sandbox → pruebas → producción con aprobación.

### 010 — Multi-empresa y multi-moneda
- Empresa como dimensión de seguridad por registro (requiere 006), con catálogos compartidos o por empresa.
- Moneda base por empresa, tipos de cambio por fecha y conversión automática en fórmulas.

### 011 — Asistente IA (MiniMax)
Va al final a propósito: la IA solo propone planes de cambios, y cuanto más completo sea el lenguaje
de metadatos, más puede construir.

## Decisión: ¿extensión con código?
La auditoría tiene razón en el costo: cada necesidad nueva exige una capacidad del motor. Se mantiene
la prohibición por ahora, con tres vías de escape en orden de seguridad:

1. **Webhooks e integraciones (008):** el código vive fuera del ERP, en el sistema que lo necesite.
2. **Biblioteca de acciones del motor (005):** cada acción nueva pasa por spec, pruebas y revisión.
3. **Scripts en sandbox (futuro, requiere ADR):** intérprete aislado, sin red ni archivos, con
   límites de tiempo y memoria, y solo para administradores. Se evaluará cuando 005 y 008 no alcancen.

Así se conserva lo que distingue al proyecto (todo es metadato auditable y la IA puede generarlo)
sin cerrar la puerta a casos que no quepan en el modelo declarativo.
