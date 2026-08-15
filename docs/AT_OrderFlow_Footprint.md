# Manual de usuario — AT OrderFlow Footprint

Versión: 4.32+.

Guía de **uso**: qué ves en pantalla, qué hace cada botón y para qué sirve.
No hay código aquí.

> Documentación para desarrollo (cada `input` uno a uno, cadena de datos NT8, decisiones de
> diseño, historial de versiones, cómo compilar): `guia_tecnica_AT_OrderFlow_Footprint.md`.

## 1. Qué es esto

Un **footprint / order flow**: en vez de una vela sólida, cada vela se abre y se ve,
**precio a precio**, cuánto se compró y cuánto se vendió dentro de ella. Encima lleva
paneles de contexto (perfil de volumen, estructura, señales, noticias, guard de fondeo).

Está instalado como **EA (Asesor Experto)**, no como indicador. Motivo: solo un EA puede
abrir el socket que trae el volumen real de NinjaTrader. No opera solo: el panel TRADE
opera **cuando tú pulsas**.

**Los dos modos de dato:**

| Modo | De dónde sale el volumen | Cuándo lo usas |
|---|---|---|
| `VOL_TICK` | Cuenta de ticks al alza/baja del propio broker | Siempre funciona. FTMO, CFD, forex, cripto |
| `VOL_REAL` | Volumen negociado real vía puente NinjaTrader | Solo con la app puente abierta y el instrumento compartido |

En CFDs el footprint es **sintético**: el reparto compra/venta se estima por la
dirección del tick. Sirve para leer presión relativa, no es volumen negociado real.
Con `VOL_REAL` sin puente conectado verás **ceros** — ése es el error típico.

---

## 2. Primer arranque (orden que funciona)

1. Arrastra el EA al gráfico. Permite **Trading algorítmico** (si no, no dibuja el panel TRADE).
2. Deja `VolumeSource = VOL_TICK` la primera vez. Comprueba que salen números en las celdas.
3. Elige tema con **Claro / Oscuro** (cambia también el fondo del gráfico, y se restaura al quitar el EA).
4. Ajusta la vista con **Z+ / Z− / ▲ / ▼**, y **Rst** para volver al encuadre automático.
5. Apaga lo que no mires. Cada panel encendido come ancho de gráfico.

**Si vas a usar volumen real (NinjaTrader):**

1. App AutomaticTrading abierta, instrumento compartido; para el libro además **Compartir L2 (DOM)**.
2. `127.0.0.1` en la lista de URLs permitidas de MT5.
3. `VolumeSource = VOL_REAL` y `NT8Symbol` = **raíz** del instrumento (`MNQ`, `MGC`…), no el símbolo del broker.
4. Enciende **NT8** y mira el panel: te dice si llega tape, libro y cuál es el basis.

> **Basis**: NT8 manda el futuro (MNQ) y tu gráfico es el CFD (US100.cash). Sus precios no
> coinciden, así que se corrige con un desfase suavizado. Vale para **leer agresión**;
> no lo uses como precio exacto de un SL o un TP.

---

## 3. La pantalla, por zonas

```
┌──────────────────────────────────────────────────────────────────┐
│ CABECERA: precio, % día, cajas 5M/15M/1H/4H/D1, ADR, sesgo       │
│ Menús: Paneles/Niveles/Datos L2 │ estilo de celda │ zoom │ tema  │
├───────────────────────────────────────┬──────────┬───────────────┤
│                                       │  BANDAS  │  PANELES      │
│   REJILLA FOOTPRINT                   │  T&S     │  Analyst      │
│   (una columna = una vela,            │  Heat    │  Meter        │
│    una fila = un nivel de precio)     │          │  Mini         │
│                                       │  CARRIL  │  Signal/Prop  │
├───────────────────────────────────────┤  VP/DOM  │  /Plan/Trade  │
│ TABLA por barra · histograma · CVD    │          │               │
└───────────────────────────────────────┴──────────┴───────────────┘
```

- **Cabecera** — contexto antes de mirar nada más: tendencia de las 5 temporalidades,
  cuánto rango diario queda (ADR%) y el sesgo.
- **Rejilla footprint** — el centro. Columna = vela, fila = nivel de precio. La rejilla es
  **propia**: tiene su alto de fila fijo para que los números siempre se lean, y el zoom de
  MT5 no la afecta.
