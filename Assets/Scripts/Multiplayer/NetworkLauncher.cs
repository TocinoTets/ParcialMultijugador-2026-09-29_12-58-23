using System;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Punto de entrada de red para la topologia Cliente/Host (§4.4).
    ///
    /// INDICE DEL MULTIJUGADOR (buscar "[RED]" con Ctrl+F para ir a cada punto):
    ///  - Crear / unirse / aprobar conexiones ........ NetworkLauncher (este archivo)
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
    /// conectan a su direccion. La autoridad del estado compartido
    /// (mazmorra, enemigos, jefe, vida y puntos) vive en el host.
    ///
    /// Que cubre esta slice de §4.3:
    ///  - Limite duro de 3 jugadores por partida.
    ///  - Ready-check antes de iniciar (ver LobbyPanel / NetPlayer.IsReady).
    ///  - Verificacion de version de cliente antes de admitir la conexion (§7).
    ///
    /// Que NO cubre todavia (y por que):
    ///  - Codigo de sala / lobbies publicos: requiere el servicio Relay + Lobby
    ///    (Unity Gaming Services). Hoy la union es por IP directa en LAN.
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

            // Si se cae el host, los clientes no tienen a quien pedir autoridad:
            // se cierra la sesion en vez de dejar la partida en un estado invalido.
            if (networkManager != null && networkManager.IsClient && !networkManager.IsServer
                && clientId == NetworkManager.ServerClientId)
            {
                Debug.LogWarning("[NetworkLauncher] El host se desconecto. No hay host migration implementada todavia; se cierra la sesion.");
                Shutdown();
            }

            SessionChanged?.Invoke();
        }
    }
}
