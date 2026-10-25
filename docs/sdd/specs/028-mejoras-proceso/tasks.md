# Tareas 028

## Reglas y documentos (H1)
- [x] T1 Retrospectiva 001 en `main` (`docs/sdd/retrospectivas/001-specs-001-026.md`).
- [x] T2 `CLAUDE.md`: reglas de trabajo, comandos, servidor por carpeta, lecciones nuevas.
- [x] T3 `docs/sdd/README.md`: flujo, cierre, reglas para agentes, auditorías y retrospectivas, plantillas, índice.
- [x] T4 `docs/sdd/verificacion.md`: compuertas, cuántas veces correr, reglas E2E, scripts, lista ampliada, revisión adversarial.

## Plantillas y métricas (H2)
- [x] T5 `docs/sdd/plantillas/spec.md`, `plan.md` (STRIDE de 8 filas), `verify.md` (bloque de métricas).
- [x] T6 `docs/sdd/metricas.mjs`: tabla de §1.2 desde los `verify.md`.

## Riesgos (H3)
- [x] T7 `docs/sdd/riesgos.md` con los residuales de 001–027 migrados y revisados.

## Herramientas (H4)
- [x] T8 `web/e2e/instancia.mjs`: carpeta/worktree, nombre de base validado, bloque de puertos libre, recrear/borrar base.
- [x] T9 `run.mjs` con base y puertos por carpeta y la revisión de `mode: 'serial'`.
- [x] T10 `playwright.config.ts` y `helpers.ts` (`PORTS`, `DB`); quitar puertos y `ErpAi_E2E` fijos de las pruebas.
- [x] T11 `E2E_INDEPENDENT` en todos los archivos seriales.
- [x] T12 `e2e/regresion.mjs` y `npm run e2e:regresion`.
- [x] T13 `e2e/revision.mjs` y `npm run revision`.
- [x] T14 Revisar dependencias de orden para `--shard`; decidir y documentar.

## Guardias (H5)
- [x] T15 Movidas a la 030 (decisión 1 de CLARIFY): registradas en el roadmap con problema, alcance y verificación.

## Roadmap y cierre
- [x] T16 Roadmap: 028 implementada; 029, 030, 031 Ventas, 032 CFDI; próxima auditoría.
- [x] T17 VERIFY: suites en verde, dos corridas simultáneas, regresión contra un commit anterior, `revision`, revisión
  adversarial, `verify.md` con la plantilla nueva.
- [x] T18 Commit y push.

## Agregadas en VERIFY
- [x] T19 Correcciones de la revisión adversarial (F-01 a F-12): carpeta y base únicas en `e2e:regresion`, limpieza solo de lo creado, `SIGINT`, aviso seguro para la unión, sin shell, huella de la ruta en la base, candado por base, `E2E_DB` validado en la configuración, `--conservar`, puerto 5950, `core.quotePath`, puertos en 127.0.0.1, árbol de procesos de `revision`.
- [x] T20 `run.mjs --veces=N` y reglas con `--veces=3` en lugar de `--repeat-each=3` (F-13).
