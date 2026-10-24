using System.Collections.Generic;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// "Variedad de salas": coloca obstáculos en los puntos marcados del prefab.
    /// Consume el mismo System.Random del piso, así que la misma semilla produce
    /// la misma sala en host y clientes.
    ///
    /// Si no hay prefabs de obstáculo configurados no hace nada: la variedad es
    /// contenido opcional, nunca un requisito para que el piso exista.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomObstacleSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject[] obstaclePrefabs;
        [SerializeField, Range(0f, 1f)] private float spawnChance = 0.5f;

        [Tooltip("Variación aleatoria de la orientación de cada obstáculo.")]
        [SerializeField] private bool randomizeRotation = true;

        private readonly List<GameObject> _spawned = new List<GameObject>();

        public void Populate(System.Random rng)
        {
            ClearSpawned();

            if (obstaclePrefabs == null || obstaclePrefabs.Length == 0 || rng == null) return;

            var points = GetComponentsInChildren<RoomObstacleSpawnPoint>(includeInactive: true);
            if (points.Length == 0) return;

            float chance = Mathf.Clamp01(spawnChance);

            foreach (RoomObstacleSpawnPoint point in points)
            {
                if (rng.NextDouble() > chance) continue;

                GameObject prefab = PickPrefab(rng);
                if (prefab == null) continue;

                Quaternion rotation = randomizeRotation
                    ? Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f)
                    : point.transform.rotation;

                GameObject instance = Instantiate(prefab, point.transform.position, rotation, transform);
                _spawned.Add(instance);
            }
        }

        private GameObject PickPrefab(System.Random rng)
        {
            var valid = new List<GameObject>();

            foreach (GameObject prefab in obstaclePrefabs)
            {
                if (prefab != null) valid.Add(prefab);
            }

            if (valid.Count == 0) return null;

            return valid[rng.Next(valid.Count)];
        }

        public void ClearSpawned()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                if (_spawned[i] == null) continue;

                if (Application.isPlaying) Destroy(_spawned[i]);
                else DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }
    }
}
