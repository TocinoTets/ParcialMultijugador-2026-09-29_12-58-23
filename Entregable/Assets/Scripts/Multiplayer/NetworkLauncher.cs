using System;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Punto de entrada de red para la topologia Cliente/Host (§4.4).
    ///
    /// INDICE DEL MULTIJUGADOR (buscar "[RED]" con Ctrl+F para ir a cada punto):
    ///  - Crear / unirse / aprobar conexiones ........ NetworkLauncher (este archivo)
    ///  - Infraestructura: Authentication + Relay +
    ///    Lobby (sala con codigo) .................... NetworkLauncher (HostOnlineAsync / JoinOnlineAsync)
    ///  - Estado compartido de la partida y semilla .. RunController
    ///  - Movimiento: cada jugador manda su posicion
    ///    directo, sin chequeo de paquetes (menos lag) NetPlayer (UpdateOwnerInput)
    ///  - Disparo y dano validados en el host ........ NetPlayer (RequestFireServerRpc), NetProjectile
    ///  - IA de enemigos y jefe solo en el host;
    ///    los clientes interpolan .................. NetEnemy, NetBoss
    ///  - Misma mazmorra en todos, poblado de salas,
    ///    reunir al grupo en una sala y jefe ......... RunBootstrapper
    ///  - Salas superadas (puertas y piso violeta) ... RoomController + RunController.ClearedRooms
    ///
    /// Un jugador arranca como host (servidor + cliente) y los demas se
    /// conectan a el. La autoridad del estado compartido (mazmorra, enemigos,
    /// jefe, vida y puntos) vive en el host.
    ///
    /// Dos formas de conectarse:
    ///  - Online (Unity Gaming Services, paquete Multiplayer Services): el host
    ///    crea una sesion con Relay y los demas entran con el codigo de sala.
    ///    Relay hace de puente, asi que nadie abre puertos ni comparte su IP.
    ///  - LAN: IP directa al puerto 7777, sin servicios (para jugar sin internet).
    ///
    /// Que cubre esta slice de §4.3:
    ///  - Limite duro de 3 jugadores por partida.
    ///  - Ready-check antes de iniciar (ver LobbyPanel / NetPlayer.IsReady).
    ///  - Verificacion de version de cliente antes de admitir la conexion (§7).
    ///
    /// Que NO cubre todavia (y por que):
    ///  - Lobbies publicos con buscador: hoy solo se entra por codigo.
    ///  - Reputacion, reportes y silenciado: necesitan el backend centralizado.
    ///  - Host migration: NGO no lo soporta de forma nativa; es una feature
    ///    propia y no esta implementada.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkLauncher : MonoBehaviour
    {
        public static NetworkLauncher Instance { get; private set; }

        [Header("Referencias")]
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private UnityTransport transport;

        [Tooltip("Prefab con el NetworkObject de RunController. Lo spawnea el host al iniciar sesion.")]
        [SerializeField] private GameObject gameFlowPrefab;

        [Header("Configuracion")]
        [SerializeField] private ushort port = 7777;
        [SerializeField] private string defaultAddress = "127.0.0.1";

        public ushort Port => port;
        public string DefaultAddress => defaultAddress;

        public bool IsRunning => networkManager != null && networkManager.IsListening;
        public bool IsHost => networkManager != null && networkManager.IsHost;
        public bool IsClientOnly => networkManager != null && networkManager.IsClient && !networkManager.IsServer;

        public int PlayerCount => networkManager != null && networkManager.IsServer
            ? networkManager.ConnectedClientsIds.Count
            : 0;

        /// <summary>Ultimo motivo de rechazo informado por el host, para mostrarlo en UI.</summary>
        public static string LastRejectReason { get; private set; } = string.Empty;

        /// <summary>Progreso o error de la conexion online, para mostrarlo en el menu.</summary>
        public static string StatusMessage { get; private set; } = string.Empty;

        /// <summary>True mientras se crea o busca una sala online (las llamadas tardan).</summary>
        public bool IsBusy { get; private set; }

        private ISession _session;

        /// <summary>Codigo que el host comparte para que los demas entren. Vacio en LAN.</summary>
        public string SessionCode => _session?.Code ?? string.Empty;

        public event Action SessionChanged;

        private RunController _gameFlow;
        public RunController GameFlow => _gameFlow;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (networkManager == null) networkManager = NetworkManager.Singleton;
            if (networkManager == null) networkManager = FindFirstObjectByType<NetworkManager>();
            if (transport == null && networkManager != null) transport = networkManager.GetComponent<UnityTransport>();

            if (networkManager != null)
            {
                // §7: todos los jugadores deben compartir la misma version de netcode.
                networkManager.NetworkConfig.ConnectionApproval = true;
                networkManager.ConnectionApprovalCallback = ApprovalCheck;
                networkManager.OnClientConnectedCallback += HandleClientConnected;
                networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
                networkManager.OnServerStarted += HandleServerStarted;
            }
        }

        private void OnDestroy()
        {
            if (networkManager == null) return;

            networkManager.ConnectionApprovalCallback = null;
            networkManager.OnClientConnectedCallback -= HandleClientConnected;
            networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
            networkManager.OnServerStarted -= HandleServerStarted;

            if (Instance == this) Instance = null;
        }

        // ---------------- Aprobacion de conexion ----------------

        /// <summary>
        /// §4.3 / §7: se rechaza por sala llena o por version de cliente distinta.
        /// El payload de union lleva la version del cliente en UTF8.
        /// </summary>
        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            // [RED] El host decide quien entra: version, partida en curso y cupo de 3.
            string clientVersion = string.Empty;
            if (request.Payload != null && request.Payload.Length > 0)
            {
                try { clientVersion = Encoding.UTF8.GetString(request.Payload); }
                catch (Exception) { clientVersion = string.Empty; }
            }

            if (clientVersion != Application.version)
            {
                response.Approved = false;
                response.Reason = $"Version incompatible (host {Application.version}, cliente {clientVersion}).";
                LastRejectReason = response.Reason;
                return;
            }

            // Un jugador que entra a mitad de la run no recibe vida ni spawn:
            // solo se admiten uniones mientras el grupo esta en el lobby.
            RunController run = RunController.Instance;
            if (run != null && run.IsSpawned && run.CurrentPhase != RunPhase.Lobby)
            {
                response.Approved = false;
                response.Reason = "Partida en curso. Espera a que el grupo vuelva a la sala de espera.";
                LastRejectReason = response.Reason;
                return;
            }

            if (networkManager.ConnectedClientsIds.Count >= GameData.MaxPlayers)
            {
                response.Approved = false;
                response.Reason = $"Sala llena (maximo {GameData.MaxPlayers} jugadores).";
                LastRejectReason = response.Reason;
                return;
            }

            response.Approved = true;
            response.CreatePlayerObject = true;
            LastRejectReason = string.Empty;
        }

        // ---------------- Arranque / union ----------------

        // ---------------- Online: Unity Gaming Services ----------------

        /// <summary>
        /// [RED] Infraestructura: inicia Unity Services y entra con una cuenta
        /// anonima (Authentication). Relay y Lobby exigen un jugador identificado.
        /// </summary>
        private static async Task EnsureSignedInAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                // shortcut: perfil al azar para poder abrir varias instancias en la misma PC
                // (cada una es otro jugador anonimo); un juego real guardaria el perfil.
                var options = new InitializationOptions().SetProfile("p" + UnityEngine.Random.Range(0, 1000000));
                await UnityServices.InitializeAsync(options);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        /// <summary>
        /// [RED] Crea la sala online: una sesion de Multiplayer Services con Relay.
        /// El paquete reserva el Relay, crea el Lobby con su codigo y arranca
        /// Netcode como host; la aprobacion de conexion de este archivo sigue valiendo.
        /// </summary>
        public async Task<bool> HostOnlineAsync()
        {
            if (networkManager == null || IsBusy || IsRunning) return false;

            IsBusy = true;
            StatusMessage = "Creando sala online...";
            try
            {
                await EnsureSignedInAsync();
                networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version);

                // Privada: solo se entra con el codigo. El cupo coincide con el de ApprovalCheck.
                var options = new SessionOptions { MaxPlayers = GameData.MaxPlayers, IsPrivate = true }
                    .WithRelayNetwork();

                _session = await MultiplayerService.Instance.CreateSessionAsync(options);
                StatusMessage = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                StatusMessage = $"No se pudo crear la sala online: {e.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
                SessionChanged?.Invoke();
            }
        }

        /// <summary>[RED] Entra a una sala online con el codigo que paso el host (Lobby + Relay).</summary>
        public async Task<bool> JoinOnlineAsync(string code)
        {
            if (networkManager == null || IsBusy || IsRunning) return false;

            IsBusy = true;
            StatusMessage = "Buscando la sala...";
            LastRejectReason = string.Empty;
            try
            {
                await EnsureSignedInAsync();
                networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version);

                _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant());
                StatusMessage = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                StatusMessage = $"No se pudo entrar a la sala: {e.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
                SessionChanged?.Invoke();
            }
        }

        // ---------------- LAN: IP directa ----------------

        /// <summary>[RED] Arranca como host (servidor + jugador) en el puerto configurado.</summary>
        public bool Host()
        {
            if (networkManager == null) return false;

            transport.SetConnectionData(defaultAddress, port, "0.0.0.0");
            networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version);

            bool started = networkManager.StartHost();
            if (started && _gameFlow == null) SpawnGameFlow();
            SessionChanged?.Invoke();
            return started;
        }

        /// <summary>[RED] Se conecta como cliente a la IP del host (LAN, sin Relay).</summary>
        public bool Join(string address)
        {
            if (networkManager == null) return false;

            string target = string.IsNullOrWhiteSpace(address) ? defaultAddress : address.Trim();
            transport.SetConnectionData(target, port);
            networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version);

            bool started = networkManager.StartClient();
            SessionChanged?.Invoke();
            return started;
        }

        public void Shutdown()
        {
            if (networkManager == null) return;

            // Salir de la sesion online libera el Lobby y el Relay (si el host sale, la sala se cierra).
            if (_session != null)
            {
                _ = _session.LeaveAsync();
                _session = null;
            }

            networkManager.Shutdown();

            if (_gameFlow != null)
            {
                Destroy(_gameFlow.gameObject);
                _gameFlow = null;
            }

            SessionChanged?.Invoke();
        }

        private void SpawnGameFlow()
        {
            if (gameFlowPrefab == null)
            {
                Debug.LogError("[NetworkLauncher] Falta 'gameFlowPrefab'. La partida no puede autorizar su estado.");
                return;
            }

            GameObject instance = Instantiate(gameFlowPrefab);
            var netObj = instance.GetComponent<NetworkObject>();

            if (netObj == null)
            {
                Debug.LogError("[NetworkLauncher] El gameFlowPrefab no tiene NetworkObject.");
                Destroy(instance);
                return;
            }

            netObj.Spawn();
            _gameFlow = instance.GetComponent<RunController>();
        }

        private void HandleServerStarted()
        {
            if (_gameFlow == null) SpawnGameFlow();
        }

        private void HandleClientConnected(ulong clientId)
        {
            SessionChanged?.Invoke();
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            // El motivo del rechazo lo decide el host; el cliente lo recibe aqui.
            if (networkManager != null && !networkManager.IsServer
                && !string.IsNullOrEmpty(networkManager.DisconnectReason))
            {
                LastRejectReason = networkManager.DisconnectReason;
            }

            // Del lado cliente: se cayo el host (no hay host migration), el host nos
            // expulso o rechazo la conexion. En los tres casos se cierra la sesion,
            // asi tambien se sale del Lobby y se vuelve al menu con el motivo.
            if (networkManager != null && !networkManager.IsServer
                && (clientId == NetworkManager.ServerClientId || clientId == networkManager.LocalClientId
                    || !networkManager.IsListening))
            {
                Shutdown();
            }

            SessionChanged?.Invoke();
        }

        /// <summary>
        /// [RED] GDD 4.3: el host expulsa a un jugador. NGO lo desconecta con un motivo
        /// que el expulsado ve en el menu; al cerrar su sesion tambien sale del Lobby.
        /// </summary>
        public void Kick(ulong clientId)
        {
            if (networkManager == null || !networkManager.IsServer) return;
            if (clientId == NetworkManager.ServerClientId) return;

            networkManager.DisconnectClient(clientId, "El host te expulso de la partida.");
        }
    }
}
