# Spec 032 — Rediseño del inicio de sesión

**Estado:** Implementado · **Depende de:** 001 (autenticación), 029 (rutas declaradas), 030 (guardias de la suite) ·
**Origen:** pedido del dueño (2026-10-25) antes de empezar Ventas: el login «parece prototipo»; quiere algo a la altura del
producto, con animaciones SVG si conviene. Ventas pasa a la 033 y CFDI 4.0 a la 034.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `web/src/pages/LoginPage.tsx` y `.login-*` (`app.css`): una tarjeta de 380 px centrada sobre un degradado radial, el glifo de tres líneas de la barra lateral y dos campos | No dice qué es el producto ni transmite su idea (un ERP declarado como datos, con IA); en 1280 px el 85 % de la pantalla está vacío |
| 2 | `messageFor` tiene un caso `423` («Cuenta bloqueada temporalmente») | El servidor nunca responde 423: el bloqueo devuelve el mismo 401 genérico (001 F-09, `AuthEndpoints.Login`). Es código muerto que, si alguien lo «arreglara» en el servidor, revelaría qué cuentas existen |
| 3 | `case 0`: «Verifica que la API esté en ejecución» | Un mensaje para desarrolladores; un 5xx muestra el `title` del servidor tal cual |
| 4 | `RequireAuth` y el cierre de sesión (`Layout.tsx`) llevan a `/login` sin motivo | Tras una sesión expirada (401 en cualquier petición) la persona ve el formulario vacío sin saber por qué la sacaron; tras cerrar sesión, tampoco hay confirmación |
| 5 | La contraseña no se puede mostrar; no hay aviso de Bloq Mayús; el error de campos vacíos es un aviso general | Errores de tecleo que terminan en intentos fallidos y, tras 5, en bloqueo de 15 min |
| 6 | Tras un 401 el foco queda en el botón y la contraseña se borra | Quien usa el teclado tiene que regresar con Shift+Tab al campo que debe reescribir |

## Principio
Sin capacidad nueva del motor ni cambio de contrato: **el mismo `POST /api/auth/login`, la misma cookie, ninguna ruta
anónima nueva** y la misma CSP. Es una pantalla de la SPA: composición, marca, ilustración decorativa y estados. Los
mensajes siguen sin distinguir «no existe», «contraseña incorrecta», «inactiva» y «bloqueada» (001 F-09): la pantalla no
puede distinguirlos y no lo intenta.

## Referencias (DISCOVER)
Cómo resuelven el inicio de sesión los productos con los que se compara el proyecto y SaaS modernos (revisión de sus
pantallas públicas y de guías de diseño, octubre de 2026):

| Producto | Composición | Qué se toma |
|---|---|---|
| Salesforce, NetSuite, Zoho, SAP (Fiori) | **Dos paneles**: formulario a un lado, marca o mensaje del producto al otro | El panel de marca explica qué es el producto sin competir con el formulario |
| Odoo, Microsoft (Dynamics / Entra) | Tarjeta centrada, sobria; Microsoft divide en pasos | Un solo propósito por pantalla; nada que distraiga |
| Linear, Vercel, Stripe | Minimalista, mucho espacio, un acento de color, fondo con un patrón o degradado sutil; Stripe anima su degradado | Animación ambiental lenta, que no pide atención |
| Guías (UXPin, Eleken, guías de sistemas de diseño, hilo WAI-IG sobre WCAG 2.2 3.3.8) | — | Botón para mostrar la contraseña, aviso propio de Bloq Mayús (el del navegador es inconsistente), pegar y gestores de contraseñas permitidos, errores en línea, recuperar la contraseña a la vista |

## Dirección visual
- **Composición: dos paneles** (≥ 1024 px): a la izquierda (≈ 55 %) el **panel de marca** sobre índigo profundo con el
  logotipo, una frase de valor y la ilustración; a la derecha el formulario sobre `--surface`, con el ancho de lectura de
  siempre (380 px). Es el patrón de los ERP de referencia (Salesforce, NetSuite, Zoho, SAP) y aprovecha la pantalla para
  decir qué es el producto sin poner nada entre la persona y el formulario. En tableta vertical y móvil (< 1024 px) el
  panel de marca se oculta y el logotipo queda sobre el formulario; el fondo conserva un degradado sutil.
- **Marca:** isotipo propio en SVG (`BrandMark`): las **tres filas** del glifo actual de la barra lateral (filas de metadatos)
  con un **nodo** al final de la fila corta (la relación, la IA que completa el modelo), sobre un mosaico índigo con degradado.
  El logotipo es el isotipo más «ERP AI» como **texto** (no imagen). Sin emojis ni imágenes de terceros.
- **Tipografía y tokens:** la pila de `--font-sans` (sin fuentes externas, CSP), tamaños `--text-*`, radios, sombras y
  `--ease`/`--dur` de `tokens.css`. Tokens nuevos solo para el panel de marca (`--brand-*`), definidos en claro y oscuro.
- **Ilustración (decorativa, `aria-hidden`):** un diagrama de catálogos — tarjetas con encabezado y filas de campos (sin
  texto) unidas por relaciones ortogonales a un nodo central. Animación ambiental lenta y solo con `transform`/`opacity`
  en CSS: las tarjetas flotan unos píxeles, pulsos recorren las relaciones hacia el nodo, el nodo «respira» y las filas de
  un catálogo nuevo aparecen una a una (el motor generando a partir de metadatos). El estado sin animación es la
  composición completa: nada depende de que la animación termine.
- **Microinteracciones:** anillo de foco de los tokens; botón **Mostrar/Ocultar contraseña** (ojo, `aria-pressed`); aviso de
  **Bloq Mayús**; el botón muestra carga («Entrando…») sin perder el foco; el aviso de error entra con un desvanecido corto;
  tras un rechazo el foco vuelve a la contraseña; al enviar, la contraseña vuelve a ocultarse (el gestor de contraseñas la
  ve como contraseña).
