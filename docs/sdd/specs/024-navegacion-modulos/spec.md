# Spec 024 — Navegación por módulos

**Estado:** Implementado · **Depende de:** 001, 006, 007, 009, 011, 017 · **Origen:** pedido del dueño (2026-10-24) antes de
agregar Ventas: el menú lateral ya no cabe y mezcla operación con administración. Ventas pasa a la 025 y CFDI 4.0 a la 026.

## Problema
El menú lateral (`Layout.tsx`) pinta en una sola columna: todos los módulos (secciones plegables por el metadato `group`
de cada catálogo), una sección «Catálogos» con los catálogos sin grupo, las diez opciones de «Configuración» y «Análisis».
Con cuatro módulos instalados ya no cabe en una pantalla de 800 px, y con Ventas (025) empeora. Además:

| # | Dónde | Lo que pasa hoy |
|---|---|---|
| 1 | `EntityDef.Group` | Un texto libre: el módulo no tiene ícono, descripción ni orden; no se puede presentar como «aplicación» |
| 2 | `Layout.tsx` «Catálogos» | Los catálogos creados en el modelador sin grupo quedan sueltos, mezclados con los de todos |
| 3 | `Layout.tsx` «Configuración» | Diez opciones administrativas en el mismo menú que el trabajo diario |
| 4 | `ReportsPage`/`DashboardsPage` | Todos los reportes y tableros juntos, lejos del módulo al que pertenecen |
| 5 | — | No hay forma de saltar a un catálogo, a un documento por su folio o a una página de configuración sin navegar el menú |

Referencias: el selector de aplicaciones de Odoo, el *App Launcher* de Salesforce y las aplicaciones de Dynamics 365.

## Historias

**H1 — Selector de módulos.** Como *usuario*, quiero elegir el módulo en el que trabajo y ver en el menú solo lo suyo.
- CA1: la pantalla de inicio (`/`, también al entrar) muestra una tarjeta por módulo con ícono, nombre y descripción breve,
  en el orden declarado. Un ícono de la barra superior («Módulos») lleva a ella desde cualquier página.
- CA2: elegir un módulo abre su página (`/m/:módulo`) y deja en el menú lateral solo sus documentos y catálogos (los
  documentos con proceso primero), su análisis y el acceso a cambiar de módulo.
- CA3: se recuerda el último módulo de cada usuario: al volver a entrar, el menú lateral muestra ese módulo. Abrir un
  catálogo de otro módulo (por un enlace guardado o la búsqueda) cambia el menú a ese módulo.
- CA4 (metadatos, constitución I): un módulo se declara como metadato con la operación `setApp` (nombre, etiqueta, ícono
  Lucide, descripción, orden) y se archiva con `archiveApp`; un catálogo pertenece al módulo cuyo nombre es su `group`. Un
  módulo nuevo (Ventas, 025) aparece solo al instalar su paquete. Un `group` sin módulo declarado sigue apareciendo, como
  módulo implícito con su nombre y un ícono genérico.
- CA5 (seguridad): cada usuario ve solo los módulos con al menos un catálogo que pueda leer; el servidor filtra la lista
  (`GET /api/model/apps`) como el resto del modelo, y la página de un módulo sin catálogos visibles responde «no encontrado».
- CA6: los módulos viajan en los paquetes de Entornos (009): exportar catálogos lleva la declaración de su módulo, el diff
  la compara y la reversión (017) la devuelve a como estaba. Compras 1.9.0, Inventario 1.8.0 y Contabilidad 1.2.0 declaran
  su módulo como actualización.
- CA7: el asistente IA (011) conoce `setApp`/`archiveApp` y propone el módulo al crear catálogos.

**H2 — Catálogos sin módulo.** Como *modelador*, quiero que lo que creo sin módulo tenga un lugar, y elegir el módulo al crear.
- CA1: los catálogos sin `group` forman el módulo **General** (ícono y descripción propios; se puede declarar con
  `setApp` «General» para cambiarlos). Ya no hay sección suelta «Catálogos».
- CA2: el editor del catálogo pide el **Módulo** con sugerencias de los existentes (vacío = General) y, al crear desde un
  módulo, lo propone.
- CA3: el modelador tiene una pestaña **Módulos** para declarar, editar (etiqueta, ícono, descripción, orden) y archivar
  módulos, por el mismo Plan de Cambios con vista previa.

**H3 — Configuración como área aparte.** Como *administrador*, quiero la configuración fuera del menú de trabajo.
- CA1: un engrane en la barra superior, visible solo para quien tenga alguna capacidad administrativa, abre el área de
  configuración (`/admin`) con su propio menú lateral agrupado: *Modelo* (Modelador, Módulos, Entornos), *Seguridad*
  (Usuarios y roles, Empresas), *Automatización e integraciones* (Automatizaciones, Integraciones, Asistente IA) y
  *Auditoría*; y un enlace para volver al módulo.
- CA2: cada opción se ve solo con su capacidad, como hoy; las rutas `/admin/*` siguen protegidas en el cliente y en el
  servidor; `/admin` sin ninguna capacidad muestra «sin permiso».

