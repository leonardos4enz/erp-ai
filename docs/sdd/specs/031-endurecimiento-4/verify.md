# Verify 031 — Endurecimiento 4: lo que se le dice a una persona, con su alcance

## Métricas
- inicio: 2026-10-25 16:20
- cierre: 2026-10-25 18:30
- unitarias: 703 (+16: 636 de Erp.Tests, +8 de esta spec; 67 de integración, +8)
- e2e: 353 (+2 en canario.spec.ts)
- corridas completas: 2 (6 min 35 s con 1 falla esperada: la versión fija de Costeo; 6 min 54 s la final tras la revisión adversarial)
- corridas parciales: 7 (canario ×3 iterando; --veces=3 sobre compras, inventario, valuacion-contable, valuacion-politicas y canario; canario --veces=3 tras la revisión; Hardening031Tests ×3; mutaciones de Hardening4Tests)
- contra el código anterior: 3 (it:regresion 7fce1d6 Hardening031Tests; e2e:regresion 7fce1d6 e2e/canario.spec.ts; mutación de Hardening4Tests y de la política por omisión)
- hallazgos: 0/3/7
- categorías: seguridad=8, pruebas=2
- revisión adversarial: 10 hallazgos (0 altos, 3 medios, 7 bajos)
- riesgos: R-031-1, R-031-2, R-031-3, R-031-4, R-031-5, R-031-6, R-031-7 (y R-029-3, R-029-6 actualizados)

## 0. Apertura
`git log`: la 030 cerrada (`7fce1d6`), `git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main`
(`7fce1d6`) en verde: es la segunda corrida completa de la 030.

## 1. Pruebas unitarias e integración
`dotnet test`: **703 correctas**: 636 de `tests/Erp.Tests` (628 + 8 nuevas) y 67 de `tests/Erp.IntegrationTests` (59 + 8).
- `Hardening4Tests` (6): mover a un usuario o equipo a la unidad propia (también desde una hija con alcance «unit»: F-B),
  reactivar un equipo del que se es miembro (F-C), crear no cuenta, `UNITCOST` con el valor del saldo oculto (F-A).
- `RouteInventoryTests` (+2): la política por omisión niega al superusuario y la de una ruta declarada lo deja pasar; tableros,
  reportes y OData son costosos con límite.
- `CostingPolicyTests`: la versión de Costeo pasa a 1.2.0.
- `Hardening031Tests` (8, SQL real, ×3 sin fallas): importación = `/compute`; confirmar pregunta por lo que la persona ve;
  bloquear dice lo que ve; la automatización que bloquea; `UNITCOST` en el PDF (texto de los flujos Flate); aprobación al
  mover un usuario y un equipo (instancia con aprobación de seguridad); costos con la clave de «Integración de tienda» por
  API, OData y `$metadata`; rutas de `Program` en `rutas.md`.

`npm --prefix web test` (Vitest): 30 correctas, sin cambios.

### Contra el código anterior
| Comando | Resultado |
|---|---|
| `npm --prefix web run it:regresion -- 7fce1d6 Hardening031Tests` | **7 fallan**: la importación no da el aviso (`[]` en lugar de «el dato secreto está vacío»); la transición con `confirm` pasa sin preguntar (200, estado «Revisado»); el bloqueo da el genérico en lugar del aviso que ve; el 400 de la automatización incluye el aviso del campo oculto; el PDF imprime «123.45»; el revisor del norte puede aprobar (`canApprove` verdadero); la clave de la tienda recibe `Valor`. Pasa la de rutas de `Program` (guardia: con `7fce1d6` no había rutas fuera del inventario) |
| `npm --prefix web run e2e:regresion -- 7fce1d6 e2e/canario.spec.ts` | **2 fallan**: «H1 CA1 (031): la vista previa de la importación le dice al lector los mismos avisos que /compute» y «H4 CA3» (mensaje genérico). Las demás pasan: el recorrido de rutas y XLSX no encontraron fugas en el código anterior, y ordenar por ocultos ya se ignoraba (`ResolveSort`): son guardias |
| `Hardening4Tests` con `GainsMember` sin el caso de alcance «unit», sin la regla de reactivar y sin `UNITCOST` en `DerivedAccess` y `ReadsHidden` (mutación; las funciones no existen en `7fce1d6`) | 4 fallan; restaurado, pasan |
| `RouteInventoryTests › Una_ruta_sin_capacidad_declarada_no_responde_a_nadie` con `AddAuthorization` sin política por omisión (como en `7fce1d6`; mutación) | falla (`FallbackPolicy` nula); restaurado, las 10 de inventario pasan |

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `canario.spec.ts` iterando (3 veces) | la 2.ª encontró F-01; luego 10 en verde | ~10 s c/u |
| 2 | Completa | 342 en verde, 1 falla (`valuacion-politicas › H8` fijaba Costeo 1.1.0), 10 sin correr por el modo serial | 6 min 35 s |
| 3 | `--veces=3` sobre `compras`, `inventario`, `valuacion-contable`, `valuacion-politicas` y `canario` | 3 × 69 en verde | 2 min 58 s |
| 4 | Completa final, tras la revisión adversarial | **353 en verde** | 6 min 54 s |
| 5 | `--veces=3` sobre `canario` (cambió con F-I) | 3 × 10 en verde | 26 s |

