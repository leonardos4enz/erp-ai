# Verificación 001 — Núcleo de metadatos

**Fecha:** 2026-10-23 · **Revisor:** Claude (navegador integrado + Playwright) · **Resultado:** Aprobada

## Compuertas
| Compuerta | Resultado |
|---|---|
| Unitarias (`dotnet test`) | 34/34 |
| E2E (`npm run e2e`) | 40/40, dos corridas limpias consecutivas |
| Revisión exploratoria | 9 hallazgos, todos resueltos |

## Cobertura de criterios de aceptación
| Historia | Pruebas |
|---|---|
| H1 Crear catálogo (CA1–CA3) | `h1-modelador.spec.ts` |
| H2 Agregar campos | `h1-modelador.spec.ts › H2` |
| H3 Reglas por rol | `h3-h4-datos.spec.ts › H3`, `api-security.spec.ts` |
| H4 Capturar datos (incluye conflicto y borrado lógico) | `h3-h4-datos.spec.ts` |
| H5 Seguridad y permisos (incluye bloqueo 5 intentos) | `h5-h6-seguridad.spec.ts`, `api-security.spec.ts` |
| H6 Auditoría | `h5-h6-seguridad.spec.ts › H6`, `api-security.spec.ts` |

## Hallazgos
| ID | Sev. | Hallazgo | Corrección | Prueba de regresión |
|---|---|---|---|---|
| F-01 | Alta | 4 dependencias npm vulnerables (Vite/esbuild, React Router) y 1 de NuGet | Actualizadas; `NuGetAudit` rompe el build | `npm audit` / build |
| F-02 | Media | Los campos fecha-hora perdían la zona horaria (el cliente envía UTC y el servidor guardaba la hora local) | Se guarda y devuelve en UTC | — (pendiente: prueba con zona horaria distinta) |
| F-03 | Media | El frontend aceptaba identificadores de 63 caracteres y con `__`, y el backend los rechazaba | `IDENTIFIER_RE` alineado con `Identifiers.cs` | `h1 CA3` |
| F-04 | Media | Contraste insuficiente en encabezados de tabla (`--text-subtle`, 4.4:1) | Token oscurecido a 4.5:1 o más | axe en `h3-h4 › H4` |
| F-05 | Media | Contraste insuficiente en la etiqueta "Bloqueado" | Usa `--danger-text` | axe en `h5 › crear usuario` |
| F-06 | Media | **Móvil: el menú lateral abría encima del formulario y lo tapaba.** Ninguna aserción lo detectaba; apareció al revisar la captura de referencia | En pantalla angosta arranca cerrado; se cierra al navegar, al tocar fuera o con Esc | `visual › móvil: el menú arranca cerrado…` |
| F-07 | Baja | El plural sugerido de "Almacén" era "Almacénes" | `pluralize()` con reglas del español (acentos, -z, invariables) | `h1 › crear catálogo` |
| F-08 | Media | El SQL de la vista previa tenía scroll, pero no se podía recorrer con teclado | `tabIndex` + `aria-label` | axe en `h1 › crear catálogo` |
| F-09 | Baja | El login respondía 423 a cuentas bloqueadas, lo que permitía saber qué usuarios existen | Respuesta 401 genérica | `api-security › el login fallido no revela…` |

## Pendientes conocidos (no bloquean)
- F-02 sin prueba automática de zona horaria.
- `icons.js` pesa 423 kB (112 kB gzip) porque incluye todo Lucide para que la IA elija cualquier icono.
  Revisar en la spec 002 si conviene una lista cerrada.