- **Carril derecho** — perfil de volumen de sesión **o** el libro DOM. Uno u otro, nunca los dos.
- **Bandas** — Time & Sales y mapa de calor, solo si los enciendes.
- **Paneles** — lecturas de texto, dial, señales, plan, trading.
- **Franja inferior** — tabla por barra, histograma compra/venta, línea CVD.

### 3.1 La cabecera, en detalle

Es lo primero que se lee. De izquierda a derecha:

- **Ticker** — símbolo, precio actual y **variación del día %** (contra la apertura diaria),
  verde o rojo según signo.
- **Cajas 5M / 15M / 1H / 4H / D1** — una por temporalidad, verde ▲ o roja ▽ según si el precio
  está por encima o por debajo de su media de 21. Sirven para **no operar contra la temporalidad
  superior**.
- **HTF / M5** — el resumen de lo anterior: `HTF:BULL` o `BEAR` (lo que digan la mayoría de
  H1/H4/D1) y el sesgo de M5.
- **ADR %** — cuánto rango lleva hecho hoy comparado con la media de los últimos días.
  **Verde <70%** queda recorrido · **naranja 70-100%** se está agotando · **rojo ≥100%** hoy ya
  ha hecho más de lo normal (perseguir aquí sale caro).
- **Segunda fila, 8 cajas de contexto técnico** — verde ▲ / rojo ▽, de un vistazo:

  | Caja | Dice |
  |---|---|
  | **MA** | Precio por encima/debajo de la media 50 |
  | **STD** | Precio por encima/debajo de la media 20 |
  | **ST** | SuperTrend (10/3) al alza o a la baja |
  | **ATR** | Volatilidad expandiéndose ↑ o contrayéndose ↓ |
  | **TRD** | Pendiente de la media 50 (tendencia) |
  | **RSI** | Por encima o por debajo de 50 |
  | **MCD** | Histograma MACD positivo o negativo |
  | **CCI** | Positivo o negativo |

  Son **verificación de un vistazo, no señales**.
- **Clase de activo y celda** `[Metal · celda 0.50]` — el EA detecta solo qué tipo de activo es
  y calcula el tamaño de celda por volatilidad. Por eso funciona igual en forex, metales,
  índices o cripto sin configurarlo a mano.

---

## 4. Los botones, uno a uno

### 4.1 La columna de menús (arriba a la izquierda)

Los botones ya no van en una fila corrida. Hay **tres botones en columna** y cada uno
abre sus chips **a su derecha**:

| Menú | Qué agrupa |
|---|---|
| **Paneles** | Todo lo que es un panel o una capa de lectura |
| **Niveles** | Lo que dibuja niveles y estructura sobre el precio |
| **Datos Level 2** | Lo que **exige el puente NinjaTrader** |

Se pueden tener los tres abiertos a la vez. Un menú cerrado esconde sus chips y
**no responde a clics** en esa zona.

Verde = encendido. Un clic conmuta. Al arrancar vienen encendidos **Analyst, Meter,
VP, Table y Header**; el resto lo enciendes tú.

#### Paneles

| Botón | Qué te da | Cuándo encenderlo |
|---|---|---|
| **Analyst** | Panel de texto: tendencia, HTF, flujo, volumen B/S, POC, VA, ATR, régimen, sesgo | Casi siempre. Es el resumen |
| **Meter** | Aguja STRONG SELL ↔ STRONG BUY | Si quieres el sesgo de un vistazo |
| **Mini** | Mini-gráfico de la temporalidad, bajo el dial | Para no perder la forma general |
| **VP** | Perfil de volumen de la sesión (carril derecho) | Para saber dónde está el "valor" |
| **Table** | Vol / Delta / CumDelta / Ask / Bid por vela | Para leer la secuencia de deltas |
| **Hist** | Histograma compra/venta bajo cada vela | Opcional (apagado por defecto) |
| **CVD** | Línea de delta acumulado | Para cazar divergencias precio↔flujo |
| **Session** | Tira de sesión (Asia / Londres / NY) bajo las velas | Para situar la hora del movimiento |
| **Header** | La cabecera de contexto | Casi siempre |
| **Prop** | Monitor de reglas de la prop firm | Siempre que estés en un reto |
| **Plan** | Planificador Entry/SL/TP con R:R y lotaje | Antes de entrar |
| **Sig** | Motor de señales BUY/SELL con grado A-F y TP1-3 | Como confirmación, no como orden |
| **Stat** | Backtest interno de las señales sobre el histórico | Puntual, para saber si el Sig vale algo |
| **News** | Próximos eventos del calendario MT5 | Para no comer una noticia |
| **NT8** | Estado del puente y basis (solo en `VOL_REAL`) | Cuando algo del volumen real falla |
| **Trade** | Panel de ejecución: BUY/SELL/pendientes/CLOSE/BE/Risk± | Cuando vas a operar a mano |

