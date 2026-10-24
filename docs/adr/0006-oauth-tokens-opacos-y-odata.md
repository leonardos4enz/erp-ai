# ADR-0006 — OAuth2 con tokens opacos y OData de solo lectura sobre el motor

**Estado:** Aceptado · **Fecha:** 2026-10-24 · **Spec:** [018](../sdd/specs/018-oauth-odata/spec.md) ·
**Relacionado:** [ADR-0002](0002-sesion-por-cookie.md), [ADR-0005](0005-aprobacion-de-cambios-de-seguridad.md)

## Contexto
Las integraciones usan claves de API estáticas (008). La spec 018 agrega un servidor de tokens con *client credentials*
y una API OData con descubrimiento. Había que decidir el formato del token y cómo se traduce una consulta OData sin abrir
un segundo camino de lectura que se salte la seguridad por registro, campo y empresa (006, 010, 015).

## Decisión
1. **Tokens opacos, no JWT.** El token es un valor aleatorio de 256 bits (`erpat_…`); la base guarda su SHA-256, su
   vencimiento (5 a 60 minutos) y sus alcances. Cada petición ya relee la cuenta contra la base (ADR-0002 y 008), así que
   un JWT no ahorra consultas; sí obligaría a administrar y rotar llaves de firma y no permitiría revocar antes del
   vencimiento. Con tokens opacos, revocar el cliente o rotar su secreto sin gracia corta todos sus tokens al instante.
2. **El cliente es una cuenta de servicio con roles**, como la clave de API. Los alcances (`odata.read`, `data.read`,
   `data.write`) solo restringen superficies (una reja por ruta y método antes de los endpoints); dentro manda el rol. Así
   no hay un segundo modelo de permisos. Alta y ampliación de roles o alcances pasan por la aprobación del ADR-0005.
3. **OData sobre las mismas piezas del motor.** `$filter` se analiza con una gramática cerrada (descenso recursivo) y se
   traduce a SQL con propiedades de lista blanca resueltas contra lo que **el usuario** ve y valores siempre como
   parámetros; el listado reutiliza `SelectClause`, `RecordFilter` y `FieldAccess` de la API de datos. No se usa una
   biblioteca OData genérica (Microsoft.AspNetCore.OData): espera un modelo EF/IQueryable estático y traduce expresiones
   arbitrarias, justo lo que la constitución (II.1) prohíbe para identificadores que no vienen del modelo validado.
4. **Descubrimiento por usuario.** El `$metadata` (CSDL 4.0) se genera en cada petición con los catálogos que el rol lee y
   los campos que ve; un documento global revelaría campos ocultos.
5. **Lo no admitido responde 400**, nunca se ignora: un cliente que pide `$search` y recibe todo creería que filtró.

## Consecuencias
- (+) Un token filtrado sirve minutos, no meses; revocar es inmediato.
- (+) OData hereda la seguridad del motor sin duplicarla; un cambio en la 006/010/015 aplica igual a las dos APIs.
- (−) Una consulta por petición para validar el token (indexada por el hash), como ya ocurría con la clave de API.
- (−) Subconjunto de OData: sin escritura, `$batch`, rutas en `$filter`, opciones anidadas en `$expand` ni funciones más
  allá de `contains`/`startswith`/`endswith`. Se amplía en specs futuras si un cliente real lo necesita.
- (−) La tabla de tokens crece con cada emisión; se purga lo vencido hace más de un día al emitir.
