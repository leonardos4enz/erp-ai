# Spec 028 — Mejoras del proceso (retrospectiva 001)

**Estado:** Implementado · **Depende de:** todas las anteriores (cambia el proceso, no el producto) · **Origen:** la
[retrospectiva 001](../../retrospectivas/001-specs-001-026.md) de las specs 001–026 y el pedido del dueño (2026-10-25) de
aplicarla como spec. Ventas pasa a la 031 y CFDI 4.0 a la 032.

## Problema
| # | Hallazgo de la retrospectiva (con evidencia en ella) | Lo que pasa hoy |
|---|---|---|
| 1 | Q1, Q2: VERIFY prueba lo que la spec construyó, desde la UI y por quien lo construyó | 21 defectos los encontraron tres auditorías externas frente a 4 de seguridad en 26 VERIFY; el STRIDE nombra amenazas sin atarlas a una prueba (015 previó el escalamiento que se escapó a la 021) |
| 2 | Q5: riesgos residuales sin dueño ni prueba | Viven dispersos en 26 `verify.md`, sin estado ni fecha de revisión; tres se volvieron defectos reales |
| 3 | §4: la suite completa se corre ~77 veces (~6.8 h) y solo una sesión de la máquina puede correrla | `run.mjs` fija la base `ErpAi_E2E` y los puertos 5090–5094; verificar contra el código anterior y montar la instancia de revisión se hace a mano en cada spec (013 F-03 vació `node_modules` al borrar un worktree) |
| 4 | §4: specs grandes y alcance que cambia en VERIFY | 025 y 026 (~4 200 líneas, 94–145 min); la 026 detuvo una corrida por un pedido nuevo; 7 de 25 specs se reabrieron |
| 5 | P13: los `verify.md` no tienen formato fijo | La tabla de métricas de la retrospectiva se armó a mano; la 025 y la 026 ni siquiera tienen fecha |

## Principio
Cambiar **reglas, plantillas y herramientas de prueba**, no el producto. Cada regla nueva se puede comprobar en el
siguiente `plan.md` o `verify.md`; cada herramienta se demuestra en el VERIFY de esta misma spec. Siguiendo P12 de la
retrospectiva, las propuestas que cambian código del producto o agregan infraestructura (P1, P2, P5, P8, P9, P14) van a
specs propias.

## Historias

**H1 — Reglas del proceso (P3, P4, P7, P12).** Como *sesión de IA que abre una spec*, quiero que `CLAUDE.md`,
`docs/sdd/README.md` y `docs/sdd/verificacion.md` digan qué exige el plan y el VERIFY, para no repetir los puntos ciegos
de 001–026.
- CA1: el STRIDE del plan tiene **8 filas obligatorias** (entradas, caminos alternos, lectura de datos, delegación,
  concurrencia, fallas parciales, límites, semántica); cada mitigación cita su prueba (archivo y título) o va al registro
  de riesgos; «revisión de código» no basta para una amenaza alta.
- CA2: cada VERIFY incluye una **revisión adversarial** de código por un subagente sin el contexto de la sesión (recibe el
  diff y la tabla STRIDE, no las conclusiones del plan); sus hallazgos son F-xx con la misma regla de oro.
- CA3: **auditoría externa** de solo lectura cada 5 specs o tras una spec que agregue un punto de entrada, un trabajo
  asíncrono o delegación de privilegios; el roadmap dice cuándo toca la siguiente.
- CA4: al cerrar, **una** corrida completa limpia + los archivos nuevos o modificados tres veces, en lugar de dos
  completas. La segunda completa la hará CI (spec 030); mientras no exista, queda dicho qué se hace.
- CA5: criterios para **dividir** una spec (dos de cuatro) y **alcance congelado** en VERIFY; «cierre completo» es parte
  de la lista de comprobación.
- CA6: las lecciones nuevas de la retrospectiva están en `CLAUDE.md`.

**H2 — Plantillas y métricas (P13).** Como *sesión que escribe una spec* o *quien hace la próxima retrospectiva*, quiero
plantillas fijas y un script de métricas.
- CA1: `docs/sdd/plantillas/spec.md`, `plan.md` (con la tabla STRIDE de 8 filas) y `verify.md` (fechas, corridas con su
  duración, pruebas, hallazgos por severidad y categoría, revisión adversarial, enlaces a riesgos).
- CA2: un script extrae de los `verify.md` la tabla de métricas por spec (§1.2 de la retrospectiva). Con los `verify.md`
  de la plantilla saca todos los datos; con los anteriores, lo que se pueda (hallazgos por severidad) y «n/d» en lo demás.

