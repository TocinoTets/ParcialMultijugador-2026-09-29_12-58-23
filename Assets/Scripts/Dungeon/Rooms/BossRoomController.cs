using System;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Sala de jefe ("Sala de boss" del devlog).
    ///
    ///  - Es una sala especial: tiene UNA sola conexión.
    ///  - El viaje al siguiente piso es direccional: hay que entrar por un lado y
    ///    salir por el lado CONTRARIO para activar el teletransporte.
    ///  - Al activarse, borra la mazmorra actual y genera una nueva desde cero.
    ///
    /// La run del vertical slice termina al derrotar al jefe, así que
    /// RunBootstrapper desactiva este componente durante las partidas. Queda aquí
    /// como la pieza que implementa el paso de piso cuando haya más de un piso.
    /// </summary>
    [DisallowMultipleComponent]
    public class BossRoomController : MonoBehaviour
    {
        [Tooltip("Distancia al centro de la sala a la que se considera que el jugador llegó a un lado.")]
        [SerializeField] private float sideThreshold = 5f;

        [Tooltip("Si está activo, al cruzar al lado opuesto pide generar el siguiente piso.")]
        [SerializeField] private bool enableNextFloor = true;

        /// <summary>Lo escucha DungeonManager, que es quien sabe generar el piso.</summary>
        public event Action NextFloorRequested;

        private Vector3 _entryDirection = Vector3.back;
        private bool _entered;
        private bool _requested;

        /// <param name="gridDirection">Dirección (en coordenadas de grid) de la única conexión de la sala.</param>
        public void SetEntryDirection(Vector2Int gridDirection)
        {
            // La coordenada Y del grid se proyecta sobre el eje Z del mundo.
            _entryDirection = new Vector3(gridDirection.x, 0f, gridDirection.y);
            if (_entryDirection.sqrMagnitude < 0.01f) _entryDirection = Vector3.back;
            _entryDirection.Normalize();
        }

        private void Update()
        {
            if (!enableNextFloor || _requested) return;

            Transform player = FindLocalPlayer();
            if (player == null) return;

            Vector3 offset = player.position - transform.position;
            offset.y = 0f;

            if (offset.magnitude < 0.01f) return;

            Vector3 flat = offset.normalized;

            // 1. Se arma al entrar por el lado de la conexión.
            if (!_entered)
            {
                if (offset.magnitude >= sideThreshold && Vector3.Dot(flat, _entryDirection) > 0.6f)
                    _entered = true;

                return;
            }

            // 2. Se dispara al alcanzar el lado opuesto.
            if (offset.magnitude >= sideThreshold && Vector3.Dot(flat, -_entryDirection) > 0.6f)
            {
                _requested = true;
                NextFloorRequested?.Invoke();
            }
        }

        private static Transform FindLocalPlayer()
        {
            var players = ProtocolExecution.NetPlayer.All;

            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] != null && players[i].IsOwner) return players[i].transform;
            }

            return null;
        }
    }
}
