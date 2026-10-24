# Spec 015 — Jerarquía de roles, equipos y unidades de negocio

**Estado:** Implementado · **Depende de:** 006, 010, 012, 014 · **Origen:** [roadmap](../../roadmap.md#015--jerarquía-de-roles-equipos-y-unidades-de-negocio)

## Problema
El modelo de principales de la 006 es plano y la 016 (tareas programadas) y la 018 (OAuth) necesitan uno más rico.

1. **Unidades sin jerarquía.** La unidad del usuario es una etiqueta de texto: el alcance «de mi unidad» solo ve a los
   dueños con exactamente esa etiqueta. Un gerente regional no ve lo de sus sucursales, salvo con un rol con alcance
   «todos», que le muestra también lo de las otras regiones.
2. **Sin equipos.** El dueño de un registro es siempre una persona. Una cola de trabajo compartida («Compras Norte»)
   obliga a dar alcance «de mi unidad» o «todos», o a reasignar registros persona por persona.
3. **Roles sin herencia.** Cada permiso se repite rol por rol: «Gerente de compras» copia a mano lo de «Comprador», y
   cuando el comprador gana un catálogo, alguien tiene que acordarse del gerente.
4. **Seguridad sin cuatro ojos.** Con `Environments:RequireApproval` el modelo solo cambia con la aprobación de otro
   administrador (009, 012), pero roles, asignaciones y claves de API cambian en vivo con una sola persona
   ([ADR-0004](../../../adr/0004-seguridad-como-dato-del-entorno.md)). Una compuerta solo en los permisos no protegía
   nada; hace falta una que cubra todas las vías de ampliar acceso.

## Historias

**H1 — Unidades en árbol.** Como *administrador de usuarios*, quiero organizar las unidades en un árbol para que el
gerente de una región vea lo de todas sus sucursales.
- CA1: Las unidades se dan de alta en *Seguridad › Unidades* con nombre y unidad superior (opcional). Se pueden mover
  bajo otra unidad; no se permite un ciclo ni más de 10 niveles. El nombre no se cambia (lo usan reglas y datos).
- CA2: La unidad de un usuario se elige de las existentes. Las etiquetas que ya tenían los usuarios se convierten en
  unidades raíz al actualizar, sin perder datos.
- CA3: Alcance nuevo `unitTree` («mi unidad y sus descendientes»): el dueño del registro pertenece a mi unidad o a una
  que cuelga de ella, a cualquier profundidad. `unit` sigue siendo solo mi unidad. Sin unidad, ambos equivalen a «propios».
- CA4: Mover una unidad cambia lo que ven sus superiores en la siguiente petición, sin volver a iniciar sesión.

**H2 — Equipos.** Como *modelador*, quiero que el dueño de un registro pueda ser un equipo, para que todos sus miembros
lo trabajen.
- CA1: En *Seguridad › Equipos* se dan de alta equipos con nombre, unidad (opcional) y miembros. Un equipo no inicia
  sesión, no tiene roles ni recibe claves de API.
- CA2: La política del catálogo declara `teamOwners: true` (requiere `owner`): el campo dueño acepta una persona o un
  equipo activo, y su buscador ofrece ambos. Los demás campos de tipo usuario siguen aceptando solo personas.
- CA3: El alcance «propios» incluye los registros cuyo dueño es un equipo del que soy miembro (activo). «De mi unidad»
  y `unitTree` incluyen los registros de equipos de esas unidades.
- CA4: Agregar o quitar a alguien de un equipo cambia lo que ve en la siguiente petición. Crear o pasar un registro a
  un equipo del que no soy miembro queda fuera de mi alcance «propios» y se rechaza (regla de la 006).

**H3 — Herencia de roles.** Como *administrador de usuarios*, quiero que un rol herede de otros para no repetir permisos.
- CA1: Un rol declara de qué roles hereda («Gerente de compras» hereda de «Comprador»). Quien tiene el rol tiene
  también, a efectos de seguridad, los roles de los que hereda, a cualquier profundidad: permisos por catálogo,
  `HASROLE()`, roles de campos, de permisos por registro, de transiciones y de reglas.
- CA2: Las capacidades administrativas (todo, modelo, usuarios, auditoría) no se heredan: no se puede heredar de un rol
  que las tenga, ni darlas a un rol del que otros heredan.
- CA3: No se permiten ciclos, más de 10 roles de los que heredar ni más de 5 niveles. El cambio aplica en la siguiente
  petición.

**H4 — Aprobación de cambios de seguridad.** Como *responsable de seguridad*, quiero que en producción nadie pueda
ampliar accesos sin que otro administrador lo apruebe, y que revocar siga siendo inmediato.
- CA1: Con `Environments:RequireApproval` **y** `Environments:RequireSecurityApproval`, los cambios que **amplían**
  acceso no se aplican: quedan como solicitud (respuesta 202). Son: alta de usuario; agregar roles o empresas, cambiar
  la unidad, reactivar o poner contraseña a otro usuario; alta de rol con permisos, capacidades o herencia; agregar
  permisos, capacidades o herencia a un rol o renombrarlo; alta de equipo con miembros; agregar miembros, cambiar la
  unidad o reactivar un equipo; mover una unidad; alta de clave de API y agregar roles a una clave.
- CA2: Lo que solo reduce acceso se aplica de inmediato: quitar roles, empresas o miembros, desactivar, quitar
  permisos, revocar o rotar una clave, cambiar nombres visibles o descripciones, dar de alta una unidad. Una petición
  que mezcla ambas cosas espera completa.
- CA3: Otro administrador (con *administrar usuarios*; las claves, otro superusuario) revisa la solicitud con el antes
  y el después y la aprueba con su huella. Si el usuario, rol, equipo, unidad o clave cambió desde la solicitud, la
  aprobación se rechaza (409) y hay que volver a solicitarla. Aprobar aplica el cambio en ese momento.
- CA4: Quien solicitó no puede aprobar. Cualquiera de los dos puede rechazar (quien solicitó, para cancelar).
- CA5: Una clave de API solicitada se muestra una sola vez a quien la pidió y funciona solo cuando se aprueba. Una
  contraseña solicitada se guarda solo como hash.
- CA6: Solicitud, aprobación y rechazo quedan en la auditoría (`security.request`, `security.approve`,
  `security.reject`), además del cambio aplicado con su antes y después.

**H5 — Una sola regla en todas las vías.** Como *responsable de seguridad*, quiero que unidades, equipos y herencia se
apliquen igual en todos lados.
- CA1: Todo se resuelve en el servidor con el filtro por registro de la 006 (SQL parametrizado): listados con totales,
  registro, buscadores, referencias, reportes, bitácora y etiquetas de referencias «(sin acceso)».

## Decisiones (CLARIFY)
Sesión sin interlocutor; se registran para revisión del dueño.
- **El equipo es un principal en `meta.[User]`** (como las cuentas de servicio de la 008), con `IsTeam = 1`. Así el
  campo dueño (columna `int` con llave a `meta.[User]`), sus etiquetas, la bitácora y los filtros siguen funcionando sin
  un tipo de campo nuevo. Nunca inicia sesión ni carga un contexto de usuario.
- **Equipos sin roles.** El equipo agrupa la propiedad de registros; los permisos siguen siendo de las personas. Roles
  de equipo (Dataverse) suman una segunda vía de privilegios que habría que aprobar y auditar; no hace falta para la
  cola compartida.
- **`teamOwners` explícito** por catálogo: aceptar equipos en cualquier campo de usuario («Solicitante», «Autorizó»)
  mezclaría personas y grupos en datos que hoy son de personas.
- **La unidad sigue siendo su nombre** en el usuario y en `CURRENTUNIT()`: las reglas `Departamento = CURRENTUNIT()` de
  la 006 no cambian. Por eso el nombre de una unidad no se edita; borrar unidades queda fuera de alcance.
- **Jerarquía y equipos se leen en la base al filtrar** (no en la sesión): moverlos aplica en la siguiente petición sin
  cerrar sesiones. La unidad propia del usuario sí está en la sesión y cambiarla la renueva (como en la 006).
- **No se heredan capacidades administrativas**: la herencia es para permisos de datos. Así una clave de API (que no
  admite roles de administración, 008) no puede obtenerlas por herencia.
- **Aprobación opcional y aparte** (`RequireSecurityApproval`): un entorno puede exigir aprobación del modelo sin
  exigirla en seguridad. Revisa [ADR-0004](../../../adr/0004-seguridad-como-dato-del-entorno.md) con el
  [ADR-0005](../../../adr/0005-aprobacion-de-cambios-de-seguridad.md).
- **Ampliar espera, reducir no.** Revocar accesos debe ser inmediato (ADR-0004). Rotar una clave es la respuesta a una
  filtración y no cambia privilegios: inmediato. Poner contraseña a otra persona permite entrar como esa persona: espera.
- **Aprobar aplica** (no hay un tercer paso «aplicar» como en paquetes): el cambio es pequeño y la huella cubre el
  antes y el después. La clave solicitada se genera al solicitar, para que el secreto lo reciba quien la pidió.
- **Con un solo administrador** las solicitudes no se pueden aprobar: se da de alta al segundo antes de activar la
  opción, o se desactiva en la configuración del servidor (fuera del alcance de la app, igual que `AllowDirectChanges`).
- **Los módulos no cambian.** Unidades y equipos son datos del entorno: Compras e Inventario no pueden suponer un árbol.
  `unitTree` y `teamOwners` son metadatos de la política y viajan en paquetes (009) como el resto.

## Fuera de alcance
Roles de equipo, borrar o renombrar unidades, una función de expresiones para comparar un campo de texto contra el
árbol de unidades, compartir registros sueltos con personas (Salesforce *sharing*), y aprobación por más de una persona.
