using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DungeonSystem
{
    /// <summary>Icono asociado a una máscara de conexión (0-15). Opcional.</summary>
    [System.Serializable]
    public class MinimapIconEntry
    {
        [Tooltip("Máscara 0-15: Arriba=1, Abajo=2, Izquierda=4, Derecha=8.")]
        public int connectionMask;

        public Sprite icon;
    }

    /// <summary>
    /// Minimapa del devlog:
    ///  - Un icono por sala, colocado en su coordenada del grid.
    ///  - La sala actual se resalta y las visitadas se ven a opacidad completa.
    ///  - Las adyacentes a una visitada se ven a opacidad reducida.
    ///  - El resto queda invisible.
    ///
    /// Es puramente visual: nunca lanza si falta un asset. Sin prefab de icono
    /// construye cuadros de color, y sin sprites por máscara usa el color del
    /// tipo de sala.
    /// </summary>
    [DisallowMultipleComponent]
    public class MinimapController : MonoBehaviour
    {
        [Header("Referencias")]
        [Tooltip("Padre donde se colocan los iconos. Si queda vacío se crea uno hijo.")]
        [SerializeField] private RectTransform mapRoot;

        [Tooltip("Prefab del icono. Opcional: sin él se construyen imágenes simples.")]
        [SerializeField] private GameObject iconPrefab;

        [Header("Iconos por máscara (opcional)")]
        [SerializeField] private List<MinimapIconEntry> connectionIcons = new List<MinimapIconEntry>();

        [Header("Disposición")]
        [SerializeField] private float iconSpacing = 24f;
        [SerializeField] private Vector2 iconSize = new Vector2(18f, 18f);

        [Header("Colores por tipo de sala")]
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color startColor = Color.cyan;
        [SerializeField] private Color specialColor = new Color(0.6f, 0.4f, 1f);
        [SerializeField] private Color shopColor = Color.yellow;
        [SerializeField] private Color bossColor = Color.red;

        [Header("Opacidades")]
        [Range(0f, 1f)][SerializeField] private float visitedAlpha = 1f;
        [Range(0f, 1f)][SerializeField] private float adjacentAlpha = 0.35f;
        [SerializeField] private float currentRoomBrightnessBoost = 0.5f;

        private readonly Dictionary<Vector2Int, MinimapRoomIcon> _icons = new Dictionary<Vector2Int, MinimapRoomIcon>();
        private Dictionary<int, Sprite> _spriteLookup;

        public void CreateMap(IReadOnlyDictionary<Vector2Int, RoomNode> rooms)
        {
            BuildSpriteLookup();
            ClearMap();

            if (rooms == null || rooms.Count == 0) return;

            RectTransform root = EnsureRoot();

            foreach (var pair in rooms)
            {
                RoomNode node = pair.Value;

                MinimapRoomIcon icon = CreateIcon(root, node.gridPosition);
                if (icon == null) continue;

                var rect = icon.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchoredPosition = new Vector2(node.gridPosition.x * iconSpacing, node.gridPosition.y * iconSpacing);
                }

                Sprite sprite = null;
                _spriteLookup?.TryGetValue(node.ConnectionMask, out sprite);

                icon.Setup(sprite, ColorForType(node.type));

                _icons[node.gridPosition] = icon;
            }
        }

        private RectTransform EnsureRoot()
        {
            if (mapRoot != null) return mapRoot;

            var go = new GameObject("MapIcons", typeof(RectTransform));
            go.transform.SetParent(transform, false);

            mapRoot = go.GetComponent<RectTransform>();
            mapRoot.anchorMin = new Vector2(0.5f, 0.5f);
            mapRoot.anchorMax = new Vector2(0.5f, 0.5f);
            mapRoot.pivot = new Vector2(0.5f, 0.5f);
            mapRoot.anchoredPosition = Vector2.zero;

            return mapRoot;
        }

        private MinimapRoomIcon CreateIcon(RectTransform root, Vector2Int coord)
        {
            GameObject go;

            if (iconPrefab != null)
            {
                go = Instantiate(iconPrefab, root);
            }
            else
            {
                go = new GameObject($"Icon_{coord.x}_{coord.y}", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(root, false);

                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = iconSize;
            }

            go.name = $"Icon_{coord.x}_{coord.y}";

            var icon = go.GetComponent<MinimapRoomIcon>();
            if (icon == null) icon = go.AddComponent<MinimapRoomIcon>();

            return icon;
        }

        private void BuildSpriteLookup()
        {
            _spriteLookup = new Dictionary<int, Sprite>();

            foreach (MinimapIconEntry entry in connectionIcons)
            {
                if (entry == null) continue;
                if (!_spriteLookup.ContainsKey(entry.connectionMask)) _spriteLookup.Add(entry.connectionMask, entry.icon);
            }
        }

        private Color ColorForType(RoomType type)
        {
            switch (type)
            {
                case RoomType.Start: return startColor;
                case RoomType.Boss: return bossColor;
                case RoomType.Shop: return shopColor;
                case RoomType.Special: return specialColor;
                default: return normalColor;
            }
        }

        /// <summary>
        /// Marca la sala actual del jugador y recalcula qué iconos se ven a
        /// opacidad completa, reducida o no se ven.
        /// </summary>
        public void RefreshVisibility(IReadOnlyDictionary<Vector2Int, RoomNode> rooms, Vector2Int currentRoomCoord)
        {
            if (rooms == null) return;

            if (rooms.TryGetValue(currentRoomCoord, out RoomNode current)) current.visited = true;

            foreach (var pair in rooms)
            {
                if (!_icons.TryGetValue(pair.Key, out MinimapRoomIcon icon) || icon == null) continue;

                RoomNode node = pair.Value;

                if (node.visited)
                {
                    icon.SetVisibility(visitedAlpha);
                }
                else if (IsAdjacentToVisited(rooms, pair.Key))
                {
                    node.discovered = true;
                    icon.SetVisibility(adjacentAlpha);
                }
                else
                {
                    icon.SetVisibility(0f);
                }

                icon.SetHighlighted(pair.Key == currentRoomCoord, currentRoomBrightnessBoost);
            }
        }

        private static bool IsAdjacentToVisited(IReadOnlyDictionary<Vector2Int, RoomNode> rooms, Vector2Int coord)
        {
            foreach (RoomDirection direction in DungeonGrid.AllDirections)
            {
                Vector2Int neighbour = coord + RoomNode.DirectionToOffset(direction);

                if (rooms.TryGetValue(neighbour, out RoomNode node) && node.visited) return true;
            }

            return false;
        }

        public void ClearMap()
        {
            foreach (var pair in _icons)
            {
                if (pair.Value == null) continue;

                if (Application.isPlaying) Destroy(pair.Value.gameObject);
                else DestroyImmediate(pair.Value.gameObject);
            }

            _icons.Clear();

            if (mapRoot == null) return;

            // Iconos que no se registraron (por ejemplo, de un piso anterior).
            for (int i = mapRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = mapRoot.GetChild(i);

                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }
        }
    }
}
