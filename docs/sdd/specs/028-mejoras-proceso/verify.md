# Verify 028 — Mejoras del proceso (retrospectiva 001)

Primer `verify.md` con la [plantilla](../../plantillas/verify.md).

## Métricas
- inicio: 2026-10-25 09:20
- cierre: 2026-10-25 10:05
- unitarias: 607 (+0)
- e2e: 385 (+0)
- corridas completas: 3 (7 min 47 s carpeta principal y 7 min 52 s worktree, simultáneas; 7 min 12 s final, simultánea con dos regresiones; las duraciones incluyen base, tipos, SPA y arranque)
- corridas parciales: 2 (--repeat-each=3 sobre asistente, endurecimiento, endurecimiento-2 y jerarquia: 9 fallan, F-13, 1 min 06 s; --veces=3 sobre esos más compras: 3 × 90 en verde, 3 min 46 s)
- contra el código anterior: 3 (e2e:regresion f6c5445 endurecimiento-2, una sola y luego dos a la vez del mismo commit)
- hallazgos: 1/9/3
- categorías: pruebas=11, proceso=2
- revisión adversarial: 11 hallazgos y 1 sospecha confirmada (F-01 a F-12)
- riesgos: R-028-1, R-028-2, R-028-3

## 1. Pruebas unitarias
`dotnet test`: **607 correctas** (sin cambios: la spec no toca el producto). 7 s.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa en la carpeta principal (`ErpAi_E2E`, puertos 5090–5094) | 385 en verde | 7 min 47 s |
| 2 | **Al mismo tiempo**, completa en un worktree vía `e2e:regresion -- HEAD e2e` (`ErpAi_erp_regresion_b77bbfb_E2E`, 5890–5894, `E2E_INDEPENDENT=1`) | 385 en verde | 7 min 52 s |
| 3 | `e2e:regresion -- f6c5445 e2e/endurecimiento-2.spec.ts` (código anterior a la 021) | 16 fallan, 1 pasa (lo esperado) | 58 s |
| 4 | Completa final, con las correcciones de la revisión adversarial | 385 en verde | 7 min 12 s |
| 5 | **Al mismo tiempo que la 4**, dos `e2e:regresion -- f6c5445` del **mismo** commit | ambas 16 fallan / 1 pasa, cada una en su carpeta (`…-LWC5qN`, `…-hwm43X`), base y puertos (5630, 5250) | ~1 min c/u |
| 6 | `--repeat-each=3` sobre los archivos cuya lógica cambió (puertos del proveedor simulado y de las instancias auxiliares, base por `DB`): asistente, endurecimiento, endurecimiento-2, jerarquia | 65 pasan, **9 fallan**: las «preparar» de las vueltas 2 y 3 (datos con nombre fijo ya creados) y `endurecimiento › H3 CA2`, que necesita Compras instalado → F-13 | 1 min 06 s |
| 7 | `--veces=3` (opción nueva, F-13) sobre esos cuatro más `compras.spec.ts` | **3 vueltas × 90 en verde**, cada una sobre base nueva | 3 min 46 s |

**Dos corridas en bases distintas no chocan (H4 CA1):** las corridas 1 y 2 y las corridas 4 y 5 fueron simultáneas y
quedaron en verde. Al terminar cada regresión el worktree y su base ya no existían (`git worktree list`,
`sys.databases`) y `web/node_modules/.bin` seguía en la carpeta principal (lección 013 F-03).

Otras comprobaciones:
- Con la corrida 4 en curso, `node e2e/run.mjs` en la misma carpeta se detuvo antes de tocar la base: «La base ErpAi_E2E la
  está usando otra corrida (PID …)».
- Un archivo temporal con `mode: 'serial'` fijo: `run.mjs` lo rechazó y nombró el archivo (H4 CA3). Se borró.
- `validarBase` rechaza `ErpAi`, `x]; DROP DATABASE ErpAi;--_E2E` y nombres con espacios; `playwright.config.ts` también
  (F-07).
