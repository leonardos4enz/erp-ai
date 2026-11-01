# Verify 042 — Tableros personales y compartir

## Métricas
- inicio: 2026-11-01 08:55
- cierre: 2026-11-01 10:32
- unitarias: 1048 (+28: 743 de Erp.Tests, +15; 232 de integración, +13; Vitest 73, +0)
- e2e: 454 (+16: 14 de `tableros-personales`, 1 del canario, 1 de `login`)
- corridas completas: 1 (dotnet test 2 min 09 s + E2E 12 min 12 s, limpia; una anterior de 13 min 40 s dio 452/453: la captura móvil del explorador de la 041, cambio intencional por «Compartir», no cuenta); la segunda es CI
- corridas parciales: 18 (iteración de `tableros-personales`, `canario`, `reportes-personales` y `login` 9; mutaciones contra el código anterior 7: integración 1, E2E 6; `--veces=3` 2)
- contra el código anterior: 7 (mutación de F-A en integración; de F-01–F-06 en E2E con `E2E_INDEPENDENT=1`)
- hallazgos: 0/4/9 (adversarial F-A…F-G y exploratoria F-01…F-06)
- categorías: seguridad=3, motor-api=3, UI/UX=3, accesibilidad=2, visual=1, pruebas=1
- revisión adversarial: 7 hallazgos
- riesgos: R-042-1, R-042-2, R-042-3, R-042-4, R-042-5 (y R-041-1 resuelto en parte)

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. La última corrida de CI de `main` (`fae54e5`, solo `verify.md` de la 041) estaba en
curso al abrir y terminó en **success** antes de cambiar código.

## 1. Pruebas unitarias e integración
`dotnet test`: **743 unitarias** (728 + 15 en `tests/Erp.Tests/PersonalDashboardsTests.cs`) y **232 de integración** (219 + 13 en
`tests/Erp.IntegrationTests/PersonalDashboardsTests.cs`), en verde, 2 min 09 s. Vitest: 73 (la guardia de pantallas exige las tres
capturas nuevas del editor).
- Unitarias: tarjetas válidas del modelo (indicador comparable por fecha, gráfica con agrupación), fuera de límite (0 y 21,
  vacía, tipo, fuente doble o ninguna, título largo), tipo que no va con el reporte, reporte oculto = inexistente = catálogo
  inexistente, reporte personal ajeno, tarjeta personal que corre con quien la ve; `CostlyGate` (tope, por persona, soltar dos
  veces, 64 entradas a la vez nunca pasan el tope); ninguna credencial (clave, OAuth con todos sus alcances, MCP) llega a
  `/api/my-dashboards` ni a `/api/my-reports/shared|share`.
- Integración (SQL real): solo roles propios (heredados sí; ajeno = inexistente; superusuario cualquiera); lo compartido corre
  con alcance y empresa de quien ve; sin definición ni nombres para quien no puede correrlo y filtros sin etiqueta; el reparto
  se revalida si la dueña pierde el rol o se inactiva; herencia en rombo (6 niveles × 10 roles); nadie cambia, borra ni
  comparte lo de otro (404); el tablero compartido corre cada tarjeta con quien ve, omite lo que no ve y solo enlaza a lo que
  abre; validación de tarjetas y tarjeta que deja de correr; versión (409, también antes de validar); compartir con versión
  (cinco a la vez: un 200); 10 altas con 15 guardados → 20 exactos; nombre único simultáneo; 4 corridas detenidas por otra
  conexión → la quinta (explorar, exportar o tablero) 429 y al soltarse vuelve a correr; bitácora sin definición, con roles y
  en la misma transacción (con la bitácora fallando no quedan ni el tablero ni el reparto).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 454 en verde | 12 min 12 s |
| 2 | `--veces=3` sobre `e2e/tableros-personales.spec.ts`, `e2e/canario.spec.ts`, `e2e/reportes-personales.spec.ts` (lento) | 2 vueltas en verde; en la segunda falló `reportes-personales › visual móvil` en `uiLogin` (F-06) | 3 min 42 s |
| 3 | `--veces=3` sobre los mismos y `e2e/login.spec.ts` (lento), tras F-06 | 3 × 59 en verde | 6 min 32 s |

