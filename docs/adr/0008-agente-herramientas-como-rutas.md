# ADR-0008 — ERP AI Studio: herramientas del agente como rutas de la API y entorno aislado en una base aparte

**Estado:** Aceptado · **Fecha:** 2026-10-27 · **Spec:** [033](../sdd/specs/033-ai-studio/spec.md)

## Contexto
El asistente de la 011 no tiene herramientas: recibe el modelo de una vez y propone un Plan de Cambios. El dueño pidió un
agente (ERP AI Studio) que investigue, compile, corrija, pruebe y entregue por pasos, y después un servidor MCP para que
cada persona conecte su Claude con exactamente sus permisos. La constitución (II.2, II.8) exige que cada endpoint declare su
permiso y que la IA no actúe con privilegios propios ni aplique sin una persona.

## Decisión
1. **Una herramienta es una ruta de la API**, ejecutada en el mismo proceso por `RouteDispatcher` con el `RequestDelegate` del
   endpoint y la identidad recargada de la persona (sello de seguridad, empresa). La capacidad no se declara en la
   herramienta: es la de la ruta (029), y la ruta aplica `RecordFilter`, `FieldAccess`, empresa y `viewer` como para el
   navegador. En el entorno real el despachador niega toda ruta que escriba; aplicar lo hace la persona por la ruta de
   siempre. Así el agente nunca tiene privilegios propios (II.8) y el servidor MCP de la 038 reutiliza el mismo catálogo.
   Desde la 034 el despachador también vuelve a aplicar los alcances de la credencial (OAuth o clave de API) con la misma
   función que `UseBearerScope`, los límites de frecuencia de la ruta y, en la copia aislada, la capacidad publicada de la
   herramienta para la persona (no la del administrador de la copia).
2. **El entorno aislado es otra base** (`<base>_<huella>_S<id>_SBX`, nombre validado) con los mismos servicios de la app
   (`ServiceBlueprint`) salvo `Db`, `IConfiguration`, `EnvironmentOptions` y los trabajos en segundo plano. El modelo se
   copia con el mismo cálculo que un paquete del 009 (`ModelDiff` → Plan de Cambios); no se copian datos, usuarios,
   empresas, claves ni destinos de webhooks. Se descartó una transacción que se revierte sobre la base real: deja bloqueos
   de esquema sobre tablas reales mientras el agente prueba y encola efectos reales dentro de la transacción.
3. **El trabajo corre en segundo plano con lease**, como las automatizaciones de la 016/021; una sesión sin trabajador
   queda interrumpida y no se repite (costaría otra vez).

## Consecuencias
- (+) Una sola regla de autorización para el navegador, el agente y el MCP; las pruebas del inventario lo comprueban.
- (+) Las pruebas del agente corren en integración con un proveedor simulado: la suite y CI no llaman a Anthropic.
- (−) El entorno aislado exige permiso `CREATE DATABASE` en el servidor; sin él, el alcance `aislado` no está disponible.
- (−) Las rutas despachadas en proceso no pasan por los limitadores de ASP.NET; desde la 034 el despachador aplica los
  mismos topes por persona con contadores propios (R-033-5 cerrado).
- (−) Con el alcance `datos`, registros del entorno viajan al proveedor y quedan en los pasos de la sesión (opt-in por sesión).
