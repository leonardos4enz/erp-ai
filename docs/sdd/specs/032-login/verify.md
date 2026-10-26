# Verify 032 — Rediseño del inicio de sesión

## Métricas
- inicio: 2026-10-25 18:03
- cierre: 2026-10-25 18:50
- unitarias: 705 (+2: 636 de Erp.Tests sin cambios; 69 de integración, +2) y Vitest 36 (+6)
- e2e: 374 (+22: 19 en login.spec.ts y 3 capturas en visual.spec.ts)
- corridas completas: 2 (7 min 36 s antes de la revisión adversarial; 7 min 22 s la final)
- corridas parciales: 9 (login.spec.ts ×4 iterando; capturas del login con --update-snapshots; --veces=3 sobre login + h5-h6-seguridad + jerarquia (falló la vuelta 1 por F-08), sobre visual -g login, sobre login + h5-h6-seguridad y sobre jerarquia; jerarquia sola; ApiSecurityTests)
- contra el código anterior: 3 (e2e:regresion 71246b2 e2e/login.spec.ts; reversión local de las correcciones de la revisión; e2e:regresion 71246b2 h5-h6-seguridad + jerarquia para F-08)
- hallazgos: 0/2/6
- categorías: seguridad=3, accesibilidad=1, UI/UX=1, pruebas=2, visual=1
- revisión adversarial: 7 hallazgos (0 altos, 1 medio, 6 bajos)
- riesgos: R-032-1, R-032-2, R-032-3, R-032-4, R-032-5, R-032-6

## 0. Apertura
`git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main` (`71246b2`, la 031) en verde
(`curl …/actions/runs?branch=main`: `completed`/`success`). Bundle medido antes de cambiar nada.

## 1. Pruebas unitarias e integración
`dotnet test`: **705 correctas**: 636 de `tests/Erp.Tests` (sin cambios: la spec no toca el servidor) y 69 de
`tests/Erp.IntegrationTests` (67 + 2 en `ApiSecurityTests`):
- `la_cuenta_bloqueada_responde_igual_que_un_usuario_inexistente`: mismo 401, `title` y `detail`.
- `el_login_responde_429_al_rebasar_el_limite_por_direccion_sin_decir_nada_del_usuario`: instancia hermana con 3 intentos
  por minuto; el 4.º es 429 aunque la contraseña sea correcta, sin el usuario en el cuerpo y sin cookie. Hasta ahora el 429
  del login no tenía prueba (la suite sube el límite).

`npm --prefix web test` (Vitest): **36 correctas** (30 + 6): `loginMessages.test.ts` (ningún estado repite el texto del
servidor; un solo 401 sin cifras; el 423 muerto ya no tiene mensaje propio; 429 y 0/5xx) y `sessionGeneration.test.ts`
(F-02: el 401 de una petición de otra generación no avisa).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa (antes de las correcciones de la revisión) | 372 en verde | 7 min 36 s |
| 2 | `--veces=3` sobre `login`, `h5-h6-seguridad`, `jerarquia` | vuelta 1: `jerarquia › H5 CA1` falla (F-08, preexistente) | 40 s |
| 3 | `--veces=3` sobre `visual.spec.ts -g login` | 3 vueltas en verde (6) | 37 s |
| 4 | `--veces=3` sobre `login` y `h5-h6-seguridad` (tras las correcciones) | 3 vueltas en verde (24) | 1 min 33 s |
| 5 | `--veces=3` sobre `jerarquia` (modificado: selector exacto de la contraseña) | 3 vueltas en verde (22) | 1 min 14 s |
| 6 | Completa final | **374 en verde** | 7 min 22 s |

La segunda corrida completa es la de CI tras el push (se registra abajo).

Archivos nuevos o modificados: `e2e/login.spec.ts` (nuevo, 19 escenarios: H1–H4, CSP y F-01/F-03), `e2e/visual.spec.ts`
(capturas del login), `e2e/helpers.ts` y `e2e/jerarquia.spec.ts` (`getByLabel('Contraseña', { exact: true })`: el botón
«Mostrar contraseña» también contiene «contraseña» y el modo estricto lo rechazaba).

