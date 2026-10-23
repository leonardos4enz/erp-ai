# Verificación 004 — Campos asignados por el sistema y actualización de módulos

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 76/76 (+2: campo bloqueado obligatorio necesita valor por defecto; transiciones pueden asignar pero no pedir capturarlo) |
| E2E | 58/58 en corrida limpia (+4 de la spec 004) |
| Actualización real | Base de desarrollo del usuario: Compras **1.0.0 → 1.1.0** desde la UI, sin perder registros |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1/CA2 — fecha y solicitante no se suplantan al crear ni al editar (API directa) | `compras › Spec 004 · H1` |
| H1 CA3 — UI bloqueada con «Lo asigna el sistema» | `compras › Spec 004 · H1` |
| Seguridad — «Autorizado por» solo por transición | `compras › Spec 004: "Autorizado por"…` |
| H2 CA1 — instalación nueva incluye actualizaciones | `compras › Spec 004 · H2` y todo `compras.spec.ts` |
| H2 CA2 — actualización de una instalación existente | Manual sobre la base del usuario (la suite siempre instala desde cero) |
| H3 — almacén por partida sugerido desde el encabezado | `compras › Spec 004 · H3` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-23 | **Alta** (seguridad) | «Autorizado por» y «Fecha de autorización» eran capturables en Borrador | Corregido en Compras 1.1.0 |
| F-24 | Baja | La vista previa decía «Actualizar campo» sin explicar el cambio | Corregido: indica «ahora lo asigna el sistema» |
| F-25 | Media | «Motivo de rechazo» es capturable en Borrador; debería pedirse solo en la transición | **Pendiente:** requiere reglas de campo por estado (roadmap · spec 006) |
| — | Info | La regresión visual detectó el cambio intencional del modelador; se revisó la captura antes de aprobarla | — |