#### Niveles

| Botón | Qué te da | Cuándo encenderlo |
|---|---|---|
| **Zones** | Zonas de oferta/demanda en swings con volumen | Marcar de dónde salió el movimiento |
| **Swings** | Estructura: HH / HL / LH / LL y divergencia RSI (`÷div`) | Si operas estructura |
| **BOS** | Ruptura de estructura: línea al nivel roto + etiqueta **BOS** | Confirmar cambio de carácter |
| **Sweep** | Barrido: el precio pasa el swing pero **cierra de vuelta** | Cazar trampas de liquidez |
| **FVG** | Fair Value Gaps sin rellenar | Buscar zonas de retorno |
| **SNR** | Bandas de soporte/resistencia por toques repetidos | Niveles "duros" |
| **MAs** | Medias 9/21/50 + VWAP de sesión | Contexto de tendencia |
| **Round** | Zonas de números redondos, con banda. Al encenderlo aparece debajo la fila **Paso** | Siempre. Es donde se acumulan órdenes y stops |
| **Std** | Pivotes diarios clásicos: PP, R1-R3, S1-S3 | Referencia intradía de toda la vida |
| **Fibo** | Pivotes por Fibonacci: PP, R1-R4, S1-S4 | Si trabajas con retrocesos |
| **Cam** | Pivotes Camarilla: PP, R1-R4, S1-S4 | Días de rango: R4/S4 son los de reversión |
| **Woody** | Pivotes Woodie: PP, R1-R4, S1-S4 | Variante con más peso en el cierre |
| **DeMark** | Pivotes DeMark: **solo PP, R1 y S1** | Cuando quieres una única referencia limpia |
| **Murrey** | Murrey Math: las 13 líneas de 0/8 a 8/8 (más ±1/8, ±2/8) | Marco de precio "cuadriculado" |
| **VolPiv** | Volatility pivot: **una** línea que camina con el precio | Como stop dinámico / filtro de tendencia |

**Las ocho son independientes: enciende las que uses y apaga el resto.** Con cuatro
familias a la vez el gráfico se llena de líneas y las etiquetas de la izquierda se
pisan. Cada etiqueta lleva prefijo para saber de quién es: `F`=Fibo, `C`=Camarilla,
`W`=Woodie, `D`=DeMark, `M`=Murrey, `R`=Round.

Detalle de qué mira cada una:

- **Round** — el **precio de ahora mismo**. El intervalo sale de la magnitud del
  activo (no es lo mismo un redondo en EURUSD que en BTCUSD), así que funciona en
  cualquier símbolo sin tocar nada. 20 zonas arriba y 20 abajo. **El paso lo eliges tú
  con la fila `Paso`** (ver abajo).
- **Std / Fibo / Cam / Woody / DeMark** — el **día de ayer**. Se calculan una vez y no
  se mueven en toda la sesión. DeMark es el único que además mira la **apertura** de
  ayer (por eso da solo tres líneas: es su diseño, no un fallo).
- **Murrey** — los **últimos 64 días**. Divide ese rango en octavos: el **4/8** es el
  eje (gris), 0/8 y 8/8 son los extremos, y 3/8-5/8 la zona de rango donde el precio
  pasa la mayor parte del tiempo.
- **VolPiv** — las velas de la **temporalidad que tengas puesta**. No es un nivel
  fijo: es un stop por volatilidad que persigue al precio y solo se aprieta. **Verde**
  = el precio está por encima (sesgo alcista) · **rojo** = por debajo.

#### La fila `Paso` (zonas redondas)

Con **Round** encendido aparece una fila más, justo debajo de los chips de Niveles:

```
Paso   10   20   50  [100]  200  500
```

El botón verde es el que está puesto. **Los números son el paso real de ese símbolo**,
no un porcentaje: en US100 lees `10 20 50 100 200 500`, en oro `1 2 5 10 20 50`, en
EURUSD `10p 20p 50p 100p 200p 500p` (pips). Un clic y se repinta.

