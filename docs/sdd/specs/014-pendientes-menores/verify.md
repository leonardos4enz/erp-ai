# Verificación 014 — Pendientes menores y riesgos residuales

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 296/296 (+8 en `PendingTests`: sugerencias de Compras 1.8.0 con y sin tipo de cambio, en orden y requisición; moneda del costo capturable; traducción del filtro del acumulado con rutas al documento, nulos en dos valores, parámetros y caída a memoria; clave del bloqueo por `match`; folios provisionales y en bloque con devolución al revertir una fila; confirmación de campos locales. +3 casos en `IntegrationTests` para Teredo y 2001::/16 público. `EnvironmentTests` y `AssistantTests` ajustados al contrato nuevo) |
| E2E | 187/187 en dos corridas completas limpias seguidas (+9 en `pendientes.spec.ts`, +1 en `integraciones.spec.ts`; `entornos.spec.ts` ajustado). Se volvieron a correr dos veces tras las correcciones de la revisión exploratoria |
| Regresiones contra el código anterior | Worktree en `567f7f5` con las pruebas nuevas copiadas (detalle abajo): **todas fallan por la razón esperada** salvo «salidas simultáneas de Inventario», que pasa también antes (ver H6 CA1). Las unitarias que compilan contra el código anterior (6: Teredo y sugerencias de moneda) fallan; las demás usan tipos nuevos |
| Visual | Sin cambios en las capturas de referencia (*Entornos* no tiene captura; la suite archiva sus catálogos de prueba) |

