# Plan 032 — Diseño técnico

## 1. Modelo de datos
Sin cambios. Ninguna tabla, columna ni operación del Plan de Cambios.

## 2. Contrato de la API
Sin cambios. La pantalla usa lo que ya existe (001) y la prueba de integración nueva fija su comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/auth/login` | `Anonymous` · `Writes(Session)` · política `login` (por dirección) | `{ username, password }` + `X-ERP-Request` | 200 `Me` + cookie `erp.auth` (`HttpOnly`, `Secure`, `SameSite=Strict`) · 401 con un único `title` genérico (inexistente, inactivo, bloqueado o contraseña incorrecta) · 429 sin cuerpo (límite por dirección) · 403 sin la cabecera anti-CSRF |
| `POST /api/auth/logout` | `Authenticated` | — | 204 |
| `GET /api/auth/me` | `Authenticated` | — | 200 `Me` · 401 |

`docs/sdd/rutas.md` no cambia (`RouteInventoryTests` lo comprueba). La CSP de `SecurityMiddleware` no cambia
(`default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; …`).

## 3. SPA
- **`components/Brand.tsx`** (nuevo): `BrandMark` (isotipo SVG: mosaico con degradado, tres filas y un nodo; `aria-hidden`)
  y `BrandIllustration` (diagrama de catálogos, `aria-hidden`, `focusable="false"`, sin `<text>`). Los degradados usan `id`
  únicos con `useId` (dos isotipos en la página no comparten `url(#…)`).
- **`pages/LoginPage.tsx`** (reescrito):
  - `login-shell` en rejilla de dos columnas ≥ 1024 px: `<aside class="login-aside">` (marca, frase, ilustración) y
    `<main class="login-main">` (selector de tema, logotipo para pantallas angostas, título `h1` «Inicia sesión», formulario,
    pie con la nota de recuperación). Bajo 1024 px el `aside` tiene `display: none`.
  - Estados: `notice` (sesión expirada o cerrada, `Alert tone="info"` → `role="status"`), `error` (`Alert tone="danger"`
    → `role="alert"`, con `id` para `aria-describedby` de la contraseña), errores de campo (vacíos, `Field` con
    `role="alert"`), `capsLock`, `showPassword`, `busy`.
  - `messageFor(err)`: 401 → genérico de credenciales/bloqueo; 429 → «Demasiados intentos»; 0 o ≥ 500 → «No pudimos
    conectar»; cualquier otro → «No se pudo iniciar sesión». **Nunca** el `title`/`detail` del servidor.
  - Tras un rechazo: contraseña vacía, oculta y con el foco (`ref`). Al enviar: `showPassword=false` con `flushSync`.
  - Destino tras entrar: `from` solo si quien entra es quien tenía la sesión que terminó (F-03), también en el `<Navigate>`.
  - Bloq Mayús: `getModifierState('CapsLock')` en `keydown`/`keyup` de la contraseña; se oculta al salir del campo. El
    aviso es `role="status"` con `id` en el `aria-describedby` del campo.
- **`auth/AuthContext.tsx`**: `sessionEnd: { reason: 'expired' | 'signedOut', username } | null`. El manejador de 401 lo
  pone en `expired` solo si había sesión (ref al `me` actual) y no pisa `signedOut`; `logout` lo pone en `signedOut` solo
  con 204 o 401 del servidor (si no, devuelve `false` y la barra muestra un aviso, F-01); `login` lo limpia. Nada en
  almacenamiento. **`api/client.ts`**: generación de sesión (`newSessionGeneration` en cada login y logout); el 401 de una
  petición enviada en otra generación no llama al manejador (F-02).
- **`styles/login.css`** (nuevo, importado en `main.tsx`) y tokens `--brand-*` en `tokens.css` (claro y oscuro). Animaciones:
  `@keyframes` con solo `transform`/`opacity`; `@media (prefers-reduced-motion: reduce)` → `animation: none` en todo lo del
  login. Las animaciones ambientales usan `animation-delay` negativo (empiezan «a medias», sin esperar) y ningún elemento
  arranca invisible: la composición sin animación está completa.
- Se quitan `.login-page`, `.login-card` y `.login-brand` de `app.css`.

## 4. Pruebas
- **Integración** (`ApiSecurityTests`): `el_login_responde_429_al_rebasar_el_limite_por_direccion_sin_decir_nada_del_usuario`
  (instancia hermana con 3 intentos por minuto: el 4.º es 429 aunque la contraseña sea correcta y el cuerpo no trae el
  usuario); `la_cuenta_bloqueada_responde_igual_que_un_usuario_inexistente` (mismo estado y mismo `title`).
- **E2E nuevo `e2e/login.spec.ts`** (anónimo con el *fixture* `page`, así corren las guardias de la 030):
  - H1 CA1/CA3: escritorio en claro y oscuro (panel, logotipo, ilustración `aria-hidden` sin texto; axe).
  - H1 CA2: 390 y 768 px sin ilustración, logotipo visible, sin desborde.
  - H2 CA1: `reducedMotion: 'reduce'` → `getAnimations()` vacío. H2 CA2: sin preferencia hay animaciones y todas animan
    solo `transform`/`opacity`. H2 CA3: con animaciones en curso, el usuario tiene el foco y se escribe.
  - H3 CA1–CA4: mostrar/ocultar (`aria-pressed`, tipo), se oculta al enviar; Bloq Mayús; autocompletado; vacíos sin petición;
    «Entrando…» con el foco (respuesta retenida con `page.route`).
  - H4 CA1: inexistente, incorrecta y **bloqueada** (usuario propio con 5 intentos fallidos por API) dan el mismo aviso y el
    foco en la contraseña. H4 CA2: 429 (respuesta simulada con `page.route`; el 429 real está en integración). H4 CA3: sin
    respuesta (`route.abort`) y 503, sin el texto del servidor. H4 CA4: cookies borradas con la sesión abierta → «Tu sesión
    expiró» y regresa a la ruta; cerrar sesión → «Cerraste sesión».
  - CSP: la cabecera de `/login` es la de siempre (sin `unsafe-inline`); las violaciones las detecta la guardia.
- **Visual** (`visual.spec.ts`): `login-light`/`login-dark` actualizadas; nuevas `login-movil`, `login-error-light`,
  `login-error-dark`. Revisión una por una.
- Archivos para `--veces=3`: `e2e/login.spec.ts`, `e2e/visual.spec.ts`, `e2e/h5-h6-seguridad.spec.ts` (usa `uiLogin`).
- **Contra el código anterior** (`e2e:regresion -- 71246b2 e2e/login.spec.ts`): deben fallar los escenarios nuevos (sin
  «Mostrar contraseña», sin aviso de sesión, mensaje de 0/5xx, foco).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? | Ninguna ruta ni operación. Cambia solo la pantalla `/login` de la SPA, que sigue llamando a `POST /api/auth/login` (`Anonymous`, política `login`). | `RouteInventoryTests` (inventario sin cambios) · `ApiSecurityTests › rechaza_peticiones_que_modifican_estado_sin_la_cabecera_anti_CSRF` |
| Caminos alternos | ¿Por qué otros caminos se llega a iniciar sesión o a saber si una cuenta existe? | La API directa (mismo 401 genérico), OAuth (`/oauth/token`, 018, sin cambios), la pantalla (no distingue). El aviso de «sesión expirada» sale solo de un 401 con sesión abierta en esta pestaña y de su misma generación: no dice nada de otra cuenta, y la ruta de quien expiró no la hereda otra persona. | `ApiSecurityTests › el_login_fallido_no_revela_si_el_usuario_existe` · `ApiSecurityTests › la_cuenta_bloqueada_responde_igual_que_un_usuario_inexistente` · `login.spec.ts › H4 CA1` · `sessionGeneration.test.ts › un 401 rezagado de una sesión anterior no avisa` · `login.spec.ts › F-03: tras una sesión que expiró, otra persona entra al inicio…` |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | No lee datos: antes de la sesión no hay catálogos. El logotipo de empresa no se muestra (CLARIFY 2) para no revelar qué empresas hay. | no aplica (sin ruta nueva: `RouteInventoryTests`) |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo? | No aplica: la pantalla no otorga nada; la sesión se crea como en la 001. | no aplica |
| Concurrencia | ¿Invariante entre filas? ¿10 envíos a la vez? | El botón en espera ignora otro envío (no se mandan dos POST por doble clic o doble Enter). Los intentos fallidos simultáneos ya los cuenta el servidor (001). | `login.spec.ts › H3 CA4` |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | Sin respuesta o 5xx al entrar: el formulario queda usable con un aviso y sin sesión a medias (la cookie solo llega con el 200). Al **cerrar** sesión sin respuesta: la cookie puede seguir viva, así que no se dice «Cerraste sesión» y la sesión sigue abierta en la pantalla (F-01). | `login.spec.ts › H4 CA3` · `login.spec.ts › F-01: si el servidor no confirma el cierre de sesión…` |
| Límites | Tamaño, tasa por usuario o IP | Mismo `maxLength` (100/256), mismo límite por dirección y bloqueo por cuenta. El 429 se comunica sin cifras. | `ApiSecurityTests › el_login_responde_429_al_rebasar_el_limite_por_direccion_sin_decir_nada_del_usuario` · `login.spec.ts › H4 CA2` |
| Semántica | Nulos y vacíos | Usuario con espacios se recorta (como antes); vacíos no se envían; la contraseña no se recorta. | `login.spec.ts › H3 CA3` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** (suplantación de la pantalla) | Copiar el login es más atractivo si se ve «real» (phishing) | Igual que antes: `frame-ancestors 'none'`, la cookie `SameSite=Strict`; la marca no es un secreto | `ApiSecurityTests › la_cookie_de_sesion_es_HttpOnly_Secure_y_SameSite_Strict_con_cabeceras_de_endurecimiento` |
| **T** (CSP relajada para la animación) | Un `style` en línea o una fuente externa obligaría a abrir la CSP | Animación en la hoja de estilos; SVG en React; sin fuentes ni CDN; la guardia falla con cualquier violación de CSP | `login.spec.ts › la CSP del login no cambia` |
| **I** (enumeración de cuentas) | Un mensaje distinto para «bloqueada» o «no existe» | Un solo aviso para 401; se quita el caso 423; nunca se muestra el texto del servidor | `login.spec.ts › H4 CA1` · `login.spec.ts › H4 CA3` |
| **I** (credenciales en el navegador) | Guardar el usuario o el motivo en `localStorage`; la contraseña visible al enviar | Nada en almacenamiento (solo el tema, como antes); la contraseña vuelve a `type=password` al enviar | `login.spec.ts › H3 CA1` · `login.spec.ts › H4 CA4` |
| **D** (CPU) | Animación costosa en equipos modestos | Solo `transform`/`opacity` en CSS (compositor), sin JS por cuadro; apagada con movimiento reducido (también transiciones y el indicador de espera, F-05) y oculta en pantallas angostas | `login.spec.ts › H2 CA1` · `login.spec.ts › H2 CA2` |
| **E** | — | No aplica: no hay privilegio en juego antes de la sesión | no aplica |
