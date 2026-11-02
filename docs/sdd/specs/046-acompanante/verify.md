# Verify 046 — Acompañante (motor)

## Métricas
- inicio: 2026-11-01 22:40
- cierre: 2026-11-01 23:58
- unitarias: 764 (+7)
- e2e: 468 (+0; `canario.spec.ts` recorre la ruta nueva de confirmar)
- corridas completas: 1 (11 min 21 s; 462 en verde, 1 roja por la guardia del canario, 5 sin correr tras ella)
- corridas parciales: 4 (`canario.spec.ts --veces=3`, 42 s; `CompanionTests` ×3, 45 s cada una; regresiones contra las correcciones revertidas)
- contra el código anterior: 1 (las cuatro `Regresion_F0x` con las correcciones revertidas a mano: el código anterior no está publicado)
- hallazgos: 0/3/6 (revisión adversarial F-01…F-08 y la guardia de la bitácora)
- categorías: seguridad=5, concurrencia=2, pruebas=2
- revisión adversarial: 8 hallazgos
- riesgos: R-046-1, R-046-2, R-046-3, R-046-4, R-046-5, R-046-6, R-046-7

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. CI de `main`: la corrida de `2a307c0` (spec 045, código) en verde; la de
`6e3ce85` (solo `verify.md`) estaba en curso al abrir. Sin `gh`: se consultó la API pública de GitHub con `curl`.

## 1. Pruebas unitarias e integración
`dotnet test`: **764 unitarias** (757 + 7 en `tests/Erp.Tests/CompanionCoreTests.cs`) y **324 de integración** (295 + 29 en
`tests/Erp.IntegrationTests/CompanionTests.cs`), en verde (1 min 52 s). Vitest: 73.
- Unitarias: el catálogo solo usa rutas de lectura del inventario (y ninguna de Studio, explorador, administración ni planes); las
  acciones solo despachan tres rutas de `/api/data` con efecto «datos»; nombres únicos y herramientas de datos que se purgan; la
  huella cubre tipo, catálogo, registro, cuerpo y versión; el prompt no habla de Planes de Cambios y el contexto de pantalla va
  escapado; formas con etiquetas; opciones acotadas.
- Integración (proveedor simulado, SQL real): H1–H6, concurrencia (diez mensajes, diez confirmaciones, confirmar y borrar),
  fallas parciales (turno sin trabajador, acción que quedó «aplicándose»), canario de campo oculto y catálogo ajeno, inyección
  desde una nota, bitácora con origen «acompañante», empresa y retención.
- `docs/sdd/rutas.md` regenerado: 192 rutas (+10 de `/api/companion`).
- La guardia de la 034 (`Hardening5Tests › Toda_accion_de_auditoria_esta_clasificada`) falló con las cuatro acciones de bitácora
  nuevas sin clasificar: `companion.action` va con las de referencia (se filtra por el registro) y las demás con las generales.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 462 en verde; 1 roja: `canario.spec.ts › H2 CA2` («rutas del inventario que el canario no recorre»: `POST /api/companion/actions/{id:int}/confirm`); 5 sin correr tras ella | 11 min 21 s |
| 2 | `--veces=3` sobre `e2e/canario.spec.ts` (con la llamada nueva) | 14 en verde, 3 vueltas | 42 s |