**Capturas de referencia, revisadas una por una:** `login-light.png` y `login-dark.png` (actualizadas: dos paneles,
ilustración completa sin animación, foco en Usuario, selector de tema), `login-error-light.png` y `login-error-dark.png`
(nuevas: aviso genérico de 401 con el detalle del bloqueo, foco en la contraseña vacía; contraste del aviso en los dos
temas), `login-movil.png` (nueva: sin panel de marca, logotipo centrado sobre el formulario, sin desborde). Vitest
(`pantallas.test.ts`) declara las cinco para `/login`.

**Bundle** (`npm --prefix web run build`, gzip): CSS 13,37 → 14,50 kB (+1,13); JS principal 141,06 → 143,31 kB (+2,25);
`icons` y `react` sin cambios (los iconos nuevos ya estaban en el paquete de Lucide). Total +3,4 kB gzip: no notable.

**CSP:** sin cambios (`SecurityMiddleware` no se tocó); `login.spec.ts › la CSP del login no cambia` compara la cabecera
completa y la guardia de la suite no vio violaciones en ninguna prueba.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió: `git diff HEAD` (más los archivos nuevos) y la tabla STRIDE de `plan.md`,
sin sus conclusiones. Revisó: enumeración por mensajes y tiempos, fugas del texto del servidor, el motivo de fin de sesión,
carreras de 401, doble envío, contraseña visible, *open redirect* por `state.from`, CSP en el SVG, regresiones por quitar
`.login-*` y por el selector exacto, y la fuerza de las pruebas. Sin problema: mensajes, *open redirect* (React Router quita
`//`), almacenamiento, CSP, contrato y rutas. Encontró F-01…F-07 (abajo).

## 4. Revisión exploratoria
Servidor de desarrollo (`erp-api`, http://localhost:5080) solo en `/login` y **sin iniciar sesión** (la base `ErpAi` no se
tocó: ningún intento fallido). Navegador integrado en 1280×800 claro y oscuro y 390×844: composición, isotipo, contraste del
panel, pulsos alineados con las relaciones (animaciones pausadas a 1,5 s y 3,8 s), sin desborde ni animaciones en móvil.
Los estados con sesión (expirada, cerrada, bloqueo, 429, 5xx) se recorrieron con la suite E2E sobre la base desechable.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | seguridad | Revisión adversarial: «Cerraste sesión» aparecía aunque el servidor no cerrara la sesión (red caída, 5xx): la cookie seguía viva y en un equipo compartido el siguiente entraba con ella | `logout` devuelve `false` salvo 204 o 401; la barra muestra «No pudimos cerrar la sesión» (`role="alert"`) y la sesión sigue en pantalla | `login.spec.ts › F-01: si el servidor no confirma el cierre de sesión…` (falla con la reversión) |
| F-02 | Baja | seguridad | Un 401 rezagado cambiaba «Cerraste sesión» por «Tu sesión expiró» o echaba a quien acababa de entrar | Generación de sesión en `api/client.ts` (`newSessionGeneration` al entrar y salir); `expired` no pisa `signedOut` | `sessionGeneration.test.ts › un 401 rezagado de una sesión anterior no avisa` (falla con la reversión) |
| F-03 | Baja | seguridad | Tras expirar, otra persona en la misma pestaña llegaba a la ruta y la búsqueda de la anterior | El motivo guarda el usuario; solo él regresa a `from` (también en el `<Navigate>` de la página, que se adelantaba) | `login.spec.ts › F-03: tras una sesión que expiró, otra persona entra al inicio…` (falla con la reversión) |
| F-04 | Baja | seguridad | Hipótesis: la contraseña seguía `type=text` durante el `submit` | `flushSync(() => setShowPassword(false))`. No se reproduce: React 18 ya vacía la actualización del evento discreto antes de que el `submit` llegue a `window` | `login.spec.ts › H3 CA1 y CA4` mira el tipo al terminar el evento (pasa también sin la corrección: guardia) |
| F-05 | Baja | accesibilidad | Con movimiento reducido el indicador del botón giraba | `.login-shell .spin { animation: none }` con `reduce` | `login.spec.ts › H2 CA1` mide también mientras espera (falla con la reversión) |
| F-06 | Baja | UI/UX | «Tu sesión expiró» se monta con la página y el lector no lo anunciaba | El campo Usuario (con el foco) lo referencia en `aria-describedby`, igual que el error | `login.spec.ts › H4 CA4` (falla con la reversión) |
| F-07 | Baja | pruebas | Pruebas débiles: la guarda `if (busy)` y la condición «había sesión» sin prueba propia; H4 CA1 no comprobaba que la cuenta estuviera de verdad bloqueada; reruns sobre la misma base | H4 CA1 comprueba por API el bloqueo (y que otra cuenta sana entra); el resto, riesgos | R-032-1, R-032-3 |
| F-08 | Media | pruebas | Preexistente (no de esta spec): `jerarquia.spec.ts › H5 CA1` falla si corre solo después de `h5-h6-seguridad.spec.ts`; la bitácora de un gerente de unidad incluye un evento de un caso de otra unidad | Fuera de alcance (alcance congelado; no bloquea el login) | R-032-6; falla igual con `71246b2` (`e2e:regresion`) |

