using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Base visual de las salas: qué prefab y qué material de suelo corresponden
    /// a cada máscara de conexión (0-15).
    ///
    /// Regla dura: si una máscara no tiene entrada, se usa
    /// <see cref="fallbackRoomPrefab"/>. El sistema anterior devolvía null y la
    /// sala desaparecía del piso; eso no puede volver a pasar.
    /// </summary>
    [CreateAssetMenu(menuName = "DungeonSystem/Room Visual Database", fileName = "RoomVisualDatabase")]
    public class RoomVisualDatabase : ScriptableObject
    {
        [System.Serializable]
        public class ConnectionRoomSet
        {
            [Tooltip("Máscara 0-15: Arriba=1, Abajo=2, Izquierda=4, Derecha=8.")]
            public int connectionMask;

            [Tooltip("Variantes válidas para esta máscara. Se elige una al azar por semilla.")]
            public List<GameObject> roomPrefabVariants = new List<GameObject>();

            [Tooltip("Material del suelo para esta máscara. Si es null se usa el de reserva.")]
            public Material floorMaterial;
        }

        [Header("Reserva (garantiza que nunca falta una sala)")]
        public GameObject fallbackRoomPrefab;
        public Material fallbackFloorMaterial;

        [Header("Salas con reglas propias")]
        public GameObject startRoomPrefab;
        public GameObject shopRoomPrefab;
        public GameObject bossRoomPrefab;

        [Header("Variantes por máscara de conexión")]
        [SerializeField] private List<ConnectionRoomSet> roomSets = new List<ConnectionRoomSet>();

        [NonSerialized] private Dictionary<int, ConnectionRoomSet> _lookup;

        public IReadOnlyList<ConnectionRoomSet> RoomSets => roomSets;

        public void RebuildLookup()
        {
            _lookup = new Dictionary<int, ConnectionRoomSet>();

            foreach (ConnectionRoomSet set in roomSets)
            {
                if (set == null) continue;

                // Primera entrada por máscara: evita el problema del sistema
                // anterior, donde las duplicadas quedaban muertas.
                if (!_lookup.ContainsKey(set.connectionMask)) _lookup.Add(set.connectionMask, set);
            }
        }

        private void EnsureLookup()
        {
            if (_lookup == null) RebuildLookup();
        }

        public GameObject GetRoomPrefab(RoomType type, int connectionMask, System.Random rng)
        {
            switch (type)
            {
                case RoomType.Start when startRoomPrefab != null:
                    return startRoomPrefab;
                case RoomType.Boss when bossRoomPrefab != null:
                    return bossRoomPrefab;
                case RoomType.Shop when shopRoomPrefab != null:
                    return shopRoomPrefab;
                case RoomType.Special when shopRoomPrefab != null:
                    return shopRoomPrefab;
            }

            EnsureLookup();

            if (_lookup.TryGetValue(connectionMask, out ConnectionRoomSet set) && set.roomPrefabVariants.Count > 0)
            {
                return PickVariant(set.roomPrefabVariants, rng);
            }

            if (fallbackRoomPrefab != null) return fallbackRoomPrefab;

            Debug.LogError($"[RoomVisualDatabase] Sin prefab para la máscara {connectionMask} y sin 'fallbackRoomPrefab'.");
            return null;
        }

        private static GameObject PickVariant(List<GameObject> variants, System.Random rng)
        {
            // Se filtran los huecos vacíos para que una lista a medio rellenar no
            // devuelva null y haga desaparecer la sala.
            var valid = new List<GameObject>();
            foreach (GameObject prefab in variants)
            {
                if (prefab != null) valid.Add(prefab);
            }

            if (valid.Count == 0) return null;
            if (rng == null) return valid[0];

            return valid[rng.Next(valid.Count)];
        }

        public Material GetFloorMaterial(int connectionMask)
        {
            EnsureLookup();

            if (_lookup.TryGetValue(connectionMask, out ConnectionRoomSet set) && set.floorMaterial != null)
                return set.floorMaterial;

            return fallbackFloorMaterial;
        }

        /// <summary>Rellena la base con todas las máscaras apuntando a un mismo prefab. Usado por el editor.</summary>
        public void ConfigureFromPrefab(GameObject roomPrefab, Material floorMaterial)
        {
            fallbackRoomPrefab = roomPrefab;
            fallbackFloorMaterial = floorMaterial;
            startRoomPrefab = roomPrefab;
            shopRoomPrefab = roomPrefab;
            bossRoomPrefab = roomPrefab;

            roomSets = new List<ConnectionRoomSet>();

            for (int mask = 0; mask < 16; mask++)
            {
                var set = new ConnectionRoomSet { connectionMask = mask, floorMaterial = floorMaterial };
                if (roomPrefab != null) set.roomPrefabVariants.Add(roomPrefab);
                roomSets.Add(set);
            }

            RebuildLookup();
        }
    }
}