Archivos modificados: `canario.spec.ts` (+2 pruebas y el recorrido de rutas no GET del inventario), `valuacion-politicas.spec.ts`
(versión de Costeo), `playwright.config.ts` (`Security__AggregatesPerMinute`). Sin pantallas nuevas ni capturas cambiadas.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión, solo lectura, con el diff sin commitear (archivos nuevos incluidos) y las ocho
preguntas del STRIDE sin sus respuestas. Revisó los caminos al mismo aviso (importación, transiciones humanas y por
automatización, PDF, exportación, OData, `_warnings`), la copia del borrador, la política por omisión, `GainsMember`, Costeo
1.2.0 en todos los caminos de lectura y si cada prueba falla con el código anterior. Confirmó sin defecto: 428 contra 200 solo
depende del cálculo de la persona, `ShownWarnings` no lee con la autoridad del motor, `CloneDraft`, la concurrencia, H2 en
API/OData/`$metadata`/filtros/orden/reportes/exportación/webhook, y que las pruebas de H1–H5 fallarían con el código anterior.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | pruebas | (de la sesión) La fila de importación del canario de la 029 incluía «Puente visible», cuyo campo para mostrar es una fórmula que el lector no ve: la fila siempre fallaba y la vista previa no calculaba avisos, así que «tampoco los POST…» no probaba la importación | Fila con solo «Puente doble»; la prueba exige `valid` | `canario › H1 CA1 (031)` (exige `valid: true`; falla con `7fce1d6` por el aviso) |
| F-A | Media | seguridad | `UNITCOST` solo cortaba por alcance por registro: con `Existencia.Valor` oculto (Costeo 1.2.0), una plantilla o un aviso con `UNITCOST('Existencia', …)` le daba el costo a quien no ve el valor | `DerivedAccess.CostBookFields`: los campos que alimentan las acciones `cost` del libro cuentan como lectura en `DerivedAccess`, `ReadsHidden` y `RelatedLoader.UnitCost` | `Hardening4Tests › UNITCOST_se_oculta_a_quien_no_ve_el_valor_del_saldo` (falla por mutación) |
| F-B | Media | seguridad | `GainsMember` trataba como «ya lo veía» a alguien de una unidad hija, pero con alcance «unit» (exacto) no se ve: pasarlo a la unidad propia se aprobaba | Gana también si la nueva es la propia y la anterior no | `Hardening4Tests › Mover_a_un_usuario_o_equipo…` (la prueba fijaba el error; falla por mutación) |
| F-C | Media-baja | seguridad | Reactivar un equipo del que quien revisa es miembro le devolvía sus registros | Regla en `team.update` | `Hardening4Tests › Reactivar_un_equipo_del_que_se_es_miembro_lo_aprueba_otro` (falla por mutación) |
| F-D | Baja | seguridad | Nombres de unidad comparados en C# sin la intercalación de la base | — | [R-031-4](../../riesgos.md#031) |
| F-E | Baja | seguridad | `FallbackPolicy` no cubre rutas de grupos con `.RequireAuthorization()` sin capacidad | Spec CA2 precisada; lo sigue deteniendo el inventario | [R-031-5](../../riesgos.md#031) |
| F-F | Baja | seguridad | Una `OverflowException` en el cálculo para la persona tumbaba la importación o la transición | `ShownWarnings` atrapa también `ArithmeticException` (SQL no) | [R-031-2](../../riesgos.md#031) (sin falla inyectada) |
| F-G | Baja | seguridad | Un bloqueo asíncrono sin rastro de qué aviso bloqueó | — | [R-031-6](../../riesgos.md#031) |
| F-H | Baja | seguridad | La vista previa de importación abarata el oráculo de las reglas de error (R-029-3) | — (spec de pendientes) | [R-031-7](../../riesgos.md#031) |
| F-I | Baja | pruebas | Encabezado de `Hardening031Tests` decía que todas fallan con el código anterior; el canario filtraba capacidades exactas; dos pruebas son guardias | Encabezado corregido; filtro por capacidad alternativa; las guardias anotadas arriba | `canario › H2 CA2: tampoco las rutas que no son GET…` |
| F-J | Baja | seguridad | El límite de 120/min alcanza a clientes OData que paginan | — | [R-031-3](../../riesgos.md#031) |

## 4. Revisión exploratoria
No aplica: la spec no cambia pantallas (sin componentes ni estilos nuevos); el 428 y el 400 llegan a los diálogos de
siempre, que cubren las guardias de alerta y foco de la 030 en la corrida completa. La SPA servida con la política por
omisión se ejerció en las dos corridas completas (inicio de sesión, navegación profunda y recarga).

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#031): R-031-1 (`Producto.CostoUltimo` para la Integración de tienda), R-031-2
… R-031-7; actualizados [R-029-3](../../riesgos.md#029) (bit del bloqueo; la importación y el 428 ya no) y
[R-029-6](../../riesgos.md#029) (la salida por la cuenta de servicio, cerrada; el almacenista, decisión del dueño).
