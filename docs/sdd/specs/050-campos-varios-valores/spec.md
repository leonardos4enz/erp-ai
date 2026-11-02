# Spec 050 — Campos de varios valores

**Estado:** Implementado · **Depende de:** 001, 005, 006, 007, 008, 012, 018, 027, 029, 041 · **Origen:** roadmap, «Specs planeadas
(después de la 049): piezas para procesos complejos» (revisión del dueño, 2026-11-02, de un proceso de compras real que **no** se
documenta). Esta es la primera de las piezas genéricas: «un proveedor con varias categorías», «un centro con varios
responsables». Los ejemplos son inventados.

## Problema
Verificado en el código de `7ea9553`.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `FieldTypes.All` tiene 14 tipos; el único que apunta a otro registro es `reference` (una columna `bigint`) y a una persona, `user` (una columna `int`) | Un proveedor que surte papelería **y** limpieza necesita dos campos, o un catálogo intermedio con partidas, que no se puede usar como valor en filtros, importación ni reportes |
| 2 | `DdlGenerator` solo genera columnas en la tabla del catálogo (`CreateTable`, `AddColumn`); `FieldTypes.IsVirtual` solo reconoce `lines` | No hay dónde guardar «varios» sin un catálogo hijo |
| 3 | `FilterSql` tiene operadores por tipo de valor (`ValueKind`), ninguno de pertenencia | En una lista o un reporte no se puede pedir «proveedores que surten limpieza» |
| 4 | La importación (`CellAsync`) y la exportación (`ExportCell`) convierten una celda en un valor | Sin una convención para «varios valores en una celda» |

## Principio
Un **tipo de campo genérico del motor** (constitución I.2): `multireference` (varios registros de otro catálogo) y `multiuser`
(varias personas del sistema). El valor vive en una **tabla de unión** que genera el DDL (`app.[Catalogo__Campo]`: el `__` no lo
puede tener ningún nombre del modelo, así que no choca con un catálogo), con llave foránea al dueño y al destino. El registro
dueño es la unidad de concurrencia y de auditoría: cambiar los valores es modificar el registro (su `rowversion`, su bitácora
antes/después, sus permisos por campo y por registro). **Todo camino que lee o escribe un registro trata el campo igual**:
API de datos, formulario, lista, búsqueda, `/compute`, importación, exportación, OData, impresión, webhooks, MCP y el
Acompañante (que leen y escriben por la misma API). Lo ejerce el paquete Compras (1.11.0) con metadatos: el proveedor y las
categorías que surte; el departamento y sus responsables.

## Historias

**H1 — Declarar el campo.** Como *administrador del modelo*, quiero agregar un campo de varios valores a un catálogo (en el plan
de cambios, el modelador y un paquete) para registrar relaciones de uno a varios sin un catálogo intermedio.
- CA1: `addField` (y `createEntity`) acepta `type: "multireference"` con `referenceEntity` (un catálogo raíz existente) y
  `type: "multiuser"` (sin `referenceEntity`); la vista previa muestra la tabla de unión que se creará.
- CA2: el plan rechaza lo que no tiene sentido con un mensaje para el modelador: en una partida, apuntando a una partida o a un
  catálogo que no existe, con `unique`, `defaultValue`, `formula` o `lookupFilter`, y una fórmula, regla, condición,
  automatización, ruta de reporte o agrupación que lea el campo como un valor simple («tiene varios valores: …»). Como
  columna de una vista sí vale (no se ordena por él).
- CA3: archivar el campo conserva la tabla de unión y sus filas (no destructivo, II.9); reactivarlo los devuelve.
- CA4: el modelador ofrece ambos tipos al agregar un campo («Varios registros de…» y «Varias personas»).

**H2 — Capturar y ver los valores.** Como *usuario*, quiero elegir varios registros en el formulario y verlos en la lista para
capturar la relación de una vez.
- CA1: el formulario muestra un selector múltiple accesible: cada valor elegido es una etiqueta con «Quitar …», y un buscador
  (combobox) agrega más; con teclado se agrega con Enter y se quita con el botón de cada etiqueta; no ofrece lo ya elegido.
- CA2: la API recibe y devuelve el campo como **lista de ids** (`[3, 7]`) con sus etiquetas en `Campo__label` (misma
  posición); `null` o `[]` es vacío; los ids repetidos cuentan una vez; un campo obligatorio exige al menos uno.
- CA3: cada id debe existir (vigente) y **ser visible** para quien guarda (como en una referencia, spec 006); los que el registro
  ya tenía se conservan aunque ya no los vea. Más de **100** valores → 400.
- CA4: la lista, el registro en solo lectura y la impresión muestran las etiquetas separadas por coma; un valor fuera del
  alcance de quien mira se muestra «(sin acceso)», como una referencia.