El paso **no cambia al hacer zoom**: un número redondo es una referencia fija del
mercado, no algo que dependa de cómo tengas encuadrado el gráfico. Solo cambia solo si
el precio cruza una potencia de diez (US100 pasando de 99.000 a 100.000).

Regla práctica: `100` para leer el día en un índice, `20` o `10` para afinar entradas
y stops, `200`/`500` para ver solo los redondos grandes en gráficos amplios.

**BOS y Sweep miran 300 velas de historia**, no solo lo que se ve. Antes solo usaban las
columnas en pantalla y con el gráfico ampliado no aparecían nunca: no cabían el swing y su
ruptura a la vez. Ahora el nivel puede venir de más atrás; la línea arranca en el borde
izquierdo si el swing quedó fuera de la vista.

Todas las marcas (HH, LL, BOS, SWEEP, FVG, SNR, zonas) se pintan como **etiqueta con fondo
de color y texto en contraste**, y **por encima de las celdas**, para que se lean tanto en
tema claro como oscuro.

#### Datos Level 2

| Botón | Qué te da | Cuándo encenderlo |
|---|---|---|
| **T&S** | Cinta de operaciones (prints agrupados) | Con el puente conectado |
| **Heat** | Mapa de calor del libro | Con el puente y L2 compartido |
| **Bloques** | Órdenes agresivas grandes y barridos | Con el puente conectado |

Si el puente no está, aquí no hay nada que ver — por eso van aparte.

### 4.2 Estilo de celda (arriba a la derecha)

Cambia **qué número** ves dentro de cada nivel de precio. Es la decisión que más cambia la
lectura:

| Botón | Ves | Úsalo para |
|---|---|---|
| **BxA** | Dos números: bid (izq) y ask (der) | Lectura clásica de footprint. El punto de partida |
| **Delta** | Un número: ask − bid | Ver rápido qué lado ganó cada nivel |
| **Heat** | Volumen total, coloreado por intensidad | Localizar HVN (imán) y LVN (el precio pasa rápido) |
| **Prof** | Barras horizontales bid izquierda / ask derecha | Ver la forma del reparto sin leer cifras |
| **VPo** | Barra de volumen total, izquierda→derecha | Perfil de volumen dentro de la propia vela |
| **Mid** | Barra de volumen centrada en la columna | Vista simétrica, menos ruido |

Regla práctica: **BxA** para estudiar una vela, **Heat** o **Delta** para escanear muchas.

### 4.3 Zoom y tema

- **Z+ / Z−** — zoom vertical propio (el de MT5 no toca la rejilla).
- **▲ / ▼** — desplazar arriba/abajo.
- **Rst** — vuelve al encuadre automático. El botón de "he perdido la vista".
- **Claro / Oscuro** — tema completo, en caliente.

> Si al reducir desaparecen los números dentro de las celdas es intencionado: por debajo de
> cierto alto de fila no caben y solo se dibujan velas, perfil y POC.

---

## 5. Cómo se lee (la parte útil)

### 5.1 Dentro de la vela

- **Imbalance (recuadro de color)** — un nivel donde un lado aplasta al otro en diagonal.
  Tres intensidades: moderado → fuerte → extremo. **Varios apilados** = muro; es la marca
  que más se usa.
- **POC de la vela** (línea discontinua) — el nivel donde más se negoció.
- **Value Area** — la zona con el 70% del volumen. Dentro = precio aceptado.
- **Single print** (borde discontinuo) — nivel casi vacío: el precio pasó corriendo. Suele volver.
- **Absorción** (celda con borde marcado) — alguien pasivo está parando el precio en ese nivel.
  Se marca **la celda**, no la vela, y **nunca en la vela viva**: hace falta ver si el precio
  falla en continuar. Es correcto que llegue tarde — en el momento no se distingue de
  "todavía no ha subido".

### 5.2 Carril derecho — perfil de sesión

Modos: **Total** (perfil clásico) · **Delta** (qué lado domina cada nivel) · **B/S**
(reparto apilado) · **TPO** (tiempo, no volumen) · **DOM** (libro real).

**Forma P** clasifica la subasta del día:

| Forma | POC | Lectura |
|---|---|---|
| **P** | Arriba | Acumulación / cierre de cortos → sesgo alcista |
| **b** | Abajo | Liquidación de largos → sesgo bajista |
| **B** | Dos picos | Doble distribución → día de tendencia |
| **D** | Centrado | Equilibrio → rango |

El botón abre una tarjeta con las cuatro formas y la actual resaltada.

