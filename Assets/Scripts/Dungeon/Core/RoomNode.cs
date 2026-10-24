using UnityEngine;

namespace DungeonSystem
{
    /// <summary>Tipo de sala dentro del piso.</summary>
    public enum RoomType
    {
        Start,
        Normal,
        Shop,
        Special,
        Boss
    }

    /// <summary>Las 4 direcciones del grid. La coordenada Y del grid se proyecta sobre el eje Z del mundo.</summary>
    public enum RoomDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    /// <summary>
    /// Una celda del grid abstracto: coordenada, tipo y sus 4 conexiones.
    /// Es data pura (sin MonoBehaviour) para poder generar el piso entero antes
    /// de tocar la escena, que es lo que permite generar con semilla.
    /// </summary>
    [System.Serializable]
    public class RoomNode
    {
        // Arriba = 1, Abajo = 2, Izquierda = 4, Derecha = 8.
        public const int MaskUp = 1;
        public const int MaskDown = 2;
        public const int MaskLeft = 4;
        public const int MaskRight = 8;

        public readonly Vector2Int gridPosition;

        public RoomType type = RoomType.Normal;

        public bool connUp;
        public bool connDown;
        public bool connLeft;
        public bool connRight;

        /// <summary>Instancia real en la escena. La rellena el builder.</summary>
        [System.NonSerialized] public GameObject sceneInstance;

        // Estado del minimapa.
        public bool visited;
        public bool discovered;

        public RoomNode(Vector2Int position)
        {
            gridPosition = position;
        }

        public int ConnectionCount
        {
            get
            {
                int count = 0;
                if (connUp) count++;
                if (connDown) count++;
                if (connLeft) count++;
                if (connRight) count++;
                return count;
            }
        }

        /// <summary>Mascara 0-15 de las 4 conexiones: es la clave de la base visual y del minimapa.</summary>
        public int ConnectionMask
        {
            get
            {
                int mask = 0;
                if (connUp) mask |= MaskUp;
                if (connDown) mask |= MaskDown;
                if (connLeft) mask |= MaskLeft;
                if (connRight) mask |= MaskRight;
                return mask;
            }
        }

        public bool IsConnected(RoomDirection direction)
        {
            switch (direction)
            {
                case RoomDirection.Up: return connUp;
                case RoomDirection.Down: return connDown;
                case RoomDirection.Left: return connLeft;
                default: return connRight;
            }
        }

        /// <summary>Direccion de la unica conexion. Solo tiene sentido en salas de 1 conexion (especial/boss).</summary>
        public Vector2Int SingleConnectionDirection
        {
            get
            {
                if (connUp) return Vector2Int.up;
                if (connDown) return Vector2Int.down;
                if (connLeft) return Vector2Int.left;
                if (connRight) return Vector2Int.right;
                return Vector2Int.zero;
            }
        }

        public bool HasEncounter => type == RoomType.Normal || type == RoomType.Boss;

        public static Vector2Int DirectionToOffset(RoomDirection direction)
        {
            switch (direction)
            {
                case RoomDirection.Up: return Vector2Int.up;
                case RoomDirection.Down: return Vector2Int.down;
                case RoomDirection.Left: return Vector2Int.left;
                default: return Vector2Int.right;
            }
        }

        public override string ToString()
        {
            return $"{type}@{gridPosition} mask={ConnectionMask}";
        }
    }
}
