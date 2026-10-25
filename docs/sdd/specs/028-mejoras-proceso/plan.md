# Plan 028 — Diseño técnico

Sin cambios al producto (`src/`, `web/src/`, `modules/`). Cambian documentos de `docs/sdd/`, `CLAUDE.md` y las
herramientas de `web/e2e/`.

## 1. Documentos
| Archivo | Cambio |
|---|---|
| `CLAUDE.md` | Reglas de trabajo (STRIDE de 8 filas, revisión adversarial, alcance congelado, dividir, auditoría), comandos nuevos, servidor de desarrollo por carpeta, lecciones nuevas (borrador §6.1 revisado contra la 027) |
| `docs/sdd/README.md` | Diagrama del flujo (PLAN con 8 filas, VERIFY con revisión adversarial), cierre con plantilla y riesgos, reglas para agentes, sección «Auditorías y retrospectivas», plantillas, índice con 028 |
| `docs/sdd/verificacion.md` | Compuertas con la revisión adversarial; «Cuántas veces correr la suite»; reglas E2E nuevas (limpieza en `afterAll`, `E2E_INDEPENDENT`, captura o razón); base y puertos por carpeta; regresión y revisión con los scripts; lista de comprobación ampliada; sección 4 «Revisión adversarial» |
| `docs/sdd/plantillas/{spec,plan,verify}.md` | Nuevas (§3) |
| `docs/sdd/riesgos.md` | Nuevo (§4) |
| `docs/sdd/roadmap.md` | 028 implementada; 029 Endurecimiento 3, 030 Integración y CI (con P9), 031 Ventas, 032 CFDI; próxima auditoría |

Las referencias del borrador a cosas que aún no existen (inventario de rutas de P1, integración de P5, guardias de P9) se
escriben **como futuras**, con su spec, para que ninguna regla pida algo que no se puede hacer hoy.

## 2. Contrato de las herramientas

### Variables de entorno (las lee `run.mjs`, `playwright.config.ts` y `helpers.ts`)
| Variable | Por defecto | Uso |
|---|---|---|
| `E2E_DB` | carpeta principal: `ErpAi_E2E`; worktree: `ErpAi_<worktree>_<huella>_E2E` | Base desechable. Debe terminar en `_E2E` y solo llevar `[A-Za-z0-9_]` (se interpola en SQL) |
| `E2E_PORT_BASE` | carpeta principal: 5090; worktree: bloque libre desde 5100 + 10·(hash del nombre mod 80) | Bloque de 10 puertos: `+0` API, `+1` proveedor simulado del asistente, `+2` instancia de producción (012), `+3` aprobación de seguridad (015), `+4` aprobación de seguridad (021) |
| `E2E_INDEPENDENT` | vacío | `1`: los archivos seriales corren en modo `default` (una falla no salta el resto) |
| `E2E_SQL_SERVER`, `E2E_ADMIN_PASSWORD`, `E2E_SIGNING_KEY` | como hoy | Sin cambios |

`run.mjs` acepta `--veces=N` (N vueltas sobre base nueva, compilando una vez; agregado en VERIFY, F-13), resuelve base y puertos, comprueba que el bloque esté libre (si la variable vino fija y está ocupado, falla con
mensaje; si es automático, avanza al siguiente bloque), **revisa que ningún archivo fije `mode: 'serial'`** sin
`E2E_INDEPENDENT`, recrea la base y exporta las variables a Playwright. La lógica común vive en `web/e2e/instancia.mjs`.

### `npm run e2e:regresion -- <commit> [archivos o argumentos de Playwright] [--conservar]`
1. `git rev-parse <commit>`; worktree desacoplado en una carpeta única `%TEMP%/erp-regresion-<sha7>-XXXXXX` (dos sesiones pueden verificar el mismo commit).
2. Copia del árbol de trabajo actual: los archivos de `web/e2e/` que difieren de `<commit>` (cambiados o nuevos, sin
   borrados) y `web/playwright.config.ts`. Así el código anterior corre con las pruebas y la infraestructura nuevas.
3. Unión (`junction`) `web/node_modules` → la de la carpeta actual.
4. `node e2e/run.mjs <archivos>` en el worktree con `E2E_INDEPENDENT=1` y reporte JSON; la base y los puertos salen del
   worktree (no chocan con la carpeta principal). Sin archivos: los `*.spec.ts` cambiados.
5. Resumen: por prueba, `correcta` / `FALLA` con la primera línea del error; JSON en `web/e2e/.results/regresion-<sha7>.json`.
6. Limpieza en `finally` (salvo `--conservar`): **quitar la unión con `rmdir` (no recursivo)**, comprobar que
   `node_modules` de la carpeta actual sigue ahí, `git worktree remove --force`, borrar la base `_E2E`.
Código de salida: el de Playwright (con el código anterior, lo esperado es ≠ 0: las regresiones fallan).

### `npm run revision [-- --conservar] [-- --sin-compilar]`
Base `ErpAi_Revision_E2E` (worktree: `ErpAi_<worktree>_Revision_E2E`), recreada salvo `--conservar`; compila la SPA salvo
`--sin-compilar`; puerto libre desde 5950; contraseña y clave de firma nuevas guardadas en
`%LOCALAPPDATA%/ErpAi/revision/<base>.txt` (fuera del repo); opciones de la suite (`RequireApproval`,
`AllowDirectChanges`, firma). Imprime URL, usuario `admin` y la ruta del archivo con la contraseña. `dotnet run` en primer
plano; Ctrl+C la detiene.