### 5.3 Con volumen real (NinjaTrader)

- **DOM** — el libro vivo, 10 niveles por lado, refresco 250 ms. Es una **foto**, no una serie.
  Arriba lleva **`LIBRO ±x.xx`**: presión del libro normalizada, con los 3 primeros niveles de
  cada lado (los lejanos se mueven para engañar). Verde/derecha = presión compradora.
  Ojo: `LIBRO` es el **libro en reposo**; el `Imb:` del Analyst es el **ejecutado**. Cosas distintas.
- **T&S** — la cinta. Prints agrupados por precio y lado; los grandes van resaltados (percentil
  del propio instrumento, se autocalibra).
- **Heat** — mapa de calor del libro: precio en vertical, tiempo en horizontal, brillo = tamaño
  en reposo. **Nace vacío** y tarda unos minutos en decir algo. Chips `<<` `>>` `Fin` para el
  histórico y un cuarto que cicla `Ambas / Calor / Burb`. Las burbujas son lo **ejecutado**
  sobre el tapiz del reposo. La ventana visible = ancho ÷ muestreo (≈3,7 min por defecto).
- **Bloques** — reconstruye **una sola orden grande** que el mercado troceó en decenas de prints:
  - **Círculo** verde/rojo = orden agresiva grande, encontró contrapartida al mismo precio.
  - **Triángulo ▲/▼** = además **barrió** varios niveles: se quedó sin contrapartida.
  - La diferencia importa: **el triángulo mueve el precio, el círculo no necesariamente**.
  - No hay marcas al arrancar: el umbral es adaptativo y necesita acumular historial.

### 5.4 Franja inferior

- **Tabla** — Vol / Delta / CumD / Ask / Bid por vela. Leer la fila **Delta** en secuencia dice más
  que cualquier panel.
- **CVD** — delta acumulado. Lo que se busca es la **divergencia**: precio hace máximo más alto,
  CVD no.

### 5.5 Panel CHART ANALYST, línea a línea

Lecturas de la última vela y del conjunto visible:

| Línea | Qué te dice |
|---|---|
| `BTCUSD M5 Día %` | Símbolo, temporalidad y variación del día |
| `Trend:` | Tendencia por medias: Alcista / Bajista / Mixto |
| `HTF:` | Cuántas temporalidades altas (H1/H4/D1) están alineadas |
| `Flujo:` | Delta de la vela actual + dirección del CVD ▲/▼ |
| `Vol:` | % comprador (B) y vendedor (S) y volumen total |
| `Imb:` | Nº de imbalances apilados ask contra bid en las últimas velas |
| `POC:` | Precio del POC de sesión y si estás por encima (ABV) o debajo (BLW) |
| `VA:` | Área de valor de sesión (del VAL al VAH) |
| `ATR:` | Volatilidad media |
| `Subasta:` | Forma del perfil P/b/B/D con su descripción |
| `Regimen(SMC):` | Tendencia alcista/bajista, mixto o rango, según la secuencia de swings |
| `Diverg:` | Última divergencia de RSI (alcista/bajista) o `no` |
| `SESGO:` | Sesgo combinado de 6 factores con su puntuación (±/6) |

Debajo, el **Aggression meter**: dos barras, **Buyer** (verde) y **Seller** (rojo), con la
presión agresiva acumulada de las últimas ~8 velas, normalizadas al lado más fuerte.

### 5.6 Zonas, niveles y marcas del gráfico

- **Zonas Oferta/Demanda** (botón **Zones**) — bandas en los swings que se hicieron con volumen
  destacado: **rojo (Supply)** en máximos, **verde (Demand)** en mínimos. Etiqueta en ambos
  extremos: `S/D <volumen> <fuerza>x`. La **fuerza `Nx`** es cuántas veces supera el volumen
  medio: **cuanto más alto, más importante la zona**.
- **Bandas SNR** — zonas donde el precio ha rebotado varias veces. Rojo por encima
  (resistencia), verde por debajo (soporte). La etiqueta `SNR x3` son los toques: más toques,
  zona más dura.
