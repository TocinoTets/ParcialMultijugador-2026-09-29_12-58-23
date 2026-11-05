using System.Collections.Generic;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// "De mapa a mazmorra": instancia una escena de sala por cada celda del
    /// grid, multiplicando la coordenada por el tamaño de sala, y le aplica sus
    /// conexiones, el material de suelo y sus obstáculos.
    ///
    /// Se recorre la lista de salas en orden de creación (no el diccionario) para
    /// que el consumo del System.Random sea idéntico en todas las máquinas y el
    /// piso salga igual con la misma semilla.
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonBuilder : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private RoomVisualDatabase database;

        [Tooltip("Tamaño real de una sala. Debe coincidir con el paso de la rejilla: 20 = 20 unidades por celda.")]
        [SerializeField] private float roomSize = 20f;

        [Tooltip("Padre de las salas instanciadas. Si queda vacío se crea uno llamado 'Dungeon'.")]
        [SerializeField] private Transform dungeonRoot;

        public float RoomSize => roomSize;
        public RoomVisualDatabase Database => database;
        public Transform Root => dungeonRoot;

        public void Build(IReadOnlyList<RoomNode> rooms, System.Random rng)
        {
            if (database == null)
            {
                Debug.LogError("[DungeonBuilder] Falta la referencia a RoomVisualDatabase.");
                return;
            }

            EnsureRoot();
            Clear();

            foreach (RoomNode node in rooms)
            {
                // La coordenada Y del grid se proyecta sobre el eje Z del mundo:
                // el grid se apoya en el plano horizontal X-Z.
                Vector3 worldPosition = new Vector3(node.gridPosition.x * roomSize, 0f, node.gridPosition.y * roomSize);

                GameObject prefab = database.GetRoomPrefab(node.type, node.ConnectionMask, rng);
                if (prefab == null)
                {
                    Debug.LogError($"[DungeonBuilder] No hay prefab para {node}; la sala queda vacía.");
                    continue;
                }

                GameObject instance = Instantiate(prefab, worldPosition, Quaternion.identity, dungeonRoot);
                instance.name = $"Room_{node.gridPosition.x}_{node.gridPosition.y}_{node.type}";
                node.sceneInstance = instance;

                var controller = instance.GetComponent<RoomController>();

                if (controller == null)
                {
                    Debug.LogError($"[DungeonBuilder] El prefab '{prefab.name}' necesita un RoomController. Se descarta la sala {node.gridPosition}.");
                    Destroy(instance);
                    node.sceneInstance = null;
                    continue;
                }

                controller.Initialize(node);
                controller.SetFloorMaterial(database.GetFloorMaterial(node.ConnectionMask));

                var boss = instance.GetComponent<BossRoomController>();
                if (boss != null)
                {
                    Vector2Int entry = node.SingleConnectionDirection;
                    if (entry != Vector2Int.zero) boss.SetEntryDirection(entry);
                }

                var obstacles = instance.GetComponent<RoomObstacleSpawner>();
                if (obstacles != null) obstacles.Populate(rng);
            }
        }

        private void EnsureRoot()
        {
            if (dungeonRoot != null) return;

            var existing = GameObject.Find("Dungeon");
            if (existing != null)
            {
                dungeonRoot = existing.transform;
                return;
            }

            dungeonRoot = new GameObject("Dungeon").transform;
        }

        /// <summary>Borra el piso actual. Se usa al generar el siguiente (sala de jefe).</summary>
        public void Clear()
        {
            if (dungeonRoot == null) return;

            for (int i = dungeonRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = dungeonRoot.GetChild(i);

                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }
        }
    }
}
