using Unity.Netcode;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Objeto para recoger que sueltan los enemigos (GDD 2: "avance recolectando
    /// recursos, armas...").
    ///
    /// [RED] Lo crea el host y solo el host decide quien lo agarro: compara la
    /// distancia a cada jugador vivo y aplica el efecto de forma autoritativa.
    /// Asi dos jugadores que llegan a la vez no pueden tomarlo los dos. Los
    /// clientes solo ven el objeto girando y su color.
    /// </summary>
    public class NetPickup : NetworkBehaviour
    {
        public enum Kind
        {
            /// <summary>Recupera vida.</summary>
            Heal = 0,

            /// <summary>Arma sobrecargada: mas dano por unos segundos.</summary>
            Overcharge = 1
        }

        [SerializeField] private float pickupRadius = 1.2f;
        [SerializeField] private float lifetime = 20f;
        [SerializeField] private float healAmount = 30f;
        [SerializeField] private float overchargeSeconds = 10f;
        [SerializeField] private Renderer bodyRenderer;

        public readonly NetworkVariable<int> PickupKind = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Kind _pendingKind;
        private float _age;
        private Vector3 _basePosition;

        private void Awake()
        {
            if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<Renderer>();
        }

        /// <summary>Elige el tipo antes de Spawn(). Solo en el host.</summary>
        public void ServerSetKind(Kind kind)
        {
            _pendingKind = kind;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) PickupKind.Value = (int)_pendingKind;

            _basePosition = transform.position;
            ApplyColor();
        }

        private void Update()
        {
            // Efecto visual local, no viaja por la red.
            transform.position = _basePosition + Vector3.up * (Mathf.Sin(Time.time * 3f) * 0.15f);
            transform.Rotate(0f, 90f * Time.deltaTime, 0f);

            if (IsServer) UpdateServer();
        }

        private void UpdateServer()
        {
            _age += Time.deltaTime;

            RunController run = RunController.Instance;
            if (run == null || run.CurrentPhase != RunPhase.Playing || _age >= lifetime)
            {
                Despawn();
                return;
            }

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null || !player.IsAlive) continue;

                Vector3 offset = player.transform.position - _basePosition;
                offset.y = 0f;
                if (offset.magnitude > pickupRadius) continue;

                if ((Kind)PickupKind.Value == Kind.Heal) player.ServerHeal(healAmount);
                else player.ServerGrantOvercharge(overchargeSeconds);

                Despawn();
                return;
            }
        }

        private void Despawn()
        {
            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned) networkObject.Despawn(true);
        }

        private void ApplyColor()
        {
            if (bodyRenderer == null) return;

            bodyRenderer.material.color = (Kind)PickupKind.Value == Kind.Heal
                ? new Color(0.25f, 1f, 0.5f)
                : new Color(1f, 0.6f, 0.15f);
        }
    }
}