- **Estructura** (**Swings / BOS / Sweep / FVG**):
  - **HH/HL** verde = estructura alcista · **LH/LL** rojo = bajista.
  - **BOS** = el precio **cierra** más allá del último swing: ruptura de estructura.
  - **×SWEEP** = el precio **supera el swing pero cierra de vuelta**: caza de stops.
    La diferencia con BOS es exactamente ésa — BOS cierra fuera, SWEEP es mecha y vuelta.
  - **FVG** = hueco de 3 velas sin rellenar, caja translúcida que se extiende a la derecha y
    desaparece cuando el precio lo rellena.
  - **`÷div`** = divergencia de RSI: rojo si el precio hace máximo más alto y el RSI no;
    verde si el precio hace mínimo más bajo y el RSI no.
- **Pivotes diarios** (botón **Std**) — `PP` (gris), `R1/R2/R3` (rojo), `S1/S2/S3` (verde),
  calculados sobre el día anterior.
- **Otras familias de niveles** (**Round / Fibo / Cam / Woody / DeMark / Murrey / VolPiv**) —
  todas se dibujan igual: **línea discontinua** de izquierda a derecha, con la etiqueta a la
  izquierda (`prefijo + nivel + precio`). **Rojo = por encima, actúa como resistencia · verde
  = por debajo, actúa como soporte · gris = el pivote central o el eje 4/8.**
  Las de **Round** llevan además una banda tenue: el redondo no es un precio exacto, es una
  zona.
- **Todo cae en su precio real.** Desde la **v4.32** la rejilla del footprint coincide
  exactamente con la escala de precio de MT5: si pintas una línea horizontal a mano en
  29900, la zona redonda de 29900 queda justo encima. Antes el desvío crecía hacia abajo
  del gráfico (hasta ~65 puntos en US100) y afectaba a **todo** lo dibujado sobre precio
  — niveles, velas, VP/POC/VAH/VAL, estructura, medias y las líneas de plan del panel
  TRADE.
- **PDH / PDL** — máximo (naranja) y mínimo (azul) del día anterior. Objetivos de liquidez.
- **Info box** (sobre la última vela) — `Δ` delta, `V` volumen y `↓% ↑%` el reparto
  vendedor/comprador. Solo en la última vela, para no saturar.
- **Columna de lecturas** (junto al dial) — posición contra MA 9/21/50 y VWAP (UP ABOVE / DN
  BELOW), ATR, posición del POC, Delta, CumDelta y Vol B/S.

### 5.7 Los paneles de decisión

- **Signal Meter** — aguja de STRONG SELL (izquierda, rojo) a STRONG BUY (derecha, verde).
  Puntúa de −100 a +100 con 4 cosas: signo del delta de la última vela, dirección del CVD,
  imbalances ask contra bid, y precio por encima o debajo del POC de sesión.
  **Es un resumen de sesgo, no una entrada.** Puede avisar con popup (y push al móvil) al
  llegar a STRONG, una vez por vela; las alertas vienen apagadas.
- **Sig** — un setup concreto por **confluencia de 7 factores**: precio vs MA21, MA9 vs MA21,
  alineación HTF, flujo delta+CVD, precio vs POC, régimen SMC y divergencia RSI.
  El panel da estado **BUY / SELL / NEUTRAL**, **grado A-F** (A ≥85%, B ≥70%, C ≥55%, D ≥40%,
  F el resto), score `±n/7`, confianza % y niveles **Entry / SL / TP1 / TP2 / TP3**
  (SL por ATR; TP1/2/3 a 1R/2R/3R). En el gráfico solo aparece si supera el mínimo configurado.
  Es un resumen con niveles sugeridos, **no una orden**.
- **Stat** — backtest **interno** (no el Strategy Tester) del núcleo de esa señal sobre las
  velas cargadas: cuántas señales, % que llega a TP1/TP2/TP3 antes que al SL, % de SL y la
  expectativa en R. Por defecto resuelve con velas y regla conservadora (si una vela toca SL y
  TP, cuenta SL); el botón **`→tick`** lo recalcula con ticks reales. Mide el núcleo
  reproducible, así que es **una estimación**, no idéntica al Sig en vivo.
- **Prop** — Balance/Equity, progreso al objetivo, pérdida diaria usada y pérdida total usada.
  Verde con margen, naranja por debajo del 30% de margen, rojo si te has pasado.
  **Solo avisa por color; no cierra nada.**
- **Plan** — cada clic en el gráfico fija **Entry → SL → TP** en ciclo (el panel indica cuál
  toca). Sombrea el riesgo en rojo y el beneficio en verde, y da **Riesgo $**, **Beneficio**,
  **R:R** (verde ≥2, naranja ≥1, rojo <1) y **los lotes** para el riesgo % que hayas puesto en
  propiedades.
