# Spec 001 — Núcleo de metadatos, API genérica y UI dinámica

**Estado:** Implementado (MVP) · **Constitución:** v1

## Problema
En un ERP tradicional cada catálogo (clientes, empaques, almacenes…) requiere que un
desarrollador programe tabla, API y pantallas. Queremos que un usuario autorizado (y más
adelante un asistente de IA) cree y modifique catálogos sin programar.

## Historias de usuario

**H1 — Crear un catálogo.** Como *modelador*, quiero definir una entidad (nombre, etiqueta,
icono, campos) para que aparezca de inmediato en el menú con su listado y formulario.
- CA1: Al aplicar el plan, existe la tabla en SQL Server, la entidad aparece en el menú
  y se puede capturar información.
- CA2: Antes de aplicar se muestra una vista previa de los pasos (incluido el SQL).
- CA3: Nombres inválidos o reservados se rechazan con mensaje claro.

**H2 — Agregar campos.** Como *modelador*, quiero agregar campos a un catálogo existente
sin perder información. Tipos: texto, texto largo, entero, decimal, sí/no, fecha,
fecha-hora, lista de opciones, referencia a otro catálogo.

**H3 — Reglas por rol.** Como *modelador*, quiero indicar que un campo es obligatorio
solo para ciertos roles (p. ej. "los Administradores deben capturar el Nombre").

**H4 — Capturar datos.** Como *usuario*, quiero buscar, paginar, ordenar, crear, editar y
eliminar registros de los catálogos a los que tengo permiso.
- CA: Si dos usuarios editan el mismo registro, el segundo recibe aviso de conflicto.
- CA: Eliminar es lógico (el registro queda en auditoría).

**H5 — Seguridad.** Como *administrador*, quiero gestionar usuarios y roles, y asignar
permisos por catálogo (leer / crear / editar / eliminar).
- CA: Un usuario sin permiso no ve el catálogo en el menú y la API responde 403.
- CA: 5 intentos fallidos bloquean la cuenta 15 minutos.

**H6 — Auditoría.** Como *auditor*, quiero ver quién cambió qué y cuándo, en datos y en
el modelo.

## Fuera de alcance (specs posteriores)
- Asistente IA (spec 002) — se construirá sobre los Planes de Cambios de esta spec.
- Procesos / flujos de aprobación (spec 003).
- Reportes, importación/exportación, multiempresa.

## Preguntas resueltas (CLARIFY)
- ¿Base de datos? SQL Server. Desarrollo en LocalDB.
- ¿Borrado de campos? No: se archivan (constitución II.9).
- ¿Obligatoriedad a nivel BD o app? App. Las columnas dinámicas son `NULL` en BD para poder
  agregar campos a tablas con datos; el motor aplica la regla.
