using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Punto marcado dentro de una sala donde el generador puede colocar un
    /// obstáculo (pinchos, pilares, cobertura...). Se coloca como hijo del
    /// prefab de sala.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomObstacleSpawnPoint : MonoBehaviour
    {
        [Tooltip("Radio de holgura alrededor del punto. Sirve para separar obstáculos entre sí.")]
        public float radius = 2f;
    }
}
