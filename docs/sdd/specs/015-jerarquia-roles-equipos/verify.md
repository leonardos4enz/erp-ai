# Verificación 015 — Jerarquía de roles, equipos y unidades de negocio

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 313/313 (+17 en `HierarchyTests`: SQL de `unitTree` y de «propios» con equipos, todo como parámetros; reducción a propios sin unidad; validación de `unitTree`/`teamOwners`; forma canónica de la política sin `teamOwners` en falso; cierre transitivo, diamantes, ciclos y unión de permisos de la herencia; «amplía o reduce» de usuario, rol, equipo, unidad y clave; huella; comparación del antes al aprobar; opción solo en entornos con aprobación) |
| E2E | 207/207 en dos corridas completas limpias seguidas, repetidas tras las correcciones de la revisión exploratoria (+20 en `jerarquia.spec.ts`; `seguridad.spec.ts` ajustado: la unidad del usuario ahora es una unidad dada de alta y se elige de una lista) |
| Contra el código anterior | Worktree en `e05072e` con `jerarquia.spec.ts`: falla en la preparación (`POST /api/admin/units` → 404) y, por ser serial, no corre el resto: la capacidad no existía. `HierarchyTests` no compila contra el código anterior (tipos nuevos) |
| Visual | Sin cambios en las capturas de referencia (*Seguridad* no tiene captura; las pestañas ahora se acomodan en dos renglones si no caben) |
| Accesibilidad | axe sin violaciones serias en Unidades, Equipos y Solicitudes (incluido el diálogo de alta) |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 árbol, sin ciclos ni nombres repetidos | `jerarquia › preparar…`, `H1 CA4`, `H1 CA1 (UI)` (alta y movimiento; no se ofrece moverla bajo sí misma) |
| H1 CA2 unidad existente, lista con sangría | `H1 CA2`, `H1 CA2 (UI)`; las etiquetas existentes pasan a unidades raíz en `Bootstrap` al arrancar (la suite parte de una base vacía: el
  `seguridad.spec.ts` ajustado da de alta «Norte» y «Sur») |
| H1 CA3 `unitTree` | `H1 CA3` (el gerente regional ve Monterrey y Saltillo, no Sur; 404 fuera de la rama; propios sin cambio) |
| H1 CA4 mover aplica en la siguiente petición | `H1 CA4` (Saltillo pasa a Sur y vuelve, sin cerrar sesiones) |
| H2 CA1–CA2 equipos y `teamOwners` | `H2 CA1 · CA2` (no es usuario; buscador con `teams=true`; `acceptsTeams` solo en el dueño; un equipo en «Revisor» → 400), `H2 CA1 (UI)`, `H2 CA2 (UI)` |
| H2 CA3–CA4 miembros, unidad del equipo, inactivo, rechazo | `H2 CA3 · CA4` |
| H3 herencia | `H3 CA1` (permisos, `HASROLE`/regla de campo y `/auth/me` por herencia, dos niveles), `H3 CA2 · CA3` (no de Administrador ni de un rol con auditoría; el heredado no gana capacidades; ciclo; a sí mismo), `H3 CA1 (UI)` |
| H4 aprobación | `H4 CA1 · CA3 · CA4` (202; quien pidió no aprueba; huella equivocada 409; aplica; no dos veces; auditoría request/approve/cambio con el aprobador), `H4 CA2` (reducir es inmediato), `H4 CA1` (rol, equipo, unidad, alta de usuario; contraseña solo como hash y oculta), `H4 CA3` (antes cambiado → 409, sigue pendiente, se rechaza), `H4 CA5` (clave solicitada no autentica hasta aprobarse; rotar y revocar inmediatos; agregar roles espera), `H4 CA3 (UI)` |
| H5 una sola regla | `H5 CA1` (buscador, reporte, bitácora y etiqueta «(sin acceso)» con `unitTree`) |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Alta | (prueba) Aprobar respondía 500: la consulta del reclamo devolvía `NULL AS ReviewedBy` como entero y Dapper no lo asignaba al registro (texto) | Corregido (`CAST(NULL AS nvarchar(100))`); cubierto por `H4 CA1 · CA3 · CA4` (falló con 500 antes de corregir) |
| F-02 | Media | (exploratoria) En Solicitudes, una edición de usuario sin cambio de empresas mostraba «Empresas: Empresa principal → —», como si se le quitaran; y una fila «Usuario — → —» | Corregido en `ChangeTable` (`null` = sin cambio); regresión en `H4 CA3 (UI)`, que falló con la tabla anterior (*Expected 0, Received 1*) |
| F-03 | Baja | (exploratoria) La unidad raíz se mostraba como «—»; el nombre de una clave (que la instantánea no guarda) aparecía como cambio «— → J15 integración» | Corregido: «Ninguna (raíz)» y sin «antes» para lo que la instantánea no guarda (solo presentación) |
| F-04 | Baja | (prueba) Un permiso repetido en la petición de un rol conserva la primera entrada (comportamiento de la 006): la prueba enviaba J15Nota dos veces y no ampliaba nada | Prueba corregida; no es un hueco (no se otorga nada) |

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5094` sobre `ErpAi_E2E` (tras `compras`, `inventario` y `jerarquia`), con
  `RequireApproval`, `RequireSecurityApproval` y `AllowDirectChanges`, como `e2e-j15-admin2`, en tema oscuro a 1024 px.
- *Unidades*: árbol con sangría y conteos de usuarios y equipos. Mover «J15 Torreón» bajo «J15 Norte» → aviso
  «Solicitud enviada…» y el árbol no cambia.
- *Solicitudes*: aviso del entorno; tres pendientes (la propia sin «Aprobar» y con la explicación; dos de `admin`).
  Detectados F-02 y F-03; corregidos y vueltos a revisar en la misma instancia.
- Aprobar la solicitud de roles de una clave que la suite ya había revocado → el diálogo muestra «Lo que se solicitó
  cambiar ya cambió desde la solicitud: recházala y vuelve a solicitarla.»; la solicitud sigue pendiente. Esc cierra el diálogo.
- 375 px en tema claro: las cinco pestañas pasan a dos renglones, las tarjetas de solicitudes y sus tablas caben, sin
  desplazamiento horizontal (`scrollWidth` = 375).
- Consola: solo el 401 previo al inicio de sesión y el 409 esperado; sin errores de JavaScript ni de CSP.

## Riesgo residual
- Con un solo administrador nada se puede aprobar (decisión de la spec y ADR-0005).
- Si el proceso se detiene entre el reclamo y la transacción de la operación, la solicitud queda en `applying`; no se
  aplica dos veces, pero hay que liberarla a mano (`UPDATE … Status = 'pending'`).
- Un administrador puede desactivar a otros sin aprobación (reducir no espera): denegación de servicio auditada.
- El filtro `unitTree` usa una subconsulta por la ruta; con miles de usuarios por unidad conviene un índice por
  `meta.[User].Unit` (hoy no existe; la 006 tampoco lo tenía).
