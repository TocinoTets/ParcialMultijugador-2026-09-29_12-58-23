using System.Collections.Generic;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Genera la matriz/grid de salas.
    ///
    /// Pasos (devlog "Cómo funciona la generación procedural"):
    ///  1. Inicio en el centro: la sala inicial se coloca en (0,0).
    ///  2. Selección de vecino: se elige al azar una casilla vecina.
    ///  3. Verificación de espacio: si está vacía, se crea una sala ahí.
    ///  4. Expansión recursiva: el origen de cada intento es CUALQUIER sala ya
    ///     existente, no solo la última, para que el mapa crezca como un racimo
    ///     y no como un pasillo.
    ///  5. Bucle hasta alcanzar el número máximo de salas.
    ///
    /// Después: salas especiales (las de UNA sola conexión) y asignación de
    /// jefe/tienda. Todo consume el mismo System.Random, así que la misma
    /// semilla produce exactamente el mismo piso en todas las máquinas.
    /// </summary>
    public class DungeonGrid
    {
        public static readonly RoomDirection[] AllDirections =
        {
            RoomDirection.Up,
            RoomDirection.Down,
            RoomDirection.Left,
            RoomDirection.Right
        };

        public Dictionary<Vector2Int, RoomNode> Rooms { get; private set; }
        public List<RoomNode> RoomList { get; private set; }

        /// <summary>Resultado de la fase de salas especiales, para diagnóstico.</summary>
        public bool ForcedRelaxedSpecial { get; private set; }

        public void Generate(int roomCount, int specialRoomCount, System.Random rng)
        {
            Rooms = new Dictionary<Vector2Int, RoomNode>();
            RoomList = new List<RoomNode>();
            ForcedRelaxedSpecial = false;

            roomCount = Mathf.Max(2, roomCount);

            // 1. Inicio en el centro.
            var start = new RoomNode(Vector2Int.zero) { type = RoomType.Start };
            Rooms.Add(start.gridPosition, start);
            RoomList.Add(start);

            // 5. Bucle hasta llegar al número de salas pedido. El contador de
            //    seguridad evita un bucle infinito si el racimo se satura de
            //    intentos contra casillas ya ocupadas.
            int safety = roomCount * 500;
            while (RoomList.Count < roomCount && safety-- > 0)
            {
                // 4. Cualquier sala existente puede ser el origen.
                RoomNode origin = RoomList[rng.Next(RoomList.Count)];

                // 2. Vecino aleatorio.
                RoomDirection direction = AllDirections[rng.Next(AllDirections.Length)];
                Vector2Int candidate = origin.gridPosition + RoomNode.DirectionToOffset(direction);

                // 3. Solo se crea si la casilla está vacía.
                if (Rooms.ContainsKey(candidate)) continue;

                var room = new RoomNode(candidate);
                Rooms.Add(candidate, room);
                RoomList.Add(room);
            }

            ComputeConnections();

            AssignSpecialRooms(Mathf.Max(1, specialRoomCount), rng);
            AssignBossAndShops(rng);

            // Recalcular tras las salas que haya forzado el asignador.
            ComputeConnections();
        }

        /// <summary>
        /// Deriva las 4 booleanas de cada sala comprobando qué celdas vecinas
        /// están ocupadas. Hay que volver a llamarlo tras cualquier cambio en el
        /// diccionario (por ejemplo, tras forzar una sala especial).
        /// </summary>
        public void ComputeConnections()
        {
            foreach (RoomNode room in Rooms.Values)
            {
                Vector2Int pos = room.gridPosition;
                room.connUp = Rooms.ContainsKey(pos + Vector2Int.up);
                room.connDown = Rooms.ContainsKey(pos + Vector2Int.down);
                room.connLeft = Rooms.ContainsKey(pos + Vector2Int.left);
                room.connRight = Rooms.ContainsKey(pos + Vector2Int.right);
            }
        }

        // ---------------- Salas especiales ----------------

        /// <summary>
        /// "Para crear salas especiales": las salas con UNA sola conexión son
        /// candidatas. Si no hay suficientes, se fuerza la creación de una sala
        /// nueva junto a la sala con menos conexiones, en un hueco donde la nueva
        /// quede con una única entrada.
        /// </summary>
        private void AssignSpecialRooms(int desiredCount, System.Random rng)
        {
            var specials = new List<RoomNode>();

            for (int i = 0; i < desiredCount; i++)
            {
                var candidates = new List<RoomNode>();
                foreach (RoomNode room in RoomList)
                {
                    if (room.type != RoomType.Normal) continue;
                    if (room.ConnectionCount == 1) candidates.Add(room);
                }

                if (candidates.Count > 0)
                {
                    RoomNode chosen = candidates[rng.Next(candidates.Count)];
                    chosen.type = RoomType.Special;
                    specials.Add(chosen);
                    continue;
                }

                // Generación forzada.
                RoomNode forced = ForceSpecialRoom(rng, strict: true);
                if (forced == null) forced = ForceSpecialRoom(rng, strict: false);

                if (forced == null) break;

                forced.type = RoomType.Special;
                specials.Add(forced);
            }

            // Garantía: la run necesita SIEMPRE una sala de jefe. Si el
            // asignador no consiguió ninguna especial (mapa demasiado denso),
            // se degrada la sala normal más alejada del centro.
            if (specials.Count == 0)
            {
                RoomNode fallback = FindFarthestRoom(RoomType.Normal);
                if (fallback != null)
                {
                    fallback.type = RoomType.Special;
                    specials.Add(fallback);
                    Debug.LogWarning("[DungeonGrid] No se pudo forzar una sala especial; se reutilizo la sala normal mas lejana.");
                }
            }
        }

        private RoomNode ForceSpecialRoom(System.Random rng, bool strict)
        {
            RoomNode reference = FindLeastConnectedRoom();
            if (reference == null) return null;

            // Orden aleatorio de direcciones para no sesgar siempre el mismo lado.
            var directions = new List<RoomDirection>(AllDirections);
            for (int i = directions.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (directions[i], directions[j]) = (directions[j], directions[i]);
            }

            foreach (RoomDirection direction in directions)
            {
                Vector2Int position = reference.gridPosition + RoomNode.DirectionToOffset(direction);
                if (Rooms.ContainsKey(position)) continue;

                int neighbours = CountOccupiedNeighbours(position);

                if (strict)
                {
                    // La nueva sala debe tener exactamente una entrada.
                    if (neighbours != 1) continue;
                }
                else
                {
                    // Relajado: basta con que tenga al menos una entrada, para
                    // que el mapa no quede sin sala de jefe.
                    if (neighbours < 1) continue;
                    ForcedRelaxedSpecial = true;
                }

                var room = new RoomNode(position);
                Rooms.Add(position, room);
                RoomList.Add(room);

                // Conexiones locales provisionales; Generate() recalcula todo al final.
                room.connUp = Rooms.ContainsKey(position + Vector2Int.up);
                room.connDown = Rooms.ContainsKey(position + Vector2Int.down);
                room.connLeft = Rooms.ContainsKey(position + Vector2Int.left);
                room.connRight = Rooms.ContainsKey(position + Vector2Int.right);

                return room;
            }

            return null;
        }

        private RoomNode FindLeastConnectedRoom()
        {
            RoomNode best = null;
            int bestCount = int.MaxValue;

            foreach (RoomNode room in RoomList)
            {
                if (room.type != RoomType.Normal) continue;

                int count = room.ConnectionCount;
                if (count < bestCount)
                {
                    bestCount = count;
                    best = room;
                }
            }

            return best;
        }

        private int CountOccupiedNeighbours(Vector2Int position)
        {
            int count = 0;
            if (Rooms.ContainsKey(position + Vector2Int.up)) count++;
            if (Rooms.ContainsKey(position + Vector2Int.down)) count++;
            if (Rooms.ContainsKey(position + Vector2Int.left)) count++;
            if (Rooms.ContainsKey(position + Vector2Int.right)) count++;
            return count;
        }

        // ---------------- Jefe y tiendas ----------------

        /// <summary>
        /// Entre las salas especiales, la más alejada del centro pasa a ser la
        /// sala de jefe y el resto tiendas. Las dos son salas de una sola
        /// conexión, tal y como describe el devlog de la sala de boss.
        /// </summary>
        private void AssignBossAndShops(System.Random rng)
        {
            var specials = new List<RoomNode>();
            foreach (RoomNode room in RoomList)
            {
                if (room.type == RoomType.Special) specials.Add(room);
            }

            if (specials.Count == 0) return;

            RoomNode boss = null;
            int bestDistance = -1;

            foreach (RoomNode room in specials)
            {
                int distance = Mathf.Abs(room.gridPosition.x) + Mathf.Abs(room.gridPosition.y);

                // El desempate usa el rng para que no dependa del orden del diccionario.
                if (distance > bestDistance || (distance == bestDistance && boss != null && rng.Next(2) == 0))
                {
                    bestDistance = distance;
                    boss = room;
                }
            }

            if (boss == null) boss = specials[rng.Next(specials.Count)];
            boss.type = RoomType.Boss;

            foreach (RoomNode room in specials)
            {
                if (room != boss) room.type = RoomType.Shop;
            }
        }

        public RoomNode FindFarthestRoom(RoomType type)
        {
            RoomNode best = null;
            int bestDistance = -1;

            foreach (RoomNode room in RoomList)
            {
                if (room.type != type) continue;

                int distance = Mathf.Abs(room.gridPosition.x) + Mathf.Abs(room.gridPosition.y);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = room;
                }
            }

            return best;
        }

        public RoomNode FindByType(RoomType type)
        {
            foreach (RoomNode room in RoomList)
            {
                if (room.type == type) return room;
            }
            return null;
        }
    }
}