### `node docs/sdd/metricas.mjs [--md]`
Lee `docs/sdd/specs/*/verify.md`. De la plantilla (bloque «Métricas» con `clave: valor`) toma inicio, cierre, duración,
unitarias, E2E, corridas y su duración, hallazgos A/M/B y por categoría, riesgos. De los anteriores cuenta filas `F-xx`
por severidad. Imprime la tabla de §1.2 de la retrospectiva («n/d» donde no hay dato).

## 3. Plantillas
- `spec.md`: estado/depende/origen, problema verificado en el código, principio, historias con CA, CLARIFY con la
  revisión de P12 (los cuatro criterios), fuera de alcance.
- `plan.md`: modelo de datos, contrato de API, SPA, pruebas y **STRIDE** con las 8 filas obligatorias + filas S/T/R/I/D/E
  propias de la spec; columna «Prueba» obligatoria (archivo › título) o `R-NNN-k`.
- `verify.md`: bloque «Métricas» (inicio, cierre, corridas con duración, pruebas, hallazgos), compuertas, revisión
  adversarial, exploratoria con hallazgos (severidad **y categoría** de la retrospectiva), «contra el código anterior»
  con el comando, riesgos con enlaces a `riesgos.md`.

## 4. Registro de riesgos
Tabla por spec de origen: ID `R-NNN-k`, riesgo, severidad, decisión (aceptado / diferido a NNN / por decidir), prueba o
«sin prueba», estado (abierto / cerrado en NNN), revisado (fecha). Reglas arriba: quién lo agrega (VERIFY), quién lo
revisa (cada auditoría empieza por él), y el tope de 5 specs para un riesgo medio o alto sin prueba.

## 5. Fragmentación
Se revisan las dependencias declaradas en los archivos (`Corre después de…`) y el estado compartido (menú en
`visual.spec`). Con evidencia en VERIFY se decide: `--shard` soportado, o documentado qué falta y a qué spec va.

## 6. Pruebas
- La suite completa en verde con las herramientas nuevas en la carpeta principal (`ErpAi_E2E`, 5090).
- **Al mismo tiempo**, la suite en un worktree con su propia base y puertos (vía `e2e:regresion` sobre el commit de la 028
  con todos los archivos): ambas en verde.
- `e2e:regresion` contra un commit anterior con una regresión conocida: falla donde debe, deja `node_modules` intacto y
  borra worktree y base.
- `revision` levanta la instancia y se inicia sesión con la contraseña del archivo.
- `run.mjs` falla con un archivo que fija `mode: 'serial'` (comprobado con un archivo temporal).
- `metricas.mjs` produce la tabla con los 28 `verify.md`.
- `dotnet test` en verde (no cambia, pero es compuerta).

## 7. STRIDE (plantilla nueva: 8 filas obligatorias)
| Fila | Pregunta | Respuesta | Prueba |
|---|---|---|---|
| Entradas | ¿Qué rutas u operaciones agrega o cambia? | Ninguna del producto. Entradas nuevas: argumentos y variables de los scripts (`E2E_DB`, `<commit>`, archivos) | §6: `run.mjs` rechaza nombres inválidos (verificado en VERIFY) |
| Caminos alternos | ¿Otro camino al mismo efecto? | Borrar bases: solo `recrearBase`/`borrarBase` de `instancia.mjs`, que exigen `_E2E` y `[A-Za-z0-9_]`; la base `ErpAi` del dueño no termina en `_E2E` | VERIFY: `E2E_DB=ErpAi` y `E2E_DB="x]; DROP…_E2E"` rechazados |
| Lectura de datos | ¿Pasa por los filtros? | No aplica: no lee datos del producto. La contraseña de `revision` va a `%LOCALAPPDATA%`, nunca al repo ni a la línea de comandos de otro proceso que no sea la API | Revisión de `git status` en VERIFY |
| Delegación | ¿Alguien otorga lo que no tiene? | No aplica: no hay usuarios ni permisos nuevos | — |
| Concurrencia | ¿Qué pasa con dos a la vez? | Dos corridas en carpetas distintas: bases y puertos distintos. Dos en la **misma** carpeta no se soportan; tras la revisión adversarial (F-05, F-06) la segunda se detiene por un candado por base en lugar de borrar la de la primera, y el nombre de la base de un worktree lleva la huella de su ruta | VERIFY: dos suites completas simultáneas en verde; candado comprobado |
| Fallas parciales | ¿Y si cae entre dos pasos? | `e2e:regresion` limpia en `finally`; si el proceso muere, queda un worktree en `%TEMP%`: el siguiente intento falla con el comando para limpiarlo, **sin borrar nada a ciegas**. La unión se quita antes que el worktree (013 F-03) | VERIFY: `node_modules` intacto tras la limpieza; worktree preexistente rechazado |
| Límites | Tamaño, tasa, topes | Puertos: hasta 80 bloques; si no hay libre, falla con mensaje. Disco: el worktree de regresión compila .NET y la SPA (~cientos de MB) y se borra al terminar | — |
| Semántica | Nulos, fechas, zonas | `metricas.mjs`: dato ausente = «n/d», nunca 0 (lección 022 F-04) | VERIFY: salida con los `verify.md` anteriores |
| **T** (inyección de comandos) | `<commit>` o archivos con metacaracteres | `git` y `node` se invocan con `execFileSync`/`spawnSync` y lista de argumentos sin `shell` donde se pasan datos del usuario; el commit se resuelve con `git rev-parse --verify` | Revisión adversarial |
| **I** (secretos en el repo) | Contraseña de `revision` o de la suite en archivos versionados | Generadas por corrida; `revision` las guarda fuera del repo; `.gitignore` sin cambios necesarios | `git status` limpio tras usarlas |
| **D** (borrado accidental) | Limpiar el worktree borra `node_modules` de la carpeta principal | `rmdir` de la unión (no recursivo) antes de `git worktree remove`; se comprueba después | VERIFY H4 CA4 |