Además, durante la implementación (no de la revisión): el minificador de CSS convirtió `animation: 5.6s linear infinite`
(sin nombre) en `animation: none` y los pulsos desaparecían en el build; lo detectó `H2 CA2` (lista exacta de animaciones).
Propiedades largas; R-032-5 y una lección en CLAUDE.md.

### Contra el código anterior
- `npm --prefix web run e2e:regresion -- 71246b2 e2e/login.spec.ts`: **14 de 16 fallan** (en ese momento el archivo tenía
  16 escenarios): sin panel ni ilustración, sin «Mostrar contraseña», sin Bloq Mayús, sin errores por campo ni foco en la
  contraseña tras el rechazo, el 0/5xx con el texto de desarrollador, sin aviso de sesión. Pasan «preparar» y «H2 CA3»
  (el formulario anterior también se podía usar de inmediato: guardia).
- Correcciones de la revisión: sin un commit intermedio, se revirtió cada corrección en el árbol (respaldo fuera del
  repositorio) y se corrió `login.spec.ts` y Vitest: fallan H2 CA1 (F-05), H4 CA4 (F-06), F-03, F-01 y la de generación
  (F-02); pasa H3 CA1 (F-04, no reproducible). Restaurado, todo en verde.

### Lista de comprobación
- [x] Teclado completo: Tab por tema → usuario → contraseña → mostrar → entrar; Enter envía; el foco nunca cae en `body`
      (guardia de la 030 en cada prueba).
- [x] `aria-live`: errores en `role="alert"`, avisos de sesión y Bloq Mayús en `role="status"`, ligados a su campo.
- [x] axe sin violaciones serias en claro, oscuro, 390 y 768 px.
- [x] Etiquetas reales y `autocomplete` (`username`, `current-password`); sin texto en la ilustración (`aria-hidden`).
- [x] Movimiento reducido: ninguna animación ni transición; sin él, solo CSS sobre `transform`/`opacity`.
- [x] Sin desbordes (guardia) en 1280, 768 y 390 px.
- [x] Sin cambios de contrato, rutas (`RouteInventoryTests`) ni CSP.
- [x] Capturas nuevas o cambiadas revisadas una por una.

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#032): R-032-1 (guardas sin prueba propia), R-032-2 (tiempos del login,
preexistente), R-032-3 (cuenta de prueba que se bloquea en reruns), R-032-4 (movimiento reducido solo en el login),
R-032-5 (minificador y `animation` sin nombre), R-032-6 (bitácora de jerarquía, preexistente, medio).

## 6. CI
Push a `main` y corrida de CI: ver abajo.