Archivos nuevos: `tableros-personales.spec.ts` (14 pruebas: una por CA, más preparar y tres capturas; no depende de otro
archivo). Modificados: `canario.spec.ts` (`H4 (042)`: un reporte y un tablero del administrador que llegan al canario —dos
saltos, campo oculto, etiqueta de un filtro— compartidos con el rol del lector; control positivo: el dueño sí ve el canario).
Modificado también `login.spec.ts` (regresión de F-06). Capturas nuevas: `editor-light.png`, `editor-dark.png` y
`editor-movil.png`, revisadas una por una (rehechas tras F-02 y F-03: los campos ocupan su columna y el nombre tiene el foco).
Cambiadas: `explorador-light/dark/movil.png` de la 041 (el botón «Compartir», después de exportar); las de claro y móvil
quedaban dentro de la tolerancia de píxeles con la imagen vieja, así que se borraron y se generaron de nuevo para que la
referencia muestre la pantalla real; revisadas.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff HEAD` y los archivos nuevos), la tabla STRIDE del plan y el
inventario de rutas. Revisó credenciales, fugas a quien ve, oráculos (también de roles), delegación y herencia, escrituras
ajenas, concurrencia y versión, `CostlyGate`, fallas parciales, límites, SQL, bitácora y la SPA. Sin hallazgos altos ni fugas
verificadas. Encontró:

| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-A | Media | La revalidación del reparto usaba una CTE recursiva sin deduplicar sobre la herencia de todas las personas | `RevalidateAsync`: solo los dueños candidatos, con `RoleGraph.Expand` | IT `Reparto_con_herencia_en_rombo_se_revalida_a_tiempo`: **no** falla con la CTE anterior (respondió en menos de 1 s) → [R-042-1](../../riesgos.md#042) |
| F-B | Baja | Quien ve recibe los valores de los filtros y las etiquetas libres | — (aceptado) | [R-042-2](../../riesgos.md#042) |
| F-C | Baja | La tarjeta de un reporte que va solo con el tablero muestra su nombre | — (decisión 6) | [R-042-3](../../riesgos.md#042) |
| F-D | Baja | El permiso de `CostlyGate` se soltaba antes de armar el archivo de exportación | La compuerta cubre el handler completo | sin prueba que falle → [R-042-4](../../riesgos.md#042) |
| F-E | Baja | El tope vive en memoria por proceso y cuenta peticiones | — (aceptado) | [R-042-4](../../riesgos.md#042) |
| F-F | Baja | `dashboard.personal` lo ve todo auditor (nombre y roles) | — (aceptado) | [R-042-5](../../riesgos.md#042) |
| F-G | Baja | La ayuda decía que una tabla necesita agrupación; el servidor acepta cualquiera | Texto alineado | captura del editor |

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`), con un catálogo, dos roles (Equipo con
alcance sin la zona «Privada» y sin el campo Interno; Ventas) y dos usuarios de prueba creados por API en esa base. Navegador
integrado: la dueña armó y compartió un tablero (nombre de 96 caracteres con ñ y acentos, doble clic en «Guardar»: uno solo),
solo con teclado en el diálogo de compartir; el lector lo abrió en móvil y oscuro (sus cifras, sin «Privada», sin enlace al
reporte no compartido, sin desbordamiento) y por la API directa recibió 404 al cambiar, compartir y borrar lo de la dueña y
al abrir su reporte no compartido. Además, al escribir la E2E apareció F-01.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | UI/UX | Sin tableros del modelo, el inicio y la paleta no ofrecían «Tableros» (ni «Reportes»): no había cómo llegar a «Nuevo tablero» | Se ofrecen a quien lee algún catálogo | `tableros-personales.spec.ts › H1 CA1` (falla: no encuentra «Tableros» en «Análisis general») |
| F-02 | Baja | visual | Los campos de cada tarjeta ocupaban su contenido y no su columna (`stack-sm` con `justify-items: start`) | `stack` | `H1 CA1` (ancho del selector > 50 % de la tarjeta) |
| F-03 | Media | accesibilidad | Al abrir el editor (nuevo o «Editar», que desaparece) el foco caía en `body` y lo escrito se perdía; al cancelar, igual | Foco en el nombre; al cancelar, en «Editar» | `H1 CA1` y `H1 CA2` (`toBeFocused`) |
| F-04 | Baja | UI/UX | La ruta de navegación del tablero personal decía solo «Inicio» | «Tableros › Tablero personal» | `H1 CA1` |
| F-05 | Baja | accesibilidad | El diálogo de compartir enfocaba «Cerrar» (como 041 F-06) | Foco en la primera casilla | `H2 CA1` |
| F-06 | Media | pruebas | Anterior a la 042 (lo encontró `--veces=3` con `--lento`): la consulta inicial «¿hay sesión?» (401) llegaba después de iniciar sesión y la borraba; la persona volvía a un formulario vacío. Explica una intermitente posible de `uiLogin` en CI (R-030-5) | `AuthContext` descarta la respuesta si la generación de sesión cambió | `login.spec.ts › Regresión 042 (F-06)` (retiene el primer `/api/auth/me` hasta que entra) |

