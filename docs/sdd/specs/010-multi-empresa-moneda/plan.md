# Plan 010 — Diseño técnico

## Piezas
```
 meta.Company ── meta.UserCompany ── meta.[User].CompanyId (empresa actual)
      │                                   │
      │  UserContext.Company / Companies ◀┘  (se carga en cada petición, como roles y unidad)
      ▼
 RecordFilter.For(e, user)  =  [empresa: t.CompanyId = @co]  AND  [permisos por registro 006]
      │                          (catálogo por empresa,              (el superusuario se los salta;
      │                           también para el superusuario)       la empresa, no)
      ▼
 RecordService · ReportService · buscadores · referencias · importación (todos ya usan RecordFilter)

 ExprEnv += Company, BaseCurrency, Rates (RelatedLoader → meta.ExchangeRate)
 BASECURRENCY() · CURRENTCOMPANY() · RATE(de, a[, fecha]) · CONVERT(importe, de, a[, fecha])
```
Nada es por módulo: `perCompany` es una propiedad genérica del catálogo y las funciones son del lenguaje.

## Metadato: catálogo por empresa
```ts
EntityDef       += { perCompany: boolean }                 // partidas: false (siguen a su documento)
CreateEntityOp  += { perCompany?: boolean }
UpdateEntityOp  += { perCompany?: boolean }                // solo true: volver a compartido se rechaza
```
- `meta.Entity.PerCompany bit NOT NULL DEFAULT 0` (migración idempotente).
- **Compilador:** una partida no admite `perCompany` («sigue a su documento»); `perCompany: false` sobre un catálogo
  por empresa → error «no se puede volver compartido». Una **fórmula** guardada de un catálogo compartido (o partida de
  uno) que use `BASECURRENCY()`/`CURRENTCOMPANY()` → error en la pasada final (`ExprAnalyzer.Uses`).
- **DDL** (identificadores de metadatos, nombres de constraint con `Identifiers.Constraint`):
  - `createEntity` por empresa: `[CompanyId] int NOT NULL CONSTRAINT FK_{E}_CompanyId REFERENCES meta.Company(Id)`,
    `IX_{E}_CompanyId`, y cada campo único con `UX_{E}_{F} (CompanyId, F)`. El folio conserva su índice global.
  - `updateEntity` a por empresa: `ADD [CompanyId] int NULL` → `UPDATE … = (empresa predeterminada)` → `ALTER COLUMN
    NOT NULL` → `ADD CONSTRAINT FK` → `IX` → por cada campo único: `DROP INDEX UX` + `CREATE UNIQUE INDEX UX (CompanyId, F)`.
    Cada sentencia en su lote (como la columna de estado). Se ejecuta dentro de la transacción del plan.
  - `addField` único en un catálogo por empresa: índice compuesto.
- **ModelDiff (009):** `createEntity` lleva `perCompany`; en catálogos existentes `updateEntity { perCompany: true }`
  cuando el paquete lo pide y el destino no; al revés, **conflicto**. La prueba de ida y vuelta de Compras + Inventario
  sigue dando diff vacío.
- `CompanyId` se agrega a las columnas del sistema (`Identifiers.SystemColumns`): ningún campo puede llamarse así.

## Datos del entorno (migración idempotente en `Bootstrap`)
```sql
meta.Company (Id int IDENTITY PK, Code nvarchar(20) UNIQUE, Name nvarchar(150), BaseCurrency char(3),
              IsDefault bit, IsActive bit, CreatedAt, UpdatedAt NULL)
meta.UserCompany (UserId int FK, CompanyId int FK, PK (UserId, CompanyId))
meta.[User] + CompanyId int NULL FK          -- empresa actual elegida
meta.ExchangeRate (Id int IDENTITY PK, FromCurrency char(3), ToCurrency char(3), RateDate date, Rate decimal(19,8),
                   UpdatedBy int FK, UpdatedAt, UNIQUE (FromCurrency, ToCurrency, RateDate))
```
Arranque: si no hay empresas, crea `PRINCIPAL` «Empresa principal» con moneda `Companies:DefaultCurrency` (MXN) como
predeterminada; todo usuario sin empresas queda asignado a la predeterminada (también el admin inicial).

