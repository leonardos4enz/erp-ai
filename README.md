# ERP AI

ERP **dirigido por metadatos**: los catálogos, campos y reglas se definen como datos, no como código.
El motor genera las tablas en SQL Server, la API y las pantallas. Un asistente de IA (Claude) propone
cambios en lenguaje natural ("haz un catálogo de empaques") usando la misma vía segura que la interfaz:
**Planes de Cambios** validados, con vista previa y confirmación humana.

## Requisitos
- .NET 10 SDK
- Node.js 20+
- SQL Server (en desarrollo se usa LocalDB `(localdb)\MSSQLLocalDB`)

## Arranque en desarrollo
```bash
npm --prefix web install
```
```bash
npm --prefix web run build
```
```bash
dotnet run --project src/Erp.Api --launch-profile http
```
Abre http://localhost:5080. En el primer arranque se crea la base `ErpAi` y el usuario `admin`;
su contraseña aleatoria **aparece una sola vez en la consola**. Para fijarla tú:
```bash
dotnet user-secrets --project src/Erp.Api set "Bootstrap:AdminPassword" "una-frase-larga-y-segura"
```
Para activar el *Asistente IA* (opcional; sin clave, el resto funciona igual), entra como administrador a
*Configuración › Asistente IA* y pega tu clave de Anthropic (se prueba y se guarda cifrada).
Para desarrollo del frontend con recarga en caliente, además ejecuta `npm --prefix web run dev`
y abre http://localhost:5173 (proxy a la API).

## Módulos
Un módulo es un paquete de **metadatos** en [`modules/`](modules/): catálogos, documentos con partidas,
fórmulas, reglas de negocio, procesos de aprobación, roles y datos iniciales. No contiene código.
Se instala desde *Configuración › Módulos* (solo administradores), con vista previa y en una sola transacción.

| Módulo | Contenido |
|---|---|
| [Compras](docs/sdd/specs/003-modulo-compras/spec.md) | Requisiciones, órdenes de compra con autorización por montos y control interno, recepciones; proveedores, productos y 6 catálogos base |

## Pruebas
Unitarias (núcleo de seguridad):
```bash
dotnet test
```
E2E con Playwright: API, criterios de aceptación, accesibilidad y regresión visual. Usa una base desechable `ErpAi_E2E`:
```bash
npm --prefix web run e2e
```
Ver fallas con video y trace paso a paso:
```bash
npm --prefix web run e2e:report
```
Cómo se cierra una spec (incluye la revisión exploratoria con agente): [docs/sdd/verificacion.md](docs/sdd/verificacion.md).

## Estructura
| Ruta | Contenido |
|---|---|
| `docs/sdd/` | Metodología SDD: constitución, specs, planes, tareas |
| `docs/adr/` | Decisiones de arquitectura |
| `src/Erp.Api/Metadata` | Modelo, validación de identificadores, generador DDL, Planes de Cambios |
| `src/Erp.Api/Data` | Motor CRUD genérico |
| `src/Erp.Api/Security` | Contraseñas, permisos, auditoría |
| `src/Erp.Api/Assistant` | Asistente IA: contexto (solo metadatos), proveedor, interpretación de propuestas |
| `src/Erp.Api/Endpoints` | API REST |
| `web/` | SPA React + TypeScript (Lucide, modo claro/oscuro) |

## Seguridad (resumen)
- Identificadores SQL solo desde metadatos con lista blanca; valores siempre parametrizados.
- Cookie `HttpOnly`/`Secure`/`SameSite=Strict`, CSRF por cabecera, CSP estricta, rate limiting.
- PBKDF2, bloqueo tras 5 intentos, revocación inmediata de sesiones.
- RBAC por catálogo; auditoría inmutable a nivel de base de datos.
- `NuGetAudit` rompe el build ante paquetes vulnerables.

Ver [constitución](docs/sdd/constitution.md) y [análisis STRIDE](docs/sdd/specs/001-nucleo-metadatos/plan.md).

## Producción (pendiente de configurar)
- Cadena de conexión por variable de entorno `ConnectionStrings__Erp`, con un login de mínimo privilegio (ver ADR-0001).
- `AllowedHosts`, HTTPS/HSTS y persistencia de llaves de Data Protection (ver ADR-0002).
- La API key de Anthropic se captura en la app (cifrada con Data Protection: persistir y proteger sus llaves) o va en la variable de entorno `Assistant__AnthropicApiKey`; nunca en el repositorio ([ADR-0003](docs/adr/0003-asistente-ia-anthropic.md)).
