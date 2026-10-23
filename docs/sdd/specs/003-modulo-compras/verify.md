# Verificación 002 + 003 — Motor de procesos y módulo de Compras

**Fecha:** 2026-10-23 · **Revisor:** Claude (prueba de humo por API, navegador integrado, Playwright) · **Resultado:** Aprobada

## Compuertas
| Compuerta | Resultado |
|---|---|
| Unitarias (`dotnet test`) | 74/74 (incluye motor de expresiones, analizador, ciclos de fórmulas y compilador de procesos) |
| E2E (`npm run e2e`) | 54/54 en dos corridas limpias consecutivas (`compras.spec.ts` + suite de la spec 001) |
| Revisión exploratoria | 13 hallazgos, todos resueltos y con prueba |

## Cobertura (spec 003 · CA3: cada regla 1–9 tiene prueba)
| Regla | Prueba |
|---|---|
| R1 Entrega ≥ fecha | `compras › CA2 · R1 · R3 · R9` (UI) |
| R2 Proveedor activo | `compras › R2` |
| R3 Rangos de partidas | `compras › CA2…` (UI, error por celda) y `R3 · R4` |
| R4 Tipo de cambio en MXN | `compras › R3 · R4` |
| R5 Sin partidas no se envía | `compras › R5` (API + botón deshabilitado en UI) |
| R6 Montos > $50,000 → Director | `compras › R6` (UI muestra el motivo) |
| R7 Control interno | `compras › R7` |
| R8 Registro de autorización / motivo obligatorio | `compras › R8` ×2 (UI) |
| R9 No editable fuera de Borrador | `compras › CA2…` (UI) y `Seguridad` (API) |
| H7 Instalar módulo | `compras › H7` (UI, una transacción) |
| CA2 Flujo completo | `compras › CA2…`, `R8`, `Recepción…`, `Requisición…` |

## Hallazgos
| ID | Sev. | Cómo se encontró | Hallazgo | Corrección | Regresión |
|---|---|---|---|---|---|
| F-10 | Media | Humo API | El compilador no aceptaba un folio como campo para mostrar | Se permite `autonumber` | `compras › H7` |
| F-11 | Alta | Humo API | La auditoría truncaba la lista de catálogos tocados (columna de 100) y **abortaba** la instalación; la transacción se revirtió completa (0 tablas residuales) | Resumen "N catálogos" si excede | `compras › H7` |
| F-12 | Alta | Humo API | **Diseño del lenguaje:** `x >= 0` con `x` vacío daba falso y una regla sobre un campo opcional bloqueaba el guardado | Comparaciones de orden contra nulo = desconocido (como SQL) | `ExpressionEvaluatorTests › Ordering_against_null…` |
| F-13 | Media | Humo API | Una condición de transición cuyo mensaje empezaba con "Requiere" respondía 403 en lugar de 400 | Indicador explícito `MissingRole` | `compras › R7` |
| F-14 | Alta | Navegador | **El backend no cumplía el contrato** de `/compute` (`{ values }`): el formulario nuevo no mostraba valores sugeridos | Respuesta envuelta según plan 002 | `compras › CA2…` (tipo de cambio sugerido) |
| F-15 | Baja | Navegador | "Nuevo orden de compra" (concordancia de género con etiquetas del usuario) | Título neutro "Registrar …" | `compras › CA2…` |
| F-16 | Media | Navegador | Al enfocar la última columna de partidas **toda la página se desplazaba**: un `.sr-only` absoluto escapaba del contenedor con scroll | `position: relative` en `.table-scroll` | `compras › CA2…` (scrollX = 0) |
| F-17 | Baja | Navegador | Campos de referencia de solo lectura decían "Buscar…" | Muestra "—" | visual |
| F-18 | Media | Captura de referencia | Con el módulo, el menú plano (15 catálogos) **empujaba Configuración fuera de la pantalla** | Metadato `group` + secciones plegables; documentos primero | `compras › H7` |
| F-19 | Baja | E2E | Catálogos de pruebas antiguas chocaban con los del módulo (Almacén, Proveedor) | Pruebas usan nombres propios | suite completa |

## Decisiones tomadas durante la verificación
- El superusuario (`admin`) puede ejecutar cualquier transición, pero `HASROLE('Director')` solo es
  verdadero para quien tiene ese rol: administrar el sistema no equivale a autorizar compras.
- Los diálogos de transición validan en el cliente antes de llamar al servidor; el servidor vuelve a validar.

## Pendientes conocidos (no bloquean)
- El breadcrumb de un documento muestra `#id` en lugar del folio.
- La recepción no actualiza automáticamente el estado de la orden (spec futura: automatizaciones).
- Los procesos y reglas se editan como JSON validado en el modelador; un editor visual queda para la spec de IA.