Además, al escribir la E2E: al guardar un tablero nuevo se limpiaba el borrador antes de navegar y la pantalla leía `label` de
nulo (lo atrapó la guardia de errores JS de `helpers.ts`); corregido antes de la revisión, cubierto por `H1 CA1`.

### Contra el código anterior
Mutación de cada corrección por separado sobre la E2E con `E2E_INDEPENDENT=1`: F-01, F-02, F-03, F-04 y F-05 hacen fallar su
prueba con el mensaje esperado (cuál falla en cada una, en la tabla); F-06, con `AuthContext.tsx` de `HEAD`, hace fallar su
prueba de `login.spec.ts`. F-A en integración: la prueba pasa con la CTE anterior
(R-042-1).

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda ni conocimiento técnico
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo (400 por tarjeta, 409 con «Volver a abrir», 429, «no disponible para ti»)
- [x] Doble clic en «Guardar» (un solo tablero), dos pestañas (409), recargar con «Volver a abrir»
- [x] Datos límite: nombre de 96 caracteres con ñ y acentos; tarjeta vacía, 21 tarjetas, roles vacíos

**Diseño**
- [x] Claro y oscuro, escritorio y móvil (390/375 px): nada tapado ni desbordado (F-02 corregido)
- [x] Solo iconos Lucide; jerarquía consistente con los tableros del modelo (mismo cuerpo de tarjeta, `WidgetBody`)
- [x] Estados vacíos («Ninguna tarjeta está disponible para ti», «No hay tableros del modelo»), carga y error con «Reintentar»

**Accesibilidad**
- [x] Diálogo de compartir y editor con teclado; foco al abrir y al cerrar (F-03, F-05); axe sin violaciones en editor,
  tablero y explorador compartido
- [x] Los diálogos atrapan el foco y se cierran con Esc

**Seguridad**
- [x] Un usuario sin el rol no ve el tablero ni llega por URL (404, «Página no encontrada»)
- [x] La consola no muestra errores ni violaciones de CSP (los 401 vistos son del cambio de sesión)
- [x] Caminos alternos del STRIDE con un usuario sin permiso, por API directa (404 en cambiar, compartir y borrar)
- [x] Dos peticiones a la vez: doble clic, dos pestañas; compartir simultáneo en integración

**Cierre**
- [x] Pasada solo con teclado por el editor y el diálogo de compartir
- [x] Hallazgos de un componente genérico: el foco inicial de los diálogos (F-05) ya estaba resuelto en los demás con
  `data-autofocus`; `Checkbox` ahora lo admite
- [x] Alcance congelado: nada nuevo pedido durante VERIFY

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md): [R-042-1 … R-042-5](../../riesgos.md#042); [R-041-1](../../riesgos.md#041) queda
resuelto en parte (explorar, exportar y tablero personal con tope; reportes y tableros del modelo sin él).