## Empresa del usuario (`UserContextLoader`)
- `UserContext += Company?: CompanyInfo; Companies: CompanyInfo[]` con `CompanyInfo(Id, Code, Name, BaseCurrency)`.
- Permitidas = activas asignadas (superusuario: todas las activas), la predeterminada primero. Actual = la elegida si
  sigue permitida; si no, la primera permitida; sin permitidas = ninguna.
- Cuenta de servicio de clave de API: al crearla recibe la empresa actual de quien la crea.
- Usuario sintético de webhook: la empresa de quien disparó el evento.

## Filtro (`RecordFilter`)
`For(e, user, write)` = `t.[CompanyId] = @sec0` (si el catálogo es por empresa; sin empresa, `1 = 0`) **AND** el filtro
de permisos por registro de la 006 (omitido para el superusuario). `ForRecordOrParent` lo hereda a las partidas por la
subconsulta del documento. Como todos los caminos de lectura y escritura ya pasan por `RecordFilter`, no hay rutas nuevas
que proteger: listado, conteos, `GET`, historial, buscador, referencias (`RelatedLoader.Visible`), `/compute`, transición,
`PUT`, `DELETE`, reportes (también catálogos relacionados), tableros, importación, exportación y webhooks.

## Escritura (`RecordService`)
- Alta en catálogo por empresa: `CompanyId` = empresa actual del usuario (no viene del cliente: no es campo). Sin empresa
  → 403 «No tienes una empresa asignada». Las automatizaciones usan el mismo usuario (empresa de quien disparó).
- Acumular con `match` (005) sobre un catálogo por empresa busca el destino **en la misma empresa**.
- La verificación de alcance de escritura posterior al alta (006) ya incluye la empresa.

## Expresiones
| Función | Resultado |
|---|---|
| `BASECURRENCY()` | Moneda base de la empresa del contexto (texto) |
| `CURRENTCOMPANY()` | Clave de la empresa del contexto (texto) |
| `RATE(de, a)` · `RATE(de, a, fecha)` | Tipo de cambio vigente a la fecha (hoy si se omite); misma moneda = 1; sin tipo = nulo |
| `CONVERT(importe, de, a)` · `CONVERT(importe, de, a, fecha)` | `importe × RATE(…)`; importe o tipo nulo = nulo |

- Tipo vigente: el registro más reciente con `RateDate ≤ fecha` entre `de→a` (directo) y `a→de` (inverso, `1/tasa`, 10
  decimales); empate de fecha: directo. Códigos en mayúsculas. Consulta parametrizada con caché por operación
  (`RelatedLoader` implementa `ICurrencyRates`).
- **Contexto:** en peticiones, la empresa actual del usuario (que es la del registro: no ve otros). Al recalcular
  fórmulas por un cambio de modelo, la empresa de cada registro (`CompanyId`).
- `RecordFilter.Translate` (reglas `where`) no admite estas funciones: la empresa ya filtra sola.

## Contrato de API
```ts
type CompanyInfo = { id: number; code: string; name: string; baseCurrency: string };
Me += { company?: CompanyInfo; companies: CompanyInfo[] }
POST /api/auth/company { companyId: number } → Me                      // 400 si no es una de las suyas; auditoría user.company
EntityDef += { perCompany: boolean }

// Superusuario (GET también para administradores de usuarios, que asignan empresas)
type Company = CompanyInfo & { isDefault: boolean; isActive: boolean; users: number };
GET  /api/admin/companies                                 → Company[]
POST /api/admin/companies { code, name, baseCurrency }    → Company   // code ^[A-Z0-9][A-Z0-9_-]{1,19}$ (se guarda en mayúsculas)
PUT  /api/admin/companies/{id} { name, baseCurrency, isActive } → Company   // la predeterminada no se desactiva
type ExchangeRate = { id: number; from: string; to: string; date: string; rate: number; updatedBy: string; updatedAt: string };
GET    /api/admin/exchange-rates?from=&to=                → ExchangeRate[]   // 200 más recientes
PUT    /api/admin/exchange-rates { from, to, date, rate } → ExchangeRate     // alta o reemplazo del mismo día
DELETE /api/admin/exchange-rates/{id}                     → 204

User += { companyIds: number[] }
CreateUserRequest += { companyIds?: number[] }            // omitido = la predeterminada
UpdateUserRequest += { companyIds?: number[] }            // omitido = sin cambio; cambiar revoca sesiones
```
Validación: moneda `^[A-Z]{3}$`; tasa `> 0` y `< 10^11` con hasta 8 decimales; fecha `yyyy-MM-dd`; `from ≠ to`;
nombre 1–150. Auditoría: `company.change` (alta y edición, antes/después), `rate.change` (alta, reemplazo y baja),
`user.company` (cambio de empresa actual); `user.change` incluye `companyIds`.