- `npm run revision`: instancia en el puerto libre (5180 antes de F-09, 5950 después), base `ErpAi_Revision_E2E`,
  contraseña en `%LOCALAPPDATA%\ErpAi\revision\` (fuera del repo); inicio de sesión de `admin` con ella → 200 las dos veces.
- `node docs/sdd/metricas.mjs`: la tabla con los 27 `verify.md`; los hallazgos por severidad de 001–026 coinciden **fila por
  fila** con la tabla §1.2 de la retrospectiva, que se armó a mano.
- Ninguna prueba fija ya `ErpAi_E2E` ni un puerto (`grep` sobre `web/e2e/*.ts`: solo `helpers.ts` tiene los valores por
  defecto).
- Capturas de referencia: sin cambios (no hay pantallas nuevas).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff` y archivos nuevos) y las filas del STRIDE como
preguntas, sin las respuestas del plan. Revisó `instancia.mjs`, `run.mjs`, `regresion.mjs`, `revision.mjs`, `helpers.ts`,
`playwright.config.ts`, los `sqlcmd` de las pruebas y `metricas.mjs`; evaluó funciones con `node -e`.

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Alta | pruebas | `e2e:regresion` usaba ruta y base derivadas solo del commit, y su limpieza quitaba la unión, el worktree y la base **aunque no los hubiera creado**: dos sesiones verificando el mismo commit se borraban el worktree y la base a mitad de corrida. | Carpeta única (`mkdtemp`) y base con su sufijo; la limpieza solo quita lo que esta corrida creó. | Corrida 5: dos regresiones del mismo commit a la vez, ambas completas. Con el código anterior la segunda habría salido con «ya existe» o borrado lo de la primera (`existsSync` + mismo nombre). |
| F-02 | Media | pruebas | Ctrl+C terminaba Node sin pasar por el `finally`: la unión de `node_modules` quedaba viva en `%TEMP%`. | Manejador de `SIGINT` (spawnSync regresa y la limpieza corre) y aviso al arrancar de corridas que quedaron a medias, sin borrarlas. | — (señal de consola; no se pudo enviar Ctrl+C desde esta sesión) |
| F-03 | Media | proceso | El mensaje sugería `rmdir` sobre la unión: en PowerShell 5.1 es `Remove-Item`, que pide confirmación y, con `-Recurse`, puede vaciar el destino. | El aviso da `[IO.Directory]::Delete(ruta, $false)` y `cmd /c rmdir`, y advierte contra `-Recurse`. | — (texto) |
| F-04 | Media | pruebas | `run.mjs` corría `npx playwright` con `shell: true`: un nombre de archivo con `&` (que llega de `git diff` en un repo público) ejecutaba comandos; `%VAR%` se expandía. | `tsc` y Playwright se ejecutan con `node` y lista de argumentos, sin shell. | Corridas 3–6 con el cambio |
| F-05 | Media | pruebas | Nombres de base que colisionaban: `feat-a`/`feat.a`/`feat_a`, dos worktrees con el mismo nombre en rutas distintas, o mayúsculas (SQL Server no distingue). | La base de un worktree lleva la huella de su ruta completa en minúsculas. | Revisado en el código (la huella es de la ruta completa); no se crearon dos worktrees homónimos para probarlo |
| F-06 | Media | pruebas | Dos corridas en la misma carpeta (o un segundo clon) se borraban la base a mitad de corrida. | Candado exclusivo por base en `%TEMP%` (un PID muerto lo libera). | Comprobado con la corrida 4 en curso (arriba) |
| F-07 | Media | pruebas | `npx playwright test` directo con `E2E_DB=ErpAi` apuntaba la API y los `sqlcmd` de las pruebas a la base del dueño. | `playwright.config.ts` exige `_E2E` y `[A-Za-z0-9_]`. | — (no se ejecutó contra `ErpAi` a propósito) |
| F-08 | Baja | pruebas | `revision -- --conservar` sin el archivo de secretos **borraba** la base. | Aborta con mensaje. | — |
| F-09 | Baja | pruebas | `revision` partía de 5180, dentro de los bloques de los worktrees (5100–5899): podía tomar un puerto auxiliar de otra suite. | Parte de 5950. | — |
| F-10 | Baja | pruebas | Sin `core.quotePath=false`, los archivos con nombre no ASCII no se copiaban al worktree. | `git -c core.quotePath=false`. | — |
| F-11 | Media | pruebas | `listen(puerto)` sin host daba por libre un puerto ocupado solo en `127.0.0.1` (el proveedor simulado). Era una sospecha; se confirmó. | Se prueba en `127.0.0.1` y sin host. | `bloqueDePuertos` con 5977 ocupado en 127.0.0.1: antes devolvía 5977, ahora 5978 |
| F-12 | Media | pruebas | `revision` detenía `dotnet run` pero dejaba `Erp.Api.exe` huérfano (puerto ocupado, compilación bloqueada). | Se detiene el árbol con `taskkill /T` al recibir la señal. | Parcial: la instancia se detuvo desde fuera con `taskkill /T` (el manejador corre con Ctrl+C en la consola, que no se pudo enviar desde esta sesión) y no quedó `Erp.Api.exe` en 5180/5950 |

Hallazgo de la propia verificación (no de la revisión adversarial):

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-13 | Media | proceso | La regla nueva «lo nuevo con `--repeat-each=3`» (P7) no sirve en esta suite: repite dentro de la misma base y las pruebas «preparar» de casi todos los archivos seriales crean datos con nombre fijo, así que la vuelta 2 falla siempre. | `run.mjs --veces=N`: N vueltas, cada una sobre base nueva, compilando una vez. Las reglas (`CLAUDE.md`, `verificacion.md`, plantillas) piden `--veces=3`; `--repeat-each` queda para archivos idempotentes. | Corrida 6 (9 fallan con `--repeat-each=3`) frente a corrida 7 (3 × 90 en verde con `--veces=3`) |

No se corrigió (registrado o aceptado): `E2E_INDEPENDENT=0` cuenta como verdadero (convención de variable presente);
el TOCTOU entre elegir el bloque de puertos y que Playwright lo use (la API falla de forma visible en +0; los auxiliares,
improbable con la huella); el `## Métricas` como última sección no se lee (la plantilla lo pone primero).

## 4. Revisión exploratoria
No aplica en el sentido de la app: la spec no cambia pantallas ni la API. Se «exploraron» las herramientas como las usará
una sesión: corridas simultáneas, interrupción de la propia prueba del candado, worktrees y bases que quedan o no, salida de
`metricas.mjs` contra la tabla manual, y lectura de las plantillas como si se abriera la 029.

### Contra el código anterior
`npm --prefix web run e2e:regresion -- f6c5445 e2e/endurecimiento-2.spec.ts`: 16 de 17 fallan con el código anterior a la
021 por la razón de cada hallazgo (p. ej. «quien solo administra usuarios no da superusuario»: el código anterior lo
permitía; «la vista previa da la huella»: no existía `baseHash`); pasa solo «preparar». Es la verificación que P5 pide
para 021-3 y que la 021 hizo a mano.

### Lista de comprobación
- [x] Reglas aplicables desde la 029 sin pedir algo que no existe (lo futuro dice su spec: 029, 030).
- [x] Datos límite: carpeta con espacio («ERP AI»), nombres de worktree con guiones, puertos ocupados solo en 127.0.0.1.
- [x] Seguridad: la base `ErpAi` y la cuenta `admin` del dueño no se tocaron; ninguna contraseña en archivos versionados.
- [x] Cierre: alcance congelado (P9 se movió a la 030 en CLARIFY, no en VERIFY); hermanos del hallazgo de puertos revisados (`revision` y `run`).

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#028): R-028-1 (misma carpeta: la segunda corrida se detiene), R-028-2 (sin
fragmentación hasta la 030) y R-028-3 (`beforeAll` repetido en modo independiente).

## 6. Fragmentación (H4 CA6)
No se usa `--shard`: 13 archivos declaran que dependen de otros y las capturas del menú dependen de lo que dejan los demás
(detalle en [verificacion.md](../../verificacion.md#fragmentación---shard-todavía-no)). Va a la 030 (R-028-2). Con las
bases por carpeta, hoy se gana lo mismo en la práctica que pedía P6: dos sesiones verifican a la vez.
