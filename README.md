# Automatic Trading

Hub de gestión de carteras algorítmicas para **MetaTrader 5** y **NinjaTrader 8**.
Centraliza el control de riesgo, la coordinación entre robots y la operativa de
varias cuentas y terminales desde una sola ventana.

**[Descargar la última versión](https://github.com/AutomaticTrading1/app/releases/latest/download/AutomaticTrading_latest.exe)** · [automatictrading.net](https://www.automatictrading.net/)

Prueba gratuita de 15 días desde la propia aplicación, sin tarjeta.

---

## Qué hace

### Control de riesgo

Pensado para cuentas de fondeo, donde el límite no se negocia.

- **Drawdown global y diario**, en porcentaje sobre la cuenta y evaluado en
  tiempo real, no al cierre del día.
- **Límites de tamaño**: máximo de lotes o contratos en total y por activo.
- **Margen**: nivel mínimo exigido y tope de margen usado.
- **Exposición por divisa**: suma lo que arriesgas en cada moneda aunque venga de
  robots, símbolos y terminales distintos.
- **Cierre preventivo.** Cierra posiciones *antes* de tocar el límite en vez de
  esperar a incumplirlo. Es la diferencia entre una cuenta de fondeo viva y una
  cuenta perdida.
- **Sistema de turnos con reintentos.** Cuando dos robots quieren entrar a la vez
  se ordenan en lugar de pisarse, con espera máxima y número de reintentos
  configurables.
- **Botón de pánico.** Cierra todo y bloquea nuevas entradas al instante.

Cada operación se valida **antes** de salir al bróker, no después.

### Vigilancia

- Balance, equidad, margen, nivel de margen, beneficio abierto y posiciones de
  todas las cuentas en un solo panel.
- Hora del bróker y ticks por minuto de cada terminal: de un vistazo se ve cuál
  está vivo y cuál se ha quedado colgado.
- **Registro de eventos** que se actualiza solo y marca en rojo lo que necesita
  tu atención, con aviso en la propia pestaña.
- Avisos en el momento de lo que suele descubrirse tarde: el botón *Algo Trading*
  apagado en MetaTrader, una segunda copia de la aplicación ya en marcha.
- Registro en fichero para soporte, en `%APPDATA%\AutomaticTrading\logs`.

### MetaTrader 5 y NinjaTrader 8 en el mismo panel

- **NinjaTrader es un terminal más, no solo una fuente de datos.** Cada una de sus
  cuentas entra en el mismo control de riesgo, en la misma cola de ejecución y en
  la misma copia de señales que las de MetaTrader, y aparece por separado.
- **Todas las cuentas de NinjaTrader a la vez**, cada una con su estado, sus
  límites y sus operaciones.
- **Integración automática.** Prepara tus EAs para conectarse al hub sin escribir
  una línea de código, con copia de seguridad del original, y decides cuáles
  gestiona y cuáles no.
- **Las estrategias de NinjaTrader no se tocan.** Las propias pueden consultar al
  hub antes de entrar; las cerradas de terceros se vigilan y se frenan igual, sin
  abrir su código.

### Filtro de noticias

- Pausa la operativa alrededor de eventos macroeconómicos, con **ventanas
  configurables antes y después** de cada noticia.
- Eliges **qué impacto filtrar**: solo alto, o también medio.
- Puede quedarse en pausar o llegar a **cerrar posiciones y borrar pendientes**
  de los símbolos afectados.
- **Solo lo afectado.** Si la noticia es de una divisa concreta, los robots de
  otros mercados siguen operando.
- **Mapeo divisa → símbolos de tu bróker**, para que un evento de USD alcance a
  tu índice o tu materia prima aunque no se llame USD.
- Dos calendarios a elegir, los dos gratuitos. Se activa por terminal.

### Filtro de régimen de mercado

- Define **alcista, bajista y rango**, y asigna qué robots pueden operar en cada
  uno.
- El régimen lo decide **el indicador que tú elijas**, no una fórmula cerrada
  nuestra.
- **Gráfico de ejemplo** para ver qué régimen está detectando antes de confiarle
  la operativa.
- Los filtros se escriben a los EAs gestionados de cada terminal.

### Copy trading

- **Local**, entre los terminales y las cuentas de tu equipo, y **Online**, entre
  cuentas propias o de terceros autorizados por internet: cifrado extremo a
  extremo y firmado, con servidor de enlace propio o conexión directa P2P.
- **Entre MetaTrader 5 y NinjaTrader 8, en los dos sentidos.** Con equivalencia
  de tamaño (lotes ↔ contratos), tabla de mapeo de símbolos entre brókers y
  traducción de stop y objetivo **por distancia**, no por precio: cada bróker
  tiene su spread y su llenado.
- Se copian también las **ampliaciones y los cierres parciales**, no solo la
  apertura y el cierre.
- **Reglas a tu medida**: filtrar por robot de origen, multiplicar o dividir el
  tamaño, **invertir la operación**, retrasar la entrada, y etiquetar la copia en
  destino con el origen (robot, ticket y terminal) para saber siempre de dónde
  vino.
- **Señales Online con directorio.** Publicas una Señal, pública o privada por
  código de invitación; quien quiera seguirla **solicita acceso y tú aceptas o
  rechazas**, uno a uno. Tu identidad es un nombre de usuario, no un número.

### Order flow real sobre MetaTrader 5

- Un CFD no publica volumen negociado ni lado agresor. El indicador de footprint
  se alimenta de la **cinta real de NinjaTrader** —precio, volumen y agresor
  calculados en el instante de cada operación— y se dibuja sobre tu gráfico de
  MT5: delta, CVD, imbalances, VWAP y perfil de volumen de sesión.
- **Arranca lleno**: pide su histórico al abrirse, con el agresor reconstruido
  operación a operación. No hay que esperar horas a que se llene.
- **Varios gráficos a la vez**: M1, M5 y M15 del mismo instrumento comparten el
  flujo de ticks, y puedes tener un instrumento en un gráfico y otro en otro.
- **Zoom que llena el gráfico**: al acercar, celdas más anchas y números
  legibles; al alejar, más contexto. Los paneles se reorganizan solos.
- La **profundidad de mercado (L2/DOM)** se comparte igual.

### Catálogo de indicadores y estrategias

- Las herramientas de Automatic Trading listas para instalar: seleccionas, eliges
  el terminal y pulsas Instalar. Vale para MetaTrader 5 (`.ex5`) y para
  NinjaTrader (`.cs`).
- **El catálogo se actualiza solo.** Cuando publicamos una herramienta nueva o
  una versión corregida aparece marcada en la lista y queda anotada en el
  Registro de Eventos. No hay que reinstalar la aplicación.
- **Manual a un clic** en las herramientas que lo traen.

### Y además

- Español e inglés. Bajo consumo de recursos.
- Soporte por correo, WhatsApp y Telegram desde el propio menú de Ayuda.

## Requisitos

- **Windows 10 u 11**, 64 bits. Se instala por usuario: no hace falta ser
  administrador.
- **Microsoft Visual C++ Redistributable 2015-2022 (x64).** El instalador lo
  comprueba y avisa si falta; sin él la aplicación no arranca aunque la
  instalación termine correctamente. En Windows 7 y Windows Server 2012 R2 hacen
  falta antes las actualizaciones KB2919355 y KB2999226.
- **MetaTrader 5** (cualquier bróker), **NinjaTrader 8**, o los dos. Ninguno es
  obligatorio si usas el otro.
- En MetaTrader 5, añadir `127.0.0.1` a la lista de URL permitidas
  (Herramientas → Opciones → Asesores Expertos). Es por terminal, y sin ello los
  EAs preparados no llegan a conectar con el hub.
- El puente con NinjaTrader necesita su **AddOn**: se instala desde la propia
  aplicación (pestaña NinjaTrader) y se compila dentro de NinjaTrader con F5.
- **El footprint con volumen real necesita un instrumento con cinta de
  operaciones** (futuros). Los CFD y el spot no publican las operaciones
  ejecutadas: ahí no hay volumen negociado ni lado agresor que compartir.
- Para el perfil de volumen histórico de NinjaTrader hace falta **Order Flow+**.
  El resto del puente —cinta, L2 y copia de señales— funciona sin él.
- **Conexión a internet**: la licencia se valida contra el servidor en cada
  arranque.

## Qué hay en este repositorio

| Contenido | Para qué |
|---|---|
| [Releases](https://github.com/AutomaticTrading1/app/releases) | El instalador de la aplicación |
| `catalog.json` y `catalog/` | Catálogo de indicadores, EAs y AddOns que la aplicación instala en tus plataformas |

La aplicación consulta el catálogo una vez al día y avisa dentro del programa
cuando hay una herramienta nueva o una versión actualizada, sin necesidad de
reinstalar nada. Cada fichero se verifica por SHA-256 antes de usarse.

## Falsos positivos de antivirus

El instalador no está firmado todavía, así que algunos antivirus marcan cada
compilación nueva como sospechosa por no conocer el fichero, no por su contenido.
Si tu antivirus lo bloquea, escríbenos y lo reportamos al fabricante; la
detección se suele retirar en un par de días.

## Soporte

- Correo: [info@automatictrading.net](mailto:info@automatictrading.net)
- Telegram: [t.me/TRADER_AT](https://t.me/TRADER_AT)
- Web: [automatictrading.net](https://www.automatictrading.net/)

## Aviso

Operar en mercados financieros conlleva riesgo de pérdida. Esta aplicación es una
herramienta de gestión y control: no garantiza resultados ni constituye
asesoramiento de inversión. El código fuente de la aplicación no es público; este
repositorio distribuye los binarios y el catálogo de herramientas.
