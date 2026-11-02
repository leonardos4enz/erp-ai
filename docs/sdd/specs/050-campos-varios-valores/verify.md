# Verify 050 — Campos de varios valores

## Métricas
- inicio: 2026-11-02 11:30
- cierre: 2026-11-02 12:58
- unitarias: 1247 (+31: 786 de Erp.Tests, +16; 377 de integración, +15; Vitest 84, +0)
- e2e: 493 (+5: 4 en `varios-valores`, 1 en `canario`; más 1 en `h1-modelador` por F-10)
- corridas completas: 2 de .NET (2 min 26 s antes de la revisión adversarial; 2 min 10 s al cerrar) y 2 E2E (15 min 51 s; 11 min 7 s al cerrar); la segunda completa es la CI del push
- corridas parciales: 7 (integración de `MultiValueTests` 4: iteración, tras la revisión adversarial con y sin corrección; E2E de iteración 3: `varios-valores`, `canario`, `h1-modelador`); lo nuevo tres veces 3 (la primera y la segunda fallaron por F-10 y por intermitentes con `--lento`, R-050-9; la tercera, 59 × 3 en verde, 5 min)
- contra el código anterior: 3 (`it:regresion -- 7ea9553 MultiValueTests`: las 13 fallan, el tipo no existía; `e2e:regresion -- 7ea9553 e2e/h1-modelador.spec.ts e2e/varios-valores.spec.ts`: fallan las 4 nuevas y pasan las 3 de antes; F-01 a F-05 se corrieron sin la corrección en esta carpeta: fallan las 4 pruebas nuevas)
- hallazgos: 0/4/6 (revisión adversarial F-01…F-09: F-01 a F-05 corregidos, F-06 a F-09 a riesgos; suite F-10, corregido)
- categorías: motor-api=4, seguridad=2, UI/UX=1, concurrencia=1, pruebas=1, visual=1
- revisión adversarial: 9 hallazgos
- riesgos: R-050-1, R-050-2, R-050-3, R-050-4, R-050-5, R-050-6, R-050-7, R-050-8, R-050-9

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0 (`7ea9553`, roadmap, solo docs). La CI de `7ea9553` estaba en curso al abrir (la
anterior, `2fd947e`, en verde) y terminó en verde (`success`) antes de cerrar. El servidor de desarrollo `erp-api` de esta carpeta estaba corriendo y se detuvo para compilar.
CLARIFY con el dueño (2026-11-02): **se divide** (la 050 es el campo de varios valores; «pertenece a» y filtros compuestos, la
051) y el campo apunta **a catálogos y a personas**.

## 1. Pruebas unitarias e integración
`dotnet test`: **786 unitarias** (770 + 16) y **377 de integración** (362 + 15), en verde (2 min 10 s). Vitest: 84.
- Unitarias nuevas (`tests/Erp.Tests/MultiValueTests.cs`, 16): DDL de la tabla de unión (nombres citados, `int` para personas,
  identificador inválido rechazado), `createEntity`/`addField` crean la tabla y no una columna, el plan rechaza destino vacío o
  inexistente, `unique`, `defaultValue`, partidas como dueño y como destino; expresiones (campo y último salto de una ruta),
  reglas y agrupaciones lo rechazan con un mensaje que nombra la 051; `set` de automatizaciones y transiciones; `requiredFields`
  de una transición (F-03); plantillas sí imprimen el campo propio; filtros con `EXISTS` parametrizado y `$me`; obligatorio
  vacío; tabla de unión de un catálogo que termina en «_» en el explorador (F-05).
- Modificadas a propósito: `NavigationTests` y `ProvenanceTests` (Compras instalado pasa a 1.11.0).
- Integración nueva (`tests/Erp.IntegrationTests/MultiValueTests.cs`, 15, SQL real): la tabla de unión y sus llaves; alta y
  lectura con ids y etiquetas, `null`, `[]` y repetidos; 400 sin escribir (no lista, texto, más de 100, inexistente, un equipo);
  obligatorio; elegir lo que no se ve (400) y conservar lo que ya tenía («(sin acceso)»); bitácora antes/después; **diez
  ediciones a la vez con la misma versión** (una 200, nueve 409, la tabla de unión es la lista ganadora); **falla después de
  escribir la tabla de unión** (403 por alcance de escritura: nada cambia); campo oculto por rol (no se lee, no se filtra, no se
  escribe, no se exporta); filtros contiene/alguno/vacío/con valores/`$me`; exportar → importar (todo o nada, misma visibilidad
  que la API); OData `$expand` con alcance, sin filtro/orden/`$select` y el tope de expansión (instancia hermana con
  `OData:MaxExpandedLines=1`); no se elimina un valor en uso (F-01); el buscador para quien captura sin leer el destino (F-02).