- **Selector de tema** (claro/oscuro) en la esquina, como en el resto de la aplicación.

## Historias

**H1 — Un inicio de sesión a la altura del producto.** Como *persona que entra al ERP*, quiero una pantalla que diga qué es
el producto y se vea terminada, para confiar en él desde el primer momento.
- CA1: en 1280 px se ven el panel de marca (logotipo, frase y la ilustración) y el formulario; en claro y en oscuro, con
  capturas de referencia revisadas.
- CA2: en 390 px y en 768 px la ilustración no se muestra, el logotipo está sobre el formulario y no hay desplazamiento
  horizontal (guardia de desborde).
- CA3: axe sin violaciones serias en claro y oscuro; la ilustración es `aria-hidden` y no tiene texto.

**H2 — Animación responsable.** Como *persona sensible al movimiento o con un equipo modesto*, quiero que la animación no
me estorbe.
- CA1: con `prefers-reduced-motion: reduce` la página no tiene ninguna animación (`document.getAnimations()` vacío).
- CA2: sin esa preferencia, todas las animaciones son CSS y solo animan `transform` u `opacity` (sin bucles de JavaScript).
- CA3: el formulario se puede usar en cuanto aparece, con la animación en curso (el usuario tiene el foco y se escribe).

**H3 — Formulario cuidado.** Como *persona que escribe su contraseña*, quiero ver lo que escribo si lo necesito y saber que
tengo Bloq Mayús, para no gastar intentos.
- CA1: «Mostrar contraseña» cambia el campo a texto y su estado (`aria-pressed`); al enviar, la contraseña vuelve a ocultarse.
- CA2: con Bloq Mayús activo en la contraseña aparece «Bloq Mayús está activado», anunciado y ligado al campo.
- CA3: los campos tienen etiqueta real y `autocomplete="username"` / `current-password`; campos vacíos dan un error en el
  campo (sin petición) y el foco va al primero vacío.
- CA4: mientras espera, el botón dice «Entrando…», no admite otro envío y conserva el foco.

**H4 — Estados con mensajes que no revelan nada.** Como *persona que no logra entrar*, quiero saber qué hacer, sin que la
pantalla le diga a un atacante qué cuentas existen.
- CA1: usuario inexistente, contraseña incorrecta y **cuenta bloqueada por intentos** dan el mismo aviso (`role="alert"`),
  que menciona ambas causas; el foco vuelve a la contraseña (guardias de alerta y foco de la 030).
- CA2: **429** (límite de intentos por dirección): «Demasiados intentos» y esperar un momento, sin cifras ni usuario.
- CA3: **servidor no disponible** (sin respuesta o 5xx): un aviso para la persona, sin el texto del servidor.
- CA4: **sesión expirada** (una petición responde 401 con la sesión abierta): el login dice «Tu sesión expiró» y, al
  entrar **la misma persona**, regresa a donde estaba (otra empieza en el inicio: revisión adversarial F-03). **Sesión
  cerrada** por la persona: «Cerraste sesión», solo si el servidor confirmó el cierre (F-01); si no, un aviso de que no se
  pudo y la sesión sigue abierta en la pantalla. Un 401 rezagado de una sesión anterior no cambia el aviso ni echa a quien
  acaba de entrar (F-02). Ninguno se guarda en el navegador.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Más de 6 historias (no: 4) · más de ~2 500 líneas de producto (no: ~600) · seguridad o
   concurrencia junto con UI nueva (no: sin cambio de contrato) · más de una pantalla principal (no: una). → No se divide.
2. **¿Logo de la empresa antes de iniciar sesión?** No. Las imágenes solo las sirve la ruta de modeladores (029); mostrarla
   sin sesión pide una ruta anónima nueva (lista blanca, `rutas.md`) y revela qué empresas hay en la instancia. Se usa la
   marca de ERP AI (opción por defecto). Si se pide, va en su propia spec.
3. **¿Distinguir «bloqueada»?** No, y no se puede: el servidor da el mismo 401 (001 F-09). El aviso genérico menciona las dos
   causas y qué hacer («espera unos minutos o pide ayuda a un administrador»), sin el número de intentos ni la duración. Se
   quita el caso 423 muerto.
4. **429:** mensaje genérico, sin cuenta regresiva (el limitador no envía `Retry-After` y el número sería una pista del
   límite). Se agrega una prueba de integración del 429 real del login (hoy solo la había para otras políticas).
5. **Sesión expirada o cerrada:** el motivo vive en memoria (`AuthContext`), no en `localStorage` ni en la URL; recargar
   la página lo olvida (aceptable: es solo un aviso).
6. **«¿Olvidaste tu contraseña?»** No hay flujo de recuperación (sería una ruta anónima y correo): una línea de texto que
   dice que la restablece un administrador. Sin enlace.
7. **Barra lateral:** conserva su glifo de tres líneas (es la variante pequeña del isotipo). Cambiarlo movería las 19 capturas
   de referencia de otras pantallas sin relación con el login → roadmap.
8. **Animación con CSS, no SMIL:** `prefers-reduced-motion` la apaga en una sola regla y `getAnimations()` la ve (prueba).
   La CSP no cambia (`style-src 'self'`: todo va en la hoja de estilos; React fija los estilos por CSSOM).

## Fuera de alcance (anotado en el roadmap)
- Isotipo nuevo en la barra lateral y la pestaña del navegador (favicon) → con la siguiente spec que toque capturas del menú.
- Recuperación de contraseña por correo, SSO o segundo factor (cambian el contrato y agregan rutas anónimas).
- Logo de la empresa en el login (decisión 2).
