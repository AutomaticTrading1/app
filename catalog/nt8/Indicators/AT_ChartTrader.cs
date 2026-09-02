// =============================================================================
//  AutomaticTrading  -  https://www.automatictrading.net/
//  (c) 2026 AutomaticTrading. Todos los derechos reservados.
//
//  Herramienta del catalogo oficial de la aplicacion AutomaticTrading.
//  Se distribuye como FUENTE a proposito: cualquiera que vaya a operar con
//  esto deberia poder leer antes que hace.
// =============================================================================
// =============================================================================
//  AT_ChartTrader.cs  -  Panel de operativa acoplado al Chart Trader de NT8.
// -----------------------------------------------------------------------------
//  Se acopla DENTRO del Chart Trader nativo (su grid interno "grdMain") y suma
//  lo que NT8 no trae como boton: entradas LMT / STP / STP LMT colocadas en el
//  grafico, dimensionado por riesgo, y un interruptor de operativa.
//
//  MODELO: SELECCIONAR -> CONFIGURAR -> EJECUTAR.
//  Pulsar un tipo de orden NO manda nada. Deja el panel en modo colocacion con
//  un plan completo ya sembrado (entrada, SL, TP, contratos), y a partir de ahi
//  se corrige por clic en el grafico, tecleando en las cajas, o con los botones.
//  Solo EXECUTE manda. Un unico camino al dinero, un unico sitio que vigilar.
//
//  SL Y TP SON PRECIOS, NO DISTANCIAS. Se colocan en el grafico como niveles.
//  El riesgo sale de |entrada - SL|, asi que el dimensionado por riesgo no se
//  muerde la cola con un SL expresado en dinero (que ES el riesgo).
//
//  BID/ASK NO ESTAN a proposito: NT8 ya los trae nativos justo encima
//  (btnBuyBid, btnBuyAsk, btnSellBid, btnSellAsk), y una LMT con la entrada
//  colocable hace lo mismo ensenando el precio en vez de elegirlo por dentro.
//
//  LAS ORDENES VAN POR Account.CreateOrder/Submit, NO POR ATM. El SL y el TP se
//  mandan como par OCO cuando la entrada se LLENA, no antes: un stop enviado
//  antes del fill saltaria de inmediato.
//
//  INTERRUPTOR DE OPERATIVA: el boton grande de abajo. Arranca DESARMADO y
//  protege UNICAMENTE a EXECUTE. "Cancelar" y "Cerrar y cancelar" funcionan
//  siempre: reducen riesgo, y bloquearlas dejaria a alguien desarmado sin poder
//  cerrar una posicion abierta.
//
//  INSTALACION
//    1. Copiar a Documents\NinjaTrader 8\bin\Custom\Indicators\.
//    2. En NT8: New -> NinjaScript Editor -> F5 (compilar).
//    3. Activar el Chart Trader del grafico (clic derecho -> Chart Trader).
//    4. Clic derecho en el grafico -> Indicators... -> AT Chart Trader.
//
//  Sin gate de licencia: es un panel de ordenes, no lleva modelo dentro que
//  proteger. Ver la nota de documentation/CHANGELOG.md.
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
// Controles de entrada propios de NT8. Alias para no arrastrar todo
// NinjaTrader.Gui.Tools al espacio de nombres.
using PriceBox = NinjaTrader.Gui.Tools.PriceUpDown;
using QtyBox   = NinjaTrader.Gui.Tools.QuantityUpDown;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class AT_ChartTrader : Indicator
    {
        // Product ID del vendor dashboard de NinjaTrader. Somos vendor oficial,
        // asi que el gate es el NATIVO de la plataforma y no hay que portar el
        // token AT1 de las herramientas MT5 a C#.
        //
        // Con 0 NO hay comprobacion: se deja asi hasta pegar aqui el Product ID
        // real. Prefiero que este visiblemente desactivado a inventarme un
        // numero que bloquee a todo el mundo, incluido el dueno.
        // static readonly y no const: siendo const a 0, el compilador pliega las
        // dos ramas y avisa de codigo inaccesible. Tiene razon (hoy el gate esta
        // apagado), pero el aviso taparia otros de verdad.
        private static readonly long VendorProductId = 0;

        public AT_ChartTrader()
        {
            if (VendorProductId != 0) VendorLicense(VendorProductId, null);
        }

        // El gate se comprueba A MANO con VerifyVendorLicense y no se confia en
        // que la plataforma nos apague sola.
        //
        // Sin licencia, NinjaTrader deja de llamar a OnBarUpdate() y a Plot().
        // Ese es TODO su castigo, y este panel no cuelga de OnBarUpdate: se monta
        // desde State.Historical y vive en un DispatcherTimer. Sin esta
        // comprobacion explicita, el panel se montaria igual y se podria operar
        // con el sin licencia.
        //
        // Aviso honesto: distribuido como FUENTE, estas lineas se borran en diez
        // segundos. El gate solo muerde de verdad exportando como assembly
        // compilada (Tools > Export > NinjaScript, "Export as compiled
        // assembly"). Aqui esta la estructura correcta, lista para ese dia.
        private bool HasLicense()
        {
            if (VendorProductId == 0) return true;
            try { return VerifyVendorLicense(); }
            catch { return false; }
        }

        // TODOS congelados. Un SolidColorBrush es un Freezable y, sin congelar,
        // queda atado al hilo que ejecute el inicializador estatico de la clase
        // - que es el que NinjaTrader toque primero, no necesariamente el de la
        // interfaz. Al asignarlo despues a un control desde el hilo de la UI,
        // WPF lanza "No se puede usar un elemento DependencyObject que pertenezca
        // a un subproceso diferente al de su primario Freezable", y como el panel
        // se repinta en bucle, sale una ventana de error tras otra hasta tener
        // que matar NinjaTrader. Congelado es inmutable y se comparte entre
        // hilos sin problema.
        private static readonly Brush BuyBrush   = Frozen(0x1B, 0x7F, 0x3B);
        private static readonly Brush SellBrush  = Frozen(0xA8, 0x2A, 0x2A);
        private static readonly Brush FlatBrush  = Frozen(0xC8, 0x7A, 0x1E);
        private static readonly Brush WarnBrush  = Frozen(0xEF, 0x9F, 0x27);
        private static readonly Brush SafeBrush  = Frozen(0x55, 0x55, 0x55);
        private static readonly Brush OnBrush    = Frozen(0x2E, 0x6D, 0xA4);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // Un pincel que venga de fuera (del tema de NinjaTrader, o del arbol
        // visual) puede no estar congelado y pertenecer a otro hilo. Se devuelve
        // una copia congelada para que se pueda usar desde cualquiera.
        private static Brush Shareable(Brush brush)
        {
            if (brush == null) return null;
            if (brush.IsFrozen) return brush;
            try
            {
                Brush copy = brush.CloneCurrentValue();
                if (copy.CanFreeze) copy.Freeze();
                return copy;
            }
            catch { return Brushes.Gainsboro; }
        }

        //--- Tipos de entrada. BID y ASK no estan: los trae NT8 nativos y una LMT
        //--- con la entrada colocable los contiene ensenando el precio.
        private static readonly string[] Kinds = { "MKT", "LMT", "STP", "STP LMT" };

        //--- Que nivel rellena el proximo clic en el grafico.
        private enum Slot { None, Entry, Limit, Stop, Target }

        // -- ventana y contenedor ------------------------------------------------
        private Chart chartWindow;
        private Grid  chartTraderGrid;
        private ChartTrader chartTraderControl;
        private Border     frame;
        private Brush      textBrush = Brushes.Gainsboro;
        private StackPanel panel;

        // -- controles -----------------------------------------------------------
        private ComboBox  accountBox;
        private TextBlock balanceText, posText, openPlText, dayPlText, statusText, ordersText;
        private TextBlock riskRealText, contractsText, placingText, riskLabel;
        // PriceUpDown / QuantityUpDown y no TextBox: la ventana del grafico
        // captura las teclas para su buscador de instrumentos, y con un TextBox
        // corriente escribir un numero abre el buscador. Estos son los mismos
        // controles que usa el Chart Trader nativo, que si acepta teclado.
        // De regalo: parsean solos, redondean a tick y traen flechas.
        private PriceBox  entryBox, limitBox, stopBox, targetBox, rrBox, riskBox, beBox;
        private QtyBox    offsetBox, qtyBox;
        // Guarda contra la recursion: escribir Value dispara ValueChanged, que
        // volveria a entrar en RefreshPlanBoxes.
        private bool      updatingBoxes;

        // Niveles "fijados". Un nivel fijado se queda en su precio absoluto
        // cuando se mueve la entrada, en vez de acompanarla. Portado del panel
        // TRADE de AT_OrderFlow_Footprint.mq5 (g_slPinned / g_tpPinned).
        //
        // pinRR es nuestro anadido y es EXCLUYENTE con los otros dos: o mandan
        // los niveles y el ratio se deduce, o manda el ratio y el TP se deduce.
        // Las dos cosas a la vez no tienen solucion.
        private bool   pinStop, pinTarget, pinRR;
        // Lo calcula RefreshPlanBoxes (hilo de la UI) y lo lee OnRender (hilo de
        // render) para pintar los niveles apagados cuando el plan no es valido.
        private volatile bool planValid;
        private Button pinStopBtn, pinTargetBtn, pinRRBtn;
        private CheckBox useStopCheck, useTargetCheck;
        private volatile bool useStop = true, useTarget = true;
        private readonly Dictionary<Slot, Button> slotLabels = new Dictionary<Slot, Button>();
        private Grid      limitRow;
        private Button    pctBtn, moneyBtn, autoBtn, manualBtn, execButton, armButton;
        private CheckBox  beCheck;
        private ComboBox  beUnit;
        private readonly List<Button> kindButtons = new List<Button>();
        private readonly List<Button> safeButtons = new List<Button>();

        private DispatcherTimer refreshTimer;
        private DispatcherTimer retryTimer;
        private int panelAttempts;

        // Copia plana de lo que hay tecleado en las cajas. OnRender corre en el
        // hilo de render y OnBarUpdate en el de NinjaScript: leer un TextBox o un
        // CheckBox desde ahi lanza InvalidOperationException, y como pasan en
        // cada tick la pantalla se llena de ventanas de error. La UI escribe
        // estos campos; los otros hilos solo los leen.
        private volatile bool inQtyManual;
        private volatile bool inBeEnabled;
        private double inRiskValue;
        private int    inManualQty  = 1;
        private int    inOffsetTicks;
        private double inBeTrigger;

        // ATR copiado a un campo plano en OnBarUpdate: el indexador de series no
        // se puede tocar desde el hilo de la UI.
        private ATR    atrIndicator;
        private double atrCache;
        private int    inBeUnit;

        // -- estado de cuenta ----------------------------------------------------
        private Account account;
        private bool    armed;

        // -- plan en curso -------------------------------------------------------
        private string selKind;                 // null = nada seleccionado
        private bool   selLong;
        private double pxEntry, pxLimit, pxStop, pxTarget;
        private Slot   placing = Slot.None;
        private double hoverPrice;              // precio bajo el cursor, para la vista previa
        private bool   hoverValid;
        private int    riskMode;                // 0 = % de la cuenta, 1 = dinero
        private ChartScale lastScale;           // la deja OnRender, la usa el raton
        private bool   warnedCoords;            // el aviso de coordenadas se da una vez

        // Entradas a la espera de fill, con los niveles que tenia el plan EN EL
        // MOMENTO DEL ENVIO. Si se leyeran al llegar el fill, un SL movido
        // mientras la orden viajaba se aplicaria a una entrada que se mando con
        // otro. Lo escribe el hilo de la UI y lo lee el de eventos de la cuenta.
        private readonly object pendingLock = new object();
        private readonly Dictionary<Order, Bracket> pending = new Dictionary<Order, Bracket>();

        // Stop vivo sobre el que actua el break-even. Lo escribe el hilo de
        // eventos al mandar la proteccion y lo lee OnBarUpdate.
        // Ordenes de proteccion vivas (el par SL/TP) y si hemos llegado a ver la
        // posicion abierta con ellas puestas. Sin ese segundo dato no se puede
        // distinguir "aun no ha aparecido la posicion" de "la posicion ya se
        // cerro", y cancelariamos la proteccion recien enviada.
        // Las protecciones se llevan por PARES OCO, no como ordenes sueltas. Hace
        // falta porque ahora la cobertura puede ser PARCIAL: se puede tener 6
        // contratos sin proteger y anadir 3 con SL y TP. Al recortar hay que
        // tocar las dos patas del mismo par a la vez, o se queda una pata
        // huerfana sin su OCO.
        private sealed class ProtPair
        {
            public Order Stop;      // puede ser null si solo se puso TP
            public Order Target;    // puede ser null si solo se puso SL
            public int   Quantity;
        }

        private readonly List<ProtPair> protectionPairs = new List<ProtPair>();
        private bool sawPosition;

        private sealed class Bracket
        {
            public bool   IsLong;
            public double StopPrice;      // 0 = sin SL
            public double TargetPrice;    // 0 = sin TP
        }

        #region Parametros

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Dif. en ticks", Order = 1, GroupName = "Operativa",
                 Description = "Semilla de la entrada al seleccionar LMT/STP/STP LMT: la entrada arranca en el mercado más o menos esa distancia. A partir de ahí manda el clic o la caja.")]
        public int StartOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cuenta", Order = 2, GroupName = "Operativa",
                 Description = "Nombre de la cuenta a preseleccionar (por ejemplo Sim101). Vacío = la primera de la lista.")]
        public string PreferredAccount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Confirmar antes de mandar", Order = 3, GroupName = "Operativa",
                 Description = "Enseña contratos, niveles y riesgo real en un diálogo antes de enviar la orden.")]
        public bool ConfirmOrders { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 100)]
        [Display(Name = "Riesgo inicial (%)", Order = 1, GroupName = "Riesgo",
                 Description = "Porcentaje del saldo arriesgado por operacion cuando el modo de riesgo es %.")]
        public double StartRiskPct { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100)]
        [Display(Name = "R:R inicial", Order = 2, GroupName = "Riesgo",
                 Description = "Relación beneficio/riesgo con la que se siembra el TP al seleccionar un tipo de orden.")]
        public double StartRR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Desplazamiento del break-even (ticks)", Order = 1, GroupName = "Break-even",
                 Description = "Ticks a favor sobre el precio de entrada al mover el stop. Sirve para cubrir comisiones: cerrar ahi deja el neto en cero, no en negativo.")]
        public int BreakEvenOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar stop loss", Order = 1, GroupName = "Protección",
                 Description = "Estado inicial de la casilla SL. Apagada, las entradas salen sin stop, como una orden de toda la vida.")]
        public bool StartUseStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar take profit", Order = 2, GroupName = "Protección",
                 Description = "Estado inicial de la casilla TP. Apagada, las entradas salen sin objetivo.")]
        public bool StartUseTarget { get; set; }

        [NinjaScriptProperty]
        [Range(200, 700)]
        [Display(Name = "Ancho del panel (px)", Order = 5, GroupName = "Operativa",
                 Description = "Ancho fijo del panel. Fijo a propósito: si dependiera del contenido, el panel se ensancharía y estrecharía cada vez que un precio cambia de número de dígitos.")]
        public int PanelWidth { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "AT Chart Trader";
                Description = "Panel de operativa acoplado al Chart Trader: entradas MKT/LMT/STP/STP LMT colocadas en el gráfico, dimensionado por riesgo, break-even automático e interruptor de operativa.";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
                IsChartOnly = true;
                DrawOnPricePanel = true;
                IsAutoScale = false;

                // Un panel de ordenes no puede morir porque el usuario cambie de
                // pestana: se llevaria por delante la suscripcion que manda el
                // SL y el TP cuando la entrada se llena.
                IsSuspendedWhileInactive = false;

                StartOffsetTicks     = 8;
                PreferredAccount     = "Sim101";
                ConfirmOrders        = true;
                StartRiskPct         = 0.5;
                StartRR              = 2.0;
                BreakEvenOffsetTicks = 2;
                StartUseStop         = true;
                StartUseTarget       = true;
                PanelWidth           = 300;
            }
            else if (State == State.DataLoaded)
            {
                atrIndicator = ATR(14);
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(CreatePanel);
            }
            else if (State == State.Terminated)
            {
                Unsubscribe();
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(RemovePanel);
            }
        }

        protected override void OnBarUpdate()
        {
            if (atrIndicator != null && CurrentBar > 20) atrCache = atrIndicator[0];

            // El break-even NO puede correr en cada tick: entra a
            // lock(account.Positions), y con volatilidad son miles de bloqueos
            // por minuto sobre una coleccion que NinjaTrader esta escribiendo a
            // la vez. Cuatro veces por segundo sobra para mover un stop, y es la
            // misma leccion que el footprint aprendio con su revalidacion:
            // intervalos por reloj, nunca por ticks.
            long now = clock.ElapsedMilliseconds;
            if (now - lastBeCheckMs < 250) return;
            lastBeCheckMs = now;

            DoAutoBreakEven();
        }

        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private long lastBeCheckMs;
        private long lastScanMs;

        #region Vista previa en el grafico

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            lastScale = chartScale;

            if (selKind == null) return;
            if (IsInHitTest || RenderTarget == null || ChartPanel == null) return;
            if (chartScale.MaxValue <= chartScale.MinValue) return;

            float x0 = ChartPanel.X;
            float x1 = ChartPanel.X + ChartPanel.W;

            SharpDX.Direct2D1.Brush neutral = Brushes.Gainsboro.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush red     = Brushes.Firebrick.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush green   = Brushes.SeaGreen.ToDxBrush(RenderTarget);
            // Texto de la casilla: blanco sobre los colores oscuros, negro sobre
            // el claro de la entrada. Sobre Gainsboro, un texto blanco no se ve.
            SharpDX.Direct2D1.Brush onDark  = Brushes.White.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush onLight = Brushes.Black.ToDxBrush(RenderTarget);

            // Mismo idioma que el boton EXECUTE: color cuando la operacion es
            // correcta, gris apagado cuando no. Se siguen pintando, apagados, en
            // vez de desaparecer: mientras colocas necesitas ver donde estan los
            // niveles, y que se esfumaran pareceria que el panel se ha colgado.
            if (!planValid)
            {
                neutral = Brushes.DimGray.ToDxBrush(RenderTarget);
                red     = Brushes.DimGray.ToDxBrush(RenderTarget);
                green   = Brushes.DimGray.ToDxBrush(RenderTarget);
                onLight = onDark;
            }
            SharpDX.Direct2D1.StrokeStyleProperties props = new SharpDX.Direct2D1.StrokeStyleProperties();
            props.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
            SharpDX.Direct2D1.StrokeStyle dash = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, props);
            SharpDX.DirectWrite.TextFormat font = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, chartControl.Properties.LabelFont.Family.ToString(), 12f);

            try
            {
                bool minForced;
                double actualRisk;
                int contracts = ComputeContracts(out minForced, out actualRisk);
                lastLabelY = float.MinValue;

                Slot bad = OffendingSlot();

                DrawLevel(chartScale, x0, x1, EffectiveEntry(),
                          "Entrada " + Fmt(EffectiveEntry()) + (bad == Slot.Entry ? BadZoneTag : ""),
                          neutral, onLight, dash, font);
                if (selKind == "STP LMT")
                    DrawLevel(chartScale, x0, x1, pxLimit,
                              "Límite " + Fmt(pxLimit) + (bad == Slot.Limit ? BadZoneTag : ""),
                              neutral, onLight, dash, font);
                if (useStop)
                DrawLevel(chartScale, x0, x1, pxStop,
                          StopLabel("SL", pxStop, contracts, actualRisk, minForced)
                          + (bad == Slot.Stop ? BadZoneTag : ""),
                          red, onDark, dash, font);
                if (useTarget)
                DrawLevel(chartScale, x0, x1, pxTarget,
                          TargetLabel("TP", pxTarget, contracts)
                          + (bad == Slot.Target ? BadZoneTag : ""),
                          green, onDark, dash, font);

                // Linea que sigue al cursor. Cuando lo que se coloca es el SL, se
                // acompana de SUS cifras, calculadas con el precio del raton: el
                // sentido de arrastrar un stop es ver cuanto arriesgarias ahi
                // ANTES de soltar. La linea del SL ya fijado se queda con las
                // suyas, para poder comparar las dos.
                if (placing != Slot.None && hoverValid)
                {
                    string label;
                    if (placing == Slot.Stop)
                    {
                        bool hoverMin;
                        double hoverRisk;
                        int hoverContracts = ContractsFor(hoverPrice, out hoverMin, out hoverRisk);
                        label = StopLabel("SL ->", hoverPrice, hoverContracts, hoverRisk, hoverMin);
                    }
                    else if (placing == Slot.Target)
                    {
                        label = TargetLabel("TP ->", hoverPrice, contracts);
                    }
                    else
                    {
                        label = SlotName(placing) + " -> " + Fmt(hoverPrice)
                              + (IsBadZone(placing, hoverPrice) ? BadZoneTag : "");
                    }

                    bool coloured = placing == Slot.Stop || placing == Slot.Target;
                    DrawLevel(chartScale, x0, x1, hoverPrice, label,
                              placing == Slot.Stop ? red : (placing == Slot.Target ? green : neutral),
                              coloured ? onDark : onLight, dash, font);
                }
            }
            finally
            {
                // ponytail: recursos por render, no cacheados. El RenderTarget se
                // recrea al cambiar de dispositivo; cachear obliga a invalidarlos
                // a mano y no ahorra nada visible.
                font.Dispose();
                dash.Dispose();
                onLight.Dispose();
                onDark.Dispose();
                green.Dispose();
                red.Dispose();
                neutral.Dispose();
            }
        }

        // El texto va dentro de una casilla rellena del color del nivel, como
        // hace NinjaTrader con sus propias ordenes. Sin fondo, el texto se pierde
        // en cuanto cae sobre una vela o una zona clara del grafico, que es
        // justo donde suele estar el nivel que interesa.
        private void DrawLevel(ChartScale scale, float x0, float x1, double price, string label,
                               SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush onFill,
                               SharpDX.Direct2D1.StrokeStyle dash,
                               SharpDX.DirectWrite.TextFormat font)
        {
            if (price <= 0) return;
            float y = scale.GetYByValue(price);
            if (y < ChartPanel.Y || y > ChartPanel.Y + ChartPanel.H) return;

            RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), fill, 1f, dash);

            // Entrada, SL y TP caen a pocos ticks unos de otros y en un grafico
            // de 60 minutos eso son 2 o 3 pixeles: las casillas se apilarian
            // ilegibles. Se empuja hacia abajo la que llegue demasiado cerca de
            // la anterior. La LINEA no se mueve, solo su casilla.
            float ty = y - ChipHeight - 2f;
            if (lastLabelY > float.MinValue && Math.Abs(ty - lastLabelY) < ChipHeight + 2f)
                ty = lastLabelY + ChipHeight + 2f;
            lastLabelY = ty;

            using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(
                       NinjaTrader.Core.Globals.DirectWriteFactory, label, font, LabelWidth, ChipHeight))
            {
                // Ancho real del texto, para que la casilla lo ajuste y no quede
                // una barra de 320 px con el texto al fondo.
                float w = tl.Metrics.Width;
                // A la derecha, junto a la escala de precios: es donde se mira
                // para leer un nivel.
                float left = x1 - w - 2f * ChipPad - 6f;

                RenderTarget.FillRectangle(
                    new SharpDX.RectangleF(left, ty, w + 2f * ChipPad, ChipHeight), fill);
                RenderTarget.DrawTextLayout(
                    new SharpDX.Vector2(left + ChipPad, ty), tl, onFill);
            }
        }

        private const float ChipHeight = 17f;
        private const float ChipPad    = 5f;

        private float lastLabelY = float.MinValue;
        private const float LabelWidth = 320f;

        // Simetrico al del SL: contratos, dinero y ratio. El beneficio se calcula
        // con el precio que se le pasa, para poder ensenarlo tambien en la linea
        // que sigue al raton mientras se coloca.
        private string TargetLabel(string prefix, double price, int contracts)
        {
            double entry = EffectiveEntry();
            if (entry <= 0 || price <= 0 || Instrument == null)
                return prefix + " " + Fmt(price);

            double reward = contracts * Math.Abs(price - entry) * Instrument.MasterInstrument.PointValue;
            double risk   = Math.Abs(entry - pxStop);
            double rr     = risk > 0 ? Math.Abs(price - entry) / risk : 0;

            return prefix + " " + Fmt(price)
                 + "   " + contracts + "c"
                 + "   +" + reward.ToString("N2", CultureInfo.CurrentCulture)
                 + "   R:R " + rr.ToString("0.00", CultureInfo.CurrentCulture);
        }

        private string StopLabel(string prefix, double price, int contracts, double risk, bool minForced)
        {
            return prefix + " " + Fmt(price)
                 + "   " + contracts + "c"
                 + "   -" + risk.ToString("N2", CultureInfo.CurrentCulture)
                 + (minForced ? "   MIN 1" : "");
        }

        // Devuelve el UNICO nivel que esta fuera de sitio, o Slot.None. Sirve para
        // poner el cartel solo en su linea: cuando el plan no vale se apagan las
        // cuatro, pero el problema casi siempre es de una, y rotularlas todas
        // como incorrectas seria mentir en tres.
        //
        // No toca la cuenta ni ningun control, solo campos y precios, asi que se
        // puede llamar desde el hilo de render.
        private Slot OffendingSlot()
        {
            if (selKind == null || Instrument == null) return Slot.None;

            double entry  = EffectiveEntry();
            double market = LastPrice();
            if (entry <= 0 || market <= 0) return Slot.None;

            if (selKind == "LMT" && (selLong ? entry >= market : entry <= market))
                return Slot.Entry;
            if ((selKind == "STP" || selKind == "STP LMT") && (selLong ? entry <= market : entry >= market))
                return Slot.Entry;
            if (selKind == "STP LMT" && pxLimit > 0 && (selLong ? pxLimit < entry : pxLimit > entry))
                return Slot.Limit;
            if (pxStop > 0 && (selLong ? pxStop >= entry : pxStop <= entry))
                return Slot.Stop;
            if (pxTarget > 0 && (selLong ? pxTarget <= entry : pxTarget >= entry))
                return Slot.Target;

            return Slot.None;
        }

        // Version para un precio suelto, para la linea que sigue al raton: avisa
        // MIENTRAS te acercas a la zona mala, no despues de soltar.
        private bool IsBadZone(Slot slot, double price)
        {
            if (selKind == null || price <= 0) return false;
            double market = LastPrice();
            if (market <= 0) return false;

            if (slot == Slot.Entry)
            {
                if (selKind == "LMT") return selLong ? price >= market : price <= market;
                if (selKind == "STP" || selKind == "STP LMT") return selLong ? price <= market : price >= market;
                return false;
            }
            if (slot == Slot.Limit)
            {
                double entry = EffectiveEntry();
                return entry > 0 && (selLong ? price < entry : price > entry);
            }
            return false;
        }

        private const string BadZoneTag = "   ZONA INCORRECTA";

        private static string SlotName(Slot s)
        {
            if (s == Slot.Entry)  return "Entrada";
            if (s == Slot.Limit)  return "Límite";
            if (s == Slot.Stop)   return "SL";
            if (s == Slot.Target) return "TP";
            return "";
        }

        private string Fmt(double price)
        {
            return price > 0 ? Instrument.MasterInstrument.FormatPrice(price) : "-";
        }

        #endregion

        #region Construccion del panel

        private void CreatePanel()
        {
            if (panel != null || ChartControl == null) return;
            if (!HasLicense())
            {
                Log("AT Chart Trader: sin licencia de AutomaticTrading para este producto. El panel no se carga.",
                    LogLevel.Warning);
                return;
            }

            chartWindow = Window.GetWindow(ChartControl.Parent) as Chart;

            // Chart expone ChartTrader como propiedad publica. Buscarlo por
            // FindFirst("ChartWindowChartTraderControl") devuelve el contenedor
            // que lo aloja, no el control: el cast daba null y el panel no se
            // montaba nunca aun con el Chart Trader abierto.
            ChartTrader ct = chartWindow != null ? chartWindow.ChartTrader : null;
            chartTraderControl = ct;
            chartTraderGrid = ResolveChartTraderGrid(ct);
            if (chartTraderGrid == null) { ScheduleRetry(); return; }
            StopRetry();

            // Marco con cabecera: el panel se apoya directamente bajo el Chart
            // Trader nativo y sin un borde no se sabe donde acaba uno y empieza
            // el otro.
            frame = new Border
            {
                BorderBrush = OnBrush,
                BorderThickness = new Thickness(1),
                Background = ThemeBrush(),
                Margin = new Thickness(2, 6, 2, 4),
                // Ancho FIJO. Con ancho automatico, cada cifra que cambia de
                // numero de digitos (el precio, el P/L, el saldo) mueve las
                // columnas y el panel entero tiembla varias veces por segundo.
                // Fijandolo, lo que sobra se reparte dentro y nada se mueve.
                Width = PanelWidth,
                // Sin esto el marco se estira al alto de la fila y reparte los
                // controles por toda la altura disponible.
                VerticalAlignment = VerticalAlignment.Top
            };
            // Las columnas de etiqueta de todas las filas comparten ancho: se
            // miden solas segun el texto mas largo y quedan alineadas. Con
            // anchos fijos en pixeles se recortaban "Contratos" y "Money".
            Grid.SetIsSharedSizeScope(frame, true);

            // El color del texto es el MISMO pincel con el que NinjaTrader pinta
            // el texto de su grafico. Asi el panel va siempre a juego con el
            // tema, sin adivinar: si el usuario cambia a claro, cambia con el.
            // Deducirlo de la luminancia del fondo era mi apano anterior y queda
            // solo de red, para cuando ChartControl.Properties no este listo.
            //
            // TextElement.Foreground se hereda: puesto en el marco, lo cogen
            // todas las etiquetas, casillas y textos de dentro de una vez.
            textBrush = ThemeTextBrush();
            TextElement.SetForeground(frame, textBrush);

            StackPanel outer = new StackPanel();
            outer.Children.Add(BuildHeader());

            panel = new StackPanel { Margin = new Thickness(5, 4, 5, 5) };
            outer.Children.Add(panel);
            frame.Child = outer;

            panel.Children.Add(BuildAccountRow());
            panel.Children.Add(BuildInfoGrid());
            panel.Children.Add(Separator());
            panel.Children.Add(BuildOffsetRow());
            panel.Children.Add(BuildKindGrid());
            panel.Children.Add(Separator());
            // El riesgo va JUNTO a los botones de entrada: es lo que se mira
            // antes de pulsar, no despues de colocar los niveles.
            panel.Children.Add(BuildRiskRow());
            panel.Children.Add(BuildQuantityRow());
            panel.Children.Add(BuildRiskRealRow());
            panel.Children.Add(Separator());
            panel.Children.Add(BuildPlacingRow());
            panel.Children.Add(BuildPriceRow("Entrada", out entryBox, Slot.Entry));
            limitRow = (Grid)BuildPriceRow("Límite", out limitBox, Slot.Limit);
            limitRow.Visibility = Visibility.Collapsed;
            panel.Children.Add(limitRow);
            panel.Children.Add(BuildPriceRow("SL", out stopBox, Slot.Stop));
            panel.Children.Add(BuildPriceRow("TP", out targetBox, Slot.Target));
            panel.Children.Add(BuildRRRow());
            panel.Children.Add(Separator());
            panel.Children.Add(BuildBreakEvenRow());
            panel.Children.Add(Separator());
            panel.Children.Add(BuildExecuteRow());
            panel.Children.Add(BuildManageRow());
            panel.Children.Add(BuildStatusRow());
            panel.Children.Add(BuildArmRow());

            chartTraderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(frame, chartTraderGrid.RowDefinitions.Count - 1);
            Grid.SetColumn(frame, 0);
            Grid.SetColumnSpan(frame, Math.Max(1, chartTraderGrid.ColumnDefinitions.Count));
            chartTraderGrid.Children.Add(frame);

            HookChart();
            LoadAccounts();
            UpdateArmVisuals();
            RefreshPlanBoxes();

            refreshTimer = new DispatcherTimer(DispatcherPriority.Background, ChartControl.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            refreshTimer.Tick += OnRefreshTick;
            refreshTimer.Start();
        }

        // La ventana del grafico no tiene por que estar construida cuando el
        // indicador entra en State.Historical, asi que "no lo encuentro" al
        // primer intento no significa que el Chart Trader este cerrado. Se
        // reintenta 5 segundos antes de rendirse y avisar.
        private void ScheduleRetry()
        {
            if (++panelAttempts > 20)
            {
                StopRetry();
                Log("AT Chart Trader: no aparece el Chart Trader de este gráfico. Ábrelo (clic derecho -> Chart Trader) y vuelve a añadir el indicador.",
                    LogLevel.Warning);
                return;
            }
            if (retryTimer != null || ChartControl == null) return;
            retryTimer = new DispatcherTimer(DispatcherPriority.Background, ChartControl.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            retryTimer.Tick += OnRetryTick;
            retryTimer.Start();
        }

        private void OnRetryTick(object sender, EventArgs e)
        {
            CreatePanel();
        }

        private void StopRetry()
        {
            if (retryTimer == null) return;
            retryTimer.Stop();
            retryTimer.Tick -= OnRetryTick;
            retryTimer = null;
        }

        // Devuelve el grid interno del Chart Trader, o null. Nunca devuelve un
        // contenedor de fuera: colgar el panel del grid exterior de la ventana
        // lo estira sobre el grafico entero, que es el fallo que hubo antes.
        private static Grid ResolveChartTraderGrid(ChartTrader ct)
        {
            if (ct == null) return null;

            // Las tres vias salen del propio ChartTrader, asi que lo que devuelvan
            // esta dentro de el por definicion: no hace falta comprobar el arbol
            // visual, y comprobarlo seria peor, porque ese arbol no existe hasta
            // que el control se dibuja y rechazaria un grid correcto por llegar
            // pronto. El fallo de la primera version fue pasar ct.Parent, que es
            // el grid EXTERIOR de la ventana; eso ya no es posible aqui.
            Grid grid = ct.FindName("grdMain") as Grid;

            if (grid == null)
            {
                // El campo privado que respalda el x:Name. Si el XAML lo renombra
                // en una version futura, esto devuelve null y el panel no se
                // monta, que es lo correcto.
                FieldInfo f = typeof(ChartTrader).GetField("grdMain", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) grid = f.GetValue(ct) as Grid;
            }

            return grid ?? ct.Content as Grid;
        }

        private void RemovePanel()
        {
            StopRetry();
            if (refreshTimer != null)
            {
                refreshTimer.Stop();
                refreshTimer.Tick -= OnRefreshTick;
                refreshTimer = null;
            }

            UnhookChart();

            if (panel != null && chartTraderGrid != null)
            {
                chartTraderGrid.Children.Remove(frame);
                // La fila se queda: quitarla renumeraria las de los demas
                // indicadores acoplados al mismo Chart Trader.
                // ponytail: fila huerfana de alto Auto, mide 0 sin hijos.
            }

            if (accountBox != null) accountBox.SelectionChanged -= OnAccountChanged;
            if (armButton  != null) armButton.Click  -= OnArmClick;
            if (execButton != null) execButton.Click -= OnExecuteClick;
            foreach (Button b in kindButtons) b.Click -= OnKindClick;
            foreach (Button b in safeButtons) b.Click -= OnManageClick;
            kindButtons.Clear();
            safeButtons.Clear();

            panel = null;
            chartTraderGrid = null;
            chartTraderControl = null;
            chartWindow = null;
        }

        private UIElement BuildAccountRow()
        {
            Grid g = TwoColumnRow();
            g.Children.Add(Label("Cuenta", 0));
            accountBox = new ComboBox { FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
            accountBox.SelectionChanged += OnAccountChanged;
            Grid.SetColumn(accountBox, 1);
            g.Children.Add(accountBox);
            return g;
        }

        private UIElement BuildInfoGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            balanceText = InfoRow("-", 0, g, "Saldo");
            posText     = InfoRow("Plana", 1, g, "Posición");
            openPlText  = InfoRow("-", 2, g, "P/L abierto");
            dayPlText   = InfoRow("-", 3, g, "P/L realizado");
            ordersText  = InfoRow("0", 4, g, "Órdenes vivas");
            return g;
        }

        private TextBlock InfoRow(string initial, int row, Grid g, string caption)
        {
            TextBlock c = Label(caption, 0);
            Grid.SetRow(c, row);
            g.Children.Add(c);

            TextBlock v = new TextBlock
            {
                Text = initial,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = textBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 2, 1)
            };
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return v;
        }

        private UIElement BuildOffsetRow()
        {
            Grid g = TwoColumnRow();
            g.Children.Add(Label("Dif. en ticks", 0));
            offsetBox = NewQtyBox(1);
            offsetBox.Value = Math.Max(1, StartOffsetTicks);
            offsetBox.ValueChanged += OnQtyInputChanged;
            Grid.SetColumn(offsetBox, 1);
            g.Children.Add(offsetBox);
            return g;
        }

        private UIElement BuildKindGrid()
        {
            Grid g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (string k in Kinds)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock buyHead  = Head("COMPRAR");
            TextBlock sellHead = Head("VENDER");
            Grid.SetColumn(sellHead, 1);
            g.Children.Add(buyHead);
            g.Children.Add(sellHead);

            for (int r = 0; r < Kinds.Length; r++)
            {
                Button buy  = KindButton(Kinds[r], true);
                Button sell = KindButton(Kinds[r], false);
                Grid.SetRow(buy, r + 1);
                Grid.SetRow(sell, r + 1);
                Grid.SetColumn(sell, 1);
                g.Children.Add(buy);
                g.Children.Add(sell);
            }
            return g;
        }

        private Button KindButton(string kind, bool isBuy)
        {
            Button b = new Button
            {
                Content    = kind,
                Tag        = (isBuy ? "B|" : "S|") + kind,
                Background = isBuy ? BuyBrush : SellBrush,
                Foreground = Brushes.White,
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(1),
                Padding    = new Thickness(0, 3, 0, 3),
                Opacity    = 0.55
            };
            b.Click += OnKindClick;
            kindButtons.Add(b);
            return b;
        }

        private UIElement BuildPlacingRow()
        {
            placingText = new TextBlock
            {
                Text = "Sin selección",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = textBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(2, 3, 2, 3)
            };
            return placingText;
        }

        // Fila de nivel: etiqueta clicable (la convierte en objetivo del proximo
        // clic en el grafico) + caja de precio editable.
        private UIElement BuildPriceRow(string caption, out PriceBox box, Slot slot)
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(Shared(UseGroup));
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(Shared(PinGroup));

            // Casilla de encendido, solo en SL y TP. Apagada, esa proteccion no
            // se dibuja, no se valida y no se manda: la entrada sale como una
            // orden de toda la vida. Es lo que hace el panel nativo, y lo que
            // faltaba para poder usar este igual cuando no quieres bracket.
            if (slot == Slot.Stop || slot == Slot.Target)
            {
                CheckBox use = new CheckBox
                {
                    IsChecked = slot == Slot.Stop ? StartUseStop : StartUseTarget,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 0)
                };
                use.Checked   += OnUseProtectionChanged;
                use.Unchecked += OnUseProtectionChanged;
                g.Children.Add(use);
                if (slot == Slot.Stop) useStopCheck = use; else useTargetCheck = use;
            }

            Button lbl = new Button
            {
                Content = caption,
                Tag = slot,
                FontSize = 11,
                Margin = new Thickness(0, 1, 2, 1),
                Padding = new Thickness(2, 1, 2, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            lbl.Click += OnSlotLabelClick;
            slotLabels[slot] = lbl;
            Grid.SetColumn(lbl, 1);
            g.Children.Add(lbl);

            box = NewPriceBox(0, true);
            box.Tag = slot;
            box.ValueChanged += OnPriceValueChanged;
            Grid.SetColumn(box, 2);
            g.Children.Add(box);

            // Solo SL y TP se pueden fijar. La entrada es la referencia y el
            // limite del STP LMT cuelga de ella.
            if (slot == Slot.Stop || slot == Slot.Target)
            {
                Button pin = Toggle("Fijar");
                pin.Click += (slot == Slot.Stop)
                             ? new RoutedEventHandler(OnPinStopClick)
                             : new RoutedEventHandler(OnPinTargetClick);
                Grid.SetColumn(pin, 3);
                g.Children.Add(pin);
                if (slot == Slot.Stop) pinStopBtn = pin; else pinTargetBtn = pin;
            }
            return g;
        }

        private UIElement BuildRRRow()
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(Shared(PinGroup));

            g.Children.Add(Label("R:R", 0));
            rrBox = NewPriceBox(0.1, false);
            rrBox.Value = StartRR;
            rrBox.ValueChanged += OnRRValueChanged;
            Grid.SetColumn(rrBox, 1);
            g.Children.Add(rrBox);

            pinRRBtn = Toggle("Fijar");
            pinRRBtn.Click += OnPinRRClick;
            Grid.SetColumn(pinRRBtn, 2);
            g.Children.Add(pinRRBtn);
            return g;
        }

        private UIElement BuildRiskRow()
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(Shared(ToggleGroupA));
            g.ColumnDefinitions.Add(Shared(ToggleGroupB));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            riskLabel = Label("Riesgo", 0);
            g.Children.Add(riskLabel);

            pctBtn = Toggle("%");
            // Elegir en que se expresa el riesgo es decir "mando yo por riesgo",
            // asi que devuelve los contratos a Auto. Antes se podian tener % y
            // Manual encendidos a la vez: dos jefes para el mismo numero.
            pctBtn.Click += (s, e) => { riskMode = 0; inQtyManual = false; SeedRiskBox(); RefreshPlanBoxes(); };
            Grid.SetColumn(pctBtn, 1);
            g.Children.Add(pctBtn);

            moneyBtn = Toggle("Money");
            moneyBtn.Click += (s, e) => { riskMode = 1; inQtyManual = false; SeedRiskBox(); RefreshPlanBoxes(); };
            Grid.SetColumn(moneyBtn, 2);
            g.Children.Add(moneyBtn);

            riskBox = NewPriceBox(0.05, false);
            riskBox.Value = StartRiskPct;
            riskBox.Margin = new Thickness(2, 0, 0, 0);
            riskBox.ValueChanged += OnRiskValueChanged;
            Grid.SetColumn(riskBox, 3);
            g.Children.Add(riskBox);
            return g;
        }

        private UIElement BuildQuantityRow()
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(Shared(ToggleGroupA));
            g.ColumnDefinitions.Add(Shared(ToggleGroupB));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            contractsText = new TextBlock
            {
                Text = "Contratos",
                FontSize = 11,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 1, 4, 1)
            };
            g.Children.Add(contractsText);

            autoBtn = Toggle("Auto");
            autoBtn.Click += (s, e) => { inQtyManual = false; RefreshPlanBoxes(); };
            Grid.SetColumn(autoBtn, 1);
            g.Children.Add(autoBtn);

            manualBtn = Toggle("Manual");
            manualBtn.Click += (s, e) => { inQtyManual = true; RefreshPlanBoxes(); };
            Grid.SetColumn(manualBtn, 2);
            g.Children.Add(manualBtn);

            qtyBox = NewQtyBox(1);
            qtyBox.Value = 1;
            qtyBox.Margin = new Thickness(2, 0, 0, 0);
            qtyBox.ValueChanged += OnQtyInputChanged;
            Grid.SetColumn(qtyBox, 3);
            g.Children.Add(qtyBox);
            return g;
        }

        private UIElement BuildRiskRealRow()
        {
            Grid g = TwoColumnRow();
            g.Children.Add(Label("Riesgo real", 0));
            riskRealText = new TextBlock
            {
                Text = "-",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = SellBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 2, 1)
            };
            Grid.SetColumn(riskRealText, 1);
            g.Children.Add(riskRealText);
            return g;
        }

        private UIElement BuildBreakEvenRow()
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            beCheck = new CheckBox
            {
                Content = "BE",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            beCheck.Foreground = textBrush;
            beCheck.Checked   += (s2, e2) => ReadInputs();
            beCheck.Unchecked += (s2, e2) => ReadInputs();
            g.Children.Add(beCheck);

            beBox = NewPriceBox(1, false);
            // Por defecto mayor que BreakEvenOffsetTicks: si el umbral no supera
            // al desplazamiento, el stop caeria en el precio o por detras y la
            // guarda lo descarta, con lo que el break-even no dispara jamas.
            beBox.Value = Math.Max(8, BreakEvenOffsetTicks * 4);
            beBox.Margin = new Thickness(1, 0, 1, 0);
            beBox.ValueChanged += OnPriceInputChanged;
            Grid.SetColumn(beBox, 1);
            g.Children.Add(beBox);

            beUnit = new ComboBox { FontSize = 11 };
            beUnit.Items.Add("Ticks");
            beUnit.Items.Add("Money");
            beUnit.SelectedIndex = 0;
            beUnit.SelectionChanged += (s2, e2) => ReadInputs();
            Grid.SetColumn(beUnit, 2);
            g.Children.Add(beUnit);
            return g;
        }

        private UIElement BuildExecuteRow()
        {
            execButton = new Button
            {
                Content = "EXECUTE",
                Foreground = Brushes.White,
                Background = SafeBrush,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(1, 3, 1, 2),
                Padding = new Thickness(0, 5, 0, 5),
                IsEnabled = false
            };
            execButton.Click += OnExecuteClick;
            return execButton;
        }

        private UIElement BuildManageRow()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Dos acciones distintas, no una repetida: Cancelar toca las ordenes
            // en el libro y deja la posicion, Cerrar todo cierra la posicion y de
            // paso cancela lo que quede (Flatten hace las dos cosas).
            Button cancel = SafeButton("Cancelar órdenes", "CANCEL");
            g.Children.Add(cancel);

            Button flat = SafeButton("Cerrar todo", "FLAT");
            Grid.SetColumn(flat, 1);
            g.Children.Add(flat);
            return g;
        }

        // Botones que REDUCEN riesgo. No pasan por el interruptor de operativa:
        // dejar a alguien desarmado sin poder cerrar seria peor que el accidente
        // que el interruptor evita.
        private Button SafeButton(string text, string tag)
        {
            Button b = new Button
            {
                Content = text,
                Background = FlatBrush,
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(1),
                Padding = new Thickness(0, 3, 0, 3),
                Tag = tag
            };
            b.Click += OnManageClick;
            safeButtons.Add(b);
            return b;
        }

        private UIElement BuildStatusRow()
        {
            statusText = new TextBlock
            {
                Text = "Desarmado",
                FontSize = 10,
                Foreground = textBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 3, 2, 2)
            };
            // El ancho se ata al del panel. Un TextBlock mide lo que ocupa su
            // texto, y como la columna del Chart Trader se ajusta al contenido,
            // un mensaje largo ensanchaba TODO el panel durante el segundo que
            // duraba y lo devolvia al desaparecer: un temblor muy molesto.
            // Atado, el texto parte en varias lineas en vez de empujar.
            statusText.SetBinding(FrameworkElement.MaxWidthProperty,
                                  new System.Windows.Data.Binding("ActualWidth") { Source = panel });
            return statusText;
        }

        private UIElement BuildArmRow()
        {
            armButton = new Button
            {
                Content = "OPERATIVA OFF",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(1, 4, 1, 2),
                Padding = new Thickness(0, 6, 0, 6)
            };
            armButton.Click += OnArmClick;
            return armButton;
        }

        #endregion

        #region Helpers de UI

        // Ancho de etiqueta compartido por todas las filas: cada columna se mide
        // por su contenido y luego WPF le da a todas el ancho de la mas larga.
        // Asi nada se recorta y todo queda alineado, sea cual sea el ancho al
        // que el usuario deje la columna del Chart Trader.
        private const string LabelGroup  = "atLabel";
        private const string ToggleGroupA = "atToggleA";
        private const string ToggleGroupB = "atToggleB";
        private const string PinGroup      = "atPin";
        private const string UseGroup      = "atUse";

        private static ColumnDefinition Shared(string group)
        {
            return new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = group };
        }

        private static Grid TwoColumnRow()
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(Shared(UseGroup));
            g.ColumnDefinitions.Add(Shared(LabelGroup));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return g;
        }

        private UIElement BuildHeader()
        {
            Border b = new Border { Background = OnBrush, Padding = new Thickness(6, 3, 6, 3) };
            b.Child = new TextBlock
            {
                // Marca en la cabecera del panel. NO es el nombre del indicador:
                // ese sigue siendo "AT Chart Trader" (SetDefaults) porque es como
                // aparece en la lista de NinjaTrader y en las plantillas de
                // grafico ya guardadas; cambiarlo las rompe.
                Text = "AutomaticTrading.net Chart Trader",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            return b;
        }

        // Ni aqui ni en las demas etiquetas se usa Opacity para "atenuar". Sobre
        // el fondo oscuro del tema, un 75 por ciento deja el texto casi al nivel
        // del fondo y no se lee. El panel nativo tampoco atenua: pinta etiquetas
        // y valores del mismo color y marca la jerarquia poniendo el VALOR en
        // negrita, que es lo que se hace aqui.
        private TextBlock Label(string text, int column)
        {
            TextBlock t = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 1, 4, 1)
            };
            Grid.SetColumn(t, column);
            return t;
        }

        private TextBlock Head(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = textBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 1)
            };
        }

        // Padding lateral y ancho automatico: con ancho fijo "Money" y "Manual"
        // se quedaban en "Mo" y "Ma".
        // Escribe en una caja SOLO si el usuario no la esta editando. Reescribirla
        // cada refresco le mueve el cursor y le come las teclas mientras teclea:
        // el mismo fallo que ya estaba documentado en AT_OrderFlow_Footprint.mq5
        // ("reescribir en CADA frame le roba el foco al control").
        private void SetValue(PriceBox box, double value)
        {
            if (box == null || box.IsKeyboardFocusWithin) return;
            if (Math.Abs(box.Value - value) < 1e-9) return;
            updatingBoxes = true;
            try { box.Value = value; }
            finally { updatingBoxes = false; }
        }

        private void SetValue(QtyBox box, int value)
        {
            if (box == null || box.IsKeyboardFocusWithin) return;
            if (box.Value == value) return;
            updatingBoxes = true;
            try { box.Value = value; }
            finally { updatingBoxes = false; }
        }

        private PriceBox NewPriceBox(double tickSize, bool bindInstrument)
        {
            PriceBox b = new PriceBox
            {
                FontSize = 11,
                Minimum  = 0,
                // Finito a proposito. Con double.MaxValue, cualquier cuenta
                // interna del control sobre el rango se va al infinito, y eso
                // encaja con que la caja de % no se moviera de 0,50.
                Maximum  = 1000000,
                TickSize = tickSize > 0 ? tickSize : 0.01
            };
            b.PreviewMouseLeftButtonDown += (s, e) => { if (!b.IsKeyboardFocusWithin) FocusInnerTextBox(b); };
            // SOLO las cajas de precio reciben el Instrument.
            //
            // Se lo puse a todas para que no se pintaran con cinco decimales,
            // pero con Instrument asignado el control redondea el Value al tick
            // del instrumento aunque MasterInstrumentMode este apagado. La
            // prueba: la etiqueta del grafico mostraba "R:R 1,44" mientras la
            // caja ensenaba "1,50" - 0,25 es el tick del MNQ.
            //
            // En Money no se notaba porque el paso son cientos de dolares y el
            // redondeo se pierde dentro. En % el paso ronda 0,4 y el redondeo se
            // lo comia entero, dejando la caja clavada. Un control que funciona
            // vale mas que unos decimales bonitos.
            if (bindInstrument && Instrument != null)
            {
                b.Instrument           = Instrument;
                b.MasterInstrumentMode = true;
                b.TickSize             = Instrument.MasterInstrument.TickSize;
            }
            return b;
        }

        private static QtyBox NewQtyBox(int minimum)
        {
            QtyBox b = new QtyBox
            {
                FontSize           = 11,
                Minimum            = minimum,
                Maximum            = 100000,
                IsZeroValueAllowed = minimum <= 0,
                IsPopupEnabled     = false
            };
            b.PreviewMouseLeftButtonDown += (s, e) => { if (!b.IsKeyboardFocusWithin) FocusInnerTextBox(b); };
            return b;
        }

        private static Button Toggle(string text)
        {
            return new Button
            {
                Content = text,
                FontSize = 10,
                MinWidth = 40,
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(7, 2, 7, 2)
            };
        }

        // Fondo del panel. Se toma del primer ancestro que tenga uno pintado en
        // vez de una clave de recurso del tema: las claves de NT8 cambian entre
        // versiones y una clave que no existe devuelve null sin avisar, que es
        // exactamente como se acaba con un panel transparente.
        private Brush ThemeBrush()
        {
            DependencyObject node = chartTraderGrid;
            while (node != null)
            {
                Control c = node as Control;
                if (c != null && c.Background != null) return Shareable(c.Background);
                Panel p = node as Panel;
                if (p != null && p.Background != null) return Shareable(p.Background);
                Border b = node as Border;
                if (b != null && b.Background != null) return Shareable(b.Background);
                node = VisualTreeHelper.GetParent(node);
            }
            if (chartWindow != null && chartWindow.Background != null) return Shareable(chartWindow.Background);
            return Frozen(0x28, 0x28, 0x28);
        }

        private Brush ThemeTextBrush()
        {
            try
            {
                if (ChartControl != null && ChartControl.Properties != null
                 && ChartControl.Properties.ChartText != null)
                    return Shareable(ChartControl.Properties.ChartText);
            }
            catch { }
            return ContrastingText(frame != null ? frame.Background : null);
        }

        // Red de seguridad: blanco roto sobre fondo oscuro, casi negro sobre
        // fondo claro. El umbral usa luminancia percibida (el ojo pesa mucho mas
        // el verde que el azul), no la media de los tres canales.
        private static Brush ContrastingText(Brush background)
        {
            SolidColorBrush scb = background as SolidColorBrush;
            if (scb == null) return Brushes.Gainsboro;

            Color c = scb.Color;
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return lum < 0.5 ? Frozen(0xE6, 0xE6, 0xE6) : Frozen(0x1A, 0x1A, 0x1A);
        }

        private static UIElement Separator()
        {
            return new Border
            {
                Height = 1,
                Background = Brushes.Gray,
                Opacity = 0.25,
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private void Status(string text)
        {
            if (statusText == null) return;
            if (statusText.Dispatcher.CheckAccess()) statusText.Text = text;
            else statusText.Dispatcher.InvokeAsync(() => statusText.Text = text);
        }

        #endregion

        #region Cuenta y cabecera

        private void LoadAccounts()
        {
            if (accountBox == null) return;

            List<string> names;
            lock (Account.All)
                names = Account.All.Select(a => a.Name).OrderBy(n => n).ToList();

            accountBox.Items.Clear();
            foreach (string n in names) accountBox.Items.Add(n);

            // Primero la cuenta del propio Chart Trader. NinjaTrader solo dibuja en
            // el grafico las ordenes de ESA cuenta, asi que arrancar en otra hace
            // que mandes una orden, se acepte, y no la veas por ningun lado.
            int index = 0;
            string chartAccount = ChartAccountName();
            int found = -1;
            if (!string.IsNullOrEmpty(chartAccount))
                found = names.FindIndex(n => string.Equals(n, chartAccount, StringComparison.OrdinalIgnoreCase));
            if (found < 0 && !string.IsNullOrWhiteSpace(PreferredAccount))
                found = names.FindIndex(n => string.Equals(n, PreferredAccount.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found >= 0) index = found;
            if (accountBox.Items.Count > 0) accountBox.SelectedIndex = index;
        }

        private string ChartAccountName()
        {
            try
            {
                if (chartTraderControl != null && chartTraderControl.Account != null)
                    return chartTraderControl.Account.Name;
            }
            catch { }
            return null;
        }

        // Cuenta distinta a la del grafico: las ordenes existen, se aceptan y se
        // ejecutan, pero NinjaTrader no las pinta aqui ni salen en las pestanas
        // filtradas por la cuenta del grafico. Parece que no ha pasado nada.
        private bool AccountMatchesChart()
        {
            string chartAccount = ChartAccountName();
            if (string.IsNullOrEmpty(chartAccount) || account == null) return true;
            return string.Equals(chartAccount, account.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void OnAccountChanged(object sender, SelectionChangedEventArgs e)
        {
            string name = accountBox.SelectedItem as string;
            Unsubscribe();
            lock (Account.All)
                account = Account.All.FirstOrDefault(a => a.Name == name);
            if (account != null)
            {
                account.OrderUpdate    += OnAccountOrderUpdate;
                account.PositionUpdate += OnAccountPositionUpdate;
            }

            // Cambiar de cuenta con la operativa armada dejaria un EXECUTE verde
            // apuntando a una cuenta que el usuario no ha revisado.
            if (armed) SetArmed(false, "Cuenta cambiada: operativa desarmada.");
            SeedRiskBox();
            RefreshPlanBoxes();
        }

        private void Unsubscribe()
        {
            if (account != null)
            {
                account.OrderUpdate    -= OnAccountOrderUpdate;
                account.PositionUpdate -= OnAccountPositionUpdate;
            }
            lock (pendingLock)
            {
                protectionPairs.Clear();
                sawPosition = false;
            }

            int orphans;
            lock (pendingLock)
            {
                orphans = pending.Count;
                pending.Clear();
            }

            // Recargar el grafico, recompilar o cambiar de cuenta mata la
            // suscripcion. Una entrada que aun no habia llenado se quedaria sin
            // SL/TP y sin nadie escuchando. No se puede reenganchar (la orden
            // vive en la cuenta, el plan vivia aqui), asi que al menos se avisa.
            // ponytail: aviso, no persistencia; si esto pasa a menudo, guardar
            // los brackets pendientes en disco y recuperarlos en State.Historical.
            if (orphans > 0)
                Log("AT Chart Trader: " + orphans + " orden(es) de entrada siguen VIVAS en la cuenta y ya no recibirán SL/TP automático. "
                    + "Están en la pestaña Órdenes; cancélalas o protégelas a mano.",
                    // Warning y no Alert: NinjaTrader pinta los Alert como cuadro
                    // de "Error", y esto no es un fallo, es la consecuencia de
                    // quitar el indicador con una entrada esperando. Aviso, no
                    // averia. Sube a Alert si se prefiere que corte el paso.
                    LogLevel.Warning);
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            if (account == null || Instrument == null)
            {
                if (balanceText != null) balanceText.Text = "-";
                return;
            }

            try
            {
                balanceText.Text = AccountCash().ToString("N2", CultureInfo.CurrentCulture);
                dayPlText.Text   = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar).ToString("N2", CultureInfo.CurrentCulture);

                // Recorrer account.Orders y account.Positions entra a sus locks.
                // A 4 Hz eso compite con los hilos de NinjaTrader justo cuando
                // mas ocupados estan. Una vez por segundo basta para un contador
                // y para detectar una posicion cerrada por fuera.
                long nowMs = clock.ElapsedMilliseconds;
                if (nowMs - lastScanMs >= 1000)
                {
                    lastScanMs = nowMs;
                    ordersText.Text = DescribeWorkingOrders();
                    Position live = CurrentPosition();
                    ReconcileProtection(live);
                    CancelOrphanProtection(live);
                }

                Position p = positionCache;
                if (p == null)
                {
                    posText.Text = "Plana";
                    openPlText.Text = "0,00";
                }
                else
                {
                    int signed = p.MarketPosition == MarketPosition.Long ? p.Quantity : -p.Quantity;
                    posText.Text = signed.ToString("+#;-#;0", CultureInfo.CurrentCulture) + " @ " + Fmt(p.AveragePrice);

                    double last = LastPrice();
                    openPlText.Text = last > 0
                        ? p.GetUnrealizedProfitLoss(PerformanceUnit.Currency, last).ToString("N2", CultureInfo.CurrentCulture)
                        : "-";
                }

                // En MKT la entrada ES el mercado, asi que la linea lo persigue y
                // arrastra SL y TP con ella. Sin arrastrarlos, la distancia al
                // stop cambiaria sola con cada tick y el riesgo que se muestra
                // dejaria de ser el que vas a correr.
                // Sin seleccion no se llama a RefreshPlanBoxes (escribiria en la
                // linea de estado 4 veces por segundo, borrando los mensajes),
                // pero el estado de los botones SI tiene que seguir a la
                // posicion. Por eso va aparte.
                RefreshPositionUi();

                if (selKind == "MKT")
                {
                    FollowMarketEntry();
                    RefreshPlanBoxes();
                    InvalidateChart();
                }
            }
            catch (Exception ex)
            {
                Status("Error leyendo la cuenta: " + ex.Message);
            }
        }

        // Si la posicion se cierra POR FUERA (boton Cerrar del panel nativo, o
        // desde la pestana Posiciones), nadie cancela el SL y el TP: el OCO solo
        // empareja entre ellos dos. Y un stop-market de salida sin posicion no es
        // proteccion, es una ENTRADA al reves esperando a que la toquen.
        //
        // Solo se cancela despues de haber VISTO la posicion abierta con esta
        // proteccion puesta. Si no, se cancelaria el par recien enviado en el
        // instante entre el fill y que la posicion aparezca.
        // La posicion cambia por EVENTO, no cada segundo. Sin esto, entre cerrar
        // parte de la posicion y que el barrido lo notara habia hasta UN SEGUNDO
        // con la proteccion cubriendo mas contratos de los que quedan: si el stop
        // salta en esa ventana no cierra nada, ABRE en contra por la diferencia.
        private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
        {
            if (e == null || e.Position == null || Instrument == null) return;
            if (e.Position.Instrument == null
             || e.Position.Instrument.FullName != Instrument.FullName) return;

            Position p = e.MarketPosition == MarketPosition.Flat ? null : e.Position;
            ReconcileProtection(p);
            CancelOrphanProtection(p);
        }

        private static int PairQty(ProtPair p)
        {
            if (p.Stop != null && IsLive(p.Stop)) return p.Stop.Quantity;
            if (p.Target != null && IsLive(p.Target)) return p.Target.Quantity;
            return 0;
        }

        private static bool IsLive(Order o)
        {
            return o.OrderState == OrderState.Working
                || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.Submitted
                || o.OrderState == OrderState.TriggerPending;
        }

        // LA CANTIDAD DE LA PROTECCION SIGUE A LA DE LA POSICION. Es la misma
        // regla que el AddOn AutomaticTradingNT8 aprendio a base de golpes: al
        // ampliar protegia de menos, y al recortar protegia de MAS - y un
        // bracket mayor que la posicion no cierra nada cuando salta, ABRE una
        // posicion en sentido contrario.
        //
        // Se ajusta la orden viva con QuantityChanged en vez de cancelar y
        // rehacer: cancelar deja un hueco de milisegundos sin proteccion, y en
        // un camino de dinero ese hueco no compensa.
        //
        // De aqui salen gratis los tres casos: piramidar, cerrar parcial y el
        // llenado parcial. Los tres son el mismo problema.
        // LA PROTECCION NUNCA PUEDE CUBRIR MAS QUE LA POSICION. Menos si, y a
        // proposito: se pueden llevar contratos sin proteger. Pero de mas no -
        // un stop que cubre mas contratos de los que hay no cierra cuando salta,
        // ABRE en contra por la diferencia. Es lo que el AddOn documento:
        // "un bracket mayor que la posicion abre posicion en sentido contrario".
        //
        // Solo RECORTA. Ampliar seria decidir por el usuario que quiere proteger
        // lo que dejo aposta sin proteger.
        private void ReconcileProtection(Position position)
        {
            if (position == null) return;
            int target = position.Quantity;
            if (target <= 0) return;

            List<Order> change = new List<Order>();
            List<Order> cancel = new List<Order>();

            lock (pendingLock)
            {
                int covered = 0;
                foreach (ProtPair pair in protectionPairs) covered += PairQty(pair);
                if (covered <= target) return;

                // Se recorta por el final, que es el tramo mas reciente.
                int excess = covered - target;
                for (int i = protectionPairs.Count - 1; i >= 0 && excess > 0; i--)
                {
                    ProtPair pair = protectionPairs[i];
                    int q = PairQty(pair);
                    if (q <= 0) continue;

                    // Las dos patas del par se tocan a la vez: recortar una sola
                    // deja la otra cubriendo de mas y sin su OCO.
                    if (q <= excess)
                    {
                        if (pair.Stop   != null && IsLive(pair.Stop))   cancel.Add(pair.Stop);
                        if (pair.Target != null && IsLive(pair.Target)) cancel.Add(pair.Target);
                        excess -= q;
                    }
                    else
                    {
                        int left = q - excess;
                        if (pair.Stop   != null && IsLive(pair.Stop))   { pair.Stop.QuantityChanged   = left; change.Add(pair.Stop); }
                        if (pair.Target != null && IsLive(pair.Target)) { pair.Target.QuantityChanged = left; change.Add(pair.Target); }
                        excess = 0;
                    }
                }
            }

            try
            {
                if (cancel.Count > 0) account.Cancel(cancel);
                if (change.Count > 0) account.Change(change);
                Status("SL/TP recortado a " + target + "c.");
            }
            catch (Exception ex)
            {
                Status("No se pudo recortar el SL/TP: " + ex.Message
                       + ". Hazlo a mano: protege más que la posición y al saltar abre en contra.");
            }
        }

        private void CancelOrphanProtection(Position position)
        {
            List<Order> doomed = null;
            lock (pendingLock)
            {
                if (protectionPairs.Count == 0) { sawPosition = false; return; }

                if (position != null) { sawPosition = true; return; }
                if (!sawPosition) return;

                doomed = new List<Order>();
                foreach (ProtPair pair in protectionPairs)
                {
                    if (pair.Stop   != null) doomed.Add(pair.Stop);
                    if (pair.Target != null) doomed.Add(pair.Target);
                }
                protectionPairs.Clear();
                sawPosition = false;
            }

            List<Order> live = doomed.FindAll(o =>
                o.OrderState == OrderState.Working
             || o.OrderState == OrderState.Accepted
             || o.OrderState == OrderState.Submitted
             || o.OrderState == OrderState.TriggerPending);
            if (live.Count == 0) return;

            try
            {
                account.Cancel(live);
                Status("Posición cerrada por fuera: cancelado el SL/TP que quedaba suelto.");
            }
            catch (Exception ex)
            {
                Status("No se pudo cancelar el SL/TP huerfano: " + ex.Message + ". Cancélalo a mano.");
            }
        }

        // Una entrada viva NO es una posicion: no sale en la pestana Posiciones,
        // sale en Ordenes. Y su SL/TP todavia no existen, porque se mandan al
        // llenarse. Sin esto no habia forma de saberlo desde el panel, y parecia
        // que la orden se hubiera perdido.
        private string DescribeWorkingOrders()
        {
            if (account == null || Instrument == null) return "0";

            int working = 0;
            try
            {
                lock (account.Orders)
                    foreach (Order o in account.Orders)
                    {
                        if (o.Instrument == null || o.Instrument.FullName != Instrument.FullName) continue;
                        if (o.OrderState == OrderState.Working
                         || o.OrderState == OrderState.Accepted
                         || o.OrderState == OrderState.Submitted
                         || o.OrderState == OrderState.TriggerPending
                         || o.OrderState == OrderState.PartFilled)
                            working++;
                    }
            }
            catch { return "-"; }

            int waiting;
            lock (pendingLock) waiting = pending.Count;

            if (working == 0) return "0";
            return working + (waiting > 0 ? "  (" + waiting + " sin SL/TP hasta llenarse)" : "");
        }

        // Ultima posicion leida. La cabecera y el break-even trabajan sobre esta
        // copia en vez de volver a entrar al lock cada vez.
        private Position positionCache;

        private Position CurrentPosition()
        {
            if (account == null || Instrument == null) { positionCache = null; return null; }
            Position p;
            lock (account.Positions)
                p = account.Positions.FirstOrDefault(x => x.Instrument != null && x.Instrument.FullName == Instrument.FullName);
            positionCache = (p == null || p.MarketPosition == MarketPosition.Flat) ? null : p;
            return positionCache;
        }

        private double AccountCash()
        {
            try { return account.Get(AccountItem.CashValue, Currency.UsDollar); }
            catch { return 0; }
        }

        #endregion

        #region Precios de mercado

        // Sin el indexador de series (Close[0]): esto lo llaman el hilo de la UI
        // y el de render, que pueden entrar con CurrentBar < 0.
        private double LastPrice()
        {
            try
            {
                if (Instrument != null && Instrument.MarketData != null && Instrument.MarketData.Last != null)
                    return Instrument.MarketData.Last.Price;
            }
            catch { }
            if (ChartBars != null && ChartBars.Bars != null && ChartBars.Bars.Count > 0)
                return ChartBars.Bars.GetClose(ChartBars.Bars.Count - 1);
            return 0;
        }

        private double BidPrice()
        {
            try
            {
                if (Instrument != null && Instrument.MarketData != null && Instrument.MarketData.Bid != null)
                    return Instrument.MarketData.Bid.Price;
            }
            catch { }
            return LastPrice();
        }

        private double AskPrice()
        {
            try
            {
                if (Instrument != null && Instrument.MarketData != null && Instrument.MarketData.Ask != null)
                    return Instrument.MarketData.Ask.Price;
            }
            catch { }
            return LastPrice();
        }

        // Precio al que se entraria de verdad. En MKT es el mercado del lado que
        // toca; en el resto es el nivel colocado. Dimensionado, R:R, validacion y
        // ejecucion pasan todos por aqui para que no puedan discrepar.
        private double EffectiveEntry()
        {
            if (selKind == "MKT") return selLong ? AskPrice() : BidPrice();
            return pxEntry;
        }

        private double Tick()
        {
            double t = Instrument != null ? Instrument.MasterInstrument.TickSize : 0;
            return t > 0 ? t : 0.01;
        }

        private double Round(double price)
        {
            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        #endregion

        #region Plan: seleccion, siembra y sincronizacion

        private void OnKindClick(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            if (b == null) return;
            string tag = b.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;

            bool isLong = tag.StartsWith("B|");
            string kind = tag.Substring(2);

            // Con posicion abierta, MKT no abre un plan: suma o resta en el
            // acto, como el panel nativo. Ahi no hay nada que planear - el SL y
            // el TP ya existen y su cantidad sigue sola a la posicion. Obligar a
            // recolocarlos para sumar un contrato era pedir trabajo por nada.
            //
            // LMT, STP y STP LMT siguen con el plan aunque haya posicion: son
            // ordenes en espera y necesitan un precio de todas formas.
            // Instantaneo SOLO si no se quiere proteccion. Con SL o TP marcados,
            // MKT abre el plan aunque ya haya posicion: es la unica forma de
            // anadir un tramo CON su propio SL y TP. Las casillas son el
            // interruptor entre los dos modos.
            if (kind == "MKT" && !useStop && !useTarget)
            {
                Position open = CurrentPosition();
                if (open != null) { ScalePosition(isLong, open); return; }
            }

            // Volver a pulsar el mismo boton cancela la seleccion.
            if (selKind == kind && selLong == isLong) { ClearSelection("Selección cancelada."); return; }

            selKind = kind;
            selLong = isLong;
            SeedPlan();
            AdvancePlacing(true);
            RefreshPlanBoxes();
            InvalidateChart();
        }

        // Siembra un plan completo. Nunca deja un campo vacio: se puede ejecutar
        // sin tocar el grafico, y cada clic solo corrige lo que ya hay.
        // Suma o resta contratos sobre una posicion ya abierta. La cantidad es
        // la de la caja Contratos.
        private void ScalePosition(bool buySide, Position p)
        {
            // Puede AUMENTAR riesgo, asi que pasa por el interruptor. Reducir no
            // lo necesitaria, pero es el mismo boton y prefiero una regla sola a
            // que dependa de en que lado estes.
            if (!armed) { Status("Operativa desarmada. Arma para añadir o reducir."); return; }
            if (account == null || Instrument == null) return;

            bool positionIsLong = p.MarketPosition == MarketPosition.Long;
            bool adding = buySide == positionIsLong;

            int n = Math.Max(1, qtyBox != null ? qtyBox.Value : 1);
            // Reduciendo, nunca mas de lo que hay: pasarse no cierra de mas, DA
            // LA VUELTA a la posicion. El panel nativo si te deja darle la
            // vuelta sin avisar; aqui no.
            if (!adding) n = Math.Min(n, p.Quantity);
            if (n < 1) return;

            OrderAction action = positionIsLong
                                 ? (buySide ? OrderAction.Buy : OrderAction.Sell)
                                 : (buySide ? OrderAction.BuyToCover : OrderAction.SellShort);

            try
            {
                Order o = account.CreateOrder(Instrument, action, OrderType.Market, OrderEntry.Manual,
                                              TimeInForce.Day, n, 0, 0, string.Empty,
                                              adding ? "AT Sumar" : "AT Restar",
                                              Core.Globals.MaxDate, null);
                account.Submit(new[] { o });

                int after = adding ? p.Quantity + n : p.Quantity - n;
                Status((adding ? "+" : "-") + n + "  " + p.Quantity + " -> " + after + "c"
                       + (adding ? "   (el SL no se mueve: arriesgas mas)" : ""));
            }
            catch (Exception ex)
            {
                Status("Error al ajustar la posición: " + ex.Message);
            }
        }

        private void SeedPlan()
        {
            double market = selLong ? AskPrice() : BidPrice();
            if (market <= 0) market = LastPrice();
            double off = OffsetTicks() * Tick();

            if (selKind == "MKT")
                pxEntry = market;
            else if (selKind == "LMT")
                pxEntry = Round(market + (selLong ? -off : off));   // limit al lado pasivo
            else
                pxEntry = Round(market + (selLong ? off : -off));   // stop al lado de ruptura

            pxLimit = selKind == "STP LMT"
                ? Round(pxEntry + (selLong ? Tick() * 2 : -Tick() * 2))
                : 0;

            double entry = EffectiveEntry();
            double slDist = DefaultStopDistance();
            pxStop   = Round(entry + (selLong ? -slDist : slDist));
            pxTarget = Round(entry + (selLong ? slDist * StartRR : -slDist * StartRR));
        }

        // Cola de colocacion por tipo. Con reset=true empieza por el primero.
        // Orden en que los clics van rellenando el plan. El TP entra en la cola:
        // asi el primer clic pone el SL, el segundo el TP, y no hace falta
        // acertar en ninguna etiqueta para colocarlos.
        //
        // Con el R:R fijado el TP se deriva, asi que no se pide.
        private void BuildQueue()
        {
            placeQueue.Clear();
            if (selKind == "LMT" || selKind == "STP") placeQueue.Add(Slot.Entry);
            if (selKind == "STP LMT") { placeQueue.Add(Slot.Entry); placeQueue.Add(Slot.Limit); }
            if (useStop) placeQueue.Add(Slot.Stop);
            if (useTarget && !pinRR) placeQueue.Add(Slot.Target);
            queueIndex = 0;
        }

        private void AdvancePlacing(bool reset)
        {
            if (reset)
            {
                BuildQueue();
                // Con SL y TP apagados no queda nada que colocar: un MKT a pelo
                // se ejecuta sin pedir un solo clic en el grafico.
                placing = placeQueue.Count > 0 ? placeQueue[0] : Slot.None;
                return;
            }

            queueIndex++;
            // Agotada la cola no se arma nada: el plan esta puesto y un clic
            // suelto en el grafico no debe mover un nivel sin querer. Para
            // retocar se pulsa la etiqueta del nivel, que lo vuelve a armar.
            placing = queueIndex < placeQueue.Count ? placeQueue[queueIndex] : Slot.None;
        }

        private readonly List<Slot> placeQueue = new List<Slot>();
        private int queueIndex;

        private void ClearSelection(string reason)
        {
            selKind = null;
            placing = Slot.None;
            // Los niveles tambien se borran: si no, R:R y "Riesgo real" siguen
            // ensenando las cifras del plan que se acaba de mandar, como si
            // hubiera uno en curso.
            pxEntry = pxLimit = pxStop = pxTarget = 0;
            queueIndex = 0;
            RefreshPlanBoxes();
            InvalidateChart();
            Status(reason);
        }

        // Fijar un nivel y fijar el ratio son incompatibles: al activar uno se
        // apaga el otro. Si no, no habria forma de saber cual manda cuando el
        // usuario mueve el SL.
        private void OnUseProtectionChanged(object sender, RoutedEventArgs e)
        {
            useStop   = useStopCheck   == null || useStopCheck.IsChecked   == true;
            useTarget = useTargetCheck == null || useTargetCheck.IsChecked == true;
            RefreshPlanBoxes();
            InvalidateChart();
        }

        private void OnPinStopClick(object sender, RoutedEventArgs e)
        {
            pinStop = !pinStop;
            if (pinStop) pinRR = false;
            RefreshPlanBoxes();
        }

        private void OnPinTargetClick(object sender, RoutedEventArgs e)
        {
            pinTarget = !pinTarget;
            if (pinTarget) pinRR = false;
            RefreshPlanBoxes();
        }

        private void OnPinRRClick(object sender, RoutedEventArgs e)
        {
            pinRR = !pinRR;
            if (pinRR)
            {
                pinStop = false;
                pinTarget = false;
                ApplyRR();
                if (placing == Slot.Target) placing = Slot.Stop;
            }
            RefreshPlanBoxes();
            InvalidateChart();
        }

        // Recoloca el TP a la distancia que pide el ratio. El SL es el ancla del
        // riesgo: mover el TP no cambia lo que arriesgas, mover el SL si.
        private void ApplyRR()
        {
            double entry = EffectiveEntry();
            double risk  = Math.Abs(entry - pxStop);
            if (entry <= 0 || risk <= 0 || rrBox == null || rrBox.Value <= 0) return;
            pxTarget = Round(entry + (selLong ? risk * rrBox.Value : -risk * rrBox.Value));
        }

        // Un SL al otro lado de la entrada no es un stop, es un objetivo. Como
        // aqui la direccion la fija el boton COMPRAR/VENDER (y no se deduce del
        // SL, como hace el footprint), hay que impedirlo en vez de dar la vuelta
        // a la operacion. Se pega al tick de al lado en vez de rechazar el clic:
        // asi la linea siempre acompana al raton y no parece que se haya colgado.
        private double ClampStop(double price)
        {
            double entry = EffectiveEntry();
            if (entry <= 0) return price;
            if (selLong)  return price >= entry ? Round(entry - Tick()) : price;
            return price <= entry ? Round(entry + Tick()) : price;
        }

        private double ClampTarget(double price)
        {
            double entry = EffectiveEntry();
            if (entry <= 0) return price;
            if (selLong)  return price <= entry ? Round(entry + Tick()) : price;
            return price >= entry ? Round(entry - Tick()) : price;
        }

        private void OnSlotLabelClick(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            if (b == null || !(b.Tag is Slot)) return;
            if (selKind == null) { Status("Selecciona antes un tipo de orden."); return; }

            Slot want = (Slot)b.Tag;
            if (want == Slot.Target && pinRR)
            {
                Status("El TP lo manda el R:R fijado. Quita 'Fijar' del R:R para moverlo.");
                return;
            }
            placing = want;
            // Elegir a mano saca de la secuencia: se coloca ese y se acaba, en
            // vez de seguir pidiendo los siguientes de la cola.
            queueIndex = placeQueue.Count;
            RefreshPlanBoxes();
            InvalidateChart();
        }

        // Los PriceUpDown ya entregan un double valido y redondeado al tick, asi
        // que aqui no hay parseo ni validacion de formato que hacer.
        private void OnPriceValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingBoxes) return;
            PriceBox box = sender as PriceBox;
            if (box == null || !(box.Tag is Slot) || selKind == null) return;
            if (e.NewValue <= 0) return;

            Slot slot = (Slot)box.Tag;
            if (slot == Slot.Entry) MoveEntryTo(Round(e.NewValue));
            else SetSlot(slot, Round(e.NewValue));
            RefreshPlanBoxes();
            InvalidateChart();
        }

        private void OnRiskValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingBoxes) return;
            SnapRiskToContract();
            RefreshPlanBoxes();
        }

        // Cualquier caja que solo cambie el dimensionado: recalcular y pintar.
        private void OnPriceInputChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingBoxes) return;
            RefreshPlanBoxes();
        }

        private void OnQtyInputChanged(object sender, RoutedEventArgs e)
        {
            if (updatingBoxes) return;
            RefreshPlanBoxes();
        }

        // Recoloca la entrada arrastrando SL y TP con ella: mover el punto de
        // entrada no debe cambiar ni lo que arriesgas ni el R:R. Las distancias
        // se miden ANTES de tocar la entrada; hacerlo despues las mediria contra
        // el nivel nuevo y siempre darian cero.
        private void MoveEntryTo(double newEntry)
        {
            double oldEntry = EffectiveEntry();
            double fallback = DefaultStopDistance();
            double slDist = (pxStop > 0 && oldEntry > 0) ? Math.Abs(oldEntry - pxStop) : fallback;
            double tpDist = (pxTarget > 0 && oldEntry > 0) ? Math.Abs(pxTarget - oldEntry) : slDist * StartRR;
            if (slDist <= 0) slDist = fallback;

            SetSlot(Slot.Entry, newEntry);

            double entry = EffectiveEntry();
            // Un nivel fijado se queda en su precio absoluto y no acompana a la
            // entrada; el riesgo y el R:R cambian, que es justo lo que se pide al
            // fijarlo (p.ej. un SL clavado en un soporte).
            if (!pinStop)   pxStop   = Round(entry + (selLong ? -slDist : slDist));
            if (!pinTarget) pxTarget = Round(entry + (selLong ?  tpDist : -tpDist));
            if (pinRR) ApplyRR();
        }

        // La entrada de una orden a mercado no la elige nadie: es el precio de
        // ahora. Se mueve por DELTA, como hace MoveEntryTo en el panel del
        // footprint, para que SL y TP conserven su distancia y con ella el riesgo
        // y el R:R. Los niveles fijados se quedan donde estan, que es justo lo
        // que se pide al fijarlos.
        //
        // No pasa por MoveEntryTo a proposito: alli, tocar la entrada en MKT se
        // interpreta como "queria una limit ahi" y convertiria la orden a LMT en
        // cada tick.
        private void FollowMarketEntry()
        {
            double market = selLong ? AskPrice() : BidPrice();
            if (market <= 0) return;

            double previous = pxEntry > 0 ? pxEntry : market;
            double delta = market - previous;
            if (Math.Abs(delta) < Tick()) return;

            pxEntry = market;
            if (!pinStop   && pxStop   > 0) pxStop   = Round(pxStop + delta);
            if (!pinTarget && pxTarget > 0) pxTarget = Round(pxTarget + delta);
            if (pinRR) ApplyRR();
        }

        private void SetSlot(Slot slot, double price)
        {
            if (slot == Slot.Entry)
            {
                pxEntry = price;
                // Teclear la entrada en MKT no tiene sentido: el mercado manda.
                // Se interpreta como "queria una limit ahi".
                if (selKind == "MKT") { selKind = "LMT"; Status("Entrada fijada: pasa a LMT."); }
            }
            else if (slot == Slot.Limit)  pxLimit = price;
            else if (slot == Slot.Stop)
            {
                pxStop = ClampStop(price);
                if (pinRR) ApplyRR();          // el TP sigue al ratio
            }
            else if (slot == Slot.Target)
            {
                // Con el ratio fijado el TP es una salida, no una entrada.
                if (!pinRR) pxTarget = ClampTarget(price);
            }
        }

        // Teclear un R:R recoloca el TP. El SL es el ancla del riesgo: mover el
        // TP no cambia lo que arriesgas, mover el SL si.
        private void OnRRValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingBoxes || selKind == null) return;
            double rr = e.NewValue;
            if (rr <= 0) { RefreshPlanBoxes(); return; }
            double entry = EffectiveEntry();
            double risk = Math.Abs(entry - pxStop);
            if (risk <= 0) { RefreshPlanBoxes(); return; }
            pxTarget = Round(entry + (selLong ? risk * rr : -risk * rr));
            RefreshPlanBoxes();
            InvalidateChart();
        }

        private double CurrentRR()
        {
            double entry = EffectiveEntry();
            double risk = Math.Abs(entry - pxStop);
            return risk > 0 ? Math.Abs(pxTarget - entry) / risk : 0;
        }

        // Distancia de stop con la que se siembra el plan. Sale del ATR, no de
        // "Dif. en ticks": ese campo es la separacion de la ENTRADA respecto al
        // mercado, y usarlo como stop daba stops de 8 ticks pegados a la entrada,
        // con las tres lineas superpuestas en pantalla y un riesgo irreal.
        private double DefaultStopDistance()
        {
            double d = atrCache > 0 ? atrCache : OffsetTicks() * Tick() * 4;
            d = Instrument.MasterInstrument.RoundToTickSize(d);
            return d >= Tick() ? d : Tick();
        }

        private int OffsetTicks()
        {
            return inOffsetTicks >= 1 ? inOffsetTicks : Math.Max(1, StartOffsetTicks);
        }

        // Copia a campos planos todo lo tecleado. Se llama SIEMPRE desde el hilo
        // de la UI. A partir de aqui, render y NinjaScript leen los campos y no
        // tocan un solo control.
        private void ReadInputs()
        {
            inOffsetTicks = (offsetBox != null && offsetBox.Value >= 1) ? offsetBox.Value : Math.Max(1, StartOffsetTicks);
            inRiskValue   = riskBox != null ? riskBox.Value : 0;
            inManualQty   = (qtyBox != null && qtyBox.Value >= 1) ? qtyBox.Value : 1;
            inBeEnabled   = beCheck != null && beCheck.IsChecked == true;
            inBeTrigger   = beBox != null ? beBox.Value : 0;
            inBeUnit      = beUnit != null ? beUnit.SelectedIndex : 0;
        }

        private void SeedRiskBox()
        {
            if (riskBox == null) return;
            // El paso de la caja acompana a la unidad: en % se afina de 0,05 en
            // 0,05; en dinero, de 10 en 10.
            riskBox.TickSize = RiskStepPerContract();
            SetValue(riskBox, riskMode == 0
                              ? StartRiskPct
                              : Math.Round(AccountCash() * StartRiskPct / 100.0));
        }

        #endregion

        #region Dimensionado por riesgo

        // Lo que cuesta UN contrato, en la unidad activa. Es el paso correcto y
        // no una comodidad: un riesgo que cae ENTRE dos contratos no se puede
        // ejecutar, porque los futuros van de uno en uno. Dejar teclear 0,60
        // cuando 0,50 y 0,75 son los unicos valores alcanzables es ofrecer una
        // precision que no existe.
        private double RiskStepPerContract()
        {
            double fallback = riskMode == 0 ? 0.05 : 5;
            if (Instrument == null || selKind == null) return fallback;

            double dist = Math.Abs(EffectiveEntry() - pxStop);
            double perContract = dist * Instrument.MasterInstrument.PointValue;
            if (perContract <= 0) return fallback;

            if (riskMode == 1) return perContract;

            double cash = AccountCash();
            return cash > 0 ? perContract / cash * 100.0 : fallback;
        }

        // Ajusta el riesgo al multiplo de un contrato mas cercano, con uno como
        // minimo. Se hace aqui y no confiando en el TickSize del control porque
        // ya se vio que ese paso no siempre se respeta.
        private void SnapRiskToContract()
        {
            if (riskBox == null || inQtyManual) return;
            double step = RiskStepPerContract();
            if (step <= 0) return;

            double snapped = Math.Round(riskBox.Value / step) * step;
            if (snapped < step) snapped = step;
            // A dos decimales: sin Instrument la caja pinta cinco, y asi los
            // tres ultimos son ceros en vez de la cola del reparto.
            snapped = Math.Round(snapped, 2);
            if (Math.Abs(snapped - riskBox.Value) > step * 0.01) SetValue(riskBox, snapped);
        }

        private double RiskMoney()
        {
            if (inRiskValue <= 0) return 0;
            return riskMode == 0 ? AccountCash() * inRiskValue / 100.0 : inRiskValue;
        }

        // Contratos y riesgo REAL. El riesgo devuelto sale siempre de los
        // contratos que se van a mandar, nunca del riesgo pedido: si el minimo de
        // 1 contrato cuesta mas de lo pedido, ensenar lo pedido seria mentir.
        private int ComputeContracts(out bool minForced, out double actualRisk)
        {
            return ContractsFor(pxStop, out minForced, out actualRisk);
        }

        // El mismo calculo con un stop cualquiera. Sirve para ensenar lo que
        // costaria el stop que hay BAJO EL CURSOR sin tocar el plan.
        private int ContractsFor(double stopPrice, out bool minForced, out double actualRisk)
        {
            minForced = false;
            actualRisk = 0;
            if (Instrument == null) return 0;

            // Sin SL no hay distancia, asi que no hay riesgo por contrato con el
            // que calcular nada: la cantidad solo puede ser manual.
            double dist = useStop ? Math.Abs(EffectiveEntry() - stopPrice) : 0;
            double perContract = dist * Instrument.MasterInstrument.PointValue;
            if (perContract <= 0)
            {
                if (!inQtyManual) return 0;
                return Math.Max(1, inManualQty);
            }

            int n;
            if (inQtyManual)
            {
                n = Math.Max(1, inManualQty);
            }
            else
            {
                n = (int)Math.Floor(RiskMoney() / perContract);
                // Los futuros son contratos enteros y el minimo operable es 1. Si
                // el riesgo pedido no llega, se manda 1 y se avisa: ese contrato
                // arriesga MAS de lo pedido, y el usuario tiene que verlo.
                if (n < 1) { n = 1; minForced = true; }
            }

            actualRisk = n * perContract;
            return n;
        }

        #endregion

        #region Validacion

        // Devuelve null si el plan se puede mandar, o el motivo por el que no.
        // Todo se comprueba ANTES de pulsar, no al recibir el rechazo del broker.
        private string Validate(int contracts)
        {
            if (account == null) return "Sin cuenta seleccionada.";
            if (account.ConnectionStatus != ConnectionStatus.Connected) return "La cuenta no está conectada.";
            if (selKind == null) return "Selecciona un tipo de orden.";
            if (Instrument == null) return "Sin instrumento.";
            if (!useStop && !inQtyManual)
                return "Sin SL no se puede dimensionar por riesgo: pon Contratos en Manual.";
            if (contracts < 1) return "Contratos inválidos.";

            double entry = EffectiveEntry();
            double market = LastPrice();
            double tick = Tick();
            if (entry <= 0 || market <= 0) return "Sin precio de mercado.";

            // Cada aviso empieza nombrando el plan al que se refiere. Asi, si
            // alguna vez vuelve a quedarse uno caducado, se ve de un vistazo que
            // habla de otra operacion en vez de parecer que el panel se equivoca.
            string who = (selLong ? "COMPRA " : "VENTA ") + selKind + ": ";

            if (useStop)
            {
                if (Math.Abs(entry - pxStop) < tick) return who + "el SL está a menos de un tick de la entrada.";
                if (selLong && pxStop >= entry)  return who + "el SL debe ir POR DEBAJO de la entrada.";
                if (!selLong && pxStop <= entry) return who + "el SL debe ir POR ENCIMA de la entrada.";
            }
            if (useTarget && pxTarget > 0)
            {
                if (selLong && pxTarget <= entry)  return who + "el TP debe ir POR ENCIMA de la entrada.";
                if (!selLong && pxTarget >= entry) return who + "el TP debe ir POR DEBAJO de la entrada.";
            }

            if (selKind == "LMT")
            {
                if (selLong && entry >= market)  return who + "la entrada debe ir POR DEBAJO del mercado.";
                if (!selLong && entry <= market) return who + "la entrada debe ir POR ENCIMA del mercado.";
            }
            else if (selKind == "STP" || selKind == "STP LMT")
            {
                if (selLong && entry <= market)  return who + "la entrada debe ir POR ENCIMA del mercado.";
                if (!selLong && entry >= market) return who + "la entrada debe ir POR DEBAJO del mercado.";
            }

            if (selKind == "STP LMT")
            {
                if (pxLimit <= 0) return who + "falta el precio límite.";
                if (selLong && pxLimit < entry)  return who + "el límite va en el stop o POR ENCIMA.";
                if (!selLong && pxLimit > entry) return who + "el límite va en el stop o POR DEBAJO.";
            }

            return null;
        }

        #endregion

        #region Refresco del panel

        private void RefreshPlanBoxes()
        {
            if (panel == null) return;
            ReadInputs();

            bool sel = selKind != null;
            RefreshPositionUi();

            if (limitRow != null)
                limitRow.Visibility = (sel && selKind == "STP LMT") ? Visibility.Visible : Visibility.Collapsed;

            SetValue(entryBox,  sel ? EffectiveEntry() : 0);
            SetValue(limitBox,  sel && selKind == "STP LMT" ? pxLimit : 0);
            SetValue(stopBox,   sel ? pxStop : 0);
            SetValue(targetBox, sel ? pxTarget : 0);
            // En MKT la entrada la manda el mercado: se deshabilita en vez de
            // dejarla editable y luego ignorar lo tecleado.
            entryBox.IsEnabled = sel && selKind != "MKT";
            // Con el ratio fijado el TP se deriva del SL: deja de ser editable y
            // de ser colocable, igual que los contratos en modo Auto.
            stopBox.IsEnabled   = sel && useStop;
            targetBox.IsEnabled = sel && !pinRR && useTarget;
            if (pinStopBtn   != null) pinStopBtn.IsEnabled   = useStop;
            if (pinTargetBtn != null) pinTargetBtn.IsEnabled = useTarget && !pinRR;
            if (sel && !pinRR) SetValue(rrBox, Math.Round(CurrentRR(), 2));
            else if (!sel) SetValue(rrBox, 0);

            Highlight(pinStopBtn,   pinStop);
            Highlight(pinTargetBtn, pinTarget);
            Highlight(pinRRBtn,     pinRR);
            pinTargetBtn.IsEnabled = !pinRR;

            // La etiqueta del nivel que recibira el proximo clic va resaltada.
            // Sin esto no habia forma de saber que se puede pulsar "TP" para
            // recolocarlo: la unica pista era el texto de la cabecera.
            foreach (KeyValuePair<Slot, Button> kv in slotLabels)
            {
                bool active = sel && kv.Key == placing;
                kv.Value.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
                // Transparent y NO null. En WPF un fondo null no se dibuja, y lo
                // que no se dibuja no recibe el raton: el boton solo respondia
                // sobre las letras. Transparent es invisible pero si se pinta,
                // asi que el clic vale en todo el rectangulo.
                kv.Value.Background = active ? OnBrush : Brushes.Transparent;
                kv.Value.Foreground = active ? Brushes.White : textBrush;
            }

            // En Manual el riesgo es una salida, asi que % y Money dejan de ir
            // resaltados: no mandan. Cual de los dos esta activo se sigue viendo
            // por el borde, porque la cifra derivada se muestra en esa unidad.
            Highlight(pctBtn,    riskMode == 0 && !inQtyManual);
            Highlight(moneyBtn,  riskMode == 1 && !inQtyManual);
            MarkUnit(pctBtn,     riskMode == 0);
            MarkUnit(moneyBtn,   riskMode == 1);
            Highlight(autoBtn,   !inQtyManual);
            Highlight(manualBtn, inQtyManual);

            bool minForced;
            double actualRisk;
            int contracts = ComputeContracts(out minForced, out actualRisk);

            if (!inQtyManual) SetValue(qtyBox, contracts);
            qtyBox.IsEnabled = inQtyManual;

            // En Auto el riesgo manda y los contratos salen de el. En Manual es al
            // reves, y entonces la caja de riesgo NO puede quedarse con el numero
            // que tenia: estaria diciendo que arriesgas 501 mientras 20 contratos
            // arriesgan otra cosa. Pasa a ser una salida, y se deshabilita para
            // que no parezca que sigue mandando ella.
            riskBox.IsEnabled = !inQtyManual;
            riskLabel.Text    = inQtyManual ? "Riesgo (del lote)" : "Riesgo";
            // El coste de un contrato cambia con la distancia al SL, asi que el
            // paso se recalcula en cada refresco, no solo al cambiar de unidad.
            riskBox.TickSize  = RiskStepPerContract();
            if (!inQtyManual) SnapRiskToContract();
            if (inQtyManual)
            {
                double cash = AccountCash();
                SetValue(riskBox, riskMode == 0
                                  ? (cash > 0 ? actualRisk / cash * 100.0 : 0)
                                  : actualRisk);
            }

            contractsText.Text = minForced ? "Contratos MIN 1" : "Contratos";
            contractsText.Foreground = minForced ? WarnBrush : textBrush;

            riskRealText.Text = actualRisk > 0
                ? "-" + actualRisk.ToString("N2", CultureInfo.CurrentCulture)
                : (sel ? "-" : "sin plan");


            string problem = sel ? Validate(contracts) : "Selecciona un tipo de orden.";
            planValid = sel && problem == null;
            bool ready = problem == null && armed;
            execButton.IsEnabled = ready;
            execButton.Background = ready ? (selLong ? BuyBrush : SellBrush) : SafeBrush;
            execButton.Content = sel
                ? "EXECUTE  " + (selLong ? "COMPRA " : "VENTA ") + contracts + "c"
                : "EXECUTE";

            // SIEMPRE se escribe el estado. Antes solo se escribia cuando habia
            // problema, asi que al arreglarse el plan el aviso viejo se quedaba
            // en pantalla contradiciendo al boton EXECUTE, que ya estaba verde.
            string mismatch = AccountMatchesChart()
                              ? ""
                              : "OJO: el gráfico está en " + ChartAccountName() + ". Las órdenes de "
                                + (account != null ? account.Name : "?") + " no se dibujan aquí. ";

            if (problem != null)
                Status(mismatch + problem);
            else if (sel)
                Status(mismatch + (selLong ? "COMPRA " : "VENTA ") + selKind + " " + contracts + "c lista"
                       + (armed ? "." : ". Falta armar la operativa."));
            else
                Status(mismatch + (armed ? "Operativa ARMADA. Selecciona un tipo de orden." : "Desarmado."));
        }

        // Marca cual es la unidad activa sin decir que mande: borde, no relleno.
        private static void MarkUnit(Button b, bool active)
        {
            if (b == null) return;
            b.BorderBrush     = active ? OnBrush : Brushes.Transparent;
            b.BorderThickness = new Thickness(active ? 2 : 1);
        }

        // Todo lo que depende de si HAY POSICION, aparte. Se llama en cada
        // refresco, no solo cuando hay un plan seleccionado: sin seleccion -que
        // es justo como quedas despues de ejecutar- RefreshPlanBoxes no se
        // llamaba nunca mas, y los botones se quedaban con el estado del
        // instante del EXECUTE, cuando la posicion todavia no existia.
        private void RefreshPositionUi()
        {
            if (panel == null) return;

            bool sel = selKind != null;
            Position pos = positionCache;

            foreach (Button b in kindButtons)
            {
                string tag = b.Tag as string;
                if (tag == null) continue;
                bool active   = sel && tag == (selLong ? "B|" : "S|") + selKind;
                bool isBuyBtn = tag.StartsWith("B|");
                bool isMkt    = tag == "B|MKT" || tag == "S|MKT";

                // Con posicion abierta los MKT actuan al instante: encendidos y
                // con el signo de lo que le hacen A TU POSICION. En un corto,
                // vender SUMA, y ponerle un menos seria mentira.
                if (isMkt && pos != null)
                {
                    bool adds = isBuyBtn == (pos.MarketPosition == MarketPosition.Long);
                    b.Content = adds ? "MKT  +" : "MKT  -";
                    b.Opacity = 1.0;
                    b.BorderThickness = new Thickness(1);
                    continue;
                }

                if (isMkt) b.Content = "MKT";
                b.Opacity = active ? 1.0 : (sel ? 0.35 : 0.55);
                b.BorderThickness = new Thickness(active ? 2 : 1);
            }

            placingText.Text = sel
                ? (selLong ? "COMPRA " : "VENTA ") + selKind + "   Coloca: " + SlotName(placing)
                : (pos != null
                   ? (!useStop && !useTarget
                      ? "MKT abierta: pulsar añade o reduce contratos"
                      : "SL/TP marcados: MKT abre plan para el tramo nuevo")
                   : "Sin selección");
            placingText.Foreground = sel ? (selLong ? BuyBrush : SellBrush)
                                         : (pos != null ? WarnBrush : textBrush);

            // Con posicion abierta la caja de Contratos es el tamano del ajuste,
            // asi que tiene que poder tocarse aunque el riesgo mande.
            if (pos != null) qtyBox.IsEnabled = true;
        }

        private static void Highlight(Button b, bool active)
        {
            if (b == null) return;
            b.Background = active ? OnBrush : SafeBrush;
            b.Foreground = Brushes.White;
        }

        private void InvalidateChart()
        {
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        #endregion

        #region Raton sobre el grafico

        private void HookChart()
        {
            if (ChartControl == null) return;
            ChartControl.MouseMove += OnChartMouseMove;
            ChartControl.MouseLeave += OnChartMouseLeave;
            ChartControl.PreviewMouseLeftButtonDown += OnChartMouseDown;
            ChartControl.PreviewKeyDown += OnChartKeyDown;

            // La ventana del grafico abre su buscador de instrumentos con un
            // manejador de teclado que BURBUJEA. Cortando el evento aqui, entre
            // las cajas y el grafico, nunca le llega.
            //
            // Se corta en KeyUp y NO en KeyDown a proposito: en WPF, consumir el
            // KeyDown impide que se genere el TextInput, que es como se filtran
            // caracteres en un TextBox. Hacerlo ahi dejaria las cajas sin poder
            // escribir, cambiando un fallo por otro. Al soltar la tecla el
            // caracter ya esta insertado, asi que consumirlo no rompe nada.
            if (frame != null)
            {
                frame.KeyUp   += OnPanelKeyUp;
                frame.KeyDown += OnPanelKeyDown;
            }
        }

        // Estos controles de NT8 son UserControl con un TextBox dentro. Dar el
        // foco al UserControl no escribe nada, asi que hay que darselo al TextBox
        // interno. Candidato a explicar por que la caja nativa si teclea y la
        // nuestra no, siendo el mismo tipo de control.
        private static void FocusInnerTextBox(DependencyObject root)
        {
            TextBox tb = FindTextBox(root);
            if (tb == null) return;
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        }

        private static TextBox FindTextBox(DependencyObject root)
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = VisualTreeHelper.GetChild(root, i);
                TextBox tb = c as TextBox;
                if (tb != null) return tb;
                tb = FindTextBox(c);
                if (tb != null) return tb;
            }
            return null;
        }

        private void OnPanelKeyUp(object sender, KeyEventArgs e)
        {
            e.Handled = true;
        }

        private void UnhookChart()
        {
            if (frame != null)
            {
                frame.KeyUp   -= OnPanelKeyUp;
                frame.KeyDown -= OnPanelKeyDown;
            }
            if (ChartControl == null) return;
            ChartControl.MouseMove -= OnChartMouseMove;
            ChartControl.MouseLeave -= OnChartMouseLeave;
            ChartControl.PreviewMouseLeftButtonDown -= OnChartMouseDown;
            ChartControl.PreviewKeyDown -= OnChartKeyDown;
        }

        // Precio bajo el cursor. Devuelve false si el par de coordenadas no
        // cuadra: GetValueByYWpf trabaja en unidades WPF (las de e.GetPosition),
        // no en pixeles de render. Si el resultado se sale de la escala visible
        // teniendo el raton dentro del panel, el espacio de coordenadas esta mal
        // y hay que avisar en vez de colocar un stop en un precio inventado.
        private bool PriceUnderMouse(MouseEventArgs e, out double price)
        {
            price = 0;
            if (lastScale == null || ChartControl == null) return false;

            Point p = e.GetPosition(ChartControl);
            price = lastScale.GetValueByYWpf(p.Y);
            if (price <= 0) return false;

            double lo = lastScale.MinValue, hi = lastScale.MaxValue;
            if (hi <= lo) return false;
            double margin = (hi - lo) * 0.25;
            if (price < lo - margin || price > hi + margin)
            {
                if (!warnedCoords)
                {
                    warnedCoords = true;
                    Log("AT Chart Trader: el precio bajo el ratón cae fuera de la escala visible. La colocación por clic queda desactivada; usa las cajas del panel.",
                        LogLevel.Warning);
                }
                return false;
            }
            return true;
        }

        private void OnChartMouseMove(object sender, MouseEventArgs e)
        {
            if (selKind == null || placing == Slot.None) { hoverValid = false; return; }
            double price;
            hoverValid = PriceUnderMouse(e, out price);
            if (!hoverValid) return;
            // La vista previa se limita igual que la colocacion: si no, la linea
            // y su cifra ensenarian un SL imposible mientras mueves el raton.
            double p2 = Round(price);
            if (placing == Slot.Stop)        p2 = ClampStop(p2);
            else if (placing == Slot.Target) p2 = ClampTarget(p2);
            hoverPrice = p2;
            InvalidateChart();
        }

        // Con el raton fuera del grafico la linea que lo seguia se queda clavada
        // donde estaba, y entonces se ven dos SL sin que uno de los dos
        // signifique nada. Al salir, desaparece.
        private void OnChartMouseLeave(object sender, MouseEventArgs e)
        {
            if (!hoverValid) return;
            hoverValid = false;
            InvalidateChart();
        }

        private void OnChartMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Sin seleccion el grafico es del usuario: no se toca el clic.
            if (selKind == null || placing == Slot.None) return;

            double price;
            if (!PriceUnderMouse(e, out price)) return;

            if (placing == Slot.Entry) MoveEntryTo(Round(price));
            else SetSlot(placing, Round(price));

            AdvancePlacing(false);
            RefreshPlanBoxes();
            InvalidateChart();

            // Solo aqui se consume el clic, y solo estando en colocacion.
            e.Handled = true;
        }

        private void OnChartKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && selKind != null)
            {
                ClearSelection("Selección cancelada.");
                e.Handled = true;
            }
        }

        // Con el foco dentro del panel el evento del ChartControl no llega, asi
        // que ESC se atiende tambien aqui.
        // Ultimo eslabon entre las cajas y la ventana del grafico.
        //
        // NT8 abre su buscador de instrumentos desde el KeyDown que burbujea
        // hasta la ventana. Medido: en el KeyDown el foco esta en nuestro
        // TextBox, y para cuando llega el TextInput ya no, porque el popup se
        // lo ha llevado. Consumir KeyUp llega tarde y consumir el TextInput
        // tambien: cuando existen, el buscador ya esta abierto.
        //
        // Solo queda consumir el KeyDown aqui. Pero WPF comprueba
        // "if (!keyArgs.Handled)" antes de crear la composicion de texto, asi
        // que consumirlo deja la caja muda. El precio de que el buscador no se
        // coma la tecla es escribir el caracter a mano, que es lo que se hace
        // abajo.
        //
        // Se consumen SOLO las teclas que producen texto (las que abren el
        // buscador). Borrar, flechas, inicio/fin, tabulador y Enter pasan sin
        // tocar: el TextBox ya los ha atendido en la hoja, antes de burbujear
        // hasta aqui, y son cosas que no queremos reimplementar.
        private void OnPanelKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && selKind != null)
            {
                ClearSelection("Selección cancelada.");
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers != ModifierKeys.None) return;   // atajos, intactos

            string ch = TypedChar(e.Key);
            bool letter = e.Key >= Key.A && e.Key <= Key.Z;
            if (ch == null && !letter) return;                     // no abre el buscador

            TextBox box = Keyboard.FocusedElement as TextBox;
            // Las letras se tragan sin escribir nada: en una caja numerica no
            // pintan nada, y sueltas abririan el buscador.
            if (box != null && ch != null) InsertText(box, ch);
            e.Handled = true;
        }

        private static string TypedChar(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
                return ((char)('0' + (key - Key.D0))).ToString(CultureInfo.InvariantCulture);
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return ((char)('0' + (key - Key.NumPad0))).ToString(CultureInfo.InvariantCulture);
            // El separador decimal sale de la cultura: en un teclado espanol la
            // coma del teclado numerico tiene que escribir lo que la caja espera.
            if (key == Key.Decimal || key == Key.OemPeriod || key == Key.OemComma)
                return CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            if (key == Key.Subtract || key == Key.OemMinus)
                return "-";
            return null;
        }

        // Inserta respetando la seleccion y dejando el cursor detras, que es lo
        // que habria hecho el TextInput que acabamos de impedir.
        private static void InsertText(TextBox box, string text)
        {
            int start  = box.SelectionStart;
            int length = box.SelectionLength;
            string current = box.Text ?? string.Empty;
            if (start > current.Length) start = current.Length;
            if (start + length > current.Length) length = current.Length - start;

            box.Text = current.Remove(start, length).Insert(start, text);
            box.SelectionStart  = start + text.Length;
            box.SelectionLength = 0;
        }

        #endregion

        #region Ejecucion

        private void OnManageClick(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            if (b == null || account == null || Instrument == null) return;
            string tag = b.Tag as string;
            try
            {
                if (tag == "CANCEL") { account.CancelAllOrders(Instrument); Status("Cancelación enviada."); }
                else if (tag == "FLAT") { account.Flatten(new[] { Instrument }); Status("Cierre y cancelación enviados."); }
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }

        private void OnExecuteClick(object sender, RoutedEventArgs e)
        {
            // El armado se comprueba AQUI, no solo con IsEnabled: un boton puede
            // quedar activo por una carrera de refresco, el envio no.
            if (!armed) { Status("Operativa desarmada."); return; }

            bool minForced;
            double actualRisk;
            int contracts = ComputeContracts(out minForced, out actualRisk);

            string problem = Validate(contracts);
            if (problem != null) { Status(problem); return; }

            if (ConfirmOrders && !Confirm(contracts, actualRisk, minForced)) { Status("Envío cancelado."); return; }

            try
            {
                OrderType type = selKind == "MKT" ? OrderType.Market
                               : selKind == "LMT" ? OrderType.Limit
                               : selKind == "STP" ? OrderType.StopMarket
                               : OrderType.StopLimit;

                double limitPrice = 0, stopPrice = 0;
                if (selKind == "LMT") limitPrice = pxEntry;
                else if (selKind == "STP") stopPrice = pxEntry;
                else if (selKind == "STP LMT") { stopPrice = pxEntry; limitPrice = pxLimit; }

                OrderAction action = selLong ? OrderAction.Buy : OrderAction.SellShort;
                Order entry = account.CreateOrder(Instrument, action, type, OrderEntry.Manual, TimeInForce.Day,
                                                  contracts, limitPrice, stopPrice, string.Empty,
                                                  "AT " + (selLong ? "Compra " : "Venta ") + selKind,
                                                  Core.Globals.MaxDate, null);

                // Solo se apunta el bracket si hay algo que proteger. Con las dos
                // casillas apagadas la entrada sale sola, como una orden de toda
                // la vida, y no queda nada esperando su fill.
                double bStop   = useStop   ? pxStop   : 0;
                double bTarget = useTarget ? pxTarget : 0;
                if (bStop > 0 || bTarget > 0)
                    lock (pendingLock)
                        pending[entry] = new Bracket { IsLong = selLong, StopPrice = bStop, TargetPrice = bTarget };

                account.Submit(new[] { entry });
                Status((selLong ? "Compra " : "Venta ") + selKind + " x" + contracts + " enviada.");
                ClearSelection("Orden enviada. Selecciona otro tipo para el siguiente plan.");
            }
            catch (Exception ex)
            {
                Status("Error al enviar: " + ex.Message);
            }
        }

        private bool Confirm(int contracts, double actualRisk, bool minForced)
        {
            string msg = (selLong ? "COMPRA " : "VENTA ") + selKind + "\n"
                       + Instrument.FullName + "   " + account.Name + "\n\n"
                       + "Contratos: " + contracts + (minForced ? "  (MÍNIMO 1: arriesga más de lo pedido)" : "") + "\n"
                       + "Entrada:   " + Fmt(EffectiveEntry()) + "\n"
                       + (selKind == "STP LMT" ? "Límite:    " + Fmt(pxLimit) + "\n" : "")
                       + "SL:        " + Fmt(pxStop) + "\n"
                       + "TP:        " + Fmt(pxTarget) + "\n"
                       + "R:R:       " + CurrentRR().ToString("0.00") + "\n\n"
                       + "Riesgo real: " + actualRisk.ToString("N2", CultureInfo.CurrentCulture);

            return MessageBox.Show(msg, "AT Chart Trader - confirmar orden",
                                   MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        // Llega en el hilo de eventos de la cuenta, no en el de la UI.
        private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
        {
            if (e == null || e.Order == null) return;

            bool dead = e.OrderState == OrderState.Filled
                     || e.OrderState == OrderState.Cancelled
                     || e.OrderState == OrderState.Rejected;

            if (dead)
                lock (pendingLock)
                    for (int i = protectionPairs.Count - 1; i >= 0; i--)
                    {
                        ProtPair pair = protectionPairs[i];
                        if (ReferenceEquals(pair.Stop, e.Order))   pair.Stop = null;
                        if (ReferenceEquals(pair.Target, e.Order)) pair.Target = null;
                        // Par sin ninguna pata viva: fuera de la lista.
                        if ((pair.Stop == null || !IsLive(pair.Stop))
                         && (pair.Target == null || !IsLive(pair.Target)))
                            protectionPairs.RemoveAt(i);
                    }

            // PartFilled cuenta. Una entrada llenada a medias es una posicion
            // REAL y abierta: si solo se atendiera Filled, esos contratos se
            // quedarian sin stop hasta que se completara el resto, que puede no
            // pasar nunca. Y si encima se cancela lo que falta, antes se
            // escribia "Entrada cancelada" y se olvidaba una posicion viva.
            bool terminal = e.OrderState == OrderState.Filled
                         || e.OrderState == OrderState.Cancelled
                         || e.OrderState == OrderState.Rejected;
            bool partial  = e.OrderState == OrderState.PartFilled;

            Bracket bracket;
            lock (pendingLock)
            {
                if (!pending.TryGetValue(e.Order, out bracket)) return;
                if (!terminal && !partial) return;
                // Solo se saca del diccionario cuando la orden ha terminado. En
                // un parcial sigue dentro, porque puede entrar mas y habra que
                // proteger tambien esos.
                if (terminal) pending.Remove(e.Order);
            }

            int filled = e.Order.Filled;

            // Cada tramo protegido lleva SU par. Se probo a mantener uno solo
            // creciendo con la posicion, pero eso impide lo que se pide ahora:
            // llevar 6 contratos sin proteger y anadir 3 con SL y TP. La regla
            // que queda es la de seguridad, no la de cantidad exacta: la
            // proteccion puede cubrir MENOS que la posicion, nunca mas.
            if (filled > 0)
            {
                Account acc = sender as Account ?? account;
                if (acc != null) SubmitProtection(acc, e.Order, bracket, filled);
            }

            if (terminal && filled == 0)
                Status("Entrada " + (e.OrderState == OrderState.Cancelled ? "cancelada" : "rechazada") + ".");
            else if (terminal && e.OrderState != OrderState.Filled)
                Status("Entrada " + (e.OrderState == OrderState.Cancelled ? "cancelada" : "rechazada")
                       + " con " + filled + " contrato(s) llenos. Esa posición SÍ queda protegida.");
            else if (partial)
                Status("Llenado parcial: " + filled + " de " + e.Order.Quantity + " protegidos.");
        }

        private bool SubmitProtection(Account acc, Order entry, Bracket bracket, int quantity)
        {
            try
            {
                if (quantity <= 0) return false;

                double fill = entry.AverageFillPrice;
                if (fill <= 0) { Status("Fill sin precio: SL/TP no enviados. Protege a mano."); return false; }

                Instrument instrument = entry.Instrument;
                double stopPrice   = bracket.StopPrice;
                double targetPrice = bracket.TargetPrice;

                // Red de seguridad contra el fill real, no contra el precio
                // planeado: un SL del lado equivocado cierra la posicion en
                // cuanto llega. Antes de mandarlo, fuera.
                if (stopPrice > 0 && (bracket.IsLong ? stopPrice >= fill : stopPrice <= fill))
                {
                    Status("SL del lado equivocado respecto al fill: no enviado. Protege a mano.");
                    stopPrice = 0;
                }
                if (targetPrice > 0 && (bracket.IsLong ? targetPrice <= fill : targetPrice >= fill))
                {
                    Status("TP del lado equivocado respecto al fill: no enviado.");
                    targetPrice = 0;
                }
                if (stopPrice <= 0 && targetPrice <= 0) return false;

                OrderAction exit = bracket.IsLong ? OrderAction.Sell : OrderAction.BuyToCover;
                // OCO solo si van los dos: con una sola orden el identificador
                // compartido no empareja nada y algunos brokers lo rechazan.
                string oco = (stopPrice > 0 && targetPrice > 0) ? Guid.NewGuid().ToString("N") : string.Empty;

                ProtPair pair = new ProtPair { Quantity = quantity };
                List<Order> exits = new List<Order>();
                if (stopPrice > 0)
                {
                    pair.Stop = acc.CreateOrder(instrument, exit, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Day,
                                                quantity, 0, stopPrice, oco, "AT SL", Core.Globals.MaxDate, null);
                    exits.Add(pair.Stop);
                }
                if (targetPrice > 0)
                {
                    pair.Target = acc.CreateOrder(instrument, exit, OrderType.Limit, OrderEntry.Manual, TimeInForce.Day,
                                                  quantity, targetPrice, 0, oco, "AT TP", Core.Globals.MaxDate, null);
                    exits.Add(pair.Target);
                }

                acc.Submit(exits);
                lock (pendingLock)
                {
                    // Se ACUMULAN: cada tramo protegido lleva su par, y puede
                    // haber contratos sin proteger conviviendo con ellos.
                    protectionPairs.Add(pair);
                }
                Status("Protección enviada: " + quantity + "c sobre fill " + Fmt(fill) + ".");
                return true;
            }
            catch (Exception ex)
            {
                Status("Error mandando SL/TP: " + ex.Message + ". Protege a mano.");
                return false;
            }
        }

        #endregion

        #region Break-even automatico

        // Mueve el stop vivo al precio de entrada mas un desplazamiento a favor,
        // cuando el beneficio flotante pasa del umbral. El desplazamiento existe
        // para cubrir comisiones: cerrar en la entrada exacta deja el neto en
        // negativo, no en cero.
        private void DoAutoBreakEven()
        {
            // Solo campos planos: esto corre en el hilo de NinjaScript, y tocar
            // beCheck/beBox/beUnit desde aqui lanzaria en cada tick.
            if (!inBeEnabled || inBeTrigger <= 0) return;
            if (account == null) return;
            if (account.ConnectionStatus != ConnectionStatus.Connected) return;

            Position p = CurrentPosition();
            if (p == null) return;

            double last = LastPrice();
            if (last <= 0) return;

            bool isLong = p.MarketPosition == MarketPosition.Long;
            bool reached;
            if (inBeUnit == 0)
            {
                double ticksProfit = (isLong ? last - p.AveragePrice : p.AveragePrice - last) / Tick();
                reached = ticksProfit >= inBeTrigger;
            }
            else
            {
                reached = p.GetUnrealizedProfitLoss(PerformanceUnit.Currency, last) >= inBeTrigger;
            }
            if (!reached) return;

            double bePrice = Round(p.AveragePrice + (isLong ? 1 : -1) * BreakEvenOffsetTicks * Tick());
            // Al otro lado del precio saltaria al momento.
            if (isLong ? bePrice >= last : bePrice <= last)
            {
                // Antes se descartaba en silencio y parecia que el break-even
                // estuviera roto. Pasa cuando el umbral no supera al
                // desplazamiento: al cumplirse, el stop cae justo en el precio.
                if (inBeUnit == 0 && inBeTrigger <= BreakEvenOffsetTicks)
                    Status("Break-even: el umbral (" + inBeTrigger.ToString("0", CultureInfo.CurrentCulture)
                           + " ticks) debe superar al desplazamiento (" + BreakEvenOffsetTicks + " ticks).");
                return;
            }

            // TODOS los stops vivos, no solo el ultimo: con llenados parciales
            // hay un par OCO por tramo, y mover uno solo dejaria el resto de la
            // posicion con su stop original.
            List<Order> move = new List<Order>();
            lock (pendingLock)
                foreach (ProtPair pair in protectionPairs)
                {
                    Order o = pair.Stop;
                    if (o == null || !IsLive(o)) continue;
                    // Nunca hacia atras: el break-even solo mejora el stop.
                    if (o.StopPrice > 0 && (isLong ? bePrice <= o.StopPrice : bePrice >= o.StopPrice)) continue;
                    move.Add(o);
                }
            if (move.Count == 0) return;

            try
            {
                foreach (Order o in move) o.StopPriceChanged = bePrice;
                account.Change(move);
                Status("Break-even aplicado a " + move.Count + " stop(s): " + Fmt(bePrice) + ".");
            }
            catch (Exception ex)
            {
                Status("Break-even falló: " + ex.Message);
            }
        }

        #endregion

        #region Interruptor de operativa

        private void OnArmClick(object sender, RoutedEventArgs e)
        {
            SetArmed(!armed, null);
        }

        private void SetArmed(bool value, string reason)
        {
            armed = value;
            UpdateArmVisuals();
            RefreshPlanBoxes();
            Status(reason ?? (armed ? "Operativa ARMADA: EXECUTE manda órdenes reales." : "Operativa desarmada."));
        }

        private void UpdateArmVisuals()
        {
            if (armButton == null) return;
            armButton.Content    = armed ? "OPERATIVA ON" : "OPERATIVA OFF";
            armButton.Background = armed ? BuyBrush : SafeBrush;
        }

        #endregion
    }
}
