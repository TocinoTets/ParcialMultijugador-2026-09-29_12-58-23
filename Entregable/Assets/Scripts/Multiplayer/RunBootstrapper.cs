using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Orquesta una run sobre la mazmorra procedural que ya existia.
    ///
    /// Responsabilidades:
    ///  1. Cuando la partida pasa a <see cref="RunPhase.Playing"/>, generar el
    ///     piso con la semilla que decidio el host, de modo que host y clientes
    ///     construyan exactamente la misma mazmorra.
    ///  2. Repartir el spawn de los jugadores (§4.2: el host elige entre todos
    ///     en la misma sala o cada uno en una sala distinta del mismo piso).
    ///  3. Poblar cada sala con enemigos (solo el host) en el momento en que un
    ///     jugador entra en ella, y generar el jefe cuando todo el grupo se
    ///     reune en la sala de jefe (§4.2: "los jugadores deben reencontrarse
    ///     antes de poder abrir la puerta del jefe").
    /// </summary>
    public class RunBootstrapper : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private DungeonSystem.DungeonManager dungeonManager;
        [SerializeField] private DungeonSystem.RoomCameraFollow cameraFollow;
        [SerializeField] private GameObject enemyPrefab;
        [SerializeField] private GameObject bossPrefab;
        [SerializeField] private GameObject playerPrefabFallback;

        [Header("Balance de enemigos")]
        [SerializeField] private float enemyBaseHealth = 30f;
        [SerializeField] private float enemyBaseDamage = 9f;
        [SerializeField] private float enemyBaseSpeed = 3.4f;
        [SerializeField] private int enemyPoints = 15;
        [SerializeField] private int roomClearPoints = 25;

        [Header("Spawn")]
        [Tooltip("Altura a la que se apoya el jugador. El prefab tiene la capsula de 1.8 m, asi que 0.1 lo deja sobre el suelo.")]
        [SerializeField] private float playerSpawnHeight = 0.1f;

        private readonly Dictionary<Vector2Int, DungeonSystem.RoomController> _rooms =
            new Dictionary<Vector2Int, DungeonSystem.RoomController>();

        private Vector2Int _bossRoomCoord;
        private bool _bossRoomKnown;
        private bool _bossSpawnedThisRun;
        private bool _geometryBuilt;
        private bool _distributingSpawns;
        private bool _bossDoorsLocked;

        private RunController Run => RunController.Instance;

        private void Awake()
        {
            if (dungeonManager == null) dungeonManager = FindFirstObjectByType<DungeonSystem.DungeonManager>();
            if (cameraFollow == null) cameraFollow = FindFirstObjectByType<DungeonSystem.RoomCameraFollow>();
        }

        private void Update()
        {
            RunController run = Run;

            // Cerrar la sesion destruye el RunController (su Instance pasa a null
            // sin pasar por el OnDestroy de este script), asi que hay que olvidar
            // la suscripcion y el estado de la run para no arrastrarlos a la
            // partida siguiente.
            if (run == null)
            {
                ResetRunState();
                return;
            }

            // El RunController aparece de forma asincrona (lo spawnea el host) y
            // es una instancia nueva en cada sesion, asi que la suscripcion se
            // rastrea por instancia y no con un booleano: con un booleano la
            // segunda partida no se suscribiria y el piso no se regeneraria.
            if (run != _subscribedRun && run.IsSpawned)
            {
                _subscribedRun = run;
                run.PhaseChanged += HandlePhaseChanged;
                run.RoomCleared += HandleRoomCleared;
                HandlePhaseChanged(run.CurrentPhase);
            }

            if (run == _subscribedRun && run.IsServer && run.CurrentPhase == RunPhase.Playing)
            {
                UpdateBossGating();
            }

            // [RED] GDD 4.2: la sala del jefe no encierra al primero que entra; se cierra en
            // todos los peers recien cuando aparece el jefe (BossSpawned), con el grupo ya reunido.
            if (run == _subscribedRun && run.CurrentPhase == RunPhase.Playing && run.BossSpawned.Value
                && !_bossDoorsLocked && _bossRoomKnown
                && _rooms.TryGetValue(_bossRoomCoord, out DungeonSystem.RoomController bossRoom) && !bossRoom.IsCleared)
            {
                bossRoom.LockDoors();
                _bossDoorsLocked = true;
            }
        }

        /// <summary>Instancia de RunController a la que se esta suscrito, o null.</summary>
        private RunController _subscribedRun;

        private void OnDestroy()
        {
            if (_subscribedRun == null) return;

            _subscribedRun.PhaseChanged -= HandlePhaseChanged;
            _subscribedRun.RoomCleared -= HandleRoomCleared;
            _subscribedRun = null;
        }

        /// <summary>
        /// Descarta todo lo que dependia de la sesion que acaba de cerrarse, de
        /// modo que la partida siguiente genere el piso desde cero.
        /// </summary>
        private void ResetRunState()
        {
            _subscribedRun = null;
            _rooms.Clear();
            _bossRoomKnown = false;
            _bossSpawnedThisRun = false;
            _bossDoorsLocked = false;
            _geometryBuilt = false;
        }

        // ---------------- Fases ----------------

        private void HandlePhaseChanged(RunPhase phase)
        {
            RunController run = Run;
            if (run == null) return;

            if (phase == RunPhase.Playing)
            {
                _bossSpawnedThisRun = false;
                _bossDoorsLocked = false;

                BuildFloorGeometry(run.Seed.Value);

                if (run.IsServer)
                {
                    // Se engancha el poblado antes de teletransportar: con el
                    // spawn repartido el jugador aparece ya dentro de la sala y
                    // TeleportPlayers avisa a mano al terminar.
                    HookRoomEncounters();
                    TeleportPlayers(run);
                }
            }
            else if (phase == RunPhase.Lobby)
            {
                _bossSpawnedThisRun = false;
            }
        }

        /// <summary>
        /// Genera la geometria a partir de la semilla replicada. Todos los peers
        /// ejecutan exactamente la misma secuencia de numeros aleatorios, por lo
        /// que obtienen el mismo piso.
        /// </summary>
        private void BuildFloorGeometry(int seed)
        {
            if (dungeonManager == null)
            {
                Debug.LogError("[RunBootstrapper] Falta la referencia a DungeonManager.");
                return;
            }

            // [RED] Misma semilla => misma mazmorra en host y clientes; no se replica la geometria.
            dungeonManager.GenerateFloor(seed);

            // La slice termina al derrotar al jefe, asi que se anula el
            // comportamiento del prefab de sala que teletransportaba a un piso
            // nuevo al cruzar la sala. El flujo de la run lo controla este script.
            foreach (var bossRoom in FindObjectsByType<DungeonSystem.BossRoomController>(FindObjectsSortMode.None))
            {
                bossRoom.enabled = false;
            }

            CacheRooms();
            _geometryBuilt = true;
        }

        private void CacheRooms()
        {
            _rooms.Clear();
            _bossRoomKnown = false;

            if (dungeonManager.Rooms == null) return;

            foreach (var pair in dungeonManager.Rooms)
            {
                DungeonSystem.RoomNode node = pair.Value;
                if (node.sceneInstance == null) continue;

                var controller = node.sceneInstance.GetComponent<DungeonSystem.RoomController>();
                if (controller == null) continue;

                // El DungeonBuilder ya coloca cada sala en el mundo; aqui solo se
                // indexa por coordenada para saber donde spawnear y cual es el jefe.
                _rooms[node.gridPosition] = controller;

                if (node.type == DungeonSystem.RoomType.Boss)
                {
                    _bossRoomCoord = node.gridPosition;
                    _bossRoomKnown = true;
                }
            }
        }

        // ---------------- Poblado de salas ----------------

        /// <summary>
        /// Enlaza cada sala con encuentro para poblar sus enemigos cuando entre
        /// el primer jugador, en lugar de hacerlo al empezar el nivel.
        /// </summary>
        private void HookRoomEncounters()
        {
            foreach (var pair in _rooms)
            {
                DungeonSystem.RoomController room = pair.Value;
                if (room == null || !room.HasEncounter) continue;

                room.PlayerEntered -= HandleRoomPlayerEntered;
                room.PlayerEntered += HandleRoomPlayerEntered;
            }
        }

        /// <summary>
        /// Se dispara cada vez que un jugador entra en una sala. El poblado real
        /// ocurre solo la primera vez, porque la sala lo reclama.
        /// </summary>
        private void HandleRoomPlayerEntered(DungeonSystem.RoomController room)
        {
            // [RED] Solo el host puebla salas y spawnea enemigos (NetworkObjects).
            RunController run = Run;
            if (run == null || !run.IsServer || run.CurrentPhase != RunPhase.Playing) return;
            if (room == null || room.Node == null || !room.HasEncounter) return;
            if (room.IsCleared) return;

            // La sala de jefe no se cierra al entrar ni trae a nadie: la puebla
            // UpdateBossGating cuando todo el grupo llego caminando (GDD 4.2).
            if (room.Node.type == DungeonSystem.RoomType.Boss) return;

            // [RED] Spawn en la misma sala: una sala normal se cierra en cuanto entra
            // alguien, asi que el resto del grupo se trae dentro. Con spawn separado
            // no: el GDD 4.2 quiere exploracion en paralelo, cada uno en su sala.
            if (!_distributingSpawns && !run.SeparateSpawns.Value) GatherPlayersInto(room);

            if (!room.TryClaimEncounter()) return;

            SpawnRoomEncounter(room, run);
        }

        /// <summary>
        /// Teletransporta a la sala a los jugadores que siguen fuera, junto al
        /// que acaba de entrar y un poco hacia el centro para no quedar en la puerta.
        /// </summary>
        private void GatherPlayersInto(DungeonSystem.RoomController room)
        {
            float roomSize = dungeonManager != null ? dungeonManager.RoomSize : 20f;
            Vector3 center = new Vector3(room.Node.gridPosition.x * roomSize, 0f, room.Node.gridPosition.y * roomSize);
            float halfExtent = roomSize * 0.5f;

            var outside = new List<NetPlayer>();
            NetPlayer anchor = null;

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null) continue;

                Vector3 offset = player.transform.position - center;
                bool inside = Mathf.Abs(offset.x) <= halfExtent && Mathf.Abs(offset.z) <= halfExtent;

                if (!inside) outside.Add(player);
                else if (anchor == null) anchor = player;
            }

            if (outside.Count == 0) return;

            Vector3 entry = anchor != null ? anchor.transform.position : center;
            entry.y = 0f;

            Vector3 inward = center - entry;
            inward = inward.sqrMagnitude > 0.01f ? inward.normalized : Vector3.forward;
            Vector3 side = new Vector3(inward.z, 0f, -inward.x);

            for (int i = 0; i < outside.Count; i++)
            {
                // Alterna a izquierda y derecha del jugador que entro.
                float lateral = (i % 2 == 0 ? 1f : -1f) * (1.4f + (i / 2) * 1.4f);
                Vector3 position = entry + inward * 1.8f + side * lateral + Vector3.up * playerSpawnHeight;
                float yaw = Mathf.Atan2(inward.x, inward.z) * Mathf.Rad2Deg;

                outside[i].ServerTeleport(position, yaw);
            }
        }

        private void SpawnRoomEncounter(DungeonSystem.RoomController room, RunController run)
        {
            if (enemyPrefab == null)
            {
                Debug.LogError("[RunBootstrapper] Falta 'enemyPrefab'.");
                return;
            }

            float roomSize = dungeonManager != null ? dungeonManager.RoomSize : 20f;
            int perRoom = run.ServerEnemiesForRoom();

            DifficultyDef difficulty = run.Difficulty;

            // Semilla derivada de la coordenada: el reparto es el mismo dentro
            // de la misma sala sea cual sea el momento en que se pueble, asi
            // que diferirlo no cambia la dificultad efectiva.
            var rng = new System.Random(RunController.Encode(room.Node.gridPosition) * 31 + run.Seed.Value);
            Vector3 center = new Vector3(room.Node.gridPosition.x * roomSize, 0f, room.Node.gridPosition.y * roomSize);

            for (int i = 0; i < perRoom; i++)
            {
                Vector3 offset = new Vector3(
                    (float)(rng.NextDouble() * 2.0 - 1.0) * roomSize * 0.32f,
                    0f,
                    (float)(rng.NextDouble() * 2.0 - 1.0) * roomSize * 0.32f);

                SpawnEnemy(room, center + offset + Vector3.up * 0.1f, difficulty, rng);
            }
        }

        private void SpawnEnemy(DungeonSystem.RoomController room, Vector3 position,
            DifficultyDef difficulty, System.Random rng)
        {
            GameObject instance = Instantiate(enemyPrefab, position, Quaternion.identity);
            var networkObject = instance.GetComponent<NetworkObject>();
            var enemy = instance.GetComponent<NetEnemy>();

            if (networkObject == null || enemy == null)
            {
                Debug.LogError("[RunBootstrapper] El enemyPrefab necesita NetworkObject + NetEnemy.");
                Destroy(instance);
                return;
            }

            float health = enemyBaseHealth * difficulty.enemyHealthMul;
            float damage = enemyBaseDamage * difficulty.enemyDamageMul;
            float speed = enemyBaseSpeed * difficulty.enemySpeedMul * (0.9f + (float)rng.NextDouble() * 0.25f);

            enemy.ServerInitialize(health, damage, speed, enemyPoints);
            networkObject.Spawn(true);

            // Registro explicito en la sala: es lo que permite a RoomController
            // saber cuantos enemigos quedan y cuando abrir las puertas.
            var marker = instance.GetComponent<DungeonSystem.EnemyMarker>();
            if (marker != null) room.RegisterEnemy(marker);
        }

        // ---------------- Spawn de jugadores (§4.2) ----------------

        private void TeleportPlayers(RunController run)
        {
            if (!run.IsServer) return;

            float roomSize = dungeonManager != null ? dungeonManager.RoomSize : 20f;

            List<Vector2Int> spawnCoords = BuildSpawnCoords(run, roomSize);

            int index = 0;
            var occupied = new HashSet<Vector2Int>();

            foreach (var clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                NetPlayer player = FindPlayer(clientId);
                if (player == null) continue;

                player.ServerResetForRun();

                Vector2Int coord = spawnCoords[Mathf.Min(index, spawnCoords.Count - 1)];
                Vector3 center = new Vector3(coord.x * roomSize, 0f, coord.y * roomSize);

                // Reparto alrededor del centro para que no se solapen.
                float angle = (index / (float)GameData.MaxPlayers) * Mathf.PI * 2f;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 3f;

                player.ServerTeleport(center + offset + Vector3.up * playerSpawnHeight, angle * Mathf.Rad2Deg);
                occupied.Add(coord);
                index++;
            }

            // Con el spawn repartido el jugador nace ya dentro de su sala, asi
            // que no hay transicion de trigger que detectar: el poblado se
            // dispara a mano aqui. La sala solo lo acepta una vez.
            _distributingSpawns = true;
            try
            {
                foreach (Vector2Int coord in occupied)
                {
                    if (_rooms.TryGetValue(coord, out DungeonSystem.RoomController room))
                        HandleRoomPlayerEntered(room);
                }
            }
            finally
            {
                _distributingSpawns = false;
            }
        }

        /// <summary>
        /// §4.2: el host decide si el grupo aparece junto o repartido por el piso.
        /// Repartidos, el grupo debe reencontrarse antes de poder enfrentar al jefe.
        /// </summary>
        private List<Vector2Int> BuildSpawnCoords(RunController run, float roomSize)
        {
            var coords = new List<Vector2Int>();

            if (!run.SeparateSpawns.Value)
            {
                coords.Add(Vector2Int.zero);
                return coords;
            }

            foreach (var pair in _rooms)
            {
                if (pair.Value.Node.type == DungeonSystem.RoomType.Start) continue;
                if (pair.Value.Node.type == DungeonSystem.RoomType.Boss) continue;
                coords.Add(pair.Key);
            }

            if (coords.Count == 0) coords.Add(Vector2Int.zero);
            return coords;
        }

        // ---------------- Jefe ----------------

        /// <summary>
        /// El jefe aparece cuando todos los jugadores vivos estan dentro de la
        /// sala de jefe. Es la traduccion directa de §4.2: el grupo debe
        /// reencontrarse antes de habilitar el enfrentamiento final.
        /// </summary>
        private void UpdateBossGating()
        {
            // [RED] Corre solo en el host: el jefe aparece cuando todo el grupo vivo esta en su sala.
            if (_bossSpawnedThisRun || !_geometryBuilt || !_bossRoomKnown) return;
            if (bossPrefab == null) return;
            if (NetPlayer.All.Count == 0) return;

            float roomSize = dungeonManager != null ? dungeonManager.RoomSize : 20f;
            Vector3 bossCenter = new Vector3(_bossRoomCoord.x * roomSize, 0f, _bossRoomCoord.y * roomSize);
            float radius = roomSize * 0.45f;

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null || !player.IsAlive) continue;

                Vector3 offset = player.transform.position - bossCenter;
                offset.y = 0f;

                if (offset.magnitude > radius) return;
            }

            SpawnBoss(bossCenter);
        }

        private void SpawnBoss(Vector3 center)
        {
            GameObject instance = Instantiate(bossPrefab, center + Vector3.up * 0.1f, Quaternion.identity);
            var networkObject = instance.GetComponent<NetworkObject>();

            if (networkObject == null)
            {
                Debug.LogError("[RunBootstrapper] El bossPrefab necesita NetworkObject.");
                Destroy(instance);
                return;
            }

            _bossSpawnedThisRun = true;
            networkObject.Spawn(true);

            // El jefe cuenta como enemigo de su sala: sin esto la sala se daba por
            // superada al entrar (0 enemigos) y abria las puertas antes de pelear.
            if (_rooms.TryGetValue(_bossRoomCoord, out DungeonSystem.RoomController room))
            {
                room.TryClaimEncounter();
                room.RegisterEnemy(instance.AddComponent<DungeonSystem.EnemyMarker>());
            }
        }

        // ---------------- Salas superadas ----------------

        private void HandleRoomCleared(Vector2Int coord)
        {
            if (_rooms.TryGetValue(coord, out DungeonSystem.RoomController room))
            {
                room.SetClearedFromNetwork();
            }
        }

        // ---------------- Utilidades ----------------

        private static NetPlayer FindPlayer(ulong clientId)
        {
            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player != null && player.OwnerClientId == clientId) return player;
            }
            return null;
        }

        /// <summary>Enlaza la camara local al jugador propio una vez existe.</summary>
        private void LateUpdate()
        {
            if (cameraFollow == null) return;
            if (cameraFollow.HasPlayer) return;

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player != null && player.IsOwner)
                {
                    cameraFollow.SetPlayer(player.transform);
                    break;
                }
            }
        }

        public void ServerAwardRoomClear()
        {
            RunController run = Run;
            if (run != null && run.IsServer) run.ServerAddPoints(roomClearPoints);
        }
    }
}
