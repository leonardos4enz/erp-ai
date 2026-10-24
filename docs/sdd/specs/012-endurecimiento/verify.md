# Verificación 012 — Endurecimiento

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 282/282 (+4 en `HardeningTests`: la bitácora quita campos ocultos del encabezado y las partidas, conserva el estado y descarta campos que ya no existen, sin tocar al superusuario ni eventos que no son de datos; la condición SQL de la bitácora solo incluye catálogos legibles, con empresa y regla `where`, y todo valor va como parámetro; con aprobación los caminos directos lanzan 409 salvo `AllowDirectChanges`; la huella de revisión cambia con el plan, los pasos o las advertencias y no se confunde al mover texto entre partes) |
| E2E | 174/174 en dos corridas limpias seguidas (+10 en `endurecimiento.spec.ts`, +2 en `asistente.spec.ts`). Corridas en un worktree aparte con su propio `bin/`: el servidor de desarrollo de otra sesión seguía en marcha en la carpeta principal y no se detuvo |
| Regresiones contra el código anterior | Las 12 pruebas nuevas se corrieron sobre `cd81760` (worktree en HEAD con las pruebas nuevas copiadas): **todas fallan por la razón esperada** (detalle abajo). Las unitarias nuevas no compilan contra el código anterior (usan tipos nuevos); la regresión de cada hallazgo es la E2E |
| Visual | Sin cambios de pantalla salvo el aviso del asistente en un entorno con aprobación (no aparece en la suite, que permite cambios directos); referencias sin cambios |

## Cobertura y regresiones
| Hallazgo | Prueba | Con el código anterior |
|---|---|---|
| 1 Bitácora sin seguridad | `endurecimiento › H1 CA1 · CA2 · CA3` (auditor con lectura solo de tareas públicas: no ve la privada, la de otra empresa, el catálogo sin permiso ni el campo oculto; el total cuenta solo lo visible; el superusuario ve todo); unitarias de `AuditFilter` | Llegaban la tarea privada (`["3","2","1"]`) y el secreto |
| 2 Transiciones sin alcance de escritura | `endurecimiento › H2 CA1 · CA2` (Beto ve la tarea de Ana; «Cerrar» sin roles: deshabilitada con motivo y 403; «Revisar» con roles: 200) | `allowed: true` y Beto la cerraba |
| 3 Sobre-recepción concurrente | `endurecimiento › H3 CA1` (10 confirmaciones simultáneas de 10 contra una partida de 10: una 200, nueve 400, recibido 10) y `H3 CA3` (6 rondas de confirmar y cancelar a la vez: gana una) | H3 CA1: nueve **500** (interbloqueos al bloquear la orden tarde); H3 CA3: ronda 1 con **200/200** (recepción confirmada sobre orden cancelada) |
| 3 Regla en el destino (Compras 1.7.0) | `endurecimiento › H3 CA2 + H7 CA1` | Versión 1.6.0, sin la regla |
| 4 Aprobación con atajos | `endurecimiento › H4 · producción` (segunda instancia en :5092 con `RequireApproval` y sin `AllowDirectChanges`: plan, actualizar e instalar módulo y aplicar propuesta → 409 «Entornos»; la vista previa sí; `directChanges: false`); unitaria | El plan no respondía 409 |
| 5 Límite del asistente | `asistente › Spec 012 · H5 CA1` (35 solicitudes simultáneas con el proveedor lento y 30 lugares: 30 llegan al proveedor, 5 reciben 429, ninguna queda `pending`) | Las 35 pasaban |
| 5 Huella de lo revisado | `asistente › Spec 012 · H5 CA2` (sin huella 400; otra persona agrega una regla y el plan la quitaría → 409 «revisaste»; con la huella nueva, 200); unitaria | No había huella |
| 6 Etiquetas fuera de alcance | `endurecimiento › H6 CA1 · CA2` (cliente de otra empresa y tarea no pública: `(sin acceso)` en detalle, listado y cálculo; buscador ya filtraba) | Se leía «Cliente de otra empresa 012» |
| Menor: moneda base con registros | `endurecimiento › H7 CA2` | 200 al pasar de MXN a USD |

