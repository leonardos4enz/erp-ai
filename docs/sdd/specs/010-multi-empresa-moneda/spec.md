# Spec 010 — Multi-empresa y multi-moneda

**Estado:** Implementado · **Depende de:** 002, 005, 006, 007, 008, 009 · **Origen:** [roadmap](../../roadmap.md#010--multi-empresa-y-multi-moneda)

## Problema
1. El ERP atiende a una sola empresa. Un grupo con dos razones sociales tendría que instalar dos entornos y repetir
   proveedores, productos y configuración, o mezclar en una base las órdenes, almacenes y existencias de ambas.
2. Nada separa los datos de una empresa de los de otra: no hay forma de declarar qué catálogos son de cada empresa
   (órdenes, almacenes, existencias) y cuáles se comparten (proveedores, productos, unidades).
3. La moneda base está fija en el modelo: Compras compara contra `'MXN'` en reglas y layout, y el «Total MXN» supone
   que toda empresa lleva su contabilidad en pesos.
4. El tipo de cambio se captura a mano en cada orden (por defecto 1). No hay tipos de cambio por fecha ni forma de
   convertir importes en una fórmula.

## Historias

**H1 — Empresas.** Como *administrador*, quiero dar de alta las empresas del grupo y decidir quién trabaja en cuál.
- CA1: En *Configuración › Empresas* doy de alta empresas con clave (única, no cambia), nombre y moneda base (código
  ISO de 3 letras), las edito y las desactivo. La empresa predeterminada no se desactiva. Todo queda en la auditoría.
- CA2: En *Usuarios y roles* elijo las empresas de cada usuario. Un usuario creado sin indicarlas recibe la
  predeterminada. Cambiar sus empresas cierra sus sesiones abiertas (como cambiar sus roles o su unidad).
- CA3: Una instalación existente no cambia de comportamiento: se crea la «Empresa principal» (MXN), todos los usuarios
  quedan asignados a ella y quien tiene una sola empresa no ve nada nuevo en la barra superior.

**H2 — Empresa actual.** Como *usuario de varias empresas*, quiero elegir en cuál estoy trabajando.
- CA1: Con más de una empresa, la barra superior muestra la empresa actual y permite cambiarla; la elección se
  recuerda entre sesiones. El menú de usuario muestra la empresa actual.
- CA2: Solo puedo elegir empresas activas que tengo asignadas (el superusuario, cualquier empresa activa). Si mi
  empresa actual se desactiva o me la quitan, quedo en otra de las mías.
- CA3: Al cambiar de empresa, listados, formularios, buscadores, reportes y tableros muestran los datos de la nueva.

**H3 — Catálogos por empresa.** Como *modelador*, quiero declarar qué catálogos son de cada empresa y cuáles se comparten.
- CA1: Un catálogo es **compartido** (por defecto) o **por empresa**; se declara al crearlo o después, con el plan de
  cambios y desde el modelador. Las partidas siguen a su documento.
- CA2: Un registro de un catálogo por empresa pertenece a la empresa actual de quien lo crea (en una automatización,
  la de quien disparó el evento). El cliente no la elige ni la cambia.
- CA3: Se aplica **en el servidor** junto con la seguridad por registro (006): listados, totales y conteos por estado,
  lectura, buscadores, validación de referencias, `/compute`, transiciones, historial, edición, eliminación, reportes,
  tableros, importación y exportación. Un registro de otra empresa responde **404**, también al superusuario.
- CA4: Los campos únicos se validan **por empresa** (dos empresas pueden tener su almacén «PRIN»). Los folios
  automáticos siguen siendo únicos en el entorno.
- CA5: Pasar un catálogo existente a «por empresa» asigna sus registros a la empresa predeterminada, sin perder datos.
  Volver a «compartido» se rechaza en la vista previa.
- CA6: Es metadato: viaja en los paquetes de *Entornos* (009) y el diff lo compara (pasar a por empresa es un cambio;
  volver a compartido, un conflicto).

**H4 — Monedas y tipos de cambio.** Como *administrador*, quiero registrar tipos de cambio por fecha, y como *modelador*,
usarlos en fórmulas.
- CA1: En *Configuración › Empresas* capturo el tipo de cambio de un par de monedas en una fecha (si ya existe ese día,
  se reemplaza), los consulto por par y elimino uno capturado por error. Todo queda en la auditoría.
- CA2: El lenguaje de expresiones gana `BASECURRENCY()` (moneda base de la empresa), `CURRENTCOMPANY()` (clave de la
  empresa), `RATE(de, a[, fecha])` y `CONVERT(importe, de, a[, fecha])`. El tipo usado es el más reciente en o antes de
  la fecha (directo o inverso); misma moneda = 1; sin tipo registrado = vacío.
- CA3: Las fórmulas guardadas se calculan con la empresa del registro, también al recalcularlas por un cambio de
  modelo. Una fórmula guardada de un catálogo compartido no puede usar `BASECURRENCY()` ni `CURRENTCOMPANY()` (su
  valor dependería de quién guarda): se rechaza en la vista previa.
- CA4: Un valor por defecto que usa `RATE` se sugiere en vivo en el formulario y se recalcula mientras la persona no
  capture el campo.

**H5 — Compras 1.6.0 e Inventario 1.4.0.** Como *usuario de Compras e Inventario de un grupo*, quiero que los módulos
separen las operaciones de cada empresa y conviertan a su moneda base.
- CA1: Requisiciones, órdenes de compra, recepciones y almacenes son por empresa; existencias, movimientos y traspasos
  también. Proveedores, productos, monedas, unidades, condiciones de pago, categorías y departamentos se comparten.
- CA2: El tipo de cambio de la orden se sugiere con `RATE(Moneda.Clave, BASECURRENCY(), Fecha)`; la regla de «tipo de
  cambio 1» y el layout comparan contra la moneda base en lugar de `'MXN'`; «Total MXN» y «Costo unitario MXN» pasan a
  «Total en moneda base» y «Costo unitario (moneda base)».
- CA3: Una recepción de la empresa B mueve las existencias de la empresa B: las de la empresa A no cambian.
- CA4: Llegan como actualizaciones versionadas, sin reinstalar ni perder datos (lo existente queda en la predeterminada).

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.

**Qué es metadato y qué es dato o configuración del entorno**
| Concepto | Naturaleza | ¿Viaja en paquetes (009)? |
|---|---|---|
| Catálogo compartido o por empresa (`perCompany`) | **Metadato** del catálogo (Plan de Cambios, auditado) | Sí; `ModelDiff` lo compara |
| Fórmulas, valores por defecto y reglas con `RATE`, `CONVERT`, `BASECURRENCY()` | **Metadato** (lenguaje de expresiones) | Sí |
| Empresas (clave, nombre, moneda base, activa, predeterminada) | **Dato del entorno** (`meta.Company`) | No: cada entorno tiene sus empresas |
| Empresas de cada usuario y su empresa actual | **Dato del entorno**, como sus roles y su unidad | No |
| Tipos de cambio por fecha | **Dato del entorno** (`meta.ExchangeRate`) | No |
| Empresa a la que pertenece un registro | **Dato** (columna de sistema `CompanyId`) | No |

- **Monedas = códigos ISO 4217 de 3 letras** (texto). El motor no tiene catálogo de monedas: la moneda base de una
  empresa y los tipos de cambio usan el código, y una fórmula lo toma de donde el modelo lo tenga (`Moneda.Clave` en
  Compras). Así el catálogo *Moneda* de Compras sigue siendo un dato del módulo.
- **Una empresa a la vez.** El usuario trabaja en su empresa actual (no hay selección múltiple). Reportes consolidados
  entre empresas quedan fuera de alcance.
- **La empresa es contexto, no privilegio:** el superusuario puede cambiar a cualquier empresa activa, pero ve los
  registros de la actual, igual que cualquiera. Así una prueba o una revisión en otra empresa no mezcla datos.
- **Sin empresas asignadas** no se ven ni se crean registros de catálogos por empresa (deny by default, 403 al crear).
- **Folios globales:** un contador por catálogo en todo el entorno. Series por empresa quedan fuera de alcance.
- **Referencias de compartido a por empresa:** se permiten (Inventario las tiene hasta 1.4.0: *Existencias* → *Almacén*).
  El buscador y la validación de referencias ya filtran por la empresa actual. Riesgo residual: la etiqueta.
- **Tipos de cambio:** los captura el superusuario (como *Integraciones*). Sin triangulación (USD→EUR vía MXN): se
  registra el par que se usa. Un rol de finanzas y la carga automática desde un banco central quedan para otra spec.
- **La conversión se fija al guardar** (fórmula guardada), como cualquier fórmula que lee datos relacionados: capturar
  después un tipo de cambio no recalcula documentos ya guardados.
- **Claves de API (008):** la cuenta de servicio trabaja en la empresa actual de quien creó la clave.
- **Webhooks (008):** la carga se arma con la empresa del evento (la de quien lo disparó).
- **Fuera de alcance:** series de folios por empresa, consolidación, intercompañía, unidades jerárquicas por empresa,
  revaluación cambiaria, catálogo de monedas del motor, volver un catálogo a compartido.
