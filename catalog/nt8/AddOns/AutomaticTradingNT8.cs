// =============================================================================
//  AutomaticTradingNT8.cs  -  Puente NinjaTrader 8 -> ATPortfolio (AutomaticTrading)
// -----------------------------------------------------------------------------
//  AddOn CLIENTE: NT8 conecta a socket_server.py:5006 y habla el protocolo
//  `|`-texto que ya usan los EAs MT5. Puentea CADA cuenta por separado como un
//  terminal independiente ("NT8_<Cuenta>"): gate de riesgo, STATE, destino de
//  copia de senales y feed de mercado (ticks, L2, historico, perfil).
//
//  INSTALACION: copiar a Documents\NinjaTrader 8\bin\Custom\AddOns\ (o Tools ->
//  Import -> NinjaScript Add-On...), compilar con F5 y validar en Sim101 antes
//  de una cuenta real. Por defecto se puentean TODAS las cuentas conectadas; se
//  limitan en CONFIGURACION, igual que el modo reactivo.
//
//  API PARA UNA STRATEGY PROPIA (gate preventivo):
//    if (AutomaticTradingBridge.CheckTrade(tag, lots, type, barTime, priority,
//                                          out magic, out reason, Account.Name))
//    {
//        EnterLong(qty, AutomaticTradingBridge.OrderTag(tag, magic));
//        AutomaticTradingBridge.Release(tag, Account.Name);
//    }
//  type: 0=Buy, 1=Sell. barTime: ToTime(Time[0]). priority: 0 = sin turno.
//  El OrderTag DEBE ser el nombre de la entrada: NT8 no tiene Magic Number y es
//  lo unico que ata la posicion a su magic. Las estrategias de TERCEROS no
//  necesitan nada: las gatea ReactiveMode.
//
//  LIMITACION: NT8 netea por cuenta+instrumento. Se asume UNA estrategia por
//  instrumento y cuenta; con varias, el mapeo magic<->instrumento se rompe.
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
        // Cuentas a puentear. "" = TODAS las conectadas (recomendado: las
        // cuentas nuevas aparecen solas en la app). Para limitar, lista separada
        // por comas: "Sim101,DEMO3005427".
        private const string AccountNames = "";
        private const int StatePeriodMs = 2000;         // frecuencia de STATE (estado de cuenta)
        private const int AccountScanMs = 5000;         // cada cuanto se buscan cuentas nuevas/idas
        private const int SymbolsMax = 5000;   // tope de instrumentos en CMD_SYMBOLS
        private const int PingPeriodMs = 5000;
        private const int CheckTradeTimeoutMs = 3000;

        // MODO REACTIVO: gatea tambien las estrategias de TERCEROS, las que no
        // llaman a CheckTrade. Ponlo a false si solo usas estrategias propias.
        //
        // OJO: aplica a TODAS las cuentas puenteadas, y una cuenta recien
        // descubierta entra con los limites POR DEFECTO de la app (2 contratos).
        // Con cuentas reales, revisa sus limites en Control del Riesgo en cuanto
        // aparezcan, o limita AccountNames, o pon esto a false.
        private const bool ReactiveMode = true;
        // ===========================================================

        private static AutomaticTradingNT8 _instance;
        private volatile bool _running;

        // Un AccountLink por cuenta puenteada, indexado por nombre de cuenta.
        private readonly ConcurrentDictionary<string, AccountLink> _links =
            new ConcurrentDictionary<string, AccountLink>();
        // Cuenta que sirve el FEED de mercado. Una sola: la primera puenteada.
        private volatile AccountLink _dataLink;
        private Thread _scanThread;

        /// <summary>Conexion por la que sale TODO el feed. Una sola cuenta lo
        /// sirve: si no, cada operacion llegaria duplicada al servidor.</summary>
        private SocketConn DataConn
        {
            get { var l = _dataLink; return l != null ? l.Conn : null; }
        }

        /// <summary>True si el feed tiene por donde salir ahora mismo.</summary>
        private bool DataReady
        {
            get { var c = DataConn; return c != null && c.IsLoggedIn; }
        }

        /// <summary>Envia una linea de feed. Lee la conexion UNA vez: la cuenta
        /// del feed puede cambiar entre el chequeo y el envio.</summary>
        private void SendData(string line)
        {
            var c = DataConn;
            if (c != null && c.IsLoggedIn) c.SendRaw(line);
        }

        // Magic sintetico: NT8 no tiene Magic Number. Se DERIVA del nombre de la
        // estrategia (FNV-1a 32 bits), no de un contador, y el strategyTag debe
        // coincidir con el nombre del fichero .cs.
        private const int MagicBase = 900001;
        private const int MagicRange = 90000;

        // Los mapas magic <-> instrumento viven en AccountLink, POR CUENTA.

        // Bracket SL/TP pendiente. Clave: la INSTANCIA Order, nunca OrderId (muta).
        private class BracketInfo
        {
            public NinjaTrader.Cbi.Instrument Instrument;
            public OrderAction ExitAction;
            public int Qty;
            public double Sl, Tp;
            // Magic protegido. Va en el NOMBRE de la orden (ver BracketName).
            public long Magic;
        }

        /// <summary>Nombre de las contrarias SL/TP. Lleva el magic dentro: es lo
        /// unico que ata una contraria a SU posicion.</summary>
        private static string BracketName(long magic)
        {
            return "ATP_bracket_" + magic;
        }

        // Streaming de ticks (CMD_STREAM), para GET_TICK desde un EA MT5.
        private readonly ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument> _streamed =
            new ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument>();
        private readonly ConcurrentDictionary<string, long> _lastTickSentMs =
            new ConcurrentDictionary<string, long>();
        private const int TickThrottleMs = 250;

        // Diagnostico del feed: tipos de MarketData vistos y trades enviados.
        private readonly ConcurrentDictionary<string, byte> _mdSeen =
            new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, long> _tradesSent =
            new ConcurrentDictionary<string, long>();

        // Historico de operaciones (CMD_HISTORY). Mientras se envia, los trades EN
        // VIVO de esa raiz se ENCOLAN: el EA los busca por biseccion y eso exige
        // orden cronologico.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<KeyValuePair<long, string>>> _histQueue =
            new ConcurrentDictionary<string, ConcurrentQueue<KeyValuePair<long, string>>>();

        // Troceado: 500k lineas seguidas por el socket dejan a NT8 sin responder.
        private const int HistChunk   = 5000;
        private const int HistPauseMs = 15;

        // Ventana MAXIMA por rango de fechas; mas atras se sirve por numero de
        // ticks. Un rango mayor deja el volcado colgado y el footprint a cero.
        private const int HistMaxHours = 12;

        // Streaming de profundidad L2/DOM (CMD_STREAM_DEPTH). NO se mantiene libro
        // propio: el unico libro es el de NinjaTrader, leido en OnMarketDepth.
        private readonly ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument> _depthStreamed =
            new ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument>();
        private readonly ConcurrentDictionary<string, long> _lastDepthSentMs =
            new ConcurrentDictionary<string, long>();
        private const int DepthThrottleMs = 250;
        private const int DepthLevels = 10;

        // Cada strategy usa su PROPIA conexion: el LOGIN fija el magic del socket.

        /// <summary>Todo lo que es de UNA cuenta NinjaTrader. La app las trata como
        /// terminales independientes ("NT8_&lt;Cuenta&gt;") y cada una necesita su propia
        /// conexion: el servidor identifica al terminal por el socket.</summary>
        private class AccountLink
        {
            public string AccountName;
            public string TerminalId;
            public Account Account;
            public SocketConn Conn;
            public Thread BridgeThread;
            // Este link concreto esta cerrado. NO vale mirar si la cuenta sigue en
            // _links: una que se va y vuelve crea un link nuevo con el mismo nombre.
            public volatile bool Stopped;

            // Mapas POR CUENTA: NT8 netea por cuenta+instrumento.
            public readonly ConcurrentDictionary<long, string> MagicToInstrument = new ConcurrentDictionary<long, string>();
            public readonly ConcurrentDictionary<string, long> InstrumentToMagic = new ConcurrentDictionary<string, long>();
            public readonly ConcurrentDictionary<Order, BracketInfo> PendingBrackets = new ConcurrentDictionary<Order, BracketInfo>();

            // NUESTRA asociacion posicion <-> contrarias: en NinjaTrader un SL/TP es
            // una ORDEN del libro, no un campo de la posicion.
            public readonly ConcurrentDictionary<long, List<Order>> ActiveBrackets = new ConcurrentDictionary<long, List<Order>>();

            // Magics con posicion viva vista alguna vez ("ya cerro" vs "aun no llega").
            public readonly ConcurrentDictionary<long, byte> MagicConPosicion = new ConcurrentDictionary<long, byte>();

            // Modo reactivo: cola de ordenes de terceros + set de las ya evaluadas.
            // Worker propio: el SendAndWait no puede bloquear los eventos de NT8.
            public readonly BlockingCollection<Order> ReactiveQueue = new BlockingCollection<Order>(new ConcurrentQueue<Order>());
            public readonly ConcurrentDictionary<Order, byte> ReactiveSeen = new ConcurrentDictionary<Order, byte>();
            public Thread ReactiveThread;

            // Conexion propia por strategy (el magic del socket lo fija el LOGIN).
            public readonly ConcurrentDictionary<string, SocketConn> StrategyConns = new ConcurrentDictionary<string, SocketConn>();

            // Guardados para poder DESuscribir: los handlers capturan el link.
            public EventHandler<OrderEventArgs> OrderHandler;
            public EventHandler<ExecutionEventArgs> ExecutionHandler;

            // Ultimo STATE enviado, solo para trazar los CAMBIOS.
            public string LastPositionsCsv = null;
        }

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
            _running = true;
            SyncAccounts();     // alta inmediata de las cuentas que ya estan conectadas
            _scanThread = new Thread(AccountScanLoop) { IsBackground = true, Name = "AutomaticTradingNT8-Scan" };
            _scanThread.Start();
        }

        private void Stop()
        {
            _running = false;
            foreach (var link in _links.Values.ToList()) StopLink(link);
            _links.Clear();
            _dataLink = null;
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
        }

        // ------------------------- altas y bajas de cuentas -------------------------

        /// <summary>Cuentas a puentear ahora mismo. Se re-evalua periodicamente:
        /// NinjaTrader conecta cuentas cuando le da la gana.</summary>
        private List<Account> WantedAccounts()
        {
            var wanted = new List<Account>();
            var filter = (AccountNames ?? "").Split(',')
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToList();
            lock (Account.All)
            {
                foreach (var a in Account.All)
                {
                    if (a == null || string.IsNullOrEmpty(a.Name)) continue;
                    if (filter.Count > 0)
                    {
                        if (!filter.Any(f => string.Equals(f, a.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    else if (a.Connection == null) continue;   // sin filtro: solo las conectadas
                    wanted.Add(a);
                }
            }
            return wanted;
        }

        private void AccountScanLoop()
        {
            while (_running)
            {
                try { SyncAccounts(); }
                catch (Exception ex) { Log("AutomaticTradingNT8: AccountScanLoop: " + ex.Message, LogLevel.Warning); }
                Thread.Sleep(AccountScanMs);
            }
        }

        /// <summary>Alta de las cuentas nuevas y baja de las que ya no estan.</summary>
        private void SyncAccounts()
        {
            var wanted = WantedAccounts();
            var wantedNames = new HashSet<string>(wanted.Select(a => a.Name));

            foreach (var acct in wanted)
            {
                if (_links.ContainsKey(acct.Name)) continue;
                StartLink(acct);
            }

            foreach (var kv in _links.ToList())
            {
                if (wantedNames.Contains(kv.Key)) continue;
                AccountLink gone;
                if (_links.TryRemove(kv.Key, out gone))
                {
                    Log("AutomaticTradingNT8: cuenta '" + gone.AccountName +
                        "' ya no esta disponible: puente cerrado.", LogLevel.Warning);
                    StopLink(gone);
                    if (_dataLink == gone) PromoteDataLink();
                }
            }
        }

        private void StartLink(Account acct)
        {
            var link = new AccountLink
            {
                AccountName = acct.Name,
                TerminalId = "NT8_" + acct.Name,
                Account = acct,
            };
            if (!_links.TryAdd(acct.Name, link)) return;

            link.Conn = new SocketConn(ServerHost, ServerPort, link.TerminalId, link.AccountName,
                                       GetOrAssignMagic("__BRIDGE__"), s => OnPush(link, s), LogFunc);

            // El feed lo sirve UNA cuenta: la primera que arranca.
            if (_dataLink == null)
            {
                _dataLink = link;
                // La duena de que se comparte es la app: al (re)conectar soltamos
                // todo y esperamos a que nos lo vuelva a pedir.
                link.Conn.OnLoggedIn = StopAllStreams;
            }

            AttachAccount(link);

            link.BridgeThread = new Thread(() => BridgeLoop(link))
            {
                IsBackground = true,
                Name = "AutomaticTradingNT8-Bridge-" + link.AccountName,
            };
            link.BridgeThread.Start();

            if (ReactiveMode)
            {
                link.ReactiveThread = new Thread(() => ReactiveLoop(link))
                {
                    IsBackground = true,
                    Name = "AutomaticTradingNT8-Reactive-" + link.AccountName,
                };
                link.ReactiveThread.Start();
            }

            Log("AutomaticTradingNT8: puenteando cuenta '" + link.AccountName + "' como " + link.TerminalId +
                (_dataLink == link ? " (sirve tambien el feed de mercado)." : "."), LogLevel.Information);
        }

        private void StopLink(AccountLink link)
        {
            if (link == null) return;
            link.Stopped = true;
            DetachAccount(link);
            try { link.ReactiveQueue.CompleteAdding(); } catch { }
            if (link.Conn != null) link.Conn.Close();
            foreach (var c in link.StrategyConns.Values) c.Close();
            link.StrategyConns.Clear();
        }

        /// <summary>La cuenta que servia el feed se fue: se asciende otra y se
        /// sueltan las suscripciones, que apuntaban a un socket cerrado.</summary>
        private void PromoteDataLink()
        {
            var next = _links.Values.FirstOrDefault();
            _dataLink = next;
            StopAllStreams();
            if (next != null)
            {
                next.Conn.OnLoggedIn = StopAllStreams;
                Log("AutomaticTradingNT8: el feed de mercado pasa a la cuenta '" + next.AccountName +
                    "'. Si estabas compartiendo instrumentos, vuelve a activarlos en la app.", LogLevel.Warning);
            }
        }

        private void AttachAccount(AccountLink link)
        {
            if (link == null || link.Account == null) return;
            try
            {
                link.OrderHandler = (s, e) => OnOrderUpdate(link, e);
                link.ExecutionHandler = (s, e) => OnExecutionUpdate(link, e);
                link.Account.OrderUpdate += link.OrderHandler;
                link.Account.ExecutionUpdate += link.ExecutionHandler;
                RebuildMagicMapFromOrders(link);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: error suscribiendo cuenta '" + link.AccountName + "': " + ex.Message, LogLevel.Error); }
        }

        /// <summary>Reconstruye magic &lt;-&gt; instrumento leyendo las ordenes de la
        /// cuenta (llevan el nombre "ATP_&lt;magic&gt;"). Los mapas viven en memoria; sin
        /// esto, tras reiniciar el AddOn un CMD_CLOSE no encuentra el magic y la
        /// copia se queda ABIERTA para siempre.</summary>
        private void RebuildMagicMapFromOrders(AccountLink link)
        {
            if (link == null || link.Account == null) return;
            int restored = 0;
            try
            {
                foreach (var o in link.Account.Orders.ToList())
                {
                    if (o == null || o.Instrument == null) continue;
                    string nm = o.Name ?? "";
                    if (!nm.StartsWith("ATP_", StringComparison.Ordinal)) continue;

                    long magic;
                    // "ATP_flatten", "ATP_bracket", "ATP_reactive_close" no parsean: se ignoran.
                    if (!long.TryParse(nm.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out magic))
                        continue;

                    CacheInstrument(o.Instrument);
                    string root = RootSymbol(o.Instrument);
                    link.MagicToInstrument[magic] = root;
                    link.InstrumentToMagic[root] = magic;
                    restored++;
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: RebuildMagicMapFromOrders: " + ex.Message, LogLevel.Warning); }

            if (restored > 0)
                Log("AutomaticTradingNT8: recuperadas " + restored + " asociaciones magic->instrumento de las ordenes de '" +
                    link.AccountName + "'.", LogLevel.Information);
        }

        private void DetachAccount(AccountLink link)
        {
            if (link == null || link.Account == null) return;
            try { if (link.OrderHandler != null) link.Account.OrderUpdate -= link.OrderHandler; } catch { }
            try { if (link.ExecutionHandler != null) link.Account.ExecutionUpdate -= link.ExecutionHandler; } catch { }
        }

        // Conexion "bridge" de una cuenta: reconecta con backoff y manda PING+STATE.
        private void BridgeLoop(AccountLink link)
        {
            int failCount = 0;
            while (_running && !link.Stopped)
            {
                if (!link.Conn.IsLoggedIn)
                {
                    if (link.Conn.Connect())
                    {
                        failCount = 0;
                        Log("AutomaticTradingNT8: bridge conectado (" + link.TerminalId + ").", LogLevel.Information);
                    }
                    else
                    {
                        failCount++;
                        int waitSec = Math.Min(60, 5 * failCount);
                        if (failCount == 1 || failCount % 5 == 0)
                            Log("AutomaticTradingNT8: fallo conexion bridge de " + link.TerminalId +
                                " (" + failCount + "). Reintento en " + waitSec + "s.", LogLevel.Warning);
                        Thread.Sleep(waitSec * 1000);
                        continue;
                    }
                }

                Thread.Sleep(Math.Min(PingPeriodMs, StatePeriodMs));
                if (link.Conn.IsLoggedIn)
                {
                    link.Conn.SendRaw("PING");
                    SendState(link);
                }
            }
        }

        // Comandos push. `link` es la cuenta por cuya conexion entro el comando.
        private void OnPush(AccountLink link, string line)
        {
            try { HandleCommand(link, line); }
            catch (Exception ex) { Log("AutomaticTradingNT8: HandleCommand '" + line + "': " + ex.Message, LogLevel.Error); }
        }

        private void LogFunc(string msg, bool warn)
        {
            Log("AutomaticTradingNT8: " + msg, warn ? LogLevel.Warning : LogLevel.Information);
        }

        // ------------------------- API publica (llamada desde Strategies) -------------------------

        /// <summary>
        /// Gate de riesgo antes de abrir. type: 0=Buy, 1=Sell. barTime:
        /// ToTime(Time[0]). priority: 0 = no ocupa el semaforo de turnos.
        ///
        /// accountName: PASARLO SIEMPRE (`Account.Name`). Con varias cuentas
        /// puenteadas, omitirlo gatea contra la cuenta del feed y sus limites no son
        /// los de la que va a recibir la orden.
        /// </summary>
        public static bool CheckTrade(string strategyTag, double lots, int type, long barTime, int priority, out int magic, out string reason, string accountName = null)
        {
            magic = 0;
            reason = "";
            var inst = _instance;
            if (inst == null) { reason = "bridge_not_loaded"; return true; } // fail-open: AddOn no cargado
            return inst.CheckTradeInternal(strategyTag, lots, type, barTime, priority, accountName, out magic, out reason);
        }

        public static void Release(string strategyTag, string accountName = null)
        {
            var inst = _instance;
            if (inst != null) inst.ReleaseInternal(strategyTag, accountName);
        }

        /// <summary>Nombre de entrada a usar en EnterLong/EnterShort para que STATE
        /// pueda asociar la posicion a este magic.</summary>
        public static string OrderTag(string strategyTag, int magic)
        {
            return "ATP_" + magic;
        }

        /// <summary>Link de la cuenta pedida; si no se pide ninguna (o no esta
        /// puenteada), el del feed. Puede ser null si aun no hay ninguna cuenta.</summary>
        private AccountLink LinkFor(string accountName)
        {
            AccountLink link;
            if (!string.IsNullOrEmpty(accountName) && _links.TryGetValue(accountName, out link))
                return link;
            return _dataLink;
        }

        private bool CheckTradeInternal(string strategyTag, double lots, int type, long barTime, int priority, string accountName, out int magic, out string reason)
        {
            reason = "";
            int magicLocal = GetOrAssignMagic(strategyTag);
            magic = magicLocal;

            var link = LinkFor(accountName);
            if (link == null) { reason = "no_account"; return true; }  // fail-open: sin puente, como si el AddOn no estuviera

            // magicLocal y no `out magic`: C# no deja capturarlo en una lambda (CS1628).
            var conn = link.StrategyConns.GetOrAdd(strategyTag, _ =>
                new SocketConn(ServerHost, ServerPort, link.TerminalId, link.AccountName, magicLocal, s => OnPush(link, s), LogFunc));

            if (!conn.IsLoggedIn && !conn.Connect())
            {
                reason = "no_socket";
                return conn.EverConnected == false; // nunca conecto -> asumir app cerrada, operar libre
            }

            string msg = string.Format(CultureInfo.InvariantCulture, "CHECK_TRADE|{0}|{1:F2}|{2}|{3}", type, lots, barTime, priority);
            string response = conn.SendAndWait(msg, CheckTradeTimeoutMs);
            if (response.StartsWith("ALLOW", StringComparison.Ordinal))
            {
                link.InstrumentToMagic[strategyTag] = magic; // best-effort, se refina en OnExecutionUpdate
                return true;
            }
            reason = response;
            return false;
        }

        private void ReleaseInternal(string strategyTag, string accountName)
        {
            var link = LinkFor(accountName);
            if (link == null) return;
            int magic = GetOrAssignMagic(strategyTag);
            SocketConn conn;
            if (link.StrategyConns.TryGetValue(strategyTag, out conn) && conn.IsLoggedIn)
                conn.SendRaw("RELEASE|" + magic);
        }

        /// <summary>Magic determinista a partir del nombre (FNV-1a 32 bits). Debe
        /// dar el MISMO numero que nt8_manager.strategy_magic() en Python: hay un
        /// test que lo comprueba.</summary>
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

        private void SendState(AccountLink link)
        {
            if (link == null || link.Account == null) return;
            try
            {
                double balance = SafeGet(link, AccountItem.CashValue);
                double equity = balance + SafeGet(link, AccountItem.UnrealizedProfitLoss);
                double margin = SafeGet(link, AccountItem.InitialMargin);
                double buyingPower = SafeGet(link, AccountItem.BuyingPower);
                double marginLevel = margin > 0 ? (equity / margin) * 100.0 : 0.0;

                var sb = new StringBuilder();
                sb.Append("STATE|").Append(Num(balance)).Append('|').Append(Num(equity)).Append('|')
                  .Append(Num(margin)).Append('|').Append(Num(marginLevel)).Append('|');

                bool first = true;
                var qtyPorMagic = new Dictionary<long, int>();
                foreach (var p in link.Account.Positions.Where(p => p.MarketPosition != MarketPosition.Flat))
                {
                    if (!first) sb.Append(';');
                    first = false;
                    // Simbolo RAIZ (roll-stable): "MNQ", no "MNQ 09-26".
                    CacheInstrument(p.Instrument);
                    string instrKey = RootSymbol(p.Instrument);
                    long magic;
                    if (!link.InstrumentToMagic.TryGetValue(instrKey, out magic)) magic = 0;
                    // "ticket" sintetico: NT8 netea y no hay id de posicion estable.
                    // SIEMPRE POSITIVO: viaja como `magic` a MT5, que es ulong.
                    int ticket = (int)(unchecked(instrKey.GetHashCode() ^ (magic * 397)) & 0x7FFFFFFF);
                    if (ticket == 0) ticket = 1;   // 0 esta reservado a "sin magic"
                    if (magic != 0)
                    {
                        qtyPorMagic[magic] = Math.Abs(p.Quantity);
                        link.MagicConPosicion[magic] = 1;   // ya sabemos que existio
                    }
                    string type = p.MarketPosition == MarketPosition.Long ? "BUY" : "SELL";
                    double unrealized = 0;
                    try { unrealized = p.GetUnrealizedProfitLoss(PerformanceUnit.Currency); } catch { }

                    // El SL/TP no es un campo de la posicion: son ORDENES contrarias
                    // vivas. Se derivan de ellas para que MT5 pueda copiarlas.
                    double slPrice = 0, tpPrice = 0;
                    FindProtectiveOrders(link, p, out slPrice, out tpPrice);

                    sb.Append(ticket).Append(':').Append(instrKey).Append(':').Append(type).Append(':')
                      .Append(Num(Math.Abs(p.Quantity))).Append(':').Append(Num(p.AveragePrice)).Append(':')
                      .Append(Num(slPrice)).Append(':').Append(Num(tpPrice)).Append(':')
                      .Append(Num(unrealized)).Append(':').Append(magic);
                }

                // El bracket tiene que parecerse a la posicion. Va sobre el ESTADO y
                // no colgando de cada cierre: la reconciliacion aplana con ordenes a
                // mercado y no pasa por ExecuteClose.
                SyncBracketsToPositions(link, qtyPorMagic);

                // Que posiciones se mandan, solo cuando cambian.
                string posCsv = sb.ToString();
                int corte = posCsv.IndexOf('|');
                for (int i = 0; i < 4 && corte >= 0; i++) corte = posCsv.IndexOf('|', corte + 1);
                string soloPos = corte >= 0 && corte + 1 < posCsv.Length ? posCsv.Substring(corte + 1) : "";
                if (soloPos != link.LastPositionsCsv)
                {
                    link.LastPositionsCsv = soloPos;
                    Log("AutomaticTradingNT8: STATE de " + link.AccountName + " — posiciones: " +
                        (soloPos.Length == 0 ? "(ninguna)" : soloPos) +
                        "   [formato ticket:instrumento:tipo:cantidad:precio:sl:tp:pnl:magic]",
                        LogLevel.Information);
                }

                link.Conn.SendRaw(posCsv);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: error enviando STATE de " + link.AccountName + ": " + ex.Message, LogLevel.Warning); }
        }

        private double SafeGet(AccountLink link, AccountItem item)
        {
            try { return link.Account.Get(item, Currency.UsDollar); } catch { return 0.0; }
        }

        // ------------------------- eventos de cuenta -------------------------

        // El bracket se coloca aqui con OrderState==Filled, NUNCA en
        // OnExecutionUpdate: la ejecucion puede llegar ANTES del Filled.
        private void OnOrderUpdate(AccountLink link, OrderEventArgs e)
        {
            try
            {
                if (e == null || e.Order == null) return;

                // Modo reactivo: encolar entradas de terceros (no ATP_) en cuanto la
                // orden es aceptada, no solo al Filled: maxima ventana para cancelar.
                if (ReactiveMode)
                {
                    string nm = e.Order.Name ?? "";
                    if (!nm.StartsWith("ATP_", StringComparison.Ordinal) &&
                        (e.OrderState == OrderState.Accepted || e.OrderState == OrderState.Working ||
                         e.OrderState == OrderState.Submitted || e.OrderState == OrderState.Filled) &&
                        link.ReactiveSeen.TryAdd(e.Order, 1))
                    {
                        try { link.ReactiveQueue.Add(e.Order); } catch { }
                    }
                }

                // Confirmar en cuanto el BROKER se pronuncia sobre una contraria
                // nuestra: antes se deducia del STATE siguiente, hasta 2 s despues.
                if (e.OrderState == OrderState.Working || e.OrderState == OrderState.Rejected)
                    SendSlTpAck(link, e.Order);

                if (e.OrderState != OrderState.Filled) return;

                BracketInfo bi;
                if (link.PendingBrackets.TryRemove(e.Order, out bi))
                    PlaceBracket(link, bi);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnOrderUpdate: " + ex.Message, LogLevel.Error); }
        }

        // ------------------------- modo reactivo (estrategias de terceros) -------------------------

        private void ReactiveLoop(AccountLink link)
        {
            try
            {
                foreach (var order in link.ReactiveQueue.GetConsumingEnumerable())
                {
                    try { ReactiveGate(link, order); }
                    catch (Exception ex) { Log("AutomaticTradingNT8: ReactiveGate: " + ex.Message, LogLevel.Error); }
                }
            }
            catch (Exception ex) { if (_running) Log("AutomaticTradingNT8: ReactiveLoop: " + ex.Message, LogLevel.Warning); }
        }

        private void ReactiveGate(AccountLink link, Order o)
        {
            if (link.Account == null) return;
            if (!IsEntryOrder(link, o)) return;   // cierres/reducciones: siempre permitidos

            int type = o.OrderAction == OrderAction.SellShort ? 1 : 0;
            double lots = o.Quantity;
            long barTime = DateTime.Now.Ticks;

            // priority=0: solo gates globales y limites, sin semaforo de turnos.
            string resp = "ALLOW";
            if (link.Conn != null && link.Conn.IsLoggedIn)
            {
                string msg = string.Format(CultureInfo.InvariantCulture, "CHECK_TRADE|{0}|{1:F2}|{2}|0", type, lots, barTime);
                resp = link.Conn.SendAndWait(msg, CheckTradeTimeoutMs);
            }

            if (resp.StartsWith("ALLOW", StringComparison.Ordinal)) return;

            Log("AutomaticTradingNT8: REACTIVO DENY (" + link.AccountName + ") -> cancelar/cerrar " +
                o.Instrument.FullName + " (" + resp + ")", LogLevel.Warning);
            CancelOrFlatten(link, o);
        }

        // Entrada = abre o aumenta posicion. Cierre/reduccion = permitir siempre.
        private bool IsEntryOrder(AccountLink link, Order o)
        {
            var pos = link.Account.Positions.FirstOrDefault(p => p.Instrument == o.Instrument);
            var mp = pos != null ? pos.MarketPosition : MarketPosition.Flat;
            if (o.OrderAction == OrderAction.Buy) return mp != MarketPosition.Short;       // abre/aumenta long (no cierra un short)
            if (o.OrderAction == OrderAction.SellShort) return mp != MarketPosition.Long;  // abre/aumenta short
            return false; // Sell, BuyToCover = cierres
        }

        private void CancelOrFlatten(AccountLink link, Order o)
        {
            try
            {
                // 1) Si la orden sigue viva, cancelarla. Por EsOrdenViva, no por una
                //    lista de estados "buenos".
                if (EsOrdenViva(o))
                {
                    link.Account.Cancel(new[] { o });
                }
                // 2) Si ya habia posicion (o lleno mientras ibamos), aplanarla.
                //    NT8 netea: esto cierra TODA la posicion del instrumento.
                var pos = link.Account.Positions.FirstOrDefault(p => p.Instrument == o.Instrument && p.MarketPosition != MarketPosition.Flat);
                if (pos != null)
                {
                    var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    var close = link.Account.CreateOrder(pos.Instrument, action, OrderType.Market, OrderEntry.Automated,
                        TimeInForce.Gtc, Math.Abs(pos.Quantity), 0, 0, string.Empty, "ATP_reactive_close",
                        Core.Globals.MaxDate, null);
                    link.Account.Submit(new[] { close });
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CancelOrFlatten: " + ex.Message, LogLevel.Warning); }
        }

        private void OnExecutionUpdate(AccountLink link, ExecutionEventArgs e)
        {
            try
            {
                if (e == null || e.Execution == null || e.Execution.Order == null) return;
                string name = e.Execution.Order.Name ?? "";
                if (name.StartsWith("ATP_", StringComparison.Ordinal))
                {
                    long magic;
                    if (long.TryParse(name.Substring(4), NumberStyles.Any, CultureInfo.InvariantCulture, out magic))
                    {
                        CacheInstrument(e.Execution.Instrument);
                        string instrKey = RootSymbol(e.Execution.Instrument);
                        link.MagicToInstrument[magic] = instrKey;
                        link.InstrumentToMagic[instrKey] = magic;
                    }
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnExecutionUpdate: " + ex.Message, LogLevel.Error); }
        }

        /// <summary>SL y TP equivalentes de una posicion, derivados de sus ordenes
        /// contrarias vivas. Se clasifica por TIPO de orden, no por el lado de la
        /// entrada. Solo cuentan las que cubren la posicion ENTERA: una menor es una
        /// toma parcial y no cabe en los campos sl/tp de MT5. Con varias, la mas
        /// cercana al precio.</summary>
        private void FindProtectiveOrders(AccountLink link, Position p, out double sl, out double tp)
        {
            sl = 0; tp = 0;
            try
            {
                if (link == null || link.Account == null || p == null) return;
                bool isLong = p.MarketPosition == MarketPosition.Long;
                // La orden que protege un largo es una VENTA, y viceversa.
                OrderAction exit = isLong ? OrderAction.Sell : OrderAction.Buy;
                int qty = Math.Abs(p.Quantity);
                string instrKey = RootSymbol(p.Instrument);

                foreach (var o in link.Account.Orders.ToList())
                {
                    if (o == null || o.Instrument == null) continue;
                    if (!EsOrdenViva(o)) continue;
                    if (RootSymbol(o.Instrument) != instrKey) continue;
                    // BuyToCover y SellShort tambien cierran; se normalizan.
                    bool isExitSell = o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort;
                    bool isExitBuy = o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover;
                    if (exit == OrderAction.Sell && !isExitSell) continue;
                    if (exit == OrderAction.Buy && !isExitBuy) continue;
                    if (o.Quantity < qty) continue;   // parcial: no cabe en sl/tp de MT5

                    if (o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit)
                    {
                        if (o.StopPrice > 0 && (sl == 0 || Math.Abs(o.StopPrice - p.AveragePrice) < Math.Abs(sl - p.AveragePrice)))
                            sl = o.StopPrice;
                    }
                    else if (o.OrderType == OrderType.Limit || o.OrderType == OrderType.MIT)
                    {
                        if (o.LimitPrice > 0 && (tp == 0 || Math.Abs(o.LimitPrice - p.AveragePrice) < Math.Abs(tp - p.AveragePrice)))
                            tp = o.LimitPrice;
                    }
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: FindProtectiveOrders: " + ex.Message, LogLevel.Warning); }
        }

        /// <summary>True si la orden sigue en juego. NO se pregunta por
        /// Working/Accepted: entre esos dos hay estados TRANSITORIOS. Se pregunta al
        /// reves, solo esta muerta la que llego a un estado FINAL.</summary>
        private static bool EsOrdenViva(Order o)
        {
            if (o == null) return false;
            return o.OrderState != OrderState.Cancelled
                && o.OrderState != OrderState.Filled
                && o.OrderState != OrderState.Rejected
                && o.OrderState != OrderState.Unknown;
        }

        /// <summary>Deja las contrarias como esta la posicion: las de una plana se
        /// cancelan, las de una viva se ajustan a su cantidad. Una Stop huerfana ABRE
        /// posicion en sentido contrario si el precio la toca.
        ///
        /// Va en el bucle de STATE y no en ExecuteClose: la reconciliacion aplana con
        /// ordenes a mercado y no pasa por ahi.</summary>
        private void SyncBracketsToPositions(AccountLink link, Dictionary<long, int> qtyPorMagic)
        {
            if (link == null || link.Account == null) return;
            try
            {
                AdoptOrphanBrackets(link);
                foreach (var magic in link.ActiveBrackets.Keys.ToList())
                {
                    int qtyPos;
                    if (qtyPorMagic.TryGetValue(magic, out qtyPos))
                    {
                        ResizeBracket(link, magic, qtyPos);
                        continue;                                       // sigue abierta
                    }
                    if (!link.MagicConPosicion.ContainsKey(magic)) continue;  // aun no llego

                    List<Order> anotadas;
                    if (!link.ActiveBrackets.TryGetValue(magic, out anotadas)) continue;

                    List<Order> vivas;
                    lock (anotadas)
                    {
                        vivas = anotadas.Where(EsOrdenViva).ToList();
                        anotadas.Clear();
                    }
                    List<Order> _v;
                    link.ActiveBrackets.TryRemove(magic, out _v);
                    byte _b;
                    link.MagicConPosicion.TryRemove(magic, out _b);
                    if (vivas.Count == 0) continue;

                    link.Account.Cancel(vivas.ToArray());
                    Log("AutomaticTradingNT8: magic=" + magic + ": posicion plana, " + vivas.Count +
                        " contraria(s) cancelada(s) en " + link.AccountName + ".", LogLevel.Information);
                }
            }
            catch (Exception ex)
            {
                Log("AutomaticTradingNT8: SyncBracketsToPositions: " + ex.Message, LogLevel.Warning);
            }
        }

        /// <summary>Dice al motor que niveles tiene AHORA el bracket. Se manda el PAR
        /// entero, no la pierna que cambio.</summary>
        private void SendSlTpAck(AccountLink link, Order o)
        {
            try
            {
                if (o == null || link == null || link.Conn == null) return;
                string nombre = o.Name ?? "";
                if (!nombre.StartsWith("ATP_bracket_", StringComparison.Ordinal)) return;

                long magic;
                if (!long.TryParse(nombre.Substring("ATP_bracket_".Length), out magic)) return;

                bool ok = o.OrderState != OrderState.Rejected;
                double sl = 0, tp = 0;
                List<Order> anotadas;
                if (link.ActiveBrackets.TryGetValue(magic, out anotadas))
                {
                    lock (anotadas)
                    {
                        foreach (var b in anotadas)
                        {
                            if (!EsOrdenViva(b)) continue;
                            if (b.OrderType == OrderType.StopMarket || b.OrderType == OrderType.StopLimit)
                                sl = b.StopPrice;
                            else if (b.OrderType == OrderType.Limit || b.OrderType == OrderType.MIT)
                                tp = b.LimitPrice;
                        }
                    }
                }
                link.Conn.SendRaw("SLTP_ACK|" + magic + "|" + Num(sl) + "|" + Num(tp) + "|" + (ok ? "1" : "0"));
                if (!ok)
                    Log("AutomaticTradingNT8: magic=" + magic + ": el broker RECHAZO la contraria (" +
                        o.OrderType + ").", LogLevel.Warning);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: SendSlTpAck: " + ex.Message, LogLevel.Warning); }
        }

        /// <summary>Readopta las contrarias que sobrevivieron a un reinicio del
        /// AddOn: ActiveBrackets vive en memoria, las ordenes no.</summary>
        private void AdoptOrphanBrackets(AccountLink link)
        {
            foreach (var o in link.Account.Orders.ToList())
            {
                if (!EsOrdenViva(o)) continue;
                string nombre = o.Name ?? "";
                if (!nombre.StartsWith("ATP_bracket_", StringComparison.Ordinal)) continue;

                long magic;
                if (!long.TryParse(nombre.Substring("ATP_bracket_".Length), out magic)) continue;
                if (magic == 0) continue;

                bool nueva = false;
                var lista = link.ActiveBrackets.GetOrAdd(magic, _ => new List<Order>());
                lock (lista)
                {
                    if (!lista.Contains(o)) { lista.Add(o); nueva = true; }
                }
                if (!nueva) continue;

                // Se marca SIEMPRE, tenga posicion ahora o no. Marcar de mas se cura
                // solo (el motor reenvia CMD_UPDATE_SLTP); una contraria viva sin
                // posicion detras no se cura nunca.
                link.MagicConPosicion[magic] = 1;
                Log("AutomaticTradingNT8: magic=" + magic + ": contraria recuperada tras reinicio (" +
                    o.OrderType + " " + o.Quantity + ").", LogLevel.Information);
            }
        }

        /// <summary>Ajusta la cantidad de las contrarias a la de la posicion. Si
        /// cubren MAS de lo que hay, al saltar abren posicion contraria.</summary>
        private void ResizeBracket(AccountLink link, long magic, int qtyPos)
        {
            if (qtyPos <= 0) return;
            List<Order> anotadas;
            if (!link.ActiveBrackets.TryGetValue(magic, out anotadas)) return;

            var ajustar = new List<Order>();
            lock (anotadas)
            {
                anotadas.RemoveAll(o => !EsOrdenViva(o));
                foreach (var o in anotadas)
                {
                    if (o.Quantity == qtyPos) continue;
                    // QuantityChanged, no Quantity: ver la nota de ExecuteUpdateSlTp.
                    o.QuantityChanged = qtyPos;
                    ajustar.Add(o);
                }
            }
            if (ajustar.Count == 0) return;

            link.Account.Change(ajustar.ToArray());
            Log("AutomaticTradingNT8: magic=" + magic + ": bracket ajustado a " + qtyPos +
                " contrato(s) (" + ajustar.Count + " orden/es).", LogLevel.Information);
        }

        /// <summary>Ordenes del bracket que este AddOn coloco para un magic.</summary>
        private System.Collections.Generic.List<Order> FindBracketOrders(AccountLink link, long magic)
        {
            var res = new System.Collections.Generic.List<Order>();
            try
            {
                string instrKey;
                if (!link.MagicToInstrument.TryGetValue(magic, out instrKey)) return res;
                // El registro propio primero: es exacto mientras el AddOn corre.
                List<Order> anotadas;
                if (link.ActiveBrackets.TryGetValue(magic, out anotadas))
                {
                    lock (anotadas)
                    {
                        anotadas.RemoveAll(o => !EsOrdenViva(o));
                        res.AddRange(anotadas);
                        if (anotadas.Count == 0) { List<Order> _v; link.ActiveBrackets.TryRemove(magic, out _v); }
                    }
                    if (res.Count > 0) return res;
                }

                // Sin registro (AddOn reiniciado): por el nombre, que lleva el magic.
                foreach (var o in link.Account.Orders.ToList())
                {
                    if (o == null || o.Instrument == null) continue;
                    if (!EsOrdenViva(o)) continue;
                    // Se acepta el nombre viejo ("ATP_bracket" a secas) de otra version.
                    bool mio = string.Equals(o.Name, BracketName(magic), StringComparison.Ordinal)
                               || string.Equals(o.Name, "ATP_bracket", StringComparison.Ordinal);
                    if (!mio) continue;
                    if (RootSymbol(o.Instrument) != instrKey) continue;
                    res.Add(o);
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: FindBracketOrders: " + ex.Message, LogLevel.Warning); }
            return res;
        }

        /// <summary>Ajusta un precio al tick. Los niveles llegan traducidos por
        /// proporcion y Account.Change descarta EN SILENCIO lo que no sea multiplo
        /// del tick.</summary>
        private static double RoundToTick(NinjaTrader.Cbi.Instrument instr, double price)
        {
            if (instr == null || price <= 0) return price;
            try
            {
                if (instr.MasterInstrument != null)
                    return instr.MasterInstrument.RoundToTickSize(price);
            }
            catch { }
            return price;
        }

        /// <summary>Coloca las contrarias que hacen de SL/TP.
        ///
        /// `ocoExistente`: si ya hay una pierna viva se reutiliza SU id de OCO,
        /// para que la nueva quede atada a ella (si una llena, la otra se cancela
        /// sola). Solo se genera uno nuevo cuando no hay ninguna.</summary>
        private void PlaceBracket(AccountLink link, BracketInfo b, string ocoExistente = null)
        {
            if (link == null || link.Account == null) return;
            try
            {
                string oco = !string.IsNullOrEmpty(ocoExistente)
                    ? ocoExistente
                    : "ATP_OCO_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                // El nombre lleva el magic: si no, dos copias sobre el mismo
                // instrumento se pisan.
                string nombre = BracketName(b.Magic);
                var children = new System.Collections.Generic.List<Order>();
                // Al tick tambien aqui: el precio que registramos es el que tendra.
                double sl = RoundToTick(b.Instrument, b.Sl);
                double tp = RoundToTick(b.Instrument, b.Tp);
                if (sl > 0)
                    children.Add(link.Account.CreateOrder(b.Instrument, b.ExitAction, OrderType.StopMarket,
                        OrderEntry.Automated, TimeInForce.Gtc, b.Qty, 0, sl, oco, nombre,
                        Core.Globals.MaxDate, null));
                if (tp > 0)
                    children.Add(link.Account.CreateOrder(b.Instrument, b.ExitAction, OrderType.Limit,
                        OrderEntry.Automated, TimeInForce.Gtc, b.Qty, tp, 0, oco, nombre,
                        Core.Globals.MaxDate, null));
                if (children.Count > 0)
                {
                    link.Account.Submit(children.ToArray());
                    // Anotar ANTES de que nadie pregunte: es la asociacion.
                    link.ActiveBrackets.AddOrUpdate(b.Magic,
                        _ => new List<Order>(children),
                        (_, previas) => { lock (previas) { previas.AddRange(children); } return previas; });
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: PlaceBracket: " + ex.Message, LogLevel.Warning); }
        }

        // ------------------------- comandos push (CMD_OPEN/CMD_CLOSE/CMD_UPDATE_SLTP) -------------------------
        // Destino de copia de senales MT5->NT8: el AddOn actua sobre la cuenta, sin
        // necesidad de ninguna Strategy activa.
        //
        // Los CMD_ de cuenta actuan sobre `link`, la cuenta por cuya conexion
        // llegaron. Los de mercado responden por DataConn.
        private void HandleCommand(AccountLink link, string line)
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
                long magic = ParseMagic(parts[6]);
                ExecuteOpen(link, symbol, type, volume, sl, tp, magic);
            }
            else if (cmd == "CMD_CLOSE" && parts.Length >= 2)
            {
                long magic = ParseMagic(parts[1]);
                ExecuteClose(link, magic);
            }
            else if (cmd == "CMD_UPDATE_SLTP" && parts.Length >= 4)
            {
                long magic = ParseMagic(parts[1]);
                double sl = ParseD(parts[2]);
                double tp = ParseD(parts[3]);
                ExecuteUpdateSlTp(link, magic, sl, tp);
            }
            else if (cmd == "CMD_CLOSE_PARTIAL" && parts.Length >= 3)
            {
                long magic = ParseMagic(parts[1]);
                double qty = ParseD(parts[2]);
                ExecuteClosePartial(link, magic, qty);
            }
            else if (cmd == "CMD_FLATTEN")
            {
                FlattenAll(link);
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
                // Las N+1 marcas (UTC ms) son las FRONTERAS de las N velas de MT5.
                // Explicitas: las velas de MT5 no son uniformes.
                StartProfile(parts[1], parts[2]);
            }
            else if (cmd == "CMD_SYMBOLS")
            {
                SendSymbols(link);
            }
        }

        // ------------------------- instrumentos disponibles (CMD_SYMBOLS) -------------------------

        /// <summary>Responde SYMBOLS|&lt;raiz&gt;,... para que la app pueda MAPEAR
        /// simbolos entre terminales. La fuente son las LISTAS DE INSTRUMENTOS del
        /// usuario, no la base de datos entera. Va por la conexion de `link`: la app
        /// guarda la lista por terminal.</summary>
        private void SendSymbols(AccountLink link)
        {
            if (link == null || link.Conn == null || !link.Conn.IsLoggedIn) return;

            var roots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                lock (InstrumentList.All)
                {
                    foreach (var list in InstrumentList.All)
                    {
                        if (list == null || list.Instruments == null) continue;
                        foreach (var instr in list.Instruments)
                        {
                            string root = RootSymbol(instr);
                            if (!string.IsNullOrEmpty(root)) roots.Add(root);
                        }
                    }
                }
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_SYMBOLS listas: " + ex.Message, LogLevel.Warning); }

            try
            {
                if (link.Account != null)
                    foreach (var p in link.Account.Positions)
                    {
                        string root = RootSymbol(p.Instrument);
                        if (!string.IsNullOrEmpty(root)) roots.Add(root);
                    }
            }
            catch { }

            foreach (var k in _rootCache.Keys) roots.Add(k);

            // Sin listas configuradas se manda vacio, no la base de datos entera.
            if (roots.Count == 0)
                Log("AutomaticTradingNT8: CMD_SYMBOLS — no hay instrumentos en las listas de NinjaTrader. " +
                    "Añade los que operes a una lista (o abre posicion) para que aparezcan en el mapeo de la aplicacion.",
                    LogLevel.Warning);

            var names = roots.Take(SymbolsMax).ToArray();
            link.Conn.SendRaw("SYMBOLS|" + string.Join(",", names));
            // Tocar el tope hay que DECIRLO: truncar en silencio despista al usuario.
            if (roots.Count > names.Length)
                Log("AutomaticTradingNT8: CMD_SYMBOLS — " + roots.Count + " instrumentos en las listas, " +
                    "se mandan los " + names.Length + " primeros por orden alfabetico. Reduce tus listas de " +
                    "NinjaTrader si falta alguno en el mapeo.", LogLevel.Warning);
            Log("AutomaticTradingNT8: CMD_SYMBOLS — " + names.Length + " instrumento(s) enviados desde " +
                link.AccountName + ".", LogLevel.Information);
        }

        // ------------------------- historico de operaciones (CMD_HISTORY) -------------------------

        // Vuelca las ultimas N operaciones como TRADE|. El agresor se reconstruye
        // igual que en vivo (Lee-Ready): se piden TAMBIEN las series de Bid y Ask
        // para fechar cada operacion. Si el feed no las sirve, side=0 y el EA cae a
        // la tick-rule.
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

        // BarsRequest de ticks: por rango de fechas si el EA manda una, o por numero
        // de ticks. OJO: MarketDataType va en BarsPeriod, NO en BarsRequest (CS0117),
        // y el constructor por fechas las toma en hora LOCAL.
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

                        // NT8 puede devolver muchos mas trades de los que caben en
                        // memoria: se envian los MAS RECIENTES.
                        int first = (nTicks > 0 && n > nTicks) ? n - nTicks : 0;
                        if (first > 0)
                            Log("AutomaticTradingNT8: " + root + " — el rango pedido tiene " + n +
                                " operaciones, por encima del tope de " + nTicks +
                                ". Se envian las mas recientes.", LogLevel.Information);

                        for (int i = first; i < n; i++)
                        {
                            if (!_running || !DataReady) break;

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

                            SendData("TRADE|" + root + "|" + tms + "|" + Num(price) + "|" +
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
                        // Avisar del fin del volcado: sin esto el EA se cree al dia en
                        // cuanto un sondeo le devuelve menos trades de los que pidio.
                        SendData("HISTORY_DONE|" + root);
                    }
                });
            }
            catch (Exception ex)
            {
                Log("AutomaticTradingNT8: historico de operaciones: " + ex.Message, LogLevel.Warning);
                FlushLiveQueue(root, 0);
                SendData("HISTORY_DONE|" + root);   // fallo: que el EA no espere para siempre
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
                SendData(item.Value);
                sent++;
            }
            if (sent > 0 || dup > 0)
                Log("AutomaticTradingNT8: " + root + " — " + sent + " trades en vivo retenidos durante el " +
                    "historico enviados (" + dup + " ya venian en el historico).", LogLevel.Information);
        }

        // ------------------------- perfil agregado (CMD_PROFILE) -------------------------
        //
        // No se mandan operaciones: se manda la ESCALERA ya agregada por vela. Una
        // vela D1 son ~2400 niveles en vez de millones de trades, que es lo que el
        // camino de ticks no puede servir. El agresor lo traen las barras Volumetric.
        //
        // DISCIPLINA DE MEMORIA: se recorre en streaming, acumulando en la vela EN
        // CURSO y liberando al cruzar la frontera. Nunca materializar la serie.

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
                // En el BarsPeriod Volumetric, 'Value' son las MARCAS POR NIVEL y el
                // periodo base va en BaseBarsPeriodType/Value. Se deja en 1: cuantiza
                // el EA, que adapta su paso de celda al ATR y al zoom.
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
                if (!_running || !DataReady) break;

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
            SendData(sb.ToString());

            // Una escalera D1 son ~48 KB: varias seguidas dejan a NT8 sin responder.
            Thread.Sleep(HistPauseMs);
            return true;
        }

        private void SendProfileDone(string root, int nBars)
        {
            SendData("PROFILE_DONE|" + root + "|" + nBars);
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

                // Suscribirse SIEMPRE funciona, aunque el feed no vaya a mandar nada.
                // Decir "compartiendo" sin comprobarlo es mentir: a los 10 s se mira.
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

        // Suelta TODAS las suscripciones de ticks al conectar: la duena del estado de
        // compartir es la aplicacion, que las vuelve a pedir tras el LOGIN.
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
                if (!DataReady) return;

                string root = RootSymbol(e.Instrument);
                var md = e.Instrument.MarketData;
                double bid = md != null && md.Bid != null ? md.Bid.Price : 0;
                double ask = md != null && md.Ask != null ? md.Ask.Price : 0;

                // Diagnostico: si nunca llega un 'Last' no hay cinta de operaciones y
                // el footprint real es imposible con ese instrumento.
                if (_mdSeen.TryAdd(root + "|" + e.MarketDataType, 0))
                    Log("AutomaticTradingNT8: " + root + " -> primer dato de tipo " +
                        e.MarketDataType + " (precio=" + Num(e.Price) + " vol=" + Num(e.Volume) + ")",
                        LogLevel.Information);

                // --- TRADE: operacion EJECUTADA. Es lo que hace REAL un footprint.
                //     SIN THROTTLE: hacen falta todos, no una muestra.
                if (e.MarketDataType == MarketDataType.Last)
                {
                    // Agresor (Lee-Ready): aqui conocemos el bid/ask EXACTO del
                    // instante. Deducirlo luego en MT5 solo es un proxy.
                    int side = 0;
                    if (ask > 0 && e.Price >= ask) side = 1;        // paga el ask -> comprador agresor
                    else if (bid > 0 && e.Price <= bid) side = -1;  // pega al bid -> vendedor agresor

                    long tms = new DateTimeOffset(e.Time.ToUniversalTime()).ToUnixTimeMilliseconds();
                    string trade = "TRADE|" + root + "|" + tms + "|" + Num(e.Price) + "|" +
                                   Num(e.Volume) + "|" + Num(bid) + "|" + Num(ask) + "|" + side;

                    // Volcado en curso: retener, o romperiamos el orden cronologico.
                    ConcurrentQueue<KeyValuePair<long, string>> hq;
                    if (_histQueue.TryGetValue(root, out hq) && hq != null)
                    {
                        hq.Enqueue(new KeyValuePair<long, string>(tms, trade));
                        return;
                    }

                    SendData(trade);

                    long sent = _tradesSent.AddOrUpdate(root, 1, (k, v) => v + 1);
                    if (sent == 1 || sent % 500 == 0)
                        Log("AutomaticTradingNT8: " + sent + " trades enviados de " + root +
                            " (ultimo " + Num(e.Price) + " vol=" + Num(e.Volume) + " side=" + side + ")",
                            LogLevel.Information);
                    return;
                }

                // --- TICK: cotizacion. Referencia de precio (GET_TICK), no order
                //     flow: aqui SI se throttlea.
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                long lastSent;
                if (_lastTickSentMs.TryGetValue(root, out lastSent) && now - lastSent < TickThrottleMs) return;
                _lastTickSentMs[root] = now;

                double lastPx = md != null && md.Last != null ? md.Last.Price : 0;
                long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SendData("TICK|" + root + "|" + Num(bid) + "|" + Num(ask) + "|" + Num(lastPx) + "|" + epoch);
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

                // Throttle ANTES de leer el libro: lo mantiene NinjaTrader, asi que
                // saltarse un evento no pierde nada.
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                long last;
                if (_lastDepthSentMs.TryGetValue(root, out last) && now - last < DepthThrottleMs) return;
                _lastDepthSentMs[root] = now;

                // El libro se lee de NinjaTrader, NO de una copia propia (envejece: los
                // niveles que deja de reportar no siempre traen su Remove). Ya viene
                // ordenado, y se lee bajo SyncMarketDepth como hace el SuperDOM.
                string bidsCsv, asksCsv;
                lock (e.Instrument.SyncMarketDepth)
                {
                    bidsCsv = string.Join(",", instr.MarketDepth.Bids.Take(DepthLevels)
                        .Select(r => Num(r.Price) + ":" + r.Volume));
                    asksCsv = string.Join(",", instr.MarketDepth.Asks.Take(DepthLevels)
                        .Select(r => Num(r.Price) + ":" + r.Volume));
                }

                SendData("DEPTH|" + root + "|" + bidsCsv + "|" + asksCsv);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: OnMarketDepth: " + ex.Message, LogLevel.Warning); }
        }

        // Aplana TODAS las posiciones (parada de emergencia). Incluye las ATP_
        // propias: la emergencia cierra TODO, sin excepciones.
        private void FlattenAll(AccountLink link)
        {
            if (link == null || link.Account == null) return;
            try
            {
                // 1) Cancelar PRIMERO las ordenes de trabajo: si se hace despues, la
                //    propia orden de cierre se cancelaria a si misma.
                var working = link.Account.Orders.Where(EsOrdenViva).ToList();
                if (working.Count > 0) link.Account.Cancel(working.ToArray());

                // 2) Aplanar todas las posiciones abiertas con market opuesta.
                var open = link.Account.Positions.Where(p => p.MarketPosition != MarketPosition.Flat).ToList();
                if (open.Count == 0)
                {
                    Log("AutomaticTradingNT8: CMD_FLATTEN (" + link.AccountName + ") — no hay posiciones abiertas.", LogLevel.Information);
                    return;
                }
                foreach (var pos in open)
                {
                    var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    var close = link.Account.CreateOrder(pos.Instrument, action, OrderType.Market, OrderEntry.Automated,
                        TimeInForce.Gtc, Math.Abs(pos.Quantity), 0, 0, string.Empty, "ATP_flatten",
                        Core.Globals.MaxDate, null);
                    link.Account.Submit(new[] { close });
                }
                Log("AutomaticTradingNT8: CMD_FLATTEN (" + link.AccountName + ") — cerradas " + open.Count + " posiciones.", LogLevel.Warning);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: FlattenAll: " + ex.Message, LogLevel.Error); }
        }

        private void ExecuteOpen(AccountLink link, string symbol, int type, double volume, double sl, double tp, long magic)
        {
            if (link == null || link.Account == null) { Log("AutomaticTradingNT8: CMD_OPEN sin cuenta.", LogLevel.Warning); return; }
            // symbol puede venir como raiz o como contrato completo.
            var instrument = ResolveInstrument(symbol);
            if (instrument == null) { Log("AutomaticTradingNT8: CMD_OPEN instrumento no resuelto: " + symbol, LogLevel.Warning); return; }

            int qty = Math.Max(1, (int)Math.Round(volume));
            OrderAction action = type == 0 ? OrderAction.Buy : OrderAction.Sell;
            try
            {
                var order = link.Account.CreateOrder(instrument, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Gtc,
                    qty, 0, 0, string.Empty, "ATP_" + magic, Core.Globals.MaxDate, null);
                link.Account.Submit(new[] { order });
                if (sl > 0 || tp > 0)
                {
                    link.PendingBrackets[order] = new BracketInfo
                    {
                        Instrument = instrument,
                        ExitAction = action == OrderAction.Buy ? OrderAction.Sell : OrderAction.Buy,
                        Qty = qty, Sl = sl, Tp = tp, Magic = magic,
                    };
                }
                string openRoot = RootSymbol(instrument);
                link.MagicToInstrument[magic] = openRoot;
                link.InstrumentToMagic[openRoot] = magic;
                Log("AutomaticTradingNT8: CMD_OPEN " + symbol + " " + action + " " + qty + " magic=" + magic +
                    " en " + link.AccountName, LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_OPEN error: " + ex.Message, LogLevel.Error); }
        }

        private void ExecuteClose(AccountLink link, long magic)
        {
            if (link == null || link.Account == null) { Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " sin cuenta.", LogLevel.Warning); return; }
            string instrKey;
            if (!link.MagicToInstrument.TryGetValue(magic, out instrKey))
            {
                // No callar: si el magic no se conoce, la copia se queda ABIERTA.
                Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " desconocido en " + link.AccountName +
                    ": no se puede cerrar (posicion sin asociar).", LogLevel.Warning);
                return;
            }
            try
            {
                var pos = link.Account.Positions.FirstOrDefault(p => RootSymbol(p.Instrument) == instrKey && p.MarketPosition != MarketPosition.Flat);
                if (pos == null)
                {
                    Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " (" + instrKey + ", " + link.AccountName +
                        "): ya no hay posicion abierta.", LogLevel.Information);
                    return;
                }
                // PRIMERO cancelar el bracket, DESPUES aplanar: el OCO ata las dos
                // ordenes ENTRE SI, no a la posicion. Si la cierra una tercera, las
                // pendientes siguen vivas y la que salte abre posicion contraria.
                var brackets = FindBracketOrders(link, magic);
                if (brackets.Count > 0)
                {
                    link.Account.Cancel(brackets.ToArray());
                    Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + ": cancelado bracket (" +
                        brackets.Count + " orden/es) antes de aplanar.", LogLevel.Information);
                }

                var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                var order = link.Account.CreateOrder(pos.Instrument, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Gtc,
                    Math.Abs(pos.Quantity), 0, 0, string.Empty, "ATP_" + magic, Core.Globals.MaxDate, null);
                link.Account.Submit(new[] { order });
                Log("AutomaticTradingNT8: CMD_CLOSE magic=" + magic + " " + instrKey + " en " + link.AccountName, LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_CLOSE error: " + ex.Message, LogLevel.Error); }
        }

        /// <summary>Mueve (o crea) el bracket SL/TP de un magic. Usa Account.Change
        /// en vez de cancelar y recolocar: cancelar deja una ventana sin
        /// proteccion.</summary>
        private void ExecuteUpdateSlTp(AccountLink link, long magic, double sl, double tp)
        {
            if (link == null || link.Account == null) return;
            try
            {
                string instrKey;
                if (!link.MagicToInstrument.TryGetValue(magic, out instrKey))
                {
                    Log("AutomaticTradingNT8: CMD_UPDATE_SLTP magic=" + magic +
                        " desconocido: no se puede situar el bracket.", LogLevel.Warning);
                    return;
                }
                var pos = link.Account.Positions.FirstOrDefault(
                    p => RootSymbol(p.Instrument) == instrKey && p.MarketPosition != MarketPosition.Flat);
                if (pos == null) return;

                // AL TICK ANTES DE TOCAR NADA: Account.Change descarta en silencio.
                sl = RoundToTick(pos.Instrument, sl);
                tp = RoundToTick(pos.Instrument, tp);

                var existentes = FindBracketOrders(link, magic);
                var cambiar = new System.Collections.Generic.List<Order>();

                // La cantidad del bracket tiene que seguir a la posicion: al ampliar,
                // NT8 netea y el bracket se quedaba corto (contratos desnudos), y
                // FindProtectiveOrders lo descarta, asi que el STATE reportaba 0:0.
                int qtyPos = Math.Abs(pos.Quantity);
                // OJO: Account.Change NO lee StopPrice/LimitPrice, lee
                // StopPriceChanged/LimitPriceChanged. Asignar la propiedad normal solo
                // muta el objeto LOCAL y ademas hace que FindProtectiveOrders reporte
                // un nivel FANTASMA.
                var cancelar = new System.Collections.Generic.List<Order>();
                bool haySl = false, hayTp = false;
                string ocoVivo = null;
                foreach (var o in existentes)
                {
                    bool esStop = o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit;
                    bool esLimit = o.OrderType == OrderType.Limit;
                    if (!esStop && !esLimit) continue;

                    // El emisor BORRO ese nivel: cancelar la contraria. Si no, sigue
                    // trabajando y cerraria la copia con el emisor aun dentro.
                    double objetivo = esStop ? sl : tp;
                    if (objetivo <= 0) { cancelar.Add(o); continue; }

                    // Solo el OCO de las que SOBREVIVEN: atar una pierna nueva al
                    // grupo de una que acabamos de cancelar no ata nada.
                    if (!string.IsNullOrEmpty(o.Oco)) ocoVivo = o.Oco;

                    // Precio y cantidad por separado: al ampliar sin mover el stop, el
                    // precio ya esta bien y solo falta crecer.
                    bool toca = false;
                    if (o.Quantity != qtyPos) { o.QuantityChanged = qtyPos; toca = true; }

                    if (esStop)
                    {
                        haySl = true;
                        if (Math.Abs(o.StopPrice - sl) >= 1e-9) { o.StopPriceChanged = sl; toca = true; }
                    }
                    else
                    {
                        hayTp = true;
                        if (Math.Abs(o.LimitPrice - tp) >= 1e-9) { o.LimitPriceChanged = tp; toca = true; }
                    }
                    if (toca) cambiar.Add(o);
                }

                if (cancelar.Count > 0)
                {
                    link.Account.Cancel(cancelar.ToArray());
                    // CANCELAR UNA PIERNA MATA EL GRUPO OCO ENTERO: lo que el emisor
                    // SIGA queriendo hay que recolocarlo con un OCO nuevo.
                    haySl = false; hayTp = false; ocoVivo = null;
                    cambiar.Clear();   // esas ordenes ya no existen: no se pueden mover
                    Log("AutomaticTradingNT8: CMD_UPDATE_SLTP magic=" + magic + ": el emisor quito " +
                        (sl <= 0 ? "el SL " : "") + (tp <= 0 ? "el TP " : "") +
                        "-> cancelado el grupo OCO (" + cancelar.Count + " pedida(s), cae el grupo entero)" +
                        ((sl > 0 || tp > 0) ? "; se recoloca lo que queda." : "."), LogLevel.Information);
                }
                if (cambiar.Count > 0)
                {
                    link.Account.Change(cambiar.ToArray());
                    Log("AutomaticTradingNT8: CMD_UPDATE_SLTP magic=" + magic + " SL=" + Num(sl) +
                        " TP=" + Num(tp) + " (" + cambiar.Count + " orden/es movidas)", LogLevel.Information);
                }

                // Piernas que el emisor quiere y aun no existen. Reutilizan el OCO de
                // las que ya estan.
                double faltaSl = (sl > 0 && !haySl) ? sl : 0;
                double faltaTp = (tp > 0 && !hayTp) ? tp : 0;
                if (faltaSl <= 0 && faltaTp <= 0) return;

                PlaceBracket(link, new BracketInfo
                {
                    Instrument = pos.Instrument,
                    ExitAction = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy,
                    Qty = Math.Abs(pos.Quantity),
                    Sl = faltaSl,
                    Tp = faltaTp,
                    Magic = magic,
                }, ocoVivo);
                Log("AutomaticTradingNT8: CMD_UPDATE_SLTP magic=" + magic +
                    " contraria(s) colocada(s) SL=" + Num(faltaSl) + " TP=" + Num(faltaTp), LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_UPDATE_SLTP error: " + ex.Message, LogLevel.Error); }
        }

        /// <summary>Recorta `qty` contratos sin cerrar la posicion. El bracket
        /// sobrante se ajusta a lo que queda.</summary>
        private void ExecuteClosePartial(AccountLink link, long magic, double qty)
        {
            if (link == null || link.Account == null) return;
            int cantidad = (int)Math.Round(qty);
            if (cantidad <= 0) return;
            try
            {
                string instrKey;
                if (!link.MagicToInstrument.TryGetValue(magic, out instrKey))
                {
                    Log("AutomaticTradingNT8: CMD_CLOSE_PARTIAL magic=" + magic +
                        " desconocido: no se recorta nada.", LogLevel.Warning);
                    return;
                }
                var pos = link.Account.Positions.FirstOrDefault(
                    p => RootSymbol(p.Instrument) == instrKey && p.MarketPosition != MarketPosition.Flat);
                if (pos == null) return;

                int vivos = Math.Abs(pos.Quantity);
                if (cantidad >= vivos)
                {
                    // Recorte que se come la posicion entera: es un cierre.
                    ExecuteClose(link, magic);
                    return;
                }
                var action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                var order = link.Account.CreateOrder(pos.Instrument, action, OrderType.Market,
                    OrderEntry.Automated, TimeInForce.Gtc, cantidad, 0, 0, string.Empty,
                    "ATP_" + magic, Core.Globals.MaxDate, null);
                link.Account.Submit(new[] { order });

                // Encoger el bracket a lo que queda.
                int restantes = vivos - cantidad;
                var brackets = FindBracketOrders(link, magic);
                if (brackets.Count > 0)
                {
                    // QuantityChanged, no Quantity: ver la nota de ExecuteUpdateSlTp.
                    foreach (var o in brackets) o.QuantityChanged = restantes;
                    link.Account.Change(brackets.ToArray());
                }
                Log("AutomaticTradingNT8: CMD_CLOSE_PARTIAL magic=" + magic + " -" + cantidad +
                    " (quedan " + restantes + ") en " + link.AccountName, LogLevel.Information);
            }
            catch (Exception ex) { Log("AutomaticTradingNT8: CMD_CLOSE_PARTIAL error: " + ex.Message, LogLevel.Error); }
        }

        /// <summary>Magic que llega en los CMD_. En la copia MT5 -&gt; NT8 el magic ES
        /// el TICKET origen, y los de MT5 son `ulong`: con `int` desbordaban y un
        /// CMD_CLOSE cerraba la posicion equivocada. Por eso `long` en todo el
        /// camino.</summary>
        private static long ParseMagic(string s)
        {
            long v;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return (long)ParseD(s);   // por si llegara con decimales ("123.0")
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

        // Cache raiz -> instrumento, sembrada con todo lo que vemos.
        private readonly ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument> _rootCache =
            new ConcurrentDictionary<string, NinjaTrader.Cbi.Instrument>();

        private void CacheInstrument(NinjaTrader.Cbi.Instrument instr)
        {
            if (instr == null) return;
            string root = RootSymbol(instr);
            if (!string.IsNullOrEmpty(root)) _rootCache[root] = instr;
        }

        /// <summary>Resuelve un symbol al Instrument de NT8: nombre completo
        /// ("MNQ 09-26") o RAIZ ("MNQ").
        ///
        /// El futuro se resuelve ANTES de preguntar por el nombre pelado, porque
        /// GetInstrument("MGC") NO devuelve null: devuelve la ACCION MGC, que no esta
        /// en el feed. Y el contrato que toca no es el de vencimiento mas proximo,
        /// sino el que marcan los roll settings.</summary>
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

        /// <summary>Contrato en vigor de una raiz de futuro ("MGC" -> MGC 12-26), o
        /// null si no es un futuro. Sale de MasterInstrument.RolloverCollection: vale
        /// el Rollover de Date mas reciente ya cumplida.</summary>
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

                // Sin calendario de rolls: el mas proximo sin expirar. Puede caer en
                // un contrato en periodo de aviso.
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
        //  SocketConn: una conexion TCP al servidor (LOGIN + PING + llamadas
        //  sincronas + recepcion de push). Cada strategy usa su propia instancia,
        //  mismo modelo que un EA MT5: un socket == una identidad/magic.
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

            // Se dispara al aceptar el LOGIN, tambien en cada RE-conexion.
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

                    // LOGIN_OK lo consume HandleLine: aqui se espera al flag.
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
                catch { /* socket cerrado/muerto: no bloquear reintentos */ }
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
                // Handshake: NUNCA encolar. En la cola de respuestas, la primera
                // CHECK_TRADE los leeria como si fueran suya.
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

            // Una sola llamada sincrona en vuelo por conexion.
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
        /// <summary>accountName: pasar SIEMPRE `Account.Name` desde la strategy.
        /// Con varias cuentas NT8 puenteadas, omitirlo gatea contra la cuenta del
        /// feed y no contra la que va a recibir la orden.</summary>
        public static bool CheckTrade(string strategyTag, double lots, int type, long barTime, int priority, out int magic, out string reason, string accountName = null)
        {
            return AutomaticTradingNT8.CheckTrade(strategyTag, lots, type, barTime, priority, out magic, out reason, accountName);
        }

        public static void Release(string strategyTag, string accountName = null) { AutomaticTradingNT8.Release(strategyTag, accountName); }

        public static string OrderTag(string strategyTag, int magic) { return AutomaticTradingNT8.OrderTag(strategyTag, magic); }
    }
}
