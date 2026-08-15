# Automatic Trading

Hub de gestión de carteras algorítmicas para **MetaTrader 5** y **NinjaTrader 8**.
Centraliza el control de riesgo, la coordinación entre robots y la operativa de
varias cuentas y terminales desde una sola ventana.

**[Descargar la última versión](https://github.com/AutomaticTrading1/app/releases/latest/download/AutomaticTrading_latest.exe)** · [automatictrading.net](https://www.automatictrading.net/)

Periodo de prueba gratuito de 15 días desde la propia aplicación.

---

## Qué hace

- **Gestión de riesgo avanzada.** Drawdown global y diario, límites de exposición
  por divisa. Pensado para cuentas de fondeo.
- **Coordinador multi-terminal y multi-EA.** Cola de ejecución que elimina los
  conflictos al operar con varios robots a la vez.
- **Filtro automático de noticias.** Pausa la operativa en eventos macro de alto
  impacto. Si la noticia afecta a un activo concreto, solo se pausan los robots
  afectados.
- **Filtro de régimen de mercado.** Define regímenes alcista, bajista y rango, y
  asigna qué robots operan en cada uno.
- **Monitorización en tiempo real.** Balance, equidad, margen y posiciones
  abiertas de todas las cuentas en un mismo panel.
- **Integración automática.** Prepara tus EAs para conectarse al hub sin escribir
  una línea de código, con copia de seguridad del original.
- **Botón de pánico.** Cierra todo y bloquea nuevas entradas al instante.
- **Copy trading** local y entre cuentas propias o de terceros autorizados.
- **Puente con NinjaTrader 8.** Volumen real y profundidad de mercado (L2/DOM)
  disponibles para indicadores y EAs de MetaTrader 5.
- Español e inglés. Bajo consumo de recursos.

## Requisitos

- Windows 10 u 11
- MetaTrader 5 (y opcionalmente NinjaTrader 8)
- Conexión a internet: la licencia se valida contra el servidor en cada arranque

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