- `docs/sdd/rutas.md`: sin cambios (ninguna ruta nueva).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa (antes de la revisión adversarial) | 490 en verde, 2 en rojo (`visual-modelador` esperaba Compras 1.10.0: actualizada) | 15 min 51 s |
| 2 | `--veces=3` sobre compras, inventario, valuacion-*, canario, integraciones, varios-valores, visual-modelador | vuelta 1: 1 en rojo (`visual-modelador › H3 CA5`, F-10) | 3 min 40 s |
| 3 | `--veces=3`, lo mismo más h1-modelador, tras F-10 | vuelta 1: 128 en verde; vuelta 2: 3 en rojo (R-050-9 y la prueba de F-10 con la guardia, corregida) | 7 min 45 s |
| 4 | `--veces=3` sobre compras, h1-modelador, canario, integraciones, varios-valores | 59 × 3 en verde | 5 min 2 s |
| 5 | Completa final | 486 en verde, 1 en rojo (`reportes-personales › H3 CA3`: esperaba 400 por nombre repetido y llegó 201) y 6 sin correr detrás de ella en su archivo serial; el archivo completo, otra vez: 15 en verde. Intermitente ajena a la spec (no usa campos de varios valores), anotada en R-050-9 | 11 min 7 s (+33 s) |

Archivos nuevos o modificados: `varios-valores.spec.ts` (nuevo: H1 CA4, H2 CA1, H2 CA4 · H5 CA1), `canario.spec.ts` (H6 CA1:
catálogo `CnNombre` con el canario como campo para mostrar y tres campos de varios valores en `CnDoc`; registro, lista,
exportación, OData con `$expand`, filtro por el oculto y PDF), `h1-modelador.spec.ts` (F-10), `integraciones.spec.ts` y
`visual-modelador.spec.ts` (versión de Compras). Capturas de referencia: ninguna cambió (los campos nuevos de Compras no se
muestran en la lista y no hay captura del formulario de Proveedor ni de Departamento). El selector múltiple no tiene captura
propia: se revisó a mano en claro/oscuro y 390 px (abajo).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff 7ea9553 -- src web/src modules tests web/e2e`), la
descripción del cambio sin las conclusiones del plan y las ocho preguntas del STRIDE más S/T/R/I/D/E. Revisó `RecordService`
(lectura, escritura, eliminación, buscador, importación, OData, impresión), `RecordFilter`, `Scopes`, `FilterSql`,
`ReportService`, `PersonalReports`, Acompañante, MCP, webhooks, `AuditFilter`, `DerivedAccess`, `ModuleService`,
`PhysicalModel`. **Sin hallazgos altos**: concurrencia (la tabla de unión solo cambia tras el `UPDATE … RowVer`), identificadores
y parámetros, etiquetas con alcance y campo para mostrar oculto, `Strip`, OData, importación y webhooks se sostienen.

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | motor-api | Eliminar no revisaba si el registro era valor de un campo de varios valores: su etiqueta seguía en la API y no en OData | 409 «está en uso en …» si un dueño vigente lo tiene | IT › `No_se_elimina_un_registro_que_es_valor_de_un_campo_de_varios_valores` |
| F-02 | Media | seguridad | El buscador de un catálogo solo se abría a quien captura una *referencia* a él: quien captura el campo de varios valores sin leer el destino recibía 403 | Cuenta también `multireference` (con el alcance de siempre) | IT › `Quien_captura_el_campo_usa_su_buscador_sin_leer_el_catalogo_destino` |
| F-03 | Media | motor-api | Una transición podía pedir el campo en `requiredFields`, que su acción no sabe capturar (inalcanzable) | El plan lo rechaza | unit › `Una_transicion_no_pide_capturar_un_campo_de_varios_valores` |
| F-04 | Baja | motor-api | La llave de un `seed` o de un `$ref` podía ser un campo sin columna (500 al instalar) | Solo campos con columna | revisión de código (el error ahora es 400 con mensaje; sin paquete que lo ejerza) |
| F-05 | Baja | UI/UX | El explorador marcaba huérfana la tabla de unión de un catálogo que termina en «_» | Se compara contra las tablas que el modelo espera | unit › `El_explorador_reconoce_la_tabla_de_union_de_un_catalogo_que_termina_en_guion_bajo` |
| F-06 | Baja | concurrencia | El PDF lee encabezado y tablas de unión sin transacción | — | [R-050-5](../../riesgos.md#050) |
| F-07 | Baja | seguridad | «contiene» con un id que no se ve dice qué registros lo enlazan (igual que `eq` en una referencia) | — | [R-050-3](../../riesgos.md#050) |
| F-08 | Baja | motor-api | Subconsulta por fila y campo en lista y exportación; consultas por valor al guardar | — | [R-050-4](../../riesgos.md#050) |
| F-09 | Baja | UI/UX | La tarjeta del Acompañante muestra números en `multiuser` | — | [R-050-6](../../riesgos.md#050) |

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`), administrador, navegador integrado.
Compras 1.11.0 instalado desde cero: el `seed` deja «Empaque, Oficina» en el primer proveedor. Recorrido: proveedor en tema
oscuro a 1280 px (etiquetas, agregar «Refacciones» solo con teclado, Mayús+Tab al «Quitar Refacciones», Enter lo quita y el foco
vuelve al buscador, se anuncia «… quitado de Categorías que surte.», guardar «Cambios guardados»); departamento en tema claro a
390 px (agregar una persona, sin desplazamiento horizontal: `scrollWidth` 390); bitácora (el cambio se ve como `[]` → `[1]`,
ids, como define H3 CA1). Consola sin errores (salvo el 401 esperado antes de entrar).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-10 | Media | pruebas | (Lo encontró la suite con `--lento`.) La vista previa en vivo del modelador que responde 400 —una regla a medio armar— no decía nada; la guardia de alertas lo marcaba en `visual-modelador › H3 CA5` | Aviso `role="alert"` en el pie: «No se pudo validar con el compilador…» | `h1-modelador › H2: si la validación en vivo falla, el editor lo dice` (falla con `7ea9553`); la causa del 400 queda en [R-050-8](../../riesgos.md#050) |

Observación sin cambio: la bitácora muestra los ids de la lista, no sus etiquetas (como una referencia; H3 CA1).

### Contra el código anterior
- `npm --prefix web run it:regresion -- 7ea9553 MultiValueTests`: las 13 fallan (el plan rechaza `multireference`).
- `npm --prefix web run e2e:regresion -- 7ea9553 e2e/h1-modelador.spec.ts e2e/varios-valores.spec.ts`: fallan la de F-10 y las
  tres de varios valores; pasan las tres anteriores de `h1-modelador`.
- F-01 a F-05: con las pruebas escritas y sin la corrección, fallan las 4 (2 unitarias, 2 de integración); con la corrección,
  pasan.

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda ni conocimiento técnico
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo, en español claro
- [x] Doble clic, Atrás, recargar a mitad (recargar conserva lo guardado: E2E H2 CA1)
- [x] Datos límite: acentos y ñ (Papelería, Cafetería), vacíos, repetidos, más de 100

**Diseño**
- [x] Claro y oscuro, escritorio y móvil
- [x] Solo iconos Lucide
- [x] Estados vacíos («—» en la lista, «Agregar…» en el buscador)

**Accesibilidad**
- [x] Todo con teclado; foco visible; al quitar, el foco vuelve al buscador (nunca a body)
- [x] axe sin violaciones en el formulario y la lista (E2E `expectAccessible`)

**Seguridad**
- [x] Sin permiso: elegir lo que no ve 400, campo oculto no se lee ni filtra ni escribe (IT)
- [x] Consola sin errores ni CSP
- [x] Caminos alternos del STRIDE por API (IT y canario)
- [x] Dos peticiones a la vez: diez ediciones a la vez (IT)

**Cierre**
- [x] Pasada con teclado por el formulario; el modelador se probó por E2E
- [x] Componente genérico: `ReferenceSelect` gana `exclude` y `placeholder`; sus usos no cambian
- [x] Alcance congelado: lo que no entró está en el roadmap (051 y pendientes de la 050)

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#050): R-050-1 … R-050-9.
