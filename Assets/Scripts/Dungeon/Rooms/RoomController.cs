using System;
using System.Collections.Generic;
using ProtocolExecution;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>Une una dirección del grid con los objetos que la materializan en el prefab de sala.</summary>
    [System.Serializable]
    public class RoomSideBinding
    {
        public RoomDirection direction;

        [Tooltip("Losa que tapa el hueco central cuando NO hay sala vecina. Se activa sola.")]
        public GameObject gate;

        [Tooltip("Panel de puerta. Solo existe donde hay conexión.")]
        public DoorController door;
    }

    /// <summary>
    /// Vive en cada sala instanciada (en el mismo GameObject que su collider
    /// trigger). Sección "Puertas de las salas":
    ///  - Bloquea las puertas al entrar un jugador en una sala con encuentro.
    ///  - Las desbloquea cuando la autoridad (el host) confirma que quedó superada.
    ///  - Las salas sin enemigos o ya superadas mantienen las puertas abiertas.
    ///  - Avisa por <see cref="PlayerEntered"/> cada vez que entra un jugador,
    ///    de modo que el host pueda poblar el encuentro en ese instante (y no
    ///    al empezar el nivel). El aviso es sincrono: cuando el host responde ya
    ///    ha registrado sus enemigos, asi que <see cref="Update"/> no confunde
    ///    una sala recien abierta con una sala superada.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomController : MonoBehaviour
    {
        /// <summary>Salas instanciadas del piso actual, por coordenada de grid.</summary>
        public static readonly Dictionary<Vector2Int, RoomController> Registry =
            new Dictionary<Vector2Int, RoomController>();

        [Header("Lados de la sala (los cablea el prefab)")]
        [SerializeField] private RoomSideBinding[] sides = new RoomSideBinding[4];

        [Header("Configuración")]
        [Tooltip("Marcar en salas sin enemigos o que ya empiezan superadas (inicial, tienda, pasillos).")]
        [SerializeField] private bool startsCleared;

        [Tooltip("Puntos que aporta al equipo superar esta sala.")]
        [SerializeField] private int clearPoints = 25;

        [Header("Visuales")]
        [Tooltip("Renderer del suelo. El builder le aplica el material de la máscara de conexión.")]
        [SerializeField] private Renderer floorRenderer;

        public RoomNode Node { get; private set; }
        public bool IsCleared { get; private set; }
        public int ClearPoints => clearPoints;
        public int AliveEnemyCount => _enemies.Count;

        /// <summary>Centro de la sala en el mundo (el suelo está a esta misma altura).</summary>
        public Vector3 Center => transform.position;

        /// <summary>
        /// Salas normales y de jefe siempre tienen encuentro; la inicial y las
        /// especiales/tienda no. Es una regla determinista, así que host y
        /// clientes llegan al mismo resultado sin replicarla.
        /// </summary>
        public bool HasEncounter => Node != null && Node.HasEncounter;

        /// <summary>
        /// Verdadero cuando la autoridad ya materializo los enemigos de esta
        /// sala. Lo marca <see cref="TryClaimEncounter"/>.
        /// </summary>
        public bool EncounterPopulated { get; private set; }

        /// <summary>
        /// Se dispara cada vez que un jugador entra en la sala (no solo la
        /// primera), para que el host decida si toca poblarla.
        /// </summary>
        public event Action<RoomController> PlayerEntered;

        private readonly List<EnemyMarker> _enemies = new List<EnemyMarker>();
        private int _playersInside;
        private bool _clearReported;

        public void Initialize(RoomNode node)
        {
            Node = node;

            Registry[node.gridPosition] = this;

            ApplyConnections(node);

            // 4. Persistencia: si el equipo ya la había superado, las puertas
            //    arrancan abiertas y no volverán a bloquearse.
            IsCleared = startsCleared || !HasEncounter || AuthoritySaysCleared(node.gridPosition);

            UnlockDoors();
            if (HasEncounter && IsCleared) MarkFloorCompleted();
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// Tiñe el piso de violeta en las salas con encuentro ya superadas, para
        /// saber cuales se completaron. Se usa un MaterialPropertyBlock para no
        /// modificar el material compartido por todas las salas.
        /// </summary>
        private void MarkFloorCompleted()
        {
            if (floorRenderer == null) return;

            var block = new MaterialPropertyBlock();
            floorRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, new Color(0.30f, 0.14f, 0.45f));
            floorRenderer.SetPropertyBlock(block);
        }

        private void OnDestroy()
        {
            if (Node == null) return;

            if (Registry.TryGetValue(Node.gridPosition, out RoomController current) && current == this)
                Registry.Remove(Node.gridPosition);
        }

        /// <summary>
        /// "De mapa a mazmorra": por cada lado, si hay sala vecina se abre la
        /// puerta y se quita la losa; si no la hay, la losa cierra el hueco y no
        /// existe puerta. Las paredes laterales siempre están activas.
        /// </summary>
        private void ApplyConnections(RoomNode node)
        {
            if (sides == null) return;

            foreach (RoomSideBinding side in sides)
            {
                if (side == null) continue;

                bool connected = node.IsConnected(side.direction);

                if (side.gate != null) side.gate.SetActive(!connected);
                if (side.door != null) side.door.gameObject.SetActive(connected);
            }
        }

        private static bool AuthoritySaysCleared(Vector2Int coord)
        {
            RunController run = RunController.Instance;
            if (run == null || !run.IsSpawned) return false;
            return run.ClearedRooms.Contains(RunController.Encode(coord));
        }

        public void SetFloorMaterial(Material material)
        {
            if (floorRenderer != null && material != null) floorRenderer.sharedMaterial = material;
        }

        // ---------------- Enemigos ----------------

        /// <summary>
        /// Reserva el poblado de esta sala. Devuelve true solo la primera vez
        /// que alguien lo consigue, aunque entren varios jugadores a la vez o
        /// el host lo dispare tambien tras un spawn repartido.
        /// </summary>
        public bool TryClaimEncounter()
        {
            if (EncounterPopulated || !HasEncounter) return false;

            EncounterPopulated = true;
            return true;
        }

        public void RegisterEnemy(EnemyMarker enemy)
        {
            if (enemy == null || _enemies.Contains(enemy)) return;

            _enemies.Add(enemy);
            enemy.OnDeath += HandleEnemyDeath;
        }

        private void HandleEnemyDeath(EnemyMarker enemy)
        {
            _enemies.Remove(enemy);
            enemy.OnDeath -= HandleEnemyDeath;

            TryReportClear();
        }

        /// <summary>
        /// Red de seguridad: si un enemigo se destruye sin pasar por Die() (por
        /// ejemplo al cerrar una conexión), la sala no debe quedar encerrada
        /// para siempre.
        /// </summary>
        private void Update()
        {
            if (IsCleared || !HasEncounter) return;
            if (_playersInside == 0 || _enemies.Count > 0) return;

            TryReportClear();
        }

        private void TryReportClear()
        {
            // 2. Condición de desbloqueo: no quedan enemigos. Solo el host puede
            //    declarar la sala superada, así que fuera del host no hace nada.
            // Una sala vacia porque todavia no se poblo (p. ej. la de jefe esperando
            // al grupo) no esta superada.
            if (_enemies.Count > 0 || !EncounterPopulated) return;

            RunController run = RunController.Instance;
            if (run == null || !run.IsServer) return;

            if (_clearReported) return;
            _clearReported = true;

            run.ServerAddPoints(clearPoints);
            run.ServerMarkRoomCleared(Node.gridPosition);
        }

        /// <summary>[RED] El host confirmó (via RunController.ClearedRooms) que el equipo superó esta sala.</summary>
        public void SetClearedFromNetwork()
        {
            IsCleared = true;
            UnlockDoors();
            MarkFloorCompleted();
        }

        // ---------------- Ocupación (1-3 jugadores) ----------------

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag("Player")) return;

            _playersInside++;

            // Aviso al host para que pueble el encuentro ahora que hay alguien
            // dentro. Se invoca antes de bloquear las puertas y de forma
            // sincrona: los enemigos quedan registrados antes de cualquier
            // Update(), que si no daria la sala por superada sin combatir.
            PlayerEntered?.Invoke(this);

            // 1. Bloqueo: basta con que entre un jugador para encerrar al grupo.
            if (!IsCleared && HasEncounter) LockDoors();
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag("Player")) return;

            _playersInside = Mathf.Max(0, _playersInside - 1);
        }

        // ---------------- Puertas ----------------

        public void LockDoors()
        {
            SetDoorsLocked(true);
        }

        public void UnlockDoors()
        {
            SetDoorsLocked(false);
        }

        private void SetDoorsLocked(bool locked)
        {
            if (sides == null) return;

            foreach (RoomSideBinding side in sides)
            {
                if (side?.door == null) continue;
                if (!side.door.gameObject.activeSelf) continue;

                side.door.SetLocked(locked);
            }
        }
    }
}
