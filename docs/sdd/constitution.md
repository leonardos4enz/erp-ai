# Constitución del proyecto — ERP AI

> Principios no negociables. Toda spec, plan, tarea o cambio de código (humano o IA)
> debe cumplirlos. Un cambio que viole un principio se rechaza aunque "funcione".

## I. Dirigido por metadatos
1. Ningún catálogo, campo, regla o proceso de negocio se programa a mano.
   Se declara como metadato y el motor lo interpreta.
2. El código solo implementa **capacidades genéricas** del motor (tipos de campo,
   tipos de regla, tipos de paso de proceso). Agregar una capacidad nueva requiere spec.
3. Todo cambio al modelo pasa por un **Plan de Cambios** (JSON) que se valida,
   se previsualiza y luego se aplica en una transacción. Humanos, UI e IA usan la misma vía.

## II. Seguridad por diseño (OWASP ASVS nivel 2 como referencia)
1. **Nunca SQL concatenado con datos.** Los valores siempre van como parámetros.
   Los identificadores (tablas/columnas) solo provienen de metadatos validados con
   lista blanca (`^[A-Za-z][A-Za-z0-9_]{1,62}$`), nunca de la petición, y se citan con `[...]`.
2. **Mínimo privilegio.** Denegar por defecto. Cada endpoint declara el permiso requerido.
   Permisos por rol a nivel entidad (leer/crear/editar/eliminar) y campo.
3. **Autenticación robusta.** Contraseñas con PBKDF2 (Identity PasswordHasher), bloqueo por
   intentos fallidos, rate limiting en login, cookies `HttpOnly` + `Secure` + `SameSite=Strict`,
   protección CSRF por cabecera obligatoria. Sin tokens en `localStorage`.
4. **Auditoría inmutable.** Toda escritura (datos y modelo) queda registrada: quién, cuándo,
   qué, antes/después. La auditoría no se edita desde la aplicación.
5. **Cabeceras seguras.** CSP estricta, `X-Content-Type-Options`, `Referrer-Policy`,
   `frame-ancestors 'none'`, HSTS en producción.
6. **Errores sin fugas.** Al cliente: ProblemDetails genérico con `traceId`. Detalle solo en logs.
7. **Secretos fuera del código.** Cadenas de conexión y API keys (p. ej. MiniMax) vía
   variables de entorno / user-secrets / vault. Nunca en el repositorio.
8. **IA sin acceso directo.** El LLM nunca ejecuta SQL ni llama endpoints de datos con
   privilegios propios. Solo propone Planes de Cambios que un humano autorizado confirma.
   El contenido generado por el LLM se trata como entrada no confiable.
9. **No destructivo por defecto.** Los campos y entidades se archivan, no se borran.

## III. Calidad
1. El código de seguridad crítica (validación de identificadores, generador de DDL,
   autorización) tiene pruebas unitarias **antes** de darse por terminado.
2. Concurrencia optimista (`rowversion`) en toda entidad dinámica.
3. Cada decisión de arquitectura relevante se registra como ADR en `docs/adr/`.
4. **Ninguna spec se cierra sin la fase VERIFY completa** ([verificacion.md](verificacion.md)):
   unitarias y E2E en verde, accesibilidad AA automática sin violaciones serias, y una
   revisión exploratoria con reporte `verify.md`.
5. **Cada bug encontrado se convierte en prueba** antes de marcarse como resuelto.
6. Las pruebas nunca usan la base de datos de desarrollo ni secretos guardados en el repositorio.

## IV. Experiencia de usuario
1. Interfaz moderna y minimalista; tokens de diseño centralizados; modo claro/oscuro.
2. Iconografía con **Lucide** (SVG). Prohibido usar emojis como iconos.
3. Accesible: contraste AA, navegación con teclado, `aria-*` en controles interactivos.
4. Toda acción destructiva o de modelo pide confirmación con vista previa.

## Gobernanza
Cambiar esta constitución requiere un ADR que explique el motivo.