**H3 — Registro de riesgos (P10).** Como *auditor* o *sesión que planea*, quiero un solo lugar con los riesgos residuales.
- CA1: `docs/sdd/riesgos.md` con los riesgos residuales de los `verify.md` 001–027 migrados: ID, spec de origen,
  severidad, decisión, prueba que lo demuestra o «sin prueba», estado y fecha de revisión.
- CA2: la regla «un riesgo medio o alto sin prueba no queda aceptado más de 5 specs» está escrita, y los que ya la
  rebasan quedan marcados para la próxima auditoría.
- CA3: el `verify.md` de esta spec y la plantilla enlazan a sus filas en lugar de listar riesgos sueltos.

**H4 — Suites en paralelo y herramientas de VERIFY (P6, P11).** Como *sesión que verifica*, quiero correr mi suite sin
esperar a otra sesión, y no montar a mano lo de siempre.
- CA1: la base y los puertos de E2E salen del worktree: la carpeta principal sigue en `ErpAi_E2E` y 5090; un worktree usa
  `ErpAi_<worktree>_<huella>_E2E` y un bloque de 10 puertos libre. `E2E_DB` y `E2E_PORT_BASE` lo fijan a mano. **Dos corridas en
  carpetas distintas al mismo tiempo quedan en verde.**
- CA2: ninguna prueba fija `ErpAi_E2E` ni un puerto: todo sale de `helpers.ts`.
- CA3: **todos** los archivos seriales admiten `E2E_INDEPENDENT=1`, y `run.mjs` falla si un archivo nuevo vuelve a fijar
  `mode: 'serial'`.
- CA4: `npm run e2e:regresion -- <commit> [archivos]` crea un worktree en el commit, copia las pruebas cambiadas, corre con
  `E2E_INDEPENDENT=1` en su propia base y puertos, resume qué falló y por qué, y al terminar quita la unión de
  `node_modules` **antes** de borrar el worktree (013 F-03) y borra su base.
- CA5: `npm run revision` levanta una instancia para la revisión exploratoria con base desechable propia, puerto libre,
  contraseña generada y guardada **fuera del repo**, las opciones habituales, e imprime URL y usuario.
- CA6: fragmentación (`--shard`): se usa si los archivos lo permiten; si no, queda documentado qué falta.

**H5 — Guardias de la suite (P9), si caben.** Si no caben, pasan a la 030 con su razón.

## Decisiones (CLARIFY)
1. **¿Dividir la 028?** Con P12: 5 historias (≤ 6), 0 líneas de producto, sin seguridad del producto ni UI nueva, sin
   pantallas. No cumple ningún criterio: **no se divide**. H5 (P9) sí cambiaría el producto de prueba en cada pantalla y
   exige demostrar cada guardia contra el código anterior a su hallazgo (cuatro commits distintos): **pasa a la 030**
   (ver tasks y roadmap).
2. **Carpeta principal compatible.** La carpeta principal conserva `ErpAi_E2E` y el puerto 5090 para no cambiar lo que
   dicen 27 `verify.md` ni la costumbre del dueño; los worktrees son los que se mueven.
3. **Puertos por bloque.** Cada corrida reserva un bloque de 10 (API, proveedor simulado del asistente e instancias
   auxiliares de 012, 015 y 021, con holgura). En un worktree se parte de un bloque derivado del nombre (5100–5890) y se
   avanza al siguiente si alguno está ocupado.
4. **Sin CI todavía.** La «segunda corrida completa» de P7 es de CI (030). Mientras no exista: la sesión hace una
   completa + lo nuevo tres veces, y la siguiente sesión, al abrir su spec, corre la suite completa **antes** de cambiar
   código (hace de segunda corrida de la anterior; si falla, reabre la anterior).
5. **Fragmentación.** Se revisan las dependencias de orden en VERIFY (H4 CA6) y se decide con evidencia.

## Fuera de alcance (anotado en el roadmap)
- **029 Endurecimiento 3:** P1 (capacidad como metadato de cada ruta, inventario con *snapshot* y prueba) y P2 (canario
  de fugas en todas las rutas GET).
- **030 Integración y CI:** P5 (pruebas de integración con SQL real: concurrencia y fallas parciales), P8 (GitHub
  Actions con SQL Server y la suite completa), P14 (Vitest para la lógica de la SPA) y P9 (guardias de la suite).
- La primera auditoría externa con la regla nueva.
