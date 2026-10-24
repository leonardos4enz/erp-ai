# Spec 014 — Pendientes menores y riesgos residuales

**Estado:** Implementado · **Depende de:** 005–013 · **Origen:** [roadmap](../../roadmap.md#014--pendientes-menores-y-riesgos-residuales)

## Problema
Una auditoría de solo lectura posterior a la 013 confirmó las correcciones de la 012 y dejó una lista corta: tres
diferidos de la 012 que sí tienen arreglo genérico, dos defectos de Compras y de webhooks, y tres riesgos residuales que
había que cerrar o documentar con prueba. Ninguno es una capacidad de plataforma (esas quedan como specs 015+).

1. **Sugerencias de precio sin convertir.** Compras 1.7.0 guarda `Producto.MonedaCosto`, pero el costo estimado de la
   requisición y el precio unitario de la orden se sugieren con `Producto.CostoUltimo` tal cual: una orden en USD, o una
   empresa con moneda base USD, recibe un número en MXN.
2. **Tope del acumulado.** `rollup` carga a memoria los registros de origen; pasados 10 000 (los movimientos de un
   producto en un almacén, p. ej.) **todo movimiento nuevo falla**.
3. **Folios bloqueados durante la importación.** La vista previa y la importación toman el folio con `UPDLOCK, HOLDLOCK`
   dentro de su transacción y lo retienen hasta el final: las altas del mismo catálogo esperan a que termine la importación.
4. **Un paquete archiva campos locales sin decisión.** El diff archiva los campos del destino que no vienen en el paquete
   y solo lo dice en una advertencia.
5. **Webhooks.** Una respuesta 4xx (p. ej. 400, 401, 404) se reintenta como si fuera temporal; Teredo (`2001::/32`)
   no está en la lista de direcciones internas del control SSRF.
6. **Riesgos residuales de la 012.** (a) El bloqueo previo a validar cubre solo destinos `target`, no los acumulados por
   `match`. (b) `Environments:AllowDirectChanges` reabre los atajos en un entorno con aprobación sin que nadie lo note.
   (c) La bitácora se filtra con la política de seguridad actual, no con la del momento del evento.
7. **Permisos de roles en entornos con aprobación.** `PUT /api/admin/roles/{id}` cambia permisos en vivo, sin cuatro ojos.

## Historias

**H1 — Precios sugeridos en la moneda del documento.** Como *comprador*, quiero que el precio que sugiere el sistema
esté en la moneda de la orden y el costo estimado en la moneda base de mi empresa.
- CA1: Compras 1.8.0: el precio unitario sugerido es el costo último convertido a la moneda de la orden con el tipo de
  cambio de la orden: `costo → moneda base (a la fecha de la orden) ÷ TipoCambio`. Así el *costo unitario (moneda base)*
  de la partida vuelve a ser el costo último.
- CA2: El costo estimado de la requisición es el costo último convertido a la moneda base de la empresa a la fecha de la
  requisición.
- CA3: Sin tipo de cambio registrado no se sugiere nada (el campo queda vacío y se captura), nunca un número en otra
  moneda. Un producto sin moneda del costo (capturado antes de 1.7.0) se toma en la moneda base de la empresa, como
  hasta ahora.
- CA4: La moneda del costo se puede capturar en el producto (antes no era editable) y se sugiere con la moneda base de
  quien lo da de alta; si no está vacía, es una clave de 3 letras.

**H2 — Acumulado sin tope.** Como *almacenista*, quiero registrar movimientos aunque un producto tenga miles.
- CA1: Si el campo a sumar se guarda en la tabla y el filtro es traducible a SQL (campos propios, campos de un registro
  referido o del documento padre, literales, `AND`/`OR`/`NOT`, comparaciones, `IN`, `ISBLANK`), la suma se calcula en la
  base con parámetros, sin cargar los registros ni tope.
- CA2: El resultado es el mismo que en memoria, incluidos los nulos: en el lenguaje de expresiones `=`, `<>` e `IN`
  contra un nulo son falso/verdadero (no desconocido), y así se traducen.
- CA3: Solo una expresión no traducible (funciones de usuario, rutas de dos niveles…) usa el camino en memoria, con el
  mismo tope y mensaje.

**H3 — Importar sin detener a los demás.** Como *capturista*, quiero dar de alta registros mientras otra persona importa.
- CA1: La vista previa no toca la secuencia de folios (usa folios provisionales que no se guardan).
- CA2: La importación reserva de una vez, en una transacción corta, los folios que necesita; un alta simultánea no espera
  y recibe el folio siguiente al bloque. Los registros importados conservan folios consecutivos.
- CA3: Si la importación falla, el bloque se devuelve si nadie reservó después; si alguien lo hizo, queda un hueco en la
  numeración (se documenta: preferimos no detener altas a no tener huecos en ese caso).

**H4 — Archivar campos locales es una decisión aparte.** Como *responsable de cambios*, quiero confirmar de forma
explícita que un paquete archivará campos que solo existen en este entorno.
- CA1: El diff lista aparte los campos locales que se archivarían (`localArchives`), fuera de las advertencias.
- CA2: Aprobar y aplicar un paquete con campos locales por archivar exigen `confirmArchive: true`; sin ella, 409 con la
  lista. La pantalla muestra la sección «Campos de este entorno que se archivarán» con una casilla que habilita *Aprobar*
  y *Aplicar*.
- CA3: La auditoría de aprobación y aplicación registra qué campos locales se confirmaron.

**H5 — Webhooks.** Como *integrador*, quiero que un rechazo definitivo no se reintente y que no se pueda apuntar a
direcciones internas por Teredo.
- CA1: Un 4xx distinto de 408 y 429 deja la entrega **fallida** al primer intento («El destino rechazó la entrega (4xx):
  no se reintenta»). 408, 429, 5xx y errores de red se reintentan como antes. El reintento manual sigue disponible.
- CA2: `2001::/32` (Teredo) se considera dirección interna (configuración y conexión).

**H6 — Riesgos residuales.**
- CA1: Antes de validar un alta, una edición o una transición, el motor toma el bloqueo por clave de los acumulados por
  `match` que dispararán sus automatizaciones (el mismo que toma el acumulado), en orden fijo. Varias altas simultáneas
  con la misma clave pasan las que caben, sin esperas de 10 s ni rechazos de más; también en catálogos sin folio.
- CA2: Con `RequireApproval` y `AllowDirectChanges` a la vez, el servidor lo advierte en el registro al arrancar y la
  página *Entornos* muestra un aviso permanente («Este entorno exige aprobación, pero permite cambios directos…»).
- CA3: La bitácora se sigue filtrando con la política actual (decisión abajo).

**H7 — Permisos de roles.** Decisión documentada en [ADR-0004](../../../adr/0004-seguridad-como-dato-del-entorno.md):
los roles y sus permisos son dato del entorno.

## Decisiones (CLARIFY)
Sesión sin interlocutor; se registran para revisión del dueño.
- **Moneda (1):** se convierte con el tipo de cambio **de la orden** y no con el del día, para que el costo en moneda
  base de la partida (`PrecioUnitario × TipoCambio`) sea exactamente el costo último: el comprador ve el mismo costo que
  registró la recepción. La requisición no tiene moneda: su costo estimado va en moneda base.
- **`CostoMXN` (1):** **no se renombra ni se migra.** Su etiqueta ya es «Costo unitario (moneda base)» desde Compras
  1.6.0. El nombre es contrato: lo usan la automatización `AcumularRecibido`, la seguridad por campo de 1.3.0, las
  cargas de webhooks y cualquier integración por API. Un campo nuevo duplicaría el dato y rompería esos contratos sin
  ganar nada que la etiqueta no dé. Mismo criterio que `TotalMXN` en la 010.
- **Folios (3):** reserva en bloque fuera de la transacción de la importación. Alternativas descartadas: folios fuera de
  toda transacción también en las altas normales (huecos en cada error de validación) y asignar folios al final (las
  automatizaciones y webhooks ya los leen).
- **Campos locales (4):** confirmación aparte, no conflicto: archivar es lo que pide el paquete (el modelo de origen ya
  no tiene el campo) y bloquearlo obligaría a editar el destino a mano, que es justo lo que los paquetes evitan.
- **4xx (5):** 408 (timeout del destino) y 429 (límite de solicitudes) son temporales por definición; los demás 4xx
  dicen que la petición está mal (URL, autenticación, formato) y repetirla no cambia nada.
- **Bloqueo de `match` (6a):** se extiende el bloqueo. La prueba con el código anterior mostró que Inventario estaba
  a salvo solo por accidente: el folio de los movimientos (`UPDLOCK, HOLDLOCK` sobre la secuencia) serializa sus altas
  (20 salidas simultáneas × 6 rondas, sin errores). En un catálogo **sin folio** con el mismo acumulado, 10 cargos
  simultáneos esperaron 10 s y se rechazó uno que cabía: la suma de uno esperaba la fila sin confirmar del otro, que
  esperaba la clave del primero, hasta vencer el bloqueo. Además, la 014 quita a las importaciones esa serialización.
- **`AllowDirectChanges` (6b):** solo aviso. Es configuración del servidor, que ya está fuera del alcance de un
  administrador de la app; la suite E2E la necesita.
- **Bitácora (6c):** se filtra con la política **actual**. Guardar la política de cada evento cuesta por escritura y
  la decisión correcta tampoco es obvia: si un rol perdió acceso a un catálogo, que deje de ver su historial es lo que
  se espera (mínimo privilegio); si lo ganó, verlo es coherente con poder leer los registros hoy.
- **Roles (7):** dato del entorno (ADR-0004). Cuatro ojos solo en los permisos no protege nada mientras asignar roles
  a usuarios, crear usuarios y claves de API siguen en vivo: quien administra usuarios se daría el rol que ya tiene los
  permisos. La aprobación de cambios de seguridad es una capacidad de plataforma; se planea con la jerarquía de roles (015).

## Fuera de alcance
Las brechas de plataforma quedan como specs 015+ en el [roadmap](../../roadmap.md): jerarquía de roles y equipos,
automatizaciones asíncronas y programadas, rollback de entornos, OAuth2 y OData, contabilidad y costeo, editores visuales.