- **News** — próximos eventos del calendario económico de MT5, hasta 5, con cuenta atrás
  D:HH:MM y color por impacto (rojo alto, naranja medio, gris bajo). Filtrable por impacto
  mínimo y por las divisas de tu símbolo. Se refresca cada minuto.
- **Trade** — la ejecución real: BUY/SELL, pendientes, CLOSE, break-even, Risk ±, con líneas
  arrastrables y cajas editables.

---

## 6. Ajustes que de verdad vas a tocar

Todos están en la pestaña **Parámetros** de la ventana de propiedades del EA (clic derecho
sobre el gráfico → *Lista de asesores* → *Propiedades*, o `F7`). Aquí van con **el nombre
tal como aparece en esa lista**.

| Quiero | Ajuste en la ventana de propiedades | Valor |
|---|---|---|
| Celdas de precio más gruesas o más finas | *Semilla de step (auto por VOLATILIDAD, universal)* | Déjalo en `true` y se calcula solo en cualquier activo |
| …y afinar esa granularidad | *Celdas objetivo por ATR de barra (granularidad)* | Más celdas = rejilla más fina (12 por defecto) |
| …o fijarlo yo a mano | *Ticks/Pips Per Price Level (según StepMode)* | Solo si pones la semilla automática en `false` |
| Más o menos velas en pantalla | *Nº de velas de footprint a mostrar (pocas y anchas)* | 24 por defecto |
| Más historial reconstruido al cargar | *N velas a analizar/reconstruir desde ticks* | Sube a la par *Max Bars to Display*; si no, solo esperas más |
| Columnas más anchas | *Ancho de columna a zoom 1 (px)* | 72 por defecto |
| Filas más altas (que quepan las marcas) | *Alto de fila de precio (px)* | 14 por defecto |
| Letra más grande dentro de la celda | *Tamaño de fuente de los números* | 11 por defecto |
| Volumen real de NinjaTrader | *Volume Source (tick / real NinjaTrader)* | `VOL_REAL` |
| …y decirle qué instrumento lo alimenta | *Instrumento NT8 que alimenta este gráfico (raíz: MNQ, MGC…)* | La **raíz** del futuro, no el símbolo del broker |
| Riesgo con el que se calcula el lotaje | *Riesgo % de la cuenta para el sizing (panel TRADE)* | 1.0 por defecto |
| Límites de mi prop firm | *Objetivo de beneficio %* · *Pérdida máxima total %* · *Pérdida máxima diaria %* | Cópialos del reto |
| Que la vela viva no parpadee celda a celda | *Vela en vivo: mostrar solo cuadro Δ/V + vela* | `true` = solo el cuadro · `false` = flujo en vivo |
| Ver más tiempo de golpe en el mapa de calor | *Intervalo de cada columna (ms)* | 1000 ≈ 3,7 min · 5000 ≈ 18 min · 15000 ≈ 55 min |
| Que aparezca alguna absorción (no veo ninguna) | *Absorción: percentil del volumen por nivel* | Baja a 70 para comprobar que vive; 90 es deliberadamente raro |
| Más o menos bloques marcados | *Umbral = media + k·desviación* | 2.0 ≈ 21/min · 2.5 ≈ 11/min · 3.5 ≈ 3,6/min (por defecto) |
| Zonas redondas más separadas o más juntas | La fila **Paso** en el gráfico (no hace falta abrir propiedades) | US100: 10/20/50/**100**/200/500 |
| …y que arranque siempre con el paso que quiero | *(auto) paso: multiplicador de la base* | `x1` por defecto. Solo siembra el arranque; luego manda el botón |
| …o fijar yo el intervalo redondo | *Intervalo automatico por decada del precio* a `false` + *(manual) intervalo en pips* | 50 pips por defecto |
| Bandas redondas más anchas | *Ancho de la zona en pips* | 10 por defecto (se auto-escala con el intervalo) |
| Menos líneas de zonas redondas en pantalla | *Niveles a cada lado* | 20 por defecto |
| Murrey sobre más o menos historia | *Murrey Math: velas D1 del octavo* | 64 por defecto |
| VolPiv más pegado o más suelto | *Volatility pivot: factor ATR* | 3.0 por defecto. Menos = más pegado y más cruces |

---

## 7. Problemas comunes

