# Verificación 006 — Seguridad por campo y por registro

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 122/122 (+28: validación de la política, traducción de permisos a SQL parametrizado, partidas que heredan el alcance del documento, acceso por campo y por estado, limpieza de salida) |
| E2E | 93/93 en dos corridas limpias seguidas (+15 de la spec 006 en `seguridad.spec.ts`; se ajustó `inventario › H3 CA4`, que esperaba 400 al forzar una existencia y ahora recibe 403 por Inventario 1.1.0) |
| Visual | Capturas de referencia sin cambios (dentro de la tolerancia); no se aprobó ninguna nueva |
| Actualización real | Base `ErpAi`: Compras **1.2.0 → 1.3.0** e Inventario **1.0.0 → 1.1.0** desde la UI, con vista previa válida; Existencias ya no ofrece «Nuevo» |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 · CA2 — oculto en GET, listado, partidas, modelo, `/compute`, búsqueda y orden; solo lectura conserva su valor | `seguridad › H1 CA1 · CA2` |
| H1 CA3 — editable por estado (encabezado y partidas); el motivo solo en su transición | `seguridad › H1 CA3` |
| H1 CA4 — formulario y listado sin ocultos, bloqueos con explicación, a11y | `seguridad › H1 CA4` |
| H1 CA5 · H5 — vista previa rechaza políticas inválidas; editor JSON del modelador | Unitarias `SecurityPolicyTests`; `seguridad › H1 CA5 · H5`, `H5` |
| H2 CA1 · CA4 — propios / unidad / todos; 404 en GET, historial, edición, baja, transición y `/compute`; buscador, referencias y conteos por estado filtrados | `seguridad › H2 CA1 · CA4` |
| H2 CA2 · CA3 — regla `where`; leer no da para editar (403) pero sí para transiciones | `seguridad › H2 CA2 · CA3` |
| H2 CA5 — no crear ni dejar un registro fuera del alcance (se revierte) | `seguridad › H2 CA5` |
| H2 CA6 — unidad asignada en Seguridad cambia lo que ve el supervisor | `seguridad › H2 CA6` |
| Automatizaciones con autoridad del motor | `seguridad › Las automatizaciones actúan…` |
| H3 CA1 — `systemManaged` (F-04) | `seguridad › H3 CA1` |
| H4 CA1–CA3 — Compras 1.3.0 (F-25, requisiciones propias, importes ocultos al almacenista) | `seguridad › H4 CA1`, `H4 CA2`, `H4 CA3` |
| H4 CA4 · CA5 — Inventario 1.1.0; actualización sin perder datos | `seguridad › H3 CA1`; manual en `ErpAi` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | El cuerpo de un diálogo con desplazamiento (vista previa de un plan largo) no se podía enfocar con el teclado (axe `scrollable-region-focusable`) | Corregido: el cuerpo se vuelve región enfocable cuando se desplaza; regresión `seguridad › H5` (`expectAccessible`), **verificada fallando con el código anterior** |
| F-02 | Baja | La pista «Se captura en su acción» no decía en qué acción | Corregido: «Se captura al ejecutar «Rechazar»»; regresión en `seguridad › H1 CA4` y `H4 CA1`, **verificada fallando con el código anterior** |
| F-03 | Baja | El modelador ofrece «Revisar cambios» sin cambios en *Orden de compra*: renumera `displayOrder` cuando no es consecutivo (comportamiento previo, no de esta spec) | **Pendiente:** metadatos de interfaz (spec 007) |
| F-04 (005) | Baja | «Nuevo» en Existencias para el administrador | **Resuelto** con `systemManaged` (Inventario 1.1.0) |
| F-25 (004) | Media | «Motivo de rechazo» capturable en Borrador | **Resuelto** con `editableIn: []` (Compras 1.3.0) |

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- Módulos: «Actualizar a v1.3.0» y «v1.1.0» con vista previa que describe cada política («Definir la seguridad de
  'Requisición': 1 regla(s) de campo, 2 permiso(s) por registro»); aplicados sin errores en consola.
- Existencias: sin «Nuevo»; el estado vacío dice «Los registra el sistema automáticamente».
- Requisición nueva: «Motivo de rechazo» bloqueado con la pista de la acción que lo captura.
- Modelador: la sección avanzada se abre sola cuando hay política y muestra el JSON sin `null`.
- Seguridad › Usuarios: campo «Unidad» con sugerencias; sin desbordamiento horizontal. No se modificó al usuario `admin`.
- Los roles distintos al administrador se revisaron con usuarios propios en la base desechable `ErpAi_E2E`.

## Riesgo residual
- Una fórmula visible que depende de un campo oculto revela información derivada: el modelador debe ocultar también
  los campos derivados (Compras 1.3.0 oculta precios **y** importes).
- Las etiquetas de referencias (`__label`) muestran el campo para mostrar del registro referido aunque el usuario no
  pueda abrirlo; por eso el campo para mostrar no se puede ocultar.
- La auditoría muestra valores completos a quien tiene ese permiso (privilegio explícito).
- `PUT /admin/users/{id}` sin `unit` la quita, igual que `roleIds` vacío quita los roles.