## Hallazgos durante la verificación
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | Con la huella como parámetro de cuerpo, `POST /apply` **sin** cuerpo JSON no encontraba la ruta (404 «Ruta no encontrada») en lugar de 409 «ya se aplicó»; lo detectó `asistente › H2 CA1` y, al cortar la suite serial, dejó el grupo «Pruebas IA» en el menú y empujó «Auditoría» fuera de la pantalla en `compras › H7` (falla en cascada de cinco archivos) | Corregido: el cuerpo se lee solo si es JSON; cubierto por `asistente › H2 CA1` (falló en la primera corrida) |
| F-02 | Baja | (prueba) `integraciones › H5 CA1` simula Compras 1.4.0 sobre un modelo ya actualizado y restauraba la versión a 1.5.0 en lugar de la instalada; con 1.7.0 la cadena repite un `addField` y la vista previa deja de ser válida | La prueba restaura la versión real y verifica solo la lista de roles (lo que protege) |
| F-03 | Baja | (prueba) La limpieza no podía archivar documentos con partidas (se referencian entre sí) | La limpieza archiva primero el campo de partidas |
| F-04 | Baja | (exploratoria) En un entorno con aprobación el modelador se enteraba de que la propuesta no se aplica aquí **después** de gastar una solicitud | Corregido: el redactor lo avisa cuando `directChanges` es falso. Solo texto: la regla la aplica el servidor (409), cubierta por `endurecimiento › H4` |
| F-05 | Media | (exploratoria, previo a 012, de la 006) Un vendedor que solo puede **ver** una oportunidad ajena recibe el formulario con campos editables y «Guardar cambios»; el servidor rechaza con 403 al guardar. No es una fuga, pero confunde | Fuera de alcance de esta spec; resuelto en la [013](../013-formulario-solo-lectura/verify.md) |

## Decisiones y diferidos
Ver [spec](spec.md#decisiones-clarify): transiciones con roles siguen sin exigir escritura; superusuario sin filtro en la
bitácora; en un entorno con aprobación se bloquean los caminos directos (también los módulos del servidor); acumulado en
memoria, folios bloqueados durante la importación y `ModelDiff` que archiva campos locales quedan diferidos con su razón.

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `127.0.0.1:5094` sobre la base desechable `ErpAi_E2E` (tras la suite), con
  `RequireApproval` y sin `AllowDirectChanges`, para entrar con los usuarios de prueba sin tocar la sesión de `localhost`:
  - `e2e-sec-vend2` abre una oportunidad pública de otro vendedor: «Enviar a revisión» (sin roles) aparece bloqueada
    con el motivo en el título y, al pulsarla, un aviso «Puedes consultar este registro, pero tu rol no puede
    modificarlo». Detectado F-05 (previo).
  - En *Cotizaciones*, una cotización que apunta a una oportunidad privada muestra «(sin acceso)» en el listado y en el
    detalle; guardarla sin cambios conserva la referencia («Cambios guardados»).
  - `e2e-ia-modelador` en *Asistente IA*: la propuesta muestra «Este entorno exige la aprobación de otro
    administrador…» y «Aplicar…» deshabilitado; en 375 px no hay desbordes (F-04 corregido y verificado tras recargar).
  - Consola: solo los 401/404 esperados de la sesión; sin errores de JavaScript ni de CSP.
- **Base de desarrollo `ErpAi`** (servidor `erp-api` relanzado con el código nuevo): Compras actualizado de 1.6.0 a
  **1.7.0** desde *Módulos* (vista previa válida de 4 pasos; conjunto de cambios #15). No se cambió la contraseña de
  `admin` ni otros datos.

## Riesgo residual
- Los bloqueos previos a validar solo cubren destinos `target` de automatizaciones; un acumulado por `match` (sin
  `target`) usa el bloqueo por clave existente y una regla que lea otro documento sin automatización no se bloquea.
- La bitácora de un registro cuyo catálogo cambió de política se filtra con la política **actual**.
- Producción sin cambios directos implica que todo cambio de modelo viaja como paquete; un entorno mal configurado con
  `AllowDirectChanges` reabre el atajo (es configuración del servidor, fuera de la app).
