# Verificación 024 — Navegación por módulos

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 542/542 (+20 en `NavigationTests.cs`; `AccountingTests` y `CostingPolicyTests` esperan Inventario 1.8.0) |
| E2E | 328/328 en dos corridas completas seguidas con el código final (+14 en `navegacion.spec.ts`; ajustadas las pruebas que navegaban por el menú plano, ver abajo) |
| Contra el código anterior | Regresiones de paquetes: fallan con los `modules/*.json` de `HEAD` (bc5d78d) en un *worktree* aparte. F-01 y F-02: con su corrección deshecha, la suite completa falla exactamente en esas dos pruebas (abajo) |
| Visual | 4 referencias nuevas (inicio y configuración, claro y oscuro) y 5 regeneradas (`formulario` claro/oscuro/móvil, `modelador` claro/oscuro), revisadas una por una |
| Accesibilidad | axe sin violaciones serias en el inicio, la página del módulo, el área de configuración, el diálogo de módulos del modelador y la paleta abierta |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 selector | E2E `H1 CA1 · CA2 · CA4` (tarjetas en orden declarado, implícitos, General al final, descripción y número de catálogos) |
| H1 CA2 menú del módulo | E2E `H1 CA1 · CA2 · CA4` (solo sus catálogos, documentos antes que catálogos, miga de pan) |
| H1 CA3 último módulo | E2E `H1 CA3` (se recuerda al volver al inicio; un enlace a un catálogo de otro módulo cambia el menú) |
| H1 CA4 metadato | `H1_CA4_*` (alta y actualización con la capitalización guardada, inválidos, archivar, JSON, huella 021); E2E `preparar…` (`setApp` por plan, grupo implícito) |
| H1 CA5 seguridad | `H1_CA5_solo_modulos_con_algun_catalogo_visible`; E2E `H1 CA5` (la API solo devuelve Compras con Proveedor, `?all=true` 403, sin permisos lista vacía, `/m/Inventario` no encontrado) |
| H1 CA6 paquetes y Entornos | `H1_CA6_*` (los paquetes declaran el módulo de cada grupo, cadena completa, exportar lleva el módulo, diff solo si difiere, reversión archiva lo creado y devuelve lo cambiado, instantánea anterior sin módulos); E2E `valuacion-politicas`/`valuacion-rutas-avisos` (Inventario 1.8.0), `integraciones` (Compras 1.9.0) |
| H1 CA7 asistente | `H1_CA7_el_asistente_conoce_los_modulos_y_avisa_al_archivar` |
| H2 CA1 General | `H1_CA1_modulos_efectivos…`; E2E `H2 CA1 · CA3`, `h1-modelador` (Camiones se llega por General) |
| H2 CA2 módulo al crear | E2E `H2 CA2` (`?app=` desde la página del módulo, sugerencias sin General) |
| H2 CA3 pestaña Módulos | E2E `H2 CA1 · CA3` (declarar un implícito, validación del orden, vista previa, aplicar, tarjeta con descripción) |
| H3 configuración | E2E `H3 CA1 · CA2` (engrane, cuatro grupos con sus opciones, «Volver a», sin capacidades sin engrane y `/admin` sin acceso); `asistente`, `programadas`, `entornos` (opciones solo con su capacidad) |
| H4 análisis | E2E `H4 CA1 · CA2` (reportes y tablero del módulo, `?app=`, vista general, «Análisis general» en el inicio); `interfaz` (reportes y tableros desde el menú del módulo) |
| H5 búsqueda | E2E `H5 CA1 · CA3` (Ctrl+K, sin acentos, flechas, `aria-activedescendant`, región de estado, Enter, Esc devuelve el foco) y `H5 CA2` (folio por API y paleta, 400 fuera de 2–50, sin resultados para quien no lee requisiciones) |
| H6 rutas, móvil, contraído | E2E `H6` (rutas guardadas), `H6 móvil 390 px` (inicio, menú flotante con `elementFromPoint`, paleta), `visual` (menú contraído sin desplazamiento), `multiempresa` (migas de pan de 320 a 760 px) |

## Pruebas existentes ajustadas al nuevo menú
`compras`, `inventario` y `valuacion-contable` llegan al módulo instalado por el selector (antes: el menú plano; la regresión
003 de «Configuración» empujada fuera de la pantalla ya no aplica: la configuración es otra área). `h1-modelador` llega a
Camiones por General. `asistente` y `programadas` abren el asistente y las automatizaciones por el engrane; el catálogo que
crea el asistente se comprueba con la paleta. `entornos` busca el enlace «Entornos» exacto (el módulo implícito «Entornos E2E»
también coincidía). `h5-h6-seguridad` buscaba «Cliente» en toda la página y lo encontraba en el menú plano: ahora lo busca en
la tabla de auditoría. `h3-h4-datos` sigue usando `getByLabel('Buscar')` (el buscador del listado): el botón global se llama
«Búsqueda global» para no confundirse con él.

