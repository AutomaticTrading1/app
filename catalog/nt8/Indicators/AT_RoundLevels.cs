// =============================================================================
//  AutomaticTrading  -  https://www.automatictrading.net/
//  (c) 2026 AutomaticTrading. Todos los derechos reservados.
//
//  Herramienta del catalogo oficial de la aplicacion AutomaticTrading.
//  Se distribuye como FUENTE a proposito: cualquiera que vaya a operar con
//  esto deberia poder leer antes que hace.
// =============================================================================
// =============================================================================
//  AT_RoundLevels.cs  -  Zonas de numeros redondos para NinjaTrader 8.
// -----------------------------------------------------------------------------
//  Port del bloque RenderRoundLevels de AT_OrderFlow_Footprint (MQL5), que a su
//  vez viene del indicador RoundLevels.mq5. Marca las bandas de precio alrededor
//  de cada numero redondo, con color semantico: por encima del precio actual son
//  oferta, por debajo demanda.
//
//  EL PASO SALE DEL RANGO VISIBLE, no de la magnitud del precio. Se divide el
//  alto visible del grafico entre las lineas objetivo y se ajusta a la serie
//  1-2-5. Asi se ven siempre las mismas lineas en pantalla sea cual sea el
//  activo, el zoom o el alto del monitor. La version por porcentaje del precio
//  fallaba en las dos cosas: caia justo en los cortes de la serie (US100 0.50%
//  -> 200, 0.25% -> 100) y doblaba el paso solo con que el indice subiera.
//
//  Se dibuja en OnRender (SharpDX) y no con objetos Draw.*: el rango visible
//  solo esta disponible ahi (ChartScale), las bandas ocupan el ancho completo
//  del panel sin anclarlas a barras, y no deja objetos en el grafico que el
//  usuario tenga que borrar despues.
//
//  INSTALACION
//    1. Instalar desde la aplicacion (pestaña Herramientas) o copiar a
//       Documents\NinjaTrader 8\bin\Custom\Indicators\.
//    2. En NT8: New -> NinjaScript Editor -> F5 (compilar).
//    3. Clic derecho en el grafico -> Indicators... -> AT Round Levels.
//
//  Los botones de paso (x0.1 ... x5) se añaden a la barra de herramientas de la
//  ventana del grafico y multiplican el paso automatico. El multiplicador vive
//  solo en memoria: el input solo lo siembra, a partir de ahi mandan los botones.
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class AT_RoundLevels : Indicator
    {
        // Multiplicadores sobre el paso automatico. Factores 0.1/0.2/0.5/1/2/5 de
        // una potencia de diez; el resultado se vuelve a ajustar a 1-2-5, asi que
        // el paso sigue siendo redondo con cualquiera de ellos.
        private static readonly double[] Mults = { 0.1, 0.2, 0.5, 1.0, 2.0, 5.0 };

        // Ancho del hueco reservado a la etiqueta de precio, a cada lado.
        private const float LabelWidth = 120f;

        // Los escribe el hilo de la UI (clic en un boton) y los lee el hilo de
        // render. volatile basta: son lecturas sueltas, no read-modify-write.
        private volatile int  multIndex = 3;      // x1
        private volatile bool drawEnabled = true;

        private Chart chartWindow;
        private StackPanel buttonPanel;
        private Button onOffButton;
        private readonly List<Button> buttons = new List<Button>();

        #region Parametros

        [NinjaScriptProperty]
        [Range(2, 40)]
        [Display(Name = "Lineas objetivo", Order = 1, GroupName = "Paso automatico",
                 Description = "Lineas que se quieren ver en el alto visible. El paso sale de dividir el rango visible entre este numero y ajustarlo a la serie 1-2-5.")]
        public int TargetLines { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Paso manual (ticks)", Order = 2, GroupName = "Paso automatico",
                 Description = "0 = paso automatico por rango visible. Distinto de 0 fija el paso en ticks e ignora los botones.")]
        public int ManualStepTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 5)]
        [Display(Name = "Multiplicador inicial", Order = 3, GroupName = "Paso automatico",
                 Description = "Semilla del multiplicador: 0=x0.1 1=x0.2 2=x0.5 3=x1 4=x2 5=x5. Tras el primer clic mandan los botones del grafico.")]
        public int StartMultIndex { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Ancho de zona (% del paso)", Order = 1, GroupName = "Zonas",
                 Description = "Media banda a cada lado del nivel. 10 = la zona ocupa el 20 por ciento del paso.")]
        public int ZonePercent { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Opacidad de zona (%)", Order = 2, GroupName = "Zonas")]
        public int ZoneOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Color por encima del precio", Order = 3, GroupName = "Zonas")]
        public Brush UpBrush { get; set; }

        [Browsable(false)]
        public string UpBrushSerialize
        {
            get { return Serialize.BrushToString(UpBrush); }
            set { UpBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Color por debajo del precio", Order = 4, GroupName = "Zonas")]
        public Brush DownBrush { get; set; }

        [Browsable(false)]
        public string DownBrushSerialize
        {
            get { return Serialize.BrushToString(DownBrush); }
            set { DownBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Dibujar linea en el nivel", Order = 1, GroupName = "Lineas")]
        public bool DrawLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mostrar precio del nivel", Order = 2, GroupName = "Lineas")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Botones de paso en el grafico", Order = 3, GroupName = "Lineas")]
        public bool ShowStepButtons { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "AT Round Levels";
                Description              = "Zonas de numeros redondos. El paso se calcula del rango visible del grafico y se ajusta a la serie 1-2-5, asi que vale igual para futuros, forex, indices y metales sin tocar parametros.";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                IsChartOnly              = true;
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = true;
                // Los niveles no deben estirar la escala: se dibujan sobre lo que
                // ya hay. Con true, las bandas de los extremos moverian el grafico.
                IsAutoScale              = false;

                TargetLines              = 7;
                ManualStepTicks          = 0;
                StartMultIndex           = 3;
                ZonePercent              = 10;
                ZoneOpacity              = 12;
                UpBrush                  = Brushes.Firebrick;
                DownBrush                = Brushes.SeaGreen;
                DrawLines                = true;
                ShowLabels               = true;
                ShowStepButtons          = true;
            }
            else if (State == State.Configure)
            {
                multIndex = Math.Min(Mults.Length - 1, Math.Max(0, StartMultIndex));
            }
            else if (State == State.Historical)
            {
                // Los controles WPF solo se tocan desde el hilo de la UI.
                if (ShowStepButtons && ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(CreateButtons);
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(RemoveButtons);
            }
        }

        protected override void OnBarUpdate()
        {
            // Todo el trabajo esta en OnRender: no hay serie que calcular, y el
            // paso depende del zoom, no de las barras.
        }

        #region Botones

        private void CreateButtons()
        {
            if (buttonPanel != null || ChartControl == null) return;

            chartWindow = Window.GetWindow(ChartControl.Parent) as Chart;
            if (chartWindow == null) return;

            buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };

            // El texto dice el estado ("Rounds ON" / "Rounds OFF") en vez de
            // marcarlo con negrita: en esta barra los botones van sin borde y la
            // negrita a 11 px no se distingue.
            onOffButton = new Button
            {
                Content  = "Rounds ON",
                Margin   = new Thickness(1, 0, 6, 0),
                Padding  = new Thickness(5, 0, 5, 0),
                MinWidth = 70,
                FontSize = 11
            };
            onOffButton.Click += OnToggleClick;
            buttonPanel.Children.Add(onOffButton);

            for (int i = 0; i < Mults.Length; i++)
            {
                Button b = new Button
                {
                    Content  = "x" + Mults[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Tag      = i,
                    Margin   = new Thickness(1, 0, 1, 0),
                    Padding  = new Thickness(5, 0, 5, 0),
                    MinWidth = 34,
                    FontSize = 11
                };
                b.Click += OnMultClick;
                buttons.Add(b);
                buttonPanel.Children.Add(b);
            }
            HighlightActive();
            chartWindow.MainMenu.Add(buttonPanel);
        }

        private void RemoveButtons()
        {
            if (buttonPanel == null) return;
            foreach (Button b in buttons) b.Click -= OnMultClick;
            buttons.Clear();
            if (onOffButton != null) { onOffButton.Click -= OnToggleClick; onOffButton = null; }
            if (chartWindow != null) chartWindow.MainMenu.Remove(buttonPanel);
            buttonPanel = null;
            chartWindow = null;
        }

        private void OnMultClick(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            if (b == null || !(b.Tag is int)) return;
            multIndex = (int)b.Tag;
            HighlightActive();
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            drawEnabled = !drawEnabled;
            if (onOffButton != null) onOffButton.Content = drawEnabled ? "Rounds ON" : "Rounds OFF";
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        private void HighlightActive()
        {
            for (int i = 0; i < buttons.Count; i++)
                buttons[i].FontWeight = (i == multIndex) ? FontWeights.Bold : FontWeights.Normal;
        }

        #endregion

        // Ajusta v al valor mas cercano de la serie {1, 2, 5} x 10^n.
        private static double SnapNice125(double v)
        {
            if (v <= 0) return 0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double f   = v / mag;                                  // 1..10
            double n   = (f < 1.5) ? 1 : (f < 3.5) ? 2 : (f < 7.5) ? 5 : 10;
            return n * mag;
        }

        private double StepFor(ChartScale chartScale)
        {
            double tick = Instrument.MasterInstrument.TickSize;
            if (tick <= 0) tick = 0.01;

            if (ManualStepTicks > 0) return ManualStepTicks * tick;

            double range = chartScale.MaxValue - chartScale.MinValue;
            if (range <= 0) return 0;

            // Doble ajuste: el multiplicador de los botones podria sacar el paso
            // de la serie (2 x 2 = 4), asi que se vuelve a encajar en 1-2-5.
            double step = SnapNice125(SnapNice125(range / Math.Max(2, TargetLines)) * Mults[multIndex]);
            return Math.Max(step, tick);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (!drawEnabled) return;
            if (IsInHitTest || RenderTarget == null || ChartPanel == null || Bars == null) return;
            if (chartScale.MaxValue <= chartScale.MinValue) return;

            double step = StepFor(chartScale);
            if (step <= 0) return;

            int i0 = (int)Math.Floor(chartScale.MinValue / step);
            int i1 = (int)Math.Ceiling(chartScale.MaxValue / step);
            // Sanidad: con un tick minusculo y un paso manual absurdo esto seria
            // un bucle de millones de iteraciones en el hilo de render.
            if (i1 - i0 > 400) return;

            double last = LastPrice();
            float  x0   = ChartPanel.X;
            float  x1   = ChartPanel.X + ChartPanel.W;
            double half = step * Math.Max(1, ZonePercent) / 100.0;

            SharpDX.Direct2D1.Brush up   = UpBrush.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush down = DownBrush.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.StrokeStyleProperties props = new SharpDX.Direct2D1.StrokeStyleProperties();
            props.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
            SharpDX.Direct2D1.StrokeStyle stroke = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, props);
            SharpDX.DirectWrite.TextFormat font = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, chartControl.Properties.LabelFont.Family.ToString(), 11f);

            try
            {
                for (int i = i0; i <= i1; i++)
                {
                    double lv = i * step;
                    if (lv <= 0) continue;

                    SharpDX.Direct2D1.Brush brush = (lv >= last) ? up : down;

                    float yTop = chartScale.GetYByValue(lv + half);
                    float yBot = chartScale.GetYByValue(lv - half);
                    if (yBot < ChartPanel.Y || yTop > ChartPanel.Y + ChartPanel.H) continue;

                    brush.Opacity = ZoneOpacity / 100f;
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, yTop, x1 - x0, Math.Max(1f, yBot - yTop)), brush);

                    float y = chartScale.GetYByValue(lv);
                    if (DrawLines)
                    {
                        brush.Opacity = 0.75f;
                        RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, 1f, stroke);
                    }
                    if (ShowLabels)
                    {
                        brush.Opacity = 1f;
                        string txt = Instrument.MasterInstrument.FormatPrice(lv);
                        using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(
                                   NinjaTrader.Core.Globals.DirectWriteFactory, txt, font, LabelWidth, 14f))
                        {
                            // El mismo layout dos veces: a la izquierda pegado al
                            // borde y a la derecha alineado al final, para no leer
                            // el precio cruzando el grafico entero.
                            tl.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(x0 + 6f, y - 14f), tl, brush);
                            tl.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(x1 - LabelWidth - 6f, y - 14f), tl, brush);
                        }
                    }
                    brush.Opacity = 1f;
                }
            }
            finally
            {
                // ponytail: recursos por render, no cacheados. Son cuatro objetos
                // y el RenderTarget se recrea al cambiar de dispositivo; cachear
                // obliga a invalidarlos a mano y no ahorra nada visible.
                font.Dispose();
                stroke.Dispose();
                up.Dispose();
                down.Dispose();
            }
        }

        // Ultimo precio SIN el indexador de series (Close[0]): OnRender corre en
        // el hilo de render y puede entrar con CurrentBar < 0.
        private double LastPrice()
        {
            if (ChartBars != null && ChartBars.Bars != null && ChartBars.Bars.Count > 0)
                return ChartBars.Bars.GetClose(ChartBars.Bars.Count - 1);
            return 0;
        }
    }
}