## Cobertura y regresiones
| Pendiente | Prueba | Con el código anterior |
|---|---|---|
| 1 Precio sugerido en otra moneda | `pendientes › H1 CA1 · CA2 · CA4` (orden en USD a 17: precio 10, costo en moneda base 170; requisición con costo en USD: 170 MXN; `MonedaCosto` por defecto y regla de 3 letras) y `H1 CA3` (orden en EUR sin tipo: precio vacío); unitarias | Precio **170** (pesos) en la orden en dólares; `[170, null]` en el cálculo en vivo |
| 2 Tope del acumulado | `pendientes › H2 CA1` (10 001 movimientos previos insertados directo; el nuevo por API → existencia 10 006) | 400 «Hay más de 10,000 registros de Movimientos de inventario para acumular» |
| 3 Folios bloqueados en la vista previa | `pendientes › H3 CA1` (otra sesión retiene el nombre único de la primera fila, así la importación queda detenida tras tomar su folio; el alta simultánea termina antes y la vista previa no consume folios) | «el alta no espera a la vista previa» → *false* |
| 3 Folios bloqueados en la importación | `pendientes › H3 CA2 · CA3` (mismo método; 200 folios consecutivos, el alta recibe el siguiente al bloque; una importación con error devuelve su bloque) | «el alta no espera a la importación» → *false* |
| 4 Campos locales sin decisión | `entornos › H2 CA1 … + H4 CA1` (sección aparte con casilla; aprobar sin confirmar → 409; *Aprobar* deshabilitado hasta marcarla) y `H4 CA2 + H5 CA2` (aplicar sin confirmar → 409; auditoría con `localArchives`); unitarias | La sección no existía; se aprobaba con una advertencia |
| 5 Reintentos de 4xx | `integraciones › Spec 014 · H5 CA1` (400: fallida con 1 intento y el mensaje; 429: 3 intentos) | 400 con **3** intentos |
| 5 Teredo | unitarias `Direcciones_internas_se_bloquean` | Permitida |
| 6a Bloqueo de `match` | `pendientes › H6 CA1` (10 cargos simultáneos de −3 a una cuenta con 10, en un catálogo **sin folio**: tres 201, siete 400, saldo 1, en menos de 5 s) | **10 s** de espera (vencía el bloqueo por clave) y solo **dos** 201: se rechazó un cargo que cabía |
| 6a (Inventario) | `pendientes › H6 CA1` salidas simultáneas del mismo producto | Pasa también: la secuencia de folios de los movimientos ya serializaba las altas (20 salidas × 6 rondas sin errores). Queda como prueba de por qué Inventario estaba a salvo |
| 6b `AllowDirectChanges` | `pendientes › H6 CA2` (`directChangesOverride: true` y aviso en *Entornos*); aviso en el registro al arrancar (visto en la instancia de la revisión) | Sin el campo ni el aviso |
| 6c Bitácora con la política actual | Decisión en la [spec](spec.md#decisiones-clarify) | — |
| 7 Permisos de roles | [ADR-0004](../../../adr/0004-seguridad-como-dato-del-entorno.md) | — |

## Hallazgos durante la verificación
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Baja | (exploratoria) La sección de campos locales decía «el origen ya no los tiene», inexacto para campos que nunca existieron en el origen | Corregido: «Existen solo en este entorno: el paquete no los trae…» (solo texto) |
| F-02 | Media | (exploratoria) La casilla de confirmación aparecía a quien subió el paquete, que no puede aprobar ni aplicar | Corregido: solo con `canApprove` o `canApply`; cubierto por `entornos › H2 CA1…` (falló sin la corrección: *Expected 0, Received 1*) |
| F-03 | Baja | (prueba) Medir el bloqueo de folios por tiempos no servía: 1000 filas se importan en ~0.3 s | La prueba retiene el nombre único de la primera fila desde otra sesión (determinista) |
| F-04 | Baja | (prueba) Un acumulado por un **texto sin índice** hace que una importación y un alta simultánea se lean filas sin confirmar y se interbloqueen (500). No depende de esta spec: lo provoca el escaneo de la tabla | Fuera de alcance; anotado como riesgo residual. La prueba de folios no usa automatizaciones |
| F-05 | Media | (decisión) La spec afirmaba que el código anterior se interbloqueaba en salidas de Inventario; la prueba lo desmintió | Spec corregida con lo observado (folio que serializa; catálogo sin folio sí falla) |

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `127.0.0.1:5094` sobre `ErpAi_E2E` (tras `compras`, `inventario` y `entornos`), con
  `RequireApproval` y `AllowDirectChanges`: el registro de arranque muestra la advertencia; *Entornos* muestra el aviso
  permanente bajo la tarjeta del entorno.
- Paquete `marcas-014` exportado y subido tras agregar dos campos locales: la revisión lista «Campos de este entorno que
  se archivarán» con los dos campos y la casilla. Como `e2e-env-aprobador`, en 375 px y tema claro: *Aprobar* y *Aplicar*
  deshabilitados hasta marcar la casilla; aprobar, luego aplicar; «Paquete aplicado» y la auditoría `environment.apply`
  con `localArchives`. Sin desbordes horizontales. Detectados F-01 y F-02.
- Orden de compra nueva desde la UI con *Industrial Supply* (USD) y tipo del día 17.5: tipo de cambio 17.5 sugerido y,
  al elegir *Caja corrugada* (costo 20 MXN), precio unitario **1.14** USD.
- Producto nuevo: «Moneda del costo» editable con MXN sugerido.
- Consola: solo 401 antes de iniciar sesión y un 404 de un recurso de la SPA anterior (se recompiló con la página
  abierta); sin errores de JavaScript ni de CSP.

## Riesgo residual
- La sugerencia de precio es un valor por defecto: se calcula cuando el campo está vacío. Si se cambia la moneda de una
  orden después de capturar partidas, sus precios no se recalculan (comportamiento existente de los valores por defecto).
- Un acumulado por `match` sobre campos sin índice escanea la tabla: con altas simultáneas puede interbloquearse (F-04).
  Los módulos usan referencias (indexadas); el modelador no lo impide.
- Una importación fallida con altas simultáneas deja un hueco en la numeración del tamaño de su bloque (decisión de la spec).
- `AllowDirectChanges` se advierte pero no se impide (configuración del servidor).