**H4 — Análisis por módulo.** Como *usuario*, quiero los tableros y reportes dentro de su módulo.
- CA1: la página del módulo y su menú muestran los reportes de sus catálogos y los tableros con alguna tarjeta de sus
  catálogos (solo los que el usuario puede ver, filtrados en el servidor como hoy).
- CA2: `/reports` y `/dashboards` siguen siendo la vista general (todos los del usuario, agrupados por módulo);
  `?app=` las acota a un módulo. La pantalla de inicio enlaza la vista general.

**H5 — Búsqueda global.** Como *usuario*, quiero saltar a cualquier lugar sin recorrer menús.
- CA1: **Ctrl+K** (⌘K en Mac) o el botón «Buscar» de la barra superior abre una paleta de comandos que encuentra
  módulos, catálogos, tableros y reportes, páginas de configuración y documentos por folio.
- CA2: los documentos se buscan en el servidor (`GET /api/search`) solo en catálogos que el usuario puede leer, con su
  alcance por registro y empresa (006/010) y sin campos ocultos; el folio es el campo autonumérico.
- CA3: accesible: diálogo con foco atrapado, cuadro combinado con lista (`aria-activedescendant`), flechas, Enter, Esc
  devuelve el foco; anuncia cuántos resultados hay.

**H6 — Sin romper lo existente.** Las rutas `/c/:entity`, `/admin/*`, `/reports`, `/dashboards` siguen funcionando (enlaces
guardados); el menú contraído y el móvil (390 px) siguen sin desbordarse; claro y oscuro; solo iconos Lucide.

## Decisiones (CLARIFY)
La sesión que abrió la spec (por encargo del dueño) delegó estas decisiones; quedan registradas aquí.

- **Cómo se declara un módulo (H1 CA4).** Metadato propio, como los tableros (007): `setApp { app: { name, label, icon?,
  description?, order? } }` y `archiveApp { name }`, en `meta.App`. No sale de la cabecera de los paquetes de `modules/`
  (`name`/`icon`/`description` describen el *paquete instalable*: Costeo no crea catálogos y no es un módulo de
  navegación; un catálogo del modelador no tiene paquete). El vínculo es el `group` que ya tienen los catálogos (sin
  migrar datos ni cambiar la forma canónica de los paquetes firmados): `group` = nombre del módulo, sin distinguir
  mayúsculas. Orden: `order` (0–9999, omitido = 100) y luego la etiqueta; General al final si no se declara.
- **Paquetes (H1 CA6).** Los paquetes de `modules/` declaran su módulo con una actualización (`upgrades`), no editando el
  plan base (CLAUDE.md): Compras 1.9.0 (orden 10), Inventario 1.8.0 (20), Contabilidad 1.2.0 (30). Ventas usará el 15. En
  Entornos, exportar catálogos agrega `setApp` del módulo de cada uno si está declarado; el diff emite `setApp` cuando la
  declaración del destino difiere; el alcance del paquete incluye sus `setApp`, y la instantánea de la 017 los guarda (las
  instantáneas anteriores no tienen módulos: se tratan como vacías).
- **Catálogos sin grupo (H2).** Módulo implícito **General** (ícono `layout-grid`), y el modelador pide el módulo al crear
  con sugerencias. No se obliga a elegir: un catálogo sin módulo sigue siendo válido y los paquetes existentes no cambian.
- **Último módulo por usuario (H1 CA3).** Se guarda en el navegador por usuario (`erp.app.<id>`): es una preferencia de
  presentación, no un dato del negocio ni de seguridad; si falta o el módulo ya no es visible, se usa el primero visible.
- **Qué muestra el menú lateral.** El módulo de la página actual (el de su catálogo, reporte o el de `/m/:módulo`); en
  las demás páginas, el último módulo. En `/admin*`, el menú de configuración.
- **Tableros por módulo (H4).** Sin metadato nuevo: un tablero pertenece a los módulos de los catálogos de sus tarjetas
  visibles para el usuario (lo calcula el servidor en la lista de tableros).
- **Búsqueda (H5).** Módulos, catálogos, tableros, reportes y configuración se buscan en el cliente sobre listas que el
  servidor ya filtró (y las rutas siguen protegidas). Los documentos, en el servidor: folio (campo `autonumber` visible)
  que *contiene* el texto, de 2 a 50 caracteres, hasta 5 por catálogo y 20 en total, en catálogos con folio que el usuario
  puede leer. No busca en campos de texto libre (costaría un recorrido por catálogo; no lo pidió nadie).
- **Nombre en la interfaz.** «Módulos» en la barra superior y en el selector (lo que el dueño pidió). La página
  administrativa de paquetes conserva su ruta `/admin/modules` y su nombre «Módulos» dentro del grupo *Modelo* del área de
  configuración, donde el contexto deja claro que es instalar paquetes.

## Fuera de alcance
El módulo de Ventas (025) y CFDI 4.0 (026). Favoritos o recientes en el menú, menús anidados dentro de un módulo,
búsqueda de texto completo en registros y preferencias del usuario guardadas en el servidor.