**H3 — Concurrencia y bitácora.** Como *auditor*, quiero que cambiar los valores sea una modificación del registro para saber
quién cambió qué y que dos personas no se pisen.
- CA1: crear o modificar el registro guarda la lista completa en la bitácora (antes y después, ids).
- CA2: dos ediciones del mismo registro con la misma versión: una guarda, la otra recibe **409** y la tabla de unión queda con
  los valores de la primera (nunca una mezcla).
- CA3: si la escritura falla después de cambiar la tabla de unión (p. ej. el registro queda fuera del alcance de escritura),
  no queda nada a medias: todo es una transacción.

**H4 — Importar y exportar.** Como *usuario*, quiero importar y exportar el campo en una celda para mover datos con hojas de
cálculo.
- CA1: la exportación escribe las etiquetas separadas por «; ».
- CA2: la importación acepta etiquetas (o `#id`) separadas por «;», con la misma búsqueda y alcance que una referencia, y
  reporta por fila el valor que no existe o es ambiguo; lo exportado se puede volver a importar.

**H5 — Filtrar y consultar.** Como *usuario*, quiero filtrar por los valores para encontrar «los proveedores que surten
limpieza».
- CA1: en listas, vistas y reportes, el campo admite «contiene» (un valor), «contiene alguno de» (lista), «vacío» y «con
  valores»; en `multiuser`, «contiene» acepta `$me` («donde soy responsable»).
- CA2: OData publica un `multireference` como **navegación de colección** a su catálogo (`$expand=Categorias`) con el alcance
  de lectura del destino (lo que no ve, no aparece) y el tope de expansión de las partidas.

**H6 — La misma regla en todos los caminos.** Como *dueño*, quiero que el campo no abra una fuga para que la seguridad por
campo y por registro siga valiendo.
- CA1: el canario de la 029 incluye campos de varios valores: uno visible que apunta a registros fuera del alcance del lector
  (etiquetas «(sin acceso)»), uno a un catálogo cuyo campo para mostrar es una fórmula que se le oculta, y uno oculto por
  campo; ninguna ruta del inventario, ni OData con `$expand`, ni la exportación, ni el PDF devuelven el canario al lector.
- CA2: un campo oculto por rol no se devuelve, no se exporta, no se filtra (400) y no se escribe (se ignora, como hoy).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (las tres piezas del roadmap, ~10: sí) · más de ~2 500 líneas de
   producto (el tipo nuevo toca ~20 archivos del motor y ~15 de la SPA; con «pertenece a» y filtros compuestos, ~4 000: sí) ·
   seguridad o concurrencia junto con UI nueva (sí) · más de una pantalla principal (no).
   → **Se divide** (dueño, 2026-11-02): la **050** es el campo de varios valores en todos los caminos (este documento) y la
   **051** «pertenece a» en expresiones y seguridad por registro (`X IN Campo`, `CURRENTUSER() IN …`, con `EXISTS`
   parametrizado) más los filtros de búsqueda compuestos. La matriz de autorización pasa a 052–054 (núcleo, fase 2, fase 3),
   los adjuntos a 053… (ver roadmap: se renumeran 051–055 → 052–056).
2. **¿A qué apunta?** (dueño, 2026-11-02): a **catálogos y a personas**. Dos tipos, como `reference` y `user`:
   `multireference` y `multiuser`. Con personas, la regla de la 051 «`CURRENTUSER() IN Centro.Responsables`» se traduce a un
   `EXISTS` directo y sirve a los perfiles de la matriz.
3. **Dónde vive el valor.** Tabla de unión `app.[Catalogo__Campo]` (`OwnerId`, `ValueId`, llave primaria compuesta, llave
   foránea a ambos, índice por `ValueId`). Se descartó una columna JSON o de texto: no tendría integridad referencial ni
   permitiría el `EXISTS` indexado que necesita la 051.
4. **Formato en la API.** Lista de ids y `Campo__label` con la lista de etiquetas en el mismo orden (por etiqueta), igual que
   `Campo`/`Campo__label` de una referencia: el cliente que ya entiende referencias entiende esto.
5. **Tope:** 100 valores por campo y registro (una persona con 4 centros de 50, un proveedor con 5 categorías); más es otro
   modelo (partidas).
6. **Fuera de las expresiones en la 050.** Una fórmula, regla, condición o automatización que lea el campo se rechaza al
   validar el plan, con un mensaje que nombra la 051. Mejor un rechazo claro que un valor simple sin sentido.
7. **Solo catálogos raíz.** Un campo de varios valores no va en partidas (una partida ya es «varios») ni apunta a una partida.

## Fuera de alcance (anotado en el roadmap)
- «Pertenece a» en expresiones, reglas de acceso por registro y filtros de búsqueda compuestos: **051**.
- Agrupar un reporte por un campo de varios valores («proveedores por categoría», cada registro en varios grupos) y usarlo en
  rutas de reportes y exploración.
- `multiuser` por OData (las personas no son un conjunto de entidades); se publica solo `multireference`.
- Campos de varios valores en partidas.
- Vista «inversa» (en la categoría, los proveedores que la tienen).