| Síntoma | Causa | Solución |
|---|---|---|
| Todo a cero en las celdas | *Volume Source* en `VOL_REAL` sin puente conectado | Vuelve a `VOL_TICK`, o arranca la app NinjaTrader |
| El panel NT8 dice `no solicitado` | El libro solo se pide si el carril está en DOM o el Heat encendido | Pon el carril en DOM. No es que no compartas L2: es que no se ha pedido |
| El DOM está vacío | Falta **Compartir L2 (DOM)** en la app, o `127.0.0.1` no está permitido en MT5 | Revisa los dos |
| El mapa de calor está negro | Nace vacío | Espera. La estructura aparece según el precio recorre niveles |
| No aparecen bloques | El umbral adaptativo necesita ~30 bloques acumulados | Espera, o baja *Umbral = media + k·desviación* |
| Faltan las marcas de bloque aunque las haya | La fila mide menos de 6 px | **Z+** hasta que la celda respire |
| No hay absorción en toda la sesión | Percentil 90 es exigente | *Absorción: percentil del volumen por nivel* a 70 para verificar; luego devuélvelo |
| No veo números en las celdas | Zoom demasiado reducido, se ocultan a propósito | **Z+** o **Rst** |
| La escala salta sola | El auto-encuadre pisa el arrastre manual | Es el comportamiento; usa **Z+/Z−/▲/▼** en vez de arrastrar la escala |
| Un botón no responde | Puede haber quedado tapado por el texto de cabecera | Ensancha ventana; desde v4.21 la fila se aparta sola |
| **DeMark** solo pinta 3 líneas | Es su fórmula: solo define PP, R1 y S1 | No es un fallo. Si quieres 4 niveles usa **Fibo**, **Cam** o **Woody** |
| Las etiquetas de niveles se pisan | Varias familias encendidas con niveles a precios parecidos | Apaga las que no uses; el prefijo (`F`/`C`/`W`/`D`/`M`/`R`) dice de quién es cada una |
| **VolPiv** dibuja una sola línea | No es un nivel fijo, es un stop que camina con el precio | Correcto por diseño |
| Demasiadas zonas redondas | Paso pequeño para ese activo | Sube el paso en la fila **Paso** (100 → 200 → 500) o baja *Niveles a cada lado* |
| Un nivel o una zona no cae donde marca la escala | Bug de rejilla anterior a la **v4.32**: el desvío crecía hacia abajo del gráfico | Actualiza el `.ex5`. Comprueba con una línea horizontal tuya: debe coincidir clavada |
| Cambio algo y no se ve | Estás mirando otro terminal MT5 | El `.ex5` recompilado va a la carpeta de datos de *su* instalación; cópialo a la del terminal que tienes abierto |

---

## 8. Lo que este producto NO hace

- **No opera solo.** El panel TRADE ejecuta cuando pulsas tú.
- **El Prop Guard no cierra posiciones.** Solo colorea. La decisión es tuya.
- **Sig y Signal Meter no son entradas**, son resúmenes de confluencia.
- **El DOM sí existe** con NinjaTrader conectado (10 niveles por lado, refresco 250 ms), pero
  es una **foto del libro vivo, no una serie**: no se guarda histórico de profundidad. Si
  quieres ver el pasado del libro, ése es el trabajo del mapa de calor (**Heat**), que lo va
  acumulando mientras está encendido. **Sin puente NinjaTrader no hay DOM, ni T&S, ni Heat,
  ni Bloques** — el broker CFD no publica libro.
- **No hay resiliencia del libro**: la recuperación de profundidad ocurre en 10-150 ms y el
  puente entrega cada 250 ms. Medirlo sería inventar.
- **En CFD/OTC el volumen es proxy**, no volumen negociado.
- **El basis NT8→CFD es aproximado**: léelo como agresión, no como precio de SL/TP.

---

## 9. Rutina sugerida (5 pasos)

1. **Cabecera** — ¿a favor de qué temporalidades estoy? ¿Queda rango diario (ADR)?
2. **Carril VP** — ¿dónde está el valor? ¿POC arriba, abajo, centrado (forma P/b/B/D)?
3. **Estructura** (Swings/BOS/Sweep) — ¿tendencia o rango? ¿acaba de haber un barrido?
4. **Footprint en el nivel** — al llegar a la zona: ¿imbalances apilados a mi favor?
   ¿absorción en contra? Con volumen real: ¿círculos o triángulos?
5. **Plan** — clic Entry/SL/TP, mira el R:R y el lotaje, y decide. **News** antes de pulsar.

---

