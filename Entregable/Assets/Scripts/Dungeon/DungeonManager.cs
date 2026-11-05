using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Punto de entrada del sistema de mazmorra. Junta las tres fases:
    /// grid (DungeonGrid) -> escena (DungeonBuilder) -> presentación (cámara y minimapa).
    ///
    /// La generación NO ocurre en Start(): la dispara la partida cuando el host
    /// decide que la run empieza, y con la semilla que el host replica. Misma
    /// semilla = mismo piso en todas las máquinas.
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonManager : MonoBehaviour
    {
        public static DungeonManager Instance { get; private set; }

        [Header("Generación")]
        [Tooltip("Número total de salas del piso, incluida la inicial.")]
        [SerializeField] private int roomCount = 16;

        [Tooltip("Salas de una sola conexión: la más lejana será el jefe y el resto tiendas.")]
        [SerializeField] private int specialRoomCount = 2;

        [Tooltip("Genera un piso local al arrancar la escena. En multijugador debe quedar desactivado: el piso lo decide el host.")]
        [SerializeField] private bool generateOnStart;

        [Tooltip("Semilla usada solo cuando 'generateOnStart' está activo, para pruebas en el editor.")]
        [SerializeField] private int debugSeed = 12345;

        [Header("Referencias")]
        [SerializeField] private DungeonBuilder builder;
        [SerializeField] private MinimapController minimap;
        [SerializeField] private RoomCameraFollow cameraFollow;

        /// <summary>Se dispara al terminar de construir un piso.</summary>
        public event Action<IReadOnlyDictionary<Vector2Int, RoomNode>> FloorBuilt;

        private DungeonGrid _grid;
        private Dictionary<Vector2Int, RoomNode> _rooms = new Dictionary<Vector2Int, RoomNode>();
        private Vector2Int _lastKnownRoomCoord;

        public IReadOnlyDictionary<Vector2Int, RoomNode> Rooms => _rooms;
        public IReadOnlyList<RoomNode> RoomList => _grid?.RoomList;

        public float RoomSize => builder != null ? builder.RoomSize : 20f;

        /// <summary>
        /// Altura de la superficie del suelo de cualquier sala. El prefab de sala
        /// se construye con el suelo a esta altura, así que el spawn de
        /// jugadores y enemigos se calcula sumando la mitad de su collider.
        /// </summary>
        public float FloorSurfaceY => 0f;

        public int LastSeed { get; private set; }
        public DungeonBuilder Builder => builder;
        public RoomCameraFollow CameraFollow => cameraFollow;
        public MinimapController Minimap => minimap;

        private void Awake()
        {
            Instance = this;
            _grid = new DungeonGrid();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Start()
        {
            if (generateOnStart) GenerateFloor(debugSeed);
        }

        private void Update()
        {
            if (cameraFollow == null || _rooms.Count == 0) return;

            Vector2Int coord = cameraFollow.GetCurrentRoomCoord();
            if (coord == _lastKnownRoomCoord) return;

            _lastKnownRoomCoord = coord;
            minimap?.RefreshVisibility(_rooms, coord);
        }

        // ---------------- Generación ----------------

        /// <summary>
        /// Genera un piso completo a partir de una semilla. Todos los peers que
        /// llamen con la misma semilla obtienen el mismo mapa.
        /// </summary>
        public void GenerateFloor(int seed)
        {
            if (builder == null)
            {
                Debug.LogError("[DungeonManager] Falta la referencia a DungeonBuilder.");
                return;
            }

            LastSeed = seed;

            // Un único rng recorre las tres fases: grid, construcción y adorno.
            var rng = new System.Random(seed);

            _grid.Generate(roomCount, specialRoomCount, rng);
            _rooms = _grid.Rooms;

            // Se construye recorriendo la lista en orden de creación, no el
            // diccionario, para que el consumo del rng sea idéntico en todos los
            // peers y los adornos aleatorios coincidan.
            builder.Build(_grid.RoomList, rng);

            if (cameraFollow != null) cameraFollow.SetRoomSize(builder.RoomSize);

            minimap?.CreateMap(_rooms);

            _lastKnownRoomCoord = Vector2Int.zero;
            if (_rooms.TryGetValue(Vector2Int.zero, out RoomNode start)) start.visited = true;

            minimap?.RefreshVisibility(_rooms, _lastKnownRoomCoord);

            FloorBuilt?.Invoke(_rooms);
        }

        /// <summary>
        /// Sección "Sala de boss": borra la mazmorra actual y genera una nueva
        /// desde cero reutilizando el mismo sistema aleatorio.
        /// </summary>
        public void GenerateNextFloor(int seed)
        {
            ClearFloor();
            GenerateFloor(seed);
        }

        public void ClearFloor()
        {
            builder?.Clear();

            // Los RoomController se dan de baja en su OnDestroy, pero Clear()
            // destruye de forma diferida en play mode, así que se limpia el
            // registro explícitamente para que no queden salas del piso anterior.
            RoomController.Registry.Clear();

            _rooms.Clear();
        }

        // ---------------- Consultas ----------------

        public bool TryGetRoom(Vector2Int coord, out RoomNode node) => _rooms.TryGetValue(coord, out node);

        public RoomNode FindByType(RoomType type)
        {
            if (_grid == null) return null;
            return _grid.FindByType(type);
        }

        public RoomNode GetRoomAt(Vector3 worldPosition)
        {
            Vector2Int coord = WorldToGrid(worldPosition);
            return _rooms.TryGetValue(coord, out RoomNode node) ? node : null;
        }

        public Vector2Int WorldToGrid(Vector3 worldPosition)
        {
            float size = RoomSize;

            return new Vector2Int(
                Mathf.RoundToInt(worldPosition.x / size),
                Mathf.RoundToInt(worldPosition.z / size));
        }

        /// <summary>Centro de la celda en el mundo. El suelo está a esta misma altura.</summary>
        public Vector3 GridToWorld(Vector2Int coord)
        {
            float size = RoomSize;
            return new Vector3(coord.x * size, FloorSurfaceY, coord.y * size);
        }
    }
}
