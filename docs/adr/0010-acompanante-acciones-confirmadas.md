# ADR-0010 — Acompañante: lee con el alcance de la persona y escribe solo con su confirmación, acción por acción

**Estado:** Aceptado · **Fecha:** 2026-11-01 · **Spec:** [046](../sdd/specs/046-acompanante/spec.md) · **Enmienda:** constitución II.8

## Contexto
La constitución II.8 dice que el LLM nunca llama endpoints de datos con privilegios propios y que solo propone Planes de
Cambios que un humano autorizado confirma. Studio (033–037) y el MCP (038, 044) la cumplen: en el entorno real solo leen, y
lo que escriben va a una copia aislada o a una propuesta de modelo. El dueño pidió (2026-11-01) un Acompañante para cualquier
persona que **pueda tomar las acciones que ella tiene permitidas**, con una regla: «la lectura no pregunta, la escritura sí».
Una escritura de datos no es un Plan de Cambios, así que II.8, tal como está escrita, la prohíbe.

## Decisión
1. **Leer, como la persona.** Las herramientas del Acompañante son rutas de lectura (efecto «ninguno», 029) ejecutadas por
   `RouteDispatcher` con la identidad recargada de la persona (sello), su empresa y su credencial; aplican `RecordFilter`,
   `FieldAccess` y empresa porque son las mismas rutas del navegador (ADR-0008). Leer no pide confirmación.
2. **Escribir, solo confirmado.** El modelo no tiene herramienta que escriba. `proponer_accion` guarda una **acción**
   (`meta.CompanionAction`): qué ruta de datos (crear, editar o transición: lista fija en el código, nunca del modelo), su
   cuerpo, la versión del registro, una vista previa y una huella. La persona la ve y la confirma —una por una, con la huella—
   y entonces el servidor la ejecuta **una vez** por `RouteDispatcher` con la identidad, la empresa y los permisos que la persona
   tiene en ese momento (`allowWrites` solo para esas tres rutas). Una versión del registro que cambió, un permiso perdido o
   una regla que falla rechazan la acción como rechazarían la pantalla.
3. **Nunca el modelo ni la seguridad.** Planes de Cambios, usuarios, roles, claves, paquetes, eliminar, importar y exportar
   no están al alcance del Acompañante (para el modelo existe Studio, con sus compuertas).
4. **Rastro.** La bitácora del cambio lleva `Origin = 'acompañante'`; la acción guarda quién la confirmó, cuándo y el resultado.

II.8 queda así: «El LLM nunca ejecuta SQL ni llama endpoints de datos con privilegios propios. Solo propone: Planes de Cambios
que un humano autorizado confirma, o (Acompañante, ADR-0010) acciones sobre registros que la propia persona confirma una por
una y que se ejecutan con su identidad y sus permisos del momento. El contenido generado por el LLM se trata como entrada no
confiable.»

## Consecuencias
- (+) El Acompañante hace por la persona lo que ella puede hacer, sin un privilegio más, y por la misma regla que la pantalla.
- (+) La inyección de instrucciones desde un registro (texto de otra persona) a lo más produce una propuesta visible, que no
  cambia nada sin la persona; el tope de propuestas pendientes limita el ruido.
- (−) Registros leídos viajan al proveedor (como Studio con `datos`); se purgan del historial al cerrar cada turno y la
  conversación se borra a los 90 días sin actividad.
- (−) La persona puede confirmar sin leer: la vista previa debe ser clara (047) y cada acción es una sola escritura acotada.
- (−) Si el proceso cae entre ejecutar la acción y guardar su resultado, la acción queda «sin confirmar si se aplicó» y no se
  repite sola (la persona revisa el registro): preferimos no duplicar a reintentar a ciegas.