La guardia del canario hizo su trabajo: toda ruta que escribe datos y que el lector puede llamar debe recorrerse. El canario E2E
la llama con acciones ajenas o inexistentes (404 sin datos); el canario con acciones reales está en integración. La segunda
corrida completa es la de CI. Sin pantalla nueva: no hay capturas que cambien (la pantalla es de la 047).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff completo y la tabla STRIDE del plan. Revisó escalada al confirmar (ruta,
método, catálogo, id, campos especiales, empresa, identidad), fugas al proveedor y en las respuestas, concurrencia y fallas
parciales, inyección, límites, inventario y la tabla contra el código y las pruebas.

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | seguridad | Borrar conversaciones reiniciaba los topes del día (costo, conversaciones, propuestas): se contaban filas que el borrado eliminaba | Borrar deja la fila como `deleted` (sin título ni texto) y las acciones sin cuerpo ni vista previa; cuentan en los topes; la retención las borra del todo | `CompanionTests › Regresion_F01_borrar_conversaciones_no_reinicia_los_topes_del_dia` (falla revertida) |
| F-02 | Media | concurrencia | Confirmar y borrar a la vez: la escritura se aplicaba y la acción desaparecía (404 tras aplicar; con «crear», duplicado al repetir) | Confirmar bloquea primero la conversación y luego la acción, como borrar; borrar lee las acciones con `UPDLOCK, HOLDLOCK`; el resultado gana sobre `unknown` | `› Regresion_F02_confirmar_y_borrar_a_la_vez_no_pierde_una_accion_aplicada` (no determinista: pasó revertida, [R-046-5](../../riesgos.md#046)) |
| F-03 | Media | seguridad | La vista previa mostraba el id crudo de una referencia; lo legible era la explicación del modelo | El servidor resuelve la referencia como la persona (etiqueta y número) o no hay propuesta; la explicación va aparte como `segunElAcompanante` | `› Regresion_F03_una_referencia_se_muestra_con_su_etiqueta_y_debe_verse` (falla revertida) |
| F-04 | Baja | seguridad | Un 429 del limitador al confirmar dejaba la acción `failed` | 429 vuelve a pendiente, como 428 | sin prueba ([R-046-4](../../riesgos.md#046)) |
| F-05 | Baja | concurrencia | El barrido podía marcar `unknown` una acción que terminaba bien y el resultado se perdía | El resultado se guarda también sobre `unknown`; filas afectadas al log | sin prueba (cubierto por el mismo `UPDATE`) |
| F-06 | Baja | seguridad | La propuesta no exigía el lease: tras «Detener» en otra instancia o tras la purga podía aparecer una | El `INSERT` exige la conversación `running` con el lease del trabajador | sin prueba (requiere dos instancias) |
| F-07 | Baja | seguridad | Fuera de la empresa de la conversación se seguía leyendo (y por SSE) lo que se dijo ahí | `GET` y el flujo dan 404 / `end` si la empresa ya no es de la persona; confirmar desde otra empresa, 409 (ahora con prueba) | `› Regresion_F07_fuera_de_la_empresa_no_se_lee_ni_se_confirma` (falla revertida) |
| F-08 | Baja | pruebas | La purga era menor que lo dicho en la tabla (texto del modelo, entradas de llamadas) | La entrada de `proponer_accion` se purga sea cual sea su paso; el resto, riesgo | [R-046-3](../../riesgos.md#046) |
| G-01 | Baja | pruebas | Las acciones de bitácora nuevas sin clasificar (guardia de la 034) | Clasificadas en `AuditFilter` | `Hardening5Tests › Toda_accion_de_auditoria_esta_clasificada` |

Divergencias de la tabla corregidas: diez rutas (no nueve); confirmar desde otra empresa ya tiene prueba. Observaciones
atendidas: índice `CompanionAction (ConversationId, Status)`; el comentario de una transición propuesta admite 1 000 caracteres,
como el motor. Quedan como observación: un campo de solo lectura en el formulario que pide una transición no se puede proponer
(limitación funcional) y la bitácora del cambio despachado no lleva IP (la de `companion.action` sí).

## 4. Revisión exploratoria
Sin pantalla (la 047). La exploración se hizo por la API en integración, como la 036: conversaciones de punta a punta con el
proveedor simulado, como usuario común, gerente, cuenta de servicio (clave de API), persona desactivada y persona en dos empresas.

### Contra el código anterior
Las correcciones de la revisión adversarial no estaban publicadas: se revirtieron a mano (copia de `src/Erp.Api/Companion/`,
pares de reemplazo invertidos) y se corrieron las cuatro `Regresion_F0x`: F-01, F-03 y F-07 fallan; F-02 pasó (carrera, ver
R-046-5). Después se restauró la copia y la clase completa corrió tres veces en verde. Las demás pruebas nuevas son de una
capacidad que no existía (las rutas dan 404 con `6e3ce85`).

### Lista de comprobación
- [x] Camino feliz de cada historia (por API en integración)
- [x] Mensajes en español claro y en lenguaje de usuario (prompt y errores: «Los pedidos de más de 1 000 los aprueba dirección.»)
- [x] Seguridad: cada camino alterno del STRIDE con un usuario sin permiso, por API (`CompanionTests`, canario)
- [x] Dos peticiones a la vez sobre la operación nueva (confirmar ×10, mensajes ×10, confirmar y borrar)
- [x] Consola sin errores ni violaciones de CSP (guardia de la suite en la corrida completa; sin pantalla nueva)
- [x] Alcance congelado: panel, avatar, guías e instructor en el roadmap (047 y pendientes)

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#046): R-046-1 … R-046-7. **Auditoría 9 adelantada** en el
[roadmap](../../roadmap.md#auditorías): la 046 agrega un punto de entrada, un trabajo asíncrono y delegación (ADR-0010).
