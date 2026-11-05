using UnityEngine;

namespace DungeonSystem
{
    /// <summary>
    /// Panel que tapa el hueco de la pared cuando hay una sala conectada.
    ///
    /// Estados:
    ///  - Abierta (destrabada): el collider queda DESHABILITADO, se puede pasar.
    ///    El material "abierto" lo distingue de una pared fija.
    ///  - Bloqueada: el collider se habilita y el jugador queda encerrado.
    ///
    /// Cada sala es dueña de sus 4 paneles, así que una frontera queda tapada
    /// mientras CUALQUIERA de las dos salas que la comparten la tenga cerrada:
    /// no hay estado compartido entre salas y no puede haber interbloqueos.
    /// </summary>
    [DisallowMultipleComponent]
    public class DoorController : MonoBehaviour
    {
        [SerializeField] private Renderer visual;
        [SerializeField] private Collider blockingCollider;
        [SerializeField] private Material openDoorMaterial;
        [SerializeField] private Material closedDoorMaterial;

        public bool IsLocked { get; private set; }

        private void Awake()
        {
            CacheReferences();
        }

        private void CacheReferences()
        {
            if (blockingCollider == null) blockingCollider = GetComponent<Collider>();
            if (visual == null) visual = GetComponentInChildren<Renderer>();
        }

        public void SetLocked(bool locked)
        {
            IsLocked = locked;

            if (blockingCollider == null) CacheReferences();

            // Las paredes fijas siempre bloquean; la puerta solo mientras está bloqueada.
            if (blockingCollider != null) blockingCollider.enabled = locked;

            if (visual != null)
            {
                Material material = locked ? closedDoorMaterial : openDoorMaterial;
                if (material != null) visual.sharedMaterial = material;
            }
        }
    }
}
