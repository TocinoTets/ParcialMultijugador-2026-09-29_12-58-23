using UnityEngine;
using UnityEngine.Serialization;

namespace DungeonSystem
{
    /// <summary>
    /// Cámara del jugador local: lo sigue desde arriba y atrás con una
    /// transición suave, de modo que el personaje siempre queda encuadrado y el
    /// movimiento nunca se ve a saltos. El jugador lo enlaza el RunBootstrapper
    /// en cuanto aparece su NetPlayer.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomCameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform player;
        [SerializeField] private float roomSize = 20f;
        [SerializeField] private float smoothTime = 0.25f;

        [Tooltip("Altura (Y) y retranqueo de la cámara respecto al jugador.")]
        [SerializeField] private Vector3 roomOffset = new Vector3(0f, 14f, -8f);

        [Tooltip("Si está activo, además de seguirlo la cámara se orienta hacia el jugador.")]
        [FormerlySerializedAs("lookAtRoom")]
        [SerializeField] private bool lookAtPlayer = true;

        [Tooltip("Altura del jugador a la que apunta la cámara.")]
        [SerializeField] private float lookHeight = 1f;

        private Vector3 _velocity;

        public bool HasPlayer => player != null;

        public void SetPlayer(Transform playerTransform) => player = playerTransform;
        public void SetRoomSize(float size) => roomSize = Mathf.Max(1f, size);

        /// <summary>Celda del grid en la que está el jugador ahora mismo.</summary>
        public Vector2Int GetCurrentRoomCoord()
        {
            if (player == null) return Vector2Int.zero;

            int x = Mathf.RoundToInt(player.position.x / roomSize);
            int z = Mathf.RoundToInt(player.position.z / roomSize);
            return new Vector2Int(x, z);
        }

        private void LateUpdate()
        {
            if (player == null) return;

            Vector3 anchor = player.position;
            transform.position = Vector3.SmoothDamp(transform.position, anchor + roomOffset, ref _velocity, smoothTime);

            if (!lookAtPlayer) return;

            // La dirección conserva su componente vertical: si se anula, desde
            // los 14 m de altura la cámara mira al horizonte y el jugador queda
            // fuera del encuadre.
            Vector3 aim = anchor + Vector3.up * lookHeight - transform.position;

            if (aim.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(aim.normalized, Vector3.up);
        }
    }
}
