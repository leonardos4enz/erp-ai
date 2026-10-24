# Verificación 013 — Formulario en solo lectura fuera del alcance de escritura

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 285/285 (+3 en `RecordAccessTests`: con rol y alcance se modifica y elimina; fuera del alcance de escritura no, con el motivo del 403; el permiso del rol y el catálogo del sistema también cuentan) |
| E2E | 177/177 en dos corridas completas limpias seguidas (+1 en `seguridad.spec.ts`, +2 en `solo-lectura.spec.ts`). Corrida en un worktree aparte con su propio `bin/`: el servidor de desarrollo de otra sesión seguía en marcha en la carpeta principal y no se detuvo |
| Regresión contra el código anterior | La prueba nueva se corrió sobre `fd4725d` (worktree en HEAD con la prueba copiada; `compras`, `inventario` y `seguridad`): **falla por la razón esperada** en la parte de UI: `getByLabel(/^Nombre/)` → *Expected: disabled, Received: enabled*. La parte de API (al final) recibía `_access: undefined` en la primera versión de la prueba. Las unitarias nuevas no compilan contra el código anterior (usan `AccessOf`). `solo-lectura › CA4` sobre `fd4725d`: falla (`_access` → *undefined* al crear; el formulario seguía editable tras la transición). F-02 sobre `a251c4f`: `locator('.field-required')` → *Expected: 0, Received: 2* |
| Visual | Sin cambios en las capturas de referencia (ninguna muestra un registro fuera del alcance de escritura) |

## Cobertura
| Criterio | Prueba |
|---|---|
| CA1 `_access` en el registro | `seguridad › Spec 013 · H1` (vend2: `update`/`delete` falsos con motivo; vend1: verdaderos, sin motivo); unitarias |
| CA2 Formulario en solo lectura | `seguridad › Spec 013 · H1` (Nombre deshabilitado, partidas como texto sin celdas capturables, aviso con el motivo, sin «Guardar cambios», «Eliminar» ni «Agregar partida»; accesible) |
| CA3 El dueño sigue editando | `seguridad › Spec 013 · H1` (vend1: editable, con «Guardar cambios» y «Eliminar») |
| CA4 Ajuste tras una transición | `solo-lectura › Spec 013 · H1 CA4` (catálogo «Pruebas 013»: el autor escribe su solicitud solo en «Borrador»; al «Enviar» desde la UI el formulario pasa a solo lectura con el motivo, sin recargar; la API coincide y el PUT responde 403). El catálogo se archiva al terminar |
| CA5 El servidor sigue rechazando | `seguridad › H2 CA2 · CA3` (PUT y DELETE de vend2 → 403), sin cambios |

## Revisión exploratoria (navegador integrado)
- Instancia temporal en `localhost:5094` sobre `ErpAi_E2E` (tras la suite), con el código nuevo: `e2e-sec-vend2` abre
  *OP-0006 Expo solo lectura* de vend1: aviso «Solo lectura · Puedes consultar este registro, pero tu rol no puede
  modificarlo.», campos deshabilitados, partida como texto, «Enviar a revisión» deshabilitada (012) y sin botones de
  guardar ni eliminar. Consola: solo el 401 esperado antes de iniciar sesión.

## Hallazgos durante la verificación
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Baja | (prueba) La primera versión esperaba la celda de la partida deshabilitada; en solo lectura las partidas se muestran como texto (comportamiento existente y mejor) | La prueba verifica la celda como texto y que no haya campo capturable |
| F-02 | Baja | (exploratoria, previo) En un formulario en solo lectura —también el de un rol sin permiso de edición— seguían el asterisco de obligatorio en «Nombre» y «Concepto» y las pistas «Solo lectura para tu rol» / «Se captura al ejecutar «Perder»» | Corregido: sin obligatorios ni pistas de edición cuando todo el formulario es de solo lectura (encabezado y partidas); cubierto por `seguridad › Spec 013 · H1` (falló con `a251c4f`) |
| F-03 | Media | (proceso) Al borrar con `git worktree remove --force` los worktrees de verificación, el borrado siguió la unión `web/node_modules` y vació el de la carpeta principal | Restaurado con `npm ci`; en adelante se quita la unión (`rmdir`) antes de borrar el worktree |

## Riesgo residual
- El listado no calcula `_access` por fila; si algún día ofrece edición o eliminación en línea, deberá pedirlo.
