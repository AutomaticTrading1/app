// =============================================================================
//  AutomaticTradingNT8.cs  -  Puente NinjaTrader 8 -> ATPortfolio (AutomaticTrading)
// -----------------------------------------------------------------------------
//  AddOn CLIENTE (topologia contraria a documentation/reference/DmriBridgeAddOn.cs,
//  que es servidor JSON de otro proyecto). Aqui NT8 conecta como cliente a
//  socket_server.py:5006 y habla el protocolo `|`-texto que ya usan los EAs MT5
//  (ver documentation/GUIA_NT8_desde_te_mt5.md).
//
//  INSTALACION:
//    1. Copiar este archivo a Documents\NinjaTrader 8\bin\Custom\AddOns\
//       (o Tools -> Import -> NinjaScript Add-On...).
//    2. Ajustar la seccion CONFIGURACION (AccountName como minimo).
//    3. Compilar en NinjaScript Editor (F5). Validar en Sim101 antes de real
//       (ver documentation/GUIA_NT8_desde_te_mt5.md #7 - la leccion mas cara).
//
//  DOS MODOS DE GATEO (software comercial: soporta ambos a la vez):
//    A) PREVENTIVO (estrategias propias): la Strategy llama a
//       AutomaticTradingBridge.CheckTrade(...) antes de entrar y solo abre si
//       ALLOW. Control total, sin ventana de riesgo. Ver ATPGateDemo.cs.
//    B) REACTIVO (estrategias de terceros/cerradas): ReactiveMode=true. NT8 no
//       deja interceptar la orden ANTES de enviarse, asi que el AddOn vigila
//       OrderUpdate; al aparecer una entrada NO gestionada consulta al servidor
//       y si DENY cancela la orden (si sigue viva) o cierra la posicion (si ya
//       lleno). Las ordenes del modo A (Name "ATP_*") quedan exentas del modo B.
//
//  INTEGRACION DESDE UNA STRATEGY (modo A):
//    int magic;
//    string reason;
//    if (AutomaticTradingBridge.CheckTrade("MiEstrategia", 1, 0, ToTime(Time[0]), 1, out magic, out reason))
//    {
//        EnterLong(1, AutomaticTradingBridge.OrderTag("MiEstrategia", magic));
//        AutomaticTradingBridge.Release("MiEstrategia");
//    }
//  El "OrderTag" DEBE usarse como nombre/señal de entrada de la orden: es la
//  unica forma de que el bridge (y por tanto STATE) sepa a que magic
//  sintetico pertenece cada posicion — NT8 no tiene Magic Number nativo.
//
//  LIMITACION CONOCIDA (netting NT8 por cuenta+instrumento, ver guia #4.3):
//  v1 asume UNA estrategia NT8 por instrumento por cuenta. Con esa asuncion,
//  el mapeo magic<->instrumento es 1:1 y determinista; con varias estrategias
//  en el mismo instrumento NT8 las netea y esa asociacion se rompe. No se
//  resuelve aqui (tampoco esta resuelto en el proyecto hermano DMRI).
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class AutomaticTradingNT8 : AddOnBase
    {
        // ===================== CONFIGURACION =====================
        private const string ServerHost = "127.0.0.1";
        private const int ServerPort = 5006;
        private const string AccountName = "Sim101";   // EDITAR: cuenta/conexion NT8 (vacio "" = autodetectar la primera conectada)
        private const int StatePeriodMs = 2000;         // frecuencia de STATE (estado de cuenta)
        private const int PingPeriodMs = 5000;
        private const int CheckTradeTimeoutMs = 3000;

        // MODO REACTIVO: gatea estrategias de TERCEROS (que no llaman a
        // AutomaticTradingBridge.CheckTrade). NT8 no permite interceptar la
        // orden ANTES de enviarse, asi que este modo es reactivo: al aparecer
        // una entrada no gestionada, consulta al servidor y si DENY cancela la
        // orden (si sigue viva) o cierra la posicion (si ya lleno). Ponlo a
        // false si SOLO usas estrategias propias con CheckTrade explicito
        // (control preventivo, sin ventana reactiva).
        private const bool ReactiveMode = true;
        // ===========================================================

        private static AutomaticTradingNT8 _instance;
        private string _terminalId;
        private string _accountName;
        private Account _account;
        private volatile bool _running;

        // Magic sintetico por estrategia. NT8 no tiene Magic Number: se asigna
        // la primera vez que una strategy llama CheckTrade con un tag nuevo.
        // El magic se DERIVA del nombre de la estrategia con un hash estable
        // (FNV-1a 32 bits), no de un contador. Dos consecuencias buscadas:
        //   1. Es el mismo numero en cada arranque: una posicion abierta no
        //      pierde su identidad al reiniciar NT8.
        //   2. La app calcula el mismo magic desde el nombre del fichero .cs
        //      (ver nt8_manager.strategy_magic), asi que puede filtrar la copia
        //      de senales por estrategia sin ningun mensaje de protocolo extra.
        // REQUISITO: el strategyTag pasado a CheckTrade debe coincidir con el
        // nombre del fichero .cs (p.ej. "ATPGateDemo" <-> ATPGateDemo.cs).
        private const int MagicBase = 900001;
        private const int MagicRange = 90000;

        // magic -> ultimo instrumento operado con ese magic (para CMD_CLOSE y
        // para resolver el magic de una posicion en STATE). Ver limitacion de
        // netting en la cabecera del archivo.
        private readonly ConcurrentDictionary<int, string> _magicToInstrument = new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<string, int> _instrumentToMagic = new ConcurrentDictionary<string, int>();

        // Bracket SL/TP pendiente por instancia Order. Order.OrderId MUTA al
        // aceptar el broker el submit (GUID interno -> id real): NUNCA usarlo
        // como clave (gotcha real, causo posiciones sin SL/TP en el proyecto
        // hermano DMRI). La instancia Order es estable entre submit y eventos.
        private class BracketInfo
        {
            public NinjaTrader.Cbi.Instrument Instrument;
            public OrderAction ExitAction;
            public int Qty;
            public double Sl, Tp;
        }
        private readonly ConcurrentDictionary<Order, BracketInfo> _pendingBrackets = new ConcurrentDictionary<Order, BracketInfo>();

        // Modo reactivo: cola de ordenes de terceros pendientes de gatear + set
        // de las ya evaluadas (OnOrderUpdate dispara varias veces por orden).
        // Worker propio para no bloquear el hilo de eventos de NT8 con el
        // SendAndWait del socket.
        private readonly BlockingCollection<Order> _reactiveQueue = new BlockingCollection<Order>(new ConcurrentQueue<Order>());
        private readonly ConcurrentDictionary<Order, byte> _reactiveSeen = new ConcurrentDictionary<Order, byte>();
        private Thread _reactiveThread;

        // Streaming de ticks (CMD_STREAM 'Compartir local'): instrumentos cuyos
        // ticks se empujan al server (TICK|raiz|bid|ask|last) para que un EA/
        // indicador MT5 los lea via GET_TICK. Throttle por raiz.
        private readonly ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument> _streamed =
            new ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument>();
        private readonly ConcurrentDictionary<string, long> _lastTickSentMs =
            new ConcurrentDictionary<string, long>();
        private const int TickThrottleMs = 250;

        // Diagnostico del feed: que tipos de MarketData hemos visto por raiz, y
        // cuantos trades hemos enviado. Sin esto, "no llegan ticks" no distingue
        // entre "el instrumento no publica operaciones" y "el puente falla".
        private readonly ConcurrentDictionary<string, byte> _mdSeen =
            new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, long> _tradesSent =
            new ConcurrentDictionary<string, long>();

        // Historico de operaciones (CMD_HISTORY). El buffer de trades del servidor
        // vive en RAM y se pierde al reiniciar la app; NinjaTrader es el unico que
        // tiene el historico de verdad, asi que el EA del footprint lo pide al
        // arrancar y el AddOn lo vuelca con BarsRequest.
        //
        // Mientras se envia el historico, los trades EN VIVO de esa raiz se ENCOLAN
        // en vez de mandarse: el EA localiza los ticks de cada vela por biseccion y
        // eso exige orden cronologico. Si un trade de ahora se colara delante de un
        // trade de hace tres horas, la busqueda fallaria en silencio.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<KeyValuePair<long, string>>> _histQueue =
            new ConcurrentDictionary<string, ConcurrentQueue<KeyValuePair<long, string>>>();

        // Envio del historico troceado: 500k lineas seguidas por el socket bloquean
        // el hilo y dejan a NT8 sin responder. Se manda en lotes con una pausa.
        private const int HistChunk   = 5000;
        private const int HistPauseMs = 15;

        // Ventana MAXIMA que se le pide a NT8 por rango de fechas.
        //
        // El EA pide por VELAS: en un grafico D1 de 24 velas eso son ~4 SEMANAS de
        // ticks. Ese BarsRequest (x3: Bid, Ask, Last) no lo sirve NT8 en un tiempo
        // razonable, y mientras dura el volcado los trades en vivo se RETIENEN en
        // _histQueue — o sea que el puente deja de mandar nada y el footprint se
        // queda a cero indefinidamente ("Puente: sincronizando... 0", "Basis:
        // esperando tick..."), en ESE grafico y en todos los demas de la misma raiz.
        //
        // Mas atras de este tope se pide por NUMERO de ticks, que es exactamente lo
        // que el tope nTicks iba a dejar de todas formas: en MNQ, 500k operaciones
        // son unas 2-3 horas. No se pierde nada que el EA pudiera mostrar.
        private const int HistMaxHours = 12;

        // Streaming de profundidad L2/DOM (CMD_STREAM_DEPTH). Alto volumen:
        // throttle mayor.
        //
        // NO se mantiene libro propio. Se mantuvo (un SortedList por precio y lado,
        // alimentado con los eventos Add/Update/Remove) y era un error de diseno:
        // una copia que envejece. Los precios que NT8 deja de reportar al alejarse
        // el mercado no siempre traen su Remove, se quedaban dentro para siempre y
        // el envio los sacaba como si fueran libro vivo. El unico libro es el de
        // NinjaTrader, leido en OnMarketDepth.
        private readonly ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument> _depthStreamed =
            new ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument>();
        private readonly ConcurrentDictionary<string, long> _lastDepthSentMs =
            new ConcurrentDictionary<string, long>();
        // 250 ms: el DOM del footprint a 500 ms se veia a saltos. No se sube
        // DepthLevels de 10: mas niveles cuadruplicarian el trafico L2 por el
        // MISMO socket que drena los trades del footprint, y ese feed ya funciona.
        private const int DepthThrottleMs = 250;
        private const int DepthLevels = 10;

        // Conexion dedicada a STATE/PING/recepcion de comandos push (CMD_OPEN/
        // CMD_CLOSE/CMD_UPDATE_SLTP). Las llamadas CheckTrade de cada strategy
        // usan su PROPIA conexion (una por magic) porque el servidor identifica
        // al llamante por la conexion TCP (LOGIN fija el magic de ese socket),
        // igual que cada EA MT5 abre su propio socket.
        private SocketConn _bridge;
        private readonly ConcurrentDictionary<string, SocketConn> _strategyConns = new ConcurrentDictionary<string, SocketConn>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "AutomaticTrading NT8 Bridge";
            }
            else if (State == State.Configure)
            {
                _instance = this;
                Start();
            }
            else if (State == State.Terminated)
            {
                Stop();
                if (_instance == this) _instance = null;
            }
        }

        // ------------------------- ciclo de vida -------------------------

        private void Start()
        {
            _accountName = ResolveAccountName();
            _terminalId = "NT8_" + _accountName;
            _running = true;
            ResolveAccount();
            AttachAccount();

            _bridge = new SocketConn(ServerHost, ServerPort, _terminalId, _accountName, GetOrAssignMagic("__BRIDGE__"), OnPush, LogFunc);
            // La app es la duena de que se comparte: al (re)conectar soltamos todo y
            // esperamos a que nos lo vuelva a pedir (ver StopAllStreams).
            _bridge.OnLoggedIn = StopAllStreams;
            var t = new Thread(BridgeLoop) { IsBackground = true, Name = "AutomaticTradingNT8-Bridge" };
            t.Start();

            if (ReactiveMode)
            {
                _reactiveThread = new Thread(ReactiveLoop) { IsBackground = true, Name = "AutomaticTradingNT8-Reactive" };
                _reactiveThread.Start();
            }

        }

        private void Stop()
        {
            _running = false;
            DetachAccount();
            foreach (var kv in _streamed)
            {
                try { if (kv.Value != null) kv.Value.MarketData.Update -= OnMarketData; } catch { }
            }
            _streamed.Clear();
            foreach (var kv in _depthStreamed)
            {
                try { if (kv.Value != null) kv.Value.MarketDepth.Update -= OnMarketDepth; } catch { }
            }
            _depthStreamed.Clear();
            try { _reactiveQueue.CompleteAdding(); } catch { }
            _bridge?.Close();
            foreach (var c in _strategyConns.Values) c.Close();
            _strategyConns.Clear();
        }

        // Nombre de cuenta a usar: el configurado (AccountName) o, si esta vacio,
        // la primera cuenta conectada (autodeteccion, punto 5 productizacion).
        private string ResolveAccountName()
        {
            if (!string.IsNullOrEmpty(AccountName)) return AccountName;
            lock (Account.All)
            {
                var a = Account.All.FirstOrDefault(x => x.Connection != null) ?? Account.All.FirstOrDefault();
                return a != null ? a.Name : "Sim101";
            }
        }

        private void ResolveAccount()
        {
            if (string.IsNullOrEmpty(_accountName)) _accountName = ResolveAccountName();
            lock (Account.All)
                _account = Account.All.FirstOrDefault(a => a.Name == _accountName);
            if (_account == null)
                Log("AutomaticTradingNT8: cuenta '" + _accountName + "' no encontrada todavia.", LogLevel.Warning);
        }

        private void AttachAccount()
        {
            if (_account == null) ResolveAccount();
            if (_account == null) return;
            try
            {
                _account.OrderUpdate += OnOrderUpdate;
                _account.ExecutionUpdate += OnExecutionUpdate;
                RebuildMagicMapFromOrders();
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: error suscribiendo cuenta: " + ex.Message, LogLevel.Error); }
        }

        /// <summary>Reconstruye magic &lt;-&gt; instrumento leyendo las ordenes de la cuenta.
        ///
        /// _magicToInstrument vive en memoria y se pierde al reiniciar/recompilar el
        /// AddOn. Sin esto, un CMD_CLOSE posterior no encuentra el magic, retorna en
        /// silencio y la posicion copiada se queda ABIERTA para siempre.
        ///
        /// Las ordenes que coloca el puente llevan el tag "ATP_&lt;magic&gt;", asi que la
        /// asociacion se recupera de la propia cuenta, sin persistir nada.
        /// </summary>
        private void RebuildMagicMapFromOrders()
        {
            if (_account == null) return;
            int restored = 0;
            try
            {
                foreach (var o in _account.Orders.ToList())
                {
                    if (o == null || o.Instrument == null) continue;
                    string nm = o.Name ?? "";
                    if (!nm.StartsWith("ATP_", StringComparison.Ordinal)) continue;

                    int magic;
                    // "ATP_flatten", "ATP_bracket", "ATP_reactive_close" no parsean: se ignoran.
                    if (!int.TryParse(nm.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out magic))
                        continue;

                    CacheInstrument(o.Instrument);
                    string root = RootSymbol(o.Instrument);
                    _magicToInstrument[magic] = root;
                    _instrumentToMagic[root] = magic;
                    restored++;
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: RebuildMagicMapFromOrders: " + ex.Message, LogLevel.Warning); }

            if (restored > 0)
                Log("AutomaticTradingNT8: recuperadas " + restored + " asociaciones magic->instrumento de las ordenes de la cuenta.", LogLevel.Information);
        }

        private void DetachAccount()
        {
            if (_account == null) return;
            try { _account.OrderUpdate -= OnOrderUpdate; } catch { }
            try { _account.ExecutionUpdate -= OnExecutionUpdate; } catch { }
        }

        // Bucle dueño de la conexion "bridge": conecta con backoff, y mientras
        // esta conectada manda PING+STATE periodicos. Un socket muerto NO debe
        // bloquear reintentos (gotcha guia #4.5).
        private void BridgeLoop()
        {
            int failCount = 0;
            while (_running)
            {
                if (!_bridge.IsLoggedIn)
                {
                    if (_bridge.Connect())
                    {
                        failCount = 0;
                        Log("AutomaticTradingNT8: bridge conectado (" + _terminalId + ").", LogLevel.Information);
                    }
                    else
                    {
                        failCount++;
                        int waitSec = Math.Min(60, 5 * failCount);
                        if (failCount == 1 || failCount % 5 == 0)
                            Log("AutomaticTradingNT8: fallo conexion bridge (" + failCount + "). Reintento en " + waitSec + "s.", LogLevel.Warning);
                        Thread.Sleep(waitSec * 1000);
                        continue;
                    }
                }

                Thread.Sleep(Math.Min(PingPeriodMs, StatePeriodMs));
                if (_bridge.IsLoggedIn)
                {
                    _bridge.SendRaw("PING");
                    SendState();
                }
            }
        }

        // Mensajes que llegan por push (no son respuesta a un SendAndWait):
        // hoy solo CMD_OPEN/CMD_CLOSE/CMD_UPDATE_SLTP.
        private void OnPush(string line)
        {
            try { HandleCommand(line); }
            catch (Exception ex) { Log("AutomaticTradingNT8: HandleCommand '" + line + "': " + ex.Message, LogLevel.Error); }
        }

        private void LogFunc(string msg, bool warn)
        {
            Log("AutomaticTradingNT8: " + msg, warn ? LogLevel.Warning : LogLevel.Information);
        }

        // ------------------------- API publica (llamada desde Strategies) -------------------------

        /// <summary>
        /// Gate de riesgo antes de abrir. type: 0=Buy, 1=Sell. barTime: NT8 no tiene
        /// iTime() como MT5; pasar Time[0].Ticks o ToTime(Time[0]) desde la strategy.
        /// priority: 0 = no ocupa el semaforo de turnos (igual que PreOpenCheck en MT5).
        /// </summary>
        public static bool CheckTrade(string strategyTag, double lots, int type, long barTime, int priority, out int magic, out string reason)
        {
            magic = 0;
            reason = "";
            var inst = _instance;
            if (inst == null) { reason = "bridge_not_loaded"; return true; } // fail-open: AddOn no cargado
            return inst.CheckTradeInternal(strategyTag, lots, type, barTime, priority, out magic, out reason);
        }

        public static void Release(string strategyTag)
        {
            _instance?.ReleaseInternal(strategyTag);
        }

        /// <summary>Nombre/señal de entrada a usar en EnterLong/EnterShort para que STATE
        /// pueda asociar la posicion resultante a este magic sintetico.</summary>
        public static string OrderTag(string strategyTag, int magic)
        {
            return "ATP_" + magic;
        }

        private bool CheckTradeInternal(string strategyTag, double lots, int type, long barTime, int priority, out int magic, out string reason)
        {
            reason = "";
            int magicLocal = GetOrAssignMagic(strategyTag);
            magic = magicLocal;
            // magicLocal (no el parametro `out magic`) porque C# prohibe capturar
            // parametros ref/out en una lambda (CS1628).
            var conn = _strategyConns.GetOrAdd(strategyTag, _ =>
                new SocketConn(ServerHost, ServerPort, _terminalId, _accountName, magicLocal, OnPush, LogFunc));

            if (!conn.IsLoggedIn && !conn.Connect())
            {
                reason = "no_socket";
                return conn.EverConnected == false; // nunca conecto -> asumir app cerrada, operar libre
            }

            string msg = string.Format(CultureInfo.InvariantCulture, "CHECK_TRADE|{0}|{1:F2}|{2}|{3}", type, lots, barTime, priority);
            string response = conn.SendAndWait(msg, CheckTradeTimeoutMs);
            if (response.StartsWith("ALLOW", StringComparison.Ordinal))
            {
                _instrumentToMagic[strategyTag] = magic; // best-effort, se refina en OnExecutionUpdate
                return true;
            }
            reason = response;
            return false;
        }

        private void ReleaseInternal(string strategyTag)
        {
            int magic = GetOrAssignMagic(strategyTag);
            SocketConn conn;
            if (_strategyConns.TryGetValue(strategyTag, out conn) && conn.IsLoggedIn)
                conn.SendRaw("RELEASE|" + magic);
        }

        /// <summary>Magic determinista a partir del nombre. FNV-1a 32 bits.
        /// Debe dar EXACTAMENTE el mismo numero que nt8_manager.strategy_magic()
        /// en Python (hay un test que lo comprueba).</summary>
        private static int GetOrAssignMagic(string strategyTag)
        {
            uint h = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(strategyTag))
            {
                h ^= b;
                h *= 16777619u;
            }
            return MagicBase + (int)(h % MagicRange);
        }

        // ------------------------- STATE -------------------------

        private void SendState()
        {
            if (_account == null) { ResolveAccount(); if (_account == null) return; }
            try
            {
                double balance = SafeGet(AccountItem.CashValue);
                double equity = balance + SafeGet(AccountItem.UnrealizedProfitLoss);
                double margin = SafeGet(AccountItem.InitialMargin);
                double buyingPower = SafeGet(AccountItem.BuyingPower);
                double marginLevel = margin > 0 ? (equity / margin) * 100.0 : 0.0;

                var sb = new StringBuilder();
                sb.Append("STATE|").Append(Num(balance)).Append('|').Append(Num(equity)).Append('|')
                  .Append(Num(margin)).Append('|').Append(Num(marginLevel)).Append('|');

                bool first = true;
                foreach (var p in _account.Positions.Where(p => p.MarketPosition != MarketPosition.Flat))
                {
                    if (!first) sb.Append(';');
                    first = false;
                    // Simbolo RAIZ (roll-stable): "MNQ", no "MNQ 09-26". Asi las
                    // reglas de copia y los mapeos no se rompen al rolar de
                    // contrato. Las ordenes se ejecutan resolviendo el frontal.
                    CacheInstrument(p.Instrument);
                    string instrKey = RootSymbol(p.Instrument);
                    int magic;
                    if (!_instrumentToMagic.TryGetValue(instrKey, out magic)) magic = 0;
                    // "ticket" sintetico: NT8 netea por instrumento, no hay id de posicion
                    // estable como el ticket de MT5. Determinista para un mismo
                    // (instrumento, magic) — ver limitacion de netting en la cabecera.
                    //
                    // SIEMPRE POSITIVO: al copiar NT8 -> MT5 este ticket viaja como
                    // `magic` de la posicion destino, y el magic de MT5 es ulong. Un
                    // valor negativo (GetHashCode devuelve int con signo) seria
                    // invalido para order_send.
                    int ticket = unchecked(instrKey.GetHashCode() ^ (magic * 397)) & 0x7FFFFFFF;
                    if (ticket == 0) ticket = 1;   // 0 esta reservado a "sin magic"
                    string type = p.MarketPosition == MarketPosition.Long ? "BUY" : "SELL";
                    double unrealized = 0;
                    try { unrealized = p.GetUnrealizedProfitLoss(PerformanceUnit.Currency); } catch { }

                    sb.Append(ticket).Append(':').Append(instrKey).Append(':').Append(type).Append(':')
                      .Append(Num(Math.Abs(p.Quantity))).Append(':').Append(Num(p.AveragePrice)).Append(':')
                      .Append("0:0:").Append(Num(unrealized)).Append(':').Append(magic);
                }

                _bridge.SendRaw(sb.ToString());
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: error enviando STATE: " + ex.Message, LogLevel.Warning); }
        }

        private double SafeGet(AccountItem item)
        {
            try { return _account.Get(item, Currency.UsDollar); } catch { return 0.0; }
        }

        // ------------------------- eventos de cuenta -------------------------

        // El bracket SL/TP se coloca en OnOrderUpdate cuando OrderState==Filled,
        // NUNCA en OnExecutionUpdate: la ejecucion puede llegar ANTES de que el
        // estado pase a Filled, y el chequeo fallaria en silencio (gotcha real,
        // dejo posiciones reales sin SL/TP en el proyecto hermano DMRI).
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e == null || e.Order == null) return;

                // Modo reactivo: encolar entradas de terceros (no ATP_) para
                // gatearlas. Se hace en cuanto la orden es aceptada/trabajando,
                // no solo al Filled, para tener la maxima ventana de cancelar
                // antes de que llene. El worker decide (una vez por orden).
                if (ReactiveMode)
                {
                    string nm = e.Order.Name ?? "";
                    if (!nm.StartsWith("ATP_", StringComparison.Ordinal) &&
                        (e.OrderState == OrderState.Accepted || e.OrderState == OrderState.Working ||
                         e.OrderState == OrderState.Submitted || e.OrderState == OrderState.Filled) &&
                        _reactiveSeen.TryAdd(e.Order, 1))
                    {
                        try { _reactiveQueue.Add(e.Order); } catch { }
                    }
                }

                if (e.OrderState != OrderState.Filled) return;

                BracketInfo bi;
                if (_pendingBrackets.TryRemove(e.Order, out bi))
                    PlaceBracket(bi);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnOrderUpdate: " + ex.Message, LogLevel.Error); }
        }

        // ------------------------- modo reactivo (estrategias de terceros) -------------------------

        private void ReactiveLoop()
        {
            try
            {
                foreach (var order in _reactiveQueue.GetConsumingEnumerable())
                {
                    try { ReactiveGate(order); }
                    catch (Exception ex) { Log("AutomaticTradingNT8: ReactiveGate: " + ex.Message, LogLevel.Error); }
                }
            }
            catch (Exception ex) { if (_running) Log("AutomaticTradingNT8: ReactiveLoop: " + ex.Message, LogLevel.Warning); }
        }

        private void ReactiveGate(Order o)
        {
            if (!IsEntryOrder(o)) return;   // cierres/reducciones: siempre permitidos
            if (_account == null) return;

            int type = o.OrderAction == OrderAction.SellShort ? 1 : 0;
            double lots = o.Quantity;
            long barTime = DateTime.Now.Ticks;

            // priority=0: no ocupa el semaforo de turnos, solo valida gates
            // globales + limites (DD/lotes/margen/emergencia/deshabilitado),
            // igual que el PreOpenCheck del EA MT5.
            string resp = "ALLOW";
            if (_bridge != null && _bridge.IsLoggedIn)
            {
                string msg = string.Format(CultureInfo.InvariantCulture, "CHECK_TRADE|{0}|{1:F2}|{2}|0", type, lots, barTime);
                resp = _bridge.SendAndWait(msg, CheckTradeTimeoutMs);
            }

            if (resp.StartsWith("ALLOW", StringComparison.Ordinal)) return;

            Log("AutomaticTradingNT8: REACTIVO DENY -> cancelar/cerrar " + o.Instrument.FullName + " (" + resp + ")", LogLevel.Warning);
            CancelOrFlatten(o);
        }

        // Entrada = abre o aumenta posicion. Cierre/reduccion = permitir siempre.
        private bool IsEntryOrder(Order o)
        {
            var pos = _account.Positions.FirstOrDefault(p => p.Instrument == o.Instrument);
            var mp = pos != null ? pos.MarketPosition : MarketPosition.Flat;
            if (o.OrderAction == OrderAction.Buy) return mp != MarketPosition.Short;       // abre/aumenta long (no cierra un short)
            if (o.OrderAction == OrderAction.SellShort) return mp != MarketPosition.Long;  // abre/aumenta short
            return false; // Sell, BuyToCover = cierres
        }

        private void CancelOrFlatten(Order o)
        {
            try
            {
                // 1) Si la orden sigue viva, cancelarla (evita que llene).
                if (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted ||
                    o.OrderState == OrderState.Submitted)
                {
                    _account.Cancel(new[] { o });
                }
                // 2) Si ya habia posicion (o lleno mientras ibamos), aplanarla.
                //    NT8 netea por instrumento: esto cierra TODA la posicion del
                //    instrumento (ver limitacion de netting en la cabecera).
                var pos = _account.Positions.FirstOrDefault(p => p.Instrument == o.Instrument && p.MarketPosition != MarketPosition.Flat);
                if (pos != null)
                {
                    var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    var close = _account.CreateOrder(pos.Instrument, action, OrderType.Market, OrderEntry.Automated,
                        TimeInForce.Gtc, Math.Abs(pos.Quantity), 0, 0, string.Empty, "ATP_reactive_close",
                        Core.Globals.MaxDate, null);
                    _account.Submit(new[] { close });
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CancelOrFlatten: " + ex.Message, LogLevel.Warning); }
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (e == null || e.Execution == null || e.Execution.Order == null) return;
                string name = e.Execution.Order.Name ?? "";
                if (name.StartsWith("ATP_", StringComparison.Ordinal))
                {
                    int magic;
                    if (int.TryParse(name.Substring(4), NumberStyles.Any, CultureInfo.InvariantCulture, out magic))
                    {
                        CacheInstrument(e.Execution.Instrument);
                        string instrKey = RootSymbol(e.Execution.Instrument);
                        _magicToInstrument[magic] = instrKey;
                        _instrumentToMagic[instrKey] = magic;
                    }
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnExecutionUpdate: " + ex.Message, LogLevel.Error); }
        }

        private void PlaceBracket(BracketInfo b)
        {
            if (_account == null) return;
            try
            {
                string oco = "ATP_OCO_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var children = new System.Collections.Generic.List<Order>();
                if (b.Sl > 0)
                    children.Add(_account.CreateOrder(b.Instrument, b.ExitAction, OrderType.StopMarket,
                        OrderEntry.Automated, TimeInForce.Gtc, b.Qty, 0, b.Sl, oco, "ATP_bracket",
                        Core.Globals.MaxDate, null));
                if (b.Tp > 0)
                    children.Add(_account.CreateOrder(b.Instrument, b.ExitAction, OrderType.Limit,
                        OrderEntry.Automated, TimeInForce.Gtc, b.Qty, b.Tp, 0, oco, "ATP_bracket",
                        Core.Globals.MaxDate, null));
                if (children.Count > 0) _account.Submit(children.ToArray());
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: PlaceBracket: " + ex.Message, LogLevel.Warning); }
        }

        // ------------------------- comandos push (CMD_OPEN/CMD_CLOSE/CMD_UPDATE_SLTP) -------------------------
        // Destino de copia de señales MT5->NT8: server->NT8, ejecutado por el AddOn
        // directamente sobre la cuenta (no requiere ninguna Strategy activa).

        private void HandleCommand(string line)
        {
            var parts = line.Split('|');
            string cmd = parts[0];

            if (cmd == "CMD_OPEN" && parts.Length >= 7)
            {
                string symbol = parts[1];
                int type = int.Parse(parts[2], CultureInfo.InvariantCulture);
                double volume = ParseD(parts[3]);
                double sl = ParseD(parts[4]);
                double tp = ParseD(parts[5]);
                int magic = (int)ParseD(parts[6]);
                ExecuteOpen(symbol, type, volume, sl, tp, magic);
            }
            else if (cmd == "CMD_CLOSE" && parts.Length >= 2)
            {
                int magic = (int)ParseD(parts[1]);
                ExecuteClose(magic);
            }
            else if (cmd == "CMD_UPDATE_SLTP" && parts.Length >= 4)
            {
                int magic = (int)ParseD(parts[1]);
                double sl = ParseD(parts[2]);
                double tp = ParseD(parts[3]);
                ExecuteUpdateSlTp(magic, sl, tp);
            }
            else if (cmd == "CMD_FLATTEN")
            {
                FlattenAll();
            }
            else if (cmd == "CMD_STREAM" && parts.Length >= 2)
            {
                StartStream(parts[1]);
            }
            else if (cmd == "CMD_STOP_STREAM" && parts.Length >= 2)
            {
                StopStream(parts[1]);
            }
            else if (cmd == "CMD_STREAM_DEPTH" && parts.Length >= 2)
            {
                StartDepthStream(parts[1]);
            }
            else if (cmd == "CMD_STOP_STREAM_DEPTH" && parts.Length >= 2)
            {
                StopDepthStream(parts[1]);
            }
            else if (cmd == "CMD_HISTORY" && parts.Length >= 2)
            {
                // CMD_HISTORY|<sym>|<MaxTicks>|<FromUtcMs>
                int nTicks;
                if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer,
                                                     CultureInfo.InvariantCulture, out nTicks))
                    nTicks = 200000;
                long fromMs;
                if (parts.Length < 4 || !long.TryParse(parts[3], NumberStyles.Integer,
                                                      CultureInfo.InvariantCulture, out fromMs))
                    fromMs = 0;
                StartHistory(parts[1], nTicks, fromMs);
            }
            else if (cmd == "CMD_PROFILE" && parts.Length >= 3)
            {
                // CMD_PROFILE|<sym>|<b0>,<b1>,...,<bN>
                // Las N+1 marcas (UTC ms, ascendentes) son las FRONTERAS de las N
                // velas del grafico de MT5. Se mandan explicitas y no como
                // "desde + paso" porque las velas de MT5 no son uniformes: fines
                // de semana, festivos y horario de verano abren huecos.
                StartProfile(parts[1], parts[2]);
            }
        }

        // ------------------------- historico de operaciones (CMD_HISTORY) -------------------------

        // Pide a NT8 las ultimas N operaciones ejecutadas y las vuelca al puente como
        // TRADE| normales. El agresor se reconstruye igual que en vivo (Lee-Ready):
        // se piden TAMBIEN las series historicas de Bid y Ask y, para cada operacion,
        // se compara su precio con la cotizacion vigente en ESE instante.
        //
        // Si el feed no sirve historico de bid/ask (depende del proveedor), se envia
        // side=0 y el EA cae a la tick-rule (direccion del precio), que es lo que hace
        // con cualquier CFD. Peor, pero no falso: no inventamos el agresor.
        private void StartHistory(string symbol, int nTicks, long fromMs)
        {
            var instr = ResolveInstrument(symbol);
            if (instr == null)
            {
                Log("AutomaticTradingNT8: CMD_HISTORY instrumento no resuelto: " + symbol, LogLevel.Warning);
                return;
            }
            string root = RootSymbol(instr);
            if (_histQueue.ContainsKey(root)) return;   // ya hay un volcado en curso

            // Acotar la ventana: ver HistMaxHours. Se hace AQUI, una vez, para que
            // las tres peticiones (Bid, Ask, Last) y el log usen el mismo rango.
            long minMs = new DateTimeOffset(DateTime.UtcNow.AddHours(-HistMaxHours))
                         .ToUnixTimeMilliseconds();
            if (fromMs > 0 && fromMs < minMs)
            {
                Log("AutomaticTradingNT8: " + root + " — historico pedido desde " +
                    DateTimeOffset.FromUnixTimeMilliseconds(fromMs).LocalDateTime
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                    ", por encima del tope de " + HistMaxHours + " h. Se sirven las ultimas " +
                    nTicks + " operaciones.", LogLevel.Information);
                fromMs = 0;                              // por numero de ticks
            }

            // A partir de aqui, los trades en vivo de esta raiz se encolan (ver OnMarketData).
            _histQueue[root] = new ConcurrentQueue<KeyValuePair<long, string>>();

            Log("AutomaticTradingNT8: CMD_HISTORY — pidiendo a NT8 el historico de " + root +
                " (" + instr.FullName + ") " +
                (fromMs > 0
                 ? "desde " + DateTimeOffset.FromUnixTimeMilliseconds(fromMs).LocalDateTime
                             .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                 : "(ultimas " + nTicks + " operaciones)") + "...", LogLevel.Information);

            var bidT = new List<long>();   var bidP = new List<double>();
            var askT = new List<long>();   var askP = new List<double>();

            // Encadenado Bid -> Ask -> Last: las cotizaciones deben estar cargadas
            // antes de recorrer las operaciones, para poder fechar el agresor.
            LoadTickSeries(instr, nTicks, fromMs, MarketDataType.Bid, bidT, bidP, () =>
                LoadTickSeries(instr, nTicks, fromMs, MarketDataType.Ask, askT, askP, () =>
                    SendTradeHistory(instr, root, nTicks, fromMs, bidT, bidP, askT, askP)));
        }

        // BarsRequest de ticks: por RANGO DE FECHAS si el EA manda una (pide por velas,
        // que es lo que necesita), o por numero de ticks si no.
        //
        // OJO: MarketDataType va en BarsPeriod, NO en BarsRequest (que no tiene esa
        // propiedad: da CS0117 'BarsRequest does not contain a definition for
        // MarketDataType'). Y el constructor por fechas las toma en hora LOCAL.
        private static BarsRequest NewTickRequest(NinjaTrader.Cbi.Instrument instr, int nTicks,
                                                  long fromMs, MarketDataType mdt)
        {
            var period = new BarsPeriod
            {
                BarsPeriodType = BarsPeriodType.Tick,
                Value          = 1,
                MarketDataType = mdt
            };
            var req = (fromMs > 0)
                      ? new BarsRequest(instr, DateTimeOffset.FromUnixTimeMilliseconds(fromMs).LocalDateTime,
                                        DateTime.Now)
                      : new BarsRequest(instr, nTicks);
            req.BarsPeriod = period;
            return req;
        }

        // Carga una serie de ticks (Bid/Ask) en las listas dadas y llama a 'next'.
        private void LoadTickSeries(NinjaTrader.Cbi.Instrument instr, int nTicks, long fromMs,
                                    MarketDataType mdt,
                                    List<long> outT, List<double> outP, Action next)
        {
            try
            {
                var req = NewTickRequest(instr, nTicks, fromMs, mdt);
                req.Request((r, err, msg) =>
                {
                    try
                    {
                        if (err == ErrorCode.NoError && r != null && r.Bars != null)
                        {
                            var bars = r.Bars;
                            int n = bars.Count;
                            for (int i = 0; i < n; i++)
                            {
                                outT.Add(new DateTimeOffset(bars.GetTime(i).ToUniversalTime()).ToUnixTimeMilliseconds());
                                outP.Add(bars.GetClose(i));
                            }
                        }
                        else
                        {
                            Log("AutomaticTradingNT8: sin historico de " + mdt + " (" + msg + "). " +
                                "El agresor de las velas historicas se deducira por tick-rule.", LogLevel.Information);
                        }
                    }
                    finally
                    {
                        if (r != null) r.Dispose();
                        next();
                    }
                });
            }
            catch (Exception ex)
            {
                Log("AutomaticTradingNT8: historico " + mdt + ": " + ex.Message, LogLevel.Warning);
                next();
            }
        }

        // Recorre las operaciones historicas, les asigna agresor con las cotizaciones
        // de su instante y las envia por lotes. Al terminar, vuelca la cola de vivos.
        private void SendTradeHistory(NinjaTrader.Cbi.Instrument instr, string root, int nTicks,
                                      long fromMs,
                                      List<long> bidT, List<double> bidP,
                                      List<long> askT, List<double> askP)
        {
            try
            {
                var req = NewTickRequest(instr, nTicks, fromMs, MarketDataType.Last);
                req.Request((r, err, msg) =>
                {
                    long lastTs = 0;
                    int  sent   = 0;
                    try
                    {
                        if (err != ErrorCode.NoError || r == null || r.Bars == null)
                        {
                            Log("AutomaticTradingNT8: NT8 no dio historico de operaciones de " + root +
                                " (" + msg + "). El footprint se llenara solo en tiempo real.", LogLevel.Warning);
                            return;
                        }

                        var bars = r.Bars;
                        int n = bars.Count;
                        int bi = 0, ai = 0;

                        // Pedido por fechas: NT8 puede devolver muchisimos mas trades de
                        // los que caben en memoria (100 velas M5 de MNQ son ~2,2M). Se
                        // envian los MAS RECIENTES hasta el tope: son los que el EA va a
                        // poder mostrar, y ademas los que sobrevivirian a la cola.
                        int first = (nTicks > 0 && n > nTicks) ? n - nTicks : 0;
                        if (first > 0)
                            Log("AutomaticTradingNT8: " + root + " — el rango pedido tiene " + n +
                                " operaciones, por encima del tope de " + nTicks +
                                ". Se envian las mas recientes.", LogLevel.Information);

                        for (int i = first; i < n; i++)
                        {
                            if (!_running || _bridge == null || !_bridge.IsLoggedIn) break;

                            long   tms   = new DateTimeOffset(bars.GetTime(i).ToUniversalTime()).ToUnixTimeMilliseconds();
                            double price = bars.GetClose(i);
                            double vol   = bars.GetVolume(i);

                            // Cotizacion vigente en el instante del trade: las tres series
                            // van en orden cronologico, asi que basta con avanzar punteros.
                            while (bi + 1 < bidT.Count && bidT[bi + 1] <= tms) bi++;
                            while (ai + 1 < askT.Count && askT[ai + 1] <= tms) ai++;
                            double bid = (bidT.Count > 0 && bidT[bi] <= tms) ? bidP[bi] : 0;
                            double ask = (askT.Count > 0 && askT[ai] <= tms) ? askP[ai] : 0;

                            int side = 0;
                            if (ask > 0 && price >= ask) side = 1;
                            else if (bid > 0 && price <= bid) side = -1;

                            _bridge.SendRaw("TRADE|" + root + "|" + tms + "|" + Num(price) + "|" +
                                            Num(vol) + "|" + Num(bid) + "|" + Num(ask) + "|" + side);
                            lastTs = tms;
                            sent++;

                            if (sent % HistChunk == 0) Thread.Sleep(HistPauseMs);
                        }

                        Log("AutomaticTradingNT8: historico de " + root + " enviado: " + sent +
                            " operaciones" + (bidT.Count > 0 && askT.Count > 0
                                              ? " (agresor real, con bid/ask de cada instante)."
                                              : " (agresor por tick-rule: el feed no sirve bid/ask historico)."),
                            LogLevel.Information);
                    }
                    finally
                    {
                        if (r != null) r.Dispose();
                        FlushLiveQueue(root, lastTs);
                        // Avisar de que el volcado ha terminado. Sin esto, el EA se cree
                        // al dia en cuanto un sondeo le devuelve menos trades de los que
                        // pidio, cosa que pasa SIEMPRE en los primeros segundos: NT8 tarda
                        // ~1 min en servir el BarsRequest, asi que el EA haria el backfill
                        // con los cuatro trades en vivo que hubiera y no lo repetiria.
                        if (_bridge != null && _bridge.IsLoggedIn)
                            _bridge.SendRaw("HISTORY_DONE|" + root);
                    }
                });
            }
            catch (Exception ex)
            {
                Log("AutomaticTradingNT8: historico de operaciones: " + ex.Message, LogLevel.Warning);
                FlushLiveQueue(root, 0);
                if (_bridge != null && _bridge.IsLoggedIn)
                    _bridge.SendRaw("HISTORY_DONE|" + root);   // fallo: que el EA no espere para siempre
            }
        }

        // Vuelca los trades en vivo retenidos durante el volcado del historico,
        // descartando los que el historico ya cubria (llegan por las dos vias).
        private void FlushLiveQueue(string root, long histLastTs)
        {
            ConcurrentQueue<KeyValuePair<long, string>> q;
            if (!_histQueue.TryRemove(root, out q) || q == null) return;

            int sent = 0, dup = 0;
            KeyValuePair<long, string> item;
            while (q.TryDequeue(out item))
            {
                if (item.Key <= histLastTs) { dup++; continue; }
                if (_bridge != null && _bridge.IsLoggedIn) _bridge.SendRaw(item.Value);
                sent++;
            }
            if (sent > 0 || dup > 0)
                Log("AutomaticTradingNT8: " + root + " — " + sent + " trades en vivo retenidos durante el " +
                    "historico enviados (" + dup + " ya venian en el historico).", LogLevel.Information);
        }

        // ------------------------- perfil agregado (CMD_PROFILE) -------------------------
        //
        // POR QUE EXISTE ESTO, aparte del historico de ticks de arriba.
        //
        // El camino de ticks manda CADA operacion por el socket. En MNQ son ~197k
        // por hora, asi que el tope de 500k cubre unas 2,5 h. Para las 24 velas que
        // pide el footprint eso llega hasta M6; de M10 para arriba el rango no cabe
        // ni de lejos (24 velas D1 son 4 semanas, decenas de millones de trades).
        // Y el intento de servirlo por ticks era peor que lento: las dos series de
        // cotizacion (Bid y Ask suman 2,8 GB en disco solo para 26 dias) se cargaban
        // ENTERAS en RAM antes de mandar nada.
        //
        // Aqui no se mandan operaciones: se manda la ESCALERA ya agregada por vela,
        // que es lo que el footprint dibuja. Una vela D1 de MNQ son ~2400 niveles
        // (~48 KB) en vez de millones de trades.
        //
        // Y el agresor no lo deducimos nosotros: lo pone NinjaTrader. Las barras
        // Volumetric (Order Flow+) ya traen el reparto bid/ask por nivel, que es
        // exactamente el trabajo para el que antes cargabamos Bid y Ask.
        //
        // DISCIPLINA DE MEMORIA (la leccion del atasco del 11-08-2026): se recorre
        // en streaming. Los minutos entran en orden, se acumulan en el diccionario
        // de la vela EN CURSO y al cruzar la frontera esa vela se emite y se libera.
        // El pico de RAM es UNA escalera, no la serie entera. Nunca materializar.

        // Raices con un perfil en vuelo. Sin esto, dos graficos pidiendo a la vez
        // lanzarian dos BarsRequest sobre lo mismo.
        private readonly ConcurrentDictionary<string, byte> _profileBusy =
            new ConcurrentDictionary<string, byte>();

        private void StartProfile(string symbol, string boundsCsv)
        {
            var instr = ResolveInstrument(symbol);
            if (instr == null)
            {
                Log("AutomaticTradingNT8: CMD_PROFILE instrumento no resuelto: " + symbol, LogLevel.Warning);
                return;
            }
            string root = RootSymbol(instr);

            // Fronteras: N+1 marcas => N velas.
            var parts = boundsCsv.Split(',');
            var bounds = new List<long>(parts.Length);
            foreach (var p in parts)
            {
                long v;
                if (long.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    bounds.Add(v);
            }
            bounds.Sort();
            if (bounds.Count < 2)
            {
                Log("AutomaticTradingNT8: CMD_PROFILE de " + root + " sin fronteras validas.", LogLevel.Warning);
                SendProfileDone(root, 0);
                return;
            }

            if (!_profileBusy.TryAdd(root, 0))
            {
                Log("AutomaticTradingNT8: CMD_PROFILE de " + root + " ignorado, ya hay uno en vuelo.",
                    LogLevel.Information);
                return;
            }

            int nBars = bounds.Count - 1;
            Log("AutomaticTradingNT8: CMD_PROFILE — perfil de " + root + " (" + instr.FullName + "): " +
                nBars + " velas, de " +
                DateTimeOffset.FromUnixTimeMilliseconds(bounds[0]).LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " a " +
                DateTimeOffset.FromUnixTimeMilliseconds(bounds[nBars]).LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "...",
                LogLevel.Information);

            try
            {
                // Resolucion MINUTO, igual que el "Order Flow Volume Profile" de NT8.
                // El perfil no se construye desde ticks crudos: desde velas de un
                // minuto ya volumetricas, que es de donde sale la velocidad.
                //
                // OJO (confirmar en la primera compilacion): en el BarsPeriod de
                // Volumetric, 'Value' son las MARCAS POR NIVEL (el "Marcas por
                // nivel" del dialogo, que dejamos en 1 = maxima resolucion) y el
                // periodo base va en BaseBarsPeriodType/BaseBarsPeriodValue.
                // Se deja en 1 a proposito: cuantizar aqui obligaria a repedir el
                // perfil cada vez que el EA cambia su paso de celda (que se adapta
                // al ATR y al zoom). Cuantiza el EA.
                var period = new BarsPeriod
                {
                    BarsPeriodType      = BarsPeriodType.Volumetric,
                    BarsPeriodTypeName  = "Volumetric",
                    BaseBarsPeriodType  = BarsPeriodType.Minute,
                    BaseBarsPeriodValue = 1,
                    Value               = 1,
                    MarketDataType      = MarketDataType.Last
                };

                var req = new BarsRequest(instr,
                                          DateTimeOffset.FromUnixTimeMilliseconds(bounds[0]).LocalDateTime,
                                          DateTimeOffset.FromUnixTimeMilliseconds(bounds[nBars]).LocalDateTime);
                req.BarsPeriod = period;

                req.Request((r, err, msg) =>
                {
                    int emitted = 0;
                    try
                    {
                        if (err != ErrorCode.NoError || r == null || r.Bars == null)
                        {
                            Log("AutomaticTradingNT8: NT8 no dio el perfil de " + root + " (" + msg + ").",
                                LogLevel.Warning);
                            return;
                        }
                        emitted = EmitProfile(instr, root, r.Bars, bounds);
                    }
                    catch (Exception ex)
                    {
                        Log("AutomaticTradingNT8: perfil de " + root + ": " + ex.Message, LogLevel.Warning);
                    }
                    finally
                    {
                        if (r != null) r.Dispose();
                        byte dummy; _profileBusy.TryRemove(root, out dummy);
                        SendProfileDone(root, emitted);
                    }
                });
            }
            catch (Exception ex)
            {
                byte dummy; _profileBusy.TryRemove(root, out dummy);
                Log("AutomaticTradingNT8: perfil de " + root + ": " + ex.Message, LogLevel.Warning);
                SendProfileDone(root, 0);   // que el EA no espere para siempre
            }
        }

        // Recorre las velas volumetricas de un minuto y las agrupa en las velas del
        // EA. Devuelve cuantas velas se han emitido.
        private int EmitProfile(NinjaTrader.Cbi.Instrument instr, string root,
                                Bars bars, List<long> bounds)
        {
            var volBars = bars.BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
            if (volBars == null)
            {
                Log("AutomaticTradingNT8: la serie de " + root + " no es Volumetric. " +
                    "El perfil necesita Order Flow+ (NinjaTrader.Vendor).", LogLevel.Warning);
                return 0;
            }

            double tick = instr.MasterInstrument.TickSize;
            if (tick <= 0) tick = 0.01;

            int n = bars.Count;
            int nBars = bounds.Count - 1;
            int k = 0;                                   // vela del EA en curso
            var ladder = new SortedDictionary<double, long[]>();   // precio -> {ask, bid}
            int emitted = 0;

            for (int i = 0; i < n; i++)
            {
                if (!_running || _bridge == null || !_bridge.IsLoggedIn) break;

                // NT8 fecha la vela por su CIERRE. Se resta 1 ms para que un minuto
                // que termina justo en una frontera caiga en la vela que lo contiene
                // y no en la siguiente.
                long tms = new DateTimeOffset(bars.GetTime(i).ToUniversalTime()).ToUnixTimeMilliseconds() - 1;

                if (tms < bounds[0]) continue;

                // Avanzar de vela del EA, emitiendo las que se cierran por el camino.
                while (k < nBars && tms >= bounds[k + 1])
                {
                    if (SendLadder(root, bounds[k], ladder)) emitted++;
                    ladder.Clear();          // liberar: el pico es UNA escalera
                    k++;
                }
                if (k >= nBars) break;

                double lo = bars.GetLow(i), hi = bars.GetHigh(i);
                for (double p = lo; p <= hi + tick * 0.5; p += tick)
                {
                    double px = instr.MasterInstrument.RoundToTickSize(p);
                    long ask = volBars.Volumes[i].GetAskVolumeForPrice(px);
                    long bid = volBars.Volumes[i].GetBidVolumeForPrice(px);
                    if (ask == 0 && bid == 0) continue;

                    long[] cell;
                    if (!ladder.TryGetValue(px, out cell)) { cell = new long[2]; ladder[px] = cell; }
                    cell[0] += ask;
                    cell[1] += bid;
                }
            }

            // La ultima vela abierta no la cierra el bucle.
            if (k < nBars && ladder.Count > 0 && SendLadder(root, bounds[k], ladder)) emitted++;

            Log("AutomaticTradingNT8: perfil de " + root + " enviado: " + emitted + " velas de " +
                nBars + " (" + n + " minutos volumetricos agregados).", LogLevel.Information);
            return emitted;
        }

        // PROFILE|<root>|<barStartUtcMs>|<p:ask:bid>;<p:ask:bid>;...
        private bool SendLadder(string root, long barStartMs, SortedDictionary<double, long[]> ladder)
        {
            if (ladder.Count == 0) return false;

            var sb = new StringBuilder(ladder.Count * 20 + 32);
            sb.Append("PROFILE|").Append(root).Append('|').Append(barStartMs).Append('|');
            bool first = true;
            foreach (var kv in ladder)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(Num(kv.Key)).Append(':').Append(kv.Value[0]).Append(':').Append(kv.Value[1]);
            }
            _bridge.SendRaw(sb.ToString());

            // Misma cortesia que el volcado de ticks: una escalera D1 son ~48 KB y
            // varias seguidas dejan a NT8 sin responder mientras escribe el socket.
            Thread.Sleep(HistPauseMs);
            return true;
        }

        private void SendProfileDone(string root, int nBars)
        {
            if (_bridge != null && _bridge.IsLoggedIn)
                _bridge.SendRaw("PROFILE_DONE|" + root + "|" + nBars);
        }

        // ------------------------- streaming de ticks (Compartir local) -------------------------

        private void StartStream(string symbol)
        {
            var instr = ResolveInstrument(symbol);
            if (instr == null) { Log("AutomaticTradingNT8: CMD_STREAM instrumento no resuelto: " + symbol, LogLevel.Warning); return; }
            string root = RootSymbol(instr);
            if (_streamed.ContainsKey(root)) return; // ya suscrito
            try
            {
                instr.MarketData.Update += OnMarketData;
                _streamed[root] = instr;
                Log("AutomaticTradingNT8: CMD_STREAM — suscrito a " + root +
                    " (" + instr.FullName + "). Esperando datos del feed...", LogLevel.Information);

                // Suscribirse SIEMPRE funciona, aunque el feed no vaya a mandar
                // nada (instrumento sin datos en esta conexion, mercado cerrado,
                // sin suscripcion de datos...). Decir "compartiendo" sin
                // comprobarlo es mentir: el usuario espera ticks que no llegaran.
                // A los 10 s se verifica y se avisa.
                var watchdog = new Thread(() =>
                {
                    Thread.Sleep(10000);
                    if (!_running || !_streamed.ContainsKey(root)) return;
                    long sent;
                    bool anyMd = _mdSeen.Keys.Any(k => k.StartsWith(root + "|", StringComparison.Ordinal));
                    _tradesSent.TryGetValue(root, out sent);

                    if (!anyMd)
                        Log("AutomaticTradingNT8: " + root + " NO envia NINGUN dato de mercado tras 10 s. " +
                            "La conexion de NT8 no tiene datos en vivo de este instrumento " +
                            "(¿mercado cerrado, o no incluido en tu feed?). No habra ticks.", LogLevel.Warning);
                    else if (sent == 0)
                        Log("AutomaticTradingNT8: " + root + " cotiza (bid/ask) pero NO publica operaciones " +
                            "ejecutadas (MarketDataType.Last). Sin cinta de operaciones no hay volumen real: " +
                            "el footprint REAL no es posible con este instrumento.", LogLevel.Warning);
                })
                { IsBackground = true, Name = "AutomaticTradingNT8-FeedCheck-" + root };
                watchdog.Start();
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: StartStream " + root + ": " + ex.Message, LogLevel.Warning); }
        }

        // Suelta TODAS las suscripciones de ticks. Se llama al conectar con el puente:
        // la DUENA del estado de compartir es la aplicacion, no el AddOn.
        //
        // Antes el AddOn mantenia la suscripcion por su cuenta y sobrevivia a que la
        // app se reiniciara: NinjaTrader seguia enviando trades de un instrumento que
        // la app ya no recordaba haber pedido, y que el usuario no podia parar (la
        // tabla "Datos compartidos" salia vacia, sin fila que seleccionar).
        //
        // Ahora la app persiste lo que compartiste y vuelve a pedirlo en cuanto el
        // AddOn hace LOGIN (ver main._on_new_terminal), asi que no se pierde nada.
        private void StopAllStreams()
        {
            int n = 0;
            foreach (var kv in _streamed)
            {
                try { if (kv.Value != null) kv.Value.MarketData.Update -= OnMarketData; } catch { }
                n++;
            }
            _streamed.Clear();
            if (n > 0)
                Log("AutomaticTradingNT8: " + n + " suscripcion(es) de ticks liberadas al conectar. " +
                    "La aplicacion volvera a pedir las que tenga guardadas.", LogLevel.Information);
        }

        private void StopStream(string symbol)
        {
            var instr = ResolveInstrument(symbol);
            string root = instr != null ? RootSymbol(instr) : symbol;
            NinjaTrader.Cbi.Instrument sub;
            if (_streamed.TryRemove(root, out sub) && sub != null)
            {
                try { sub.MarketData.Update -= OnMarketData; } catch { }
                Log("AutomaticTradingNT8: CMD_STOP_STREAM — " + root, LogLevel.Information);
            }
        }

        private void OnMarketData(object sender, MarketDataEventArgs e)
        {
            try
            {
                if (e == null || e.Instrument == null) return;
                if (_bridge == null || !_bridge.IsLoggedIn) return;

                string root = RootSymbol(e.Instrument);
                var md = e.Instrument.MarketData;
                double bid = md != null && md.Bid != null ? md.Bid.Price : 0;
                double ask = md != null && md.Ask != null ? md.Ask.Price : 0;

                // Diagnostico: que tipos de dato manda REALMENTE este instrumento.
                // Si nunca llega un 'Last', no hay cinta de operaciones y el
                // footprint real es imposible con el (pasa con muchos CFD/spot:
                // solo cotizan bid/ask, no publican las operaciones ejecutadas).
                if (_mdSeen.TryAdd(root + "|" + e.MarketDataType, 0))
                    Log("AutomaticTradingNT8: " + root + " -> primer dato de tipo " +
                        e.MarketDataType + " (precio=" + Num(e.Price) + " vol=" + Num(e.Volume) + ")",
                        LogLevel.Information);

                // --- TRADE: una operacion EJECUTADA (MarketDataType.Last). Es el
                //     dato que hace REAL un footprint: precio, VOLUMEN negociado y
                //     lado agresor. SIN THROTTLE: hacen falta todos, no una muestra.
                if (e.MarketDataType == MarketDataType.Last)
                {
                    // Agresor (Lee-Ready): aqui conocemos el bid/ask EXACTO del
                    // instante del trade. Deducirlo despues, en MT5, por la
                    // direccion del precio (tick-rule) es solo un proxy.
                    int side = 0;
                    if (ask > 0 && e.Price >= ask) side = 1;        // paga el ask -> comprador agresor
                    else if (bid > 0 && e.Price <= bid) side = -1;  // pega al bid -> vendedor agresor

                    long tms = new DateTimeOffset(e.Time.ToUniversalTime()).ToUnixTimeMilliseconds();
                    string trade = "TRADE|" + root + "|" + tms + "|" + Num(e.Price) + "|" +
                                   Num(e.Volume) + "|" + Num(bid) + "|" + Num(ask) + "|" + side;

                    // Volcado de historico en curso: retener. Mandarlo ahora lo pondria
                    // por delante de operaciones mas antiguas y romperia el orden
                    // cronologico del que depende el EA (ver _histQueue).
                    ConcurrentQueue<KeyValuePair<long, string>> hq;
                    if (_histQueue.TryGetValue(root, out hq) && hq != null)
                    {
                        hq.Enqueue(new KeyValuePair<long, string>(tms, trade));
                        return;
                    }

                    _bridge.SendRaw(trade);

                    long sent = _tradesSent.AddOrUpdate(root, 1, (k, v) => v + 1);
                    if (sent == 1 || sent % 500 == 0)
                        Log("AutomaticTradingNT8: " + sent + " trades enviados de " + root +
                            " (ultimo " + Num(e.Price) + " vol=" + Num(e.Volume) + " side=" + side + ")",
                            LogLevel.Information);
                    return;
                }

                // --- TICK: cotizacion (bid/ask). Sirve de referencia de precio
                //     (GET_TICK), no para el footprint. Aqui SI se throttlea: un
                //     cambio de bid/ask no aporta nada al order flow.
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                long lastSent;
                if (_lastTickSentMs.TryGetValue(root, out lastSent) && now - lastSent < TickThrottleMs) return;
                _lastTickSentMs[root] = now;

                double lastPx = md != null && md.Last != null ? md.Last.Price : 0;
                long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _bridge.SendRaw("TICK|" + root + "|" + Num(bid) + "|" + Num(ask) + "|" + Num(lastPx) + "|" + epoch);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnMarketData: " + ex.Message, LogLevel.Warning); }
        }

        // ------------------------- streaming de profundidad L2/DOM -------------------------

        private void StartDepthStream(string symbol)
        {
            var instr = ResolveInstrument(symbol);
            if (instr == null) { Log("AutomaticTradingNT8: CMD_STREAM_DEPTH instrumento no resuelto: " + symbol, LogLevel.Warning); return; }
            string root = RootSymbol(instr);
            if (_depthStreamed.ContainsKey(root)) return;
            try
            {
                instr.MarketDepth.Update += OnMarketDepth;
                _depthStreamed[root] = instr;
                Log("AutomaticTradingNT8: CMD_STREAM_DEPTH — compartiendo L2 de " + root, LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: StartDepthStream " + root + ": " + ex.Message, LogLevel.Warning); }
        }

        private void StopDepthStream(string symbol)
        {
            var instr = ResolveInstrument(symbol);
            string root = instr != null ? RootSymbol(instr) : symbol;
            NinjaTrader.Cbi.Instrument sub;
            if (_depthStreamed.TryRemove(root, out sub) && sub != null)
            {
                try { sub.MarketDepth.Update -= OnMarketDepth; } catch { }
                Log("AutomaticTradingNT8: CMD_STOP_STREAM_DEPTH — " + root, LogLevel.Information);
            }
        }

        private void OnMarketDepth(object sender, MarketDepthEventArgs e)
        {
            try
            {
                if (e == null || e.Instrument == null) return;
                string root = RootSymbol(e.Instrument);
                NinjaTrader.Cbi.Instrument instr;
                if (!_depthStreamed.TryGetValue(root, out instr) || instr == null) return;

                // Throttle por raiz (L2 es alto volumen). Va ANTES de leer el
                // libro: el libro lo mantiene NinjaTrader, no nosotros, asi que
                // saltarse un evento no pierde nada — la siguiente lectura ya
                // trae el estado actualizado.
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                long last;
                if (_lastDepthSentMs.TryGetValue(root, out last) && now - last < DepthThrottleMs) return;
                _lastDepthSentMs[root] = now;

                // El libro se lee de NinjaTrader (MarketDepth.Bids/.Asks), NO de
                // una copia propia.
                //
                // Manteniamos un SortedList por precio y lo actualizabamos con
                // los eventos (Add/Update fijan tamano, Remove borra). Los niveles
                // cuyo precio NT8 deja de reportar al alejarse el mercado NUNCA
                // recibian su Remove y se quedaban dentro para siempre. Al
                // serializar, Asks.Take(10) devolvia los 10 precios MAS BAJOS =
                // los huerfanos del momento en que se activo el L2: el ask salia
                // congelado horas, con el bid aparentemente sano (Bids.Reverse()
                // toma los mas altos, y esos si eran reales). Verificado en vivo
                // el 27-07-2026: bids siguiendo al mercado en 28722-28724 y asks
                // clavados en 28706-28709 con los mismos tamanos durante 15 s.
                //
                // NT8 ya mantiene el libro correcto y lo expone ordenado
                // (Bids[0] = mejor bid, Asks[0] = mejor ask), que es justo el
                // orden del protocolo. Se lee bajo SyncMarketDepth, como hace el
                // propio SuperDOM (ver SuperDomColumns/@APQ.cs).
                string bidsCsv, asksCsv;
                lock (e.Instrument.SyncMarketDepth)
                {
                    bidsCsv = string.Join(",", instr.MarketDepth.Bids.Take(DepthLevels)
                        .Select(r => Num(r.Price) + ":" + r.Volume));
                    asksCsv = string.Join(",", instr.MarketDepth.Asks.Take(DepthLevels)
                        .Select(r => Num(r.Price) + ":" + r.Volume));
                }

                if (_bridge != null && _bridge.IsLoggedIn)
                    _bridge.SendRaw("DEPTH|" + root + "|" + bidsCsv + "|" + asksCsv);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnMarketDepth: " + ex.Message, LogLevel.Warning); }
        }

        // Aplana TODAS las posiciones de la cuenta (parada de emergencia /
        // cerrar todas desde la app). Incluye las ATP_ propias: la emergencia
        // debe cerrar TODO, sin excepciones.
        private void FlattenAll()
        {
            if (_account == null) { ResolveAccount(); if (_account == null) return; }
            try
            {
                // 1) Cancelar PRIMERO las ordenes de trabajo (brackets SL/TP,
                //    pendientes). Debe ir ANTES de crear el flatten: si se hace
                //    despues, la propia orden de cierre (que pasa por Working)
                //    se cancelaria a si misma (bug: la posicion no se cerraba).
                var working = _account.Orders.Where(o => o.OrderState == OrderState.Working ||
                    o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted).ToList();
                if (working.Count > 0) _account.Cancel(working.ToArray());

                // 2) Aplanar todas las posiciones abiertas con market opuesta.
                var open = _account.Positions.Where(p => p.MarketPosition != MarketPosition.Flat).ToList();
                if (open.Count == 0)
                {
                    Log("AutomaticTradingNT8: CMD_FLATTEN — no hay posiciones abiertas.", LogLevel.Information);
                    return;
                }
                foreach (var pos in open)
                {
                    var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    var close = _account.CreateOrder(pos.Instrument, action, OrderType.Market, OrderEntry.Automated,
                        TimeInForce.Gtc, Math.Abs(pos.Quantity), 0, 0, string.Empty, "ATP_flatten",
                        Core.Globals.MaxDate, null);
                    _account.Submit(new[] { close });
                }
                Log("AutomaticTradingNT8: CMD_FLATTEN — cerradas " + open.Count + " posiciones.", LogLevel.Warning);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: FlattenAll: " + ex.Message, LogLevel.Error); }
        }

        private void ExecuteOpen(string symbol, int type, double volume, double sl, double tp, int magic)
        {
            if (_account == null) { ResolveAccount(); if (_account == null) { Log("AutomaticTradingNT8: CMD_OPEN sin cuenta.", LogLevel.Warning); return; } }
            // symbol puede venir como raiz ("MNQ") o contrato completo: ResolveInstrument
            // devuelve el frontal para la raiz.
            var instrument = ResolveInstrument(symbol);
            if (instrument == null) { Log("AutomaticTradingNT8: CMD_OPEN instrumento no resuelto: " + symbol, LogLevel.Warning); return; }

            int qty = Math.Max(1, (int)Math.Round(volume));
            OrderAction action = type == 0 ? OrderAction.Buy : OrderAction.Sell;
            try
            {
                var order = _account.CreateOrder(instrument, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Gtc,
                    qty, 0, 0, string.Empty, "ATP_" + magic, Core.Globals.MaxDate, null);
                _account.Submit(new[] { order });
                if (sl > 0 || tp > 0)
                {
                    _pendingBrackets[order] = new BracketInfo
                    {
                        Instrument = instrument,
                        ExitAction = action == OrderAction.Buy ? OrderAction.Sell : OrderAction.Buy,
                        Qty = qty, Sl = sl, Tp = tp,
                    };
                }
                string openRoot = RootSymbol(instrument);
                _magicToInstrument[magic] = openRoot;
                _instrumentToMagic[openRoot] = magic;
                Log("AutomaticTradingNT8: CMD_OPEN " + symbol + " " + action + " " + qty + " magic=" + magic, LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_OPEN error: " + ex.Message, LogLevel.Error); }
        }

        private void ExecuteClose(int magic)
        {
            if (_account == null) { Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " sin cuenta.", LogLevel.Warning); return; }
            string instrKey;
            if (!_magicToInstrument.TryGetValue(magic, out instrKey))
            {
                // No callar: si el magic no se conoce, la copia se queda ABIERTA.
                // Suele significar que el AddOn se reinicio y la orden original no
                // aparece en _account.Orders (ver RebuildMagicMapFromOrders).
                Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic +
                    " desconocido: no se puede cerrar (posicion sin asociar).", LogLevel.Warning);
                return;
            }
            try
            {
                var pos = _account.Positions.FirstOrDefault(p => RootSymbol(p.Instrument) == instrKey && p.MarketPosition != MarketPosition.Flat);
                if (pos == null)
                {
                    Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " (" + instrKey + "): ya no hay posicion abierta.", LogLevel.Information);
                    return;
                }
                var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                var order = _account.CreateOrder(pos.Instrument, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Gtc,
                    Math.Abs(pos.Quantity), 0, 0, string.Empty, "ATP_" + magic, Core.Globals.MaxDate, null);
                _account.Submit(new[] { order });
                Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " " + instrKey, LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_CLOSE error: " + ex.Message, LogLevel.Error); }
        }

        private void ExecuteUpdateSlTp(int magic, double sl, double tp)
        {
            // Reservado: requiere localizar/cancelar las ordenes hijas SL/TP existentes
            // del bracket de ese magic y recolocarlas. No implementado en v1 (el
            // copiador de señales sincroniza SL/TP tras abrir; para NT8 el bracket ya
            // se coloca en la apertura via CMD_OPEN — ver PlaceBracket).
            Log("AutomaticTradingNT8: CMD_UPDATE_SLTP magic=" + magic + " no implementado en v1.", LogLevel.Warning);
        }

        private static double ParseD(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0.0;
        }

        private static string Num(double v)
        {
            return v.ToString("0.########", CultureInfo.InvariantCulture);
        }

        // Nombre RAIZ del instrumento ("MNQ"), roll-stable (no "MNQ 09-26").
        private static string RootSymbol(NinjaTrader.Cbi.Instrument instr)
        {
            if (instr == null) return "";
            return instr.MasterInstrument != null ? instr.MasterInstrument.Name : instr.FullName;
        }

        // Cache raiz -> instrumento ya resuelto. Se siembra con cada instrumento
        // que vemos (posiciones, ejecuciones, streams), asi que tras la primera
        // operacion la resolucion es inmediata.
        private readonly ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument> _rootCache =
            new ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument>();

        private void CacheInstrument(NinjaTrader.Cbi.Instrument instr)
        {
            if (instr == null) return;
            string root = RootSymbol(instr);
            if (!string.IsNullOrEmpty(root)) _rootCache[root] = instr;
        }

        /// <summary>Resuelve un symbol al Instrument de NT8.
        ///
        /// Acepta el nombre completo ("MNQ 09-26") o la RAIZ ("MNQ").
        ///
        /// Con la raiz de un futuro hay dos trampas, las dos vistas en vivo el
        /// 31-07-2026 con MGC:
        ///
        ///  a) El contrato que toca NO es el de vencimiento mas proximo, es el
        ///     que marcan los roll settings de NT8. MGC AUG26 no habia expirado
        ///     (vence a finales de agosto) pero ya estaba en periodo de aviso:
        ///     cero operaciones en una hora de streaming y 135 en todo el
        ///     historico, mientras NT8 graficaba MGC DEC26. El oro cotiza casi
        ///     todos los meses y rueda mucho antes de expirar; en MNQ, que solo
        ///     lista trimestrales, "el que vence antes" y "el activo" coinciden
        ///     y por eso no se noto nunca.
        ///
        ///  b) Instrument.GetInstrument("MGC") NO devuelve null: devuelve la
        ///     ACCION MGC, que existe con ese nombre exacto y no esta en el feed
        ///     ('Symbol is inaccessible'). Por eso el futuro se resuelve ANTES
        ///     de preguntar por el nombre pelado.
        ///
        /// La notacion "MGC ##-##" tampoco vale aqui: GetInstrument devuelve un
        /// Instrument literal con ese nombre que el feed rechaza igual. Solo la
        /// entienden los Data Series de los graficos.
        /// </summary>
        private NinjaTrader.Cbi.Instrument ResolveInstrument(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;

            // 1. Cache (raiz de un instrumento ya visto).
            NinjaTrader.Cbi.Instrument cached;
            if (_rootCache.TryGetValue(symbol, out cached) && cached != null)
                return cached;

            // 2. Nombre completo ("MNQ 09-26"): el usuario ya eligio contrato.
            if (symbol.IndexOf(' ') >= 0)
            {
                try
                {
                    var full = NinjaTrader.Cbi.Instrument.GetInstrument(symbol);
                    if (full != null) { CacheInstrument(full); return full; }
                }
                catch { }
            }

            // 3. Raiz de un futuro: contrato ACTIVO segun los roll settings.
            var rolled = ResolveFrontFuture(symbol);
            if (rolled != null) { CacheInstrument(rolled); return rolled; }

            // 4. Sin vencimiento (forex, acciones, indices): por nombre.
            try
            {
                var direct = NinjaTrader.Cbi.Instrument.GetInstrument(symbol);
                if (direct != null) { CacheInstrument(direct); return direct; }
            }
            catch (Exception ex)
            {
                Log("AutomaticTradingNT8: ResolveInstrument('" + symbol + "'): " + ex.Message, LogLevel.Warning);
            }
            return null;
        }

        /// <summary>Contrato en vigor de una raiz de futuro ("MGC" -> MGC 12-26),
        /// o null si esa raiz no es un futuro.
        ///
        /// NT8 guarda el calendario de rolls en MasterInstrument.RolloverCollection:
        /// cada Rollover dice "a partir de Date, el contrato es ContractMonth" (mismo
        /// dato que usa la columna @DaysUntilRollover del Market Analyzer). El que
        /// vale es el de Date mas reciente ya cumplida.
        /// </summary>
        private NinjaTrader.Cbi.Instrument ResolveFrontFuture(string root)
        {
            try
            {
                DateTime now = Core.Globals.Now;

                // Cualquier contrato de la raiz sirve para llegar al MasterInstrument
                // (y de paso confirma que la raiz ES un futuro: tiene vencimientos).
                List<NinjaTrader.Cbi.Instrument> candidates;
                lock (NinjaTrader.Cbi.Instrument.All)
                    candidates = NinjaTrader.Cbi.Instrument.All
                        .Where(i => i != null && i.MasterInstrument != null &&
                                    i.Expiry != DateTime.MinValue &&
                                    string.Equals(i.MasterInstrument.Name, root, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                if (candidates.Count == 0) return null;

                var master = candidates[0].MasterInstrument;
                DateTime contractMonth = DateTime.MinValue, rolledOn = DateTime.MinValue;
                lock (master.RolloverCollection)
                {
                    foreach (Rollover r in master.RolloverCollection)
                    {
                        if (r == null || r.Date > now || r.Date < rolledOn) continue;
                        rolledOn = r.Date;
                        contractMonth = r.ContractMonth;
                    }
                }

                NinjaTrader.Cbi.Instrument front = null;
                if (contractMonth != DateTime.MinValue)
                    front = candidates.FirstOrDefault(i => i.Expiry == contractMonth)
                            // El contrato puede no estar instanciado todavia: pedirlo por
                            // nombre lo crea ("MGC 12-26").
                            ?? NinjaTrader.Cbi.Instrument.GetInstrument(
                                   root + " " + contractMonth.ToString("MM-yy"));

                // Sin calendario de rolls (o ninguno vencido aun): el mas proximo
                // sin expirar. Es lo que se hacia antes; puede caer en un contrato
                // en periodo de aviso, ver la nota (a) de ResolveInstrument.
                if (front == null)
                    front = candidates.Where(i => i.Expiry.Date >= now.Date)
                                      .OrderBy(i => i.Expiry).FirstOrDefault() ?? candidates[0];

                Log("AutomaticTradingNT8: '" + root + "' resuelto al contrato " + front.FullName +
                    (rolledOn != DateTime.MinValue ? " (roll de NT8 del " + rolledOn.ToString("dd-MM-yyyy") + ")"
                                                   : " (sin calendario de rolls: vencimiento mas proximo)"),
                    LogLevel.Information);
                return front;
            }
            catch (Exception ex)
            {
                Log("AutomaticTradingNT8: ResolveFrontFuture('" + root + "'): " + ex.Message, LogLevel.Warning);
                return null;
            }
        }

        // =====================================================================
        //  SocketConn — una conexion TCP al servidor (LOGIN + PING + CHECK_TRADE/
        //  RELEASE sincronos + recepcion de mensajes push). Cada strategy usa su
        //  propia instancia (mismo modelo que un EA MT5: un socket == una
        //  identidad/magic para el servidor).
        // =====================================================================
        private class SocketConn
        {
            private readonly string _host;
            private readonly int _port;
            private readonly string _terminalId;
            private readonly string _symbol;
            private readonly int _magic;
            private readonly Action<string> _onPush;
            private readonly Action<string, bool> _log;

            private TcpClient _tcp;
            private NetworkStream _stream;
            private Thread _rxThread;
            private readonly StringBuilder _rxBuffer = new StringBuilder();
            private readonly BlockingCollection<string> _pendingResponse = new BlockingCollection<string>(new ConcurrentQueue<string>());
            private readonly object _requestLock = new object();
            private readonly object _sendLock = new object();

            public volatile bool IsLoggedIn;
            public bool EverConnected;

            // Se dispara al aceptar el LOGIN (tambien en cada RE-conexion). El AddOn
            // lo usa para soltar sus suscripciones: el estado de "que se comparte" lo
            // manda la aplicacion, que las volvera a pedir.
            public Action OnLoggedIn;

            public SocketConn(string host, int port, string terminalId, string symbol, int magic, Action<string> onPush, Action<string, bool> log)
            {
                _host = host; _port = port; _terminalId = terminalId; _symbol = symbol; _magic = magic;
                _onPush = onPush; _log = log;
            }

            public bool Connect()
            {
                try
                {
                    Close();
                    _tcp = new TcpClient();
                    _tcp.Connect(_host, _port);
                    _tcp.NoDelay = true;
                    _stream = _tcp.GetStream();
                    lock (_rxBuffer) _rxBuffer.Length = 0;
                    while (_pendingResponse.TryTake(out _)) { }

                    _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "AutomaticTradingNT8-Rx-" + _magic };
                    _rxThread.Start();

                    // LOGIN_OK lo consume HandleLine (marca IsLoggedIn); no llega
                    // a _pendingResponse, asi que aqui se espera al flag en vez
                    // de a una respuesta encolada.
                    string login = string.Format(CultureInfo.InvariantCulture, "LOGIN|{0}|{1}|{2}", _magic, _symbol, _terminalId);
                    SendRaw(login);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (!IsLoggedIn && sw.ElapsedMilliseconds < 5000)
                        Thread.Sleep(20);

                    if (IsLoggedIn) return true;

                    _log("LOGIN sin respuesta (magic=" + _magic + ").", true);
                    Close();
                    return false;
                }
                catch (Exception ex)
                {
                    _log("error conectando (magic=" + _magic + "): " + ex.Message, true);
                    Close();
                    return false;
                }
            }

            public void Close()
            {
                IsLoggedIn = false;
                try { _stream?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
                _stream = null;
                _tcp = null;
            }

            private void ReceiveLoop()
            {
                var stream = _stream;
                var buffer = new byte[4096];
                try
                {
                    while (stream != null)
                    {
                        int n = stream.Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;
                        lock (_rxBuffer) { _rxBuffer.Append(Encoding.UTF8.GetString(buffer, 0, n)); }
                        string line;
                        while ((line = TakeLine()) != null)
                            HandleLine(line);
                    }
                }
                catch { /* socket cerrado/muerto: no bloquear reintentos (gotcha guia #4.5) */ }
                finally
                {
                    IsLoggedIn = false;
                }
            }

            private string TakeLine()
            {
                lock (_rxBuffer)
                {
                    for (int i = 0; i < _rxBuffer.Length; i++)
                    {
                        if (_rxBuffer[i] == '\n')
                        {
                            string line = _rxBuffer.ToString(0, i).Trim();
                            _rxBuffer.Remove(0, i + 1);
                            return line;
                        }
                    }
                }
                return null;
            }

            private void HandleLine(string line)
            {
                if (string.IsNullOrEmpty(line) || line == "PONG") return;
                if (line.StartsWith("CMD_", StringComparison.Ordinal))
                {
                    _onPush?.Invoke(line);
                    return;
                }
                // Handshake: NUNCA encolar. El servidor envia WELCOME al aceptar
                // la conexion y LOGIN_OK tras el LOGIN. Si acaban en la cola de
                // respuestas, la primera CHECK_TRADE puede leerlos como si
                // fueran su respuesta y devolver "DENEGADA -> LOGIN_OK".
                if (line == "WELCOME") return;
                if (line == "LOGIN_OK")
                {
                    IsLoggedIn = true;
                    EverConnected = true;
                    var cb = OnLoggedIn;
                    if (cb != null)
                    {
                        try { cb(); } catch { }
                    }
                    return;
                }
                try { _pendingResponse.Add(line); } catch { }
            }

            public void SendRaw(string msg)
            {
                var stream = _stream;
                if (stream == null) return;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(msg + "\n");
                    lock (_sendLock) { stream.Write(bytes, 0, bytes.Length); }
                }
                catch (Exception ex)
                {
                    _log("error enviando (magic=" + _magic + "): " + ex.Message, true);
                    IsLoggedIn = false;
                }
            }

            // Serializa request/respuesta: una sola llamada sincrona en vuelo a la vez
            // por conexion (cada strategy tiene la suya, asi que no compiten entre si).
            public string SendAndWait(string msg, int timeoutMs)
            {
                lock (_requestLock)
                {
                    while (_pendingResponse.TryTake(out _)) { }
                    SendRaw(msg);
                    string response;
                    return _pendingResponse.TryTake(out response, timeoutMs) ? response : "";
                }
            }
        }
    }

    /// <summary>Alias publico corto para llamar desde Strategies sin exponer la clase AddOn completa.</summary>
    public static class AutomaticTradingBridge
    {
        public static bool CheckTrade(string strategyTag, double lots, int type, long barTime, int priority, out int magic, out string reason)
        {
            return AutomaticTradingNT8.CheckTrade(strategyTag, lots, type, barTime, priority, out magic, out reason);
        }

        public static void Release(string strategyTag) { AutomaticTradingNT8.Release(strategyTag); }

        public static string OrderTag(string strategyTag, int magic) { return AutomaticTradingNT8.OrderTag(strategyTag, magic); }
    }
}
