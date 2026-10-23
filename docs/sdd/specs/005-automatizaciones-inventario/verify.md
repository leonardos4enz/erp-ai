# Verificación 005 — Automatizaciones entre registros, cantidades e Inventario

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 94/94 (+18: compilador de automatizaciones, ciclos, `merge`, filtro del buscador, campo para mostrar calculado, escala decimal) |
| E2E | 78/78 en dos corridas limpias seguidas (+16 de la spec 005; se ajustó la prueba de recepción de la spec 003, que marcaba la orden recibida a mano) |
| Visual | Capturas de referencia sin cambios (dentro de la tolerancia); no se aprobó ninguna nueva |
| Actualización real | Base `ErpAi`: Compras **1.1.0 → 1.2.0** desde la UI (conjunto de cambios #5) e instalación de **Inventario 1.0.0**; la recepción quedó con las dos automatizaciones (`AcumularRecibido`, `EntradaAlmacen`) |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 · CA2 — cuatro eventos y cuatro acciones | `automatizaciones › H1 CA1 · CA2`, `H1 CA2 · CA6` |
| H1 CA3 — misma transacción, mensaje con el nombre | `automatizaciones › H1 CA3`; `inventario › H3 CA2`, `H3 CA3` |
| H1 CA4 — validación estática y ciclos | Unitarias `AutomationCompilerTests`; `automatizaciones › H1 CA4 · CA7` y `H1 CA4 · CA5` |
| H1 CA5 — profundidad máxima 5 | `automatizaciones › H1 CA4 · CA5` (5 niveles pasan, 6 se revierten) |
| H1 CA6 — auditoría e historial «Automática: …» | `automatizaciones › H1 CA2 · CA6`; `compras › Spec 005 · H2 … UI` |
| H1 CA7 — JSON en el modelador | `automatizaciones › H1 CA4 · CA7` |
| H2 CA1 · CA2 — recibido/pendiente, buscador filtrado y sugerencias | `compras › Spec 005 · H2 CA1 · CA2`, `… desde la UI` |
| H2 CA3 · CA5 — acumula, costo último, «Recibida parcial» / «Recibida» sola | `compras › Spec 005 · H2 CA3 · CA5`, `… desde la UI`, `Recepción: …` |
| H2 CA4 — no más de lo pendiente ni partidas de otra orden; transición del sistema | `compras › Spec 005 · H2 CA4` |
| H2 CA6 — actualización sin perder datos y fórmulas recalculadas | Manual en `ErpAi`; recálculo: `automatizaciones › Fórmulas nuevas…` |
| H3 CA1–CA5 — Inventario | `inventario.spec.ts` (un escenario por criterio) |
| H3 CA6 — requiere Compras | `automatizaciones › H3 CA6` (antes de instalar Compras) |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | El buscador filtrado respondía 400 cuando el campo del filtro era un «documento padre» (la partida de OC por `Orden`) | Corregido; regresión `compras › Spec 005 · H2 CA1 · CA2`, **verificada fallando con el código anterior** |
| F-02 | Baja | El modelador mostraba propiedades `null` en el JSON de reglas, proceso y automatizaciones | Corregido; regresión en `automatizaciones › H1 CA4 · CA7`, **verificada fallando con el código anterior** |
| F-03 | Baja | El texto de una fórmula dependía del origen del decimal («pendiente 10» en memoria, «10.00» desde la base) | Corregido: la conversión fija la escala del campo; unitaria `DecimalScaleTests`, **verificada fallando con el código anterior** |
| F-04 | Baja | Al administrador se le ofrece «Nuevo» en *Existencias* aunque las crea el sistema (la regla lo rechaza con mensaje claro) | **Pendiente:** requiere metadatos de interfaz o permisos por campo (specs 006/007) |
| — | Info | `pick()` de las pruebas podía elegir una opción de la lista sin filtrar todavía; ahora espera a que la opción buscada sea la primera del listbox | Prueba robustecida |

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- Módulos: «Actualizar a v1.2.0» con vista previa válida; Inventario muestra «Usa Compras» y la vista previa
  lista el rol nuevo y las automatizaciones. Ambos aplicados sin errores en consola.
- Recepción nueva: la columna «Partida de la orden» explica «Elige primero orden de compra» antes de elegir la orden.
- Movimiento nuevo: fecha y documento origen bloqueados con «Lo asigna el sistema»; efecto calculado en vivo.
- Existencias en móvil (375 px): sin desbordamiento horizontal; estado vacío con diseño.
- Modelador: las automatizaciones se ven y editan como JSON en «Reglas, proceso y automatizaciones».

## Riesgo residual
- Dos recepciones simultáneas de la misma partida pueden pasar a la vez la regla «no más de lo pendiente»;
  el acumulado sigue exacto porque se recalcula desde la fuente con el documento bloqueado.
- Recepciones confirmadas antes de Compras 1.2.0 no tienen liga a la partida de la orden: sus cantidades no se reconstruyen.
