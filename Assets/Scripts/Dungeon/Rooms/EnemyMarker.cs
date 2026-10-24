using System;
using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Marca a un enemigo como participante del encuentro de una sala.
    ///
    /// Igual que en el resto del sistema de red, la muerte la declara el host:
    /// <see cref="Die"/> solo avisa a quien lo tenga registrado (la sala), y la
    /// sala decide si con esto queda superada.
    /// </summary>
    [DisallowMultipleComponent]
    public class EnemyMarker : MonoBehaviour
    {
        /// <summary>Se dispara una sola vez, cuando el enemigo muere.</summary>
        public event Action<EnemyMarker> OnDeath;

        public bool IsDead { get; private set; }

        public void Die()
        {
            if (IsDead) return;
            IsDead = true;

            OnDeath?.Invoke(this);

            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
        }

        private void OnDestroy()
        {
            // Si la sala se destruye antes que el enemigo, se limpia la suscripción.
            OnDeath = null;
        }
    }
}