## UI
- **Barra superior:** con 2+ empresas, botón con el icono `building-2` y el nombre de la empresa actual que abre un
  menú (`role="menu"`, teclado y Esc) con las empresas; elegir llama a `/auth/company`, recarga permisos y catálogos y
  vuelve al inicio. Con una sola empresa no aparece (capturas de referencia intactas). El menú de usuario muestra la
  empresa actual.
- **Configuración › Empresas** (superusuario, después de «Entornos»): pestaña *Empresas* (tabla con clave, nombre,
  moneda base, usuarios, estado; alta y edición en diálogo) y pestaña *Tipos de cambio* (alta/reemplazo con par, fecha y
  tasa; tabla filtrable por par; eliminar con confirmación).
- **Usuarios:** casillas «Empresas» en el diálogo del usuario; la tabla muestra sus empresas cuando hay más de una.
- **Modelador:** interruptor «Un registro por empresa» en las propiedades del catálogo (deshabilitado si ya lo es o si
  es una partida), con la explicación de que no se puede revertir.

## Compras 1.6.0 e Inventario 1.4.0 (entradas en `upgrades`)
- Compras: `updateEntity perCompany` en `Almacen`, `Requisicion`, `OrdenCompra`, `Recepcion`; `OrdenCompra.TipoCambio`
  con `defaultValue: RATE(Moneda.Clave, BASECURRENCY(), Fecha)`; reglas de la orden con `Moneda.Clave = BASECURRENCY()`;
  layout de la orden con `Moneda.Clave <> BASECURRENCY()`; etiquetas «Total en moneda base» y «Costo unitario (moneda
  base)» (campos, reportes y tablero).
- Inventario: `updateEntity perCompany` en `Existencia`, `MovimientoInventario` y `Traspaso`.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **I** Leer registros de otra empresa (listado, id directo, buscador, reporte, tablero, exportación, referencia por fórmula) | El filtro de empresa vive en `RecordFilter`, el único punto que ya usan todas las vías; aplica también al superusuario; 404 igual que inexistente; totales y conteos filtrados |
| **T/E** Crear o mover un registro a otra empresa | `CompanyId` no es campo: el cliente no lo envía ni lo cambia; se toma de la empresa actual en el servidor; la verificación de alcance de escritura posterior al alta incluye la empresa |
| **E** Elegir una empresa no asignada (o inactiva) | `POST /auth/company` valida contra las permitidas calculadas en el servidor; `UserContextLoader` vuelve a validar la elegida en cada petición |
| **E** Sesión vieja con empresas retiradas | Cambiar empresas de un usuario renueva su sello de seguridad (cierra sesiones) |
| **T** Inyección por clave de empresa o moneda | Códigos con lista blanca (`^[A-Z0-9_-]`, `^[A-Z]{3}$`) y siempre como parámetros; `CompanyId` es entero |
| **T** Inyección en el DDL de «por empresa» | Mismo `DdlGenerator`: identificadores de metadatos validados y citados; el id de la predeterminada por subconsulta, no concatenado |
| **I** Fuga entre empresas por la etiqueta de una referencia de un catálogo compartido a uno por empresa | Riesgo residual documentado; el buscador y la validación de referencias filtran por empresa |
| **T** Tipos de cambio manipulados | Solo superusuario; cada alta, reemplazo y baja en la auditoría inmutable con antes/después |
| **R** Repudio de cambios de empresa o tipo de cambio | `company.change`, `rate.change`, `user.company`, `user.change` en la auditoría |
| **D** Consultas de tipo de cambio por cada fórmula | Caché por operación en `RelatedLoader`; índice único (de, a, fecha) |