## Contra el código anterior
| Prueba | Falla sin la corrección |
|---|---|
| `H1_CA6_los_paquetes_declaran_el_modulo_de_cada_grupo_que_crean` | con los paquetes de `HEAD`: Compras, Inventario y Contabilidad sin declarar |
| `H1_CA6_las_actualizaciones_de_los_paquetes_compilan_y_declaran_su_modulo` | *Expected* Compras, Inventario, Contabilidad; *Actual* Compras, Contabilidad, Inventario (implícitos por nombre) |
| E2E `H3 CA1 · CA2` (F-01) | `["Automatización e integraciones"]` recortado |
| E2E `H5 CA1 · CA3` (F-02) | *Expected* «Almacenes ME»; *Received* «Almacenes» (la selección regresó a la primera) |

## Hallazgos
| ID | Severidad | Hallazgo | Resolución |
|---|---|---|---|
| F-01 | Baja | El título del grupo «Automatización e integraciones» no cabía en el menú de configuración y se cortaba en el borde (visto al revisar la captura de referencia) | Los títulos de sección pasan a dos líneas con el menú expandido. Regresión: ningún título del menú de configuración desborda (`H3 CA1 · CA2`) |
| F-02 | Media | En la paleta, quien bajaba con la flecha justo después de escribir perdía la selección: al llegar la búsqueda de folios (250 ms después) volvía a la primera opción; con Enter se abría otra cosa | La opción activa se recuerda por su identificador y solo se reinicia al cambiar el texto. Regresión en `H5 CA1 · CA3` |
| F-03 | Baja (prueba) | La primera versión del selector pasaba la prueba de orden antes de que llegaran los módulos (lista vacía) y la paleta se abría antes de montar la barra superior | Las pruebas esperan la primera tarjeta y el botón de búsqueda; no es un defecto de la app (el atajo queda activo en cuanto la página monta) |
| F-04 | Media (referencias) | Las capturas de `visual.spec.ts` aún mostraban el menú plano y pasaban por la tolerancia del 1 %: ya no protegían nada | Regeneradas con `--update-snapshots=all` y revisadas una por una |

## Revisión exploratoria
Servidor aparte (:5096) sobre la base desechable `ErpAi_E2E` después de una corrida verde (nunca `ErpAi`), con el superusuario
de prueba de esa corrida.
- **Menú contraído (1024 px, oscuro):** solo íconos del módulo actual con su nombre como `title`; el encabezado del módulo
  se anuncia «Resumen de Compras»; sin desplazamiento horizontal.
- **Teclado:** Ctrl+K desde el inicio abre la paleta con el foco en el campo; «almacen» encuentra «Almacenes» (Compras),
  «Almacenes ME», el reporte «Existencias por almacén» y el módulo Inventario por su descripción; ↓ y Enter abren la opción;
  Esc cierra y devuelve el foco. Aquí apareció F-02.
- **Barra superior:** orden de tabulación Expandir menú → miga de pan (Inicio, Compras) → Búsqueda global (`aria-keyshortcuts`
  Control+K) → Cambiar de módulo → Configuración → tema → usuario.
- **Móvil 375 px, claro:** las tarjetas del selector ocupan el ancho; el menú flotante del área de configuración muestra los
  cuatro grupos con el título largo en dos líneas, encima del contenido; sin desplazamiento horizontal.
- Consola: solo el 401 esperado antes de iniciar sesión.

## Riesgos residuales
- El último módulo se recuerda por usuario en el navegador, no en el servidor: en otro equipo se empieza por el primero.
- Un `group` escrito con otra capitalización que la de su módulo cae en el mismo módulo (sin distinguir mayúsculas), pero un
  error de dedo crea un módulo implícito nuevo; el modelador lo ve en la pestaña Módulos como «Implícito».
- La búsqueda de folios recorre hasta 30 catálogos con folio por consulta (5 por catálogo, 20 en total); con muchos más
  catálogos documentales habrá que indexarla.
- Los tableros pertenecen a los módulos de sus tarjetas visibles; un tablero que mezcla módulos aparece en todos ellos.
